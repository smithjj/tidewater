using System;
using Tidewater.Engine;
using Tidewater.Fx;
using Tidewater.World.Boat;
using JMath = Tidewater.Engine.MathUtils;

// Port of src/player/BoatSpray.js: spray thrown off the hull, after the contact model of threejs-water-pro (SprayContacts / SpraySystem),
// adapted to this boat's lines and to the shared GPU particle pool (Fx/Spray.cs).
//
//  * contacts: the forebody waterline on each side, from the stem aft to the shoulder, in a few segments. Each knows its outward normal,
//    its velocity (rigid body: v + w x r) and how deep it sits; only segments the water surface actually crosses throw spray
//  * two sources per segment: velocity-driven (the face pushing water aside: the normal speed max( 0, v . n )) and impact-driven (the
//    rate the segment is being immersed: the bow dropping onto a wave or a wave running up it). response = min( 30, max( 0, speed -
//    threshold ) * scale ), demand = length x response x intensity, emission ~ demand
//  * launch, what is thrown and the collision with the hull and wheelhouse: see the JS (the particles' motion, lighting and the water
//    surface are the pool's)
//
// Also: slams (the bow dropping hard into a head sea) for the boat's onSlam hook (audio), and droplets kicked up by the propeller race
// when it runs near the surface. Everything here is in sim coordinates (the pool takes sim space) and double precision like the JS.
namespace Tidewater.Player
{
	public sealed class BoatSpray
	{
		// A spray source (after SpraySource in threejs-water-pro): the driving speed (m/s) above a threshold, scaled and bounded, drives
		// both how much is thrown and how fast.
		sealed class Source
		{
			public double speedThreshold, velocityScale, intensity, lifetime;
			public double response( double speed ) => Math.Min( 30, Math.Max( 0, speed - speedThreshold ) * velocityScale );
		}

		sealed class Contact
		{
			public double side; public Vector3 a, b, mid; public double length; public Vector3 normal;
			public double depth, rate; public bool primed;
			public double[] carry = new double[ 5 ]; // fractional particles: drops, sheet, ligaments, white water, mist
			public double wet, hw, rV, rI, demandV, demandI, nEmit;
			public Vector3 vel = new Vector3(), n = new Vector3();
		}

		sealed class Side { public Contact best; public double n; }

		const int SEGMENTS = 3;        // contact segments per side, stem -> shoulder
		const double EMIT_RATE = 300;  // drops per unit of demand (m of waterline x m/s of response) per second
		const double MAX_PER_FRAME = 150; // particle cap per frame for the bow (both sides; the pool's CPU ring is 8192)
		const int FREE_REQUESTS = 8;   // emit requests of the shared pool left to other emitters each frame
		const double GRAVITY = 9.81;

		static readonly Vector3 _a = new Vector3(), _b = new Vector3(), _m = new Vector3(), _v = new Vector3(), _w = new Vector3(), _r = new Vector3(),
			_fwd = new Vector3(), _la = new Vector3(), _one = new Vector3( 1, 1, 1 );
		static readonly Matrix4 _M = new Matrix4();
		static readonly SprayEmitOptions _opts = new SprayEmitOptions();

		public readonly BoatController boat;
		readonly HullLines lines;
		readonly Spray spray;
		readonly Source velocitySource = new Source { speedThreshold = 0.4, velocityScale = 1.6, intensity = 1, lifetime = 0.8 };
		readonly Source impactSource = new Source { speedThreshold = 0.6, velocityScale = 1.2, intensity = 3, lifetime = 1.4 };
		readonly System.Collections.Generic.List<Contact> contacts = new System.Collections.Generic.List<Contact>();
		readonly Side[] side = { new Side(), new Side() };

		// the collision shape handed to the pool, and kept for the outline of the spray's launch points
		double zAft, zShoulder, zStem, halfBeam, wlShoulder, wlStem, wlHalfBeam, ySheerAft, ySheerStem;
		readonly Vector3 stern;
		double washCarry, slamCooldown, burst;
		public int statParticles, statRequests;
		public double statDemandV, statDemandI;

		public BoatSpray( BoatController boat, BoatModel model, Spray spray )
		{
			this.boat = boat; this.spray = spray; lines = model.lines;
			double zStem = lines.wlEnd - 0.02;
			double maxHB = 0;
			for ( double z = lines.wlStart; z <= lines.wlEnd; z += 0.05 ) maxHB = Math.Max( maxHB, lines.halfBeamAt( z ) );
			double zSh = zStem;
			while ( zSh > lines.wlStart && lines.halfBeamAt( zSh ) < 0.92 * maxHB ) zSh -= 0.05;
			foreach ( double s in new double[] { 1, -1 } )
			{
				var pts = new Vector3[ SEGMENTS + 1 ];
				for ( int i = 0; i <= SEGMENTS; i ++ )
				{
					// stations bunched toward the stem, where the entrance is sharpest
					double f = Math.Pow( ( double ) i / SEGMENTS, 1.3 );
					double z = zStem + ( zSh - zStem ) * f;
					pts[ i ] = new Vector3( s * ( lines.halfBeamAt( z ) + 0.03 ), 0.05, z );
				}

				for ( int i = 0; i < SEGMENTS; i ++ )
				{
					var a = pts[ i ]; var b = pts[ i + 1 ];
					var t = _v.subVectors( b, a ); t.y = 0; t.normalize();
					contacts.Add( new Contact
					{
						side = s, a = a, b = b, mid = a.clone().add( b ).multiplyScalar( 0.5 ),
						length = a.distanceTo( b ),
						normal = new Vector3( - s * t.z, 0, s * t.x ), // outward (and forward)
					} );
				}
			}

			// collision shape for the spray: hull outline at the waterline and at the sheer, the wheelhouse box
			double hbTop = 0;
			for ( int i = 0; i <= 200; i ++ ) hbTop = Math.Max( hbTop, lines.sheerX( i / 200.0 ) );
			double zShTop = lines.zBow;
			while ( zShTop > lines.zAft && lines.sheerX( lines.tAtSheerZ( zShTop ) ) < 0.95 * hbTop ) zShTop -= 0.05;
			double zShWL = zStem;
			while ( zShWL > lines.wlStart && lines.halfBeamAt( zShWL ) < 0.95 * maxHB ) zShWL -= 0.05;
			var box = new Box3();
			foreach ( var c in model.colliders ?? new System.Collections.Generic.List<BoatCollider>() )
			{
				if ( c.tag != "houseWall" && c.tag != "roof" ) continue;
				box.expandByPoint( _v.copy( c.center ).sub( c.half ) );
				box.expandByPoint( _v.copy( c.center ).add( c.half ) );
			}

			if ( box.min.x > box.max.x ) { box.min.set( 0, - 10, 0 ); box.max.set( 0, - 10, 0 ); }
			zAft = lines.zAft; zShoulder = zShTop; this.zStem = lines.zBow; halfBeam = hbTop + 0.02;
			wlShoulder = zShWL; wlStem = lines.wlEnd; wlHalfBeam = maxHB + 0.01;
			ySheerAft = lines.sheerY( 0 ) + 0.04; ySheerStem = lines.sheerY( 1 ) + 0.04;
			spray.SetBodyShape( ( float ) zAft, ( float ) zShoulder, ( float ) this.zStem, ( float ) halfBeam, ( float ) wlShoulder, ( float ) wlStem, ( float ) wlHalfBeam,
				( float ) ySheerAft, ( float ) ySheerStem, - 0.8f, F( box.min ), F( box.max ) );
			stern = new Vector3( 0, 0.05, lines.wlStart + 0.1 );
		}

		static UnityEngine.Vector3 F( Vector3 v ) => new UnityEngine.Vector3( ( float ) v.x, ( float ) v.y, ( float ) v.z );

		// half breadth of the spray collision outline at boat-frame z and height y (as Spray._collideBody)
		double _halfBeam( double z, double y )
		{
			double sheer = JMath.lerp( ySheerAft, ySheerStem, JMath.clamp( ( z - zAft ) / ( zStem - zAft ), 0, 1 ) );
			double f = JMath.clamp( y / Math.Max( sheer, 0.1 ), 0, 1 );
			double zSh = JMath.lerp( wlShoulder, zShoulder, f ), zSt = JMath.lerp( wlStem, zStem, f ), HB = JMath.lerp( wlHalfBeam, halfBeam, f );
			if ( z >= zSt ) return 0;
			double e = JMath.clamp( ( z - zSh ) / Math.Max( zSt - zSh, 0.01 ), 0, 1 );
			return HB * Math.Sqrt( Math.Max( 1 - e * e, 0.02 ) );
		}

		// emit the whole particles of a fractional count (per contact and kind), leaving the shared pool its free requests
		void _emit( Contact c, int k, double count, float kind, double size, double life, double spread, double jitter, Vector3 vel )
		{
			c.carry[ k ] += count;
			int m = ( int ) Math.Floor( c.carry[ k ] );
			if ( m <= 0 ) return;
			c.carry[ k ] -= m;
			if ( spray.RequestCount > 32 - FREE_REQUESTS ) return;
			_opts.to = F( _b );
			_opts.spread = ( float ) spread;
			_opts.jitter = ( float ) jitter;
			_opts.life = ( float ) life;
			_opts.sizeJitter = 0.6f;
			spray.Emit( F( _a ), F( vel ), m, ( float ) size, kind, _opts );
			statParticles += m;
			statRequests ++;
		}

		public void update( double dt )
		{
			var b = boat;
			statParticles = statRequests = 0;
			var q = b.quaternion;
			_M.compose( b.position, q, _one );
			spray.SetBody( ToUnity( _M ), F( b.velocity ) );
			if ( dt <= 0 || ! b.hasWater ) return;
			dt = Math.Min( dt, 1.0 / 20 );
			b.forward( _fwd );
			double speed = Math.Max( b.velocity.dot( _fwd ), 0 );
			var VS = velocitySource; var IS = impactSource;
			slamCooldown = Math.Max( 0, slamCooldown - dt );

			// ---- contacts: normal speed, immersion rate, demand
			double total = 0, slam = 0;
			double dV = 0, dI = 0;
			foreach ( var c in contacts )
			{
				b.toWorld( c.mid, _m );
				double hw = b.sampleWaterAt( _m );
				double depth = hw - _m.y; // + = the design waterline is under water
				double rate = c.primed ? ( depth - c.depth ) / dt : 0;
				c.rate += ( rate - c.rate ) * Math.Min( 1, dt / 0.03 ); // query noise
				c.depth = depth;
				c.primed = true;
				// the surface crosses this part of the hull (bow out of the water: dry; buried to the sheer: no sheet)
				double wet = JMath.smoothstep( depth, - 0.35, - 0.08 ) * ( 1 - JMath.smoothstep( depth, 0.7, 1.1 ) );
				c.wet = wet;
				// point velocity and the horizontal outward normal
				_r.subVectors( _m, b.position );
				_w.copy( b.angular ).cross( _r ).add( b.velocity );
				c.vel.copy( _w );
				c.n.copy( c.normal ).applyQuaternion( q ); c.n.y = 0; c.n.normalize();
				c.hw = hw;
				double vn = Math.Max( 0, _w.x * c.n.x + _w.z * c.n.z );
				c.rV = VS.response( vn );
				c.rI = IS.response( c.rate );
				c.demandV = c.length * c.rV * VS.intensity * wet;
				c.demandI = c.length * c.rI * IS.intensity * wet * ( 0.15 + 0.85 * JMath.smoothstep( speed, 2, 6 ) );
				dV += c.demandV; dI += c.demandI;
				total += c.demandV + c.demandI;
				// slam: the stem dropping hard into the water in a head sea
				if ( c.a.z == contacts[ 0 ].a.z && wet > 0.3 && speed > 2.5 ) slam = Math.Max( slam, Math.Min( ( c.rate - 1.8 ) * 0.4 + speed * 0.02, 1 ) );
			}

			statDemandV = dV; statDemandI = dI;
			if ( slam > 0 && slamCooldown == 0 )
			{
				slamCooldown = 0.4;
				if ( b.onSlam != null ) b.onSlam( slam );
			}

			// ---- emission, bounded per frame; a slam throws a burst
			burst = Math.Max( burst * Math.Exp( - dt / 0.15 ), slam );
			double burstV = burst;
			double boost = 1 + 2.5 * burstV;
			double budget = Math.Min( 1, MAX_PER_FRAME / Math.Max( 1e-6, total * EMIT_RATE * dt * boost * 1.3 ) );
			double making = JMath.smoothstep( speed, 3, 6 );
			// per side: the segment that throws most also throws the sheet, ligaments, white water and mist
			side[ 0 ].best = side[ 1 ].best = null;
			side[ 0 ].n = side[ 1 ].n = 0;
			foreach ( var c in contacts )
			{
				double d = c.demandV + c.demandI;
				var sd = side[ c.side > 0 ? 0 : 1 ];
				c.nEmit = d * EMIT_RATE * dt * budget * boost;
				sd.n += c.nEmit;
				if ( d > 1e-4 && ( sd.best == null || d > sd.best.demandV + sd.best.demandI ) ) sd.best = c;
			}

			foreach ( var c in contacts )
			{
				if ( c.demandV + c.demandI <= 1e-4 ) continue;
				double n = c.nEmit;
				double imp = c.demandI / ( c.demandV + c.demandI ); // impact share
				double r = c.rV * ( 1 - imp ) + c.rI * imp;
				// launch: tangential hull velocity (part of it: the sheet streams aft past the hull), outward and up (a slam throws it
				// higher and wider)
				double vn = c.vel.x * c.n.x + c.vel.z * c.n.z;
				_v.copy( c.vel ).addScaledVector( c.n, - vn ).multiplyScalar( 0.6 );
				_v.y = c.vel.y * 0.3;
				double up = c.rV * 0.55 * ( 1 - imp ) + c.rI * 1.15 * imp + burstV * 2.5;
				_v.addScaledVector( c.n, 0.8 * r + 0.4 + burstV * 1.0 );
				_v.y += up;
				// the sheet leaves from where the surface meets the hull, raised by the stagnation rise of the flow against the face
				double rise = Math.Min( 0.5, ( vn * vn ) / ( 2 * GRAVITY ) * 0.5 );
				// just outside the collision outline at that height (the spray collides with it)
				double ya = Math.Min( Math.Max( c.hw + rise - b.position.y, - 0.3 ), 0.9 );
				double yb = Math.Min( Math.Max( c.hw + rise * 0.6 - b.position.y, - 0.3 ), 0.9 );
				b.toWorld( _la.set( c.side * ( _halfBeam( c.a.z, ya ) + 0.05 ), ya, c.a.z ), _a );
				b.toWorld( _la.set( c.side * ( _halfBeam( c.b.z, yb ) + 0.05 ), yb, c.b.z ), _b );
				_a.y = c.hw + rise;
				_b.y = c.hw + rise * 0.6;
				double spread = ( 0.3 * JS.Hypot( 0.65 * r, up ) + 0.15 ) * ( 1 + burstV );
				double life = VS.lifetime * ( 1 - imp ) + IS.lifetime * imp;
				// drops (3-7 mm) from every wetted segment
				_emit( c, 0, n, SprayKind.DROPLET, 0.005 + 0.0006 * r, life, spread, 0.05, _v );
				var sd = side[ c.side > 0 ? 0 : 1 ];
				if ( sd.best != c ) continue;
				double N = sd.n;
				// a few fragments of the clear sheet at the root (they tear into strands and drop clusters)
				_emit( c, 1, N * 0.025 * making * ( 1 - burstV * 0.5 ), SprayKind.SHEET, 0.12 + 0.02 * r, 0.45, spread * 0.5, 0.06, _v );
				// ligaments torn off the sheet: the readable blobs of water
				_emit( c, 2, N * 0.3 * JMath.smoothstep( r, 1.0, 5 ), SprayKind.LIGAMENT, 0.011 + 0.0012 * r, life, spread, 0.05, _v );
				// white water only on impacts: torn white sheets
				_emit( c, 3, N * ( 0.06 * imp * JMath.smoothstep( c.rI, 1.5, 5 ) + 0.1 * burstV ) * making, SprayKind.SPRAY, 0.04 + 0.004 * r + 0.03 * burstV, 0.6 + 0.4 * imp, spread, 0.12, _v );
				// a fine, faint mist on impacts that the relative wind carries aft over the boat
				_emit( c, 4, N * ( 0.04 * imp + 0.15 * burstV ) * making, SprayKind.MIST, 0.3 + 0.2 * burstV, 2.0, spread * 0.5, 0.15, _w.copy( _v ).multiplyScalar( 0.6 ) );
			}

			// ---- propeller race: drops kicked up when the churn is near the surface (pro: activity sqrt( thrust / 1000 ) x e^( -depth / r ))
			b.toWorld( b.model.propeller, _m );
			double propDepth = b.sampleWaterAt( _m ) - _m.y;
			double activity = b.driven && propDepth > - 0.2 ? Math.Sqrt( Math.Abs( b.thrust ) / 1000 ) * Math.Exp( - Math.Max( propDepth, 0 ) / 0.45 ) : 0;
			if ( activity > 0.05 )
			{
				b.toWorld( stern, _a );
				if ( b.sampleWaterAt( _a ) - _a.y > - 0.3 )
				{
					washCarry += activity * 45 * dt;
					int m = ( int ) Math.Floor( washCarry );
					washCarry -= m;
					double dir = JS.Or( JS.Sign( b.thrust != 0 ? b.thrust : b.throttle ), 1 );
					_v.copy( b.velocity ).multiplyScalar( 0.6 ).addScaledVector( _fwd, - ( 0.8 + activity * 0.6 ) * dir );
					_v.y = 0.5 + activity * 0.5;
					if ( m > 0 && spray.RequestCount <= 32 - FREE_REQUESTS )
					{
						_opts.to = null; _opts.spread = 0.8f; _opts.jitter = 0.35f; _opts.life = 0.8f; _opts.sizeJitter = 0.5f;
						spray.Emit( F( _a ), F( _v ), m, 0.005f, SprayKind.DROPLET, _opts );
						statParticles += m;
						statRequests ++;
					}
				}
			}
		}

		static UnityEngine.Matrix4x4 ToUnity( Matrix4 m )
		{
			var e = m.elements; var u = new UnityEngine.Matrix4x4();
			for ( int i = 0; i < 16; i ++ ) u[ i ] = ( float ) e[ i ]; // both column-major
			return u;
		}
	}
}
