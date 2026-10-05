using System;
using System.Collections.Generic;
using System.Linq;
using Tidewater.Util;
using Tidewater.World.Terrain;
using static Tidewater.Util.MathX;

// Port of src/world/vegetation/Scatter.js: the CPU-side vegetation placement. The land cover mirrors the terrain shader's classification (forest weight
// from height, macro noise, slope and gullies: the same detail-texture fbm samples), so trees stand on the forest floor, the meadow grass on the meadow and bare
// rock stays bare; then the exclusion zones and the per-type scatter (the random numbers are drawn in the JS's order, so the records are the same plants).
// Everything is in sim coordinates (x east, y up, z south); the render boundary mirrors z.
namespace Tidewater.World.Vegetation
{
	public sealed class VegRec
	{
		public double x, y, z, s, sy, yaw, la, l, H, seed;
		public double qr; // (the view) the radius up to which the plant is wanted
	}

	public sealed class VegRecords
	{
		public List<VegRec> palms = new List<VegRec>(), trees = new List<VegRec>(), bananas = new List<VegRec>(), shrubs = new List<VegRec>(), youngPalms = new List<VegRec>(),
			ferns = new List<VegRec>(), monsteras = new List<VegRec>(), elephantEars = new List<VegRec>(), heliconias = new List<VegRec>(), strelitzias = new List<VegRec>();
		public int villagePalms;
	}

	public struct VegFootprint { public double x, z, r; }
	public sealed class VegPath { public List<double[]> points = new List<double[]>(); public double width = 1.8; }

	// land cover at a point (Scatter.js cover())
	public sealed class VegCover
	{
		public double h, ny, slope, macro, mA, mB, gully, forest, rock, bare, sand, path, scarp;
	}

	public static class VegRules
	{
		public const double minHeight = 1.8; // wet sand / water below
		public const double villageRadius = 100; // palms inside it are capped (between the houses)
		public const int villagePalms = 10;
		public const double pathClear = 12; // big plants: around the pier path x = 55, z in [-130, -40]
		public const double pathX = 55, pathZ0 = - 130, pathZ1 = - 40;
		public const double spawnClear = 5;
		public const double obstacleClear = 2; // around village footprints and boardwalks
		// default boardwalk (pier steps -> plaza) when no village object is supplied
		public static readonly double[][] boardwalk = { new[] { 55.0, - 65 }, new[] { 54.6, - 72 }, new[] { 52.4, - 82 }, new[] { 48.4, - 92 }, new[] { 44.8, - 100.5 }, new[] { 42.6, - 107.2 } };
		public const double boardwalkWidth = 1.8;
	}

	public sealed class VegSite
	{
		public readonly TerrainData terrain;
		public readonly Noise2D noise, noise2;
		readonly double vx = WorldLayout.Village.x, vz = WorldLayout.Village.z; // WORLD.village.center
		readonly double sx = WorldLayout.SpawnPosition.x, sz = WorldLayout.SpawnPosition.z;
		readonly byte[] detail;
		const int S = DetailTexture.S;
		public readonly bool hasVillage;
		VegFootprint[] footprints;
		double[][] segments; // x0, z0, x1, z1, half width
		double bx0, bz0, bx1, bz1;

		public VegSite( TerrainData terrain, int seed = 1234, List<VegFootprint> footprints = null, List<VegPath> paths = null )
		{
			this.terrain = terrain;
			noise = new Noise2D( seed );
			noise2 = new Noise2D( seed * 7 + 3 );
			detail = DetailTexture.Get();
			hasVillage = footprints != null && footprints.Count > 0;
			if ( paths == null || paths.Count == 0 ) paths = new List<VegPath> { new VegPath { points = VegRules.boardwalk.ToList(), width = VegRules.boardwalkWidth } };
			SetObstacles( footprints ?? new List<VegFootprint>(), paths );
		}

		// bilinear sample of one channel of the (repeating) detail texture at uv
		double Detail( double u, double v, int c )
		{
			double x = u * S - 0.5, y = v * S - 0.5;
			double xf = Math.Floor( x ), yf = Math.Floor( y );
			double tx = x - xf, ty = y - yf;
			int xi = ( int ) xf, yi = ( int ) yf;
			int x0 = ( ( xi % S ) + S ) % S, y0 = ( ( yi % S ) + S ) % S;
			int x1 = ( x0 + 1 ) % S, y1 = ( y0 + 1 ) % S;
			double a = detail[ ( y0 * S + x0 ) * 4 + c ], b = detail[ ( y0 * S + x1 ) * 4 + c ];
			double e = detail[ ( y1 * S + x0 ) * 4 + c ], f = detail[ ( y1 * S + x1 ) * 4 + c ];
			return ( ( a * ( 1 - tx ) + b * tx ) * ( 1 - ty ) + ( e * ( 1 - tx ) + f * tx ) * ty ) / 255;
		}

		static void Rot( double x, double z, double a, out double rx, out double rz ) { rx = x * Math.Cos( a ) - z * Math.Sin( a ); rz = x * Math.Sin( a ) + z * Math.Cos( a ); }

		public void SetObstacles( List<VegFootprint> fps, List<VegPath> paths )
		{
			footprints = fps.ToArray();
			var segs = new List<double[]>();
			foreach ( var p in paths )
				for ( int i = 0; i < p.points.Count - 1; i ++ ) segs.Add( new[] { p.points[ i ][ 0 ], p.points[ i ][ 1 ], p.points[ i + 1 ][ 0 ], p.points[ i + 1 ][ 1 ], p.width * 0.5 } );
			segments = segs.ToArray();

			// bounds for a cheap early out
			bx0 = bz0 = double.PositiveInfinity; bx1 = bz1 = double.NegativeInfinity;
			void Grow( double x, double z, double r ) { bx0 = Math.Min( bx0, x - r ); bx1 = Math.Max( bx1, x + r ); bz0 = Math.Min( bz0, z - r ); bz1 = Math.Max( bz1, z + r ); }
			foreach ( var f in footprints ) Grow( f.x, f.z, f.r );
			foreach ( var g in segments ) { Grow( g[ 0 ], g[ 1 ], g[ 4 ] ); Grow( g[ 2 ], g[ 3 ], g[ 4 ] ); }
		}

		// distance from (x, z) to the nearest footprint / boardwalk edge (large if far away)
		public double ObstacleDist( double x, double z )
		{
			const double pad = 25;
			if ( x < bx0 - pad || x > bx1 + pad || z < bz0 - pad || z > bz1 + pad ) return 1e9;
			double d = 1e9;
			foreach ( var f in footprints ) d = Math.Min( d, Hypot( x - f.x, z - f.z ) - f.r );
			foreach ( var g in segments )
			{
				double dx = g[ 2 ] - g[ 0 ], dz = g[ 3 ] - g[ 1 ];
				double l2 = dx * dx + dz * dz; if ( l2 == 0 ) l2 = 1;
				double t = Clamp( ( ( x - g[ 0 ] ) * dx + ( z - g[ 1 ] ) * dz ) / l2, 0, 1 );
				d = Math.Min( d, Hypot( x - g[ 0 ] - dx * t, z - g[ 1 ] - dz * t ) - g[ 4 ] );
			}

			return d;
		}

		public double Height( double x, double z ) => terrain.HeightAt( x, z );

		// bilinear sample of a terrain mask array (Float32 0..1 or Uint8 0..255)
		double Mask( Func<int, double> arr, double x, double z, double scale = 1 )
		{
			var t = terrain;
			double fx = ( x - t.origin ) / t.texel - 0.5, fz = ( z - t.origin ) / t.texel - 0.5;
			if ( fx < 0 || fz < 0 || fx >= t.res - 1 || fz >= t.res - 1 ) return 0;
			int i = ( int ) Math.Floor( fx ), j = ( int ) Math.Floor( fz );
			double tx = fx - i, tz = fz - j;
			int k = j * t.res + i;
			return ( ( arr( k ) * ( 1 - tx ) + arr( k + 1 ) * tx ) * ( 1 - tz ) + ( arr( k + t.res ) * ( 1 - tx ) + arr( k + t.res + 1 ) * tx ) * tz ) * scale;
		}

		double Mask( byte[] arr, double x, double z, double scale = 1 ) => Mask( k => arr[ k ], x, z, scale );

		public double Rock( double x, double z ) { var a = terrain.rock; return Mask( k => a[ k ], x, z ); }

		// the terrain's normal (TerrainData.normalAt: central differences one texel apart), in doubles
		void Normal( double x, double z, out double nx, out double ny, out double nz )
		{
			double e = terrain.texel;
			double hx = Height( x + e, z ) - Height( x - e, z );
			double hz = Height( x, z + e ) - Height( x, z - e );
			nx = - hx; ny = 2 * e; nz = - hz;
			double l = Math.Sqrt( nx * nx + ny * ny + nz * nz );
			if ( l > 0 ) { nx /= l; ny /= l; nz /= l; }
		}

		public double NormalY( double x, double z ) { Normal( x, z, out _, out double ny, out _ ); return ny; }

		public double VillageDist( double x, double z ) => Hypot( x - vx, z - vz );

		public double PathDist( double x, double z )
		{
			double cz = Clamp( z, VegRules.pathZ0, VegRules.pathZ1 );
			return Hypot( x - VegRules.pathX, z - cz );
		}

		public double SpawnDist( double x, double z ) => Hypot( x - sx, z - sz );

		// the central bay / beach "zone" (dune plants)
		public bool InBay( double x, double z ) => Math.Abs( x - 10 ) < 235 && z > - 245 && z < - 30;

		// Land cover at (x, z), matching Terrain.js: forest: forest-floor weight (0 meadow .. 1 forest); slope: 1 - N.y; rock: bare-rock tendency (mask + steepness);
		// gully, sand, path: splat masks; macro: large-scale fbm
		public void Cover( double x, double z, VegCover o )
		{
			var t = terrain;
			double h = Height( x, z );
			double ny = NormalY( x, z );
			double slope = 1 - ny;
			Rot( x, z, 0.7, out double ax, out double az ); Rot( x, z, 2.1, out double bx, out double bz );
			double mA = Detail( ax / 173, az / 173, 3 ), mB = Detail( bx / 47, bz / 47, 3 );
			double macro = mA * 0.6 + mB * 0.4;
			double gully = Mask( t.gully, x, z, 1.0 / 255 ) * Smoothstep( - 0.5, 0.5, h );
			double forest = Clamp( Smoothstep( 9, 24, h + ( macro - 0.5 ) * 18 ) + Smoothstep( 0.18, 0.36, slope ) + gully * 0.6, 0, 1 );
			double rock = Rock( x, z );
			o.h = h; o.ny = ny; o.slope = slope; o.macro = macro; o.mA = mA; o.mB = mB;
			o.gully = gully; o.forest = forest; o.rock = rock;
			o.bare = Math.Max( rock * 1.4, Smoothstep( 0.42, 0.55, slope ) ); // > 0.5: bare rock face
			o.sand = Mask( t.sand, x, z, 1.0 / 255 );
			o.path = Mask( t.path, x, z, 1.0 / 255 );
			// the eroded embankment's face (0.55 toe .. 1 lip; TerrainData._scarp)
			o.scarp = t.scarp != null ? Mask( t.scarp, x, z, 1.0 / 255 ) : 0;
		}

		public double ScarpAt( double x, double z ) => Mask( terrain.scarp, x, z, 1.0 / 255 );

		// Common exclusion test: true if a plant of radius `clear` may stand at (x, z).
		public bool Allowed( double x, double z, VegCover c, double minH = VegRules.minHeight, double maxBare = 0.35, double clear = 0, bool big = false, double maxSand = 0.5, double maxPath = 0.3 )
		{
			if ( c.h < minH || c.bare > maxBare || c.sand > maxSand || c.path > maxPath ) return false;
			// nothing rooted on the embankment face
			if ( c.scarp > 0.3 ) return false;
			if ( SpawnDist( x, z ) < VegRules.spawnClear + clear ) return false;
			// the pier path corridor: big plants keep RULES.pathClear, small ones half of it
			if ( PathDist( x, z ) < ( big ? VegRules.pathClear : VegRules.pathClear * 0.5 ) ) return false;
			if ( ObstacleDist( x, z ) < VegRules.obstacleClear + clear ) return false;
			if ( terrain.PathDistance( x, z ) < 0.6 + clear * 0.5 ) return false;
			return true;
		}

		// ground height under a trunk (lowest point of the footprint so nothing floats)
		public double GroundY( double x, double z, double r )
		{
			double h = Height( x, z );
			if ( r > 0 ) h = Math.Min( h, Math.Min( Math.Min( Height( x + r, z ), Height( x - r, z ) ), Math.Min( Height( x, z + r ), Height( x, z - r ) ) ) );
			return h;
		}

		public void Downhill( double x, double z, out double dx, out double dz )
		{
			Normal( x, z, out double nx, out _, out double nz );
			double l = Hypot( nx, nz );
			if ( l > 1e-4 ) { dx = nx / l; dz = nz / l; } else { dx = 0; dz = 1; }
		}
	}

	// Spatial hash of placed plants for minimum-distance tests across types.
	sealed class Occupancy
	{
		readonly double cell;
		readonly Dictionary<long, List<double>> map = new Dictionary<long, List<double>>();

		public Occupancy( double cell = 4 ) { this.cell = cell; }

		static long Key( int i, int j ) => ( i + 32768L ) * 65536 + ( j + 32768L );

		public void Add( double x, double z, double r, int kind )
		{
			long k = Key( ( int ) Math.Floor( x / cell ), ( int ) Math.Floor( z / cell ) );
			if ( ! map.TryGetValue( k, out var a ) ) map[ k ] = a = new List<double>();
			a.Add( x ); a.Add( z ); a.Add( r ); a.Add( kind );
		}

		// true if a circle (x, z, r) is free
		public bool Free( double x, double z, double r, double spacing = 1 )
		{
			double reach = r + 8;
			int i0 = ( int ) Math.Floor( ( x - reach ) / cell ), i1 = ( int ) Math.Floor( ( x + reach ) / cell );
			int j0 = ( int ) Math.Floor( ( z - reach ) / cell ), j1 = ( int ) Math.Floor( ( z + reach ) / cell );
			for ( int j = j0; j <= j1; j ++ )
				for ( int i = i0; i <= i1; i ++ )
				{
					if ( ! map.TryGetValue( Key( i, j ), out var a ) ) continue;
					for ( int k = 0; k < a.Count; k += 4 )
					{
						double dx = a[ k ] - x, dz = a[ k + 1 ] - z;
						double d = ( a[ k + 2 ] + r ) * spacing;
						if ( dx * dx + dz * dz < d * d ) return false;
					}
				}

			return true;
		}
	}

	public static class VegScatter
	{
		const int PALM = 1, TREE = 2, BANANA = 3, SHRUB = 4, YOUNG = 5, FERN = 6, BROAD = 7;
		const double TAU = Math.PI * 2;

		// Jittered-grid dart throwing over a rectangle.
		static void Scatter( Mulberry32 rand, double x0, double z0, double x1, double z1, double step, Action<double, double> fn )
		{
			for ( double z = z0; z < z1; z += step )
				for ( double x = x0; x < x1; x += step )
				{
					double jx = rand.Next(), jz = rand.Next();
					fn( x + jx * step, z + jz * step );
				}
		}

		// Open meadow slopes away from the village (the headlands): 0..1 weight for extra scattered trees, tree clumps and scrub. Denser in the gullies and hollows,
		// thinner on the high exposed slopes, none on bare rock, the beach sand or the paths; open grassy patches are left by the noise gating of the callers.
		static double Headland( VegSite s, double x, double z, VegCover c, bool sandOk = false )
		{
			if ( c.h < 2.3 || c.h > 80 || c.bare > 0.45 || c.path > 0.3 ) return 0;
			double dv = s.VillageDist( x, z );
			double away = Smoothstep( VegRules.villageRadius + 5, VegRules.villageRadius + 40, dv );
			if ( away <= 0 ) return 0;
			double exposed = Smoothstep( 18, 55, c.h ) * ( 1 - c.gully );
			double shelter = 1 - 0.6 * exposed + 0.5 * c.gully;
			return away * shelter * ( sandOk ? 1 : 1 - Smoothstep( 0.35, 0.7, c.sand ) ) * ( 1 - Smoothstep( 0.25, 0.45, c.rock ) );
		}

		public static VegRecords Scatter( VegSite site, uint seed = 99 )
		{
			var rand = new Mulberry32( seed );
			// occ: trunks / plant footprints (everything tests against it); occT: tree crowns (trees keep their spacing, palms stay out of the crowns, the understory may grow
			// beneath them)
			var occ = new Occupancy( 4 );
			var occT = new Occupancy( 6 );
			var N = site.noise; var N2 = site.noise2;
			var o = new VegRecords();
			var c = new VegCover();
			double R() => rand.Next();

			// island bounds (land lies roughly within these); the headlands either side of the bay reach south to z ~ +320 (HZ1)
			const double X0 = - 660, X1 = 660, Z0 = - 900, Z1 = - 20, HZ1 = 320;

			// --- broadleaf trees: a closed canopy on the forest ground (hillsides, gullies), thinning at the forest edge into scattered trees; a few lone trees on the meadow
			Scatter( rand, X0, Z0, X1, HZ1, 4.6, ( x, z ) =>
			{
				site.Cover( x, z, c );
				if ( c.h < 3 ) return;
				double clump = N.Fbm( x / 60, z / 60, 3 ) * 0.6 + N2.Fbm( x / 17, z / 17, 2 ) * 0.4;
				double p = c.forest > 0.5 ? 0.9 : Smoothstep( 0.15, 0.5, c.forest ) * 0.38 * Smoothstep( - 0.25, 0.2, clump ) + 0.008;
				// headlands (south of the bay line): wind-shaped scattered trees and clumps, not a closed forest
				if ( z > Z1 ) p = c.forest > 0.5 ? 0.45 * Smoothstep( - 0.2, 0.2, clump ) : p;
				if ( c.forest <= 0.5 ) p += Headland( site, x, z, c ) * ( 0.9 * Smoothstep( - 0.2, 0.2, clump ) + 0.1 );
				if ( R() > p ) return;
				// crowns must not overhang the houses: trunks >= 7 m from footprints / boardwalks
				if ( ! site.Allowed( x, z, c, minH: 3, maxBare: 0.3, clear: 5, big: true ) ) return;
				// emergent giants now and then; smaller trees at the forest edge; sub-canopy trees fill the gaps between the big crowns
				bool sub = c.forest > 0.5 && R() < 0.3;
				double s = ( sub ? 0.55 + R() * 0.2 : 0.8 + R() * 0.5 ) * ( c.forest > 0.5 ? 1 : 0.85 ) * ( R() < 0.08 ? 1.3 : 1 );
				double sy = 0.78 + R() * 0.34 + ( R() < 0.1 ? 0.25 : 0 );
				double r = 2.5 * s;
				if ( ! occT.Free( x, z, r, 1 ) ) return;
				occT.Add( x, z, r, TREE );
				occ.Add( x, z, 0.5 * s, TREE );
				double yaw = R() * TAU;
				o.trees.Add( new VegRec { x = x, y = site.GroundY( x, z, 0.6 ) - 0.15, z = z, s = s, sy = sy, yaw = yaw, la = yaw, l = sy, H = 12.5 * s * sy, seed = R() } );
			} );

			// --- coconut palms along the back of the beach (groves, leaning to the sea)
			VegRec BeachPalm( double x, double z, double h )
			{
				site.Downhill( x, z, out double dhx, out double dhz );
				double lx = dhx * 0.35, lz = 1 + dhz * 0.35; // mostly toward the sea (+z)
				double la = Math.Atan2( lz, lx ) + ( R() - 0.5 ) * 1.1;
				double nearShore = 1 - Smoothstep( 2.3, 5.5, h );
				double lean = ( 0.1 + 0.32 * nearShore ) * ( 0.55 + 0.75 * R() );
				double s = 0.85 + R() * 0.32;
				double H = ( 7 + R() * 6.5 ) * ( 0.9 + 0.2 * s );
				double yaw = R() * TAU;
				return new VegRec { x = x, y = site.GroundY( x, z, 0.35 ) - 0.05, z = z, s = s, yaw = yaw, la = la, l = lean, H = H, seed = R() };
			}

			Scatter( rand, - 240, - 230, 250, - 40, 3.4, ( x, z ) =>
			{
				if ( ! site.InBay( x, z ) ) return;
				if ( site.VillageDist( x, z ) < VegRules.villageRadius ) return;
				site.Cover( x, z, c );
				double grove = N.Fbm( x / 48, z / 48, 3 ) + 0.4 * N2.Fbm( x / 17, z / 17, 2 );
				double band = Smoothstep( 1.95, 2.35, c.h ) * ( 1 - Smoothstep( 5.2, 7.2, c.h ) );
				double p = 0.5 * Smoothstep( - 0.28, 0.22, grove ) * band;
				if ( R() > p ) return;
				if ( ! site.Allowed( x, z, c, minH: 2.0, maxBare: 0.2, clear: 3, big: true, maxSand: 1.01, maxPath: 0.5 ) || c.ny < 0.9 ) return;
				if ( ! occ.Free( x, z, 1.95, 1 ) || ! occT.Free( x, z, 1.5, 1 ) ) return;
				occ.Add( x, z, 1.95, PALM );
				o.palms.Add( BeachPalm( x, z, c.h ) );
			} );

			// --- a few palms inside the village radius: between the houses and along the beach edge, clear of buildings, boardwalks and the pier path
			var vc = new List<( double x, double z, double h, double r )>();
			Scatter( rand, - 70, - 220, 150, - 60, 3.5, ( x, z ) =>
			{
				if ( site.VillageDist( x, z ) >= VegRules.villageRadius ) return;
				if ( ! site.hasVillage && z < - 106 ) return; // unknown houses: stay on the beach edge
				site.Cover( x, z, c );
				if ( c.h < 2.05 || c.h > 14 || c.bare > 0.2 || c.ny < 0.9 ) return;
				if ( site.PathDist( x, z ) < VegRules.pathClear || site.SpawnDist( x, z ) < VegRules.spawnClear + 3 ) return;
				double od = site.ObstacleDist( x, z );
				if ( od < 3.5 ) return;
				double between = site.hasVillage ? Smoothstep( 4.5, 6, od ) * ( 1 - Smoothstep( 10, 16, od ) ) : 0;
				double beach = Smoothstep( 2.0, 2.3, c.h ) * ( 1 - Smoothstep( 3.6, 4.4, c.h ) );
				vc.Add( ( x, z, c.h, R() * ( 0.25 + Math.Max( between, beach ) ) ) );
			} );
			int village = 0;
			foreach ( var v in vc.OrderByDescending( a => a.r ) ) // (a stable sort, as the JS's)
			{
				if ( village >= VegRules.villagePalms ) break;
				if ( ! occ.Free( v.x, v.z, 5.5, 1 ) || ! occT.Free( v.x, v.z, 3, 1 ) ) continue;
				occ.Add( v.x, v.z, 5.5, PALM );
				o.palms.Add( BeachPalm( v.x, v.z, v.h ) );
				village ++;
			}

			o.villagePalms = village;

			// --- palms scattered over the valley and the forest edge (above the canopy here and there)
			Scatter( rand, X0, Z0, X1, Z1, 12, ( x, z ) =>
			{
				site.Cover( x, z, c );
				double patch = N2.Fbm( x / 110 + 3.3, z / 110 - 1.7, 3 );
				double p = ( 0.04 + 0.4 * Smoothstep( 0.0, 0.45, patch ) ) * ( 1 - Smoothstep( 60, 160, c.h ) );
				if ( R() > p ) return;
				if ( site.VillageDist( x, z ) < VegRules.villageRadius ) return;
				if ( ! site.Allowed( x, z, c, minH: 6, maxBare: 0.2, clear: 3, big: true ) || c.ny < 0.8 ) return;
				if ( ! occ.Free( x, z, 2.0, 1 ) || ! occT.Free( x, z, 1.2, 1 ) ) return;
				occ.Add( x, z, 2.0, PALM );
				site.Downhill( x, z, out double dhx, out double dhz );
				double s = 0.8 + R() * 0.35;
				double yaw = R() * TAU;
				double la = Math.Atan2( dhz, dhx ) + ( R() - 0.5 ) * 1.5;
				double l = 0.03 + R() * 0.12;
				double H = ( 8 + R() * 7 ) * s;
				o.palms.Add( new VegRec { x = x, y = site.GroundY( x, z, 0.35 ) - 0.05, z = z, s = s, yaw = yaw, la = la, l = l, H = H, seed = R() } );
			} );

			// lean clustered palms away from each other (natural "fan" groups)
			foreach ( var p in o.palms )
			{
				double ax = 0, az = 0;
				foreach ( var q in o.palms )
				{
					if ( q == p ) continue;
					double dx = p.x - q.x, dz = p.z - q.z;
					double d2 = dx * dx + dz * dz;
					if ( d2 < 25 && d2 > 1e-6 )
					{
						double d = Math.Sqrt( d2 );
						ax += dx / d * ( 5 - d );
						az += dz / d * ( 5 - d );
					}
				}

				if ( ax != 0 || az != 0 )
				{
					double cx = Math.Cos( p.la ), cz = Math.Sin( p.la );
					double w = Math.Min( 1, Hypot( ax, az ) / 3 );
					p.la = Math.Atan2( cz * ( 1 - w ) + az / Hypot( ax, az ) * w, cx * ( 1 - w ) + ax / Hypot( ax, az ) * w );
					p.l = Math.Max( p.l, 0.14 + 0.1 * w );
				}
			}

			// --- shrubs: thickets along the forest edge and in the gullies, understory in the forest, a few clumps out on the meadow and the back of the beach
			Scatter( rand, X0, Z0, X1, HZ1, 3.0, ( x, z ) =>
			{
				site.Cover( x, z, c );
				if ( c.h < 2.3 ) return;
				double thicket = N.Fbm( x / 26 + 11.3, z / 26 - 4.1, 3 ) * 0.7 + N2.Fbm( x / 9, z / 9, 2 ) * 0.3;
				double edge = Smoothstep( 0.2, 0.45, c.forest ) * ( 1 - Smoothstep( 0.75, 0.95, c.forest ) );
				bool bay = site.InBay( x, z ) && c.h < 5.5;
				double p = edge * 0.75 + c.gully * 0.5 + ( c.forest > 0.9 ? 0.12 : 0 ) + ( bay ? 0.08 : 0.03 );
				double open = Headland( site, x, z, c, true );
				p += open * ( 0.32 + 0.45 * ( 1 - Smoothstep( 3.5, 9, c.h ) ) ) * ( 1 - Smoothstep( 0.5, 0.8, c.forest ) );
				p *= Smoothstep( - 0.2, 0.25, thicket );
				if ( site.VillageDist( x, z ) < VegRules.villageRadius ) p *= 0.35;
				if ( R() > p ) return;
				if ( ! site.Allowed( x, z, c, minH: 2.3, maxBare: 0.4, clear: 1.2 ) ) return;
				double s = ( 0.75 + R() * 0.8 ) * ( open > 0.3 ? 1.35 : 1 );
				double sy = 0.65 + R() * 0.65;
				if ( ! occ.Free( x, z, 0.85 * s, 1 ) ) return;
				occ.Add( x, z, 0.85 * s, SHRUB );
				double yaw = R() * TAU;
				o.shrubs.Add( new VegRec { x = x, y = site.GroundY( x, z, 0.3 ) - 0.08 * s, z = z, s = s, sy = sy, yaw = yaw, la = yaw, l = - sy, H = 1.6 * s * sy, seed = R() } );
			} );

			// --- banana groves on the lower slopes around the village
			Scatter( rand, - 280, - 420, 360, - 40, 4.2, ( x, z ) =>
			{
				double dv = site.VillageDist( x, z );
				if ( dv > 330 ) return;
				site.Cover( x, z, c );
				double grove = N2.Fbm( x / 38 - 5.1, z / 38 + 2.2, 3 );
				double p = 0.75 * Smoothstep( 0.12, 0.38, grove ) * ( 1 - Smoothstep( 220, 330, dv ) ) * ( 1 - Smoothstep( 0.8, 1, c.forest ) * 0.6 );
				if ( R() > p ) return;
				if ( ! site.Allowed( x, z, c, minH: 3.4, maxBare: 0.2, clear: 1.5 ) || c.ny < 0.82 ) return;
				double s = 0.8 + R() * 0.45;
				if ( ! occ.Free( x, z, 1.1 * s, 1 ) ) return;
				occ.Add( x, z, 1.1 * s, BANANA );
				double yaw = R() * TAU, la = R() * TAU, l = R() * 0.08;
				double H = 1.8 * s * ( 0.8 + R() * 0.5 );
				o.bananas.Add( new VegRec { x = x, y = site.GroundY( x, z, 0.2 ) - 0.05, z = z, s = s, yaw = yaw, la = la, l = l, H = H, seed = R() } );
			} );

			// --- young palms: grove edges, forest edge, the back of the beach
			Scatter( rand, X0, Z0, X1, Z1, 6.5, ( x, z ) =>
			{
				site.Cover( x, z, c );
				if ( c.h < 2.3 || c.h > 120 ) return;
				double grove = N.Fbm( x / 48, z / 48, 3 );
				double p = ( site.InBay( x, z ) ? 0.22 : 0.1 ) * Smoothstep( - 0.3, 0.2, grove ) * ( 1 - c.forest * 0.6 );
				if ( R() > p ) return;
				if ( ! site.Allowed( x, z, c, minH: 2.3, maxBare: 0.2, clear: 1.5 ) ) return;
				double s = 0.7 + R() * 0.6;
				if ( ! occ.Free( x, z, 1.1 * s, 1 ) ) return;
				occ.Add( x, z, 1.1 * s, YOUNG );
				double yaw = R() * TAU, la = R() * TAU, l = R() * 0.1;
				o.youngPalms.Add( new VegRec { x = x, y = c.h - 0.05, z = z, s = s, yaw = yaw, la = la, l = l, H = 0.35 * s, seed = R() } );
			} );

			// --- ferns: forest floor, gullies, the shady foot of the forest edge
			Scatter( rand, X0, Z0, X1, Z1, 2.2, ( x, z ) =>
			{
				site.Cover( x, z, c );
				if ( c.h < 4 ) return;
				double fn = N2.Fbm( x / 28 - 9.9, z / 28 + 3.7, 3 );
				double p = ( 0.08 + 0.72 * Smoothstep( 0.3, 0.8, c.forest ) + c.gully * 0.4 ) * Smoothstep( - 0.35, 0.2, fn );
				if ( R() > p ) return;
				if ( ! site.Allowed( x, z, c, minH: 4, maxBare: 0.45, clear: 0.5 ) ) return;
				double s = 0.75 + R() * 0.7;
				if ( ! occ.Free( x, z, 0.5 * s, 1 ) ) return;
				occ.Add( x, z, 0.5 * s, FERN );
				double yaw = R() * TAU;
				o.ferns.Add( new VegRec { x = x, y = c.h - 0.03, z = z, s = s, yaw = yaw, la = 0, l = 0, H = 0.05, seed = R() } );
			} );

			// --- broadleaf understory (big leaves)
			// monstera: clumps on the shaded forest floor and along the forest edge, in the gullies, and a few in the village gardens
			Scatter( rand, X0, Z0, X1, Z1, 3.2, ( x, z ) =>
			{
				site.Cover( x, z, c );
				if ( c.h < 3.5 ) return;
				double clump = N2.Fbm( x / 22 + 7.7, z / 22 - 1.3, 3 );
				double dv = site.VillageDist( x, z );
				double garden = dv < 95 ? 0.18 * Smoothstep( 0.3, 0.7, c.forest + 0.4 ) : 0;
				double p = ( 0.5 * Smoothstep( 0.35, 0.75, c.forest ) + c.gully * 0.35 + garden ) * Smoothstep( 0.0, 0.35, clump );
				if ( R() > p ) return;
				if ( ! site.Allowed( x, z, c, minH: 3.5, maxBare: 0.35, clear: 1.0, maxSand: 0.15 ) || c.ny < 0.75 ) return;
				double sc = 0.8 + R() * 0.6;
				if ( ! occ.Free( x, z, 0.9 * sc, 1 ) ) return;
				occ.Add( x, z, 0.9 * sc, BROAD );
				double yaw = R() * TAU, la = R() * TAU, l = R() * 0.05;
				o.monsteras.Add( new VegRec { x = x, y = c.h - 0.04, z = z, s = sc, yaw = yaw, la = la, l = l, H = 0.02, seed = R() } );
			} );

			// elephant ears: damp ground (gullies, the low backshore behind the bay, the forest foot)
			Scatter( rand, X0, Z0, X1, Z1, 3.6, ( x, z ) =>
			{
				site.Cover( x, z, c );
				if ( c.h < 2.6 ) return;
				double clump = N.Fbm( x / 30 - 3.1, z / 30 + 8.4, 3 );
				double damp = c.gully * 0.8 + Smoothstep( 7, 3, c.h ) * 0.25 + Smoothstep( 0.25, 0.5, c.forest ) * ( 1 - Smoothstep( 0.7, 0.95, c.forest ) ) * 0.25;
				double p = damp * Smoothstep( 0.05, 0.4, clump );
				if ( R() > p ) return;
				if ( ! site.Allowed( x, z, c, minH: 2.6, maxBare: 0.3, clear: 1.0, maxSand: 0.15 ) || c.ny < 0.8 ) return;
				double sc = 0.95 + R() * 0.55;
				if ( ! occ.Free( x, z, 1.1 * sc, 1 ) ) return;
				occ.Add( x, z, 1.1 * sc, BROAD );
				double yaw = R() * TAU, la = R() * TAU, l = R() * 0.05;
				o.elephantEars.Add( new VegRec { x = x, y = c.h - 0.04, z = z, s = sc, yaw = yaw, la = la, l = l, H = 0.02, seed = R() } );
			} );

			// heliconia: colour around the village (beside houses and paths) and at the sunny forest edge
			Scatter( rand, X0, Z0, X1, Z1, 3.4, ( x, z ) =>
			{
				double dv = site.VillageDist( x, z );
				site.Cover( x, z, c );
				if ( c.h < 3 ) return;
				double edge = Smoothstep( 0.2, 0.45, c.forest ) * ( 1 - Smoothstep( 0.7, 0.9, c.forest ) );
				double od = site.ObstacleDist( x, z );
				double vil = dv < 110 ? Smoothstep( 16, 4, od ) * 0.45 + 0.04 : 0;
				double p = ( edge * 0.12 + vil ) * Smoothstep( - 0.1, 0.3, N2.Fbm( x / 18 + 2.2, z / 18 - 6.6, 2 ) );
				if ( R() > p ) return;
				if ( ! site.Allowed( x, z, c, minH: 3, maxBare: 0.3, clear: 1.2, maxSand: 0.25 ) || c.ny < 0.8 ) return;
				double sc = 0.85 + R() * 0.35;
				if ( ! occ.Free( x, z, 0.9 * sc, 1 ) ) return;
				occ.Add( x, z, 0.9 * sc, BROAD );
				double yaw = R() * TAU, la = R() * TAU, l = R() * 0.04;
				o.heliconias.Add( new VegRec { x = x, y = c.h - 0.04, z = z, s = sc, yaw = yaw, la = la, l = l, H = 0.02, seed = R() } );
			} );

			// bird of paradise: planted beside the village houses
			Scatter( rand, X0, Z0, X1, Z1, 3.0, ( x, z ) =>
			{
				if ( site.VillageDist( x, z ) > 105 ) return;
				site.Cover( x, z, c );
				if ( c.h < 3 ) return;
				double od = site.ObstacleDist( x, z );
				double p = Smoothstep( 10, 3.5, od ) * 0.35 * Smoothstep( - 0.2, 0.3, N.Fbm( x / 14 - 4.4, z / 14 + 6.6, 2 ) );
				if ( R() > p ) return;
				if ( ! site.Allowed( x, z, c, minH: 3, maxBare: 0.3, clear: 0.8, maxSand: 0.25 ) || c.ny < 0.85 ) return;
				double sc = 0.85 + R() * 0.3;
				if ( ! occ.Free( x, z, 0.7 * sc, 1 ) ) return;
				occ.Add( x, z, 0.7 * sc, BROAD );
				double yaw = R() * TAU, la = R() * TAU, l = R() * 0.03;
				o.strelitzias.Add( new VegRec { x = x, y = c.h - 0.03, z = z, s = sc, yaw = yaw, la = la, l = l, H = 0.02, seed = R() } );
			} );

			return o;
		}

		// Ground-flora density mask, RGBA8 over the terrain grid at 2 m (linear filtered by the grass shader): R dune grass, G tall meadow grass, B sea oats, A beach creeper.
		public const double GrassMaskTexel = 2;

		public static byte[] BuildGrassMask( VegSite site, out int res )
		{
			var t = site.terrain;
			res = ( int ) Round( t.size / GrassMaskTexel );
			double texel = t.size / res;
			var data = new byte[ res * res * 4 ];
			var N = site.noise; var N2 = site.noise2;
			var c = new VegCover();
			for ( int j = 1; j < res - 1; j ++ )
			{
				double z = t.origin + ( j + 0.5 ) * texel;
				if ( z > 0 || z < - 950 ) continue;
				for ( int i = 1; i < res - 1; i ++ )
				{
					int k = j * res + i;
					double x = t.origin + ( i + 0.5 ) * texel;
					if ( t.HeightAt( x, z ) < 1.7 ) continue;
					if ( site.Rock( x, z ) > 0.45 ) continue;
					site.Cover( x, z, c );
					if ( c.bare > 0.5 ) continue;
					// exclusions (+2 m so bilinear filtering of the texels never bleeds inside)
					double sd = site.SpawnDist( x, z );
					if ( sd < VegRules.spawnClear + 2 ) continue;
					double od = site.ObstacleDist( x, z );
					if ( od < VegRules.obstacleClear + 1 ) continue;
					double keep = Smoothstep( VegRules.spawnClear + 2, VegRules.spawnClear + 4, sd ) * Smoothstep( VegRules.obstacleClear + 1, VegRules.obstacleClear + 3.5, od )
						* ( 1 - Smoothstep( 0.2, 0.5, c.path ) ) * ( 1 - Smoothstep( 0.25, 0.45, c.bare ) );
					bool bay = site.InBay( x, z );
					double clump = N.Noise( x / 7.5, z / 7.5 ) * 0.65 + N2.Noise( x / 19, z / 19 ) * 0.35;

					// tall meadow grass on the open ground, thinning into the forest; trodden near houses
					double house = site.hasVillage ? 1 - 0.55 * ( 1 - Smoothstep( 3, 9, od ) ) : ( 1 - 0.85 * ( 1 - Smoothstep( VegRules.villageRadius - 6, VegRules.villageRadius + 4, site.VillageDist( x, z ) ) ) );
					double meadow = Smoothstep( 2.5, 4.5, c.h ) * ( 1 - Smoothstep( 0.45, 0.85, c.forest ) ) * ( 1 - Smoothstep( 0.3, 0.7, c.sand ) ) * house
						* ( 0.75 + 0.25 * Smoothstep( - 0.4, 0.3, clump ) );

					// Backshore vegetation edge (in the bay): driven by the ground height above the sea, so it follows the beach profile (and any later embankment): a lobed, noisy edge
					// with tongues of grass reaching seaward and isolated clumps ahead of it; the lowest ~1.5 m (the ~20 m of beach above the waterline) stays bare sand.
					double dune = 0, oats = 0, vine = 0;
					if ( bay )
					{
						double lobe = N.Noise( x / 16 + 4.4, z / 16 - 2.2 ) * 0.6 + N2.Noise( x / 6 - 1.7, z / 6 + 8.1 ) * 0.4;
						double e = c.h - ( 2.15 - lobe * 0.4 ); // > 0 behind the edge
						// Where the embankment runs, the vegetation stops at its lip: the beach below it (up to ~32 m seaward of a face, found by looking landward up the slope)
						// stays sand, the face only carries a few trailing runners and tufts, and the grass on top reaches right to the lip and overhangs it.
						double belowScarp = 0;
						if ( c.scarp < 0.3 && t.scarp != null )
						{
							// landward: up the smoothed slope (8 m differences: the berm's local normal wanders); the face is only 1 - 3 m wide, so step finely
							double gx = site.Height( x + 8, z ) - site.Height( x - 8, z ), gz = site.Height( x, z + 8 ) - site.Height( x, z - 8 );
							double gl = Hypot( gx, gz );
							if ( gl > 1e-3 )
							{
								gx /= gl; gz /= gl;
								for ( double d = 1; d <= 34; d += 1.5 ) belowScarp = Math.Max( belowScarp, site.ScarpAt( x + gx * d, z + gz * d ) );
							}

							belowScarp = Smoothstep( 0.3, 0.5, belowScarp );
						}

						double onFace = Smoothstep( 0.3, 0.5, c.scarp );
						e = e * ( 1 - belowScarp ) - 2 * belowScarp;
						double main = Smoothstep( 0.0, 0.35, e );
						double ahead = Smoothstep( - 0.45, - 0.1, e ) * ( 1 - main ) * Smoothstep( 0.35, 0.6, N.Noise( x / 2.6 + 9.3, z / 2.6 - 4.1 ) );
						double inland = 1 - Smoothstep( 4.8, 6.5, c.h );
						// patchy sward: dense clumps (a few metres), thinner stretches and bare sand gaps
						double patch = N2.Noise( x / 4.2 + 5.5, z / 4.2 - 3.3 ) * 0.6 + N.Noise( x / 11 - 7.1, z / 11 + 1.9 ) * 0.4;
						double clumpD = ( 0.3 + 0.7 * Smoothstep( - 0.45, 0.25, patch ) ) * ( 0.6 + 0.4 * Smoothstep( - 0.35, 0.35, clump ) );
						dune = Math.Max( main * clumpD, ahead * 0.85 ) * inland * ( 1 - Smoothstep( 0.4, 0.8, c.forest ) );
						// the face: sparse tufts hanging on (more near the lip)
						dune = dune * ( 1 - onFace ) + onFace * 0.18 * Smoothstep( 0.7, 1.0, c.scarp ) * Smoothstep( 0.0, 0.4, patch );
						// sea oats: fore-dune tufts just behind the edge
						oats = Smoothstep( 0.0, 0.25, e ) * ( 1 - Smoothstep( 1.4, 2.2, e ) ) * Smoothstep( 0.0, 0.45, N.Noise( x / 13 + 3.1, z / 13 - 7.7 ) );
						// creepers (beach morning glory): runners mat the ground at the edge and reach further seaward than the grass
						vine = Smoothstep( - 0.6, - 0.15, e ) * ( 1 - Smoothstep( 1.2, 2.0, e ) ) * Smoothstep( - 0.15, 0.3, N2.Noise( x / 9 - 2.3, z / 9 + 5.3 ) );
						// runners trailing down the face from the lip, and matting its top
						vine = Math.Max( vine * ( 1 - onFace ), onFace * 0.6 * Smoothstep( 0.0, 0.35, N2.Noise( x / 3.5 + 1.1, z / 3.5 - 2.9 ) ) );
					}

					int o = k * 4;
					data[ o ] = ( byte ) Round( 255 * Clamp( dune * keep, 0, 1 ) );
					data[ o + 1 ] = ( byte ) Round( 255 * Clamp( meadow * keep * ( 1 - dune ), 0, 1 ) );
					data[ o + 2 ] = ( byte ) Round( 255 * Clamp( oats * keep, 0, 1 ) );
					data[ o + 3 ] = ( byte ) Round( 255 * Clamp( vine * keep, 0, 1 ) );
				}
			}

			return data;
		}
	}
}
