using System;
using System.Collections.Generic;
using Tidewater.Engine;
using Tidewater.Util;
using static Tidewater.World.Fish.FishGeometry;

// Port of src/world/fish/FishProps.js (the model half): fish as village props: whole fish lying on ice or hanging from the stall, split
// salted fish on the drying racks, a fish cut in two on a cleaning table, plus the crushed ice, banana leaves and spiny lobsters of the
// displays. Everything placed while the village is built (Props fish(), iceBed(), bananaLeaf(), lobster()) is recorded here (add) and
// drawn by FishPropsView (the Unity side of build() / cull(): one renderer per piece, see there).
//
// Instance record (FishMaterial propVertex): r0 = ( position, length ), r1 = orientation, r2 = ( pattern + seed * 0.9, curl, sag, jaw ),
// r3 = ( cloudy eye, wet, dried, blood ).
namespace Tidewater.World.Fish
{
	// the o argument of FishProps.add
	public sealed class FishAddOpts
	{
		public double? curl, sag, seed, jaw, cloudy, wet, dried, blood;
		public double[] anchor;
	}

	public sealed class FishItem
	{
		public string kind, species;
		public double L;
		public double x, y, z;       // sim position
		public double[] q;           // sim orientation ( x, y, z, w )
		public double pattern, seed, curl, sag, jaw;
		public double[] flags;       // cloudy eye, wet, dried, blood
	}

	public sealed class FishProps
	{
		// ---------------------------------------------------------------------------
		// display geometry (unit size; aData as the fish: x along, y part, z / w pattern coordinates)

		sealed class Raw
		{
			public readonly List<double> pos = new List<double>(), dat = new List<double>();
			public readonly List<int> idx = new List<int>();
			public int count => pos.Count / 3;
			public BufferGeometry build()
			{
				var g = new BufferGeometry();
				g.setAttribute( "position", new BufferAttribute( pos, 3 ) );
				g.setAttribute( "aData", new BufferAttribute( dat, 4 ) );
				g.setIndex( idx );
				g.computeVertexNormals();
				g.computeBoundingSphere();
				return g;
			}
		}

		static Func<double> mulberry( uint seed ) { var m = new Mulberry32( seed ); return m.Next; }

		// Crushed ice heaped in a round basin of radius 1 (the instance length is the radius): a lumpy mound with faceted chunks on top.
		public static BufferGeometry iceGeometry( int lod )
		{
			var R = new Raw(); var pos = R.pos; var dat = R.dat; var idx = R.idx;
			var rnd = mulberry( 17 );
			int v( double x, double y, double z, double a, double b )
			{
				pos.Add( x ); pos.Add( y ); pos.Add( z );
				dat.Add( 0 ); dat.Add( Part.ICE ); dat.Add( a ); dat.Add( b );
				return pos.Count / 3 - 1;
			}

			// mound
			int rings = lod != 0 ? 3 : 6, segs = lod != 0 ? 12 : 24;
			double mound( double r, double a ) => 0.16 * ( 1 - r * r ) + 0.025 * Math.Sin( a * 5 + r * 7 ) * r + 0.02;
			int c = v( 0, mound( 0, 0 ), 0, 0.5, 0.5 );
			var grid = new List<int[]>();
			for ( int i = 1; i <= rings; i ++ )
			{
				double r = ( double ) i / rings;
				var row = new int[ segs ];
				for ( int j = 0; j < segs; j ++ )
				{
					double a = ( double ) j / segs * Math.PI * 2;
					double x = Math.Cos( a ) * r, y = mound( r, a ), z = Math.Sin( a ) * r;
					double p0 = rnd(), p1 = rnd();
					row[ j ] = v( x, y, z, p0, p1 );
				}

				grid.Add( row );
			}

			// skirt down the basin wall (hidden by it: no gap between the ice and the wood)
			var skirt = new int[ segs ];
			for ( int j = 0; j < segs; j ++ )
			{
				double a = ( double ) j / segs * Math.PI * 2;
				double p0 = rnd(), p1 = rnd();
				skirt[ j ] = v( Math.Cos( a ) * 0.8, - 0.35, Math.Sin( a ) * 0.8, p0, p1 );
			}

			grid.Add( skirt );
			for ( int j = 0; j < segs; j ++ ) { idx.Add( c ); idx.Add( grid[ 0 ][ ( j + 1 ) % segs ] ); idx.Add( grid[ 0 ][ j ] ); }
			for ( int i = 0; i < rings; i ++ )
				for ( int j = 0; j < segs; j ++ )
				{
					int a = grid[ i ][ j ], b = grid[ i ][ ( j + 1 ) % segs ], cc = grid[ i + 1 ][ ( j + 1 ) % segs ], d = grid[ i + 1 ][ j ];
					idx.Add( a ); idx.Add( b ); idx.Add( d ); idx.Add( b ); idx.Add( cc ); idx.Add( d );
				}

			// chunks: irregular tetrahedra / wedges half sunk into the mound
			int n = lod != 0 ? 50 : 170;
			double[][] cornerBase = { new[] { 1, 0.2, 0.1 }, new[] { - 0.6, 0.9, 0.2 }, new[] { - 0.5, - 0.4, 0.9 }, new[] { 0.1, - 0.6, - 0.9 }, new[] { 0.3, 0.8, - 0.5 } };
			int[][] faces = { new[] { 0, 1, 2 }, new[] { 0, 2, 3 }, new[] { 0, 3, 4 }, new[] { 0, 4, 1 }, new[] { 1, 4, 3 }, new[] { 1, 3, 2 } };
			for ( int k = 0; k < n; k ++ )
			{
				double r = Math.Sqrt( rnd() ) * 0.92; double a = rnd() * Math.PI * 2;
				double x = Math.Cos( a ) * r, z = Math.Sin( a ) * r, y = mound( r, a );
				double s = 0.06 + rnd() * 0.09;
				double e0 = rnd() * 6, e1 = rnd() * 6, e2 = rnd() * 6;
				var q = new Engine.Quaternion().setFromEuler( new Euler( e0, e1, e2 ) );
				var corners = new Vector3[ 5 ];
				for ( int m = 0; m < 5; m ++ )
				{
					var p = cornerBase[ m ];
					double cx = p[ 0 ] * ( 0.7 + rnd() * 0.6 ), cy = p[ 1 ] * ( 0.7 + rnd() * 0.6 ), cz = p[ 2 ] * ( 0.7 + rnd() * 0.6 );
					corners[ m ] = new Vector3( cx, cy, cz ).multiplyScalar( s ).applyQuaternion( q );
				}

				double ca = rnd(), cb = rnd();
				foreach ( var f in faces )
				{
					// flat faces (own vertices) catch glints; winding fixed to face outward
					var A = corners[ f[ 0 ] ]; var B = corners[ f[ 1 ] ]; var C = corners[ f[ 2 ] ];
					var nrm = new Vector3().subVectors( B, A ).cross( new Vector3().subVectors( C, A ) );
					var ctr = new Vector3().add( A ).add( B ).add( C );
					bool flip = nrm.dot( ctr ) < 0;
					var ids = new int[ 3 ]; var ps = new[] { A, B, C };
					for ( int m = 0; m < 3; m ++ ) ids[ m ] = v( x + ps[ m ].x, y + ps[ m ].y * 0.8, z + ps[ m ].z, ca, cb );
					if ( flip ) { idx.Add( ids[ 0 ] ); idx.Add( ids[ 2 ] ); idx.Add( ids[ 1 ] ); }
					else { idx.Add( ids[ 0 ] ); idx.Add( ids[ 1 ] ); idx.Add( ids[ 2 ] ); }
				}
			}

			return R.build();
		}

		// Banana leaf (length 1 along +z from its stalk end, blade up +y): V-folded blade, curled edges, torn along the veins near the tips.
		public static BufferGeometry leafGeometry( int lod )
		{
			var R = new Raw(); var pos = R.pos; var dat = R.dat; var idx = R.idx;
			int nz = lod != 0 ? 8 : 20, nx = lod != 0 ? 3 : 7;
			var rnd = mulberry( 5 );
			var tears = new List<double[]>();
			for ( int k = 0; k < 6; k ++ ) { double tz = 0.3 + rnd() * 0.6; double sd = rnd() < 0.5 ? - 1 : 1; tears.Add( new[] { tz, sd } ); }
			foreach ( bool back in new[] { false, true } )
			{
				int bas = pos.Count / 3;
				for ( int i = 0; i <= nz; i ++ )
				{
					double t = ( double ) i / nz;
					double halfW = 0.19 * Math.Sin( Math.PI * Math.Min( 1, 0.08 + t * 0.95 ) ) + 0.012;
					for ( int j = - nx; j <= nx; j ++ )
					{
						double a = ( double ) j / nx;
						// torn strips: a split pulls the edge apart a little
						double x = a * halfW;
						foreach ( var ts in tears ) if ( JS.Sign( a ) == ts[ 1 ] && Math.Abs( a ) > 0.45 && t > ts[ 0 ] ) x += ts[ 1 ] * 0.01 * Math.Abs( a );
						double fold = Math.Abs( a ) * halfW * 0.25; // V-fold along the midrib
						double curl = a * a * 0.04 * ( 1 - t );
						pos.Add( x ); pos.Add( fold + curl + 0.01 * Math.Sin( t * 9 ) * a ); pos.Add( t );
						dat.Add( t ); dat.Add( Part.LEAF ); dat.Add( a ); dat.Add( t );
					}
				}

				int row = 2 * nx + 1;
				for ( int i = 0; i < nz; i ++ )
					for ( int j = 0; j < 2 * nx; j ++ )
					{
						int a = bas + i * row + j, b = a + 1, c = a + row + 1, d = a + row;
						if ( back ) { idx.Add( a ); idx.Add( b ); idx.Add( d ); idx.Add( b ); idx.Add( c ); idx.Add( d ); }
						else { idx.Add( a ); idx.Add( d ); idx.Add( b ); idx.Add( b ); idx.Add( d ); idx.Add( c ); }
					}
			}

			return R.build();
		}

		// Caribbean spiny lobster, body length 1 (carapace + tail) along +z (head), legs below, long antennae sweeping back over the sides.
		public static BufferGeometry lobsterGeometry( int lod )
		{
			var R = new Raw(); var pos = R.pos; var dat = R.dat; var idx = R.idx;
			int v( double x, double y, double z, double a, double b )
			{
				pos.Add( x ); pos.Add( y ); pos.Add( z );
				dat.Add( 0.5 - z ); dat.Add( Part.SHELL ); dat.Add( a ); dat.Add( b );
				return pos.Count / 3 - 1;
			}

			void tri6( int a, int b, int c, int d ) { idx.Add( a ); idx.Add( d ); idx.Add( b ); idx.Add( b ); idx.Add( d ); idx.Add( c ); }

			int segs = lod != 0 ? 6 : 12;
			// shell of revolution around the body axis (z), flattened on the underside
			void shell( double[][] pts, double flat )
			{
				var rows = new List<int[]>();
				foreach ( var pt in pts )
				{
					double z = pt[ 0 ], r = pt[ 1 ], h = pt[ 2 ];
					var row = new int[ segs + 1 ];
					for ( int j = 0; j <= segs; j ++ )
					{
						double a = Math.PI * j / segs; // over the back, from one side to the other
						double x = Math.Cos( a ) * r, y = Math.Sin( a ) * h;
						row[ j ] = v( x, y * ( y < 0 ? flat : 1 ), z, x * 3, z * 3 );
					}

					// underside
					rows.Add( row );
				}

				for ( int i = 0; i < rows.Count - 1; i ++ )
					for ( int j = 0; j < segs; j ++ ) tri6( rows[ i ][ j ], rows[ i ][ j + 1 ], rows[ i + 1 ][ j + 1 ], rows[ i + 1 ][ j ] );

				// flat belly
				for ( int i = 0; i < rows.Count - 1; i ++ )
				{
					int a = rows[ i ][ 0 ], b = rows[ i ][ segs ], c = rows[ i + 1 ][ segs ], d = rows[ i + 1 ][ 0 ];
					idx.Add( a ); idx.Add( b ); idx.Add( d ); idx.Add( b ); idx.Add( c ); idx.Add( d );
				}
			}

			// carapace (front 40 %) with the horns over the eyes
			shell( new[] { new[] { 0.5, 0.03, 0.03 }, new[] { 0.47, 0.09, 0.075 }, new[] { 0.4, 0.125, 0.105 }, new[] { 0.28, 0.135, 0.115 }, new[] { 0.15, 0.125, 0.11 }, new[] { 0.1, 0.115, 0.1 } }, 0.25 );
			// horns over the eyes
			foreach ( int s in new[] { 1, - 1 } )
			{
				int b0 = v( s * 0.035, 0.07, 0.47, 0.5, 0.5 ), b1 = v( s * 0.075, 0.06, 0.46, 0.5, 0.5 ), b2 = v( s * 0.05, 0.03, 0.47, 0.5, 0.5 ), tip = v( s * 0.075, 0.1, 0.55, 0.5, 0.5 );
				if ( s > 0 ) idx.AddRange( new[] { b0, b1, tip, b1, b2, tip, b2, b0, tip } );
				else idx.AddRange( new[] { b0, tip, b1, b1, tip, b2, b2, tip, b0 } );
			}

			// tail: six overlapping segments, curled slightly down toward the fan
			var tail = new List<double[]>();
			for ( int k = 0; k < 6; k ++ )
			{
				double z0 = 0.1 - k * 0.075, r = 0.115 - k * 0.009;
				double dy = - k * k * 0.002;
				tail.Add( new[] { z0, r, r * 0.8, dy } );
			}

			foreach ( var tl in tail )
			{
				double z0 = tl[ 0 ], r = tl[ 1 ], h = tl[ 2 ], dy = tl[ 3 ];
				int bas = pos.Count / 3;
				shell( new[] { new[] { z0 + 0.005, r * 0.95, h * 0.95 }, new[] { z0 - 0.02, r, h }, new[] { z0 - 0.08, r * 0.96, h * 0.92 } }, 0.3 );
				for ( int i = bas; i < pos.Count / 3; i ++ ) pos[ i * 3 + 1 ] += dy;
			}

			// tail fan
			for ( int k = - 2; k <= 2; k ++ )
			{
				double a = k * 0.32;
				double z0 = - 0.36, y0 = - 0.05;
				double l = 0.13 - Math.Abs( k ) * 0.012;
				int p0 = v( 0, y0, z0, 0, 0 ), p1 = v( Math.Sin( a - 0.13 ) * l, y0 - 0.01, z0 - Math.Cos( a - 0.13 ) * l, 1, 0 ), p2 = v( Math.Sin( a + 0.13 ) * l, y0 - 0.01, z0 - Math.Cos( a + 0.13 ) * l, 1, 1 );
				idx.AddRange( new[] { p0, p2, p1, p0, p1, p2 } );
			}

			// tapered tubes: antennae, legs, eye stalks
			void tube( double[][] pts, double r0, double r1, double n )
			{
				var rows = new List<int[]>();
				int sides = lod != 0 ? 3 : 5;
				for ( int i = 0; i < pts.Length; i ++ )
				{
					var p = pts[ i ]; var q = pts[ Math.Min( i + 1, pts.Length - 1 ) ]; var o = pts[ Math.Max( i - 1, 0 ) ];
					var d = new Vector3( q[ 0 ] - o[ 0 ], q[ 1 ] - o[ 1 ], q[ 2 ] - o[ 2 ] ).normalize();
					var s1 = new Vector3( 0, 1, 0 ).cross( d ).normalize();
					if ( s1.lengthSq() < 1e-6 ) s1.set( 1, 0, 0 );
					var s2 = new Vector3().crossVectors( d, s1 );
					double r = r0 + ( r1 - r0 ) * i / ( pts.Length - 1 );
					var row = new int[ sides ];
					for ( int j = 0; j < sides; j ++ )
					{
						double a = ( double ) j / sides * Math.PI * 2;
						row[ j ] = v( p[ 0 ] + ( s1.x * Math.Cos( a ) + s2.x * Math.Sin( a ) ) * r, p[ 1 ] + ( s1.y * Math.Cos( a ) + s2.y * Math.Sin( a ) ) * r, p[ 2 ] + ( s1.z * Math.Cos( a ) + s2.z * Math.Sin( a ) ) * r, n, ( double ) i / pts.Length );
					}

					rows.Add( row );
				}

				for ( int i = 0; i < rows.Count - 1; i ++ )
					for ( int j = 0; j < sides; j ++ )
					{
						int a = rows[ i ][ j ], b = rows[ i ][ ( j + 1 ) % sides ], c = rows[ i + 1 ][ ( j + 1 ) % sides ], d = rows[ i + 1 ][ j ];
						idx.Add( a ); idx.Add( b ); idx.Add( d ); idx.Add( b ); idx.Add( c ); idx.Add( d );
					}
			}

			foreach ( int s in new[] { 1, - 1 } )
			{
				// antennae: thick spiny bases, sweeping forward and out, then back along the sides
				var ant = new List<double[]>();
				int na = lod != 0 ? 5 : 10;
				for ( int i = 0; i <= na; i ++ )
				{
					double t = ( double ) i / na;
					ant.Add( new[] { s * ( 0.05 + Math.Sin( t * 2.2 ) * 0.28 ), 0.05 + t * 0.06 - t * t * 0.1, 0.48 + Math.Sin( t * 2.6 ) * 0.25 - t * t * 0.75 } );
				}

				tube( ant.ToArray(), 0.042, 0.005, 2 );
				// walking legs
				for ( int k = 0; k < 5; k ++ )
				{
					double z = 0.36 - k * 0.055;
					tube( new[] { new[] { s * 0.09, - 0.01, z }, new[] { s * 0.19, 0.025, z - 0.01 }, new[] { s * 0.26, - 0.01, z - 0.04 }, new[] { s * 0.29, - 0.07, z - 0.07 } }, 0.013, 0.005, 3 );
				}

				tube( new[] { new[] { s * 0.03, 0.05, 0.47 }, new[] { s * 0.05, 0.08, 0.5 } }, 0.012, 0.01, 4 );
			}

			return R.build();
		}

		// ---------------------------------------------------------------------------

		// JS mirror of the vertex shader's bends (FishMaterial propVertex): where a model point ends up
		public static Vector3 bendPoint( double x, double y, double z, double curl, double sag, Vector3 @out )
		{
			double k1 = curl + ( curl >= 0 ? 1e-4 : - 1e-4 );
			double t1 = k1 * z;
			double h1 = Math.Sin( t1 * 0.5 );
			double x1 = 2 * h1 * h1 / k1 + x * Math.Cos( t1 );
			double z1 = Math.Sin( t1 ) / k1 - x * Math.Sin( t1 );
			double k2 = sag + ( sag >= 0 ? 1e-4 : - 1e-4 );
			double t2 = k2 * z1;
			double h2 = Math.Sin( t2 * 0.5 );
			return @out.set( x1, 2 * h2 * h2 / k2 + y * Math.Cos( t2 ), Math.Sin( t2 ) / k2 - y * Math.Sin( t2 ) );
		}

		// model frame (nose +z, back +y, left flank +x) -> placement frame, per pose
		static readonly Dictionary<string, Matrix4> POSE = new Dictionary<string, Matrix4>
		{
			// lying on its side, nose toward +x, left flank up
			{ "side", new Matrix4().makeBasis( new Vector3( 0, 1, 0 ), new Vector3( 0, 0, 1 ), new Vector3( 1, 0, 0 ) ) },
			// the other flank up
			{ "sideFlip", new Matrix4().makeBasis( new Vector3( 0, - 1, 0 ), new Vector3( 0, 0, - 1 ), new Vector3( 1, 0, 0 ) ) },
			// hanging by the tail: nose down, left flank toward +z, back toward -x
			{ "tail", new Matrix4().makeBasis( new Vector3( 0, 0, 1 ), new Vector3( - 1, 0, 0 ), new Vector3( 0, - 1, 0 ) ) },
			// split fish hung by the tail: nose down, the flesh side (model +y) toward +z
			{ "tailFlat", new Matrix4().makeBasis( new Vector3( 1, 0, 0 ), new Vector3( 0, 0, 1 ), new Vector3( 0, - 1, 0 ) ) },
			// hanging from a hook through the gills: nose up, left flank toward +z, back toward +x
			{ "gill", new Matrix4().makeBasis( new Vector3( 0, 0, 1 ), new Vector3( 1, 0, 0 ), new Vector3( 0, 1, 0 ) ) },
			// upright (belly down), nose toward +x: lobsters, leaves, ice
			{ "flat", new Matrix4().makeBasis( new Vector3( 0, 0, - 1 ), new Vector3( 0, 1, 0 ), new Vector3( 1, 0, 0 ) ) },
		};

		public List<FishItem> items = new List<FishItem>();

		static readonly Matrix4 _m = new Matrix4();
		static readonly Vector3 _p = new Vector3(), _s = new Vector3();
		static readonly Engine.Quaternion _q = new Engine.Quaternion();
		static readonly System.Random _fallback = new System.Random();

		// kind: "whole" | "split" | "head" | "trunk" | "ice" | "leaf" | "lobster". frame: world matrix of the placement (see POSE: the pose
		// rotation is applied here). anchor (model units): the model point placed at the frame origin (after bending).
		public void add( string kind, string species, Matrix4 frame, string pose, double L, FishAddOpts o = null )
		{
			o = o ?? new FishAddOpts();
			Species S = species != null ? FishSpecies.SPECIES[ species ] : null;
			double curl = o.curl ?? 0, sag = o.sag ?? 0;
			_m.multiplyMatrices( frame, POSE[ pose ] );
			_m.decompose( _p, _q, _s );
			// the anchor point of the model sits at the frame origin
			var a = o.anchor ?? new double[] { 0, 0, 0 };
			var b = bendPoint( a[ 0 ], a[ 1 ], a[ 2 ], curl, sag, new Vector3() ).multiplyScalar( L ).applyQuaternion( _q );
			_p.sub( b );
			items.Add( new FishItem
			{
				kind = kind, species = species, L = L,
				x = _p.x, y = _p.y, z = _p.z, q = new[] { _q.x, _q.y, _q.z, _q.w },
				pattern = S != null ? S.pattern : 0, seed = o.seed ?? _fallback.NextDouble(),
				curl = curl, sag = sag, jaw = o.jaw ?? 0,
				flags = new[] { o.cloudy ?? 0.4, o.wet ?? 1, o.dried ?? 0, o.blood ?? 0 },
			} );
		}

		// half thickness of a fish lying on its side (m)
		public static double restHeight( string species, double L )
		{
			var S = FishSpecies.SPECIES[ species ];
			double w = 0;
			for ( double u = 0.1; u < 0.9; u += 0.05 ) w = Math.Max( w, section( S, u ).W );
			return w * L * 0.85;
		}

		// model z of the caudal peduncle / the hook through the gill cover
		public static double[] tailAnchor( string species ) => new[] { 0, 0, 0.5 - FishSpecies.SPECIES[ species ].body * 0.985 };

		public static double[] gillAnchor( string species )
		{
			var S = FishSpecies.SPECIES[ species ];
			return new[] { 0, S.mouth.y * 0.5, 0.5 - S.body * ( S.mouth.corner + 0.03 ) };
		}

		// the model of a kind at a level of detail (FishProps.build: two levels per kind and species)
		public static BufferGeometry geometryOf( string kind, string species, int lod )
		{
			Species S = species != null ? FishSpecies.SPECIES[ species ] : null;
			switch ( kind )
			{
				case "split": return splitFishGeometry( S, lod );
				case "head": return fishGeometry( S, new FishGeoOpts { lod = lod, pose = "dead", u1 = S.opercle + 0.02, eyes = lod == 0 } );
				case "trunk": return fishGeometry( S, new FishGeoOpts { lod = lod, pose = "dead", u0 = S.opercle + 0.02 } );
				case "ice": return iceGeometry( lod );
				case "leaf": return leafGeometry( lod );
				case "lobster": return lobsterGeometry( lod );
				default: return fishGeometry( S, new FishGeoOpts { lod = lod, pose = "dead", eyes = lod == 0 } );
			}
		}
	}
}
