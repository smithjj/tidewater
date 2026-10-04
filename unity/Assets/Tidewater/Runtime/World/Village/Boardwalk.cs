using System;
using System.Collections.Generic;
using Tidewater.Engine;
using static Tidewater.World.Village.Props;

// Port of src/world/village/Boardwalk.js: raised timber boardwalk following a smooth curve over the terrain.
// pts: world control points (x, z). Returns the sampled centerline (for layout checks).
namespace Tidewater.World.Village
{
	public struct FoundationCheck { public double x, y, z; }

	public sealed class WalkSample { public Vector3 p, tan; public double nx, nz, g, gLow, yaw; }

	public sealed class BoardwalkResult { public List<WalkSample> samples; public double[] deck; public double length, width; }

	public sealed class BoardwalkOpts { public double? width, lift, startY, lightEvery; }

	public sealed class BuildCtx
	{
		public Builder B; public TerrainData terrain; public Colliders colliders; public Rand rand; public List<LightSource> lights; public InstancedProps inst; public List<FoundationCheck> checks;
	}

	public static class Boardwalk
	{
		public static BoardwalkResult buildBoardwalk( BuildCtx ctx, double[][] pts, BoardwalkOpts opts = null )
		{
			opts = opts ?? new BoardwalkOpts();
			var B = ctx.B; var terrain = ctx.terrain; var colliders = ctx.colliders; var rand = ctx.rand; var lights = ctx.lights; var checks = ctx.checks;
			double width = opts.width ?? 1.8;
			double lift = opts.lift ?? 0.26;
			var cp = new List<Vector3>();
			foreach ( var p in pts ) cp.Add( new Vector3( p[ 0 ], 0, p[ 1 ] ) );
			var curve = new CatmullRomCurve3( cp, false, "centripetal" );
			double L = curve.getLength();
			double pitch = 0.2;
			int n = ( int ) Math.Floor( L / pitch );

			var S = new List<WalkSample>();
			for ( int i = 0; i <= n; i ++ )
			{
				double t = ( double ) i / n;
				var p = curve.getPointAt( t );
				var tan = curve.getTangentAt( t, new Vector3() );
				tan.y = 0;
				tan.normalize();
				double nx = - tan.z, nz = tan.x;
				double g = double.NegativeInfinity, gLow = double.PositiveInfinity;
				foreach ( double o in new[] { - width / 2 - 0.1, - width / 4, 0, width / 4, width / 2 + 0.1 } )
				{
					double h = terrain.HeightAt( p.x + nx * o, p.z + nz * o );
					g = Math.Max( g, h );
					gLow = Math.Min( gLow, h );
				}

				S.Add( new WalkSample { p = p, tan = tan, nx = nx, nz = nz, g = g, gLow = gLow, yaw = Math.Atan2( tan.x, tan.z ) } );
			}

			// deck profile: local max of the ground, smoothed, never closer than 12 cm to the ground
			var deck = new double[ n + 1 ];
			for ( int i = 0; i <= n; i ++ )
			{
				double m = double.NegativeInfinity;
				for ( int k = Math.Max( 0, i - 4 ); k <= Math.Min( n, i + 4 ); k ++ ) m = Math.Max( m, S[ k ].g );
				deck[ i ] = m + lift;
			}

			for ( int pass = 0; pass < 3; pass ++ )
			{
				var nd = ( double[] ) deck.Clone();
				for ( int i = 0; i <= n; i ++ )
				{
					double sum = 0; int c = 0;
					for ( int k = Math.Max( 0, i - 6 ); k <= Math.Min( n, i + 6 ); k ++ ) { sum += deck[ k ]; c ++; }
					nd[ i ] = Math.Max( sum / c, S[ i ].g + 0.12 );
				}

				deck = nd;
			}

			if ( opts.startY.HasValue )
			{
				for ( int i = 0; i <= Math.Min( n, 12 ); i ++ )
				{
					double k = i / 12.0;
					deck[ i ] = Math.Max( S[ i ].g + 0.1, opts.startY.Value * ( 1 - k ) + deck[ i ] * k );
				}
			}

			// planks
			for ( int i = 0; i <= n; i ++ )
			{
				var s = S[ i ];
				double a = deck[ Math.Max( 0, i - 1 ) ], b = deck[ Math.Min( n, i + 1 ) ];
				double slope = ( b - a ) / ( pitch * ( Math.Min( n, i + 1 ) - Math.Max( 0, i - 1 ) ) );
				bool newer = rand.chance( 0.05 );
				double bx = s.p.x + rand.range( - 0.02, 0.02 ) * s.nx;
				double by = deck[ i ] - 0.021 - rand.range( 0, 0.005 );
				double bz = s.p.z + rand.range( - 0.02, 0.02 ) * s.nz;
				double bw = width + rand.range( - 0.04, 0.03 );
				double ry = s.yaw + rand.range( - 0.01, 0.01 );
				double[] tint;
				if ( newer ) tint = new[] { 1.06, 1.0, 0.93 };
				else { double k = rand.range( 0.86, 1.08 ), w = rand.range( - 0.01, 0.04 ); tint = new[] { k * ( 1 + w ), k, k * ( 1 - w ) }; }
				double ws = rand.next();
				double weather = newer ? 0.35 : rand.range( 0.6, 1.0 );
				B.box( "wood", bx, by, bz, bw, 0.042, 0.182, new O { grain = 0, skip = 8, ry = ry, rx = - Math.Atan( slope ), tint = tint, data = WOOD( ws, weather, 0, 7 ) } );
			}

			// stringers + posts
			int seg = 6; // samples per stringer piece (1.2 m)
			for ( int i = 0; i < n; i += seg )
			{
				int j = Math.Min( n, i + seg );
				foreach ( double o in new[] { - width / 2 + 0.18, width / 2 - 0.18 } )
				{
					var a = S[ i ]; var b = S[ j ];
					B.beam( "wood", new[] { a.p.x + a.nx * o, deck[ i ] - 0.042 - 0.07, a.p.z + a.nz * o }, new[] { b.p.x + b.nx * o, deck[ j ] - 0.042 - 0.07, b.p.z + b.nz * o }, 0.08, 0.14, new O { extend = 0.06, data = WOOD( rand.next(), 0.85 ) } );
				}
			}

			for ( int i = 0; i <= n; i += 9 )
			{
				var s = S[ i ];
				foreach ( double o in new[] { - width / 2 + 0.18, width / 2 - 0.18 } )
				{
					double px = s.p.x + s.nx * o, pz = s.p.z + s.nz * o;
					double g = terrain.HeightAt( px, pz );
					double top = deck[ i ] - 0.18;
					if ( top - g > - 0.05 )
					{
						B.box( "wood", px, ( top + g - 0.3 ) / 2, pz, 0.11, top - g + 0.3, 0.11, new O { grain = 1, ry = s.yaw, data = WOOD( rand.next(), 0.9 ) } );
						checks.Add( new FoundationCheck { x = px, y = g - 0.3, z = pz } );
					}
				}
			}

			// edge kick boards for a finished look
			for ( int i = 0; i < n; i += seg )
			{
				int j = Math.Min( n, i + seg );
				foreach ( int sgn in new[] { - 1, 1 } )
				{
					double o = sgn * ( width / 2 + 0.01 );
					var a = S[ i ]; var b = S[ j ];
					B.beam( "wood", new[] { a.p.x + a.nx * o, deck[ i ] - 0.1, a.p.z + a.nz * o }, new[] { b.p.x + b.nx * o, deck[ j ] - 0.1, b.p.z + b.nz * o }, 0.03, 0.12, new O { extend = 0.03, data = WOOD( rand.next(), 0.8 ) } );
				}
			}

			// walkable colliders (1 m long pieces)
			int cs = 5;
			for ( int i = 0; i < n; i += cs )
			{
				int j = Math.Min( n, i + cs );
				var a = S[ i ]; var b = S[ j ];
				double top = double.NegativeInfinity, gl = double.PositiveInfinity;
				for ( int k = i; k <= j; k ++ ) { top = Math.Max( top, deck[ k ] ); gl = Math.Min( gl, S[ k ].gLow ); }
				double cx = ( a.p.x + b.p.x ) / 2, cz = ( a.p.z + b.p.z ) / 2;
				double len = JS.Hypot( b.p.x - a.p.x, b.p.z - a.p.z );
				double bot = gl - 0.3;
				colliders.addBox( new Vector3( cx, ( top + bot ) / 2, cz ), new Vector3( width / 2, ( top - bot ) / 2, len / 2 + 0.02 ), Math.Atan2( b.p.x - a.p.x, b.p.z - a.p.z ), true, true, "boardwalk" );
			}

			// path lights
			int every = ( int ) ( opts.lightEvery ?? 60 );
			int side = 1;
			for ( int i = ( int ) Math.Floor( every * 0.4 ); i < n - 10; i += every )
			{
				var s = S[ i ];
				double o = side * ( width / 2 + 0.2 );
				double px = s.p.x + s.nx * o, pz = s.p.z + s.nz * o;
				double g = terrain.HeightAt( px, pz );
				var w = pathLight( B, px, g - 0.25, pz, rand.next() );
				lights.Add( new LightSource { position = w, color = new Color( 1.0, 0.7, 0.4 ), intensity = 2.5, kind = "pathLight" } );
				colliders.addCylinder( px, pz, 0.1, g - 0.25, g + 0.95, "pathLight" );
				checks.Add( new FoundationCheck { x = px, y = g - 0.25, z = pz } );
				side = - side;
			}

			return new BoardwalkResult { samples = S, deck = deck, length = L, width = width };
		}
	}
}
