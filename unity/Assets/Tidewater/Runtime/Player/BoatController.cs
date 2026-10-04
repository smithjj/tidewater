using System;
using System.Collections.Generic;
using Tidewater.Engine;
using Tidewater.World;
using Tidewater.World.Boat;

// Port of src/player/BoatController.js: the rigid-body model of an 8.2 m, 3.2 t Downeast lobster boat (semi-displacement hull, full keel).
// SIM space (three.js axes: x east, y up, z south), boat frame +Z forward, +Y up, +X port, doubles throughout, the same operation order
// and the same scratch vectors as the JS (so the aliasing is the same too); only the render boundary (BoatView.SetPose) mirrors.
//
// Hydrostatics come from the hull model's buoyancy samples (waterplane patches, water heights queried on the GPU). The samples are laid
// out as a slightly narrower / shorter "effective" waterplane so the boat has a lobster boat's feel rather than the stiffness of the
// bare canoe body: GM ~0.6 m, roll ~3.3 s, pitch ~1.8 s, heave ~1.25 s, all well damped. Added mass and inertia (heave, pitch, sway...)
// scale with how much of the hull is in the water.
//
// Manoeuvring: calm-water resistance curve with the wave-making hump past hull speed; propeller thrust from a spooling engine (falls off
// towards the pitch speed); rudder as a lifting surface in the propeller race; hull lift and cross-flow drag along the keel (directional
// stability, the turning circle, speed lost in turns, outward heel); running trim and rise with speed (the bow lifts over the hump, then
// runs at a few degrees); prop walk astern. Grounding, pier piles and mooring lines as before.
namespace Tidewater.Player
{
	// The anchor (a boat holds still on it): dropped from the bow chock, it lies on the seabed where it landed, and the rode (scope times
	// the depth) lets the boat swing about it until the line comes taut. Past that the line pulls at the chock like a stiff, damped spring
	// (it only pulls), so a boat blown or drifting downwind brings up on it and swings bow to the anchor, the way a real one lies.
	public static class ANCHOR
	{
		public const double maxDepth = 20;      // m of water: deeper and the rode would be too long to hold it
		public const double minDepth = 0.8;     // m: aground, not afloat
		public const double maxSpeed = 2.5;     // m/s through the water: faster and the anchor would not set (and would not be safe to let go)
		public const double scope = 5;          // rode as a multiple of the depth (5:1 for a light boat in fair weather)
		public const double spare = 4;          // m added to the rode for the height of the chock above the water
		public const double minRode = 8;
		public const double k = 9000;           // N per m of stretch past the rode
		public const double c = 7000;           // N s / m, only while the line is lengthening (it does not push the boat back)
		public const double maxTension = 60000; // N: what the anchor, line and chock will take (the engine's bollard pull is ~26 kN)
	}

	public sealed class BoatDock
	{
		public Vector3 position; public double heading;
		public BoatDock( Vector3 position, double heading ) { this.position = position; this.heading = heading; }
		public static BoatDock Lobster => new BoatDock( new Vector3( 64.5, 0, 36.5 ), 0 );               // WORLD.boatDock
		public static BoatDock Pelagic => new BoatDock( new Vector3( 43.5, 0, 38 ), -0.35 );            // WORLD.pelagicMooring
		public static BoatDock Mini => new BoatDock( new Vector3( 55, 0, 44 ), 0.1 );                   // WORLD.miniMooring
	}

	// What the controller reads of a boat model (BoatModel.js / Pelagic30.js): the hydrostatics, the buoyancy samples, the anchor points and
	// the optional tuning overrides (null = the lobster boat's defaults).
	public sealed class BoatDynamics
	{
		public BoatHydro hydro;
		public List<HullSample> hullSamples;
		public Vector3 propeller;
		public double rudderZ;
		// the hull lines (reserve buoyancy of the topsides, the deck the chock stands on); none: no reserve
		public bool hasLines;
		public double zAft, zFwd, deckY = 0.9;
		public Func<double, double, double> halfBreadth;   // ( t, y )
		public Func<double, double> tAtSheerZ;
		public double reserveRise = 0.1, reserveInset = 0.97, reserveArea = 0.5;
		public double[] reserveStations = { 0.2, 0.32, 0.44, 0.56, 0.68 };
		public double[][] stations; public double? lateralY, hullLift, rudderLift, maxThrust, pitchSpeed, reverseFactor, bowZ, sternZ;
		public Vector3[] contactPoints, outline;
		// the damping, resistance and mooring constants are tuned on the 3.2 t lobster boat; a much lighter hull (the mini fishing boat) scales them
		// down with forceScale (its mass ratio, about). reserveSamples (position, area) replace the topsides' reserve buoyancy computed from the hull
		// lines; chockY is the bow chock's height where there are no lines.
		public double forceScale = 1; public double? chockY;
		public List<HullSample> reserveSamples;
	}

	// The model's visual: the controller hands it the pose and the control positions (BoatView)
	public interface IBoatVisual
	{
		void SetPose( Vector3 position, Quaternion quaternion );
		void SetSteering( double a );
		void SetThrottle( double t );
		void SetPropellerRPM( double rpm );
	}

	// The water queries the controller reads (WaterQuery.cs)
	public interface IWaterQuery
	{
		float latency { get; }
		bool cpuValid { get; }
		int version { get; }
		double resultTime { get; }
		float[] cpu { get; }
		float[] resultInputs { get; }
		int Allocate( string name, int n );
		void SetPoint( int i, float x, float z );
	}

	public sealed class BoatController
	{
		const double RHO = 1025; // sea water density
		const double GRAV = 9.81;
		const double DEG = Math.PI / 180;

		static double sstep( double x, double a, double b ) => MathUtils.smoothstep( x, a, b );
		static double clamp( double v, double lo, double hi ) => MathUtils.clamp( v, lo, hi );

		// scratch (the integrator runs at 120 Hz: no allocations in the loop)
		static readonly Vector3 _F = new Vector3(), _T = new Vector3(), _com = new Vector3(), _p = new Vector3(), _r = new Vector3(), _f = new Vector3(),
			_vp = new Vector3(), _vl = new Vector3(), _a = new Vector3(), _fwd = new Vector3(), _side = new Vector3(), _up = new Vector3(), _v = new Vector3(),
			_rud = new Vector3(), _g = new Vector3(), _t = new Vector3(), _c1 = new Vector3(), _c2 = new Vector3(), _c3 = new Vector3(), _c4 = new Vector3(),
			_c5 = new Vector3(), _chock = new Vector3(), _ch = new Vector3();
		static int _boatCount = 0; // controllers made so far: each gets its own block of water queries
		static readonly Quaternion _invQ = new Quaternion(), _dq = new Quaternion();

		public sealed class Sample { public Vector3 p; public double area, bottom; public bool reserve; public int @ref; }

		public sealed class AnchorState { public bool down; public double x, z, rode, depth, tension; }
		public struct AnchorResult { public bool ok; public double rode; public string reason; }

		public readonly BoatDynamics model;
		public readonly IWaterQuery query;
		readonly Func<double, double, double> terrainHeightAt;
		public readonly Colliders colliders;
		public IBoatVisual view;
		public BoatModel boatModel; // the model data of the boat (JS: `model`: helm / board / exit points, colliders, hull lines), for the player

		public double mass, forceScale;
		public Vector3 com, inertia;
		public Vector3 addedMass = new Vector3( 0.6, 0.7, 0.05 );   // x (sway), y (heave), z (surge)
		public Vector3 addedInertia = new Vector3( 1.0, 0.4, 0.2 ); // x (pitch), y (yaw), z (roll)
		public readonly List<Sample> samples;
		public double pitchStiffness, BG;
		public int nHull, slot;
		public double[][] stations;
		public double lateralY, bank, hullLift, rudderLift;
		public Vector3 position, velocity = new Vector3(), angular = new Vector3();
		public Quaternion quaternion;
		public double throttle, steer, rpm, maxThrust, pitchSpeed, reverseFactor;
		public double throttleTarget;
		public bool driven, moored;
		public BoatDock homeDock;
		public Vector3 mooringAnchor; public double mooringHeading;
		public readonly AnchorState anchor = new AnchorState();
		public readonly float[] waterH, waterV, waterOff, hEff, qx, qz, gx, gz;
		public bool hasWater;
		public double wetFraction, speed, forwardSpeed, thrust, slam;
		public Action<double> onSlam;
		double _acc, _age;
		int _qVersion = -1; double? _qTime;
		public readonly Vector3 bowWorld = new Vector3(), sternWorld = new Vector3();
		public Vector3[] contactPoints, outline;

		public BoatController( BoatDynamics model, IWaterQuery query, Func<double, double, double> terrainHeightAt, Colliders colliders, BoatDock dock = null )
		{
			dock = dock ?? BoatDock.Lobster;
			this.model = model;
			this.query = query;
			this.terrainHeightAt = terrainHeightAt;
			this.colliders = colliders;

			var hydro = model.hydro;
			mass = hydro != null && hydro.suggestedMass != 0 ? hydro.suggestedMass : 3200;
			forceScale = model.forceScale;
			com = hydro != null && hydro.centerOfMass != null ? new Vector3().copy( hydro.centerOfMass ) : new Vector3( 0, 0.3, - 0.74 );
			// principal inertia in the boat frame (+Z forward, +X port): x = pitch, y = yaw, z = roll. Gear high on deck (traps, hauler,
			// wheelhouse) gives a larger roll radius than the bare hull.
			inertia = hydro != null && hydro.inertia != null ? hydro.inertia.clone() : new Vector3( 14600, 15700, 3500 );
			inertia.z *= 1.24;

			// effective waterplane: lateral / longitudinal lever arms of the buoyancy samples
			double cbz = hydro != null && hydro.centerOfBuoyancy != null ? hydro.centerOfBuoyancy.z : com.z;
			double sx = 0.79, sz = 0.9;
			samples = new List<Sample>();
			foreach ( var s in model.hullSamples )
				samples.Add( new Sample { p = new Vector3( s.position.x * sx, s.position.y, cbz + ( s.position.z - cbz ) * sz ), area = s.area, bottom = s.bottomY } );
			double pitchK = 0;
			foreach ( var s in samples ) pitchK += s.area * ( s.p.z - cbz ) * ( s.p.z - cbz );
			pitchStiffness = RHO * GRAV * pitchK; // N m / rad (for the running-trim moment)
			// COM above the centre of buoyancy, as this sample model has it (each buoyancy force acts at its own sample, so the centre is the
			// force-weighted sample height at the design draft, not the hull's): the lever the wave-slope torque acts through (see step)
			double fy = 0, fs = 0;
			foreach ( var s in samples ) { double dd = Math.Max( - s.p.y, 0 ); fy += s.area * dd * s.p.y; fs += s.area * dd; }
			BG = fs > 0 ? Math.Max( 0, com.y - fy / fs ) : 0;
			nHull = samples.Count;
			samples.AddRange( reserveSamples() );
			// Each reserve sample reads the water from the hull sample nearest it (see step): only the hull samples are queried, and each
			// boat has a block of its own.
			foreach ( var r in samples )
			{
				if ( ! r.reserve ) continue;
				int best = 0; double bd = double.PositiveInfinity;
				for ( int i = 0; i < nHull; i ++ )
				{
					var q = samples[ i ].p; double d = ( q.x - r.p.x ) * ( q.x - r.p.x ) + ( q.z - r.p.z ) * ( q.z - r.p.z );
					if ( d < bd ) { bd = d; best = i; }
				}

				r.@ref = best;
			}

			slot = query.Allocate( "boatHull" + ( _boatCount ++ ), nHull );

			// lateral stations along the keel: (z, lateral area m^2) for hull lift and cross-flow drag (overridable per model)
			stations = model.stations ?? new[] { new[] { -3.4, 0.73 }, new[] { -2.3, 0.78 }, new[] { -1.2, 0.8 }, new[] { -0.1, 0.77 }, new[] { 1.0, 0.62 }, new[] { 2.1, 0.33 }, new[] { 3.2, 0.14 } };
			lateralY = model.lateralY ?? 0.06; // height of the centre of lateral resistance (boat frame)
			bank = 0; // roll moment per (u * drift velocity): hull bottom lift banking into turns
			hullLift = model.hullLift ?? 0.5; // lift coefficient of the hull + keel per radian of drift
			rudderLift = model.rudderLift ?? 2.8; // rudder lift slope (x area 0.12 m^2), includes the hull's flap effect

			// state (position = model origin at the design waterline)
			position = new Vector3().copy( dock.position );
			quaternion = new Quaternion().setFromAxisAngle( new Vector3( 0, 1, 0 ), dock.heading );

			throttle = 0; // lever -1..1 (moves with some inertia)
			steer = 0; // wheel -1..1
			rpm = 0; // engine 0..1 (spools after the lever)
			maxThrust = model.maxThrust ?? 26000; // N, bollard pull at full rpm
			pitchSpeed = model.pitchSpeed ?? 16; // m/s, propeller pitch speed at full rpm (thrust -> 0 there)
			reverseFactor = model.reverseFactor ?? 0.45; // astern thrust relative to ahead
			driven = false;
			moored = true;
			homeDock = dock; // where exitBoat re-moors (Player) and reset() returns to
			mooringAnchor = dock.position.clone(); mooringHeading = dock.heading;

			int n = samples.Count;
			waterH = new float[ n ]; // latest read-back
			waterV = new float[ n ]; // vertical velocity of the water (m/s)
			waterOff = new float[ n ]; // blends the step when a new read-back arrives
			hEff = new float[ n ]; // water height used by the integrator (interpolated)
			qx = new float[ n ]; // query point + slope of the last result
			qz = new float[ n ];
			gx = new float[ n ];
			gz = new float[ n ];

			// grounding contact points and the pier-pile hull outline (per-model, sized to the hull)
			contactPoints = model.contactPoints ?? new[]
			{
				new Vector3( 0, - 0.7, 3.2 ), new Vector3( 0, - 0.75, 0 ), new Vector3( 0, - 0.72, - 3.4 ),
				new Vector3( 1.1, - 0.4, 1.5 ), new Vector3( - 1.1, - 0.4, 1.5 ), new Vector3( 1.2, - 0.35, - 2.5 ), new Vector3( - 1.2, - 0.35, - 2.5 ),
				new Vector3( 0, 0.2, 4.2 ),
			};
			outline = model.outline ?? new[]
			{
				new Vector3( 0, 0.3, 4.1 ), new Vector3( 1.2, 0.3, 2.0 ), new Vector3( - 1.2, 0.3, 2.0 ),
				new Vector3( 1.4, 0.3, - 1.0 ), new Vector3( - 1.4, 0.3, - 1.0 ), new Vector3( 1.2, 0.3, - 3.8 ), new Vector3( - 1.2, 0.3, - 3.8 ),
			};

			apply();
		}

		// Reserve buoyancy of the topsides. The waterplane samples only know the hull at the waterline, so once the boat heels far enough
		// that the windward side has lifted out they run out of righting moment (the stability curve died at ~40 degrees, and the boat
		// capsized on a steep wave). A real hull keeps gaining righting arm as the flared topsides go under: a few samples out at the rail,
		// just above the sole, that only push once the water reaches them. A model without hull lines gets none.
		List<Sample> reserveSamples()
		{
			var outS = new List<Sample>();
			if ( model.reserveSamples != null ) { foreach ( var r in model.reserveSamples ) outS.Add( new Sample { p = r.position.clone(), area = r.area, bottom = r.position.y, reserve = true } ); return outS; }
			if ( ! model.hasLines || model.halfBreadth == null || model.tAtSheerZ == null ) return outS;
			double zA = model.zAft, zF = model.zFwd;
			if ( ! double.IsFinite( zA ) || ! double.IsFinite( zF ) ) return outS;
			double y = model.deckY + model.reserveRise;
			foreach ( double f in model.reserveStations )
			{
				double z = zA + f * ( zF - zA );
				double x = model.halfBreadth( model.tAtSheerZ( z ), y ) * model.reserveInset;
				if ( ! ( x > 0.2 ) ) continue;
				foreach ( double sd in new double[] { 1, -1 } ) outS.Add( new Sample { p = new Vector3( sd * x, y, z ), area = model.reserveArea, bottom = y, reserve = true } );
			}

			return outS;
		}

		// the bow chock the anchor line leads through (boat frame)
		public Vector3 chock => _chock.set( 0, model.chockY ?? ( model.hasLines ? model.deckY : 0.9 ), model.bowZ ?? 3.9 );

		// Can the anchor go down here? { ok, rode } or { ok: false, reason } (the reason reads as a toast)
		public AnchorResult canAnchor( double depth )
		{
			if ( anchor.down ) return new AnchorResult { ok = false, reason = "The anchor is already down" };
			if ( speed > ANCHOR.maxSpeed ) return new AnchorResult { ok = false, reason = "Slow down to drop the anchor" };
			if ( depth < ANCHOR.minDepth ) return new AnchorResult { ok = false, reason = "Too shallow to anchor here" };
			if ( depth > ANCHOR.maxDepth ) return new AnchorResult { ok = false, reason = "Too deep to anchor here" };
			return new AnchorResult { ok = true, rode = Math.Max( ANCHOR.minRode, depth * ANCHOR.scope + ANCHOR.spare ) };
		}

		// let the anchor go from the bow, in `depth` metres of water
		public AnchorResult dropAnchor( double depth )
		{
			var r = canAnchor( depth );
			if ( ! r.ok ) return r;
			var c = toWorld( chock, _p );
			anchor.down = true; anchor.x = c.x; anchor.z = c.z; anchor.rode = r.rode; anchor.depth = depth; anchor.tension = 0;
			return r;
		}

		public bool weighAnchor()
		{
			bool was = anchor.down;
			anchor.down = false;
			anchor.tension = 0;
			return was;
		}

		// world position of a local point
		public Vector3 toWorld( Vector3 local, Vector3 outV ) => outV.copy( local ).applyQuaternion( quaternion ).add( position );

		public Vector3 forward( Vector3 outV ) => outV.set( 0, 0, 1 ).applyQuaternion( quaternion );

		public void setInput( double throttleIn, double steerIn, double dt )
		{
			// the throttle lever moves with some inertia (the engine then spools after it); the rudder follows the wheel
			throttleTarget = throttleIn;
			throttle += ( throttleIn - throttle ) * ( 1 - Math.Exp( - dt * 2.2 ) );
			steer += ( steerIn - steer ) * ( 1 - Math.Exp( - dt * 1.2 ) );
		}

		// Queue this frame's water height queries at the hull sample positions. The results arrive q.latency seconds later, so ask where
		// the samples will be by then (at speed the hull moves ~0.5 m in that time).
		public void queueQueries()
		{
			var q = query;
			double lead = q.latency;
			for ( int i = 0; i < nHull; i ++ )
			{
				toWorld( samples[ i ].p, _v ).addScaledVector( velocity, lead );
				q.SetPoint( slot + i, ( float ) _v.x, ( float ) _v.z );
			}
		}

		// New read-backs arrive every 1-3 frames. The vertical velocity of the water under each sample is the local rate of the surface,
		// dh/dt at a fixed point: the height change between consecutive results, minus the part caused by the sample moving across the
		// slope (at speed a wave face would otherwise read as fast-rising water and launch the hull), over the time between the results.
		// Between read-backs the heights are extrapolated with that rate, and the small jump when a new result lands is blended out, so
		// the hull is never kicked by a step in the forcing.
		public void readQueries()
		{
			var q = query;
			if ( ! q.cpuValid || q.version == _qVersion ) return;
			double dt = q.resultTime - ( _qTime ?? q.resultTime );
			_qVersion = q.version;
			_qTime = q.resultTime;
			var c = q.cpu; var pts = q.resultInputs;
			for ( int i = 0; i < nHull; i ++ )
			{
				int k = ( slot + i ) * 4;
				// never let a bad GPU sample into the integrator (keep the last good value)
				double h = c[ k ];
				if ( ! double.IsFinite( h ) ) h = hasWater ? waterH[ i ] : 0;
				double nx = double.IsFinite( c[ k + 1 ] ) ? c[ k + 1 ] : 0, nz = double.IsFinite( c[ k + 2 ] ) ? c[ k + 2 ] : 0;
				double ny = Math.Sqrt( Math.Max( 1 - nx * nx - nz * nz, 0.05 ) );
				double gxn = - nx / ny, gzn = - nz / ny; // surface slope dh/dx, dh/dz
				double w = 0;
				if ( hasWater && dt > 1e-4 )
				{
					double dx = pts[ k ] - qx[ i ], dz = pts[ k + 1 ] - qz[ i ];
					w = ( h - waterH[ i ] - 0.5 * ( ( gxn + gx[ i ] ) * dx + ( gzn + gz[ i ] ) * dz ) ) / dt;
				}

				if ( hasWater ) waterOff[ i ] = ( float ) ( hEff[ i ] - h ); // where the interpolation was
				else waterOff[ i ] = 0;
				waterV[ i ] = ( float ) ( waterV[ i ] + ( clamp( w, - 3, 3 ) - waterV[ i ] ) * 0.5 );
				waterH[ i ] = ( float ) h;
				hEff[ i ] = ( float ) ( h + waterOff[ i ] );
				qx[ i ] = pts[ k ];
				qz[ i ] = pts[ k + 1 ];
				gx[ i ] = ( float ) gxn;
				gz[ i ] = ( float ) gzn;
			}

			_age = 0;
			hasWater = true;
		}

		public void update( double dt )
		{
			readQueries();
			if ( ! hasWater )
			{
				apply();
				return;
			}

			// engine: spools up after the lever (~0.9 s), a little slower down; idles while driven
			double lever = driven ? Math.Abs( throttle ) : 0;
			double target = Math.Max( lever, driven ? 0.15 : 0 );
			double tau = target > rpm ? 0.9 : 1.3;
			rpm += ( target - rpm ) * ( 1 - Math.Exp( - Math.Min( dt, 0.1 ) / tau ) );

			double h = 1.0 / 120;
			_acc += Math.Min( dt, 0.1 );
			int steps = 0;
			while ( _acc >= h && steps < 12 )
			{
				step( h );
				_acc -= h;
				steps ++;
			}

			if ( ! isFinite() ) reset();

			if ( view != null )
			{
				view.SetThrottle( throttle );
				view.SetSteering( steer );
				view.SetPropellerRPM( rpm * 2400 * JS.Sign( JS.Or( throttle, 1 ) ) );
			}

			apply();
		}

		void step( double h )
		{
			double m = mass;
			var F = _F.set( 0, - m * GRAV, 0 );
			var T = _T.set( 0, 0, 0 ); // torque about COM (world)
			var comW = toWorld( com, _com );
			var invQ = _invQ.copy( quaternion ).invert();

			void addForceAt( Vector3 fv, Vector3 pw )
			{
				F.add( fv );
				T.add( _r.copy( pw ).sub( comW ).cross( fv ) );
			}

			// water heights at the samples: latest read-back, extrapolated with the water's vertical velocity (up to ~0.12 s), step
			// blended out over ~0.06 s
			_age = Math.Min( _age + h, 0.12 );
			double blend = Math.Exp( - h / 0.06 );

			// ---- buoyancy + vertical damping per hull sample
			double wetArea = 0, totalArea = 0, immersion = 0;
			double slopeX = 0, slopeZ = 0; // the surface slope under the hull, area weighted (the waterplane samples)
			for ( int i = 0; i < samples.Count; i ++ )
			{
				var s = samples[ i ];
				var pw = toWorld( s.p, _p );
				double hw, wv;
				if ( s.reserve )
				{
					// not queried: the surface plane of the hull sample nearest it (height and slope there)
					int j = s.@ref; var pj = toWorld( samples[ j ].p, _t );
					hw = hEff[ j ] + gx[ j ] * ( pw.x - pj.x ) + gz[ j ] * ( pw.z - pj.z );
					wv = waterV[ j ];
					hEff[ i ] = ( float ) hw;
				}
				else
				{
					waterOff[ i ] = ( float ) ( waterOff[ i ] * blend );
					hw = waterH[ i ] + waterV[ i ] * _age + waterOff[ i ];
					wv = waterV[ i ];
					hEff[ i ] = ( float ) hw;
				}

				double depth = hw - pw.y;
				if ( ! s.reserve )
				{
					totalArea += s.area;
					slopeX += s.area * gx[ i ];
					slopeZ += s.area * gz[ i ];
				}

				if ( depth <= 0 ) continue;
				double sub = Math.Min( depth, 1.6 );
				if ( ! s.reserve )
				{
					double wetS = Math.Min( 1, depth / 0.3 );
					wetArea += s.area * wetS;
					immersion += s.area * Math.Min( sub / Math.Max( - s.p.y, 0.05 ), 1.5 );
				}

				// buoyancy + heave damping against the water's own vertical motion, along world up (in the boat frame a trimmed hull
				// would turn forward speed into an upward push)
				_vp.copy( angular ).cross( _r.copy( pw ).sub( comW ) ).add( velocity );
				double vy = _vp.y - wv * 0.6;
				double wetK = Math.Min( 1, sub / 0.25 ) * s.area;
				_f.set( 0, RHO * GRAV * s.area * sub - ( 1800 * vy + 900 * vy * Math.Abs( vy ) ) * wetK * forceScale, 0 );
				addForceAt( _f, pw );
			}

			double wet = wetFraction = totalArea > 0 ? wetArea / totalArea : 0;
			// how deep the hull sits relative to its design draft (lift fades as it rises out)
			double imm = totalArea > 0 ? clamp( immersion / totalArea, 0, 1 ) : 0;
			double wetD = Math.Min( 1, wet * 1.6 ); // hydrodynamic forces: most of the hull in the water
			var fwd = forward( _fwd );
			var side = _side.set( 1, 0, 0 ).applyQuaternion( quaternion ); // port
			var up = _up.set( 0, 1, 0 ).applyQuaternion( quaternion );
			_vl.copy( velocity ).applyQuaternion( invQ ); // boat frame: x port, z forward
			double u = _vl.z, vs = _vl.x;
			speed = velocity.length();
			forwardSpeed = u;
			var aLoc = _a.copy( angular ).applyQuaternion( invQ ); // x pitch, y yaw, z roll rates

			// ---- wave pressure on the hull. In a wave the water's own horizontal acceleration is ~ -g * slope (down the face), and that
			// tilts the *effective* gravity until it stands on the surface: a boat rides the slope (heel = slope), however small its GM.
			// World-vertical gravity alone does not: the weight stays plumb while the buoyancy follows the surface, and the hull heels by
			// slope * BM / GM (x1.7, x2.4 once it is moving) -- a boat that exaggerates every wave and rolls over on a steep one. The
			// missing piece is the horizontal pressure force on the hull, which acts at the centre of buoyancy, a lever BG below the COM.
			// Only the torque is applied: the boat does not slide along with the water's orbit. Short waves wash out in the average over
			// the hull. Scaled by how deep the hull sits (imm: 1 at its design draft, fading as it rises out), not by wetD, which is ~0.6
			// at rest.
			if ( totalArea > 0 && BG > 0 )
			{
				// torque = r x F with r = -BG * up (COB below COM) and F = m * a, a = -g * slope (horizontal)
				_f.set( slopeX, 0, slopeZ ).multiplyScalar( - m * GRAV * imm / totalArea ); // F (N), horizontal
				_r.copy( up ).multiplyScalar( - BG ).cross( _f );
				T.add( _r );
			}

			// ---- calm-water resistance (friction + the wave-making hump past hull speed + planing)
			double au = Math.Abs( u );
			double R = ( 40 * au + 22 * au * au + 3000 * sstep( au, 2.8, 5.4 ) + 55 * au * au * sstep( au, 7, 11 ) ) * wetD * forceScale;
			_p.set( 0, - 0.2, com.z );
			toWorld( _p, _p );
			addForceAt( _f.copy( fwd ).multiplyScalar( - R * JS.Sign( u ) ), _p );
			// air drag on hull + house (Cd ~0.9, ~6 m^2 frontal area)
			F.addScaledVector( velocity, - 3.3 * speed * forceScale );

			// ---- lateral hydrodynamics along the keel: hull lift ~ u * v and cross-flow drag ~ v|v| at each station (v includes the yaw
			// rate): directional stability, the turning circle, speed lost to the drift angle in turns and the outward heel (lateral
			// resistance below the COM)
			double induced = 0;
			foreach ( var st in stations )
			{
				double sz2 = st[ 0 ], Ast = st[ 1 ];
				double dz = sz2 - com.z;
				// lateral velocity at the station: drift + yaw rate (about +Y) + roll rate (about +Z, the keel swinging sideways below
				// the COM)
				double vk = vs + aLoc.y * dz - aLoc.z * ( lateralY - com.y );
				double Y = - 0.5 * RHO * Ast * ( hullLift * au * vk + 1.1 * vk * Math.Abs( vk ) ) * wetD;
				induced += Y * vk;
				_p.set( 0, lateralY, sz2 );
				toWorld( _p, _p );
				addForceAt( _f.copy( side ).multiplyScalar( Y ), _p );
			}

			// the drift angle also banks the hull into the turn (bottom pressure on the outer side), leaving only a slight outward heel
			T.addScaledVector( fwd, bank * au * vs * wetD );

			// ---- propeller: thrust from the engine rpm, falling off towards the pitch speed; prop walk astern (stern to port)
			var propW = toWorld( model.propeller, _p );
			double propSub = sampleWaterAt( propW ) - propW.y;
			double propWet = clamp( propSub / 0.3 + 0.5, 0, 1 );
			double dir = driven ? JS.Sign( throttle ) : 0;
			double n = rpm * ( driven && Math.Abs( throttle ) > 0.02 ? 1 : 0 );
			double thr = 0;
			if ( dir > 0 ) thr = maxThrust * n * n * Math.Max( 0, 1 - Math.Max( u, 0 ) / ( pitchSpeed * Math.Max( n, 0.2 ) ) );
			else if ( dir < 0 ) thr = - reverseFactor * maxThrust * n * n * Math.Max( 0, 1 - Math.Max( - u, 0 ) / ( pitchSpeed * 0.6 * Math.Max( n, 0.2 ) ) );
			thr *= propWet;
			thrust = thr;
			addForceAt( _f.copy( fwd ).multiplyScalar( thr ), propW );
			if ( thr < 0 ) addForceAt( _f.copy( side ).multiplyScalar( - thr * 0.08 ), propW );

			// ---- rudder: lifting surface in the propeller race (actuator-disc slipstream); the local flow angle at the stern (drift +
			// yaw) reduces its angle of attack
			// the rudder's side force is partly carried by the aft hull (flap effect), so it acts higher than the blade's centre: less
			// heel kick when the wheel goes over
			var rudW = toWorld( _rud.set( 0, - 0.15, model.rudderZ ), _rud );
			double race = Math.Max( thr, 0 ) * 2 / ( RHO * 0.14 ); // slipstream dynamic pressure term (m^2/s^2)
			double Ur2 = u * Math.Abs( u ) + 0.9 * race;
			double Ur = Math.Sqrt( Math.Abs( Ur2 ) ) * JS.Sign( Ur2 );
			double vRud = vs + aLoc.y * ( model.rudderZ - com.z );
			double delta = steer * 0.6 + ( Math.Abs( Ur ) > 0.3 ? Math.Atan2( vRud, Math.Abs( Ur ) ) * JS.Sign( Ur ) : 0 );
			double d = clamp( delta, - 0.7, 0.7 );
			double lift = 0.5 * RHO * 0.12 * Math.Abs( Ur2 ) * rudderLift * Math.Sin( d ) * Math.Cos( d ) * propWet * JS.Sign( JS.Or( Ur2, 1 ) );
			addForceAt( _f.copy( side ).multiplyScalar( - lift ), rudW );
			// rudder drag (induced + form) slows the boat in a turn
			addForceAt( _f.copy( fwd ).multiplyScalar( - Math.Abs( lift * Math.Sin( d ) ) * 0.8 * JS.Sign( JS.Or( u, 1 ) ) ), rudW );

			// ---- running trim and rise: the bow lifts over the hump, then the hull runs at a few degrees with some dynamic lift (only
			// while the hull is in the water)
			double trim = ( 2.8 * sstep( u, 2.5, 4.8 ) - 0.8 * sstep( u, 4.8, 7.5 ) - 0.4 * sstep( u, 7.5, 10.5 ) ) * DEG;
			T.addScaledVector( side, pitchStiffness * trim * imm * - 1 );
			F.addScaledVector( up, m * GRAV * 0.15 * sstep( u, 3.5, 9 ) * imm );

			// ---- small extra angular damping (appendages, bilge), scaled by wetness
			double wd = 0.2 + wetD;
			// the keel's lift resists roll in proportion to speed (a boat underway rolls much less)
			_v.set( - aLoc.x * 50000, - aLoc.y * 2000, - aLoc.z * ( 4500 + 900 * au ) ).multiplyScalar( wd * forceScale ).applyQuaternion( quaternion );
			T.add( _v );

			// ---- mooring lines when docked and not driven
			if ( moored && ! driven )
			{
				var a = mooringAnchor;
				double k = 5500 * forceScale, c = 4200 * forceScale;
				double dx = a.x - position.x, dz = a.z - position.z;
				F.x += dx * k - velocity.x * c;
				F.z += dz * k - velocity.z * c;
				double dy = mooringHeading - getYaw();
				dy = Math.Atan2( Math.Sin( dy ), Math.Cos( dy ) );
				T.y += ( dy * 60000 - angular.y * 30000 ) * forceScale;
			}

			// ---- the anchor line: slack inside the rode, a stiff damped spring at the chock past it
			var A = anchor;
			if ( A.down )
			{
				var ch = toWorld( chock, _ch );
				double dx = A.x - ch.x, dz = A.z - ch.z, dd = JS.Hypot( dx, dz );
				if ( dd > A.rode && dd > 1e-6 )
				{
					double ux = dx / dd, uz = dz / dd;
					// the chock's own velocity (the hull's plus its turn about the centre of mass)
					var cv = _vp.crossVectors( angular, _r.copy( ch ).sub( comW ) ).add( velocity );
					double away = - ( cv.x * ux + cv.z * uz ); // m/s the line is lengthening
					double tn = Math.Min( ANCHOR.maxTension, ANCHOR.k * ( dd - A.rode ) + ANCHOR.c * Math.Max( 0, away ) );
					addForceAt( _f.set( ux * tn, 0, uz * tn ), ch );
					A.tension = tn;
				}
				else A.tension = 0;
			}

			// ---- grounding on terrain + contact with pier piles
			contacts( F, T, comW );

			// ---- integrate (semi-implicit Euler) with added mass / inertia in the boat frame
			var fl = _vl.copy( F ).applyQuaternion( invQ );
			fl.x /= m * ( 1 + addedMass.x * wetD );
			fl.y /= m * ( 1 + addedMass.y * wetD );
			fl.z /= m * ( 1 + addedMass.z * wetD );
			velocity.addScaledVector( fl.applyQuaternion( quaternion ), h );
			var tl = T.applyQuaternion( invQ );
			var I = inertia; var ai = addedInertia;
			tl.set( tl.x / ( I.x * ( 1 + ai.x * wetD ) ), tl.y / ( I.y * ( 1 + ai.y * wetD ) ), tl.z / ( I.z * ( 1 + ai.z * wetD ) ) );
			angular.addScaledVector( tl.applyQuaternion( quaternion ), h );
			// integrate position of COM, then derive origin
			comW.addScaledVector( velocity, h );
			var w2 = angular;
			double angle = w2.length() * h;
			if ( angle > 1e-8 )
			{
				_dq.setFromAxisAngle( _v.copy( w2 ).normalize(), angle );
				quaternion.premultiply( _dq ).normalize();
			}

			// origin = com - R * comLocal
			position.copy( comW ).sub( _v.copy( com ).applyQuaternion( quaternion ) );
		}

		public bool isFinite()
		{
			Func<Vector3, bool> ok = v => double.IsFinite( v.x ) && double.IsFinite( v.y ) && double.IsFinite( v.z );
			return ok( position ) && ok( velocity ) && ok( angular ) && double.IsFinite( quaternion.w );
		}

		// back to the berth, at rest (safety net if the integration ever blows up)
		public void reset()
		{
			position.copy( homeDock.position );
			quaternion.setFromAxisAngle( new Vector3( 0, 1, 0 ), homeDock.heading );
			velocity.set( 0, 0, 0 );
			angular.set( 0, 0, 0 );
			throttle = 0;
			steer = 0;
			rpm = 0;
			moored = true;
			mooringAnchor.copy( homeDock.position );
			mooringHeading = homeDock.heading;
			weighAnchor();
		}

		public double getYaw()
		{
			var f = forward( _g );
			return Math.Atan2( f.x, f.z );
		}

		public double sampleWaterAt( Vector3 p )
		{
			// nearest hull sample's water height (good enough for the prop / rudder)
			double best = 0, bd = double.PositiveInfinity;
			for ( int i = 0; i < nHull; i ++ )
			{
				toWorld( samples[ i ].p, _t );
				double d = ( _t.x - p.x ) * ( _t.x - p.x ) + ( _t.z - p.z ) * ( _t.z - p.z );
				if ( d < bd ) { bd = d; best = hasWater ? hEff[ i ] : 0; }
			}

			return best;
		}

		void contacts( Vector3 F, Vector3 T, Vector3 comW )
		{
			var pw = _c1; var vp = _c2; var f = _c3; var r = _c4;
			foreach ( var lp in contactPoints )
			{
				toWorld( lp, pw );
				double ground = terrainHeightAt( pw.x, pw.z );
				double pen = ground - pw.y;
				if ( pen > 0 )
				{
					vp.copy( angular ).cross( r.copy( pw ).sub( comW ) ).add( velocity );
					double fn = pen * 400000 - Math.Min( vp.y, 0 ) * 30000;
					f.set( - vp.x * 6000, Math.Max( fn, 0 ), - vp.z * 6000 );
					F.add( f );
					T.add( r.copy( pw ).sub( comW ).cross( f ) );
				}
			}

			// pier piles: keep the hull outline out of vertical cylinders / solid boxes near the waterline
			if ( colliders != null )
			{
				var tmp = _c5;
				foreach ( var lp in outline )
				{
					toWorld( lp, pw );
					tmp.copy( pw );
					tmp.y -= 0.9;
					if ( colliders.resolveCapsule( tmp, 0.25, 1.6, 0 ) )
					{
						vp.copy( angular ).cross( r.copy( pw ).sub( comW ) ).add( velocity );
						f.set( ( tmp.x - pw.x ) * 260000 - vp.x * 8000, 0, ( tmp.z - pw.z ) * 260000 - vp.z * 8000 );
						F.add( f );
						T.add( r.copy( pw ).sub( comW ).cross( f ) );
					}
				}
			}
		}

		public void apply()
		{
			view?.SetPose( position, quaternion );
			toWorld( _v.set( 0, 0, model.bowZ ?? 3.9 ), bowWorld );
			toWorld( _v.set( 0, 0, model.sternZ ?? - 3.8 ), sternWorld );
		}
	}
}
