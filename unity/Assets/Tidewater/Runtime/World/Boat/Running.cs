using System;
using System.Collections.Generic;
using Tidewater.Engine;
using static Tidewater.World.Boat.HullBuilder;

// Port of src/world/boat/Running.js: the propeller and the rudder.
namespace Tidewater.World.Boat
{
	// Propeller hub centre (boat frame) and rudder stock pivot.
	public static class PROP { public static readonly Vector3 position = new Vector3( 0, -0.53, -3.3 ); public const double radius = 0.21, pitch = 0.42; public const int blades = 4; }
	public static class RUDDER { public static readonly Vector3 pivot = new Vector3( 0, -0.52, -3.58 ); public const double top = -0.33, bottom = -0.72, lead = 0.08, trail = -0.27, maxAngle = 0.61; }

	public static class Running
	{
		static Vector3 V( double x, double y, double z ) => new Vector3( x, y, z );
		static readonly Opts BRONZE = new Opts { color = 0xc8905a, rough = 0.3, metal = 1 };

		// Four-blade right-handed propeller. Local frame: shaft axis +Z (forward); a positive rotation about +Z (clockwise seen from
		// astern) drives the boat ahead.
		public static BufferGeometry propellerGeometry()
		{
			double R = PROP.radius, P = PROP.pitch; int blades = PROP.blades;
			var list = new List<BufferGeometry>();

			var hub = GK.lathe( new[] { new[] { 0.0, -0.09 }, new[] { 0.015, -0.085 }, new[] { 0.03, -0.068 }, new[] { 0.043, -0.04 }, new[] { 0.048, 0.0 }, new[] { 0.047, 0.04 }, new[] { 0.042, 0.06 }, new[] { 0.0, 0.06 } }, 18 );
			hub.applyMatrix4( new Matrix4().makeRotationX( Math.PI / 2 ) );
			list.Add( GK.prepare( hub, BRONZE ) );

			double rh = 0.042;
			int NR = 9, NC = 9;
			for ( int b = 0; b < blades; b ++ )
			{
				double theta0 = b * Math.PI * 2 / blades;
				var mid = new List<List<Vector3>>();
				for ( int i = 0; i < NR; i ++ )
				{
					double rho = ( double ) i / ( NR - 1 );
					double r = rh + ( R - rh ) * rho;
					double chord = 0.15 * Math.Sqrt( Math.Max( 0, 1 - Math.Pow( rho, 2.4 ) ) ) * ( 0.62 + 0.38 * Math.Sin( Math.PI * Math.Min( 1, rho * 1.4 ) ) ) + 0.002;
					double phi = Math.Atan2( P, 2 * Math.PI * r );
					double skew = 0.32 * rho * rho;
					var row = new List<Vector3>();
					for ( int k = 0; k < NC; k ++ )
					{
						double c = -1 + 2.0 * k / ( NC - 1 );
						double s = c * chord * 0.5;
						double th = theta0 - skew + s * Math.Cos( phi ) / r;
						row.Add( V( Math.Cos( th ) * r, Math.Sin( th ) * r, s * Math.Sin( phi ) ) );
					}

					mid.Add( row );
				}

				// thickness along the local surface normal, vanishing at the edges and tip
				var face = new List<List<Vector3>>(); var back = new List<List<Vector3>>();
				for ( int i = 0; i < NR; i ++ )
				{
					double rho = ( double ) i / ( NR - 1 );
					var fr = new List<Vector3>(); var br = new List<Vector3>();
					for ( int k = 0; k < NC; k ++ )
					{
						double c = -1 + 2.0 * k / ( NC - 1 );
						var du = mid[ Math.Min( NR - 1, i + 1 ) ][ k ].clone().sub( mid[ Math.Max( 0, i - 1 ) ][ k ] );
						var dv = mid[ i ][ Math.Min( NC - 1, k + 1 ) ].clone().sub( mid[ i ][ Math.Max( 0, k - 1 ) ] );
						var n = du.cross( dv ).normalize();
						double t = 0.013 * ( 1 - 0.8 * rho ) * Math.Sqrt( Math.Max( 0, 1 - c * c ) ) * ( 1 - Math.Pow( rho, 8 ) );
						fr.Add( mid[ i ][ k ].clone().addScaledVector( n, t * 0.5 ) );
						br.Add( mid[ i ][ k ].clone().addScaledVector( n, -t * 0.5 ) );
					}

					face.Add( fr ); back.Add( br );
				}

				var gf = GK.gridSurface( face );
				var gb = GK.gridSurface( back, new GridOpts { flip = true } );
				// make sure each side faces away from the other
				var probe = face[ 4 ][ 4 ].clone().sub( back[ 4 ][ 4 ] );
				GK.orientTowards( gf, probe );
				GK.orientTowards( gb, probe.clone().negate() );
				gf.computeVertexNormals();
				gb.computeVertexNormals();
				list.Add( GK.prepare( gf, BRONZE ) ); list.Add( GK.prepare( gb, BRONZE ) );
			}

			return GeoKit.mergePrepared( list );
		}

		// Balanced spade rudder on a stock; local origin on the stock axis at mid blade.
		public static BufferGeometry rudderGeometry()
		{
			var pivot = RUDDER.pivot; double top = RUDDER.top, bottom = RUDDER.bottom, lead = RUDDER.lead, trail = RUDDER.trail;
			double yTop = top - pivot.y, yBot = bottom - pivot.y;
			Func<double, double, List<Vector3>> foil = ( chordScale, y ) =>
			{
				double c = ( lead - trail ) * chordScale;
				double z0 = lead * chordScale;
				var pts = new List<double[]>();
				int N = 12;
				for ( int i = 0; i <= N; i ++ )
				{
					double x = 1 - Math.Cos( Math.PI * i / N ) * 0.5 - 0.5; // cosine spacing 0..1
					double yt = 5 * 0.14 * ( 0.2969 * Math.Sqrt( x ) - 0.126 * x - 0.3516 * x * x + 0.2843 * Math.Pow( x, 3 ) - 0.1036 * Math.Pow( x, 4 ) );
					pts.Add( new[] { z0 - x * c, yt * c } );
				}

				var loop = new List<Vector3>();
				for ( int i = 0; i <= N; i ++ ) loop.Add( V( pts[ i ][ 1 ], y, pts[ i ][ 0 ] ) );
				for ( int i = N - 1; i >= 1; i -- ) loop.Add( V( -pts[ i ][ 1 ], y, pts[ i ][ 0 ] ) );
				return loop;
			};

			var bottomLoop = foil( 0.92, yBot ); var topLoop = foil( 1.0, yTop );
			var b0 = new List<Vector3>( bottomLoop ); b0.Add( bottomLoop[ 0 ].clone() );
			var t0 = new List<Vector3>( topLoop ); t0.Add( topLoop[ 0 ].clone() );
			var side = GK.gridSurface( new List<List<Vector3>> { b0, t0 } );
			// normals must point away from the stock axis
			var p = side.attributes[ "position" ]; var n = side.attributes[ "normal" ];
			double dot = 0;
			var a = V( 0, 0, 0 ); var b = V( 0, 0, 0 );
			for ( int i = 0; i < p.count; i ++ )
			{
				a.fromBufferAttribute( p, i ); b.fromBufferAttribute( n, i );
				dot += b.x * a.x;
			}

			if ( dot < 0 )
			{
				var idx = ( int[] ) side.index.array.Clone();
				for ( int i = 0; i < idx.Length; i += 3 ) { int t = idx[ i + 1 ]; idx[ i + 1 ] = idx[ i + 2 ]; idx[ i + 2 ] = t; }
				side.setIndex( idx );
				side.computeVertexNormals();
			}

			var opts = new Opts { color = Palette.antifouling, rough = 0.75 };
			var list = new List<BufferGeometry> { GK.prepare( side, opts ), GK.prepare( GK.fanCap( bottomLoop, V( 0, -1, 0 ) ), opts ), GK.prepare( GK.fanCap( topLoop, V( 0, 1, 0 ) ), opts ) };
			var stock = GK.cylinder( 0.022, 0.022, 0.3, 10 );
			stock.translate( 0, yTop + 0.15, 0 );
			list.Add( GK.prepare( stock, BRONZE ) );
			var heel = GK.cylinder( 0.012, 0.012, 0.05, 8 );
			heel.translate( 0, yBot - 0.015, 0 );
			list.Add( GK.prepare( heel, BRONZE ) );
			return GeoKit.mergePrepared( list );
		}
	}
}
