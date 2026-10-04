using System;
using System.Collections.Generic;
using Tidewater.Engine;
using Tidewater.Util;
using static Tidewater.World.Village.GeoBuilder;
using static Tidewater.World.Village.Props;

// Port of src/world/village/Buildings.js: parametric tropical fishing-village buildings (cottages, two-storey houses and
// stilt huts, a boathouse, a market stall, a shed). All geometry goes into a Builder in the building's local frame
// (x across the facade, +z = front, y = world height). The random calls keep the JS order, argument lists included.
namespace Tidewater.World.Village
{
	public sealed class Footprint { public double x, z, r; public string kind; }

	public sealed class BuildResult { public double floorY, roofTop; public Footprint footprint; }

	public sealed class PorchSpec { public double depth; public double? width, offset, stairX; public string rail; }

	public sealed class AnnexSpec { public double? w, d, x; public double[] wall; }

	// the building spec object of Village._layout (every field optional, as in JS)
	public sealed class Spec
	{
		public string name; public bool harbor;
		public double x, z, yaw;
		public double? w, d, floorY, storyH, clearance, paint, weather, pitch, ovE, ovR, winW, winH, doorX, litChance, closedChance, rust, thatchAge, porchPitch, siding, stoneStyle;
		public int stories, chimney, tank, woodpile, buoys;
		public string roof, roofMat, foundation, shutters;
		public double[] roofColor, wall, trim, accent, curtain, porchPaint, plaster;
		public double[] railNet, door;
		public PorchSpec porch; public AnnexSpec annex;
		public bool galv, stovepipe, antenna, gutter, doorGlass, fewWindows, rimRaw, noVent;
		public bool? lantern, porchBench;
	}

	// per-building style (the JS `st` object; cloned with overrides for single windows)
	public sealed class Style
	{
		public double[] trim, accent, curtain, stepTint;
		public double paint, trimPaint, weather, litChance, stepPaint;
		public string shutters;
		public bool doorGlass, closed;
		public double? closedPattern;
		public Style Clone() => ( Style ) MemberwiseClone();
	}

	sealed class Ridge { public double y, ta, yF, zf, xe; public char axis; }

	sealed class Frame
	{
		public Func<double, double, double[]> toW;
		public Func<double, double, double> gAt;
		public List<FoundationCheck> checks;
		public Func<double, double, double, FoundationCheck> worldPt;
		public Action<double, double, double, double, double, double, bool, bool, string> addBoxL;
	}

	public static class Buildings
	{
		static readonly Vector3 UPV = new Vector3( 0, 1, 0 );
		static Vector3 UP => UPV.clone();
		static readonly double[] WARM = { 1.0, 0.68, 0.38 };
		static Color WarmColor() => new Color( 1.0, 0.68, 0.38 );
		static readonly double[] WHITE3 = { 1, 1, 1 };

		static void frameFns( TerrainData terrain, double x, double z, double yaw, out Func<double, double, double[]> toW, out Func<double, double, double> gAt )
		{
			double cy = Math.Cos( yaw ), sy = Math.Sin( yaw );
			Func<double, double, double[]> tw = ( lx, lz ) => new[] { x + lx * cy + lz * sy, z - lx * sy + lz * cy };
			toW = tw;
			gAt = ( lx, lz ) => { var p = tw( lx, lz ); return terrain.HeightAt( p[ 0 ], p[ 1 ] ); };
		}

		static List<double> linspace( double a, double b, int n )
		{
			var o = new List<double>();
			if ( n <= 1 ) { o.Add( ( a + b ) / 2 ); return o; }
			for ( int i = 0; i < n; i ++ ) o.Add( a + ( b - a ) * i / ( n - 1 ) );
			return o;
		}

		// pentagonal gable end in the local XY plane (base width `base` along x), extruded by `t` along z
		static Part gableEndPart( double bas, double hSide, double hApex, double t )
		{
			var pts = new[] { V3( - bas / 2, 0, t / 2 ), V3( bas / 2, 0, t / 2 ), V3( bas / 2, hSide, t / 2 ), V3( 0, hApex, t / 2 ), V3( - bas / 2, hSide, t / 2 ) };
			return slabPart( pts, t, V3( 1, 0, 0 ), V3( 0, 0, 1 ) );
		}

		// ---------------------------------------------------------------------------
		// shared building blocks (all in the builder's current frame; wall surface at z = 0, facing +z)

		// returns the lit window's centre (x, y) or null
		static double[] windowUnit( Builder B, Rand rand, double cx, double sillY, double ww, double wh, Style st )
		{
			double tw = 0.085, tp = 0.035;
			var trim = st.trim;
			double[] td() => new[] { rand.next(), st.trimPaint, 0, st.weather };
			B.box( "wood", cx - ww / 2 - tw / 2, sillY + wh / 2, tp / 2, tw, wh + 0.02, tp, new O { grain = 1, skip = 44, tint = trim, data = td() } );
			B.box( "wood", cx + ww / 2 + tw / 2, sillY + wh / 2, tp / 2, tw, wh + 0.02, tp, new O { grain = 1, skip = 44, tint = trim, data = td() } );
			B.box( "wood", cx, sillY + wh + 0.06, tp / 2 + 0.004, ww + 2 * tw + 0.05, 0.12, tp + 0.008, new O { grain = 0, skip = 32, tint = trim, data = td() } );
			B.box( "wood", cx, sillY + wh + 0.13, 0.04, ww + 2 * tw + 0.1, 0.025, 0.075, new O { grain = 0, tint = trim, data = td() } );
			B.box( "wood", cx, sillY - 0.025, 0.045, ww + 2 * tw + 0.08, 0.045, 0.09, new O { grain = 0, rx = 0.1, tint = trim, data = td() } );

			if ( st.closed )
			{
				// closed board shutters instead of glass
				foreach ( int s in new[] { - 1, 1 } )
				{
					B.box( "wood", cx + s * ww / 4, sillY + wh / 2, 0.018, ww / 2 - 0.006, wh, 0.03, new O { grain = 0, tint = st.accent, data = new[] { rand.next(), st.paint, st.closedPattern ?? 3, st.weather } } );
				}

				return null;
			}

			// sash frame, glass, muntins
			B.box( "wood", cx, sillY + 0.03, 0.012, ww, 0.06, 0.024, new O { grain = 0, skip = 35, tint = trim, data = td() } );
			B.box( "wood", cx, sillY + wh - 0.03, 0.012, ww, 0.06, 0.024, new O { grain = 0, skip = 35, tint = trim, data = td() } );
			B.box( "wood", cx, sillY + wh / 2, 0.016, ww, 0.045, 0.03, new O { grain = 0, skip = 35, tint = trim, data = td() } );
			B.box( "wood", cx - ww / 2 + 0.025, sillY + wh / 2, 0.012, 0.05, wh, 0.024, new O { grain = 1, skip = 44, tint = trim, data = td() } );
			B.box( "wood", cx + ww / 2 - 0.025, sillY + wh / 2, 0.012, 0.05, wh, 0.024, new O { grain = 1, skip = 44, tint = trim, data = td() } );
			B.box( "wood", cx, sillY + wh / 2, 0.01, 0.022, wh - 0.1, 0.018, new O { grain = 1, skip = 44, tint = trim, data = td() } );
			int lit = rand.chance( st.litChance ) ? 1 : 0;
			B.part( "glass", quad01Part( ww - 0.05, wh - 0.05 ), cx, sillY + wh / 2, 0.004, new O { tint = st.curtain, data = new[] { rand.next(), 0, ( double ) lit, 0 } } );

			if ( st.shutters == "louver" || st.shutters == "board" )
			{
				double sw = ww / 2 + 0.03, sh = wh + 0.05;
				int pat = st.shutters == "louver" ? 4 : 3;
				foreach ( int s in new[] { - 1, 1 } )
				{
					double hx = cx + s * ( ww / 2 + tw );
					double open = rand.range( 0.02, 0.45 );
					B.pushAt( hx, 0, 0.03, - s * open );
					B.box( "wood", s * ( sw / 2 + 0.01 ), sillY + wh / 2, 0.016, sw, sh, 0.028, new O { grain = 0, tint = st.accent, data = new[] { rand.next(), st.paint, ( double ) pat, st.weather } } );
					if ( pat == 4 )
					{
						foreach ( double yy in new[] { sillY + wh / 2 - sh / 2 + 0.035, sillY + wh / 2 + sh / 2 - 0.035 } )
						{
							B.box( "wood", s * ( sw / 2 + 0.01 ), yy, 0.033, sw, 0.06, 0.012, new O { grain = 0, skip = 32, tint = st.accent, data = new[] { rand.next(), st.paint, 0, st.weather } } );
						}

						foreach ( double xx in new[] { 0.03, sw - 0.03 } )
						{
							B.box( "wood", s * ( xx + 0.01 ), sillY + wh / 2, 0.033, 0.055, sh - 0.12, 0.012, new O { grain = 1, skip = 44, tint = st.accent, data = new[] { rand.next(), st.paint, 0, st.weather } } );
						}
					}

					B.pop();
				}
			}
			else if ( st.shutters == "bahama" )
			{
				double bw = ww + 2 * tw + 0.08, bh = wh + 0.12;
				double hy = sillY + wh + 0.14;
				double ang = rand.range( 0.45, 0.75 );
				B.pushAt( cx, hy, 0.05, 0, - ang );
				B.box( "wood", 0, - bh / 2, 0.015, bw, bh, 0.03, new O { grain = 0, tint = st.accent, data = new[] { rand.next(), st.paint, 4, st.weather } } );
				B.box( "wood", 0, - bh + 0.03, 0.035, bw, 0.06, 0.015, new O { grain = 0, tint = st.accent, data = new[] { rand.next(), st.paint, 0, st.weather } } );
				B.pop();
				// prop sticks
				double tipY = hy - Math.Cos( ang ) * bh, tipZ = 0.05 + Math.Sin( ang ) * bh;
				foreach ( int s in new[] { - 1, 1 } )
				{
					B.rod( "wood", new[] { cx + s * ( bw / 2 - 0.08 ), sillY - 0.02, 0.07 }, new[] { cx + s * ( bw / 2 - 0.08 ), tipY + 0.02, tipZ - 0.02 }, 0.012, 0.012, new O { segs = 4, data = WOOD( rand.next(), 0.8 ) } );
				}
			}

			return lit != 0 ? new[] { cx, sillY + wh / 2 } : null;
		}

		static void doorUnit( Builder B, Rand rand, double cx, double floorY, Style st )
		{
			double dw = 0.92, dh = 2.08, tw = 0.09;
			var trim = st.trim;
			double[] td() => new[] { rand.next(), st.trimPaint, 0, st.weather };
			B.box( "wood", cx, floorY + dh / 2, 0.012, dw, dh, 0.045, new O { grain = 1, tint = st.accent, data = new[] { rand.next(), st.paint, 3, st.weather } } );
			B.box( "wood", cx - dw / 2 - tw / 2, floorY + dh / 2 + 0.02, 0.02, tw, dh + 0.04, 0.04, new O { grain = 1, tint = trim, data = td() } );
			B.box( "wood", cx + dw / 2 + tw / 2, floorY + dh / 2 + 0.02, 0.02, tw, dh + 0.04, 0.04, new O { grain = 1, tint = trim, data = td() } );
			B.box( "wood", cx, floorY + dh + 0.1, 0.024, dw + 2 * tw + 0.06, 0.14, 0.048, new O { grain = 0, tint = trim, data = td() } );
			B.box( "wood", cx, floorY + dh + 0.18, 0.045, dw + 2 * tw + 0.12, 0.025, 0.09, new O { grain = 0, tint = trim, data = td() } );
			B.box( "wood", cx, floorY + 0.015, 0.05, dw + 0.12, 0.03, 0.1, new O { grain = 0, data = WOOD( rand.next(), 0.7 ) } );
			if ( st.doorGlass )
			{
				B.box( "wood", cx, floorY + 1.55, 0.04, 0.56, 0.5, 0.012, new O { grain = 0, tint = trim, data = td() } );
				B.part( "glass", quad01Part( 0.46, 0.4 ), cx, floorY + 1.55, 0.047, new O { tint = st.curtain, data = new[] { rand.next(), 0, rand.chance( 0.5 ) ? 1.0 : 0.0, 0 } } );
				B.box( "wood", cx, floorY + 1.55, 0.049, 0.02, 0.4, 0.006, new O { grain = 1, tint = trim, data = td() } );
			}

			B.lathe( "hard", cx + dw / 2 - 0.1, floorY + 0.98, 0.035, new[] { new[] { 0.0, 0 }, new[] { 0.012, 0 }, new[] { 0.012, 0.03 }, new[] { 0.028, 0.045 }, new[] { 0.0, 0.065 } }, new O { segs = 8, rx = Math.PI / 2, tint = C.brass, data = HARD( rand.next(), 0.1, 0.9, 0.35 ) } );
		}

		sealed class StairResult { public double g, run; public int n; }

		// Straight stair out along +z from (cx, topY, zs) down to the terrain. Registers walkable colliders.
		static StairResult stairRun( BuildCtx ctx, Frame fr, double cx, double zs, double topY, double width, Style st )
		{
			var B = ctx.B; var rand = ctx.rand;
			double tread = 0.28, maxRise = 0.2;
			double g = fr.gAt( cx, zs + 0.8 );
			int n = ( int ) Math.Max( 1, Math.Ceiling( ( topY - g ) / maxRise ) );
			g = fr.gAt( cx, zs + n * tread );
			n = ( int ) Math.Max( 1, Math.Ceiling( ( topY - g ) / maxRise ) );
			g = Math.Min( g, fr.gAt( cx, zs + n * tread ) );
			double rh = ( topY - g ) / n;
			for ( int k = 1; k < n; k ++ )
			{
				double top = topY - k * rh;
				double zc = zs + ( k - 0.5 ) * tread;
				B.box( "wood", cx, top - 0.02, zc, width, 0.04, tread + 0.02, new O { grain = 0, tint = st.stepTint ?? WHITE3, data = WOOD( rand.next(), 0.75, st.stepPaint, 0 ) } );
				double gb = Math.Min( fr.gAt( cx, zc ), top - 0.1 );
				fr.addBoxL( cx, ( top + gb - 0.4 ) / 2, zc, width / 2, ( top - gb + 0.4 ) / 2, tread / 2 + 0.005, true, true, "stairs" );
			}

			double run = n * tread;
			foreach ( int s in new[] { - 1, 1 } )
			{
				double sx = cx + s * ( width / 2 + 0.025 );
				B.beam( "wood", new[] { sx, topY - 0.1, zs - 0.02 }, new[] { sx, g - 0.02, zs + run + 0.05 }, 0.05, 0.24, new O { tint = st.trim, data = WOOD( rand.next(), 0.75, st.trimPaint * 0.8, 0 ) } );
				if ( n >= 5 )
				{
					// handrail for taller stairs
					B.box( "wood", sx, g + 0.5, zs + run - 0.1, 0.07, 1.0 + 0.3, 0.07, new O { grain = 1, tint = st.trim, data = WOOD( rand.next(), 0.75, st.trimPaint, 0 ) } );
					B.beam( "wood", new[] { sx, topY + 0.9, zs + 0.05 }, new[] { sx, g + 0.95, zs + run - 0.1 }, 0.06, 0.05, new O { tint = st.trim, data = WOOD( rand.next(), 0.75, st.trimPaint, 0 ) } );
				}

				fr.checks.Add( fr.worldPt( sx, g - 0.02, zs + run ) );
			}

			return new StairResult { g = g, run = run, n = n };
		}

		static Frame makeFrame( BuildCtx ctx, double yaw, Func<double, double, double[]> toW, Func<double, double, double> gAt )
		{
			var colliders = ctx.colliders;
			var fr = new Frame { toW = toW, gAt = gAt, checks = ctx.checks };
			fr.worldPt = ( lx, y, lz ) => { var p = toW( lx, lz ); return new FoundationCheck { x = p[ 0 ], y = y, z = p[ 1 ] }; };
			fr.addBoxL = ( lx, ly, lz, hx, hy, hz, walkable, solid, tag ) =>
			{
				var p = toW( lx, lz );
				colliders.addBox( new Vector3( p[ 0 ], ly, p[ 1 ] ), new Vector3( hx, hy, hz ), yaw, walkable, solid, tag );
			};
			return fr;
		}

		// ---------------------------------------------------------------------------
		// House / cottage / stilt hut

		public static BuildResult buildHouse( BuildCtx ctx, Spec s )
		{
			var B = ctx.B; var terrain = ctx.terrain; var colliders = ctx.colliders; var rand = ctx.rand; var lights = ctx.lights; var checks = ctx.checks;
			double x = s.x, z = s.z, yaw = s.yaw, w = s.w.Value, d = s.d.Value;
			frameFns( terrain, x, z, yaw, out var toW, out var gAt );
			var fr = makeFrame( ctx, yaw, toW, gAt );

			int stories = s.stories != 0 ? s.stories : 1;
			double storyH = s.storyH.HasValue && s.storyH.Value != 0 ? s.storyH.Value : ( stories > 1 ? 2.75 : 2.95 );
			double H = stories * storyH;
			double t = 0.14;
			var porch = s.porch;
			double pd = porch != null ? porch.depth : 0;
			double pw = porch != null ? Math.Min( w, porch.width.HasValue && porch.width.Value != 0 ? porch.width.Value : w ) : 0;
			double pcx = porch != null ? ( porch.offset ?? 0 ) : 0;

			double annexD = s.annex != null ? ( s.annex.d ?? 2.2 ) : 0;
			double gMin = double.PositiveInfinity, gMax = double.NegativeInfinity;
			for ( int i = 0; i <= 4; i ++ )
				for ( int j = 0; j <= 6; j ++ )
				{
					double g = gAt( ( i / 4.0 - 0.5 ) * w, - d / 2 - annexD + j / 6.0 * ( d + pd + annexD ) );
					gMin = Math.Min( gMin, g );
					gMax = Math.Max( gMax, g );
				}

			double floorY = s.floorY ?? ( gMax + ( s.clearance ?? 0.55 ) );
			double yE = floorY + H;

			var st = new Style
			{
				trim = s.trim, accent = s.accent, curtain = s.curtain ?? lin( 0xd8c8a8 ),
				paint = s.paint ?? 0.7, trimPaint = Math.Min( 1, ( s.paint ?? 0.7 ) + 0.16 ), weather = s.weather ?? 0.6,
				shutters = s.shutters ?? "louver", doorGlass = s.doorGlass, litChance = s.litChance ?? 0.6,
				stepTint = s.porchPaint, stepPaint = s.porchPaint != null ? 0.5 : 0,
			};
			double siding = s.siding ?? 1;
			double[] wallData() => new[] { rand.next(), st.paint, siding, st.weather };
			double[] trimData() => new[] { rand.next(), st.trimPaint, 0, st.weather };
			double gableSiding = siding == 1 ? 2 : 1;

			B.pushAt( x, 0, z, yaw );

			// ------------------------------------------------------------- foundation
			double rimH = 0.24;
			string fnd = s.foundation ?? "posts";
			if ( fnd == "stone" )
			{
				double bot = gMin - 0.35, top = floorY - rimH + 0.03;
				double hh = top - bot, sT = 0.32;
				var tint = s.plaster ?? lin( 0xe6dfcf );
				double[] sd() => new[] { rand.next(), s.stoneStyle ?? 1, 0, 0 };
				B.box( "stone", 0, bot + hh / 2, d / 2 - sT / 2 + 0.03, w + 0.06, hh, sT, new O { grain = 0, tint = tint, data = sd() } );
				B.box( "stone", 0, bot + hh / 2, - d / 2 + sT / 2 - 0.03, w + 0.06, hh, sT, new O { grain = 0, tint = tint, data = sd() } );
				B.box( "stone", w / 2 - sT / 2 + 0.03, bot + hh / 2, 0, sT, hh, d - 2 * sT + 0.06, new O { grain = 2, tint = tint, data = sd() } );
				B.box( "stone", - w / 2 + sT / 2 - 0.03, bot + hh / 2, 0, sT, hh, d - 2 * sT + 0.06, new O { grain = 2, tint = tint, data = sd() } );
				foreach ( var c in new[] { new[] { - w / 2, - d / 2 }, new[] { w / 2, - d / 2 }, new[] { w / 2, d / 2 }, new[] { - w / 2, d / 2 }, new[] { 0.0, d / 2 }, new[] { 0.0, - d / 2 } } ) checks.Add( fr.worldPt( c[ 0 ], bot, c[ 1 ] ) );
			}
			else
			{
				bool stilts = fnd == "stilts";
				int nx = ( int ) Math.Max( 2, Math.Ceiling( w / 2.1 ) + 1 ), nz = ( int ) Math.Max( 2, Math.Ceiling( d / 2.1 ) + 1 );
				var xs = linspace( - w / 2 + 0.14, w / 2 - 0.14, nx ); var zs = linspace( - d / 2 + 0.14, d / 2 - 0.14, nz );
				var pts = new List<double[]>();
				for ( int i = 0; i < nx; i ++ )
					for ( int j = 0; j < nz; j ++ )
					{
						bool edge = i == 0 || j == 0 || i == nx - 1 || j == nz - 1;
						if ( ! edge && ! stilts && ( i + j ) % 2 != 0 ) continue;
						pts.Add( new[] { xs[ i ], zs[ j ], edge ? 1.0 : 0.0 } );
					}

				foreach ( var pt in pts )
				{
					double lx = pt[ 0 ], lz = pt[ 1 ]; bool edge = pt[ 2 ] != 0;
					double g = gAt( lx, lz );
					double top = floorY - rimH + 0.02;
					if ( stilts )
					{
						double bottom = g - 0.9;
						double rxx = rand.range( - 0.015, 0.015 ), rzz = rand.range( - 0.015, 0.015 );
						double ws = rand.next(), wh = rand.range( 0.75, 1 );
						B.cyl( "wood", lx, bottom, lz, 0.1, 0.12, top - bottom, new O { segs = 8, rx = rxx, rz = rzz, tint = new[] { 0.95, 0.92, 0.9 }, data = WOOD( ws, wh ) } );
						var p = toW( lx, lz );
						colliders.addCylinder( p[ 0 ], p[ 1 ], 0.14, bottom, top, "stilt" );
						checks.Add( new FoundationCheck { x = p[ 0 ], y = bottom, z = p[ 1 ] } );
					}
					else if ( edge || ( s.clearance ?? 0.55 ) > 0.3 )
					{
						double bottom = g - 0.3;
						B.box( "wood", lx, ( bottom + top ) / 2, lz, 0.16, top - bottom, 0.16, new O { grain = 1, data = WOOD( rand.next(), 0.85 ) } );
						B.box( "stone", lx, g - 0.06, lz, 0.34, 0.26, 0.34, new O { grain = 0, tint = lin( 0xcfc8b8 ), data = new[] { rand.next(), 0, 0, 0 } } );
						checks.Add( fr.worldPt( lx, g - 0.19, lz ) );
					}
				}

				if ( stilts )
				{
					// cross bracing around the perimeter
					double tall = floorY - gMin;
					if ( tall > 1.2 )
					{
						double yT = floorY - rimH - 0.15;
						foreach ( var e in new[] {
							new[] { xs[ 0 ], zs[ 0 ], xs[ nx - 1 ], zs[ 0 ] }, new[] { xs[ 0 ], zs[ nz - 1 ], xs[ nx - 1 ], zs[ nz - 1 ] },
							new[] { xs[ 0 ], zs[ 0 ], xs[ 0 ], zs[ nz - 1 ] }, new[] { xs[ nx - 1 ], zs[ 0 ], xs[ nx - 1 ], zs[ nz - 1 ] } } )
						{
							double ax = e[ 0 ], az = e[ 1 ], bx = e[ 2 ], bz = e[ 3 ];
							double ga = gAt( ax, az ), gb = gAt( bx, bz );
							double yB = Math.Max( ga, gb ) + 0.35;
							if ( yT - yB < 0.8 ) continue;
							double nxv = az == bz ? 0 : ( ax < 0 ? - 1 : 1 ), nzv = az == bz ? ( az < 0 ? - 1 : 1 ) : 0;
							double o = 0.13;
							B.beam( "wood", new[] { ax + nxv * o, yT, az + nzv * o }, new[] { bx + nxv * o, yB, bz + nzv * o }, 0.045, 0.16, new O { data = WOOD( rand.next(), 0.9 ) } );
							B.beam( "wood", new[] { ax + nxv * o * 1.4, yB, az + nzv * o * 1.4 }, new[] { bx + nxv * o * 1.4, yT, bz + nzv * o * 1.4 }, 0.045, 0.16, new O { data = WOOD( rand.next(), 0.9 ) } );
						}
					}
				}
			}

			// rim band + floor edge
			double rimY = floorY - rimH / 2;
			var rimTint = s.rimRaw ? WHITE3 : st.trim;
			double[] rimData() => new[] { rand.next(), s.rimRaw ? 0 : st.trimPaint, 0, st.weather };
			B.box( "wood", 0, rimY, d / 2 + 0.01, w + 0.08, rimH, 0.06, new O { grain = 0, tint = rimTint, data = rimData() } );
			B.box( "wood", 0, rimY, - d / 2 - 0.01, w + 0.08, rimH, 0.06, new O { grain = 0, tint = rimTint, data = rimData() } );
			B.box( "wood", w / 2 + 0.01, rimY, 0, 0.06, rimH, d + 0.02, new O { grain = 2, tint = rimTint, data = rimData() } );
			B.box( "wood", - w / 2 - 0.01, rimY, 0, 0.06, rimH, d + 0.02, new O { grain = 2, tint = rimTint, data = rimData() } );

			// ------------------------------------------------------------- walls & trim
			double wy = floorY + H / 2;
			B.box( "wood", 0, wy, d / 2 - t / 2, w, H, t, new O { grain = 0, tint = s.wall, data = wallData() } );
			B.box( "wood", 0, wy, - d / 2 + t / 2, w, H, t, new O { grain = 0, tint = s.wall, data = wallData() } );
			B.box( "wood", w / 2 - t / 2, wy, 0, t, H, d - 2 * t, new O { grain = 2, tint = s.wall, data = wallData() } );
			B.box( "wood", - w / 2 + t / 2, wy, 0, t, H, d - 2 * t, new O { grain = 2, tint = s.wall, data = wallData() } );
			foreach ( int sx in new[] { - 1, 1 } )
				foreach ( int sz in new[] { - 1, 1 } )
				{
					B.box( "wood", sx * ( w / 2 - 0.05 ), wy, sz * ( d / 2 - 0.05 ), 0.135, H + 0.01, 0.135, new O { grain = 1, tint = st.trim, data = trimData() } );
				}

			// frieze under the eaves and belt board between storeys
			void band( double y, double h, double proud )
			{
				B.box( "wood", 0, y, d / 2 + proud / 2, w + 0.02, h, proud, new O { grain = 0, tint = st.trim, data = trimData() } );
				B.box( "wood", 0, y, - d / 2 - proud / 2, w + 0.02, h, proud, new O { grain = 0, tint = st.trim, data = trimData() } );
				B.box( "wood", w / 2 + proud / 2, y, 0, proud, h, d + 0.02, new O { grain = 2, tint = st.trim, data = trimData() } );
				B.box( "wood", - w / 2 - proud / 2, y, 0, proud, h, d + 0.02, new O { grain = 2, tint = st.trim, data = trimData() } );
			}

			band( yE - 0.1, 0.2, 0.025 );
			if ( stories > 1 ) band( floorY + storyH, 0.16, 0.03 );

			// ------------------------------------------------------------- roof
			string roofType = s.roof ?? "gable";
			bool isThatch = s.roofMat == "thatch";
			double a = s.pitch ?? ( isThatch ? 0.68 : 0.44 );
			double ta = Math.Tan( a );
			double r0 = 0.12;
			double T = isThatch ? 0.28 : 0.06;
			double ovE = s.ovE ?? ( isThatch ? 0.6 : 0.45 );
			double ovR = s.ovR ?? ( isThatch ? 0.45 : 0.32 );
			string roofKey = isThatch ? "thatch" : "roofMetal";
			var roofTint = isThatch ? WHITE3 : ( s.roofColor ?? lin( 0xa5452f ) );
			double thatchAge = s.thatchAge ?? 0.4, rust = s.rust ?? 0.4;
			double galv = s.galv ? 1 : 0;
			double[] roofData() => isThatch ? new[] { rand.next(), thatchAge, 0, 0 } : new[] { rand.next(), rust, galv, 0 };
			void roofSlab( Vector3[] pts, Vector3 uDir ) => B.slab( roofKey, pts, T, new O { uDir = uDir.normalize(), up = UP, tint = roofTint, data = roofData() } );
			double Tv = T / Math.Cos( a );
			double roofTop = yE;
			Ridge ridge = null;

			void fascia( double ax, double ay, double az, double bx, double by, double bz )
			{
				if ( isThatch ) return;
				B.beam( "wood", new[] { ax, ay - 0.1, az }, new[] { bx, by - 0.1, bz }, 0.032, 0.2, new O { tint = st.trim, data = trimData() } );
			}

			void rafterTails( double zWall, double zEdge, double sign, double yWallTop, double yEdgeTop, double x0, double x1 )
			{
				int n = ( int ) Math.Floor( ( x1 - x0 ) / 0.6 );
				for ( int i = 0; i <= n; i ++ )
				{
					double px = x0 + ( x1 - x0 ) * i / Math.Max( 1, n );
					B.beam( "wood", new[] { px, yWallTop - Tv - 0.055, zWall - sign * 0.08 }, new[] { px, yEdgeTop - Tv - 0.055, zEdge - sign * 0.05 }, 0.05, 0.1, new O { data = WOOD( rand.next(), 0.7 ) } );
				}
			}

			double gableEnd( double cx, double cz, double ry, double bas )
			{
				double hSide = r0 - 0.03;
				double hApex = r0 + ( bas / 2 ) * ta - 0.03;
				B.part( "wood", gableEndPart( bas, hSide, hApex, t ), cx, yE, cz, new O { ry = ry, tint = s.wall, data = new[] { rand.next(), st.paint, gableSiding, st.weather } } );
				return hApex;
			}

			void gableVent( double cz, double dir, double yBase, double ry )
			{
				// small louvered vent in the gable triangle
				B.pushAt( 0, 0, cz, ry );
				double vy = yBase + 0.45;
				B.box( "wood", 0, vy, 0.02, 0.5, 0.5, 0.04, new O { grain = 0, tint = st.trim, data = trimData() } );
				B.box( "wood", 0, vy, 0.03, 0.38, 0.38, 0.03, new O { grain = 0, tint = st.accent, data = new[] { rand.next(), st.paint, 4, st.weather } } );
				B.pop();
			}

			void ridgeCap( Vector3 p0, Vector3 p1, Vector3 across )
			{
				if ( isThatch )
				{
					B.rod( "thatch", new[] { p0.x, p0.y - 0.06, p0.z }, new[] { p1.x, p1.y - 0.06, p1.z }, 0.2, 0.2, new O { segs = 8, tint = new[] { 0.9, 0.88, 0.85 }, data = new[] { rand.next(), thatchAge + 0.2, 0, 0 } } );
					return;
				}

				double wc = 0.2, drop = wc * Math.Tan( a * 0.85 );
				foreach ( int sg in new[] { - 1, 1 } )
				{
					var o = across.clone().multiplyScalar( sg * wc );
					var pts = new[] { V3( p0.x, p0.y + 0.02, p0.z ), V3( p1.x, p1.y + 0.02, p1.z ), V3( p1.x + o.x, p1.y + 0.02 - drop, p1.z + o.z ), V3( p0.x + o.x, p0.y + 0.02 - drop, p0.z + o.z ) };
					var ud = o.clone().normalize().negate(); ud.y = 0.3;
					B.slab( "roofMetal", pts, 0.01, new O { up = UP, uDir = ud, tint = roofTint, data = new[] { rand.next(), rust + 0.2, galv, 0 } } );
				}
			}

			bool hasPorchEave = porch != null && stories == 1 && roofType != "gableFront";

			if ( roofType == "gable" )
			{
				double X = w / 2 + ovR;
				double yR = yE + r0 + ( d / 2 ) * ta;
				double zf = d / 2 + ovE, yF = yE + r0 - ovE * ta;
				roofSlab( new[] { V3( - X, yF, zf ), V3( X, yF, zf ), V3( X, yR, 0 ), V3( - X, yR, 0 ) }, V3( 0, yR - yF, - zf ) );
				roofSlab( new[] { V3( X, yF, - zf ), V3( - X, yF, - zf ), V3( - X, yR, 0 ), V3( X, yR, 0 ) }, V3( 0, yR - yF, zf ) );
				ridgeCap( V3( - X, yR, 0 ), V3( X, yR, 0 ), V3( 0, 0, 1 ) );
				foreach ( int sx in new[] { - 1, 1 } )
				{
					gableEnd( sx * ( w / 2 - t / 2 ), 0, Math.PI / 2, d );
					fascia( sx * ( X - 0.016 ), yF, zf, sx * ( X - 0.016 ), yR, 0 );
					fascia( sx * ( X - 0.016 ), yF, - zf, sx * ( X - 0.016 ), yR, 0 );
				}

				fascia( - X, yF, zf - 0.016, X, yF, zf - 0.016 );
				fascia( - X, yF, - zf + 0.016, X, yF, - zf + 0.016 );
				if ( ! isThatch )
				{
					rafterTails( d / 2, zf, 1, yE + r0, yF, - w / 2 + 0.1, w / 2 - 0.1 );
					rafterTails( - d / 2, - zf, - 1, yE + r0, yF, - w / 2 + 0.1, w / 2 - 0.1 );
				}

				roofTop = yR + 0.2;
				ridge = new Ridge { y = yR, axis = 'x', ta = ta, yF = yF, zf = zf };
			}
			else if ( roofType == "gableFront" )
			{
				double Z = d / 2 + ovR;
				double yR = yE + r0 + ( w / 2 ) * ta;
				double xe = w / 2 + ovE, yF = yE + r0 - ovE * ta;
				roofSlab( new[] { V3( xe, yF, Z ), V3( xe, yF, - Z ), V3( 0, yR, - Z ), V3( 0, yR, Z ) }, V3( - xe, yR - yF, 0 ) );
				roofSlab( new[] { V3( - xe, yF, - Z ), V3( - xe, yF, Z ), V3( 0, yR, Z ), V3( 0, yR, - Z ) }, V3( xe, yR - yF, 0 ) );
				ridgeCap( V3( 0, yR, - Z ), V3( 0, yR, Z ), V3( 1, 0, 0 ) );
				foreach ( int sz in new[] { - 1, 1 } )
				{
					double hA = gableEnd( 0, sz * ( d / 2 - t / 2 ), sz > 0 ? 0 : Math.PI, w );
					if ( hA > 1.0 && ! s.noVent ) gableVent( sz * ( d / 2 ), sz, yE, sz > 0 ? 0 : Math.PI );
					fascia( xe, yF, sz * ( Z - 0.016 ), 0, yR, sz * ( Z - 0.016 ) );
					fascia( - xe, yF, sz * ( Z - 0.016 ), 0, yR, sz * ( Z - 0.016 ) );
				}

				fascia( xe - 0.016, yF, - Z, xe - 0.016, yF, Z );
				fascia( - xe + 0.016, yF, - Z, - xe + 0.016, yF, Z );
				roofTop = yR + 0.2;
				ridge = new Ridge { y = yR, axis = 'z', ta = ta, yF = yF, xe = xe };
			}
			else
			{
				// hip roof (expects w >= d)
				double ov = ovE;
				double yR = yE + r0 + ( d / 2 ) * ta;
				double ye = yE + r0 - ov * ta;
				double XE = w / 2 + ov, ZE = d / 2 + ov, XR = Math.Max( 0, ( w - d ) / 2 );
				if ( XR > 0.02 )
				{
					roofSlab( new[] { V3( - XE, ye, ZE ), V3( XE, ye, ZE ), V3( XR, yR, 0 ), V3( - XR, yR, 0 ) }, V3( 0, yR - ye, - ZE ) );
					roofSlab( new[] { V3( XE, ye, - ZE ), V3( - XE, ye, - ZE ), V3( - XR, yR, 0 ), V3( XR, yR, 0 ) }, V3( 0, yR - ye, ZE ) );
					ridgeCap( V3( - XR, yR, 0 ), V3( XR, yR, 0 ), V3( 0, 0, 1 ) );
				}
				else
				{
					roofSlab( new[] { V3( - XE, ye, ZE ), V3( XE, ye, ZE ), V3( 0, yR, 0 ) }, V3( 0, yR - ye, - ZE ) );
					roofSlab( new[] { V3( XE, ye, - ZE ), V3( - XE, ye, - ZE ), V3( 0, yR, 0 ) }, V3( 0, yR - ye, ZE ) );
				}

				roofSlab( new[] { V3( XE, ye, ZE ), V3( XE, ye, - ZE ), V3( XR, yR, 0 ) }, V3( - XE + XR, yR - ye, 0 ) );
				roofSlab( new[] { V3( - XE, ye, - ZE ), V3( - XE, ye, ZE ), V3( - XR, yR, 0 ) }, V3( XE - XR, yR - ye, 0 ) );
				// hip rolls
				foreach ( int sx in new[] { - 1, 1 } )
					foreach ( int sz in new[] { - 1, 1 } )
					{
						var p0 = new[] { sx * XE, ye + 0.02, sz * ZE }; var p1 = new[] { sx * XR, yR + 0.02, 0 };
						if ( isThatch ) B.rod( "thatch", new[] { p0[ 0 ], p0[ 1 ] - 0.05, p0[ 2 ] }, new[] { p1[ 0 ], p1[ 1 ] - 0.05, p1[ 2 ] }, 0.14, 0.17, new O { segs = 6, tint = new[] { 0.9, 0.88, 0.85 }, data = new[] { rand.next(), thatchAge + 0.2, 0, 0 } } );
						else B.rod( "roofMetal", p0, p1, 0.045, 0.045, new O { segs = 6, tint = roofTint, data = new[] { rand.next(), rust + 0.2, galv, 0 } } );
					}

				fascia( - XE, ye, ZE - 0.016, XE, ye, ZE - 0.016 );
				fascia( - XE, ye, - ZE + 0.016, XE, ye, - ZE + 0.016 );
				fascia( XE - 0.016, ye, - ZE, XE - 0.016, ye, ZE );
				fascia( - XE + 0.016, ye, - ZE, - XE + 0.016, ye, ZE );
				if ( ! isThatch )
				{
					rafterTails( d / 2, ZE, 1, yE + r0, ye, - w / 2 + 0.1, w / 2 - 0.1 );
					rafterTails( - d / 2, - ZE, - 1, yE + r0, ye, - w / 2 + 0.1, w / 2 - 0.1 );
				}

				roofTop = yR + 0.2;
				ridge = new Ridge { y = yR, axis = 'x', ta = ta, yF = ye, zf = ZE };
			}

			// thatch eave fringe (ragged strands hanging below the eave edges)
			if ( isThatch )
			{
				void fringe( double ax, double az, double bx, double bz, double yTop, double outX, double outZ )
				{
					double L = JS.Hypot( bx - ax, bz - az );
					int n = ( int ) Math.Max( 4, JS.Round( L / 0.05 ) );
					var part = gridPart( n, 1, ( i, j ) =>
					{
						double tt = ( double ) i / n;
						double r = Math.Abs( Math.Sin( i * 12.9898 + ax * 78.233 + az * 37.719 ) * 43758.5453 ) % 1;
						double hang = 0.04 + 0.16 * r * r * ( i % 2 != 0 ? 1 : 0.6 );
						double px = ax + ( bx - ax ) * tt, pz = az + ( bz - az ) * tt;
						double yy = j == 0 ? yTop - T * 0.95 - hang : yTop - T * 0.3;
						double o = j == 0 ? 0.05 : 0.0;
						return new GridVertex( px + outX * o, yy, pz + outZ * o, outX, 0.2, outZ, j == 0 ? - hang : 0.2, tt * L );
					} );
					B.add( "thatch", part, new Matrix4(), new[] { 0.95, 0.92, 0.88 }, new[] { rand.next(), thatchAge + 0.1, 0, 0 } );
				}

				if ( roofType == "gable" )
				{
					double X = w / 2 + ovR, zf = d / 2 + ovE, yF = yE + r0 - ovE * ta;
					fringe( - X, zf, X, zf, yF, 0, 1 );
					fringe( X, - zf, - X, - zf, yF, 0, - 1 );
				}
				else if ( roofType == "gableFront" )
				{
					double Z = d / 2 + ovR, xe = w / 2 + ovE, yF = yE + r0 - ovE * ta;
					fringe( xe, Z, xe, - Z, yF, 1, 0 );
					fringe( - xe, - Z, - xe, Z, yF, - 1, 0 );
				}
				else
				{
					double XE = w / 2 + ovE, ZE = d / 2 + ovE, ye = yE + r0 - ovE * ta;
					fringe( - XE, ZE, XE, ZE, ye, 0, 1 );
					fringe( XE, - ZE, - XE, - ZE, ye, 0, - 1 );
					fringe( XE, ZE, XE, - ZE, ye, 1, 0 );
					fringe( - XE, - ZE, - XE, ZE, ye, - 1, 0 );
				}
			}

			// ------------------------------------------------------------- openings
			double winW = s.winW ?? 0.86, winH = s.winH ?? 1.2;
			double doorX = s.doorX ?? 0;
			var litWindows = new List<Vector3>();
			List<double> slots( double L, double[] avoid, double spacing = 1.9 )
			{
				int n = ( int ) Math.Max( 1, Math.Floor( ( L - 0.9 ) / spacing ) );
				var o = new List<double>();
				for ( int i = 0; i < n; i ++ )
				{
					double px = - L / 2 + L * ( i + 0.5 ) / n;
					bool ok = true; foreach ( var q in avoid ) if ( ! ( Math.Abs( px - q ) > 1.05 ) ) { ok = false; break; }
					if ( ok ) o.Add( px );
				}

				return o;
			}

			var walls = new[]
			{
				new { L = w, fx = 0.0, fz = d / 2, ry = 0.0, front = true },
				new { L = w, fx = 0.0, fz = - d / 2, ry = Math.PI, front = false },
				new { L = d, fx = w / 2, fz = 0.0, ry = Math.PI / 2, front = false },
				new { L = d, fx = - w / 2, fz = 0.0, ry = - Math.PI / 2, front = false },
			};
			// keep side-wall windows clear of an external chimney (wall-local x of the chimney center)
			double chimZ = ( s.roof ?? "gable" ) == "gableFront" ? - d * 0.25 : 0;

			for ( int wi = 0; wi < 4; wi ++ )
			{
				var wl = walls[ wi ];
				B.pushAt( wl.fx, 0, wl.fz, wl.ry );
				for ( int sIdx = 0; sIdx < stories; sIdx ++ )
				{
					double baseY = floorY + sIdx * storyH;
					List<double> xs;
					if ( wl.front ) xs = sIdx == 0 ? slots( wl.L, new[] { doorX } ) : slots( wl.L, new double[ 0 ] );
					else if ( wi == 1 ) xs = slots( wl.L, new double[ 0 ], 2.3 );
					else xs = slots( wl.L, wi == 2 && s.chimney == 1 ? new[] { - chimZ } : ( wi == 3 && s.chimney == - 1 ? new[] { chimZ } : new double[ 0 ] ), 2.2 );
					if ( ! wl.front && s.fewWindows && xs.Count > 1 ) xs = new List<double> { xs[ ( int ) Math.Floor( rand.next() * xs.Count ) ] };
					foreach ( double px in xs )
					{
						bool closed = rand.chance( s.closedChance ?? 0.12 );
						var wst = st.Clone(); wst.closed = closed; wst.closedPattern = st.shutters == "louver" ? 4 : 3;
						var lit = windowUnit( B, rand, px, baseY + 0.88, winW, winH, wst );
						if ( lit != null && wl.front ) litWindows.Add( B.toWorld( lit[ 0 ], lit[ 1 ], 0.35 ) );
					}
				}

				if ( wl.front )
				{
					doorUnit( B, rand, doorX, floorY, st );
					if ( s.lantern != false )
					{
						var lw = wallLantern( B, doorX + 0.78, floorY + 1.95, 0.0, rand.next() );
						lights.Add( new LightSource { position = lw, color = WarmColor(), intensity = 3.5, kind = "lantern" } );
					}
				}

				B.pop();
			}

			if ( litWindows.Count > 0 ) lights.Add( new LightSource { position = litWindows[ 0 ], color = new Color( 1.0, 0.62, 0.32 ), intensity = 1.6, kind = "window" } );

			// ------------------------------------------------------------- porch or stoop
			var porchPaint = s.porchPaint;
			if ( porch != null )
			{
				double porchY = floorY - 0.05;
				double pz0 = d / 2, pz1 = d / 2 + pd;
				double xL = pcx - pw / 2, xR = pcx + pw / 2;
				// planks
				int np = ( int ) Math.Max( 2, Math.Floor( pd / 0.145 ) );
				for ( int i = 0; i < np; i ++ )
				{
					double zc = pz0 + 0.01 + ( i + 0.5 ) * ( pd - 0.01 ) / np;
					double bx = pcx + rand.range( - 0.012, 0.012 );
					double bry = rand.range( - 0.004, 0.004 );
					double ws = rand.next(), wth = rand.range( 0.55, 0.9 );
					B.box( "wood", bx, porchY - 0.02, zc, pw + 0.06, 0.04, 0.13, new O { grain = 0, ry = bry, tint = porchPaint ?? WHITE3, data = WOOD( ws, wth, porchPaint != null ? 0.68 : 0, 0 ) } );
				}

				// rim + support posts
				B.box( "wood", pcx, porchY - 0.15, pz1 - 0.03, pw + 0.08, 0.22, 0.06, new O { grain = 0, tint = rimTint, data = rimData() } );
				foreach ( double sx in new[] { xL, xR } ) B.box( "wood", sx + ( sx < pcx ? 0.03 : - 0.03 ), porchY - 0.15, ( pz0 + pz1 ) / 2, 0.06, 0.22, pd, new O { grain = 2, tint = rimTint, data = rimData() } );
				foreach ( double px in linspace( xL + 0.1, xR - 0.1, ( int ) Math.Max( 2, Math.Ceiling( pw / 2.2 ) + 1 ) ) )
				{
					double g = gAt( px, pz1 - 0.12 );
					double top = porchY - 0.26;
					if ( top - g > 0.05 )
					{
						B.box( "wood", px, ( top + g - 0.3 ) / 2, pz1 - 0.12, 0.14, top - g + 0.3, 0.14, new O { grain = 1, data = WOOD( rand.next(), 0.85 ) } );
						checks.Add( fr.worldPt( px, g - 0.3, pz1 - 0.12 ) );
					}
				}

				// roof height
				double pp = s.porchPitch ?? 0.26;
				double Tp = isThatch ? 0.24 : 0.06;
				double yAtt;
				if ( stories > 1 ) yAtt = floorY + storyH - 0.02;
				else if ( ! hasPorchEave ) yAtt = yE - 0.03;
				else
				{
					double ovF = ovE;
					double eaveEdgeTop = yE + r0 - ovF * ta;
					double fasciaBot = eaveEdgeTop - ( isThatch ? T / Math.Cos( a ) + 0.16 : 0.22 );
					yAtt = Math.Min( yE - 0.03, fasciaBot - 0.03 + ovF * Math.Tan( pp ) );
				}

				double zEnd = pz1 + 0.32;
				double yEnd = yAtt - ( zEnd - pz0 ) * Math.Tan( pp );
				double beamZ = pz1 - 0.12;
				double beamTop = yAtt - ( beamZ - pz0 ) * Math.Tan( pp ) - Tp / Math.Cos( pp ) - 0.005;
				double beamH = 0.18;
				double postTop = beamTop - beamH;
				double roofX0 = xL - 0.22, roofX1 = xR + 0.22;
				string porchKey = isThatch ? "thatch" : "roofMetal";
				B.slab( porchKey, new[] { V3( roofX0, yEnd, zEnd ), V3( roofX1, yEnd, zEnd ), V3( roofX1, yAtt, pz0 ), V3( roofX0, yAtt, pz0 ) }, Tp, new O
				{
					up = UP, uDir = V3( 0, yAtt - yEnd, pz0 - zEnd ).normalize(), tint = roofTint, data = roofData(),
				} );
				if ( ! isThatch )
				{
					B.box( "wood", pcx, yEnd - 0.1, zEnd - 0.016, roofX1 - roofX0, 0.18, 0.032, new O { grain = 0, tint = st.trim, data = trimData() } );
					B.box( "wood", pcx, yAtt - 0.02, pz0 + 0.03, roofX1 - roofX0, 0.06, 0.05, new O { grain = 0, tint = st.trim, data = trimData() } );
				}
				else
				{
					// ragged thatch edge along the porch roof front
					int n = ( int ) JS.Round( ( roofX1 - roofX0 ) / 0.07 );
					var part = gridPart( n, 1, ( i, j ) =>
					{
						double tt = ( double ) i / n;
						double r = Math.Abs( Math.Sin( i * 12.9898 + roofX0 * 78.233 ) * 43758.5453 ) % 1;
						double hang = 0.04 + 0.13 * r * r * ( i % 2 != 0 ? 1 : 0.6 );
						double px = roofX0 + ( roofX1 - roofX0 ) * tt;
						double yy = j == 0 ? yEnd - Tp - hang : yEnd - Tp * 0.3;
						return new GridVertex( px, yy, zEnd + ( j == 0 ? 0.04 : 0 ), 0, 0.2, 1, j == 0 ? - hang : 0.2, px );
					} );
					B.add( "thatch", part, new Matrix4(), new[] { 0.95, 0.92, 0.88 }, new[] { rand.next(), thatchAge + 0.1, 0, 0 } );
				}

				// beam + posts with knee braces
				B.box( "wood", pcx, beamTop - beamH / 2, beamZ, pw + 0.1, beamH, 0.12, new O { grain = 0, tint = st.trim, data = trimData() } );
				var postXs = linspace( xL + 0.08, xR - 0.08, ( int ) Math.Max( 2, Math.Ceiling( pw / 2.6 ) + 1 ) );
				foreach ( double px in postXs )
				{
					B.box( "wood", px, ( porchY + postTop ) / 2, beamZ, 0.12, postTop - porchY, 0.12, new O { grain = 1, tint = st.trim, data = trimData() } );
					B.box( "wood", px, porchY + 0.05, beamZ, 0.16, 0.1, 0.16, new O { grain = 0, tint = st.trim, data = trimData() } );
					B.box( "wood", px, postTop - 0.03, beamZ, 0.16, 0.06, 0.16, new O { grain = 0, tint = st.trim, data = trimData() } );
					foreach ( int sg in new[] { - 1, 1 } )
					{
						if ( ( px <= xL + 0.1 && sg < 0 ) || ( px >= xR - 0.1 && sg > 0 ) ) continue;
						B.beam( "wood", new[] { px + sg * 0.04, postTop - 0.34, beamZ }, new[] { px + sg * 0.36, postTop - 0.02, beamZ }, 0.05, 0.06, new O { tint = st.trim, data = trimData() } );
					}
				}

				// railing with a gap for the stairs
				double stairX = porch.stairX ?? doorX;
				string railStyle = porch.rail ?? "balusters";
				void railSeg( double ax, double az, double bx, double bz )
				{
					double L = JS.Hypot( bx - ax, bz - az );
					if ( L < 0.2 ) return;
					B.beam( "wood", new[] { ax, porchY + 0.86, az }, new[] { bx, porchY + 0.86, bz }, 0.1, 0.045, new O { tint = st.trim, data = trimData() } );
					B.beam( "wood", new[] { ax, porchY + 0.13, az }, new[] { bx, porchY + 0.13, bz }, 0.07, 0.045, new O { tint = st.trim, data = trimData() } );
					double dx = ( bx - ax ) / L, dz = ( bz - az ) / L;
					if ( railStyle == "balusters" )
					{
						int nn = ( int ) Math.Floor( L / 0.12 );
						for ( int i = 1; i < nn; i ++ )
						{
							double tt = ( double ) i / nn;
							B.box( "wood", ax + dx * L * tt, porchY + 0.495, az + dz * L * tt, 0.034, 0.71, 0.034, new O { grain = 1, skip = 12, tint = st.trim, data = trimData() } );
						}
					}
					else
					{
						int nb = ( int ) Math.Max( 1, JS.Round( L / 1.1 ) );
						for ( int i = 0; i < nb; i ++ )
						{
							double t0 = ( double ) i / nb, t1 = ( double ) ( i + 1 ) / nb;
							var p0 = new[] { ax + dx * L * t0, az + dz * L * t0 }; var p1 = new[] { ax + dx * L * t1, az + dz * L * t1 };
							B.beam( "wood", new[] { p0[ 0 ], porchY + 0.16, p0[ 1 ] }, new[] { p1[ 0 ], porchY + 0.83, p1[ 1 ] }, 0.035, 0.07, new O { tint = st.trim, data = trimData() } );
							B.beam( "wood", new[] { p0[ 0 ], porchY + 0.83, p0[ 1 ] }, new[] { p1[ 0 ], porchY + 0.16, p1[ 1 ] }, 0.03, 0.07, new O { tint = st.trim, data = trimData() } );
						}
					}

					fr.addBoxL( ( ax + bx ) / 2, porchY + 0.55, ( az + bz ) / 2, Math.Abs( dx ) > 0.5 ? L / 2 : 0.06, 0.55, Math.Abs( dx ) > 0.5 ? 0.06 : L / 2, false, true, "porchRail" );
				}

				double gapL = stairX - 0.68, gapR = stairX + 0.68;
				if ( s.railNet != null && gapL - xL > 1.0 )
				{
					// a fishing net thrown over the railing to dry
					double nx0 = xL + 0.2, nx1 = gapL - 0.12;
					double L = nx1 - nx0;
					var netPart = gridPart( 10, 8, ( i, j ) =>
					{
						double u = i / 10.0, v = j / 8.0;
						double xx = nx0 + u * L;
						double fold = Math.Sin( u * 17 + v * 2 ) * 0.035;
						// v: 0 inside bottom -> over the rail -> 1 outside bottom
						bool inside = v < 0.35;
						double tt = inside ? ( 0.35 - v ) / 0.35 : ( v - 0.35 ) / 0.65;
						double yy = porchY + 0.9 - tt * ( inside ? 0.35 : 0.78 ) - Math.Sin( u * Math.PI ) * 0.06 * tt;
						double zz = beamZ + ( inside ? - 0.05 - tt * 0.05 : 0.06 + tt * 0.1 ) + fold * tt;
						return new GridVertex( xx, yy, zz, 0, 0.3, inside ? - 1 : 1, u * L, v * 1.2 );
					} );
					double ns = rand.next();
					B.add( "net", netPart, new Matrix4(), s.railNet, Attr.Of( ( px, py, pz, ii ) => new[] { ns, Math.Min( 1, Math.Max( 0, porchY + 0.9 - py ) ) * 0.5, 0.04, 0 } ) );
				}

				railSeg( xL + 0.08, beamZ, gapL, beamZ );
				railSeg( gapR, beamZ, xR - 0.08, beamZ );
				railSeg( xL + 0.08, pz0 + 0.08, xL + 0.08, beamZ );
				railSeg( xR - 0.08, pz0 + 0.08, xR - 0.08, beamZ );
				foreach ( double gx in new[] { gapL, gapR } )
				{
					B.box( "wood", gx, porchY + 0.5, beamZ, 0.1, 1.0, 0.1, new O { grain = 1, tint = st.trim, data = trimData() } );
				}

				var sr = stairRun( ctx, fr, stairX, pz1, porchY, 1.2, st );
				// porch floor collider
				double gP = Math.Min( gMin, sr.g );
				fr.addBoxL( pcx, ( porchY + gP - 0.5 ) / 2, ( pz0 + pz1 ) / 2, pw / 2, ( porchY - gP + 0.5 ) / 2, pd / 2, true, true, "porch" );
				roofTop = Math.Max( roofTop, yAtt );

				// porch life: a chair or bench, hanging buoys, potted things
				if ( s.porchBench != false )
				{
					double bx = stairX > pcx ? xL + 1.1 : xR - 1.1;
					benchLow( B, rand, bx, porchY, pz0 + 0.45, st );
				}
			}
			else
			{
				// front stoop with steps
				double stoopY = floorY - 0.04;
				double sw = 1.5, sd = 1.0;
				int np = 7;
				for ( int i = 0; i < np; i ++ ) B.box( "wood", doorX, stoopY - 0.02, d / 2 + 0.02 + ( i + 0.5 ) * sd / np, sw, 0.04, sd / np - 0.012, new O { grain = 0, data = WOOD( rand.next(), 0.8 ) } );
				double gS = gAt( doorX, d / 2 + sd );
				foreach ( int sx in new[] { - 1, 1 } )
				{
					B.box( "wood", doorX + sx * ( sw / 2 - 0.06 ), ( stoopY + gS - 0.3 ) / 2, d / 2 + sd - 0.06, 0.12, stoopY - gS + 0.3, 0.12, new O { grain = 1, data = WOOD( rand.next(), 0.85 ) } );
					checks.Add( fr.worldPt( doorX + sx * ( sw / 2 - 0.06 ), gS - 0.3, d / 2 + sd - 0.06 ) );
				}

				var sr = stairRun( ctx, fr, doorX, d / 2 + sd, stoopY, 1.1, st );
				fr.addBoxL( doorX, ( stoopY + Math.Min( gS, sr.g ) - 0.4 ) / 2, d / 2 + sd / 2, sw / 2, ( stoopY - Math.Min( gS, sr.g ) + 0.4 ) / 2, sd / 2, true, true, "stoop" );
				// small hood over the door
				double hy = floorY + 2.5;
				B.slab( roofKey, new[] { V3( doorX - 0.85, hy - 0.22, d / 2 + 0.75 ), V3( doorX + 0.85, hy - 0.22, d / 2 + 0.75 ), V3( doorX + 0.85, hy, d / 2 ), V3( doorX - 0.85, hy, d / 2 ) }, isThatch ? 0.14 : 0.04, new O
				{
					up = UP, uDir = V3( 0, 0.22, - 0.75 ).normalize(), tint = roofTint, data = roofData(),
				} );
				foreach ( int sx in new[] { - 1, 1 } ) B.beam( "wood", new[] { doorX + sx * 0.75, hy - 0.5, d / 2 + 0.02 }, new[] { doorX + sx * 0.75, hy - 0.2, d / 2 + 0.65 }, 0.05, 0.07, new O { tint = st.trim, data = trimData() } );
			}

			// ------------------------------------------------------------- extras
			if ( s.stovepipe && ridge != null )
			{
				double px = ridge.axis == 'x' ? w * 0.22 : - w * 0.22, pz = ridge.axis == 'x' ? - d * 0.22 : d * 0.18;
				double surf = ridge.axis == 'x' ? ridge.y - Math.Abs( pz ) * ta : ridge.y - Math.Abs( px ) * ta;
				var hd = HARD( rand.next(), 0.75, 0.6, 0.55 );
				B.cyl( "hard", px, surf - 0.3, pz, 0.08, 0.08, ridge.y - surf + 1.0, new O { segs = 10, capTop = false, tint = lin( 0x3a3632 ), data = hd } );
				B.cyl( "hard", px, ridge.y + 0.72, pz, 0.02, 0.2, 0.14, new O { segs = 10, capBot = true, tint = lin( 0x3a3632 ), data = hd } );
				B.cyl( "hard", px, surf - 0.02, pz, 0.1, 0.2, 0.08, new O { segs = 10, tint = lin( 0x3a3632 ), data = hd } );
			}

			if ( s.chimney != 0 )
			{
				int sx = s.chimney;
				double cxl = sx * ( w / 2 + 0.36 ), czl = roofType == "gableFront" ? - d * 0.25 : 0;
				double g = gAt( cxl, czl );
				double top = roofType == "gable" ? ( ridge.y + 0.7 ) : ( ridge.y + 0.3 );
				double hh = top - ( g - 0.3 );
				B.box( "stone", cxl, g - 0.3 + hh / 2, czl, 0.72, hh, 0.82, new O { grain = 0, tint = lin( 0xd8d0c0 ), data = new[] { rand.next(), 2, 0, 0 } } );
				B.box( "stone", cxl, top + 0.04, czl, 0.84, 0.08, 0.94, new O { grain = 0, tint = lin( 0xc8c0b0 ), data = new[] { rand.next(), 0, 0, 0 } } );
				B.cyl( "stone", cxl, top + 0.08, czl, 0.1, 0.12, 0.3, new O { segs = 8, tint = lin( 0xa86a4a ), data = new[] { rand.next(), 1, 0, 0 } } );
				checks.Add( fr.worldPt( cxl, g - 0.3, czl ) );
				fr.addBoxL( cxl, g + hh / 2 - 0.3, czl, 0.36, hh / 2, 0.41, false, true, "chimney" );
			}

			if ( s.antenna && ridge != null )
			{
				double ax = ridge.axis == 'x' ? - w * 0.3 : 0, az = ridge.axis == 'x' ? 0 : - d * 0.3;
				var hd = HARD( rand.next(), 0.5, 0.8, 0.4 );
				B.rod( "hard", new[] { ax, ridge.y - 0.1, az }, new[] { ax, ridge.y + 2.4, az }, 0.025, 0.02, new O { segs = 6, tint = C.galv, data = hd } );
				foreach ( var hl in new[] { new[] { 2.2, 1.2 }, new[] { 1.9, 1.0 }, new[] { 1.6, 0.8 } } )
				{
					double hy = hl[ 0 ], len = hl[ 1 ];
					B.rod( "hard", new[] { ax, ridge.y + hy, az - len / 2 }, new[] { ax, ridge.y + hy, az + len / 2 }, 0.008, 0.008, new O { segs = 4, tint = C.galv, data = hd } );
				}

				B.rod( "hard", new[] { ax, ridge.y + 2.25, az }, new[] { ax + 0.9, ridge.y + 2.25, az }, 0.012, 0.012, new O { segs = 4, tint = C.galv, data = hd } );
			}

			if ( s.gutter && ! isThatch && ridge != null && ridge.axis == 'x' )
			{
				double zf = ridge.zf + 0.06, yg = ridge.yF - 0.12;
				double X = w / 2 + ovR;
				var hd = HARD( rand.next(), 0.55, 0.8, 0.4 );
				B.rod( "hard", new[] { - X + 0.1, yg, - zf }, new[] { X - 0.1, yg, - zf }, 0.06, 0.06, new O { segs = 8, capTop = true, tint = C.galv, data = hd } );
				double dx = X - 0.25;
				B.rod( "hard", new[] { dx, yg, - zf }, new[] { dx, gAt( dx, - zf ) + 0.95, - zf }, 0.04, 0.04, new O { segs = 6, tint = C.galv, data = hd } );
				var bp = toW( dx, - zf - 0.05 );
				double by = gAt( dx, - zf - 0.05 ) - 0.02;
				ctx.inst.add( "barrel", bp[ 0 ], by, bp[ 1 ], rand.range( 0, 6 ), new[] { 0.9, 0.85, 0.8 } );
				colliders.addCylinder( bp[ 0 ], bp[ 1 ], 0.32, gAt( dx, - zf ) - 0.1, gAt( dx, - zf ) + 0.9, "barrel" );
			}

			// back lean-to annex (kitchen / store room) with its own shed roof
			if ( s.annex != null )
			{
				var an = s.annex;
				double aw = Math.Min( w - 0.6, an.w ?? w * 0.62 ), ad = annexD, ax = an.x ?? 0;
				double za0 = - d / 2, za1 = - d / 2 - ad;
				double pa = 0.3, Ta = isThatch ? 0.22 : 0.05;
				double yAtt;
				if ( stories > 1 ) yAtt = floorY + storyH - 0.02;
				else if ( roofType == "gableFront" ) yAtt = yE - 0.03;
				else
				{
					double eaveEdgeTop = yE + r0 - ovE * ta;
					double fasciaBot = eaveEdgeTop - ( isThatch ? T / Math.Cos( a ) + 0.16 : 0.22 );
					yAtt = Math.Min( yE - 0.03, fasciaBot - 0.03 + ovE * Math.Tan( pa ) );
				}

				double zEnd = za1 - 0.3;
				double yEnd = yAtt - ( za0 - zEnd ) * Math.Tan( pa );
				double cutV = Ta / Math.Cos( pa ) + 0.01;
				double yBackTop = yAtt - ( za0 - za1 ) * Math.Tan( pa ) - cutV;
				double Ha = yBackTop - floorY;
				var aColor = an.wall ?? s.wall;
				double aPaint = Math.Max( 0.3, st.paint - 0.12 );
				double[] aData() => new[] { rand.next(), aPaint, siding, Math.Min( 1, st.weather + 0.1 ) };
				B.box( "wood", ax, floorY + Ha / 2, za1 + t / 2, aw, Ha, t, new O { grain = 0, tint = aColor, data = aData() } );
				foreach ( int sx in new[] { - 1, 1 } )
				{
					double xo = ax + sx * aw / 2;
					var pts = new[] { V3( xo, floorY, za0 - 0.01 ), V3( xo, floorY, za1 ), V3( xo, yBackTop, za1 ), V3( xo, yAtt - cutV, za0 - 0.01 ) };
					B.slab( "wood", pts, t, new O { up = V3( sx, 0, 0 ), uDir = V3( 0, 0, - sx ), tint = aColor, data = aData() } );
					B.box( "wood", xo - sx * 0.05, floorY + Ha / 2, za1 + 0.05, 0.13, Ha + 0.01, 0.13, new O { grain = 1, tint = st.trim, data = trimData() } );
					// back corner posts down to the ground
					double lx = ax + sx * ( aw / 2 - 0.12 ), lz = za1 + 0.12;
					double g = gAt( lx, lz );
					B.box( "wood", lx, ( g - 0.3 + floorY - rimH ) / 2, lz, 0.15, floorY - rimH - g + 0.3, 0.15, new O { grain = 1, data = WOOD( rand.next(), 0.85 ) } );
					checks.Add( fr.worldPt( lx, g - 0.3, lz ) );
					B.box( "wood", xo + sx * 0.02, rimY, ( za0 + za1 ) / 2, 0.06, rimH, ad, new O { grain = 2, tint = rimTint, data = rimData() } );
				}

				B.box( "wood", ax, rimY, za1 - 0.02, aw + 0.08, rimH, 0.06, new O { grain = 0, tint = rimTint, data = rimData() } );
				B.box( "wood", ax, yBackTop - 0.08, za1 - 0.012, aw, 0.16, 0.025, new O { grain = 0, tint = st.trim, data = trimData() } );
				double rx0 = ax - aw / 2 - 0.22, rx1 = ax + aw / 2 + 0.22;
				B.slab( roofKey, new[] { V3( rx0, yEnd, zEnd ), V3( rx1, yEnd, zEnd ), V3( rx1, yAtt, za0 ), V3( rx0, yAtt, za0 ) }, Ta, new O
				{
					up = UP, uDir = V3( 0, yAtt - yEnd, za0 - zEnd ).normalize(), tint = roofTint, data = roofData(),
				} );
				if ( ! isThatch ) B.box( "wood", ax, yEnd - 0.09, zEnd + 0.016, rx1 - rx0, 0.16, 0.03, new O { grain = 0, tint = st.trim, data = trimData() } );
				B.pushAt( ax, 0, za1, Math.PI );
				{
					var ast = st.Clone(); ast.closed = rand.chance( 0.25 ); ast.closedPattern = 3;
					windowUnit( B, rand, 0, floorY + 1.0, 0.7, 0.85, ast );
				}

				B.pop();
				fr.addBoxL( ax, ( gMin - 0.3 + yAtt ) / 2, ( za0 + za1 ) / 2, aw / 2 + 0.05, ( yAtt - gMin + 0.3 ) / 2, ad / 2, false, true, "house" );
			}

			if ( s.buoys != 0 )
			{
				// fishing floats hung on the side wall
				int side = s.buoys;
				B.pushAt( side * ( w / 2 + 0.02 ), 0, 0, side * Math.PI / 2 );
				buoyString( B, new[] { - d / 2 + 0.5, floorY + 2.3, 0.05 }, new[] { d / 2 - 0.9, floorY + 2.3, 0.05 }, ( int ) Math.Max( 3, Math.Floor( d / 0.8 ) ), rand, 0.3 );
				B.pop();
			}

			B.pop();

			// extras placed in world space
			if ( s.tank != 0 )
			{
				var tp = toW( s.tank * ( w / 2 + 1.25 ), - d / 4 );
				double g = terrain.HeightAt( tp[ 0 ], tp[ 1 ] );
				double tseed = rand.next(); bool tgalv = rand.chance( 0.6 );
				waterTank( B, tp[ 0 ], g, tp[ 1 ], tseed, tgalv );
				colliders.addBox( new Vector3( tp[ 0 ], g + 1.5, tp[ 1 ] ), new Vector3( 0.75, 1.5, 0.75 ), 0, false, true, "tank" );
				checks.Add( new FoundationCheck { x = tp[ 0 ], y = g - 0.2, z = tp[ 1 ] } );
			}

			if ( s.woodpile != 0 )
			{
				double lx = - s.woodpile * ( w / 2 + 0.45 ), lz = - d / 4;
				var wp = toW( lx, lz );
				woodpile( B, wp[ 0 ], terrain.HeightAt( wp[ 0 ], wp[ 1 ] ), wp[ 1 ], yaw + Math.PI / 2, rand );
				colliders.addBox( new Vector3( wp[ 0 ], terrain.HeightAt( wp[ 0 ], wp[ 1 ] ) + 0.3, wp[ 1 ] ), new Vector3( 0.3, 0.3, 0.6 ), yaw, false, true, "woodpile" );
			}

			// main body collider
			double bodyBot = gMin - 0.3;
			fr.addBoxL( 0, ( bodyBot + roofTop ) / 2, 0, w / 2 + 0.06, ( roofTop - bodyBot ) / 2, d / 2 + 0.06, false, true, "house" );

			return new BuildResult
			{
				floorY = floorY, roofTop = roofTop,
				footprint = new Footprint { x = x, z = z, r = JS.Hypot( w, d + Math.Max( pd, annexD ) * 2 ) / 2 + 0.8, kind = "building" },
			};
		}

		static void benchLow( Builder B, Rand rand, double x, double y, double z, Style st )
		{
			double[] wd() => WOOD( rand.next(), 0.8, st.trimPaint * 0.8, 0 );
			for ( int i = 0; i < 2; i ++ ) B.box( "wood", x, y + 0.43, z - 0.06 + i * 0.13, 1.3, 0.035, 0.11, new O { grain = 0, tint = st.accent, data = wd() } );
			foreach ( double sx in new[] { - 0.55, 0.55 } ) B.box( "wood", x + sx, y + 0.21, z, 0.06, 0.42, 0.3, new O { grain = 1, tint = st.accent, data = wd() } );
			B.box( "wood", x, y + 0.72, z - 0.17, 1.3, 0.12, 0.03, new O { grain = 0, rx = - 0.12, tint = st.accent, data = wd() } );
			foreach ( double sx in new[] { - 0.55, 0.55 } ) B.box( "wood", x + sx, y + 0.6, z - 0.15, 0.05, 0.36, 0.03, new O { grain = 1, rx = - 0.12, tint = st.accent, data = wd() } );
		}

		// ---------------------------------------------------------------------------
		// Open-fronted boathouse on the beach (local +z = open side toward the sea)

		public static BuildResult buildBoathouse( BuildCtx ctx, Spec s )
		{
			var B = ctx.B; var terrain = ctx.terrain; var colliders = ctx.colliders; var rand = ctx.rand; var lights = ctx.lights; var checks = ctx.checks;
			double x = s.x, z = s.z, yaw = s.yaw;
			double w = s.w ?? 5.4, d = s.d ?? 7.2;
			frameFns( terrain, x, z, yaw, out var toW, out var gAt );
			void addBoxL( double lx, double ly, double lz, double hx, double hy, double hz, string tag )
			{
				var p = toW( lx, lz );
				colliders.addBox( new Vector3( p[ 0 ], ly, p[ 1 ] ), new Vector3( hx, hy, hz ), yaw, false, true, tag );
			}

			double gMin = double.PositiveInfinity, gMax = double.NegativeInfinity;
			for ( int i = 0; i <= 4; i ++ )
				for ( int j = 0; j <= 4; j ++ )
				{
					double g = gAt( ( i / 4.0 - 0.5 ) * w, ( j / 4.0 - 0.5 ) * d );
					gMin = Math.Min( gMin, g ); gMax = Math.Max( gMax, g );
				}

			double bas = gMax;
			double yE = bas + 2.7;
			var wall = s.wall ?? lin( 0x9fb9b0 );
			double paint = s.paint ?? 0.55;
			double[] wdat( double pat ) => new[] { rand.next(), paint, pat, 0.8 };
			B.pushAt( x, 0, z, yaw );

			// posts
			var postXs = new[] { - w / 2 + 0.09, w / 2 - 0.09 };
			var postZs = linspace( - d / 2 + 0.09, d / 2 - 0.09, 4 );
			foreach ( double px in postXs )
				foreach ( double pz in postZs )
				{
					double g = gAt( px, pz );
					B.box( "wood", px, ( g - 0.5 + yE ) / 2, pz, 0.18, yE - g + 0.5, 0.18, new O { grain = 1, data = WOOD( rand.next(), 0.9 ) } );
					var tw = toW( px, pz );
					checks.Add( new FoundationCheck { x = tw[ 0 ], y = g - 0.5, z = tw[ 1 ] } );
				}

			// walls: back + sides (board and batten, faded paint), open front
			double wallBot = gMin - 0.15;
			double hW = yE - wallBot;
			B.box( "wood", 0, wallBot + hW / 2, - d / 2 + 0.05, w, hW, 0.08, new O { grain = 0, tint = wall, data = wdat( 2 ) } );
			foreach ( int sx in new[] { - 1, 1 } ) B.box( "wood", sx * ( w / 2 - 0.05 ), wallBot + hW / 2, 0, 0.08, hW, d - 0.1, new O { grain = 2, tint = wall, data = wdat( 2 ) } );
			addBoxL( 0, wallBot + hW / 2, - d / 2 + 0.05, w / 2, hW / 2, 0.08, "boathouse" );
			foreach ( int sx in new[] { - 1, 1 } ) addBoxL( sx * ( w / 2 - 0.05 ), wallBot + hW / 2, 0, 0.08, hW / 2, d / 2, "boathouse" );
			// header over the opening
			B.box( "wood", 0, yE - 0.14, d / 2 - 0.09, w, 0.28, 0.16, new O { grain = 0, data = WOOD( rand.next(), 0.85 ) } );

			// gable roof, ridge along z (toward the sea)
			double a = 0.5, ta = Math.Tan( a ), r0 = 0.1;
			double yR = yE + r0 + ( w / 2 ) * ta;
			double ov = 0.4, Z = d / 2 + 0.45;
			double xe = w / 2 + ov, yF = yE + r0 - ov * ta;
			double[] rd() => new[] { rand.next(), 0.75, 0, 0 };
			var rt = s.roofColor ?? lin( 0x6d7f86 );
			B.slab( "roofMetal", new[] { V3( xe, yF, Z ), V3( xe, yF, - Z ), V3( 0, yR, - Z ), V3( 0, yR, Z ) }, 0.05, new O { up = UP, uDir = V3( - xe, yR - yF, 0 ).normalize(), tint = rt, data = rd() } );
			B.slab( "roofMetal", new[] { V3( - xe, yF, - Z ), V3( - xe, yF, Z ), V3( 0, yR, Z ), V3( 0, yR, - Z ) }, 0.05, new O { up = UP, uDir = V3( xe, yR - yF, 0 ).normalize(), tint = rt, data = rd() } );
			foreach ( int sz in new[] { - 1, 1 } )
			{
				B.part( "wood", gableEndPart( w, r0 - 0.03, r0 + ( w / 2 ) * ta - 0.03, 0.08 ), 0, yE, sz * ( d / 2 - 0.05 ), new O { ry = sz > 0 ? 0 : Math.PI, tint = wall, data = wdat( 2 ) } );
			}

			// ridge beam + rafters (visible from inside)
			B.box( "wood", 0, yR - 0.14, 0, 0.1, 0.18, d + 0.8, new O { grain = 2, data = WOOD( rand.next(), 0.8 ) } );
			foreach ( double pz in linspace( - d / 2 + 0.3, d / 2 - 0.3, 5 ) )
			{
				foreach ( int sx in new[] { - 1, 1 } ) B.beam( "wood", new[] { 0, yR - 0.12, pz }, new[] { sx * ( w / 2 + ov - 0.05 ), yF - 0.1, pz }, 0.05, 0.12, new O { data = WOOD( rand.next(), 0.75 ) } );
				B.box( "wood", 0, yE - 0.05, pz, w - 0.1, 0.1, 0.07, new O { grain = 0, data = WOOD( rand.next(), 0.8 ) } );
			}

			// inside: a rowboat on a cradle, oars on the wall, nets, workbench, lantern
			double gIn = gAt( 0.3, 0 );
			rowboat( B, 0.35, gIn + 0.28, 0.1, 0, new RowboatO { seed = rand.next(), hull = lin( 0xd9d2bf ), bottom = lin( 0x2f5f7a ), trim = lin( 0x2f5f7a ), length = 4.2 } );
			foreach ( double pz in new[] { - 1.1, 1.2 } ) B.box( "wood", 0.35, gIn + 0.12, pz, 1.1, 0.24, 0.14, new O { grain = 0, data = WOOD( rand.next(), 0.9 ) } );
			addBoxL( 0.35, gIn + 0.5, 0.1, 0.8, 0.5, 2.2, "boat" );
			oar( B, new[] { - w / 2 + 0.12, gAt( - w / 2 + 0.2, - 1.5 ) + 0.4, - 1.6 }, new[] { - w / 2 + 0.12, gAt( - w / 2 + 0.2, - 1.5 ) + 2.55, - 1.35 }, rand.next(), lin( 0xc23b2e ) );
			oar( B, new[] { - w / 2 + 0.12, gAt( - w / 2 + 0.2, - 1.0 ) + 0.4, - 0.9 }, new[] { - w / 2 + 0.12, gAt( - w / 2 + 0.2, - 1.0 ) + 2.55, - 0.7 }, rand.next(), lin( 0xc23b2e ) );
			// workbench along the right wall
			double bx = w / 2 - 0.45;
			double gB = gAt( bx, - 1.5 );
			for ( int i = 0; i < 3; i ++ ) B.box( "wood", bx - 0.2 + i * 0.2, gB + 0.9, - 1.5, 0.19, 0.05, 2.0, new O { grain = 2, data = WOOD( rand.next(), 0.7 ) } );
			foreach ( double pz in new[] { - 2.4, - 0.6 } )
				foreach ( double px in new[] { bx - 0.25, bx + 0.25 } ) B.box( "wood", px, gB + 0.43, pz, 0.07, 0.9, 0.07, new O { grain = 1, data = WOOD( rand.next(), 0.8 ) } );
			bucket( B, bx, gB + 0.925, - 2.1, lin( 0xd8d8d0 ), rand.next() );
			fish( B, bx, gB + 0.925, - 1.2, new FishO { species = "mullet", len = 0.4, ry = 1.9, sag = 0.15, jaw = 0.2, wet = 0.6, cloudy = 0.6, seed = rand.next() } );
			addBoxL( bx, gB + 0.45, - 1.5, 0.35, 0.45, 1.0, "bench" );
			// net hanging on the left wall
			var netPart = gridPart( 10, 8, ( i, j ) =>
			{
				double u = i / 10.0, v = j / 8.0;
				double pz = - 0.2 + u * 2.4;
				double sag = Math.Sin( u * Math.PI ) * 0.35 * v;
				return new GridVertex( - w / 2 + 0.16 + Math.Sin( u * 12 + v * 3 ) * 0.03, yE - 0.35 - v * 1.6 + sag * 0.3, pz + Math.Sin( v * 5 ) * 0.05, 1, 0, 0, u * 2.4, v * 1.6 );
			} );
			B.add( "net", netPart, new Matrix4(), lin( 0x4f7a6a ), Attr.Of( ( px, py, pz, ii ) => new[] { 0.3, Math.Min( 1, Math.Max( 0, ( yE - 0.35 - py ) / 1.6 ) ) * 0.4, 0.04, 0 } ) );
			var lp = lantern( B, 0, yR - 0.25, 0.8, rand.next() );
			lights.Add( new LightSource { position = B.toWorld( lp[ 0 ], lp[ 1 ], lp[ 2 ] ), color = WarmColor(), intensity = 3, kind = "lantern" } );

			// slipway rails down toward the water
			foreach ( double sx in new[] { - 0.55, 1.25 } )
			{
				double zA = d / 2 - 0.3, zB = d / 2 + 6.5;
				double gA = gAt( sx, zA ), gBv = gAt( sx, zB );
				B.beam( "wood", new[] { sx, gA + 0.04, zA }, new[] { sx, gBv + 0.02, zB }, 0.14, 0.12, new O { data = WOOD( rand.next(), 0.95 ) } );
				foreach ( double pz in linspace( zA + 0.5, zB - 0.3, 5 ) )
				{
					double gg = gAt( sx, pz );
					B.box( "wood", sx + 0.35, gg - 0.02, pz, 1.1, 0.09, 0.16, new O { grain = 0, data = WOOD( rand.next(), 0.95 ) } );
				}
			}

			B.pop();
			{
				var fp = toW( - w / 2 - 0.6, d / 2 - 0.3 );
				flagPole( B, fp[ 0 ], gAt( - w / 2 - 0.6, d / 2 - 0.3 ), fp[ 1 ], 5.5, lin( 0x2f6fb0 ), rand.next() );
			}
			{
				var p = toW( - w / 2 - 0.6, d / 2 - 0.3 );
				colliders.addCylinder( p[ 0 ], p[ 1 ], 0.08, gAt( - w / 2 - 0.6, d / 2 - 0.3 ) - 0.3, gAt( - w / 2 - 0.6, d / 2 - 0.3 ) + 5.5, "flagpole" );
			}

			return new BuildResult { footprint = new Footprint { x = x, z = z, r = JS.Hypot( w, d ) / 2 + 1, kind = "building" } };
		}

		// ---------------------------------------------------------------------------
		// Market stall with a thatched roof (local +z = customer side)

		sealed class Basin { public double bx; public string bed; public object[][] fish; public int lobsters; }

		public static BuildResult buildMarketStall( BuildCtx ctx, Spec s )
		{
			var B = ctx.B; var terrain = ctx.terrain; var colliders = ctx.colliders; var rand = ctx.rand; var lights = ctx.lights; var checks = ctx.checks;
			double x = s.x, z = s.z, yaw = s.yaw;
			double w = 3.4, d = 2.4;
			frameFns( terrain, x, z, yaw, out var toW, out var gAt );
			void addBoxL( double lx, double ly, double lz, double hx, double hy, double hz, string tag )
			{
				var p = toW( lx, lz );
				colliders.addBox( new Vector3( p[ 0 ], ly, p[ 1 ] ), new Vector3( hx, hy, hz ), yaw, false, true, tag );
			}

			B.pushAt( x, 0, z, yaw );
			double g0 = gAt( 0, 0 );
			double top = g0 + 2.55;
			foreach ( double px in new[] { - w / 2, w / 2 } )
				foreach ( double pz in new[] { - d / 2, d / 2 } )
				{
					double g = gAt( px, pz );
					B.cyl( "wood", px, g - 0.4, pz, 0.065, 0.075, top - g + 0.45, new O { segs = 7, data = WOOD( rand.next(), 0.9 ) } );
					var p = toW( px, pz );
					colliders.addCylinder( p[ 0 ], p[ 1 ], 0.1, g - 0.4, top, "stall" );
					checks.Add( new FoundationCheck { x = p[ 0 ], y = g - 0.4, z = p[ 1 ] } );
				}

			foreach ( double pz in new[] { - d / 2, d / 2 } ) B.rod( "wood", new[] { - w / 2 - 0.2, top, pz }, new[] { w / 2 + 0.2, top, pz }, 0.06, 0.06, new O { segs = 6, data = WOOD( rand.next(), 0.85 ) } );
			// hip thatch roof
			double ov = 0.5, a = 0.62, ta = Math.Tan( a );
			double XE = w / 2 + ov, ZE = d / 2 + ov, XR = ( w - d ) / 2;
			double ye = top + 0.08, yR = ye + ZE * ta;
			double[] td() => new[] { rand.next(), 0.5, 0, 0 };
			double T = 0.2;
			B.slab( "thatch", new[] { V3( - XE, ye, ZE ), V3( XE, ye, ZE ), V3( XR, yR, 0 ), V3( - XR, yR, 0 ) }, T, new O { up = UP, uDir = V3( 0, yR - ye, - ZE ).normalize(), data = td() } );
			B.slab( "thatch", new[] { V3( XE, ye, - ZE ), V3( - XE, ye, - ZE ), V3( - XR, yR, 0 ), V3( XR, yR, 0 ) }, T, new O { up = UP, uDir = V3( 0, yR - ye, ZE ).normalize(), data = td() } );
			B.slab( "thatch", new[] { V3( XE, ye, ZE ), V3( XE, ye, - ZE ), V3( XR, yR, 0 ) }, T, new O { up = UP, uDir = V3( - XE + XR, yR - ye, 0 ).normalize(), data = td() } );
			B.slab( "thatch", new[] { V3( - XE, ye, - ZE ), V3( - XE, ye, ZE ), V3( - XR, yR, 0 ) }, T, new O { up = UP, uDir = V3( XE - XR, yR - ye, 0 ).normalize(), data = td() } );
			B.rod( "thatch", new[] { - XR - 0.05, yR - 0.04, 0 }, new[] { XR + 0.05, yR - 0.04, 0 }, 0.15, 0.15, new O { segs = 7, data = new[] { rand.next(), 0.7, 0, 0 } } );

			// counter with fish baskets
			double cy = g0 + 0.92;
			for ( int i = 0; i < 4; i ++ ) B.box( "wood", 0, cy, d / 2 - 0.55 + i * 0.13 - 0.2, w - 0.2, 0.045, 0.12, new O { grain = 0, data = WOOD( rand.next(), 0.75 ) } );
			foreach ( double px in new[] { - w / 2 + 0.25, w / 2 - 0.25 } )
				foreach ( double pz in new[] { d / 2 - 0.3, d / 2 - 0.85 } ) B.box( "wood", px, g0 + 0.45, pz, 0.07, 0.9, 0.07, new O { grain = 1, data = WOOD( rand.next(), 0.8 ) } );
			B.box( "wood", 0, g0 + 0.5, d / 2 - 0.26, w - 0.3, 0.6, 0.03, new O { grain = 0, tint = lin( 0x5aa7a0 ), data = new[] { rand.next(), 0.55, 3, 0.7 } } );
			addBoxL( 0, g0 + 0.5, d / 2 - 0.55, w / 2 - 0.05, 0.5, 0.35, "stall" );
			// the fish displays draw from their own random sequence (the village's stays as it was)
			var fr = new Rand( new Mulberry32( unchecked( ( uint ) ( long ) Math.Floor( rand.next() * 4294967296 ) ) ) );
			for ( int i = 1; i < 58; i ++ ) rand.next();
			// basins: fish on crushed ice or banana leaves, lying on each other, curled; lobsters
			var basins = new[]
			{
				new Basin { bx = - 1.05, bed = "ice", fish = new[] { new object[] { "redSnapper", 0.4 }, new object[] { "redSnapper", 0.36 }, new object[] { "yellowtail", 0.36 }, new object[] { "redSnapper", 0.38 }, new object[] { "yellowtail", 0.34 } } },
				new Basin { bx = 0.0, bed = "leaf", fish = new[] { new object[] { "parrot", 0.42 }, new object[] { "grunt", 0.3 }, new object[] { "grunt", 0.28 }, new object[] { "parrot", 0.38 }, new object[] { "grunt", 0.3 } } },
				new Basin { bx = 1.05, bed = "ice", fish = new[] { new object[] { "jack", 0.4 }, new object[] { "mullet", 0.36 }, new object[] { "jack", 0.38 } }, lobsters = 2 },
			};
			double bz = d / 2 - 0.58;
			foreach ( var b in basins )
			{
				double bx = b.bx;
				B.lathe( "wood", bx, cy + 0.02, bz, new[] { new[] { 0.0, 0 }, new[] { 0.21, 0 }, new[] { 0.21, 0.0 }, new[] { 0.3, 0.16 }, new[] { 0.315, 0.17 }, new[] { 0.29, 0.165 }, new[] { 0.2, 0.03 }, new[] { 0.0, 0.03 } }, new O { segs = 14, tint = lin( 0xc9a66a ), data = WOOD( fr.next(), 0.2, 0, 5 ) } );
				// basin floor at cy + 0.05; inner radius 0.2 there, 0.29 at the rim (cy + 0.19)
				double floor = cy + 0.05;
				double lift;
				if ( b.bed == "ice" )
				{
					iceBed( B, bx, cy + 0.12, bz, 0.25, fr.next() );
					lift = cy + 0.15;
				}
				else
				{
					// leaves lining the basin, their tips over the rim
					for ( int k = 0; k < 4; k ++ )
					{
						double aa = k * Math.PI / 2 + fr.range( - 0.3, 0.3 );
						bananaLeaf( B, bx + Math.Cos( aa ) * 0.02, floor + 0.004 + k * 0.002, bz + Math.Sin( aa ) * 0.02, - aa, 0.36, fr.next(), 0.5 );
					}

					lift = floor + 0.02;
				}

				// fish in layers, heads alternating, bodies curved to the basin, tails over the rim
				for ( int i = 0; i < b.fish.Length; i ++ )
				{
					string species = ( string ) b.fish[ i ][ 0 ]; double len = ( double ) b.fish[ i ][ 1 ];
					double aa = i * 2.4 + fr.range( - 0.4, 0.4 );
					double r = fr.range( 0.0, 0.07 );
					var fo = new FishO { species = species, len = len };
					fo.ry = aa + ( i % 2 != 0 ? Math.PI : 0 ) + fr.range( - 0.3, 0.3 ); fo.rx = fr.range( - 0.12, 0.12 ); fo.rz = fr.range( - 0.1, 0.1 );
					fo.flip = fr.chance( 0.3 );
					double sagMag = fr.range( 0.35, 0.8 ); fo.sag = sagMag * ( fr.chance( 0.5 ) ? 1 : - 1 ); fo.curl = fr.range( - 0.25, 0.15 );
					fo.jaw = fr.range( 0.15, 0.5 ); fo.cloudy = fr.range( 0.2, 0.6 ); fo.blood = fr.range( 0.3, 1 ); fo.seed = fr.next();
					fish( B, bx + Math.Cos( aa ) * r, lift + Math.Floor( i / 2.0 ) * 0.04, bz + Math.Sin( aa ) * r, fo );
				}

				for ( int k = 0; k < b.lobsters; k ++ )
				{
					double aa = k * Math.PI + 0.6;
					lobster( B, bx + Math.Cos( aa ) * 0.09, lift + 0.07, bz + Math.Sin( aa ) * 0.1, aa + Math.PI * 0.5, 0.26, fr.next(), fr.range( - 0.15, 0.15 ) );
				}
			}

			// small fry on banana leaves between the basins
			foreach ( double lx in new[] { - 0.52, 0.52 } )
			{
				double bly = fr.range( - 0.12, 0.12 ); double blSeed = fr.next();
				bananaLeaf( B, lx - 0.22, cy + 0.025, bz - 0.02, bly, 0.44, blSeed );
				for ( int k = 0; k < 3; k ++ )
				{
					double fx = lx + fr.range( - 0.04, 0.04 );
					var fo = new FishO();
					fo.species = fr.pick( new[] { "grunt", "yellowtail", "mullet" } ); fo.len = fr.range( 0.2, 0.26 ); fo.ry = fr.range( - 0.4, 0.4 ) + ( k % 2 != 0 ? Math.PI : 0 );
					fo.sag = fr.range( - 0.4, 0.4 ); fo.jaw = fr.range( 0.1, 0.4 ); fo.seed = fr.next();
					fish( B, fx, cy + 0.035 + k * 0.012, bz - 0.06 + k * 0.05, fo );
				}
			}

			// fish hanging from the front beam: by the tail on twine, or on S-hooks through the gills;
			// limp (slightly curled and sagging), mouths open
			var hung = new[]
			{
				new object[] { - 1.42, "mahi", 0.9, "tail" }, new object[] { - 0.9, "redSnapper", 0.46, "gill" }, new object[] { - 0.45, "grouper", 0.62, "tail" },
				new object[] { 0.05, "barracuda", 0.95, "tail" }, new object[] { 0.52, "tuna", 0.58, "tail" }, new object[] { 0.95, "redSnapper", 0.42, "gill" }, new object[] { 1.4, "yellowtail", 0.4, "gill" },
			};
			foreach ( var h in hung )
			{
				double px = ( double ) h[ 0 ]; double len = ( double ) h[ 2 ]; string how = ( string ) h[ 3 ];
				string species = ( string ) h[ 1 ];
				var opts = new FishO { species = species, len = len, pose = how };
				opts.ry = fr.range( - 0.15, 0.15 ); opts.sag = fr.range( - 0.2, 0.2 ); opts.curl = fr.range( - 0.25, 0.25 );
				opts.jaw = how == "tail" ? fr.range( 0.35, 0.6 ) : fr.range( 0.1, 0.3 );
				opts.cloudy = fr.range( 0.2, 0.5 ); opts.blood = fr.range( 0.4, 1 ); opts.seed = fr.next();
				if ( how == "tail" )
				{
					double y = top - 0.2 - fr.range( 0, 0.08 );
					fishTwine( B, new[] { px, top - 0.05, d / 2 }, new[] { px, y, d / 2 }, fr.next(), 0.012 + len * 0.01 );
					fish( B, px, y, d / 2, opts );
				}
				else
				{
					double y = sHook( B, px, top, d / 2, 0.06, fr.next() );
					fish( B, px, y, d / 2, opts );
				}
			}

			var lp = lantern( B, 0, top - 0.02, - d / 2, rand.next() );
			lights.Add( new LightSource { position = B.toWorld( lp[ 0 ], lp[ 1 ], lp[ 2 ] ), color = WarmColor(), intensity = 3, kind = "lantern" } );
			B.pop();

			foreach ( var c in new[] { new[] { - 1.3, - 0.4 }, new[] { - 0.7, - 0.6 }, new[] { 1.2, - 0.5 } } )
			{
				double lx = c[ 0 ], lz = c[ 1 ];
				var p = toW( lx, lz );
				double cyy = gAt( lx, lz );
				double cry = yaw + rand.range( - 0.3, 0.3 );
				ctx.inst.add( "crate", p[ 0 ], cyy, p[ 1 ], cry, new[] { rand.range( 0.85, 1.05 ), 0.95, 0.9 } );
			}

			return new BuildResult { footprint = new Footprint { x = x, z = z, r = JS.Hypot( w, d ) / 2 + 1.2, kind = "building" } };
		}

		// ---------------------------------------------------------------------------
		// Small lean-to garden / tool shed (local +z = door side)

		public static BuildResult buildShed( BuildCtx ctx, Spec s )
		{
			var B = ctx.B; var terrain = ctx.terrain; var colliders = ctx.colliders; var rand = ctx.rand; var checks = ctx.checks;
			double x = s.x, z = s.z, yaw = s.yaw;
			double w = s.w ?? 1.9, d = s.d ?? 1.7;
			frameFns( terrain, x, z, yaw, out var toW, out var gAt );
			double gMin = double.PositiveInfinity, gMax = double.NegativeInfinity;
			foreach ( var c in new[] { new[] { - 1.0, - 1.0 }, new[] { 1.0, - 1.0 }, new[] { 1.0, 1.0 }, new[] { - 1.0, 1.0 }, new[] { 0.0, 0.0 } } )
			{
				double g = gAt( c[ 0 ] * w / 2, c[ 1 ] * d / 2 );
				gMin = Math.Min( gMin, g ); gMax = Math.Max( gMax, g );
			}

			double bas = gMax + 0.22;
			double hF = 2.3, hB = 1.95, t = 0.05;
			var col = s.wall ?? lin( 0x9aa7a0 );
			double paint = s.paint ?? 0.45;
			double[] wd( double pat ) => new[] { rand.next(), paint, pat, 0.9 };
			B.pushAt( x, 0, z, yaw );
			foreach ( int sx in new[] { - 1, 1 } )
				foreach ( int sz in new[] { - 1, 1 } )
				{
					double px = sx * ( w / 2 - 0.05 ), pz = sz * ( d / 2 - 0.05 );
					double g = gAt( px, pz );
					double top = bas + ( sz > 0 ? hF : hB );
					B.box( "wood", px, ( g - 0.3 + top ) / 2, pz, 0.1, top - g + 0.3, 0.1, new O { grain = 1, data = WOOD( rand.next(), 0.9 ) } );
					var tw = toW( px, pz );
					checks.Add( new FoundationCheck { x = tw[ 0 ], y = g - 0.3, z = tw[ 1 ] } );
				}

			double skirt = bas - gMin + 0.1;
			B.box( "wood", 0, bas - skirt / 2 + hF / 2, d / 2 - t / 2, w, hF + skirt, t, new O { grain = 0, tint = col, data = wd( 2 ) } );
			B.box( "wood", 0, bas - skirt / 2 + hB / 2, - d / 2 + t / 2, w, hB + skirt, t, new O { grain = 0, tint = col, data = wd( 2 ) } );
			foreach ( int sx in new[] { - 1, 1 } )
			{
				double xo = sx * w / 2;
				var pts = new[] { V3( xo, bas - skirt, d / 2 - t ), V3( xo, bas - skirt, - d / 2 + t ), V3( xo, bas + hB, - d / 2 + t ), V3( xo, bas + hF, d / 2 - t ) };
				B.slab( "wood", pts, t, new O { up = V3( sx, 0, 0 ), uDir = V3( 0, 0, - sx ), tint = col, data = wd( 2 ) } );
			}

			// plank door with Z brace + hinges
			var dc = s.door ?? lin( 0x2d7f7a );
			B.box( "wood", 0.15, bas + 0.92, d / 2 + 0.012, 0.8, 1.8, 0.03, new O { grain = 1, tint = dc, data = new[] { rand.next(), 0.55, 3, 0.8 } } );
			B.beam( "wood", new[] { - 0.2, bas + 0.25, d / 2 + 0.035 }, new[] { 0.5, bas + 1.55, d / 2 + 0.035 }, 0.02, 0.1, new O { tint = dc, data = new[] { rand.next(), 0.55, 0, 0.8 } } );
			foreach ( double hy in new[] { 0.3, 1.5 } ) B.box( "hard", - 0.13, bas + hy, d / 2 + 0.03, 0.25, 0.04, 0.01, new O { tint = C.iron, data = HARD( rand.next(), 0.8, 0.5, 0.6 ) } );
			// shed roof
			var rf = new[] { V3( - w / 2 - 0.2, bas + hB + 0.02, - d / 2 - 0.25 ), V3( w / 2 + 0.2, bas + hB + 0.02, - d / 2 - 0.25 ), V3( w / 2 + 0.2, bas + hF + 0.08, d / 2 + 0.3 ), V3( - w / 2 - 0.2, bas + hF + 0.08, d / 2 + 0.3 ) };
			B.slab( "roofMetal", rf, 0.04, new O { up = UP, uDir = V3( 0, hF - hB, d + 0.55 ).normalize(), tint = s.roofColor ?? lin( 0x8a5a40 ), data = new[] { rand.next(), 0.8, s.galv ? 1.0 : 0.0, 0 } } );
			B.pop();
			var cc = toW( 0, 0 );
			colliders.addBox( new Vector3( cc[ 0 ], ( gMin - 0.3 + bas + hF ) / 2, cc[ 1 ] ), new Vector3( w / 2 + 0.05, ( bas + hF - gMin + 0.3 ) / 2, d / 2 + 0.05 ), yaw, false, true, "shed" );
			return new BuildResult { floorY = bas, roofTop = bas + hF + 0.1, footprint = new Footprint { x = x, z = z, r = JS.Hypot( w, d ) / 2 + 0.6, kind = "building" } };
		}
	}
}
