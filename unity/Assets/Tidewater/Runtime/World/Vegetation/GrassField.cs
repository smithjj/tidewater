using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Tidewater.Util;
using UnityEngine;
using UnityEngine.Rendering;
using TerrainData = Tidewater.World.TerrainData;

// The camera-following ground flora (src/world/vegetation/GrassField.js): tall meadow grass, dune grass, sea oats and beach creeper. The world is divided into CELL x CELL metre
// cells; every visible cell near the camera draws one instance of a "patch": a fixed blue-noise set of clump slots. The vertex stage (Shaders/Vegetation/VegGrass.hlsl) puts each
// slot at cellOrigin + slot offset, takes its height from the terrain heightmap and its presence / size from the density mask (R dune grass, G meadow grass, B sea oats, A
// creeper). Three levels share the same clumps and blades, so a cell switching between them never moves a blade. The patch meshes are built here with the JS's random streams
// (blue noise, clump and blade parameters); the vertices are in the SIM frame like the plants' (the vertex stage mirrors z at the end).
namespace Tidewater.World.Vegetation
{
	public sealed class GrassField
	{
		public const int CELL = 8;
		public const float R_NEAR = 18, R_MID = 46, R_FAR = 88;
		const int MAX_NEAR = 48, MAX_MID = 160, MAX_FAR = 420;
		const int CLUMPS = 384, BLADES = 7, OATS = 12, VINES = 28;
		static readonly int[] BLADE_TIER = { 0, 0, 1, 1, 2, 2, 2 };
		const int K_GRASS = 0, K_OAT_STALK = 1, K_OAT_HEAD = 2, K_CREEPER = 3, K_FLOWER = 4;

		// ---------------------------------------------------------------------------------------------------------------- the patch meshes

		// Mitchell's best-candidate blue noise on a torus; any prefix is also well distributed.
		static List<double[]> BlueNoise( Mulberry32 rand, int n, double size, int k = 10 )
		{
			var pts = new List<double[]>();
			for ( int i = 0; i < n; i ++ )
			{
				double[] best = null; double bestD = - 1;
				int tries = i == 0 ? 1 : k;
				for ( int c = 0; c < tries; c ++ )
				{
					double x = rand.Next() * size, z = rand.Next() * size;
					double dmin = double.PositiveInfinity;
					foreach ( var p in pts )
					{
						double dx = Math.Abs( p[ 0 ] - x ), dz = Math.Abs( p[ 1 ] - z );
						dx = Math.Min( dx, size - dx );
						dz = Math.Min( dz, size - dz );
						dmin = Math.Min( dmin, dx * dx + dz * dz );
					}

					if ( dmin > bestD ) { bestD = dmin; best = new[] { x, z }; }
				}

				pts.Add( best );
			}

			return pts;
		}

		[StructLayout( LayoutKind.Sequential )]
		struct Vert { public Vector3 p, n; public Vector4 aBlade, aSlot; public Vector2 side, across; }

		sealed class PatchBuilder
		{
			public readonly List<Vert> verts = new List<Vert>();
			public readonly List<int> idx = new List<int>();

			// side: xyz offset direction * half width, w: across coordinate ( -1 left edge, 1 right, 0 tip )
			public int V( double[] p, double[] n, double[] side, double[] slot, double[] blade )
			{
				verts.Add( new Vert
				{
					p = new Vector3( ( float ) p[ 0 ], ( float ) p[ 1 ], ( float ) p[ 2 ] ),
					n = new Vector3( ( float ) n[ 0 ], ( float ) n[ 1 ], ( float ) n[ 2 ] ),
					aBlade = new Vector4( ( float ) blade[ 0 ], ( float ) blade[ 1 ], ( float ) blade[ 2 ], ( float ) blade[ 3 ] ),
					aSlot = new Vector4( ( float ) slot[ 0 ], ( float ) slot[ 1 ], ( float ) slot[ 2 ], ( float ) slot[ 3 ] ),
					side = new Vector2( ( float ) side[ 0 ], ( float ) side[ 2 ] ),
					across = new Vector2( ( float ) side[ 3 ], 0 ),
				} );
				return verts.Count - 1;
			}

			static readonly double[] ZERO = { 0, 0, 0, 0 };

			// blade: centre-line points with half widths; the tip row has zero width (a single vertex). w4: aBlade.w ( grass: tier )
			public void Blade( double[] slot, int kind, double[] rows, double width, double dirX, double dirZ, double lean, double height, double curve, double rnd, double[] bs, double w4 )
			{
				double sx = - dirZ, sz = dirX; // horizontal side direction
				var ids = new List<int[]>();
				for ( int r = 0; r < rows.Length; r ++ )
				{
					double h = rows[ r ];
					double outD = ( Math.Sin( lean ) * h + curve * h * h ) * height;
					double up = ( Math.Cos( lean ) * h - curve * 0.35 * h * h ) * height;
					var p = new[] { bs[ 0 ] + dirX * outD, up, bs[ 1 ] + dirZ * outD };
					// tangent for the normal
					double dOut = Math.Sin( lean ) + 2 * curve * h, dUp = Math.Cos( lean ) - 0.7 * curve * h;
					double tl = Math.Sqrt( dOut * dOut + dUp * dUp );
					double tx = dirX * dOut / tl, ty = dUp / tl, tz = dirZ * dOut / tl;
					// normal = side x tangent
					double nx = - sz * ty, ny = sz * tx - sx * tz, nz = sx * ty;
					double nl = Math.Sqrt( nx * nx + ny * ny + nz * nz ); if ( nl == 0 ) nl = 1;
					var n = new[] { nx / nl, ny / nl, nz / nl };
					double hw = width * ( 1 - Math.Pow( h, 1.6 ) ) * ( 0.75 + 0.25 * ( 1 - h ) );
					var blade = new[] { h, kind, rnd, w4 };
					if ( r == rows.Length - 1 ) ids.Add( new[] { V( p, n, ZERO, slot, blade ) } );
					else ids.Add( new[]
					{
						V( p, n, new[] { - sx * hw, 0, - sz * hw, - 1 }, slot, blade ),
						V( p, n, new[] { sx * hw, 0, sz * hw, 1 }, slot, blade ),
					} );
				}

				for ( int r = 0; r < ids.Count - 1; r ++ )
				{
					var a = ids[ r ]; var b = ids[ r + 1 ];
					if ( b.Length == 1 ) { idx.Add( a[ 0 ] ); idx.Add( a[ 1 ] ); idx.Add( b[ 0 ] ); }
					else { idx.Add( a[ 0 ] ); idx.Add( a[ 1 ] ); idx.Add( b[ 1 ] ); idx.Add( a[ 0 ] ); idx.Add( b[ 1 ] ); idx.Add( b[ 0 ] ); }
				}
			}

			// a small leaf / flower lying on the ground: a triangle fan around a centre point. outline( a ) is the radius factor for angle a ( 0 = pointing along dir )
			public void Fan( double[] slot, int kind, double cx, double cz, double radius, double dirX, double dirZ, double tilt, int sides, Func<double, double> outline, double rnd, double lift, double hf = 0.1 )
			{
				var up = new[] { 0.0, 1.0, 0.0 };
				int c = V( new[] { cx, lift, cz }, up, ZERO, slot, new[] { hf, kind, rnd, 1.0 } );
				var ring = new int[ sides ];
				for ( int i = 0; i < sides; i ++ )
				{
					double a = ( double ) i / sides * Math.PI * 2;
					double r = radius * outline( a );
					double lx = Math.Cos( a ) * r, lz = Math.Sin( a ) * r * 0.85;
					double x = cx + lx * dirX - lz * dirZ;
					double z = cz + lx * dirZ + lz * dirX;
					double along = lx / radius;
					double y = lift + ( along + 0.6 ) * radius * tilt;
					ring[ i ] = V( new[] { x, y, z }, up, ZERO, slot, new[] { hf + 0.05 * Math.Max( 0, along ), kind, rnd, 0.0 } );
				}

				for ( int i = 0; i < sides; i ++ ) { idx.Add( c ); idx.Add( ring[ ( i + 1 ) % sides ] ); idx.Add( ring[ i ] ); }
			}

			// a thin ribbon lying on the ground ( creeper runner )
			public void Ribbon( double[] slot, int kind, List<double[]> pts, double width, double rnd )
			{
				var up = new[] { 0.0, 1.0, 0.0 };
				var ids = new List<int[]>();
				for ( int i = 0; i < pts.Count; i ++ )
				{
					var p = pts[ i ]; var q = pts[ Math.Min( pts.Count - 1, i + 1 ) ]; var o = pts[ Math.Max( 0, i - 1 ) ];
					double dx = q[ 0 ] - o[ 0 ], dz = q[ 2 ] - o[ 2 ];
					double l = Math.Sqrt( dx * dx + dz * dz ); if ( l == 0 ) l = 1;
					dx /= l; dz /= l;
					double sx = - dz * width, sz = dx * width;
					ids.Add( new[]
					{
						V( new[] { p[ 0 ] - sx, p[ 1 ], p[ 2 ] - sz }, up, ZERO, slot, new[] { 0.05, kind, rnd, 0.0 } ),
						V( new[] { p[ 0 ] + sx, p[ 1 ], p[ 2 ] + sz }, up, ZERO, slot, new[] { 0.05, kind, rnd, 0.0 } ),
					} );
				}

				for ( int i = 0; i < ids.Count - 1; i ++ )
				{
					var a = ids[ i ]; var b = ids[ i + 1 ];
					idx.Add( a[ 0 ] ); idx.Add( b[ 1 ] ); idx.Add( a[ 1 ] ); idx.Add( a[ 0 ] ); idx.Add( b[ 0 ] ); idx.Add( b[ 1 ] );
				}
			}

			// the winding is reversed: the faces the JS calls front are Unity's front in the mirrored image
			public Mesh Build( string name )
			{
				int vc = verts.Count, ic = idx.Count;
				var m = new Mesh { name = name, indexFormat = vc > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16, hideFlags = HideFlags.HideAndDontSave };
				m.SetVertexBufferParams( vc,
					new VertexAttributeDescriptor( VertexAttribute.Position, VertexAttributeFormat.Float32, 3 ),
					new VertexAttributeDescriptor( VertexAttribute.Normal, VertexAttributeFormat.Float32, 3 ),
					new VertexAttributeDescriptor( VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4 ),
					new VertexAttributeDescriptor( VertexAttribute.Color, VertexAttributeFormat.Float32, 4 ),
					new VertexAttributeDescriptor( VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2 ),
					new VertexAttributeDescriptor( VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2 ) );
				m.SetVertexBufferData( verts, 0, 0, vc );
				var ix = idx.ToArray();
				for ( int i = 0; i + 2 < ic; i += 3 ) { var t = ix[ i + 1 ]; ix[ i + 1 ] = ix[ i + 2 ]; ix[ i + 2 ] = t; }
				m.SetIndexBufferParams( ic, m.indexFormat );
				if ( m.indexFormat == IndexFormat.UInt16 )
				{
					var s = new ushort[ ic ];
					for ( int i = 0; i < ic; i ++ ) s[ i ] = ( ushort ) ix[ i ];
					m.SetIndexBufferData( s, 0, 0, ic );
				}
				else m.SetIndexBufferData( ix, 0, 0, ic );
				m.subMeshCount = 1;
				m.SetSubMesh( 0, new SubMeshDescriptor( 0, ic, MeshTopology.Triangles ), MeshUpdateFlags.DontRecalculateBounds );
				m.bounds = new Bounds( Vector3.zero, Vector3.one * 100f );
				return m;
			}

			public int triangles => idx.Count / 3;
		}

		sealed class Blade { public double dx, dz, lean, h, curve, rnd; public double[] bs; public int tier; }
		sealed class Clump { public double x, z, slotRand; public Blade[] blades; }

		// clump / blade parameters shared by all levels ( the same seeds: identical blades )
		static List<Clump> ClumpParams( uint seed )
		{
			var rand = new Mulberry32( seed );
			var slots = BlueNoise( new Mulberry32( seed + 1 ), CLUMPS, CELL );
			var o = new List<Clump>();
			foreach ( var s in slots )
			{
				var c = new Clump { x = s[ 0 ], z = s[ 1 ], blades = new Blade[ BLADES ] };
				c.slotRand = rand.Next();
				double az0 = rand.Next() * Math.PI * 2;
				for ( int k = 0; k < BLADES; k ++ )
				{
					// golden-angle azimuths: any prefix of the blades spreads around the clump; the first two ( kept at every distance ) lean away from each other
					double az = az0 + k * 2.39996 + ( rand.Next() - 0.5 ) * 0.5;
					double outer = k == 0 ? 0.1 + 0.3 * rand.Next() : k == 1 ? 0.35 + 0.4 * rand.Next() : rand.Next();
					var b = new Blade { dx = Math.Cos( az ), dz = Math.Sin( az ) };
					b.lean = 0.06 + 0.55 * outer * ( 0.6 + 0.8 * rand.Next() );
					b.h = ( 0.7 + 0.5 * rand.Next() ) * ( 1.06 - 0.22 * outer );
					b.curve = 0.12 + 0.5 * outer * ( 0.5 + rand.Next() );
					b.rnd = rand.Next();
					double bx = Math.Cos( az ) * 0.09 * rand.Next();
					double bz = Math.Sin( az ) * 0.09 * rand.Next();
					b.bs = new[] { bx, bz };
					b.tier = BLADE_TIER[ k ];
					c.blades[ k ] = b;
				}

				o.Add( c );
			}

			return o;
		}

		static readonly double[] ROWS0 = { 0, 0.3, 0.62, 1 }, ROWS1 = { 0, 0.55, 1 }, ROWS2 = { 0, 1 };
		static readonly double[] OAT_NEAR = { 0, 0.3, 0.6, 0.85, 1 }, OAT_MID = { 0, 0.6, 1 }, OAT_EXTRA = { 0, 0.45, 0.8, 1 };
		static readonly double[] NO_BASE = { 0, 0 };

		// level: 0 near, 1 mid, 2 far
		static PatchBuilder BuildPatch( int level, List<Clump> clumps, uint seed = 7 )
		{
			var b = new PatchBuilder();
			var rows = level == 0 ? ROWS0 : level == 1 ? ROWS1 : ROWS2;
			int maxTier = 2 - level;
			foreach ( var c in clumps )
			{
				var slot = new[] { c.x, c.z, c.slotRand, 0.0 };
				foreach ( var bl in c.blades )
				{
					if ( bl.tier > maxTier ) continue;
					b.Blade( slot, K_GRASS, rows, 0.012, bl.dx, bl.dz, bl.lean, bl.h, bl.curve, bl.rnd, bl.bs, bl.tier );
				}
			}

			if ( level == 2 ) return b;

			// sea oats ( tier 1: fade out before the far level ) and, near only, beach creeper. Every slot has its own random stream, so the near and mid versions of a plant are identical.
			var oatSlots = BlueNoise( new Mulberry32( seed + 2 ), OATS, CELL );
			bool near = level == 0;
			for ( int si = 0; si < oatSlots.Count; si ++ )
			{
				double x = oatSlots[ si ][ 0 ], z = oatSlots[ si ][ 1 ];
				var rand = new Mulberry32( unchecked( seed * 7919u + ( uint ) ( si * 131 + 17 ) ) );
				var slot = new[] { x, z, rand.Next(), 1.0 };
				int stalks = near ? 3 : 1;
				for ( int q = 0; q < 3; q ++ )
				{
					double az = rand.Next() * Math.PI * 2;
					double dx = Math.Cos( az ), dz = Math.Sin( az );
					double lean = 0.06 + 0.14 * rand.Next();
					double hq = 0.8 + 0.3 * rand.Next();
					double rnd = rand.Next();
					double bx = q == 0 ? 0 : ( rand.Next() - 0.5 ) * 0.22, bz = q == 0 ? 0 : ( rand.Next() - 0.5 ) * 0.22;
					var hs = new double[ 8 ];
					for ( int i = 0; i < 8; i ++ ) hs[ i ] = rand.Next();
					if ( q >= stalks ) continue;
					double qTier = q == 0 ? 0 : 2;
					// the stalk ( unit height, scaled in the shader )
					b.Blade( slot, K_OAT_STALK, near ? OAT_NEAR : OAT_MID, near ? 0.0075 : 0.014, dx, dz, lean, hq, 0.2, rnd, new[] { bx, bz }, qTier );
					// the drooping panicle: flat spikelets hanging off the upper stalk
					int heads = near ? 8 : 3;
					for ( int k = 0; k < heads; k ++ )
					{
						double hf = 0.7 + 0.3 * ( ( double ) k / Math.Max( 1, heads - 1 ) );
						double outD = ( Math.Sin( lean ) * hf + 0.2 * hf * hf ) * hq;
						double up = ( Math.Cos( lean ) * hf - 0.07 * hf * hf ) * hq;
						double sa = az + ( k % 2 != 0 ? 1 : - 1 ) * ( 0.45 + 0.6 * hs[ k ] );
						double sdx = Math.Cos( sa ), sdz = Math.Sin( sa );
						double cx = bx + dx * outD + sdx * 0.02, cz = bz + dz * outD + sdz * 0.02;
						double L = near ? 0.085 : 0.13, W = near ? 0.022 : 0.04;
						var n = new[] { 0.0, 1.0, 0.0 };
						var sideZero = new[] { 0.0, 0.0, 0.0, 0.0 };
						var slotB = new[] { hf, K_OAT_HEAD, rnd, qTier };
						int a = b.V( new[] { cx, up + 0.01, cz }, n, sideZero, slot, slotB );
						int l = b.V( new[] { cx + sdx * L * 0.45 - sdz * W, up - L * 0.4, cz + sdz * L * 0.45 + sdx * W }, n, sideZero, slot, slotB );
						int r = b.V( new[] { cx + sdx * L * 0.45 + sdz * W, up - L * 0.4, cz + sdz * L * 0.45 - sdx * W }, n, sideZero, slot, slotB );
						int t = b.V( new[] { cx + sdx * L * 0.8, up - L * 0.95, cz + sdz * L * 0.8 }, n, sideZero, slot, slotB );
						b.idx.Add( a ); b.idx.Add( l ); b.idx.Add( t ); b.idx.Add( a ); b.idx.Add( t ); b.idx.Add( r );
					}
				}

				if ( near )
				{
					for ( int k = 0; k < 4; k ++ )
					{
						double la = ( double ) k / 4 * Math.PI * 2 + rand.Next();
						double lean = 0.55 + 0.4 * rand.Next();
						b.Blade( slot, K_GRASS, OAT_EXTRA, 0.01, Math.Cos( la ), Math.Sin( la ), lean, 0.45, 0.35, rand.Next(), NO_BASE, 2 );
					}
				}
			}

			if ( ! near ) return b;

			// the beach creeper ( railroad vine ): a runner crawling over the sand with notched leaves
			Func<double, double> notched = a =>
			{
				double d = Math.Min( Math.Abs( a ), Math.Abs( a - Math.PI * 2 ) );
				return 1 - 0.38 * Math.Exp( - ( d * d ) / 0.12 ) - 0.15 * Math.Pow( Math.Sin( a * 0.5 ), 8 );
			};
			Func<double, double> petals = a => 0.82 + 0.18 * Math.Cos( a * 5 );
			var vineSlots = BlueNoise( new Mulberry32( seed + 3 ), VINES, CELL );
			for ( int si = 0; si < vineSlots.Count; si ++ )
			{
				double x = vineSlots[ si ][ 0 ], z = vineSlots[ si ][ 1 ];
				var rand = new Mulberry32( unchecked( seed * 3571u + ( uint ) ( si * 197 + 5 ) ) );
				var slot = new[] { x, z, rand.Next(), 2.0 };
				double az = rand.Next() * Math.PI * 2;
				var pts = new List<double[]>();
				int n = 12;
				double px = - Math.Cos( az ) * 1.1, pz = - Math.Sin( az ) * 1.1;
				for ( int i = 0; i < n; i ++ )
				{
					pts.Add( new[] { px, 0.01, pz } );
					az += ( rand.Next() - 0.5 ) * 0.5;
					px += Math.Cos( az ) * 0.19;
					pz += Math.Sin( az ) * 0.19;
				}

				b.Ribbon( slot, K_CREEPER, pts, 0.011, rand.Next() );
				for ( int i = 1; i < pts.Count; i ++ )
				{
					int side = i % 2 != 0 ? 1 : - 1;
					var p0 = pts[ i - 1 ]; var p1 = pts[ i ];
					double dx = p1[ 0 ] - p0[ 0 ], dz = p1[ 2 ] - p0[ 2 ];
					double l = Math.Sqrt( dx * dx + dz * dz ); if ( l == 0 ) l = 1;
					dx /= l; dz /= l;
					// the leaves stand on short petioles, angled forward off the runner, tilted up
					double la = Math.Atan2( dz, dx ) + side * ( 0.7 + 0.5 * rand.Next() );
					double ldx = Math.Cos( la ), ldz = Math.Sin( la );
					double r = ( 0.07 + 0.04 * rand.Next() ) * ( 0.6 + 0.4 * Math.Sin( Math.PI * i / n ) );
					double tilt = 0.3 + 0.3 * rand.Next();
					double lrnd = rand.Next();
					b.Fan( slot, K_CREEPER, p1[ 0 ] + ldx * r * 0.9, p1[ 2 ] + ldz * r * 0.9, r, ldx, ldz, tilt, 7, notched, lrnd, 0.015 );
				}

				var fp = pts[ 3 + ( int ) Math.Floor( rand.Next() * 4 ) ];
				double frnd = rand.Next();
				b.Fan( slot, K_FLOWER, fp[ 0 ] + 0.03, fp[ 2 ] + 0.03, 0.036, 1, 0, 1.4, 10, petals, frnd, 0.07, 0.2 );
			}

			return b;
		}

		// ---------------------------------------------------------------------------------------------------------------- the field

		sealed class Level
		{
			public Mesh mesh;
			public int max, tris;
		}

		// the cells of one camera type's current frame ( Game / Scene views see different cells )
		sealed class CamState
		{
			public readonly GraphicsBuffer[] buf = new GraphicsBuffer[ 3 ];
			public readonly Vector2[][] cells = new Vector2[ 3 ][];
			public readonly int[] count = new int[ 3 ];
		}

		readonly TerrainData terrain;
		readonly int cellsPerSide;
		readonly byte[] cellFlags;
		readonly float[] cellMinY, cellMaxY;
		readonly Level[] levels = new Level[ 3 ];
		readonly Dictionary<CameraType, CamState> states = new Dictionary<CameraType, CamState>();
		readonly Plane[] planes = new Plane[ 6 ];
		readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
		readonly Texture2D maskTex;
		readonly Material material;
		public readonly double buildMs;

		public GrassField( TerrainData terrain, byte[] mask, int mres )
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			this.terrain = terrain;
			int res = terrain.res;
			maskTex = new Texture2D( mres, mres, TextureFormat.RGBA32, false, true ) { name = "grassMask", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
			maskTex.SetPixelData( mask, 0 );
			maskTex.Apply( false, false );

			// per-cell occupancy and height range ( to skip empty cells and for the frustum tests )
			cellsPerSide = ( int ) Math.Ceiling( terrain.size / CELL );
			cellFlags = new byte[ cellsPerSide * cellsPerSide ];
			cellMinY = new float[ cellsPerSide * cellsPerSide ];
			cellMaxY = new float[ cellsPerSide * cellsPerSide ];
			double mpc = CELL / ( terrain.size / mres ); // mask texels per cell
			double hpc = CELL / terrain.texel; // height texels per cell
			for ( int cj = 0; cj < cellsPerSide; cj ++ )
				for ( int ci = 0; ci < cellsPerSide; ci ++ )
				{
					int any = 0; float mn = float.PositiveInfinity, mx = float.NegativeInfinity;
					int i0 = Math.Max( 0, ( int ) Math.Floor( ci * mpc ) - 1 ), i1 = Math.Min( mres - 1, ( int ) Math.Ceiling( ( ci + 1 ) * mpc ) + 1 );
					int j0 = Math.Max( 0, ( int ) Math.Floor( cj * mpc ) - 1 ), j1 = Math.Min( mres - 1, ( int ) Math.Ceiling( ( cj + 1 ) * mpc ) + 1 );
					for ( int j = j0; j <= j1 && any == 0; j ++ )
						for ( int i = i0; i <= i1; i ++ )
						{
							int o = ( j * mres + i ) * 4;
							if ( mask[ o ] > 8 || mask[ o + 1 ] > 8 || mask[ o + 2 ] > 8 || mask[ o + 3 ] > 8 ) { any = 1; break; }
						}

					int c = cj * cellsPerSide + ci;
					cellFlags[ c ] = ( byte ) any;
					if ( any == 0 ) continue;
					int hi0 = Math.Max( 0, ( int ) Math.Floor( ci * hpc ) - 1 ), hi1 = Math.Min( res - 1, ( int ) Math.Ceiling( ( ci + 1 ) * hpc ) + 1 );
					int hj0 = Math.Max( 0, ( int ) Math.Floor( cj * hpc ) - 1 ), hj1 = Math.Min( res - 1, ( int ) Math.Ceiling( ( cj + 1 ) * hpc ) + 1 );
					for ( int j = hj0; j <= hj1; j ++ )
						for ( int i = hi0; i <= hi1; i ++ )
						{
							float h = terrain.heights[ j * res + i ];
							if ( h < mn ) mn = h;
							if ( h > mx ) mx = h;
						}

					cellMinY[ c ] = mn; cellMaxY[ c ] = mx;
				}

			var clumps = ClumpParams( 7 );
			var maxes = new[] { MAX_NEAR, MAX_MID, MAX_FAR };
			var names = new[] { "grass-near", "grass-mid", "grass-far" };
			for ( int l = 0; l < 3; l ++ )
			{
				var p = BuildPatch( l, clumps );
				levels[ l ] = new Level { mesh = p.Build( "veg-" + names[ l ] ), max = maxes[ l ], tris = p.triangles };
			}

			var shader = Shader.Find( "Tidewater/VegGrass" );
			material = new Material( shader ) { name = "veg-grass", hideFlags = HideFlags.HideAndDontSave };
			material.SetTexture( "_GrassMask", maskTex );
			material.SetVector( "_GrassP", new Vector4( ( float ) terrain.origin, ( float ) terrain.size, ( float ) terrain.texel, res ) );
			buildMs = sw.Elapsed.TotalMilliseconds;
		}

		public int triangles( CameraType t ) { int n = 0; if ( states.TryGetValue( t, out var s ) ) for ( int l = 0; l < 3; l ++ ) n += s.count[ l ] * levels[ l ].tris; return n; }
		public string Info( CameraType t ) { if ( ! states.TryGetValue( t, out var s ) ) return "grass: -"; return $"grass: {s.count[ 0 ]} near / {s.count[ 1 ]} mid / {s.count[ 2 ]} far cells, {triangles( t ) / 1000} k triangles"; }

		// the visible cells around the camera ( sim frame position ), filled into the camera type's buffers
		void Update( Camera cam, CamState st )
		{
			GeometryUtility.CalculateFrustumPlanes( cam, planes );
			var p = cam.transform.position;
			double cx = p.x, cz = - p.z; // sim
			double origin = terrain.origin;
			int n = cellsPerSide;
			int i0 = Math.Max( 0, ( int ) Math.Floor( ( cx - R_FAR - origin ) / CELL ) ), i1 = Math.Min( n - 1, ( int ) Math.Floor( ( cx + R_FAR - origin ) / CELL ) );
			int j0 = Math.Max( 0, ( int ) Math.Floor( ( cz - R_FAR - origin ) / CELL ) ), j1 = Math.Min( n - 1, ( int ) Math.Floor( ( cz + R_FAR - origin ) / CELL ) );
			int nc = 0, mc = 0, fc = 0;
			for ( int j = j0; j <= j1; j ++ )
				for ( int i = i0; i <= i1; i ++ )
				{
					int c = j * n + i;
					if ( cellFlags[ c ] == 0 ) continue;
					double x0 = origin + i * CELL, z0 = origin + j * CELL;
					// the nearest point of the cell ( horizontal, like the shader's LOD distance )
					double dx = Math.Max( Math.Max( x0 - cx, 0 ), cx - x0 - CELL );
					double dz = Math.Max( Math.Max( z0 - cz, 0 ), cz - z0 - CELL );
					double d = Math.Sqrt( dx * dx + dz * dz );
					if ( d > R_FAR ) continue;
					float y0 = cellMinY[ c ] - 0.5f, y1 = cellMaxY[ c ] + 1.8f;
					var box = new Bounds( new Vector3( ( float ) ( x0 + CELL / 2.0 ), ( y0 + y1 ) / 2, ( float ) - ( z0 + CELL / 2.0 ) ), new Vector3( CELL, y1 - y0, CELL ) );
					if ( ! GeometryUtility.TestPlanesAABB( planes, box ) ) continue;
					var cell = new Vector2( ( float ) x0, ( float ) z0 );
					if ( d < R_NEAR && nc < MAX_NEAR ) st.cells[ 0 ][ nc ++ ] = cell;
					else if ( d < R_MID && mc < MAX_MID ) st.cells[ 1 ][ mc ++ ] = cell;
					else if ( d >= R_MID && fc < MAX_FAR ) st.cells[ 2 ][ fc ++ ] = cell;
				}

			st.count[ 0 ] = nc; st.count[ 1 ] = mc; st.count[ 2 ] = fc;
			for ( int l = 0; l < 3; l ++ ) if ( st.count[ l ] > 0 ) st.buf[ l ].SetData( st.cells[ l ], 0, 0, st.count[ l ] );
		}

		public void Draw( Camera cam, int layer )
		{
			if ( ! states.TryGetValue( cam.cameraType, out var st ) )
			{
				st = new CamState();
				for ( int l = 0; l < 3; l ++ ) { st.buf[ l ] = new GraphicsBuffer( GraphicsBuffer.Target.Structured, levels[ l ].max, 8 ); st.cells[ l ] = new Vector2[ levels[ l ].max ]; }
				states[ cam.cameraType ] = st;
			}

			Update( cam, st );
			var bounds = new Bounds( cam.transform.position, Vector3.one * 400f );
			for ( int l = 0; l < 3; l ++ )
			{
				if ( st.count[ l ] == 0 ) continue;
				block.SetBuffer( "_GrassCells", st.buf[ l ] );
				Graphics.DrawMeshInstancedProcedural( levels[ l ].mesh, 0, material, bounds, st.count[ l ], block, ShadowCastingMode.Off, true, layer, cam );
			}
		}

		public void Dispose()
		{
			foreach ( var st in states.Values ) foreach ( var b in st.buf ) b?.Release();
			states.Clear();
			foreach ( var l in levels ) if ( l != null && l.mesh != null ) UnityEngine.Object.DestroyImmediate( l.mesh );
			if ( material != null ) UnityEngine.Object.DestroyImmediate( material );
			if ( maskTex != null ) UnityEngine.Object.DestroyImmediate( maskTex );
		}
	}
}
