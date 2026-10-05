using System;
using System.Collections.Generic;
using Tidewater.Fx;
using UnityEngine;
using V3 = Tidewater.Engine.Vector3;

// The humpback's marks on the water surface (src/ocean/WhaleWater.js; the water shader reads them: Water.hlsl TWWhaleWater): churned white water where it breaks the
// surface and rolls, spreading and fading over ~15 s, and the smooth, flat "fluke print" slick it leaves where it dives. A handful of analytic patches in a small
// global array (x, z, radius, strength), strength > 0 foam, < 0 slick. Also the spray bursts of white water around the body as it breaks the surface.
// The patches and the bursts use random numbers the JS draws from Math.random: here a System.Random.
namespace Tidewater.World.Marine
{
	public sealed class WhaleWater
	{
		const int N = 6;
		sealed class Mark { public double x, z, r, age, life, peak; public bool foam; }

		static readonly int idMarks = Shader.PropertyToID( "_TWWhaleMarks" );
		readonly List<Mark> events = new List<Mark>();
		readonly Vector4[] marks = new Vector4[ N ];
		readonly System.Random rnd = new System.Random( 7 );
		readonly V3 _p = new V3(), _v = new V3();
		double foamTimer;
		bool slickDone;
		double breaches, splashes;

		double Rand() => rnd.NextDouble();

		public void Update( Whale whale, double dt, Spray spray )
		{
			var b = whale.brain;
			double d = b.water - b.position.y; // depth of the root below the surface
			bool breaking = b.state == "surface" && d < 1.6;
			if ( breaking )
			{
				// churn: a new patch every ~1.2 s at mid-body while the back is out, spreading behind
				foamTimer -= dt;
				if ( foamTimer <= 0 )
				{
					foamTimer = 1.2;
					whale.toWorld( _p.set( 0, whale.restY[ 8 ], whale.zHead - 5 ), _v );
					Push( new Mark { x = _v.x, z = _v.z, r = 5.5, life = 16, foam = true, peak = 0.9 } );
				}

				// white water erupting along the flanks at the waterline
				if ( spray != null && Rand() < dt * 30 )
				{
					double side = Rand() < 0.5 ? - 1 : 1;
					whale.toWorld( _p.set( side * 1.6, whale.restY[ 8 ], whale.zHead - 2 - Rand() * 8 ), _v );
					_v.y = b.water + 0.05;
					spray.Emit( U( _v ), new Vector3( ( float ) ( ( Rand() - 0.5 ) * 1.5 ), ( float ) ( 1.6 + Rand() * 1.6 ), ( float ) ( ( Rand() - 0.5 ) * 1.5 ) ), 10, 0.05f, 0, new SprayEmitOptions { spread = 1.2f, jitter = 0.4f, life = 1.4f } );
				}
			}

			// breach: white water where it bursts out, a huge splash, spray and foam where it falls back
			if ( b.breaches != breaches )
			{
				breaches = b.breaches;
				whale.toWorld( _p.set( 0, whale.restY[ 2 ], whale.zHead - 2 ), _v );
				Push( new Mark { x = _v.x, z = _v.z, r = 6, life = 14, foam = true, peak = 1 } );
				Burst( whale, spray, _v, 5, 7, 18 );
			}

			if ( b.splashes != splashes )
			{
				splashes = b.splashes;
				whale.toWorld( _p.set( 0, whale.restY[ 8 ], whale.zHead - 5 ), _v );
				Push( new Mark { x = _v.x, z = _v.z, r = 11, life = 20, foam = true, peak = 1 } );
				Push( new Mark { x = _v.x + 3, z = _v.z - 2, r = 7, life = 12, foam = true, peak = 0.9 } );
				Burst( whale, spray, _v, 9, 11, 26 );
			}

			// the fluke-up dive leaves a flat slick where the flukes went under
			if ( b.flukeUp > 0.6 && ! slickDone )
			{
				slickDone = true;
				whale.toWorld( _p.set( 0, whale.restY[ whale.restY.Length - 1 ], whale.notchZ ), _v );
				Push( new Mark { x = _v.x, z = _v.z, r = 6, life = 24, foam = false, peak = 1 } );
				Push( new Mark { x = _v.x, z = _v.z, r = 3.5, life = 7, foam = true, peak = 0.7 } );
			}

			if ( b.state != "surface" ) slickDone = false;

			// age the patches; write the array
			for ( int k = 0; k < N; k ++ )
			{
				if ( k >= events.Count ) { marks[ k ] = new Vector4( 0, 0, 1, 0 ); continue; }
				var e = events[ k ];
				e.age += dt;
				double f = Math.Max( 0, 1 - e.age / e.life );
				double grow = e.foam ? 1 + e.age * 0.06 : 1 + e.age * 0.02;
				marks[ k ] = new Vector4( ( float ) e.x, ( float ) e.z, ( float ) ( e.r * grow ), ( float ) ( ( e.foam ? 1 : - 1 ) * e.peak * f * Math.Min( 1, e.age * 3 ) ) );
			}

			events.RemoveAll( e => e.age >= e.life );
			Shader.SetGlobalVectorArray( idMarks, marks );
		}

		static Vector3 U( V3 v ) => new Vector3( ( float ) v.x, ( float ) v.y, ( float ) v.z );

		// a ring of spray sheets, drops and mist around p (radius r, launch speed up)
		void Burst( Whale whale, Spray spray, V3 p, double r, double up, int n )
		{
			if ( spray == null ) return;
			double water = whale.brain.water;
			for ( int k = 0; k < 9; k ++ )
			{
				double a = k / 9.0 * Math.PI * 2 + Rand() * 0.5;
				var q = new Vector3( ( float ) ( p.x + Math.Cos( a ) * r * ( 0.4 + Rand() * 0.6 ) ), ( float ) ( water + 0.2 ), ( float ) ( p.z + Math.Sin( a ) * r * ( 0.4 + Rand() * 0.6 ) ) );
				var vel = new Vector3( ( float ) ( Math.Cos( a ) * 2.5 ), ( float ) ( up * ( 0.6 + Rand() * 0.5 ) ), ( float ) ( Math.Sin( a ) * 2.5 ) );
				spray.Emit( q, vel, n, 0.3f, 3, new SprayEmitOptions { spread = 2.4f, jitter = 0.5f, life = 2.8f, sizeJitter = 0.8f } );
				spray.Emit( q, vel * 0.9f, n * 2, 0.025f, 0, new SprayEmitOptions { spread = 3, jitter = 0.4f, life = 2.4f } );
				if ( k % 3 == 0 ) spray.Emit( q, vel * 0.5f, n, 0.8f, 1, new SprayEmitOptions { spread = 2.2f, jitter = 0.6f, life = 5 } );
			}
		}

		void Push( Mark e )
		{
			events.Add( e );
			if ( events.Count > N ) events.RemoveAt( 0 );
		}

		// no whale (or none drawn): no marks
		public static void Clear() => Shader.SetGlobalVectorArray( idMarks, new Vector4[ N ] );
	}
}
