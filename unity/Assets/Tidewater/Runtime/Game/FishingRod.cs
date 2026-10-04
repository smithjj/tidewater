using System;
using System.Collections.Generic;
using Tidewater.Engine;
using Tidewater.Player;
using Matrix4 = Tidewater.Engine.Matrix4;
using Quaternion = Tidewater.Engine.Quaternion;
using Vector3 = Tidewater.Engine.Vector3;

// Port of src/game/FishingRod.js (the state and the physics half): the fishing rod in the player's hands, its line and the bobber.
//
// A 7 ft medium saltwater spinning combo at real scale (the geometry is FishingRodView.cs). Rod frame: +Y along the blank (butt at 0, tip at
// ROD_L), the reel hangs toward -Z, +X to the right. Held in first person by the reel seat (SEAT_Y), relative to the camera: a pose (elevation of
// the rod above the view direction, sideways swing, hand position) is eased between idle / wind-up / flick / fighting.
//
// The blank bends toward the line with a fast action: under a light load only the tip section bends, under a heavy one the bend works down into
// the butt. The shader (Tidewater/Boat kind 8) bends the mesh with the values written here (rodBend, rodShape, reelAnim, reelAnim2); the same curve
// is evaluated here for the tip the line leaves from.
//
// States: "stowed" -> "idle" -> ( hold ) "windup" -> ( release ) "flick" -> "flying" (bobber in the air) -> "floating" (on the water; bites come
// here) -> "fighting" (fish on) -> back to "idle"; "retrieving" reels an empty line in.
//
// Sim coordinates (three.js axes), doubles. The camera is the SimCamera of the player; the rod draws relative to it.
namespace Tidewater.Game
{
	// what the rod and the game make noise with (SoundScape.js); the sound is not ported yet, every hook is optional
	public interface IGameAudio
	{
		void rodReady();
		void bail( bool open );
		void rodLoop( double crankRate, double lineOutRate, double tension );
		void whoosh( double power );
		void lineOut( double power );
		void plop( Vector3 at );
		void fishSplash( Vector3 at, double v );
		void fishFlop();
		void lineSnap();
		void splash( double v );
		void coin();
	}

	public struct RodPose
	{
		public double elev, side; public double[] hand;
		public RodPose( double elev, double side, double hx, double hy, double hz ) { this.elev = elev; this.side = side; hand = new[] { hx, hy, hz }; }
	}

	public sealed class FishingRod
	{
		public const double ROD_L = 2.13; // 7 ft
		public const double BLANK_START = 0.535; // front of the fore grip: the blank bends from here
		public const double SEAT_Y = 0.405; // the hand holds the reel seat (reel foot between the fingers)
		public const double REEL_Z = - 0.092; // reel axis below the blank
		public const double BODY_Y = 0.327; // crank axis height along the rod
		public const double PIVOT_Y = 0.403; // bail pivots at the rotor arm ends
		public const double GEAR = 5.2; // rotor turns per crank turn
		public const double LINE_PER_CRANK = 0.8; // m of line per crank turn
		public const int LINE_SEGS = 48;
		const double GRAV = 9.81;

		public static readonly Dictionary<string, RodPose> POSES = new Dictionary<string, RodPose>
		{
			{ "stowed", new RodPose( - 0.9, 0.35, 0.3, - 0.62, - 0.25 ) },
			{ "idle", new RodPose( 0.46, 0.22, 0.15, - 0.05, - 0.45 ) },
			{ "windup", new RodPose( 2.0, 0.12, 0.22, - 0.08, - 0.2 ) },
			{ "flick", new RodPose( 0.16, 0.14, 0.18, - 0.12, - 0.55 ) },
			{ "follow", new RodPose( 0.24, 0.02, 0.17, - 0.1, - 0.56 ) }, // follow-through: the rod points along the cast
			{ "floating", new RodPose( 0.4, 0.18, 0.15, - 0.06, - 0.46 ) },
			{ "fighting", new RodPose( 0.9, 0.12, 0.14, - 0.03, - 0.43 ) },
			{ "landing", new RodPose( 0.85, 0.3, 0.16, - 0.08, - 0.46 ) }, // rod up, the fish swinging in view
		};

		// bend of the blank at rod height y (same curve in the vertex shader): lateral deflection along the bend direction and the drop along the
		// rod that keeps its length about constant
		public static double BEND_P( double load ) => Tidewater.Engine.MathUtils.lerp( 3.4, 1.7, Tidewater.Engine.MathUtils.clamp( load, 0, 1 ) );
		static void bendAt( double y, double bend, double p, out double lat, out double drop )
		{
			double s = Tidewater.Engine.MathUtils.clamp( ( y - BLANK_START ) / ( ROD_L - BLANK_START ), 0, 1 );
			lat = bend * ROD_L * Math.Pow( s, p );
			drop = 0.5 * lat * lat / ( y - BLANK_START + 0.06 );
		}

		static readonly Matrix4 _m = new Matrix4();
		static readonly Vector3 _v = new Vector3(), _w = new Vector3(), _x = new Vector3(), _y = new Vector3(), _z = new Vector3(), _h = new Vector3();
		static readonly Vector3 _tipOld = new Vector3(); // last frame's tip: its own vector (the scratch vectors are reused during update)
		static readonly Vector3 _up = new Vector3( 0, 1, 0 );

		readonly SimCamera camera;
		readonly IWaterQuery query;
		readonly Func<double, double, double> heightAt;
		readonly IGameAudio audio;
		readonly int slot;
		readonly Matrix4 camMatrix = new Matrix4();
		static readonly Vector3 _one = new Vector3( 1, 1, 1 );

		public string state = "stowed";
		public bool equipped;
		public double power; // cast charge 0..1
		public double castM = 22;
		public double reelSpeed = 1.1;
		public double t; // time in state
		public double poseElev, poseSide;
		public readonly Vector3 poseHand;
		public double bend; // 0..~0.3 (tip deflection / length)
		public readonly Vector3 bendDir = new Vector3( 0, 0, - 1 );

		public readonly Vector3 bobber = new Vector3();
		public readonly Vector3 bobberVel = new Vector3();
		public double dip; // bobber pulled under (bite cues / fish on)
		public double waterY;
		public double depth; // water depth under the bobber (from the query's floor)
		public double lineOut; // m of line out
		public double slack = 1;
		public readonly Vector3 fishPos = new Vector3(); // where the hooked fish is (fighting)
		double _wander;
		public Action<string> onLand; // callback( "water" | "ground" ) when the bobber lands
		public readonly Vector3 tip = new Vector3();

		// reel / bend animation state
		double bendVel;
		public double load; // 0..1: how deep the bend works into the blank
		public double rotor, crank, spoolAng, spoolOsc;
		public double bail; // 0 closed .. 1 open
		double bailTarget;
		double crankRate; // crank turns / s (smoothed)
		public double lineFill = 1; // line left on the spool (1 = full)
		double _lastOut;
		double _bailSnd = double.NaN;
		Vector3 _landFrom;

		// what the view reads
		public readonly Matrix4 rodMatrix = new Matrix4(); // rod frame -> world
		public bool rodVisible, lineVisible, bobberVisible;
		public readonly double[] rodBend = new double[ 4 ]; // xyz bend direction (rod space), w bend
		public double rodShapeX = 3; // x: bend exponent (fast action)
		public readonly double[] reelAnim = new double[ 4 ]; // rotor angle, bail open 0..1, crank angle, spool angle
		public readonly double[] reelAnim2 = { 0, 1, 0, 0 }; // spool oscillation (m), line fill
		public readonly Vector3 lineA = new Vector3(), lineB = new Vector3(), lineCtl = new Vector3(); // the line: a quadratic Bezier tip -> sag -> bobber
		public double lineShow;
		public double bobberScale = 1, bobberTilt;

		public FishingRod( SimCamera camera, IWaterQuery query, Func<double, double, double> heightAt, IGameAudio audio = null )
		{
			this.camera = camera;
			this.query = query;
			this.heightAt = heightAt;
			this.audio = audio;
			slot = query.Allocate( "bobber", 1 );
			var p = POSES[ "stowed" ];
			poseElev = p.elev; poseSide = p.side; poseHand = new Vector3( p.hand[ 0 ], p.hand[ 1 ], p.hand[ 2 ] );
		}

		public void setGear( double castM, double reelSpeed )
		{
			this.castM = castM;
			this.reelSpeed = reelSpeed;
		}

		public void equip( bool on )
		{
			equipped = on;
			if ( on && state == "stowed" ) setState( "idle" );
			if ( on && audio != null ) audio.rodReady();
			if ( ! on ) setState( "stowed" );
		}

		public void setState( string s )
		{
			state = s;
			t = 0;
		}

		public bool lineInWater => state == "floating" || state == "fighting" || state == "retrieving" || state == "flying" || state == "landing";

		// ---- actions (from the game)
		public bool startWindup()
		{
			if ( state != "idle" ) return false;
			setState( "windup" );
			power = 0;
			return true;
		}

		public bool release()
		{
			if ( state != "windup" ) return false;
			setState( "flick" );
			return true;
		}

		public void retrieve()
		{
			if ( state == "floating" || state == "flying" ) setState( "retrieving" );
		}

		public bool hook()
		{
			if ( state != "floating" ) return false;
			setState( "fighting" );
			fishPos.copy( bobber );
			return true;
		}

		// fish landed: swing it up out of the water on a short line (the game hangs the fish on the end)
		public void land()
		{
			setState( "landing" );
			_landFrom = bobber.clone();
		}

		// fish lost: line back to the rod
		public void endFight() { setState( lineOut > 3 ? "retrieving" : "idle" ); }

		// Object3D.getWorldDirection: the camera's -z axis from its world matrix
		Vector3 worldDirection( Vector3 target )
		{
			var e = camMatrix.elements;
			return target.set( e[ 8 ], e[ 9 ], e[ 10 ] ).normalize().negate();
		}

		// ---- per frame. fight: the CatchMinigame while fighting
		public void update( double dt, bool visible, CatchMinigame fight = null )
		{
			t += dt;
			var cam = camera;
			var tipOld = _tipOld.copy( tip );
			camMatrix.compose( cam.position, cam.quaternion, _one );

			// charge while winding up
			if ( state == "windup" ) power = Math.Min( 1, power + dt / 1.1 );

			// ---- rod pose
			var target = POSES.TryGetValue( state, out var tp ) ? tp : POSES[ "idle" ];
			if ( state == "flying" ) target = POSES[ "follow" ];
			if ( state == "retrieving" ) target = POSES[ "floating" ];
			if ( state == "flick" && t < 0.06 ) target = POSES[ "windup" ];
			double speed = state == "flick" ? 28 : state == "windup" ? 7 : 5;
			double k = 1 - Math.Exp( - speed * dt );
			poseElev += ( target.elev - poseElev ) * k;
			poseSide += ( target.side - poseSide ) * k;
			poseHand.x += ( target.hand[ 0 ] - poseHand.x ) * k;
			poseHand.y += ( target.hand[ 1 ] - poseHand.y ) * k;
			poseHand.z += ( target.hand[ 2 ] - poseHand.z ) * k;
			// fighting: the rod dips and sways with the fish
			double elev = poseElev, side = poseSide;
			if ( state == "fighting" && fight != null )
			{
				elev += - 0.35 * fight.surge + 0.12 * Math.Sin( t * 2.3 );
				side += 0.12 * Math.Sin( t * 1.1 + fight.surge * 2 );
			}

			// a live hand: breathing sway, and the tip twitching with each crank turn
			elev += Math.Sin( t * 1.3 ) * 0.012 + Math.Sin( crank ) * 0.006 * Math.Min( 1, crankRate );
			side += Math.Sin( t * 0.9 + 1.7 ) * 0.01;

			// rod basis in camera space: +Y along the rod, +Z roughly up (the reel hangs below the rod); the hand holds the reel seat, the butt runs
			// back under the forearm
			_y.set( 0, 0, - 1 ).applyAxisAngle( _x.set( 1, 0, 0 ), elev ).applyAxisAngle( _up, side ).normalize();
			_z.set( 0, 1, 0 ).addScaledVector( _y, - _y.y ).normalize();
			_x.crossVectors( _y, _z ).normalize();
			_m.makeBasis( _x, _y, _z ).setPosition( _h.copy( poseHand ).addScaledVector( _y, - SEAT_Y ) );
			rodMatrix.multiplyMatrices( camMatrix, _m );
			rodVisible = visible && ( equipped || poseElev > POSES[ "stowed" ].elev + 0.1 );

			// ---- bend: a damped spring toward the load (the flick loads the blank back, then it whips through; bites nod the tip), direction toward
			// the line when it is out
			double bendT = 0, loadT = 0.15;
			if ( state == "fighting" && fight != null )
			{
				bendT = 0.06 + 0.3 * Math.Min( fight.tension, 1.1 ) + 0.05 * fight.surge;
				loadT = Math.Min( 1, fight.tension * 1.1 );
			}
			else if ( state == "retrieving" ) bendT = 0.035;
			else if ( state == "windup" ) bendT = 0.02 + 0.03 * power;
			else if ( state == "flick" )
			{
				bendT = t < 0.09 ? - 0.2 * ( 0.4 + power ) : 0; // loaded back, then released
				loadT = 0.55;
			}
			else if ( state == "floating" ) bendT = 0.012 + dip * 0.07;
			else if ( state == "landing" )
			{
				bendT = 0.14;
				loadT = 0.5;
			}

			const double K = 250, C = 8; // ~2.5 Hz, lightly damped
			bendVel += ( ( bendT - bend ) * K - bendVel * C ) * dt;
			bend += bendVel * dt;
			load += ( loadT - load ) * ( 1 - Math.Exp( - dt * 6 ) );
			// bend direction: toward the bobber (rod space, across the blank) when the line is out, otherwise down toward the reel side (negative
			// bend = loaded back up)
			if ( lineInWater && state != "flying" )
			{
				_v.copy( bobber ).applyMatrix4( _m.copy( rodMatrix ).invert() ); _v.y = 0;
				if ( _v.lengthSq() > 1e-6 ) bendDir.lerp( _v.normalize(), 1 - Math.Exp( - dt * 10 ) ).normalize();
			}
			else bendDir.lerp( _w.set( 0, 0, - 1 ), 1 - Math.Exp( - dt * 10 ) ).normalize();
			double P = BEND_P( load );
			rodBend[ 0 ] = bendDir.x; rodBend[ 1 ] = 0; rodBend[ 2 ] = bendDir.z; rodBend[ 3 ] = bend;
			rodShapeX = P;

			// tip in world space (same curve as the shader)
			bendAt( ROD_L, bend, P, out double bLat, out double bDrop );
			tip.set( bendDir.x * bLat, ROD_L - bDrop, bendDir.z * bLat ).applyMatrix4( rodMatrix );

			// ---- reel: the bail opens as you wind up (finger on the line) and snaps shut on the first crank; the handle and rotor turn with the line
			// coming in, the spool slips back when a fish takes line against the drag
			bailTarget = state == "windup" || state == "flick" || state == "flying" ? 1 : 0;
			if ( bailTarget != _bailSnd )
			{
				if ( ! double.IsNaN( _bailSnd ) && audio != null ) audio.bail( bailTarget == 1 );
				_bailSnd = bailTarget;
			}

			double bailRate = bailTarget > bail ? 6 : 16;
			bail += JS.Sign( bailTarget - bail ) * Math.Min( Math.Abs( bailTarget - bail ), bailRate * dt );
			double outNow = state == "fighting" && fight != null ? fight.distance : lineOut;
			double dOut = lineInWater && dt > 0 ? outNow - _lastOut : 0;
			_lastOut = outNow;
			double rateT = 0;
			if ( ( state == "fighting" || state == "retrieving" || state == "landing" ) && dt > 0 )
			{
				if ( dOut < 0 ) rateT = Math.Min( 1.6, - dOut / dt / LINE_PER_CRANK );
				else if ( state == "fighting" ) spoolAng -= Math.Min( dOut, 0.5 ) / 0.023; // drag slipping
				if ( state == "landing" ) rateT = t < 0.5 ? 1.2 : 0;
			}

			crankRate += ( rateT - crankRate ) * ( 1 - Math.Exp( - dt * 10 ) );
			double dCrank = crankRate * Math.PI * 2 * dt;
			crank += dCrank;
			// the rotor turns GEAR times faster; shown at most ~3 turns a second (it would strobe)
			rotor += Math.Min( dCrank * GEAR, 3.1 * Math.PI * 2 * dt );
			spoolOsc = Math.Sin( crank * 0.5 ) * 0.0035;
			lineFill = 1 - Math.Min( 1, lineOut / 220 ) * 0.5;
			reelAnim[ 0 ] = rotor; reelAnim[ 1 ] = bail; reelAnim[ 2 ] = crank; reelAnim[ 3 ] = spoolAng;
			if ( audio != null ) audio.rodLoop( crankRate, state == "fighting" && dOut > 0 && dt > 0 ? dOut / dt : 0, state == "fighting" && fight != null ? fight.tension : 0 );
			reelAnim2[ 0 ] = spoolOsc; reelAnim2[ 1 ] = lineFill; reelAnim2[ 2 ] = 0; reelAnim2[ 3 ] = 0;

			// ---- flick: the bobber leaves the tip half way through
			if ( state == "flick" && t > 0.09 )
			{
				double v0 = 7 + 13 * power * Math.Pow( castM / 22, 0.5 );
				// toward where you aim: the heading runs from the tip (off to the side of the view) to the point on the water straight ahead at the
				// cast's reach, so the line leaves along the rod toward the crosshair instead of parallel to the view from the tip
				var d = worldDirection( _v );
				double hl = Math.Max( JS.Hypot( d.x, d.z ), 1e-3 );
				double reach = castM * ( 0.35 + 0.65 * power );
				double tx = cam.position.x + d.x / hl * reach - tip.x, tz = cam.position.z + d.z / hl * reach - tip.z;
				double tl = Math.Max( JS.Hypot( tx, tz ), 1e-3 );
				d.set( tx / tl * hl, Math.Max( d.y, - 0.2 ) + 0.35, tz / tl * hl ).normalize();
				bobber.copy( tip );
				bobberVel.copy( d ).multiplyScalar( v0 );
				setState( "flying" );
				if ( audio != null ) { audio.whoosh( power ); audio.lineOut( power ); }
			}

			// water at the bobber (read back from last frame's query)
			query.SetPoint( slot, ( float ) bobber.x, ( float ) bobber.z );
			if ( query.cpuValid )
			{
				float h = query.cpu[ slot * 4 ], f = query.cpu[ slot * 4 + 3 ];
				if ( ! float.IsNaN( h ) && ! float.IsInfinity( h ) ) waterY = h;
				if ( ! float.IsNaN( f ) && ! float.IsInfinity( f ) ) depth = Math.Max( 0, ( double ) h - f );
			}

			double ground = heightAt( bobber.x, bobber.z );
			if ( state == "flying" )
			{
				// ballistic with a little drag, capped by the rod's casting range
				bobberVel.y -= GRAV * dt;
				bobberVel.multiplyScalar( Math.Exp( - dt * 0.25 ) );
				bobber.addScaledVector( bobberVel, dt );
				double range = JS.Hypot( bobber.x - tip.x, bobber.z - tip.z );
				if ( range > castM )
				{
					bobberVel.x *= 0.5;
					bobberVel.z *= 0.5;
				}

				double surf = Math.Max( waterY, ground );
				if ( bobber.y <= surf )
				{
					bobber.y = surf;
					bool onWater = waterY > ground + 0.05;
					setState( onWater ? "floating" : "retrieving" );
					bobberVel.set( 0, 0, 0 );
					if ( onWater && audio != null ) audio.plop( bobber );
					if ( onLand != null ) onLand( onWater ? "water" : "ground" );
				}
			}
			else if ( state == "floating" )
			{
				// riding the waves; dips with the bite cues
				double bob = Math.Sin( t * 2.1 ) * 0.008;
				double yT = waterY + 0.012 + bob - dip * 0.09;
				bobber.y += ( yT - bobber.y ) * ( 1 - Math.Exp( - dt * 12 ) );
			}
			else if ( state == "fighting" && fight != null )
			{
				// the fish runs about at the fight's distance, the bobber dragged under near it
				_wander += dt * ( 0.4 + fight.surge * 1.5 );
				_v.copy( fishPos ).sub( tip ); _v.y = 0;
				double d0 = _v.length(); if ( d0 == 0 ) d0 = 1;
				_v.multiplyScalar( 1 / d0 );
				var sideways = _x.set( - _v.z, 0, _v.x ).multiplyScalar( Math.Sin( _wander ) * 0.9 * dt * ( 1 + fight.surge ) );
				double dist = Math.Max( 1, fight.distance );
				fishPos.set( tip.x + _v.x * dist, 0, tip.z + _v.z * dist ).add( sideways );
				bobber.x += ( fishPos.x - bobber.x ) * ( 1 - Math.Exp( - dt * 6 ) );
				bobber.z += ( fishPos.z - bobber.z ) * ( 1 - Math.Exp( - dt * 6 ) );
				bobber.y += ( waterY - 0.05 - 0.2 * fight.surge - bobber.y ) * ( 1 - Math.Exp( - dt * 8 ) );
			}
			else if ( state == "landing" )
			{
				// lifted out and swung in to hang in front of you, a little right of centre and below eye level
				double kk = Math.Min( 1, t / 0.6 );
				double e = kk * kk * ( 3 - 2 * kk );
				var fwd = worldDirection( _x ); fwd.y = 0; fwd.normalize();
				_v.copy( cam.position ).addScaledVector( fwd, 1.25 ).add( _h.set( - fwd.z, 0, fwd.x ).multiplyScalar( 0.15 ) );
				_v.y += 0.12;
				bobber.lerpVectors( _landFrom ?? bobber, _v, e );
				bobber.y += Math.Sin( e * Math.PI ) * 0.8;
			}
			else if ( state == "retrieving" )
			{
				// reel an empty line in: the bobber skims back to the rod
				_v.copy( tip ).sub( bobber ); _v.y = 0;
				double d = _v.length();
				double step = Math.Min( d, ( 3 + reelSpeed * 3 ) * dt );
				if ( d > 1e-3 ) bobber.addScaledVector( _v.multiplyScalar( 1 / d ), step );
				bobber.y += ( Math.Max( waterY + 0.02, ground ) - bobber.y ) * ( 1 - Math.Exp( - dt * 10 ) );
				if ( d < 2.2 ) setState( "idle" );
			}

			// ---- line
			bool @out = lineInWater;
			lineOut = @out ? tip.distanceTo( bobber ) : 0;
			lineA.copy( tip );
			lineB.copy( bobber );
			double taut = state == "fighting" ? Math.Min( 1, ( fight != null ? fight.tension : 0 ) * 1.5 ) : state == "retrieving" ? 0.6 : 0;
			double sag = lineOut * ( state == "flying" ? 0.03 : 0.07 ) * ( 1 - taut ) + 0.02;
			lineCtl.copy( tip ).lerp( bobber, 0.5 ); lineCtl.y -= sag;
			// the line near the rod follows the rod's motion a little (whips on the flick)
			if ( state == "flying" ) lineCtl.addScaledVector( _v.copy( tip ).sub( tipOld ), - 2 );
			lineShow = 1;
			lineVisible = visible && @out;
			bobberVisible = visible && @out && state != "landing";
			// a real float is a few pixels at casting range: grow it with distance so it stays readable
			double camD = bobber.distanceTo( cam.position );
			bobberScale = Math.Max( 1, camD / 7 );
			// bobber tilts toward the pull
			bobberTilt = dip * 0.4;
		}
	}
}
