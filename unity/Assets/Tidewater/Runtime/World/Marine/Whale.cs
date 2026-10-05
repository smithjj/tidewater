using System;
using Newtonsoft.Json.Linq;
using Tidewater.Engine;
using Tidewater.Fx;
using Tidewater.Player;
using UnityEngine;
using V3 = Tidewater.Engine.Vector3;
using Q4 = Tidewater.Engine.Quaternion;

// Port of the CPU half of src/world/marine/Whale.js: the humpback (Megaptera novaeangliae), ~14.5 m.
//
// Rig: a chain of K spine frames from the snout to the fluke tips plus a joint per flipper, posed on the CPU every frame (WhaleBrain: route, surfacing, blows, fluke-up dive)
// and applied in the vertex shader (Shaders/Whale/WhaleSurface.hlsl: each vertex follows the frame at its rest axial position). The arrays the shader reads (uPos / uRot /
// uFlip) are the same Vector4 lists the JS packs every frame. The spray effects (the blow, the water streaming off the flukes, the flipper slap) and the level-of-detail /
// culling rules are here too; the meshes, the textures and the draw are WhaleView's. Everything in SIM space (x east, y up, z south).
namespace Tidewater.World.Marine
{
	public sealed class Whale
	{
		public const int K = 40;                          // spine frames
		static readonly double[] LOD_DIST = { 48, 170 };  // m: lod0 -> lod1 -> lod2
		const double MAX_DIST = 1800;

		static readonly Q4 _q = new Q4(), _q2 = new Q4();
		static readonly V3 _v = new V3(), _v2 = new V3(), _t = new V3(), _x = new V3( 1, 0, 0 );

		public readonly WhaleBrain brain;
		public Spray spray;
		public double notchZ, zHead, dz;
		public double bodyLength = 14.5; // m (the manifest's)
		public readonly double[] restZ = new double[ K ], restY = new double[ K ];
		public int rootIndex;
		// the shader's uniform arrays: this frame [0, K), last frame [K, 2K)
		public readonly UnityEngine.Vector4[] uPos = new UnityEngine.Vector4[ 2 * K ], uRot = new UnityEngine.Vector4[ 2 * K ], uFlip = new UnityEngine.Vector4[ 4 ];
		readonly V3[] pos = new V3[ K ];
		readonly Q4[] rot = new Q4[ K ];
		public V3 pecL, pecR, blowLocal;
		bool hasPrev;
		public double heightMin, heightRange;
		public int lod = -1;                               // the level drawn (-1: not drawn)
		int lodCur;
		double blowAcc, dropAcc, slapsSeen;
		readonly System.Random rnd = new System.Random( 1 );

		public Whale( WhaleBrain brain, JObject manifest )
		{
			this.brain = brain;
			SetupRig( manifest );
		}

		static V3 Vec( JToken a ) => new V3( ( double ) a[ 0 ], ( double ) a[ 1 ], ( double ) a[ 2 ] );

		void SetupRig( JObject m )
		{
			if ( m[ "length" ] != null ) bodyLength = ( double ) m[ "length" ];
			zHead = ( double ) m[ "snoutZ" ] + 0.15;
			notchZ = ( double ) m[ "notchZ" ];
			double zTail = notchZ - 1.2; // past the fluke tips
			dz = ( zHead - zTail ) / ( K - 1 );
			var cz = ( ( JArray ) m[ "centerline" ][ "z" ] ).ToObject<double[]>();
			var cy = ( ( JArray ) m[ "centerline" ][ "y" ] ).ToObject<double[]>();   // z descending
			for ( int k = 0; k < K; k ++ )
			{
				double z = zHead - k * dz;
				restZ[ k ] = z; restY[ k ] = YcAt( cz, cy, z );
			}

			rootIndex = ( int ) Math.Floor( zHead / dz + 0.5 ); // frame nearest to z = 0
			for ( int k = 0; k < 2 * K; k ++ ) { uPos[ k ] = UnityEngine.Vector4.zero; uRot[ k ] = new UnityEngine.Vector4( 0, 0, 0, 1 ); }
			for ( int i = 0; i < 4; i ++ ) uFlip[ i ] = new UnityEngine.Vector4( 1, 0, 0, 0 );
			for ( int k = 0; k < K; k ++ ) { pos[ k ] = new V3(); rot[ k ] = new Q4(); }
			pecL = Vec( m[ "pectoral" ][ "left" ][ "origin" ] ); pecR = Vec( m[ "pectoral" ][ "right" ][ "origin" ] );
			blowLocal = Vec( m[ "blowhole" ] );
			var hr = m[ "textures" ] != null ? m[ "textures" ][ "heightRange" ] : null;
			if ( hr != null ) { heightMin = ( double ) hr[ 0 ]; heightRange = ( double ) hr[ 1 ] - ( double ) hr[ 0 ]; }
		}

		static double YcAt( double[] cz, double[] cy, double z )
		{
			if ( z >= cz[ 0 ] ) return cy[ 0 ];
			for ( int i = 1; i < cz.Length; i ++ ) if ( z >= cz[ i ] ) return cy[ i - 1 ] + ( cy[ i ] - cy[ i - 1 ] ) * ( z - cz[ i - 1 ] ) / ( cz[ i ] - cz[ i - 1 ] );
			return cy[ cy.Length - 1 ];
		}

		// Pose the spine frames from the brain's state (root pose, path history, stroke, arch).
		public void Pose()
		{
			var b = brain;
			int ri = rootIndex;
			var P = uPos; var R = uRot;
			// last frame's pose -> previous slots
			for ( int k = 0; k < K; k ++ ) { P[ K + k ] = P[ k ]; R[ K + k ] = R[ k ]; }

			var Qr = b.quaternion;
			for ( int k = 0; k < K; k ++ )
			{
				double z = restZ[ k ];
				var q = rot[ k ];
				if ( z >= 0 )
				{
					// head and chest: rigid with the root plus a little recoil against the stroke
					double recoil = - b.strokeAmp * 0.1 * Math.Sin( b.strokePhase + 0.6 ) * Math.Min( 1, z / 5 );
					q.copy( Qr ).multiply( _q.setFromAxisAngle( _x, recoil + b.headPitch * Math.Min( 1, z / 4 ) ) );
				}
				else
				{
					double d = - z;
					// follow the path (the body bends along the route: turns, rolling over at the surface)
					b.pathRotation( d, _q2 );
					q.copy( Qr ).slerp( _q2, b.follow );
					// stroke: a travelling wave of pitch, growing toward the flukes
					const double L = 9.6;
					double u = Math.Min( d / L, 1.15 );
					double env = u * u * 1.1 + 0.05 * u;
					double ph = b.strokePhase - d * 0.52;
					double beta = b.strokeAmp * env * ( Math.Sin( ph ) + 0.22 * Math.Sin( 2 * ph ) );
					// the flukes lead the tail stock (angle of attack)
					if ( d > 7.6 ) beta += b.strokeAmp * 2.0 * Math.Sin( ph + 1.35 ) * Math.Min( 1, ( d - 7.6 ) / 1.2 );
					// arch: the back humps (both ends down) when rolling over and before the fluke-up dive
					beta += b.arch * 3.0 * Math.Min( d / 6, 1.0 );
					q.multiply( _q.setFromAxisAngle( _x, beta ) );
				}
			}

			// positions: integrate the chain outward from the root
			pos[ ri ].copy( b.position ).add( _v.set( 0, restY[ ri ], restZ[ ri ] ).applyQuaternion( rot[ ri ] ) );
			for ( int k = ri - 1; k >= 0; k -- )
			{
				_q.copy( rot[ k ] ).slerp( rot[ k + 1 ], 0.5 );
				_v.set( 0, restY[ k ] - restY[ k + 1 ], restZ[ k ] - restZ[ k + 1 ] ).applyQuaternion( _q );
				pos[ k ].copy( pos[ k + 1 ] ).add( _v );
			}

			for ( int k = ri + 1; k < K; k ++ )
			{
				_q.copy( rot[ k ] ).slerp( rot[ k - 1 ], 0.5 );
				_v.set( 0, restY[ k ] - restY[ k - 1 ], restZ[ k ] - restZ[ k - 1 ] ).applyQuaternion( _q );
				pos[ k ].copy( pos[ k - 1 ] ).add( _v );
			}

			// frame k maps rest point (x, y, z) -> pos_k + rot_k * (x, y - yc_k, z - z_k)
			for ( int k = 0; k < K; k ++ )
			{
				P[ k ] = new UnityEngine.Vector4( ( float ) pos[ k ].x, ( float ) pos[ k ].y, ( float ) pos[ k ].z, ( float ) restY[ k ] );
				var q = rot[ k ];
				// keep the quaternion hemisphere continuous along the chain (shader nlerp)
				if ( k > 0 && q.x * R[ k - 1 ].x + q.y * R[ k - 1 ].y + q.z * R[ k - 1 ].z + q.w * R[ k - 1 ].w < 0 ) R[ k ] = new UnityEngine.Vector4( ( float ) - q.x, ( float ) - q.y, ( float ) - q.z, ( float ) - q.w );
				else R[ k ] = new UnityEngine.Vector4( ( float ) q.x, ( float ) q.y, ( float ) q.z, ( float ) q.w );
			}

			// flippers: axis-angle relative to the bind pose
			var F = uFlip;
			F[ 2 ] = F[ 0 ]; F[ 3 ] = F[ 1 ];
			for ( int s = 0; s < 2; s ++ )
			{
				b.flipperRotation( s, _q );
				double ang = 2 * Math.Acos( Math.Min( 1, Math.Abs( _q.w ) ) );
				double sgn = _q.w < 0 ? -1 : 1;
				double sn = Math.Sqrt( Math.Max( 1e-12, 1 - _q.w * _q.w ) );
				F[ s ] = new UnityEngine.Vector4( ( float ) ( _q.x / sn * sgn ), ( float ) ( _q.y / sn * sgn ), ( float ) ( _q.z / sn * sgn ), ( float ) ang );
				if ( ang < 1e-5 ) F[ s ] = new UnityEngine.Vector4( 1, 0, 0, 0 );
			}

			if ( ! hasPrev )
			{
				for ( int k = 0; k < K; k ++ ) { P[ K + k ] = P[ k ]; R[ K + k ] = R[ k ]; }
				F[ 2 ] = F[ 0 ]; F[ 3 ] = F[ 1 ];
				hasPrev = true;
			}
		}

		// the lowest level of detail's skin in rest model coordinates (xyz triples, unit normals): where the escort's remoras find the belly
		public float[] skinPos, skinNrm;
		// the head frame's turn (the way the escort's `ahead` runs)
		public Q4 headRot => rot[ 0 ];

		// the spine's rest height at model z
		public double restYAt( double z )
		{
			double fi = MathUtils.clamp( ( zHead - z ) / dz, 0, K - 1.001 );
			int i = ( int ) Math.Floor( fi ); double t = fi - i;
			return restY[ i ] + ( restY[ i + 1 ] - restY[ i ] ) * t;
		}

		// world position of a rest-frame point (CPU copy of the vertex shader)
		public V3 toWorld( V3 local, V3 o )
		{
			double fi = MathUtils.clamp( ( zHead - local.z ) / dz, 0, K - 1.001 );
			int i = ( int ) Math.Floor( fi ); double t = fi - i;
			double yc = restY[ i ] + ( restY[ i + 1 ] - restY[ i ] ) * t;
			_q.copy( rot[ i ] ).slerp( rot[ i + 1 ], t );
			o.set( local.x, local.y - yc, 0 ).applyQuaternion( _q );
			_t.copy( pos[ i ] ).lerp( pos[ i + 1 ], t );
			return o.add( _t );
		}

		// once a frame: the behaviour, the pose, the level of detail and the culling. camera: the camera's position (sim space), or null (the nearest level, drawn)
		public void Update( double dt, V3 camera )
		{
			var b = brain;
			b.update( dt );
			Pose();

			// level of detail and culling (the posed mesh lives in world space)
			int l = 0; bool visible = true;
			if ( camera != null )
			{
				double d = camera.distanceTo( b.position );
				bool camUnder = camera.y < b.water;
				// hysteresis: no popping back and forth at the switch distances
				l = lodCur;
				while ( l < 2 && d > LOD_DIST[ l ] * 1.08 ) l ++;
				while ( l > 0 && d < LOD_DIST[ l - 1 ] * 0.92 ) l --;
				lodCur = l;
				if ( d > MAX_DIST ) visible = false;
				// lost in the blue: underwater visibility is a few tens of metres
				if ( camUnder && d > 120 ) visible = false;
				// from above the water a deep whale is invisible beyond a short distance
				if ( ! camUnder && b.backDepth > 7 && d > 70 ) visible = false;
			}

			lod = visible ? l : -1;
			if ( dt > 0 ) Effects( dt );
		}

		double Rand() => rnd.NextDouble();

		static UnityEngine.Vector3 U( V3 v ) => new UnityEngine.Vector3( ( float ) v.x, ( float ) v.y, ( float ) v.z );

		// blow spout and water streaming off the flukes (Spray particles)
		void Effects( double dt )
		{
			var b = brain;
			if ( spray == null ) return;
			// flipper slap: a sheet of spray where the flipper comes down on the water
			if ( b.slaps != slapsSeen )
			{
				slapsSeen = b.slaps;
				int side = b.slap != null ? b.slap.side : 0;
				var r = side == 0 ? pecL : pecR;
				var p = toWorld( _v.set( r.x + ( side == 0 ? 3.2 : -3.2 ), r.y, r.z - 1.2 ), _v2 );
				p.y = b.water + 0.1;
				for ( int k = 0; k < 4; k ++ )
				{
					var vel = new UnityEngine.Vector3( ( float ) ( ( Rand() - 0.5 ) * 3 ), ( float ) ( 4.5 + Rand() * 3 ), ( float ) ( ( Rand() - 0.5 ) * 3 ) );
					spray.Emit( U( p ), vel, 18, 0.25f, SprayKind.SPRAY, new SprayEmitOptions { spread = 2.4f, jitter = 0.6f, life = 2.2f, sizeJitter = 0.8f } );
					spray.Emit( U( p ), vel, 30, 0.022f, SprayKind.DROPLET, new SprayEmitOptions { spread = 3, jitter = 0.5f, life = 2 } );
				}
			}

			if ( b.blow > 0 )
			{
				var p = toWorld( blowLocal, _v2 );
				if ( p.y > b.water - 0.3 )
				{
					p.y = Math.Max( p.y, b.water ) + 0.15;
					// the column: dense cloud of drops, a haze that drifts downwind, and heavier drops
					double fx = Math.Sin( b.yaw ), fz = Math.Cos( b.yaw );
					double vUp = 7.5 + 2.5 * b.blow;
					var vel = new UnityEngine.Vector3( ( float ) ( fx * 0.8 ), ( float ) vUp, ( float ) ( fz * 0.8 ) );
					blowAcc += dt * 60 * b.blow;
					int n = ( int ) Math.Floor( blowAcc );
					blowAcc -= n;
					if ( n > 0 )
					{
						spray.Emit( U( p ), vel, 7 * n, 0.3f, SprayKind.SPRAY, new SprayEmitOptions { spread = 3.2f, jitter = 0.25f, life = 2.2f, sizeJitter = 0.8f } );
						spray.Emit( U( p ), vel * 0.75f, 10 * n, 0.7f, SprayKind.MIST, new SprayEmitOptions { spread = 2.6f, jitter = 0.35f, life = 5.5f, sizeJitter = 0.6f } );
						spray.Emit( U( p ), vel * 0.9f, 12 * n, 0.018f, SprayKind.DROPLET, new SprayEmitOptions { spread = 3.5f, jitter = 0.2f, life = 2.2f } );
					}
				}
			}

			// flukes lifting clear: sheets of water pour off the trailing edge
			if ( b.flukeUp > 0.05 )
			{
				dropAcc += dt * 60;
				int n = ( int ) Math.Floor( dropAcc );
				dropAcc -= n;
				if ( n > 0 )
				{
					foreach ( double x in new[] { -1.9, -1.2, -0.5, 0.5, 1.2, 1.9 } )
					{
						double zte = notchZ - 0.1 - 0.35 * Math.Pow( Math.Abs( x ) / 2.2, 2 );
						var p = toWorld( _v.set( x, restY[ K - 1 ], zte ), _v2 );
						if ( p.y < b.water + 0.2 ) continue;
						spray.Emit( U( p ), new UnityEngine.Vector3( 0, -0.5f, 0 ), 5 * n, 0.02f, SprayKind.DROPLET, new SprayEmitOptions { spread = 0.5f, jitter = 0.12f, life = 1.6f } );
					}
				}
			}
		}
	}
}
