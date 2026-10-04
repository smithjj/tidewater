using System;
using System.Collections.Generic;
using Tidewater.Engine;

// Port of src/world/fish/FishGeometry.js: procedural fish meshes built from the anatomy tables (FishSpecies.cs).
//
// Local frame (total length 1): snout at z = +0.5, tip of the tail fin at z = -0.5, back at +y, the body axis on y = 0. Parts:
//  - body: rings of superellipse cross-sections (depth above / below the axis and half width change from the snout to the caudal
//    peduncle). The mouth line splits the head into an upper jaw and a lower jaw (jaw weight 1, rotated about the hinge at the corner
//    of the mouth by the vertex stage) with a mouth cavity behind the lips.
//  - fins: membranes stretched between rays. Every ray is a column of the fin mesh and the membrane dips between the ray tips (deeply
//    for spines). Caudal fins take their outline from the species (forked, lunate, rounded, truncate); pectorals fan out from a short
//    base behind the gill cover; tunas get finlets.
//  - eyes: domes over the painted eye (nearest level of detail).
// Poses: "swim" (fins spread) and "dead" (fins relaxed and partly lowered: landed and market fish).
//
// aData per vertex (attribute "aData", 4 floats):
//   x: position along the fish (0 snout .. 1 tail tip): swimming wave, bending
//   y: part id + 0.9 * jaw weight
//   z, w: body: arc length around the girth from the dorsal midline, height fraction (-1 belly .. 1 back); fins: position along the ray
//         (0 base .. 1 edge), ray coordinate (integer on the rays); eye: disc coordinates (-1 .. 1); mouth: depth (0 lips .. 1 throat);
//         flesh: coordinates on the cut face
namespace Tidewater.World.Fish
{
	public static class Part
	{
		public const double BODY = 0, DORSAL1 = 1, DORSAL2 = 2, ANAL = 3, CAUDAL = 4, PECTORAL = 5, PELVIC = 6, FINLET = 7, EYE = 8,
			MOUTH = 9, FLESH = 10, ICE = 11, LEAF = 12, SHELL = 13, FILLET = 14,
			DISC = 15, WHIP = 16, CARAPACE = 17, SKIN = 18, FLIPPER = 19; // rays and the turtle (CreatureGeometry.js)
	}

	// section of a species at u
	public sealed class Sec { public double T, B, W, e; }

	// the options object of fishGeometry
	public sealed class FishGeoOpts
	{
		public int lod = 0;
		public string pose = "swim";
		public bool? mouth;      // split jaws + cavity (default: lod < 2 and a dead pose)
		public bool? eyes;       // default: lod 0
		public bool fins = true;
		public double? u0, u1;   // a cut piece: flesh on the cut faces
	}

	public static class FishGeometry
	{
		const double TAU = Math.PI * 2;

		// smooth piecewise profile: pts = [ [ u, value ], ... ] with increasing u
		public static double prof( double[][] pts, double u )
		{
			if ( u <= pts[ 0 ][ 0 ] ) return pts[ 0 ][ 1 ];
			for ( int i = 1; i < pts.Length; i ++ )
			{
				if ( u <= pts[ i ][ 0 ] )
				{
					var a = pts[ i - 1 ]; var b = pts[ i ];
					double t = ( u - a[ 0 ] ) / ( b[ 0 ] - a[ 0 ] );
					double s = t * t * ( 3 - 2 * t );
					return a[ 1 ] + ( b[ 1 ] - a[ 1 ] ) * ( 0.5 * t + 0.5 * s );
				}
			}

			return pts[ pts.Length - 1 ][ 1 ];
		}

		static double lerp( double a, double b, double t ) => a + ( b - a ) * t;
		static double clamp( double x, double a, double b ) => Math.Max( a, Math.Min( b, x ) );
		static double smooth( double a, double b, double x )
		{
			double t = clamp( ( x - a ) / ( b - a ), 0, 1 );
			return t * t * ( 3 - 2 * t );
		}

		// ---------------------------------------------------------------------------
		// mesh accumulator

		public sealed class MeshData
		{
			public readonly List<double> pos = new List<double>();
			public readonly List<double> dat = new List<double>();
			public readonly List<int> idx = new List<int>();
			public readonly List<int[]> seams = new List<int[]>(); // pairs of coincident vertices whose normals are averaged

			public int v( double x, double y, double z, double d0, double d1, double d2, double d3 )
			{
				pos.Add( x ); pos.Add( y ); pos.Add( z );
				dat.Add( d0 ); dat.Add( d1 ); dat.Add( d2 ); dat.Add( d3 );
				return pos.Count / 3 - 1;
			}

			public void tri( int a, int b, int c ) { idx.Add( a ); idx.Add( b ); idx.Add( c ); }

			// a b / d c, counter-clockwise seen from the front
			public void quad( int a, int b, int c, int d ) { idx.Add( a ); idx.Add( b ); idx.Add( d ); idx.Add( b ); idx.Add( c ); idx.Add( d ); }

			public BufferGeometry build()
			{
				var g = new BufferGeometry();
				g.setAttribute( "position", new BufferAttribute( pos, 3 ) );
				g.setAttribute( "aData", new BufferAttribute( dat, 4 ) );
				g.setIndex( idx );
				g.computeVertexNormals();
				var n = g.getAttribute( "normal" ).array;
				foreach ( var ab in seams )
				{
					int a = ab[ 0 ], b = ab[ 1 ];
					double x = n[ a * 3 ] + n[ b * 3 ], y = n[ a * 3 + 1 ] + n[ b * 3 + 1 ], z = n[ a * 3 + 2 ] + n[ b * 3 + 2 ];
					double l = JS.Hypot( x, y, z ); if ( l == 0 ) l = 1;
					x /= l; y /= l; z /= l;
					n[ a * 3 ] = n[ b * 3 ] = ( float ) x;
					n[ a * 3 + 1 ] = n[ b * 3 + 1 ] = ( float ) y;
					n[ a * 3 + 2 ] = n[ b * 3 + 2 ] = ( float ) z;
				}

				g.computeBoundingSphere();
				return g;
			}
		}

		// ---------------------------------------------------------------------------
		// body

		// cross-section of species S at u (0 snout .. 1 caudal peduncle)
		public static Sec section( Species S, double u ) => new Sec { T = prof( S.top, u ), B = prof( S.bot, u ), W = prof( S.wid, u ), e = 2 / S.sec };

		// point on the section outline at angle phi (0 top, PI / 2 right flank (+x), PI belly)
		static double[] outline( Sec c, double phi, double[] @out )
		{
			double s = Math.Sin( phi ), k = Math.Cos( phi );
			@out[ 0 ] = c.W * JS.Sign( s ) * Math.Pow( Math.Abs( s ), c.e );
			@out[ 1 ] = ( k >= 0 ? c.T : c.B ) * JS.Sign( k ) * Math.Pow( Math.Abs( k ), c.e );
			return @out;
		}

		// arc length along the outline from the dorsal midline to angle phi (0 .. PI)
		static double arcTo( Sec c, double phi )
		{
			int n = 24; var p = new double[ 2 ]; var q = new double[ 2 ];
			outline( c, 0, p );
			double s = 0;
			for ( int i = 1; i <= n; i ++ )
			{
				outline( c, phi * i / n, q );
				s += JS.Hypot( q[ 0 ] - p[ 0 ], q[ 1 ] - p[ 1 ] );
				p[ 0 ] = q[ 0 ];
				p[ 1 ] = q[ 1 ];
			}

			return s;
		}

		// half width of the body surface at height y on the section (0 outside)
		public static double surfaceX( Sec c, double y )
		{
			double H = y >= 0 ? c.T : c.B;
			double r = Math.Min( 1, Math.Abs( y ) / Math.Max( H, 1e-4 ) );
			// |x / W| ^ (2 / e) + |y / H| ^ (2 / e) = 1
			double n = 2 / c.e;
			return c.W * Math.Pow( Math.Max( 0, 1 - Math.Pow( r, n ) ), 1 / n );
		}

		// angle on the outline where it crosses height y on the right flank
		static double angleAt( Sec c, double y )
		{
			double e = c.e;
			double k = y >= 0 ? Math.Pow( clamp( y / Math.Max( c.T, 1e-4 ), 0, 0.97 ), 1 / e ) : - Math.Pow( clamp( - y / Math.Max( c.B, 1e-4 ), 0, 0.97 ), 1 / e );
			return Math.Acos( k );
		}

		static readonly int[] RING_N = { 34, 16, 7, 4 };
		static readonly double[] RING_U0 = { 0.006, 0.014, 0.03, 0.05 };
		static readonly int[] BODY_NQ = { 26, 14, 7, 5 };

		static List<double> ringUs( Species S, int lod, double u0, double u1 )
		{
			int n = RING_N[ lod ];
			var us = new List<double>();
			for ( int i = 0; i <= n; i ++ )
			{
				double t = ( double ) i / n;
				us.Add( 0.6 * Math.Pow( t, 1.55 ) + 0.4 * t ); // denser at the head
			}

			us[ 0 ] = RING_U0[ lod ];
			// rings exactly at the corner of the mouth (jaw hinge) and around the eye
			void snap( double u )
			{
				int best = 1; double bd = double.PositiveInfinity;
				for ( int i = 1; i < us.Count - 1; i ++ )
				{
					double d = Math.Abs( us[ i ] - u );
					if ( d < bd ) { bd = d; best = i; }
				}

				us[ best ] = u;
			}

			if ( lod < 2 ) snap( S.mouth.corner );
			var @out = us.FindAll( u => u >= u0 - 1e-6 && u <= u1 + 1e-6 );
			if ( @out[ 0 ] > u0 + 1e-4 && u0 > 0 ) @out.Insert( 0, u0 );
			if ( @out[ @out.Count - 1 ] < u1 - 1e-4 ) @out.Add( u1 );
			return @out;
		}

		sealed class Ring
		{
			public double u, z, phiM, wj, jawFwd;
			public List<int> upper = new List<int>(), lower = new List<int>();
			public Sec c;
		}

		// o: { lod, mouth (split jaws + cavity), u0, u1 (cut pieces: flesh caps at the cut ends) }
		static List<Ring> buildBody( MeshData M, Species S, FishGeoOpts o, bool mouthOpt )
		{
			int lod = o.lod;
			double L = S.body;
			double zOf( double u ) => 0.5 - u * L;
			double u0 = o.u0 ?? 0, u1 = o.u1 ?? 1;
			int NQ = BODY_NQ[ lod ];
			var us = ringUs( S, lod, u0, u1 );
			bool mouth = mouthOpt && u0 == 0;
			var Mo = S.mouth;
			double corner = Mo.corner;
			double mouthY( double u ) => lerp( Mo.tip, Mo.y, Math.Min( 1, u / corner ) );
			// angle of the upper / lower split: the mouth line up to the corner, then kept
			var cCorner = section( S, corner );
			double phiCorner = angleAt( cCorner, Mo.y );
			int nu = ( int ) clamp( JS.Round( NQ * phiCorner / Math.PI ), 3, NQ - 3 ), nl = NQ - nu;
			double jawW( double u ) => mouth ? 1 - smooth( corner * 0.8, corner * 1.3, u ) : 0;
			var rings = new List<Ring>();
			var p = new double[ 2 ];

			foreach ( double u in us )
			{
				var c = section( S, u );
				double phiM = u < corner ? angleAt( c, mouthY( u ) ) : angleAt( c, Mo.y * ( c.T + c.B ) / ( cCorner.T + cCorner.B ) );
				double z = zOf( u );
				double jawFwd = 0;
				var ring = new Ring { u = u };
				double wj = jawW( u );
				double arcHalf = arcTo( c, Math.PI );
				for ( int j = 0; j <= nu; j ++ )
				{
					double phi = - phiM + 2 * phiM * j / nu;
					outline( c, phi, p );
					double s = arcTo( c, Math.Abs( phi ) );
					double h = p[ 1 ] >= 0 ? p[ 1 ] / Math.Max( c.T, 1e-4 ) : p[ 1 ] / Math.Max( c.B, 1e-4 );
					ring.upper.Add( M.v( p[ 0 ], p[ 1 ], z, u * L, Part.BODY, s, h ) );
				}

				for ( int j = 0; j <= nl; j ++ )
				{
					double phi = phiM + ( TAU - 2 * phiM ) * j / nl;
					outline( c, phi, p );
					double a = phi > Math.PI ? TAU - phi : phi;
					double s = Math.Min( arcHalf, arcTo( c, a ) );
					double h = p[ 1 ] >= 0 ? p[ 1 ] / Math.Max( c.T, 1e-4 ) : p[ 1 ] / Math.Max( c.B, 1e-4 );
					// lips move with the jaw only in front of the corner (behind it the seam stays closed)
					double w = ( j == 0 || j == nl ) && u >= corner ? 0 : wj;
					ring.lower.Add( M.v( p[ 0 ], p[ 1 ], z, u * L, Part.BODY + 0.9 * w, s, h ) );
				}

				M.seams.Add( new[] { ring.upper[ nu ], ring.lower[ 0 ] } ); M.seams.Add( new[] { ring.upper[ 0 ], ring.lower[ nl ] } );
				ring.c = c;
				ring.z = z;
				ring.phiM = phiM;
				ring.wj = wj;
				ring.jawFwd = jawFwd;
				rings.Add( ring );
			}

			// skin between the rings
			for ( int i = 0; i < rings.Count - 1; i ++ )
			{
				var A = rings[ i ]; var B = rings[ i + 1 ];
				for ( int j = 0; j < nu; j ++ ) M.quad( A.upper[ j ], A.upper[ j + 1 ], B.upper[ j + 1 ], B.upper[ j ] );
				for ( int j = 0; j < nl; j ++ ) M.quad( A.lower[ j ], A.lower[ j + 1 ], B.lower[ j + 1 ], B.lower[ j ] );
			}

			var first = rings[ 0 ]; var last = rings[ rings.Count - 1 ];

			// ---- snout
			if ( u0 == 0 )
			{
				double zt = 0.5;
				double yt = mouthY( 0 );
				double gap = Math.Min( first.c.T, first.c.B ) * 0.3;
				int upTip = M.v( 0, yt + gap * 0.5, zt, 0, Part.BODY, 0, 0 );
				int loTip = M.v( 0, yt - gap * 0.5, zt + ( mouth ? Mo.protrude : 0 ), 0, Part.BODY + 0.9 * ( mouth ? 1 : 0 ), 0, 0 );
				for ( int j = 0; j < nu; j ++ ) M.tri( upTip, first.upper[ j + 1 ], first.upper[ j ] );
				for ( int j = 0; j < nl; j ++ ) M.tri( loTip, first.lower[ j + 1 ], first.lower[ j ] );
				if ( ! mouth )
				{
					// closed lips at the tip
					M.tri( upTip, first.upper[ 0 ], loTip );
					M.tri( upTip, loTip, first.upper[ nu ] );
				}

				if ( mouth )
				{
					// cavity: roof under the upper jaw and floor over the lower jaw, meeting in the throat at the corner of the mouth
					var roof = new List<int[]>(); var floor = new List<int[]>();
					var ringsM = rings.FindAll( r => r.u <= corner + 1e-6 );
					double dep( double u ) => Math.Min( 1, u / corner );
					foreach ( var r in ringsM )
					{
						double k = 0.45 * ( 1 - dep( r.u ) );
						double ym = mouthY( r.u );
						int R = r.upper[ nu ], Lf = r.upper[ 0 ];
						double rx = M.pos[ R * 3 ], lx = M.pos[ Lf * 3 ];
						double yr = M.pos[ R * 3 + 1 ];
						double top = r.c.T, bot = r.c.B;
						double dd = dep( r.u );
						roof.Add( new[] {
							M.v( rx * 0.97, yr, r.z, r.u * L, Part.MOUTH, dd, 0 ),
							M.v( 0, ym + k * ( top - ym ) * 0.8, r.z - 0.004, r.u * L, Part.MOUTH, dd, 0 ),
							M.v( lx * 0.97, yr, r.z, r.u * L, Part.MOUTH, dd, 0 ),
						} );
						double w = r.wj;
						double zf = r.z + r.jawFwd;
						floor.Add( new[] {
							M.v( rx * 0.97, yr, zf, r.u * L, Part.MOUTH + 0.9 * w, dd, 0 ),
							M.v( 0, ym - k * ( ym + bot ) * 0.8, zf - 0.004, r.u * L, Part.MOUTH + 0.9 * w, dd, 0 ),
							M.v( lx * 0.97, yr, zf, r.u * L, Part.MOUTH + 0.9 * w, dd, 0 ),
						} );
					}

					for ( int i = 0; i < roof.Count - 1; i ++ )
					{
						var a = roof[ i ]; var b = roof[ i + 1 ];
						// roof faces down (into the mouth)
						M.quad( a[ 0 ], a[ 1 ], b[ 1 ], b[ 0 ] );
						M.quad( a[ 1 ], a[ 2 ], b[ 2 ], b[ 1 ] );
						var f = floor[ i ]; var g = floor[ i + 1 ];
						M.quad( f[ 1 ], f[ 0 ], g[ 0 ], g[ 1 ] );
						M.quad( f[ 2 ], f[ 1 ], g[ 1 ], g[ 2 ] );
					}

					// front of the cavity closed to the lip tips
					var r0 = roof[ 0 ]; var f0 = floor[ 0 ];
					M.tri( upTip, r0[ 1 ], r0[ 0 ] );
					M.tri( upTip, r0[ 2 ], r0[ 1 ] );
					M.tri( loTip, f0[ 0 ], f0[ 1 ] );
					M.tri( loTip, f0[ 1 ], f0[ 2 ] );
				}
			}
			else
			{
				fleshCap( M, first, nu, nl, L, true );
			}

			// ---- caudal peduncle end (under the tail fin) or a cut face
			if ( u1 >= 1 )
			{
				int end = M.v( 0, 0, last.z - 0.004, L, Part.BODY, 0, 0 );
				for ( int j = 0; j < nu; j ++ ) M.tri( end, last.upper[ j ], last.upper[ j + 1 ] );
				for ( int j = 0; j < nl; j ++ ) M.tri( end, last.lower[ j ], last.lower[ j + 1 ] );
			}
			else
			{
				fleshCap( M, last, nu, nl, L, false );
			}

			return rings;
		}

		// cut face: the section filled with flesh (coordinates on the face in z / w)
		static void fleshCap( MeshData M, Ring ring, int nu, int nl, double L, bool front )
		{
			var ids = new List<int>( ring.upper );
			for ( int i = 1; i < nl; i ++ ) ids.Add( ring.lower[ i ] );
			var verts = new List<int>();
			foreach ( int i in ids )
			{
				double x = M.pos[ i * 3 ], y = M.pos[ i * 3 + 1 ];
				verts.Add( M.v( x, y, ring.z, ring.u * L, Part.FLESH, x, y ) );
			}

			int c = M.v( 0, ( ring.c.T - ring.c.B ) * 0.3, ring.z, ring.u * L, Part.FLESH, 0, ( ring.c.T - ring.c.B ) * 0.3 );
			for ( int j = 0; j < verts.Count; j ++ )
			{
				int a = verts[ j ], b = verts[ ( j + 1 ) % verts.Count ];
				if ( front ) M.tri( c, b, a );
				else M.tri( c, a, b );
			}
		}

		// ---------------------------------------------------------------------------
		// fins

		// a ray of a fin: base, tip and their positions along the fish
		sealed class Ray { public double[] b, t; public double ub, ut, dip, id; }

		static double[] mid3( double[] p, double[] q ) => new[] { ( p[ 0 ] + q[ 0 ] ) / 2, ( p[ 1 ] + q[ 1 ] ) / 2, ( p[ 2 ] + q[ 2 ] ) / 2 };

		// A membrane between rays. rays: base, tip and their positions along the fish; notch: membrane dip between the tips (fraction of
		// the ray length); rows: vertex rows from the base to the edge; rayIds: ray coordinate of each ray.
		static void membrane( MeshData M, double part, List<Ray> rays, double notch, int rows, List<double> rayIds, bool flip )
		{
			var cols = new List<Ray>();
			for ( int k = 0; k < rays.Count; k ++ )
			{
				cols.Add( new Ray { b = rays[ k ].b, t = rays[ k ].t, ub = rays[ k ].ub, ut = rays[ k ].ut, dip = 0, id = rayIds[ k ] } );
				if ( k < rays.Count - 1 )
				{
					var a = rays[ k ]; var b = rays[ k + 1 ];
					cols.Add( new Ray { b = mid3( a.b, b.b ), t = mid3( a.t, b.t ), ub = ( a.ub + b.ub ) / 2, ut = ( a.ut + b.ut ) / 2, dip = notch, id = ( rayIds[ k ] + rayIds[ k + 1 ] ) / 2 } );
				}
			}

			// front and back faces (separate vertices: opposite normals)
			foreach ( bool back in new[] { false, true } )
			{
				var grid = new List<int[]>();
				foreach ( var c in cols )
				{
					var col = new int[ rows + 1 ];
					double top = 1 - c.dip;
					for ( int r = 0; r <= rows; r ++ )
					{
						double t = ( ( double ) r / rows ) * top;
						col[ r ] = M.v(
							lerp( c.b[ 0 ], c.t[ 0 ], t ), lerp( c.b[ 1 ], c.t[ 1 ], t ), lerp( c.b[ 2 ], c.t[ 2 ], t ),
							lerp( c.ub, c.ut, t ), part, t, c.id );
					}

					grid.Add( col );
				}

				for ( int k = 0; k < grid.Count - 1; k ++ )
				{
					for ( int r = 0; r < rows; r ++ )
					{
						int a = grid[ k ][ r ], b = grid[ k + 1 ][ r ], c = grid[ k + 1 ][ r + 1 ], d = grid[ k ][ r + 1 ];
						if ( back != flip ) M.quad( b, a, d, c );
						else M.quad( a, b, c, d );
					}
				}
			}
		}

		// subset of ray indices for a level of detail (always the first and the last)
		static List<int> pickRays( int n, int max )
		{
			var @out = new List<int>();
			if ( n <= max ) { for ( int i = 0; i < n; i ++ ) @out.Add( i ); return @out; }
			for ( int i = 0; i < max; i ++ ) @out.Add( ( int ) JS.Round( ( double ) ( i * ( n - 1 ) ) / ( max - 1 ) ) );
			return @out;
		}

		static readonly int[] MID_ROWS = { 3, 1, 1, 1 };
		static readonly int[] MID_RAYS = { 40, 7, 3, 2 };

		// dorsal (sign 1) and anal (sign -1) fins along the midline
		static void midlineFins( MeshData M, Species S, List<Seg> segs, int sign, FishGeoOpts o )
		{
			double L = S.body; double zOf( double u ) => 0.5 - u * L;
			bool dead = o.pose == "dead";
			int rows = MID_ROWS[ o.lod ];
			for ( int si = 0; si < segs.Count; si ++ )
			{
				var seg = segs[ si ];
				double part = sign > 0 ? ( seg.spiny ? Part.DORSAL1 : Part.DORSAL2 ) : Part.ANAL;
				var ids = pickRays( seg.rays, MID_RAYS[ o.lod ] );
				double fold = dead ? ( seg.spiny ? 0.42 : 0.22 ) : 0;
				var rays = new List<Ray>();
				foreach ( int k in ids )
				{
					double t = seg.rays > 1 ? ( double ) k / ( seg.rays - 1 ) : 0;
					double u = lerp( seg.from, seg.to, t );
					var c = section( S, u );
					double y0 = sign > 0 ? c.T * 0.9 : - c.B * 0.9;
					double h = prof( seg.h, t ) + ( sign > 0 ? c.T : c.B ) * 0.1;
					double rake = lerp( seg.rake[ 0 ], seg.rake[ 1 ], t );
					rake = lerp( rake, 1.45, fold );
					h *= dead ? ( seg.rays > 30 ? 0.85 : 0.95 ) : 1;
					var tip = new[] { 0, y0 + sign * h * Math.Cos( rake ), zOf( u ) - h * Math.Sin( rake ) };
					rays.Add( new Ray { b = new[] { 0, y0, zOf( u ) }, t = tip, ub = u * L, ut = ( u * L ) + h * Math.Sin( rake ) } );
				}

				// the last ray of a segment followed by another one: membrane continues to its base
				var rayIds = new List<double>(); foreach ( int k in ids ) rayIds.Add( k + si * 40 );
				membrane( M, part, rays, o.lod >= 2 ? 0 : seg.notch, rows, rayIds, sign < 0 );
			}
		}

		static readonly int[] CAUDAL_RAYS = { 40, 9, 5, 3 };

		static void caudalFin( MeshData M, Species S, FishGeoOpts o )
		{
			var C = S.caudal; double L = S.body;
			double zb = 0.5 - L + 0.014;
			var cEnd = section( S, 1 );
			double hp = Math.Min( cEnd.T, cEnd.B ) * 0.9;
			bool dead = o.pose == "dead";
			int R = C.rays;
			var ids = pickRays( R, CAUDAL_RAYS[ o.lod ] );
			double span = C.span * ( dead ? 0.93 : 1 );
			var rays = new List<Ray>(); var rayIds = new List<double>();
			foreach ( int k in ids )
			{
				double s = - 1 + 2.0 * k / ( R - 1 ); // -1 lower lobe .. 1 upper lobe
				double a = Math.Abs( s );
				double y, len;
				if ( C.shape == "rounded" )
				{
					y = span * s * 0.95;
					len = C.len * ( 1 - 0.3 * s * s );
				}
				else if ( C.shape == "truncate" )
				{
					y = span * s;
					len = C.len * ( 1 - 0.05 * s * s ) * lerp( C.fork, 1, a );
				}
				else if ( C.shape == "lunate" )
				{
					y = span * s * ( 0.75 + 0.25 * a );
					len = C.len * ( C.fork + ( 1 - C.fork ) * Math.Pow( a, 1.8 ) );
				}
				else
				{
					y = span * s;
					len = C.len * ( C.fork + ( 1 - C.fork ) * Math.Pow( a, 1.3 ) );
				}

				rays.Add( new Ray { b = new[] { 0, hp * s, zb }, t = new[] { 0, y, zb - 0.014 - len }, ub = L - 0.014, ut = L + len } );
				rayIds.Add( k );
			}

			membrane( M, Part.CAUDAL, rays, o.lod >= 2 ? 0 : 0.05, MID_ROWS[ o.lod ], rayIds, false );
		}

		static readonly int[] PAIRED_RAYS = { 24, 5, 3, 2 };
		static readonly int[] PAIRED_ROWS = { 2, 1, 1, 1 };

		// paired fins: rays fanning out from a short base on the flank
		static void pairedFins( MeshData M, Species S, Paired F, double part, FishGeoOpts o )
		{
			double L = S.body; double zOf( double u ) => 0.5 - u * L;
			bool dead = o.pose == "dead";
			var c = section( S, F.u );
			int R = F.rays;
			var ids = pickRays( R, PAIRED_RAYS[ o.lod ] );
			int rows = PAIRED_ROWS[ o.lod ];
			bool pelvic = part == Part.PELVIC;
			foreach ( int side in new[] { 1, - 1 } )
			{
				var rays = new List<Ray>(); var rayIds = new List<double>();
				foreach ( int k in ids )
				{
					double t = R > 1 ? ( double ) k / ( R - 1 ) : 0; // 0 leading (upper) ray .. 1 last
					double yb, xb, alpha, len, spread;
					if ( pelvic )
					{
						yb = - c.B * 0.88;
						xb = side * ( c.W * 0.22 + t * c.W * 0.12 );
						alpha = lerp( - 0.35, - 0.75, t );
						len = F.len * ( 1 - 0.45 * t );
						spread = dead ? 0.14 : 0.35;
					}
					else
					{
						yb = F.y + F.bas * ( 0.5 - t );
						xb = side * surfaceX( c, yb ) * 0.94;
						string shape = F.shape;
						alpha = shape == "falcate" ? lerp( 0.05, - 0.55, t ) : shape == "pointed" ? lerp( 0.1, - 0.85, t ) : lerp( 0.25, - 1.1, t );
						len = F.len * ( shape == "falcate" ? 1 - 0.85 * Math.Pow( t, 0.55 ) : shape == "pointed" ? 1 - 0.62 * Math.Pow( t, 0.9 ) : 0.62 + 0.38 * Math.Sin( Math.PI * ( 0.15 + 0.85 * t ) ) );
						spread = dead ? 0.16 : F.spread;
						if ( dead ) alpha = alpha * 0.6 - 0.08; // relaxed: rays closer together, pointing back
					}

					var dir = new[] { side * Math.Sin( spread ) * Math.Cos( alpha ), Math.Sin( alpha ), - Math.Cos( spread ) * Math.Cos( alpha ) };
					double zb = zOf( F.u );
					rays.Add( new Ray { b = new[] { xb, yb, zb }, t = new[] { xb + dir[ 0 ] * len, yb + dir[ 1 ] * len, zb + dir[ 2 ] * len }, ub = F.u * L, ut = F.u * L - dir[ 2 ] * len } );
					rayIds.Add( k );
				}

				int first = M.pos.Count / 3;
				membrane( M, part, rays, o.lod >= 2 ? 0 : 0.035, rows, rayIds, side < 0 );
				// keep the membrane outside the flank it lies against
				for ( int i = first; i < M.pos.Count / 3; i ++ )
				{
					double x = M.pos[ i * 3 ], y = M.pos[ i * 3 + 1 ], z = M.pos[ i * 3 + 2 ];
					double u = clamp( ( 0.5 - z ) / L, 0, 1 );
					double sx = surfaceX( section( S, u ), y ) + 0.004;
					if ( Math.Abs( x ) < sx ) M.pos[ i * 3 ] = side * sx;
				}
			}
		}

		static void finlets( MeshData M, Species S, FishGeoOpts o )
		{
			var F = S.finlets; double L = S.body; double zOf( double u ) => 0.5 - u * L;
			foreach ( var ns in new[] { ( F.dorsal, 1 ), ( F.ventral, - 1 ) } )
			{
				int n = ns.Item1; int sign = ns.Item2;
				for ( int i = 0; i < n; i ++ )
				{
					double u = lerp( F.from, F.to, ( i + 0.5 ) / n );
					var c = section( S, u );
					double y0 = sign > 0 ? c.T * 0.85 : - c.B * 0.85;
					double w = ( F.to - F.from ) / n * 0.75;
					double h = 0.006 + ( sign > 0 ? c.T : c.B ) * 0.12;
					foreach ( bool back in new[] { false, true } )
					{
						int a = M.v( 0, y0, zOf( u ), u * L, Part.FINLET, 0, 0 );
						int b = M.v( 0, y0, zOf( u + w ), ( u + w ) * L, Part.FINLET, 0, 1 );
						int t = M.v( 0, y0 + sign * h, zOf( u + w * 1.6 ), ( u + w * 1.6 ) * L, Part.FINLET, 1, 0.5 );
						if ( ( sign > 0 ) != back ) M.tri( a, t, b );
						else M.tri( a, b, t );
					}
				}
			}
		}

		// domes over the eyes: disc coordinates in z / w
		static void eyes( MeshData M, Species S, FishGeoOpts o )
		{
			var E = S.eye; double L = S.body;
			double u = E.u, z0 = 0.5 - u * L;
			var c = section( S, u );
			double r = E.r;
			int segs = o.lod == 0 ? 18 : 10, rings = o.lod == 0 ? 5 : 2;
			double bulge = 0.34 * r;
			foreach ( int side in new[] { 1, - 1 } )
			{
				double xs = surfaceX( c, E.y );
				// axis: sideways, a little forward (fish look ahead) and up
				var ax = new Vector3( side, 0.06, 0.22 ).normalize();
				var t1 = new Vector3( 0, 1, 0 ).addScaledVector( ax, - ax.y ).normalize();
				var t2 = new Vector3().crossVectors( ax, t1 );
				var bas = new Vector3( side * ( xs - r * 0.12 ), E.y, z0 );
				var idx = new List<int[]>();
				for ( int i = 0; i <= rings; i ++ )
				{
					double rho = ( double ) i / rings; // 0 apex .. 1 rim
					int n = i == 0 ? 1 : segs;
					var row = new int[ n ];
					for ( int j = 0; j < n; j ++ )
					{
						double a = ( ( double ) j / segs ) * TAU;
						double ca = Math.Cos( a ) * rho, sa = Math.Sin( a ) * rho;
						double h = bulge * ( 1 - rho * rho ) + r * 0.12;
						double px = bas.x + t1.x * sa * r + t2.x * ca * r + ax.x * h;
						double py = bas.y + t1.y * sa * r + t2.y * ca * r + ax.y * h;
						double pz = bas.z + t1.z * sa * r + t2.z * ca * r + ax.z * h;
						row[ j ] = M.v( px, py, pz, u * L, Part.EYE, ca * side, sa );
					}

					idx.Add( row );
				}

				for ( int i = 0; i < rings; i ++ )
				{
					for ( int j = 0; j < segs; j ++ )
					{
						int j1 = ( j + 1 ) % segs;
						if ( i == 0 )
						{
							if ( side > 0 ) M.tri( idx[ 0 ][ 0 ], idx[ 1 ][ j ], idx[ 1 ][ j1 ] );
							else M.tri( idx[ 0 ][ 0 ], idx[ 1 ][ j1 ], idx[ 1 ][ j ] );
						}
						else
						{
							int a = idx[ i ][ j ], b = idx[ i ][ j1 ], cc = idx[ i + 1 ][ j1 ], d = idx[ i + 1 ][ j ];
							if ( side > 0 ) M.quad( a, d, cc, b );
							else M.quad( a, b, cc, d );
						}
					}
				}
			}
		}

		// ---------------------------------------------------------------------------
		// public builders

		// Whole fish. opts: { lod: 0 | 1 | 2 | 3, pose: "swim" | "dead", mouth (split jaws), eyes, fins, u0 / u1 (a cut piece: 0 .. 1 of
		// the body, flesh on the cut faces) }
		public static BufferGeometry fishGeometry( Species S, FishGeoOpts opts = null )
		{
			var o = opts ?? new FishGeoOpts();
			int lod = o.lod;
			bool mouthOn = o.mouth ?? ( lod < 2 && o.pose == "dead" );
			var M = new MeshData();
			double u0 = o.u0 ?? 0, u1 = o.u1 ?? 1;
			buildBody( M, S, o, mouthOn );
			bool has( double u ) => u >= u0 && u <= u1;
			if ( o.fins )
			{
				List<Seg> main( List<Seg> segs ) => lod < 3 ? segs : segs.GetRange( segs.Count - 1, 1 );
				midlineFins( M, S, main( new List<Seg>( Array.FindAll( S.dorsal, d => has( d.from ) ) ) ), 1, o );
				if ( lod < 3 ) midlineFins( M, S, new List<Seg>( Array.FindAll( S.anal, d => has( d.from ) ) ), - 1, o );
				if ( u1 >= 1 ) caudalFin( M, S, o );
				if ( has( S.pectoral.u ) && lod < 3 ) pairedFins( M, S, S.pectoral, Part.PECTORAL, o );
				if ( S.pelvic != null && has( S.pelvic.u ) && lod < 2 ) pairedFins( M, S, S.pelvic, Part.PELVIC, o );
				if ( S.finlets != null && lod < 2 && u1 >= 1 ) finlets( M, S, o );
			}

			if ( ( o.eyes ?? lod == 0 ) && has( S.eye.u ) ) eyes( M, S, o );
			return M.build();
		}

		// Split, salted and sun-dried fish (butterflied from the back, head removed): a thin slab, the flesh side up (+y), skin below, with
		// the dried tail fin. Same length conventions as a fish (the tail tip at z = -0.5).
		public static BufferGeometry splitFishGeometry( Species S, int lod = 0 )
		{
			var M = new MeshData();
			double L = S.body;
			double zOf( double u ) => 0.5 - u * L;
			double uf = S.opercle + 0.02;
			int nu = new[] { 16, 7 }[ lod ], nx = new[] { 8, 4 }[ lod ];
			// half width of the opened fish (one flank from the back to the belly): widest at the shoulders, tapering in a long triangle to the tail
			double w0 = ( section( S, uf ).T + section( S, uf ).B ) * 0.8;
			double halfW( double u )
			{
				double t = ( u - uf ) / ( 1 - uf );
				var c = section( S, u );
				return Math.Max( ( c.T + c.B ) * 0.55, w0 * ( 1 - 0.72 * Math.Pow( t, 1.1 ) ) * ( 1 - 0.15 * ( 1 - t ) * ( 1 - t ) ) );
			}

			double thick( double u, double t )
			{
				var c = section( S, u );
				return Math.Max( 0.004, c.W * 0.5 * ( 1 - 0.75 * t * t ) );
			}

			// cupped: the edges curl up a little as the flesh dries
			double cup( double u, double t ) => 0.012 * t * t * ( 1 - 0.5 * u );
			var top = new List<int[]>(); var bot = new List<int[]>();
			for ( int i = 0; i <= nu; i ++ )
			{
				double u = lerp( uf, 1, Math.Pow( ( double ) i / nu, 0.85 ) );
				double w = halfW( u );
				// front edge: the cut behind the collarbone, curved back at the sides
				double zc = zOf( u );
				var rowT = new int[ 2 * nx + 1 ]; var rowB = new int[ 2 * nx + 1 ];
				for ( int j = - nx; j <= nx; j ++ )
				{
					double t = Math.Abs( ( double ) j / nx );
					double x = ( ( double ) j / nx ) * w;
					double zz = zc - ( i == 0 ? 0.04 * t * t : 0 );
					double y = cup( u, t );
					double th = thick( u, t );
					rowT[ j + nx ] = M.v( x, y + th * 0.5, zz, u * L, Part.FILLET, ( double ) j / nx, zz );
					rowB[ j + nx ] = M.v( x, y - th * 0.5, zz, u * L, Part.BODY, Math.Abs( x ), 1 - 2 * t );
				}

				top.Add( rowT );
				bot.Add( rowB );
			}

			int n = 2 * nx;
			for ( int i = 0; i < nu; i ++ )
			{
				for ( int j = 0; j < n; j ++ )
				{
					M.quad( top[ i ][ j ], top[ i ][ j + 1 ], top[ i + 1 ][ j + 1 ], top[ i + 1 ][ j ] );
					M.quad( bot[ i ][ j + 1 ], bot[ i ][ j ], bot[ i + 1 ][ j ], bot[ i + 1 ][ j + 1 ] );
				}
			}

			// rim: the thin cut edges around the slab
			void rim( int[] a, int[] b, bool flip )
			{
				for ( int k = 0; k < a.Length - 1; k ++ )
				{
					if ( flip ) M.quad( a[ k + 1 ], a[ k ], b[ k ], b[ k + 1 ] );
					else M.quad( a[ k ], a[ k + 1 ], b[ k + 1 ], b[ k ] );
				}
			}

			int[] col( List<int[]> rows, int j ) { var r = new int[ rows.Count ]; for ( int i = 0; i < r.Length; i ++ ) r[ i ] = rows[ i ][ j ]; return r; }
			rim( top[ 0 ], bot[ 0 ], false );
			rim( col( top, 0 ), col( bot, 0 ), false );
			rim( col( top, n ), col( bot, n ), true );

			// dried tail fin, flattened into the plane of the slab
			var C = S.caudal;
			int R = new[] { 13, 5 }[ lod ];
			double zb = zOf( 1 ) + 0.01;
			double w1 = halfW( 1 ) * 0.9;
			var rays = new List<Ray>(); var ids = new List<double>();
			for ( int k = 0; k < R; k ++ )
			{
				double s = - 1 + 2.0 * k / ( R - 1 );
				double a = Math.Abs( s );
				double len = C.len * ( C.shape == "rounded" || C.shape == "truncate" ? 1 - 0.2 * s * s : lerp( C.fork, 1, Math.Pow( a, 1.3 ) ) ) * 0.9;
				rays.Add( new Ray { b = new[] { w1 * s, cup( 1, a ), zb }, t = new[] { C.span * 0.8 * s, cup( 1, a ) + 0.004, zb - len }, ub = L, ut = L + len } );
				ids.Add( k );
			}

			membrane( M, Part.CAUDAL, rays, 0.08, 1, ids, true );
			return M.build();
		}
	}
}
