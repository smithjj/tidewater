using System;
using System.Collections.Generic;
using Tidewater.Engine;
using Tidewater.Player;
using Tidewater.Util;

// Port of src/world/marine/WhaleBrain.js: the behaviour of the humpback. A loop through the deep water of the bay mouth and past the reef drop-off (~4.5 min),
// a surfacing sequence of 3-6 breaths (rise, blow, roll back under) in view of the beach and the pier, and a fluke-up dive back to depth.
//
// Outputs for the rig (Whale.Pose): the root pose (position, quaternion), the path orientation history (the body follows the route), stroke phase / amplitude, arch,
// head pitch, flipper rotations, and events: blow intensity (0..1) and fluke-lift (drips). All in SIM space (x east, y up, z south), in doubles like the JS.
namespace Tidewater.World.Marine
{
	public sealed class WhaleBrain
	{
		// route (x, z) through the bay, checked against TerrainData: passes ~130 m off the beach and ~65 m from the pier head; >= 8.8 m of water everywhere
		static readonly double[][] ROUTE = { new double[] { 70, 300 }, new double[] { 60, 200 }, new double[] { 35, 120 }, new double[] { 5, 88 }, new double[] { -25, 100 },
			new double[] { -52, 128 }, new double[] { -75, 178 }, new double[] { -52, 262 }, new double[] { -10, 330 } };
		const double SURFACE_AT = 0.25;   // route fraction where the surfacing sequence starts (heading in toward the beach)
		const double CRUISE_SPEED = 2.6;  // m/s underwater
		const double SURFACE_SPEED = 1.5;
		const double TAU = Math.PI * 2;

		static readonly Euler _e = new Euler();
		static readonly Engine.Quaternion _q = new Engine.Quaternion();
		static readonly Engine.Vector3 _p = new Engine.Vector3();

		// one keyframe of the surfacing sequence (duration, target depth of the root below the water, pitch offset, arch, follow, speed, stroke, flags)
		public sealed class Key
		{
			public double t, depth, pitch, arch, follow, speed, stroke;
			public bool blow, fluke;
			public string breach;
		}

		public sealed class Seq { public List<Key> keys; public int i; public double t; public bool blown, splashed; }

		// a pectoral flipper: sweep (fore / aft), lift (up / down) and twist (about the long axis), each a heavy damped spring toward its target
		public sealed class Fin { public double sweep, lift, twist, vs, vl, vt; }

		public sealed class Slap { public int side; public double t; public bool hit; }

		public readonly TerrainData terrain;
		readonly IWaterQuery query;
		readonly Func<double> rand;
		public double length;
		int rN;
		double[] rPos, rYaw;
		double yawWrap;
		public CatmullRomCurve3 curve;

		public double u;
		public readonly Engine.Vector3 position = new Engine.Vector3(), velocity = new Engine.Vector3();
		public readonly Engine.Quaternion quaternion = new Engine.Quaternion();
		public double speed = CRUISE_SPEED;
		public double yaw, pitch, roll, rollV, breachRoll;
		public double breaches, splashes;   // counters of breach events (read by the water marks and the sound)
		public double bob, y = -9, vy, water, wetAge = 100;
		public double strokePhase, strokeAmp = 0.13, arch, headPitch, follow = 0.85;
		public double blow, flukeUp;
		public readonly double[] flip = { 0, 0 }, flipV = { 0, 0 };
		public readonly Fin[] fin = { new Fin(), new Fin() };
		public Slap slap;
		public double slapTimer = 12;
		public double slaps;
		public double yawRate, time;
		public bool forceBreach;
		public Action onBlow;

		// path history: root orientation stamped with the travelled distance (ring buffer, one entry per update). The tail samples it at (arc - d) with
		// interpolation, so the body follows the route continuously at any frame rate.
		public readonly int HN = 2048;
		readonly double[] hArc;
		readonly float[] hQ;
		int hHead = -1, hCount;
		public double arc;
		public Seq seq;
		public string state = "cruise";
		bool lastWrap;
		readonly int slot = -1;

		public WhaleBrain( TerrainData terrain, IWaterQuery query = null, uint seed = 1 )
		{
			this.terrain = terrain; this.query = query;
			var m = new Mulberry32( seed ); rand = m.Next;
			hArc = new double[ HN ]; hQ = new float[ HN * 4 ];
			BuildRoute();
			u = length * ( SURFACE_AT - 0.06 ); // start just before the surfacing zone
			if ( query != null ) slot = query.Allocate( "whale", 1 );
			Place( 0 );
			// seed the history with a straight run-in
			for ( int i = 60; i >= 0; i -- ) Record( -i * 0.4 );
		}

		void BuildRoute()
		{
			var pts = new List<Engine.Vector3>();
			foreach ( var p in ROUTE ) pts.Add( new Engine.Vector3( p[ 0 ], 0, p[ 1 ] ) );
			curve = new CatmullRomCurve3( pts, true, "centripetal", 0.5 ) { arcLengthDivisions = 4000 };
			length = curve.getLength();
			// dense arc-length table: position and heading, sampled every 0.25 m (smooth C1 route)
			int n = ( int ) Math.Ceiling( length / 0.25 );
			rN = n; rPos = new double[ ( n + 1 ) * 2 ]; rYaw = new double[ n + 1 ];
			var pp = new Engine.Vector3(); var tg = new Engine.Vector3();
			double prevYaw = 0;
			for ( int i = 0; i <= n; i ++ )
			{
				double uu = ( double ) i / n;
				curve.getPointAt( uu, pp );
				curve.getTangentAt( uu, tg );
				rPos[ i * 2 ] = pp.x; rPos[ i * 2 + 1 ] = pp.z;
				double yw = Math.Atan2( tg.x, tg.z );
				if ( i > 0 ) yw = prevYaw + Math.Atan2( Math.Sin( yw - prevYaw ), Math.Cos( yw - prevYaw ) ); // unwrapped
				rYaw[ i ] = prevYaw = yw;
			}

			yawWrap = rYaw[ n ] - rYaw[ 0 ]; // one loop turns by 2 pi
		}

		void RouteIndex( double uu, out int i, out double t, out int loops )
		{
			double L = length;
			loops = ( int ) Math.Floor( uu / L );
			double f = ( uu - loops * L ) / L * rN;
			i = Math.Min( ( int ) Math.Floor( f ), rN - 1 );
			t = f - i;
		}

		public Engine.Vector3 routeAt( double uu, Engine.Vector3 o )
		{
			RouteIndex( uu, out int i, out double t, out _ );
			var P = rPos;
			return o.set( P[ i * 2 ] + ( P[ i * 2 + 2 ] - P[ i * 2 ] ) * t, 0, P[ i * 2 + 1 ] + ( P[ i * 2 + 3 ] - P[ i * 2 + 1 ] ) * t );
		}

		// heading (unwrapped, continuous over loops) at route distance u, smoothed over +-2 m
		public double routeYaw( double uu )
		{
			double s = 0;
			for ( int o = -2; o <= 2; o ++ )
			{
				RouteIndex( uu + o, out int i, out double t, out int loops );
				s += rYaw[ i ] + ( rYaw[ i + 1 ] - rYaw[ i ] ) * t + loops * yawWrap;
			}

			return s / 5;
		}

		void Record( double a )
		{
			int h = hHead = ( hHead + 1 ) % HN;
			hArc[ h ] = a;
			var q = quaternion;
			hQ[ h * 4 ] = ( float ) q.x; hQ[ h * 4 + 1 ] = ( float ) q.y; hQ[ h * 4 + 2 ] = ( float ) q.z; hQ[ h * 4 + 3 ] = ( float ) q.w;
			hCount = Math.Min( hCount + 1, HN );
		}

		double ArcAt( int k ) => hArc[ ( hHead - k + HN ) % HN ];

		// orientation the root had when it was `d` metres of travel behind its current position
		public Engine.Quaternion pathRotation( double d, Engine.Quaternion o )
		{
			double target = arc - d;
			int N = HN, H = hHead;
			int lo = 0, hi = hCount - 1; // lo: newest index offset, hi: oldest
			if ( target >= ArcAt( 0 ) ) return Q( H, o );
			if ( target <= ArcAt( hi ) ) return Q( ( H - hi + N ) % N, o );
			while ( hi - lo > 1 )
			{
				int m = ( lo + hi ) >> 1;
				if ( ArcAt( m ) > target ) lo = m; else hi = m;
			}

			double a0 = ArcAt( lo ), a1 = ArcAt( hi );
			double t = ( a0 - target ) / Math.Max( a0 - a1, 1e-9 );
			Q( ( H - lo + N ) % N, o );
			return o.slerp( Q( ( H - hi + N ) % N, _q ), t );
		}

		Engine.Quaternion Q( int idx, Engine.Quaternion o ) => o.set( hQ[ idx * 4 ], hQ[ idx * 4 + 1 ], hQ[ idx * 4 + 2 ], hQ[ idx * 4 + 3 ] );

		public Engine.Quaternion flipperRotation( int side, Engine.Quaternion o )
		{
			// side 0 = left (+x), 1 = right; flippers hang lower and sweep with the stroke
			double sg = side == 0 ? 1 : -1;
			var f = fin[ side ];
			_e.set( f.twist, sg * ( 0.12 + f.sweep ), sg * ( -0.28 + f.lift ), "YZX" );
			return o.setFromEuler( _e );
		}

		// Flippers: slow rowing strokes while cruising (sweep forward feathered, pull back flat), banked into turns (the inside flipper dips, the outer one rises and
		// reaches forward), angled with the dives and the rise to the surface, spread wide in a breach, and now and then at the surface a big lift of one flipper clear
		// of the water and a slap back down.
		void Flippers( double dt, Key target )
		{
			bool surface = state == "surface" && water - y < 2.5;
			// turning: bank from the yaw rate (smoothed)
			double turn = Math.Max( -1, Math.Min( 1, yawRate * 25 ) );
			double row = time * TAU / 11; // one heavy stroke every ~11 s
			bool air = target.breach == "air";
			// the slap: chosen now and then while the back is at the surface
			if ( surface && water - y < 1.35 && slap == null && ! air )
			{
				slapTimer -= dt;
				if ( slapTimer < 0 )
				{
					slap = new Slap { side = rand() < 0.5 ? 0 : 1, t = 0 };
					slapTimer = 18 + rand() * 25;
				}
			}

			for ( int s = 0; s < 2; s ++ )
			{
				var f = fin[ s ]; double sg = s == 0 ? 1 : -1;
				double ph = row + s * 0.35;
				double sweep = 0.18 * Math.Sin( ph ) + 0.1 + ( surface ? 0.12 : 0 );
				double lift = 0.1 * Math.Sin( ph + 0.8 ) - sg * turn * 0.45 + ( surface ? 0.08 : 0 );
				double twist = 0.3 * Math.Cos( ph ) + pitch * 0.6;
				sweep += Math.Max( 0, sg * turn ) * 0.25; // the outer flipper reaches forward
				double stiff = 0.45;
				if ( air )
				{
					sweep = 0.45; lift = 0.7; twist = 0.3; stiff = 2.5;
				}
				else if ( slap != null && slap.side == s )
				{
					// raise the flipper high over ~3 s, hold, then bring it down hard
					double t = slap.t;
					if ( t < 3.5 ) { lift = 1.55; sweep = 0.35; twist = -0.2; stiff = 0.9; }
					else
					{
						lift = -0.45; twist = 0.35; stiff = 9;
						if ( t > 3.8 && ! slap.hit ) { slap.hit = true; slaps ++; }
					}
				}

				double k = stiff, c = 2 * Math.Sqrt( stiff ) * 0.9;
				f.vs += ( ( sweep - f.sweep ) * k - f.vs * c ) * dt;
				f.vl += ( ( lift - f.lift ) * k - f.vl * c ) * dt;
				f.vt += ( ( twist - f.twist ) * k - f.vt * c ) * dt;
				f.sweep += f.vs * dt;
				f.lift += f.vl * dt;
				f.twist += f.vt * dt;
			}

			if ( slap != null )
			{
				slap.t += dt;
				if ( slap.t > 6 ) slap = null;
			}
		}

		public double floorAt( double x, double z ) => terrain != null ? terrain.HeightAt( x, z ) : -50;

		void Place( double dt )
		{
			var p = routeAt( u, _p );
			double yaw0 = yaw;
			yaw = routeYaw( u );
			if ( dt > 0 ) yawRate += ( Math.Atan2( Math.Sin( yaw - yaw0 ), Math.Cos( yaw - yaw0 ) ) / dt - yawRate ) * Math.Min( 1, dt * 1.2 );
			// bank into turns: curvature (rad / m) x speed = yaw rate; eased, gentle
			double curv = ( routeYaw( u + 3 ) - routeYaw( u - 3 ) ) / 6;
			double rollT = MathUtils.clamp( -curv * speed * 1.6, -0.22, 0.22 );
			rollV += ( ( rollT - roll ) * 0.6 - rollV * 1.4 ) * dt;
			roll += rollV * dt;
			position.set( p.x, y, p.z );
			// the whole body pitches gently against the tail beat (heavy, slow)
			_e.set( -( pitch + bob ), yaw, roll + breachRoll, "YXZ" );
			quaternion.setFromEuler( _e );
		}

		static Key K( double t, double depth, double pitch, double arch, double follow, double speed, double stroke, bool blow = false, string breach = null, bool fluke = false )
			=> new Key { t = t, depth = depth, pitch = pitch, arch = arch, follow = follow, speed = speed, stroke = stroke, blow = blow, breach = breach, fluke = fluke };

		// Surfacing sequence: keyframes of (duration, target depth of the root below the water, pitch offset, arch, follow, speed, blow)
		void StartSequence()
		{
			int n = 3 + ( int ) Math.Floor( rand() * 4 ); // 3-6 breaths
			var k = new List<Key>();
			k.Add( K( 9, 1.3, 0.08, 0, 0.85, SURFACE_SPEED, 0.07 ) ); // rise
			for ( int i = 0; i < n; i ++ )
			{
				k.Add( K( 3.5, 1.12, 0.1, 0.02, 0.85, SURFACE_SPEED, 0.03, blow: true ) );
				if ( i < n - 1 )
				{
					// roll back under: the head goes down, the back and dorsal fin roll through
					k.Add( K( 4, 2.2, -0.14, -0.05, 0.92, SURFACE_SPEED, 0.04 ) );
					k.Add( K( 7 + rand() * 6, 3.4, 0, 0, 0.85, SURFACE_SPEED, 0.06 ) );
					k.Add( K( 5, 1.3, 0.08, 0, 0.85, SURFACE_SPEED, 0.06 ) );
				}
			}

			// now and then (about every other surfacing, i.e. every few minutes) a breach: sound, then drive up and launch two thirds of the body out of the water,
			// twist, fall back on the side
			if ( rand() < ( forceBreach ? 1 : 0.5 ) )
			{
				k.Add( K( 7, 9, -0.2, 0, 0.6, 2.4, 0.1 ) );
				k.Add( K( 6, 9, 0.45, 0, 0.3, 3.5, 0.14, breach: "launch" ) );
				k.Add( K( 6, 2, 0, 0, 0.3, 2.5, 0.05, breach: "air" ) );
				k.Add( K( 6, 2.4, 0.05, 0, 0.85, SURFACE_SPEED, 0.05 ) );
			}

			// terminal dive: arch the back high, pitch down steeply, the flukes lift clear and slip under
			k.Add( K( 3, 1.25, -0.12, -0.14, 0.85, 1.8, 0.02 ) );
			k.Add( K( 4.5, 5.5, -0.95, -0.12, 0.12, 1.9, 0.0, fluke: true ) );
			k.Add( K( 6, 11, -0.45, 0, 0.6, 2.2, 0.08 ) );
			k.Add( K( 8, 10, 0, 0, 0.85, CRUISE_SPEED, 0.1 ) );
			seq = new Seq { keys = k, i = 0, t = 0, blown = false };
			state = "surface";
		}

		// jump to the next key of the sequence (the breach launch ends when the whale breaks through)
		void NextKey( string expect )
		{
			var s = seq;
			if ( s == null || s.i + 1 >= s.keys.Count || s.keys[ s.i + 1 ].breach != expect ) return;
			s.i ++;
			s.t = 0;
			breaches ++;
		}

		public void update( double dt )
		{
			dt = Math.Min( dt, 0.1 );
			time += dt;
			// water level at the head (read back from the GPU water query, 1-3 frames old)
			var q = query;
			if ( q != null )
			{
				double hx = position.x + Math.Sin( yaw ) * 3, hz = position.z + Math.Cos( yaw ) * 3;
				q.SetPoint( slot, ( float ) hx, ( float ) hz );
				if ( q.cpuValid )
				{
					double h = q.cpu[ slot * 4 ];
					if ( ! double.IsNaN( h ) && ! double.IsInfinity( h ) ) water += ( h - water ) * Math.Min( 1, dt * 4 );
				}
			}

			// wet film: fresh while the back is out of the water, drying after a few seconds
			wetAge = backDepth < 0.05 ? wetAge + dt : 0;

			// ---- sequencer
			double frac = ( ( u / length ) % 1 + 1 ) % 1;
			if ( state == "cruise" && frac > SURFACE_AT && frac < SURFACE_AT + 0.05 && ! lastWrap )
			{
				StartSequence();
				lastWrap = true;
			}

			if ( frac < SURFACE_AT - 0.1 || frac > SURFACE_AT + 0.3 ) lastWrap = false;
			var target = new Key { depth = 9.5, pitch = 0, arch = 0, follow = 0.85, speed = CRUISE_SPEED, stroke = 0.13 };
			blow = 0;
			flukeUp = 0;
			if ( seq != null )
			{
				var s = seq;
				var key = s.keys[ s.i ];
				s.t += dt;
				target = key;
				if ( key.blow )
				{
					// exhale when the blowholes clear the water (about 0.6 s into the hold), ~1.4 s
					double tb = s.t - 0.6;
					blow = tb > 0 && tb < 1.4 ? Math.Pow( Math.Sin( Math.PI * Math.Min( tb / 1.4, 1 ) ), 0.5 ) : 0;
					if ( tb > 0 && ! s.blown )
					{
						s.blown = true;
						onBlow?.Invoke();
					}
				}

				if ( key.fluke ) flukeUp = Math.Min( 1, s.t / key.t );
				if ( s.t >= key.t )
				{
					s.i ++;
					s.t = 0;
					s.blown = false;
					if ( s.i >= s.keys.Count )
					{
						seq = null;
						state = "cruise";
					}
				}
			}

			// ---- depth: critically damped toward the target, never near the seafloor
			double x = position.x, z = position.z;
			double sy = Math.Sin( yaw ), cy = Math.Cos( yaw );
			double floor = -1e9;
			// look ahead along the heading so the whale rises before the seabed does
			foreach ( double d in new double[] { -9, -5, 0, 5, 10, 16, 24 } ) floor = Math.Max( floor, floorAt( x + sy * d, z + cy * d ) );
			double yT = Math.Max( water - target.depth, floor + 5.2 );
			double w = target.fluke ? 1.0 : 0.7;
			double ay = ( yT - y ) * w * w - 2 * w * vy;
			if ( target.breach == "launch" )
			{
				// drive for the surface; out of the water at ~9 m/s
				ay = 14;
				if ( y > water - 2.5 ) NextKey( "air" );
			}
			else if ( target.breach == "air" )
			{
				// ballistic above the water (buoyancy and drag take over below), twisting onto the side
				ay = vy > 0 || y > water - 1 ? -9.81 : ( yT - y ) * 0.5 - 1.5 * vy;
				breachRoll += ( 1.9 - breachRoll ) * Math.Min( 1, dt * 1.2 );
				if ( vy < 0 && y < water - 1.5 && ! seq.splashed )
				{
					seq.splashed = true;
					splashes ++;
				}
			}

			if ( target.breach != "air" ) breachRoll += ( 0 - breachRoll ) * Math.Min( 1, dt * 0.6 );
			vy += ay * dt;
			y += vy * dt;
			// hard floor: never closer than ~3.3 m (belly ~1.9 m) above the seabed under the body
			double hard = floor + 4.6;
			if ( y < hard )
			{
				y = hard;
				vy = Math.Max( vy, 0 );
			}

			double k2 = Math.Min( 1, dt * 1.2 );
			speed += ( target.speed - speed ) * Math.Min( 1, dt * 0.5 );
			double pitchT = Math.Atan2( vy, Math.Max( speed, 0.5 ) ) * 0.8 + target.pitch;
			pitch += ( pitchT - pitch ) * Math.Min( 1, dt * ( target.breach != null ? 2.5 : target.fluke ? 1.1 : 0.9 ) );
			arch += ( target.arch - arch ) * k2;
			follow += ( target.follow - follow ) * Math.Min( 1, dt * ( target.fluke ? 1.5 : 0.8 ) );
			strokeAmp += ( target.stroke - strokeAmp ) * Math.Min( 1, dt * 0.6 );
			headPitch += ( ( target.blow ? 0.03 : 0 ) - headPitch ) * k2;
			// tail beat: slow and heavy, ~5 s per stroke cruising, longer at the surface
			strokePhase += TAU * ( 0.09 + 0.042 * speed ) * dt;
			bob = -strokeAmp * 0.12 * Math.Sin( strokePhase + 0.5 );
			Flippers( dt, target );

			// ---- advance along the route (horizontal speed shrinks when steeply pitched)
			double du = speed * Math.Cos( pitch ) * dt;
			u += du;
			Place( dt );
			// path history (orientation at this travelled distance)
			arc += du;
			Record( arc );
		}

		// depth of the highest point of the back below the water (m, > 0 = submerged)
		public double backDepth => water - ( y + 1.25 );
	}

}
