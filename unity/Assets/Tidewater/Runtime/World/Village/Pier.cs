using System;
using System.Collections.Generic;
using Tidewater.Engine;
using static Tidewater.World.Village.GeoBuilder;
using static Tidewater.World.Village.Props;

// Port of src/world/Pier.js: timber pier with a T-shaped head. Everything is procedural geometry written into a Builder;
// colliders are registered for decks, steps, rails, piles and props. Deck top is exactly WorldLayout.Pier.deckHeight everywhere
// (colliders tagged 'pierDeck'). The random calls keep the JS order, argument lists included: the village's sequence runs on.
namespace Tidewater.World.Village
{
	// a light the village registers (position is live for the swinging lanterns)
	public sealed class LightSource
	{
		public Vector3 position;
		public Color color;
		public double intensity;
		public string kind;
	}

	public sealed class HungLantern { public Builder B; public Vector3 pivot, rest, live; }

	public sealed class PierInfo
	{
		public List<Vector3> lamps = new List<Vector3>(), bollards = new List<Vector3>();
		public List<HungLantern> hung = new List<HungLantern>();
		public Vector3 signPivot, stepFoot, ladder;
	}

	public static class PierDims
	{
		public const double x = WorldLayout.Pier.x, zStart = WorldLayout.Pier.zStart, zEnd = WorldLayout.Pier.zEnd, deck = WorldLayout.Pier.deckHeight;
		public const double width = WorldLayout.Pier.width, halfW = WorldLayout.Pier.width / 2;
		public const double headZ0 = WorldLayout.Pier.zEnd - WorldLayout.Pier.headDepth;
		public const double headX0 = WorldLayout.Pier.x - WorldLayout.Pier.headWidth / 2, headX1 = WorldLayout.Pier.x + WorldLayout.Pier.headWidth / 2;
		public const double plankT = 0.055, pileR = 0.15, pileOff = WorldLayout.Pier.width / 2 + 0.17, stringerH = 0.22, capH = 0.26;
	}

	public static class Pier
	{
		const double ARCH_H = 2.75; // entrance arch beam height above the deck

		sealed class PileRec { public double x, z, sx, sz, yRef, bottom, top, rTop, rBot; }
		struct PileAtResult { public double x, z, off; }
		sealed class PieceOpts { public double[] tint, data; }

		public static PierInfo buildPier( Builder B, TerrainData terrain, Colliders colliders, Rand rand, List<LightSource> lights, InstancedProps inst, Builder signB = null, Func<Builder> hang = null )
		{
			const double X = PierDims.x, DK = PierDims.deck, halfW = PierDims.halfW;
			const double z0 = PierDims.zStart, zH = PierDims.headZ0, z1 = PierDims.zEnd;
			const double hx0 = PierDims.headX0, hx1 = PierDims.headX1;
			const double plankT = PierDims.plankT;
			const double stringerTop = DK - plankT;
			const double capTop = stringerTop - PierDims.stringerH;
			const double capBot = capTop - PierDims.capH;
			const double pileR = PierDims.pileR, pileOff = PierDims.pileOff, capH = PierDims.capH, stringerH = PierDims.stringerH;
			double ground( double x, double z ) => terrain.HeightAt( x, z );
			double[] pierWood( double w0 = 0.6, double w1 = 0.95 ) { double s = rand.next(); return WOOD( s, rand.range( w0, w1 ), 0, 0 ); }
			// timber tone per piece: most boards close to the average, some dark (water-stained, oily) or
			// pale (sun-bleached, recently planed) - an old pier is a patchwork of repairs
			double[] tone()
			{
				double k = rand.range( 0.84, 1.1 );
				double r = rand.next();
				if ( r < 0.1 ) k *= rand.range( 0.7, 0.82 );
				else if ( r > 0.94 ) k *= rand.range( 1.1, 1.2 );
				double w = rand.range( -0.02, 0.06 );
				return new[] { k * ( 1 + w ), k, k * ( 1 - w * 1.2 ) };
			}

			// a replacement board: fresh, warm, not yet silvered
			double[] freshTone() => new[] { rand.range( 1.04, 1.1 ), rand.range( 0.98, 1.02 ), rand.range( 0.9, 0.95 ) };
			var info = new PierInfo();

			void addBox( double cx, double cy, double cz, double hx, double hy, double hz, bool walkable = false, bool solid = true, string tag = "" )
				=> colliders.addBox( new Vector3( cx, cy, cz ), new Vector3( hx, hy, hz ), 0, walkable, solid, tag );

			// ------------------------------------------------------------------ piles
			var piles = new Dictionary<string, PileRec>();
			string pileKey( double px, double pz ) => toFixed( px, 3 ) + "," + toFixed( pz, 3 );
			// where a pile's axis is at height y, and the offset from it to the face a brace of thickness t sits on
			PileAtResult pileAt( double px, double pz, double y, double t = 0.05 )
			{
				var q = piles[ pileKey( px, pz ) ];
				double k = Math.Min( 1, Math.Max( 0, ( y - q.bottom ) / ( q.top - q.bottom ) ) );
				double r = q.rBot + ( q.rTop - q.rBot ) * k;
				return new PileAtResult { x = q.x + q.sx * ( y - q.yRef ), z = q.z + q.sz * ( y - q.yRef ), off = r + t * 0.3 };
			}

			var _e = new Euler();
			void pile( double px, double pz, double top, bool flatTop = false, double? rOpt = null )
			{
				double g = ground( px, pz );
				double bottom = g - 1.5;
				double r = rOpt ?? pileR * rand.range( 0.84, 1.16 );
				bool post = top > DK;
				double h = top - bottom;
				// driven piles wander: posts that carry a rail lean less
				double tilt = post ? 0.008 : 0.028;
				double dataSeed = rand.next();
				var data = WOOD( dataSeed, rand.range( 0.8, 1.0 ), 0, 0 );
				double rx = rand.range( -tilt, tilt ), rz = rand.range( -tilt, tilt );
				double rTop = r * rand.range( 0.86, 0.95 ), rBot = r * 1.06;
				// the pile leans about the height its cap beams / rail posts attach at, so the axis passes
				// through ( px, pz ) there; braces follow the leaning axis (pileAt) and stay attached
				var lean = new Vector3( 0, 1, 0 ).applyEuler( _e.set( rx, 0, rz, "YXZ" ) );
				double sx = lean.x / lean.y, sz = lean.z / lean.y;
				double yRef = post ? DK : capBot;
				piles[ pileKey( px, pz ) ] = new PileRec { x = px, z = pz, sx = sx, sz = sz, yRef = yRef, bottom = bottom, top = top, rTop = rTop, rBot = rBot };
				B.cyl( "wood", px - sx * ( yRef - bottom ), bottom, pz - sz * ( yRef - bottom ), rTop, rBot, h, new O
				{
					segs = 10, capTop = ! post || flatTop, rx = rx, rz = rz, tint = tone(), data = data,
				} );
				double wl = 0.0; // mean sea level
				// sistered repair: a shorter, thinner pile bolted alongside a rotten one
				if ( ! post && rand.chance( 0.18 ) && top - g > 2.5 )
				{
					double a = rand.range( 0, Math.PI * 2 ), d = r + 0.08;
					double sx2 = px + Math.Cos( a ) * d, sz2 = pz + Math.Sin( a ) * d;
					double sb = Math.Max( g - 0.5, bottom ), st = top - rand.range( 0.05, 0.4 );
					B.cyl( "wood", sx2, sb, sz2, 0.075, 0.085, st - sb, new O { segs = 7, rx = rand.range( -0.01, 0.01 ), rz = rand.range( -0.01, 0.01 ), tint = freshTone(), data = WOOD( rand.next(), rand.range( 0.2, 0.45 ), 0, 0 ) } );
					foreach ( double by in new[] { wl + 0.6, st - 0.3 } )
					{
						if ( by < sb + 0.2 ) continue;
						B.cyl( "hard", ( px + sx2 ) / 2, by, ( pz + sz2 ) / 2, 0.012, 0.012, d + 0.1, new O { segs = 5, rx = Math.PI / 2, ry = -a + Math.PI / 2, tint = C.iron, data = HARD( rand.next(), 0.95, 0.4, 0.7 ) } );
					}
				}

				// old mooring rope wrapped round a pile above the water, or a tyre slipped over it
				if ( ! post && rand.chance( 0.1 ) )
				{
					double y = wl + rand.range( 0.7, 1.4 );
					for ( int k = 0; k < 3; k ++ ) B.torus( "rope", px, y + k * 0.035, pz, r + 0.02, 0.018, new O { rx = rand.range( -0.08, 0.08 ), radial = 5, tubular = 14, tint = rand.chance( 0.5 ) ? C.rope : C.ropeDark, data = new[] { rand.next(), 0.8, 0, 0 } } );
				}
				else if ( ! post && rand.chance( 0.05 ) )
				{
					B.torus( "hard", px, wl + rand.range( 0.3, 0.9 ), pz, r + 0.2, 0.1, new O { rx = rand.range( -0.25, 0.25 ), rz = rand.range( -0.2, 0.2 ), radial = 6, tubular = 14, tint = C.rubber, data = HARD( rand.next(), 0, 0, 0.85 ) } );
				}

				if ( post && ! flatTop )
				{
					B.cyl( "wood", px, top, pz, r * 0.5, r * 0.93, 0.07, new O { segs = 10, tint = tone(), data = data } );
				}

				colliders.addCylinder( px, pz, r + 0.02, bottom, top, "pile" );
			}

			void bolt( double x, double y, double z, double dir )
			{
				B.cyl( "hard", x, y, z, 0.022, 0.026, 0.022, new O { segs = 5, rx = dir * Math.PI / 2, tint = C.iron, data = HARD( rand.next(), 0.85, 0.4, 0.6 ) } );
			}

			// double cap beam (one on each face of a pile row) running along x at z = bz
			void capRow( double bz, double xa, double xb, List<double> pileXs )
			{
				double off = pileR + 0.05;
				foreach ( int s in new[] { -1, 1 } )
				{
					B.box( "wood", ( xa + xb ) / 2, capBot + capH / 2, bz + s * off, xb - xa, capH, 0.09, new O { grain = 0, tint = tone(), data = pierWood( 0.7, 0.95 ) } );
					foreach ( double px in pileXs ) bolt( px, capBot + capH / 2, bz + s * ( off + 0.045 ), s );
				}

				// cap beams are solid obstacles for anyone walking / swimming beneath the deck
				addBox( ( xa + xb ) / 2, capBot + capH / 2, bz, ( xb - xa ) / 2, capH / 2, off + 0.05, tag: "pierCap" );
			}

			void xBrace( double xa, double xb, double bz, double clearBottom )
			{
				double yTop = capBot - 0.12;
				double yBot = Math.Max( clearBottom + 0.35, yTop - 4.2 );
				if ( yTop - yBot < 0.9 ) return;
				// bays differ: one brace lost to a storm, braces replaced at slightly different heights
				double r = rand.next();
				double j() => rand.range( -0.18, 0.18 );
				O piece() => rand.chance( 0.2 ) ? new O { tint = freshTone(), data = WOOD( rand.next(), rand.range( 0.25, 0.5 ), 0, 0 ) } : new O { tint = tone(), data = pierWood( 0.8, 1 ) };
				// each end is fixed to the face of its pile at that height (side = +1 / -1 of the pile row)
				double[] end( double px, double y, int side ) { var a = pileAt( px, bz, y ); return new[] { a.x, y, a.z + side * a.off }; }
				if ( r > 0.12 ) { double ya = yTop + j(), yb = yBot + j(); B.beam( "wood", end( xa, ya, 1 ), end( xb, yb, 1 ), 0.05, rand.range( 0.17, 0.22 ), piece() ); }
				if ( r < 0.84 ) { double ya = yBot + j(), yb = yTop + j(); B.beam( "wood", end( xa, ya, -1 ), end( xb, yb, -1 ), 0.05, rand.range( 0.17, 0.22 ), piece() ); }
				// a horizontal waler here and there, lapped over the pile faces
				if ( rand.chance( 0.25 ) )
				{
					double y = yBot + 0.3; int side = r > 0.12 ? 1 : -1;
					var a = end( xa, y, side ); var b = end( xb, y + rand.range( -0.06, 0.06 ), side );
					a[ 0 ] -= 0.1; b[ 0 ] += 0.1; a[ 2 ] += side * 0.05; b[ 2 ] += side * 0.05;
					B.beam( "wood", a, b, 0.05, 0.18, new O { tint = tone(), data = pierWood( 0.85, 1 ) } );
				}
			}

			// ------------------------------------------------------------------ walkway bents
			var bentZ = new List<double>();
			double zb0 = z0 + 0.35, zbN = zH + 0.25;
			int nB = ( int ) JS.Round( ( zbN - zb0 ) / 3.0 );
			for ( int i = 0; i <= nB; i ++ ) bentZ.Add( zb0 + i * ( zbN - zb0 ) / nB );
			int lastBent = bentZ.Count - 1;
			var swayBraces = new List<Action>();

			var gapNeg = new[] { 9, 10, 20 }; var gapPos = new[] { 14, 15, 26 };
			bool railBay( int side, int i ) => i >= 1 && i < lastBent && Array.IndexOf( side < 0 ? gapNeg : gapPos, i ) < 0;

			for ( int bi = 0; bi < bentZ.Count; bi ++ )
			{
				double bz = bentZ[ bi ];
				foreach ( int side in new[] { -1, 1 } )
				{
					double px = X + side * pileOff;
					bool needPost = railBay( side, bi ) || railBay( side, bi - 1 ) || bi == 0 || bi == lastBent;
					double top = capTop;
					if ( needPost ) top = DK + ( bi == 0 ? ARCH_H + 0.25 : 0.95 );
					else if ( bi % 3 == 1 ) top = DK + 0.45;
					pile( px, bz, top, needPost && bi != 0, bi == 0 ? 0.17 : ( double? ) null );
				}

				if ( bi != lastBent )
				{
					double xa = X - pileOff - 0.3, xb = X + pileOff + 0.3;
					capRow( bz, xa, xb, new List<double> { X - pileOff, X + pileOff } );
					double gMin = Math.Min( ground( X - pileOff, bz ), ground( X + pileOff, bz ) );
					xBrace( X - pileOff, X + pileOff, bz, gMin );
				}

				// longitudinal sway bracing on alternate bays
				if ( bi < lastBent && bi % 2 == 0 )
				{
					double bz2 = bentZ[ bi + 1 ];
					foreach ( int side in new[] { -1, 1 } )
					{
						double g = Math.Max( ground( X + side * pileOff, bz ), ground( X + side * pileOff, bz2 ) );
						double yTop = capBot - 0.2, yBot = Math.Max( g + 0.4, yTop - 3.2 );
						if ( yTop - yBot > 0.9 && rand.chance( 0.85 ) )
						{
							// from the outer face of one pile to the outer face of the next, ends on the pile axes;
							// placed once the next bent's piles exist (swayBraces below)
							bool flip = rand.chance( 0.3 );
							double px = X + side * pileOff;
							double ya = flip ? yBot + rand.range( 0, 0.2 ) : yTop - rand.range( 0, 0.2 );
							double yb = flip ? yTop - rand.range( 0, 0.2 ) : yBot + rand.range( 0, 0.2 );
							double w = rand.range( 0.17, 0.22 ); var opts = new O { tint = tone(), data = pierWood( 0.8, 1 ) };
							int sideC = side; double bzC = bz, bz2C = bz2;
							swayBraces.Add( () =>
							{
								var a = pileAt( px, bzC, ya ); var b = pileAt( px, bz2C, yb );
								B.beam( "wood", P( a.x + sideC * a.off, ya, a.z ), P( b.x + sideC * b.off, yb, b.z ), 0.05, w, opts );
							} );
						}
					}
				}
			}

			foreach ( var place in swayBraces ) place();

			// stringers per bay (4 lines), extend onto the head's north cap row
			var stringerXs = new[] { -1.2, -0.42, 0.42, 1.2 };
			for ( int bi = 0; bi < lastBent; bi ++ )
			{
				double za = bi == 0 ? z0 + 0.02 : bentZ[ bi ];
				double zb = bentZ[ bi + 1 ];
				foreach ( double sx in stringerXs )
				{
					B.box( "wood", X + sx, stringerTop - stringerH / 2, ( za + zb ) / 2, 0.1, stringerH, zb - za + 0.04, new O { grain = 2, tint = tone(), data = pierWood( 0.7, 0.95 ) } );
				}
			}

			// One deck board centred on (cx, zc), length L along x. Old decking is irregular: boards shrink,
			// cup and twist, ends don't line up, some have been replaced with fresh timber, some are split
			// and a very few are gone. Walkway boards get a worn path down the middle (passed to the wood
			// material as paint = -( 1 + path centre u ), see VillageMaterials).
			void plank( double cx, double zc, double L, bool head )
			{
				if ( rand.chance( head ? 0.006 : 0.012 ) ) return; // missing board
				bool newer = rand.chance( 0.07 );
				double wth = newer ? rand.range( 0.25, 0.45 ) : rand.range( 0.55, 1.0 );
				var t = newer ? freshTone() : tone();
				// boards nearly fill the 0.215 m pitch (gaps ~0.5-2.5 cm): wider gaps turned the distant deck
				// into rows of sub-pixel dark lines that no anti-aliasing kept steady
				double wdt = rand.range( 0.198, 0.206 );
				double yOff = rand.range( 0, 0.014 ) + ( rand.chance( 0.08 ) ? rand.range( 0.006, 0.016 ) : 0 );
				double e0 = rand.range( -0.06, 0.05 ), e1 = rand.range( -0.05, 0.06 );
				double xa = cx - L / 2 + e0, xb = cx + L / 2 + e1;
				O opts() => new O { grain = 0, ry = rand.range( -0.012, 0.012 ), rx = rand.range( -0.022, 0.022 ), rz = rand.range( -0.006, 0.006 ), tint = t };
				double walk( double u0 ) => head ? 0 : -( 1 + ( X - u0 ) ); // path centre along this piece
				double zj = zc + rand.range( -0.004, 0.004 );
				if ( ! newer && rand.chance( 0.05 ) )
				{
					// split board: two pieces with a ragged gap
					double xs = rand.range( xa + 0.4, xb - 0.4 );
					{
						double a1 = ( xa + xs - 0.01 ) / 2;
						var oo = opts(); oo.data = WOOD( rand.next(), wth, walk( xa ), 7 );
						B.box( "wood", a1, DK - plankT / 2 - yOff, zj, xs - xa - 0.01, plankT, wdt, oo );
					}
					{
						double cx2 = ( xs + 0.012 + xb ) / 2; double zz = zj + rand.range( -0.004, 0.004 ); double wd2 = wdt * rand.range( 0.92, 1 );
						var oo = opts(); oo.data = WOOD( rand.next(), wth, walk( xs + 0.012 ), 7 );
						B.box( "wood", cx2, DK - plankT / 2 - yOff - 0.004, zz, xb - xs - 0.012, plankT, wd2, oo );
					}

					return;
				}

				{
					var oo = opts(); oo.data = WOOD( rand.next(), wth, walk( xa ), 7 );
					B.box( "wood", ( xa + xb ) / 2, DK - plankT / 2 - yOff, zj, xb - xa, plankT, wdt, oo );
				}
			}

			// deck planks (walkway)
			double pitch = 0.215;
			int nPl = ( int ) Math.Floor( ( zH - z0 ) / pitch );
			for ( int i = 0; i < nPl; i ++ )
			{
				double zc = z0 + ( i + 0.5 ) * ( zH - z0 ) / nPl;
				plank( X, zc, PierDims.width, false );
			}

			// walkway deck collider
			addBox( X, DK - 0.04, ( z0 + zH ) / 2, halfW, 0.04, ( zH - z0 ) / 2, true, true, "pierDeck" );

			// ------------------------------------------------------------------ railings (walkway)
			double railTop = DK + 0.95;
			for ( int bi = 0; bi < lastBent; bi ++ )
			{
				foreach ( int side in new[] { -1, 1 } )
				{
					if ( ! railBay( side, bi ) ) continue;
					double za = bentZ[ bi ], zb = bentZ[ bi + 1 ];
					double px = X + side * pileOff;
					double len = zb - za;
					bool fresh = rand.chance( 0.12 );
					B.box( "wood", px + rand.range( -0.01, 0.01 ), railTop + 0.022 + rand.range( -0.012, 0.01 ), ( za + zb ) / 2, 0.2, 0.045, len + 0.16, new O
					{
						grain = 2, rx = rand.range( -0.006, 0.006 ), rz = rand.range( -0.01, 0.01 ), tint = fresh ? freshTone() : tone(), data = fresh ? WOOD( rand.next(), 0.3, 0, 0 ) : pierWood( 0.7, 1 ),
					} );
					double mx = px - side * ( pileR + 0.03 );
					// the mid rail: a few have come off one end and hang, one or two are gone
					double mr = rand.next();
					if ( mr > 0.04 )
					{
						double drop = mr < 0.1 ? rand.range( 0.15, 0.35 ) : 0;
						bool dropA = rand.chance( 0.5 );
						var pA = P( mx, DK + 0.48 + rand.range( -0.012, 0.012 ) - ( dropA ? drop : 0 ), za - 0.05 );
						var pB = P( mx, DK + 0.48 + rand.range( -0.012, 0.012 ) - ( dropA ? 0 : drop ), zb + 0.05 );
						B.beam( "wood", pA, pB, 0.05, 0.14, new O { tint = tone(), data = pierWood( 0.7, 1 ) } );
					}

					addBox( px, DK + 0.55, ( za + zb ) / 2, 0.09, 0.55, len / 2, tag: "pierRail" );
				}
			}

			// lamp posts along the walkway (inside the rail, arm over the deck)
			foreach ( var bs in new[] { new[] { 3, -1 }, new[] { 8, 1 }, new[] { 13, -1 }, new[] { 18, 1 }, new[] { 23, -1 }, new[] { 28, 1 } } )
			{
				int bi = bs[ 0 ], side = bs[ 1 ];
				if ( bi >= lastBent ) continue;
				double lx = X + side * ( halfW - 0.1 ), lz = bentZ[ bi ] + 0.32;
				double lseed = rand.next();
				var w = hang != null ? hungLampPost( B, hang, info, lx, DK, lz, -side * Math.PI / 2, 3.1, lseed ) : lampPost( B, lx, DK, lz, -side * Math.PI / 2, 3.1, lseed );
				lights.Add( new LightSource { position = w, color = new Color( 1.0, 0.72, 0.42 ), intensity = 5, kind = "lantern" } );
				info.lamps.Add( w );
				colliders.addCylinder( lx, lz, 0.1, DK, DK + 3.2, "lampPost" );
			}

			// ------------------------------------------------------------------ entrance arch with a painted fish sign
			{
				double az = bentZ[ 0 ];
				double by = DK + ARCH_H;
				double[] wd() => WOOD( rand.next(), 0.7, 0.6, 0 );
				var white = lin( 0xefe9dc ); var teal = lin( 0x2f8f8c ); var coral = lin( 0xe07a5f );
				B.box( "wood", X, by, az, PierDims.width + 1.1, 0.24, 0.16, new O { grain = 0, tint = white, data = wd() } );
				B.box( "wood", X, by + 0.2, az, PierDims.width + 1.35, 0.06, 0.22, new O { grain = 0, tint = white, data = wd() } );
				foreach ( int s in new[] { -1, 1 } )
				{
					B.beam( "wood", P( X + s * ( pileOff - 0.12 ), by - 0.62, az ), P( X + s * ( pileOff - 0.62 ), by - 0.1, az ), 0.1, 0.09, new O { tint = white, data = wd() } );
				}

				// sign board hung under the beam on two short chains (built into its own builder when given:
				// the village swings it in the wind about the tops of the chains, info.signPivot)
				var S = signB ?? B;
				double sy = by - 0.52;
				info.signPivot = new Vector3( X, by - 0.12, az + 0.02 );
				S.box( "wood", X, sy, az + 0.02, 1.9, 0.52, 0.05, new O { grain = 0, tint = teal, data = WOOD( rand.next(), 0.6, 0.72, 6 ) } );
				S.box( "wood", X, sy, az + 0.02, 1.98, 0.6, 0.03, new O { grain = 0, tint = white, data = wd() } );
				foreach ( double s in new[] { -0.7, 0.7 } ) S.rod( "hard", P( X + s, sy + 0.26, az + 0.02 ), P( X + s, by - 0.12, az + 0.02 ), 0.008, 0.008, new O { segs = 4, tint = C.iron, data = HARD( rand.next(), 0.6, 0.6, 0.5 ) } );
				// fish silhouette (both faces)
				foreach ( int f in new[] { 1, -1 } )
				{
					double zf = az + 0.02 + f * 0.032;
					var body = new Vector3[ 14 ];
					for ( int i = 0; i < 14; i ++ )
					{
						double a = ( double ) i / 14 * Math.PI * 2;
						body[ i ] = new Vector3( X - 0.1 + Math.Cos( a ) * 0.5, sy + Math.Sin( a ) * 0.17, zf );
					}

					S.slab( "wood", body, 0.01, new O { up = new Vector3( 0, 0, f ), uDir = new Vector3( 1, 0, 0 ), tint = coral, data = WOOD( rand.next(), 0.6, 0.75, 0 ) } );
					S.slab( "wood", new[] { new Vector3( X + 0.36, sy, zf ), new Vector3( X + 0.72, sy + 0.2, zf ), new Vector3( X + 0.66, sy, zf ) }, 0.01, new O { up = new Vector3( 0, 0, f ), tint = coral, data = WOOD( rand.next(), 0.6, 0.75, 0 ) } );
					S.slab( "wood", new[] { new Vector3( X + 0.36, sy, zf ), new Vector3( X + 0.66, sy, zf ), new Vector3( X + 0.72, sy - 0.2, zf ) }, 0.01, new O { up = new Vector3( 0, 0, f ), tint = coral, data = WOOD( rand.next(), 0.6, 0.75, 0 ) } );
					S.cyl( "hard", X - 0.42, sy + 0.04, zf + f * 0.006, 0.035, 0.035, 0.008, new O { segs = 8, rx = f * Math.PI / 2, tint = C.black, data = HARD( rand.next(), 0, 0, 0.4 ) } );
				}

				// lanterns hanging from the beam next to each post
				foreach ( int s in new[] { -1, 1 } )
				{
					// outside the posts on the landward side, under the beam's overhang
					double hx = X + s * ( pileOff + 0.3 ), hz = az - 0.07;
					Vector3 pos;
					if ( hang != null )
					{
						B.torus( "hard", hx, by - 0.125, hz, 0.02, 0.005, new O { rx = Math.PI / 2, radial = 3, tubular = 6, tint = C.iron, data = HARD( 0.5, 0.35, 0.5, 0.45 ) } );
						pos = hangLantern( hang, info, new Vector3( hx, by - 0.12, hz ), rand.next(), 0.9, 0.1 );
					}
					else
					{
						var c = lantern( B, hx, by - 0.12, hz, rand.next(), 0.9 );
						pos = new Vector3( c[ 0 ], c[ 1 ], c[ 2 ] );
					}

					lights.Add( new LightSource { position = pos, color = new Color( 1.0, 0.72, 0.42 ), intensity = 4, kind = "lantern" } );
				}

				addBox( X, by, az, PierDims.width / 2 + 0.6, 0.15, 0.1, tag: "pierArch" );
			}

			// ------------------------------------------------------------------ landward steps
			{
				double gs = ground( X, z0 - 0.7 );
				double rise = DK - gs;
				int nR = ( int ) Math.Max( 2, Math.Ceiling( rise / 0.22 ) );
				double rh = rise / nR;
				double tread = 0.32;
				double run = ( nR - 1 ) * tread;
				for ( int k = 1; k < nR; k ++ )
				{
					double top = DK - k * rh;
					double zc = z0 - ( k - 0.5 ) * tread;
					foreach ( double dz in new[] { -0.078, 0.078 } )
					{
						B.box( "wood", X, top - 0.024, zc + dz, PierDims.width - 0.1, 0.048, 0.15, new O { grain = 0, tint = tone(), data = pierWood( 0.5, 0.9 ) } );
					}

					double gb = ground( X, zc );
					addBox( X, ( top + gb - 0.4 ) / 2, zc, halfW, ( top - gb + 0.4 ) / 2, tread / 2 + 0.005, true, true, "pierStep" );
				}

				foreach ( int side in new[] { -1, 1 } )
				{
					double sx = X + side * ( halfW - 0.04 );
					double gEnd = ground( sx, z0 - run - 0.2 );
					B.beam( "wood", P( sx, DK - 0.12, z0 + 0.15 ), P( sx, gEnd - 0.05, z0 - run - 0.25 ), 0.07, 0.26, new O { tint = tone(), data = pierWood( 0.7, 0.95 ) } );
				}

				info.stepFoot = new Vector3( X, ground( X, z0 - run - 0.3 ), z0 - run - 0.3 );
			}

			// ------------------------------------------------------------------ T-head
			var rowsZ = new[] { zH + 0.25, ( zH + z1 ) / 2, z1 - 0.25 };
			var colsX = new double[ 5 ];
			for ( int k = 0; k < 5; k ++ ) colsX[ k ] = hx0 + 0.3 + k * ( hx1 - hx0 - 0.6 ) / 4;

			for ( int r = 0; r < 3; r ++ )
			{
				double rz = rowsZ[ r ];
				var xs = new List<double>();
				for ( int k = 0; k < 5; k ++ )
				{
					double px = colsX[ k ];
					if ( r == 0 && k == 2 ) continue; // walkway piles stand here
					bool perimeter = r == 0 || r == 2 || k == 0;
					bool post = perimeter && ! ( k == 4 && r == 1 );
					pile( px, rz, post ? DK + 0.95 : capTop, post );
					xs.Add( px );
				}

				if ( r == 0 ) { xs.Add( X - pileOff ); xs.Add( X + pileOff ); }
				capRow( rz, hx0 - 0.08, hx1 + 0.08, xs );
				for ( int k = 0; k < 4; k ++ )
				{
					if ( r == 0 && ( k == 1 || k == 2 ) ) continue;
					double gMin = Math.Min( ground( colsX[ k ], rz ), ground( colsX[ k + 1 ], rz ) );
					xBrace( colsX[ k ], colsX[ k + 1 ], rz, gMin );
				}
			}

			// head stringers along z
			for ( double sx = hx0 + 0.25; sx < hx1 - 0.1; sx += 0.78 )
			{
				B.box( "wood", sx, stringerTop - stringerH / 2, ( zH + z1 ) / 2, 0.1, stringerH, z1 - zH - 0.05, new O { grain = 2, tint = tone(), data = pierWood( 0.7, 0.95 ) } );
			}

			// head planks (along x, staggered butt joints)
			int nRows = ( int ) Math.Floor( ( z1 - zH ) / pitch );
			for ( int r = 0; r < nRows; r ++ )
			{
				double zc = zH + ( r + 0.5 ) * ( z1 - zH ) / nRows;
				var joints = new List<double> { hx0 };
				double jx = hx0 + 2.1 + ( r % 3 ) * 1.45;
				while ( jx < hx1 - 1.2 )
				{
					joints.Add( jx );
					jx += 4.35;
				}

				joints.Add( hx1 );
				for ( int j = 0; j < joints.Count - 1; j ++ )
				{
					double xa = joints[ j ] + 0.004, xb = joints[ j + 1 ] - 0.004;
					plank( ( xa + xb ) / 2, zc, xb - xa - 0.1, true );
				}
			}

			addBox( X, DK - 0.04, ( zH + z1 ) / 2, ( hx1 - hx0 ) / 2, 0.04, ( z1 - zH ) / 2, true, true, "pierDeck" );

			// head railings: south edge, west edge, north edge (both sides of the walkway)
			void railRun( double ax, double az, double bx, double bz )
			{
				double len = JS.Hypot( bx - ax, bz - az );
				bool alongX = Math.Abs( bx - ax ) > Math.Abs( bz - az );
				double cx = ( ax + bx ) / 2, cz = ( az + bz ) / 2;
				B.box( "wood", cx, railTop + 0.022, cz, alongX ? len + 0.16 : 0.2, 0.045, alongX ? 0.2 : len + 0.16, new O { grain = alongX ? 0 : 2, tint = tone(), data = pierWood( 0.65, 0.9 ) } );
				B.box( "wood", cx, DK + 0.48, cz, alongX ? len : 0.05, 0.14, alongX ? 0.05 : len, new O { grain = alongX ? 0 : 2, tint = tone(), data = pierWood( 0.65, 0.95 ) } );
				addBox( cx, DK + 0.55, cz, alongX ? len / 2 : 0.09, 0.55, alongX ? 0.09 : len / 2, tag: "pierRail" );
			}

			for ( int k = 0; k < 4; k ++ ) railRun( colsX[ k ], rowsZ[ 2 ], colsX[ k + 1 ], rowsZ[ 2 ] );
			railRun( colsX[ 0 ], rowsZ[ 0 ], colsX[ 0 ], rowsZ[ 1 ] );
			railRun( colsX[ 0 ], rowsZ[ 1 ], colsX[ 0 ], rowsZ[ 2 ] );
			railRun( colsX[ 0 ], rowsZ[ 0 ], colsX[ 1 ], rowsZ[ 0 ] );
			railRun( colsX[ 1 ], rowsZ[ 0 ], X - pileOff, rowsZ[ 0 ] );
			railRun( X + pileOff, rowsZ[ 0 ], colsX[ 3 ], rowsZ[ 0 ] );
			railRun( colsX[ 3 ], rowsZ[ 0 ], colsX[ 4 ], rowsZ[ 0 ] );

			// ------------------------------------------------------------------ T-head outfitting
			double edgeX = hx1; // boat side (east)
			// mooring bollards + cleat, with rope loops
			foreach ( double bz in new[] { 34.3, 38.7 } )
			{
				double bx = edgeX - 0.45;
				bollard( B, bx, DK, bz, rand.next() );
				ropeLoop( B, bx, DK + 0.36, bz, 0.155, null, rand.next() );
				ropeCoil( B, bx - 0.75, DK, bz + ( bz < 36 ? 0.35 : -0.35 ), 0.08, 0.3, 4, rand.next() );
				colliders.addCylinder( bx, bz, 0.22, DK, DK + 0.5, "bollard" );
				info.bollards.Add( new Vector3( bx, DK + 0.45, bz ) );
			}

			cleat( B, edgeX - 0.22, DK, 36.5, Math.PI / 2, rand.next() );
			info.bollards.Add( new Vector3( edgeX - 0.22, DK + 0.1, 36.5 ) );

			// tyre fenders on the berthing face
			foreach ( double fz in new[] { 33.75, 36.0, 37.35, 39.25 } ) tireFender( B, edgeX, DK - 0.04, fz, 0, rand.range( 0.65, 0.85 ), rand.next() );

			// ladder down into the water on the boat side
			{
				double lz = 35.1, lx = edgeX + 0.07;
				double yb = -2.2, yt = DK + 0.85;
				var t = C.galv; var d = HARD( rand.next(), 0.45, 0.85, 0.4 );
				foreach ( int s in new[] { -1, 1 } )
				{
					double rzs = lz + s * 0.24;
					B.rod( "hard", P( lx, yb, rzs ), P( lx, yt - 0.15, rzs ), 0.024, 0.024, new O { segs = 7, tint = t, data = d } );
					var arc = new List<Vector3>();
					for ( int i = 0; i <= 6; i ++ )
					{
						double a = ( double ) i / 6 * Math.PI;
						arc.Add( new Vector3( lx - 0.25 + Math.Cos( a ) * 0.25, yt - 0.15 + Math.Sin( a ) * 0.15, rzs ) );
					}

					B.tube( "hard", arc, 0.024, new O { radial = 6, tint = t, data = d } );
					B.rod( "hard", P( lx - 0.5, yt - 0.15, rzs ), P( lx - 0.5, DK, rzs ), 0.024, 0.024, new O { segs = 7, tint = t, data = d } );
					B.box( "hard", lx - 0.5, DK + 0.004, rzs, 0.12, 0.008, 0.12, new O { tint = t, data = d } );
					B.box( "hard", lx - 0.1, capBot + 0.12, rzs, 0.2, 0.05, 0.03, new O { tint = t, data = d } );
				}

				var rungTops = new List<double>();
				for ( double y = yb + 0.25; y < DK - 0.1; y += 0.3 )
				{
					B.rod( "hard", P( lx, y, lz - 0.24 ), P( lx, y, lz + 0.24 ), 0.018, 0.018, new O { segs = 6, tint = t, data = d } );
					rungTops.Add( y );
				}

				// walkable "stair" boxes so a swimmer can climb out (and walk down from the deck)
				foreach ( double y in rungTops )
				{
					if ( y < -0.4 ) continue;
					colliders.addBox( new Vector3( lx + 0.25, y - 0.15, lz ), new Vector3( 0.27, 0.15, 0.3 ), 0, true, false, "ladder" );
				}

				info.ladder = new Vector3( lx, DK, lz );
			}

			// south edge: bench facing the sea, life ring, fish cleaning table, fishing rods
			bench( B, 52.4, DK, z1 - 0.95, 0, 1.7, rand.next() );
			addBox( 52.4, DK + 0.45, z1 - 1.0, 0.85, 0.45, 0.3, tag: "bench" );
			lifeRing( B, 55.8, DK + 0.58, rowsZ[ 2 ] + 0.12, 0, rand.next() );
			cleaningTable( B, 58.3, DK, z1 - 0.9, 0, rand.next() );
			addBox( 58.3, DK + 0.45, z1 - 0.9, 0.7, 0.45, 0.35, tag: "table" );
			bucket( B, 59.3, DK, z1 - 0.75, C.blue, rand.next() );
			foreach ( var rl in new[] { new[] { 56.55, 0.35 }, new[] { 56.9, 0.28 } } )
			{
				double rx = rl[ 0 ], lean = rl[ 1 ];
				var bas = P( rx, DK + 0.01, rowsZ[ 2 ] - 0.25 );
				var tip = P( rx + 0.25, DK + 2.6, rowsZ[ 2 ] + 2.6 * Math.Tan( lean ) );
				B.rod( "hard", bas, tip, 0.016, 0.005, new O { segs = 5, tint = lin( 0x2a2a2a ), data = HARD( rand.next(), 0, 0.3, 0.35 ) } );
				B.cyl( "hard", rx + 0.02, DK + 0.35, rowsZ[ 2 ] - 0.18, 0.04, 0.04, 0.05, new O { segs = 8, rz = Math.PI / 2, capBot = true, tint = C.galv, data = HARD( rand.next(), 0, 0.8, 0.35 ) } );
			}

			// deck clutter (instanced crates / traps / barrels)
			void crate( double x, double y, double z, double ry ) => inst.add( "crate", x, y, z, ry, new[] { rand.range( 0.85, 1.1 ), rand.range( 0.85, 1.05 ), rand.range( 0.82, 1.0 ) } );
			crate( 49.25, DK, 34.35, 0.05 );
			crate( 49.25, DK, 34.8, -0.04 );
			crate( 49.95, DK, 34.55, 1.57 );
			crate( 49.3, DK + 0.4, 34.55, 0.2 );
			addBox( 49.55, DK + 0.4, 34.6, 0.55, 0.4, 0.45, tag: "crates" );

			void trap( double x, double y, double z, double ry ) => inst.add( "trap", x, y, z, ry, new[] { rand.range( 0.85, 1.05 ), rand.range( 0.85, 1.0 ), rand.range( 0.8, 0.95 ) } );
			trap( 50.1, DK, 38.55, 0.02 );
			trap( 50.1, DK, 39.1, -0.03 );
			trap( 51.05, DK, 38.6, 0.05 );
			trap( 50.3, DK + 0.31, 38.8, 0.12 );
			trap( 50.5, DK + 0.31, 38.75, 1.2 );
			addBox( 50.55, DK + 0.35, 38.8, 0.75, 0.35, 0.5, tag: "traps" );

			inst.add( "barrel", 48.75, DK, 36.1, 0.3, new[] { 0.9, 0.85, 0.8 } );
			inst.add( "barrel", 49.4, DK, 36.75, 1.1, new[] { 1.05, 1.0, 0.95 } );
			colliders.addCylinder( 48.75, 36.1, 0.32, DK, DK + 0.9, "barrel" );
			colliders.addCylinder( 49.4, 36.75, 0.32, DK, DK + 0.9, "barrel" );

			// floats hanging on the west rail
			buoyString( B, P( hx0 + 0.12, railTop + 0.02, 34.0 ), P( hx0 + 0.12, railTop + 0.02, 35.9 ), 4, rand, 0.22 );
			buoyString( B, P( hx0 + 0.12, railTop + 0.02, 37.1 ), P( hx0 + 0.12, railTop + 0.02, 38.9 ), 3, rand, 0.18 );

			// head lamps
			foreach ( var hl in new[] { new[] { hx0 + 0.55, z1 - 0.55, 3 * Math.PI / 4 }, new[] { hx1 - 0.6, z1 - 0.55, -3 * Math.PI / 4 }, new[] { hx0 + 0.55, zH + 0.6, Math.PI / 4 } } )
			{
				double lx = hl[ 0 ], lz = hl[ 1 ], yaw = hl[ 2 ];
				double lseed = rand.next();
				var w = hang != null ? hungLampPost( B, hang, info, lx, DK, lz, yaw, 3.1, lseed ) : lampPost( B, lx, DK, lz, yaw, 3.1, lseed );
				lights.Add( new LightSource { position = w, color = new Color( 1.0, 0.72, 0.42 ), intensity = 5, kind = "lantern" } );
				info.lamps.Add( w );
				colliders.addCylinder( lx, lz, 0.1, DK, DK + 3.2, "lampPost" );
			}

			// a few things along the walkway: gear left where it was last used
			bucket( B, X + 0.9, DK, -21.3, C.white, rand.next() );
			bucket( B, X - 0.95, DK, 12.6, C.orange, rand.next() );
			ropeCoil( B, X + 0.85, DK, -33.0, 0.07, 0.24, 3, rand.next(), C.ropeDark );
			ropeCoil( B, X - 0.85, DK, 21.5, 0.06, 0.22, 4, rand.next() );
			foreach ( var cs in new[] { new[] { -47.3, 1 }, new[] { -15.4, -1 }, new[] { 27.8, 1 } } )
			{
				double cz = cs[ 0 ], side = cs[ 1 ];
				// a cleat on the deck edge with a line still made fast, trailing over the side
				double cx = X + side * ( halfW - 0.18 );
				cleat( B, cx, DK, cz, 0, rand.next() );
				ropeLoop( B, cx, DK + 0.06, cz, 0.08, P( X + side * ( halfW + 0.35 ), DK - 1.4, cz + rand.range( -0.4, 0.4 ) ), rand.next() );
			}

			inst.add( "crate", X - 0.95, DK, -3.1, 1.4, new[] { 0.9, 0.85, 0.8 } );
			inst.add( "crate", X - 0.92, DK + 0.4, -3.05, 1.2, new[] { 1.0, 0.95, 0.9 } );
			colliders.addBox( new Vector3( X - 0.95, DK + 0.4, -3.1 ), new Vector3( 0.3, 0.4, 0.35 ), 0, tag: "crate" );
			inst.add( "trap", X + 0.8, DK, 30.4, 0.05, new[] { 0.9, 0.9, 0.85 } );
			colliders.addBox( new Vector3( X + 0.8, DK + 0.25, 30.4 ), new Vector3( 0.48, 0.3, 0.28 ), 0.05, tag: "trap" );
			ropeCoil( B, X - 0.8, DK, 4.5, 0.07, 0.26, 4, rand.next(), C.ropeBlue );
			inst.add( "crate", X + 0.95, DK, -40.2, 0.1, new[] { 1, 0.95, 0.9 } );
			colliders.addBox( new Vector3( X + 0.95, DK + 0.2, -40.2 ), new Vector3( 0.33, 0.2, 0.24 ), 0.1, tag: "crate" );

			return info;
		}

		// Lamp post (as Props.lampPost) whose lantern hangs from the end of the arm on a short chain, built
		// into its own builder (hang()) so the village can swing it in the wind. Returns the lantern centre
		// (a live vector: the village moves it with the swing, the light follows).
		static Vector3 hungLampPost( Builder B, Func<Builder> hang, PierInfo info, double x, double y, double z, double armYaw, double h, double seed )
		{
			var wd = WOOD( seed, 0.75, 0.62, 0 );
			var pc = lin( 0xd6cfbd );
			B.box( "wood", x, y + h / 2, z, 0.14, h, 0.14, new O { grain = 1, tint = pc, data = wd } );
			B.box( "wood", x, y + h + 0.03, z, 0.18, 0.06, 0.18, new O { grain = 0, tint = pc, data = wd } );
			B.pushAt( x, y, z, armYaw );
			var t = C.iron; var d = HARD( seed, 0.35, 0.5, 0.45 );
			double ay = h - 0.18;
			B.box( "hard", 0, ay, 0.35, 0.035, 0.035, 0.62, new O { tint = t, data = d } );
			B.rod( "hard", P( 0, ay - 0.35, 0.075 ), P( 0, ay - 0.01, 0.4 ), 0.012, 0.012, new O { segs = 5, tint = t, data = d } );
			B.torus( "hard", 0, ay - 0.08, 0.2, 0.08, 0.008, new O { ry = Math.PI / 2, rz = Math.PI / 2, radial = 3, tubular = 8, tint = t, data = d } );
			// eye bolt under the arm end
			B.torus( "hard", 0, ay - 0.03, 0.6, 0.014, 0.004, new O { ry = Math.PI / 2, radial = 3, tubular = 6, tint = t, data = d } );
			var hook = B.toWorld( 0, ay - 0.04, 0.6 );
			B.pop();
			return hangLantern( hang, info, hook, seed, 1, 0.16 );
		}

		// chain of `chain` metres from `hook` (world) and a lantern under it, into a new builder
		static Vector3 hangLantern( Func<Builder> hang, PierInfo info, Vector3 hook, double seed, double scale, double chain )
		{
			var LB = hang();
			var t = C.iron; var d = HARD( seed, 0.3, 0.55, 0.45 );
			double pitch = 0.03;
			int n = ( int ) Math.Max( 2, JS.Round( chain / pitch ) );
			for ( int i = 0; i < n; i ++ )
			{
				// interlocking links, alternately turned 90 degrees
				LB.torus( "hard", hook.x, hook.y - 0.012 - i * pitch, hook.z, 0.012, 0.0032, new O { ry = i % 2 != 0 ? Math.PI / 2 : 0, radial = 3, tubular = 6, tint = t, data = d } );
			}

			var c = lantern( LB, hook.x, hook.y - n * pitch, hook.z, seed, scale );
			var center = new Vector3( c[ 0 ], c[ 1 ], c[ 2 ] );
			info.hung.Add( new HungLantern { B = LB, pivot = hook.clone(), rest = center.clone(), live = center } );
			return center;
		}
	}
}
