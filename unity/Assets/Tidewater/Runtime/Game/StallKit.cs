using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// The photoscanned stalls (src/game/StallKit.js, FishStand.js, Chandlery.js): Joe's fish stand and Marta's chandlery, each ONE merged mesh with ONE material.
// The geometry is the very mesh the browser builds: unity/tools/dump-stalls.mjs runs the real JS kit builder and writes Resources/stalls/{stand,chandlery}.bytes,
// verified vertex by vertex by Editor/StallKitOracle.cs. This file reads those, makes the Unity mesh, and builds the material (shader Tidewater/Stall,
// Shaders/Stall/StallFragment.hlsl: the surfaces of the JS material) with the texture arrays the JS builds in loadStallAssets.
//
// File: int32 'STL1', int32 vertexCount, int32 indexCount, vertexCount x ( pos 3, normal 3, uv 2, tint 3, layer, uv2 2 ) float32 (sim-local frame: x right, y up,
// z toward the customer), indexCount x uint32. The Unity mesh mirrors z (the stall's group sits at Unity ( x, y, -z ), see StallsView.Group) and flips the winding.
namespace Tidewater.Game
{
	public static class StallKit
	{
		const int MAGIC = 0x314c5453;
		// the tiling surfaces, in layer order (StallKit.js SURF), and the prop texture sets (props.json "layers")
		public static readonly string[] SURF = { "weathered_brown_planks", "weathered_planks", "worn_corrugated_iron", "weathered_peeling_timber" };
		public static readonly string[] PROPS =
		{
			"wooden_crate_02_0", "wooden_crate_01_0", "wooden_bucket_01_0", "fish_knife_0", "wooden_cutting_board_0", "lifebuoy_0", "wooden_lantern_01_0", "wooden_lantern_01_1",
			"fishermans_hat_0", "WoodenTable_03_0", "metal_jerrycan_green_0", "plastic_jerrycan_0", "life_jacket_0", "metal_toolbox_0", "wooden_display_shelves_01_0",
		};

		static Material material;
		static readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();

		// Resources/stalls/<name>.bytes -> the mesh (null when the file is missing or damaged)
		public static Mesh LoadMesh( string name )
		{
			var asset = Resources.Load<TextAsset>( "stalls/" + name );
			if ( asset == null ) { Debug.LogError( "StallKit: Resources/stalls/" + name + ".bytes is missing (node unity/tools/dump-stalls.mjs)" ); return null; }
			var b = asset.bytes;
			if ( b.Length < 12 || BitConverter.ToInt32( b, 0 ) != MAGIC ) { Debug.LogError( "StallKit: " + name + ".bytes is not a stall file" ); return null; }
			int nv = BitConverter.ToInt32( b, 4 ), ni = BitConverter.ToInt32( b, 8 );
			if ( b.Length != 12 + nv * 14 * 4 + ni * 4 ) { Debug.LogError( "StallKit: " + name + ".bytes has the wrong size" ); return null; }
			var f = new float[ nv * 14 ]; var idx = new int[ ni ];
			Buffer.BlockCopy( b, 12, f, 0, nv * 14 * 4 );
			Buffer.BlockCopy( b, 12 + nv * 14 * 4, idx, 0, ni * 4 );
			return Build( name, f, idx );
		}

		// vertex floats as the file has them; z mirrored, triangle winding reversed, tangents built from the uvs
		public static Mesh Build( string name, float[] f, int[] idx )
		{
			int nv = f.Length / 14, ni = idx.Length;
			var v = new Vertex[ nv ];
			var pos = new Vector3[ nv ]; var nrm = new Vector3[ nv ]; var uv = new Vector2[ nv ];
			for ( int i = 0; i < nv; i ++ )
			{
				int o = i * 14;
				pos[ i ] = new Vector3( f[ o ], f[ o + 1 ], - f[ o + 2 ] );
				nrm[ i ] = new Vector3( f[ o + 3 ], f[ o + 4 ], - f[ o + 5 ] );
				uv[ i ] = new Vector2( f[ o + 6 ], f[ o + 7 ] );
				v[ i ].pos = pos[ i ]; v[ i ].nrm = nrm[ i ]; v[ i ].uv = uv[ i ];
				v[ i ].col = new Vector4( f[ o + 8 ], f[ o + 9 ], f[ o + 10 ], f[ o + 11 ] );
				v[ i ].uv2 = new Vector2( f[ o + 12 ], f[ o + 13 ] );
			}

			var tri = new int[ ni ];
			for ( int i = 0; i < ni; i += 3 ) { tri[ i ] = idx[ i ]; tri[ i + 1 ] = idx[ i + 2 ]; tri[ i + 2 ] = idx[ i + 1 ]; }

			var tan = Tangents( pos, nrm, uv, tri );
			for ( int i = 0; i < nv; i ++ ) v[ i ].tan = tan[ i ];

			var mesh = new Mesh { name = name };
			mesh.indexFormat = IndexFormat.UInt32;
			mesh.SetVertexBufferParams( nv,
				new VertexAttributeDescriptor( VertexAttribute.Position, VertexAttributeFormat.Float32, 3 ),
				new VertexAttributeDescriptor( VertexAttribute.Normal, VertexAttributeFormat.Float32, 3 ),
				new VertexAttributeDescriptor( VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4 ),
				new VertexAttributeDescriptor( VertexAttribute.Color, VertexAttributeFormat.Float32, 4 ),
				new VertexAttributeDescriptor( VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2 ),
				new VertexAttributeDescriptor( VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2 ) );
			mesh.SetVertexBufferData( v, 0, 0, nv );
			mesh.SetIndexBufferParams( ni, IndexFormat.UInt32 );
			mesh.SetIndexBufferData( tri, 0, 0, ni );
			mesh.subMeshCount = 1;
			mesh.SetSubMesh( 0, new SubMeshDescriptor( 0, ni ) );
			mesh.RecalculateBounds();
			return mesh;
		}

		[System.Runtime.InteropServices.StructLayout( System.Runtime.InteropServices.LayoutKind.Sequential )]
		struct Vertex { public Vector3 pos; public Vector3 nrm; public Vector4 tan; public Vector4 col; public Vector2 uv; public Vector2 uv2; }

		// Normalize without Unity's cut-off: tiny triangles carry vectors far below 1e-5
		static Vector3 Unit( Vector3 v )
		{
			float m = Mathf.Max( Mathf.Abs( v.x ), Mathf.Max( Mathf.Abs( v.y ), Mathf.Abs( v.z ) ) );
			if ( ! ( m > 1e-30f ) ) return Vector3.zero;
			v /= m;
			return v / v.magnitude;
		}

		// The frame the JS material builds from derivatives: T along the uv's u, and the bitangent running the way image v runs DOWN (OpenGL normal maps, v up in the
		// texture, so -dP/dv), per vertex from its triangles (area weighted), orthogonal to the normal. xyz = T, w = the handedness of cross( N, T ) against that
		// bitangent. Vertices without a usable uv (ice, plain, rope: they do not use it) get any frame.
		public static Vector4[] Tangents( Vector3[] pos, Vector3[] nrm, Vector2[] uv, int[] tri )
		{
			int nv = pos.Length;
			var T = new Vector3[ nv ]; var Bd = new Vector3[ nv ];
			for ( int i = 0; i < tri.Length; i += 3 )
			{
				int a = tri[ i ], b = tri[ i + 1 ], c = tri[ i + 2 ];
				Vector3 e1 = pos[ b ] - pos[ a ], e2 = pos[ c ] - pos[ a ];
				Vector2 d1 = uv[ b ] - uv[ a ], d2 = uv[ c ] - uv[ a ];
				float det = d1.x * d2.y - d2.x * d1.y;
				if ( Mathf.Abs( det ) < 1e-20f ) continue;
				float r = 1f / det;
				Vector3 t = ( e1 * d2.y - e2 * d1.y ) * r;   // dP/du
				Vector3 bb = ( e2 * d1.x - e1 * d2.x ) * r;  // dP/dv
				// weight by the triangle's area so slivers do not decide
				float w = Vector3.Cross( e1, e2 ).magnitude;
				T[ a ] += Unit( t ) * w; T[ b ] += Unit( t ) * w; T[ c ] += Unit( t ) * w;
				Vector3 bn = - Unit( bb ) * w;
				Bd[ a ] += bn; Bd[ b ] += bn; Bd[ c ] += bn;
			}

			var o = new Vector4[ nv ];
			for ( int i = 0; i < nv; i ++ )
			{
				Vector3 n = nrm[ i ].normalized;
				Vector3 t = T[ i ] - n * Vector3.Dot( n, T[ i ] );
				if ( ! ( t.sqrMagnitude > 1e-24f ) )
				{
					// no frame from the uvs: any tangent
					t = Mathf.Abs( n.y ) < 0.9f ? Vector3.Cross( n, Vector3.up ) : Vector3.Cross( n, Vector3.right );
				}
				t = Unit( t );
				// Unity's bitangent is cross( N, T ) * w ( HDRP multiplies by the object's negative-scale sign: none here )
				float h = Vector3.Dot( Vector3.Cross( n, t ), Bd[ i ] ) < 0f ? - 1f : 1f;
				o[ i ] = new Vector4( t.x, t.y, t.z, h );
			}

			return o;
		}

		// ---- the material: the shader and the texture arrays of loadStallAssets

		public static Material GetMaterial()
		{
			if ( material != null ) return material;
			var shader = Shader.Find( "Tidewater/Stall" );
			if ( shader == null ) { Debug.LogError( "StallKit: shader Tidewater/Stall not found" ); return null; }
			material = new Material( shader ) { name = "stall", hideFlags = HideFlags.HideAndDontSave };
			var sa = Array( "s_", SURF, "_a", true ); var sn = Array( "s_", SURF, "_n", false ); var sr = Array( "s_", SURF, "_r", false );
			var pa = Array( "p_", PROPS, "_a", true ); var pn = Array( "p_", PROPS, "_n", false ); var pr = Array( "p_", PROPS, "_r", false );
			if ( sa == null || sn == null || sr == null || pa == null || pn == null || pr == null ) { Debug.LogError( "StallKit: the stall maps are missing: run Tidewater / Set up stalls" ); return null; }
			material.SetTexture( "_SkSurfA", sa ); material.SetTexture( "_SkSurfN", sn ); material.SetTexture( "_SkSurfR", sr );
			material.SetTexture( "_SkPropA", pa ); material.SetTexture( "_SkPropN", pn ); material.SetTexture( "_SkPropR", pr );
			var signs = Resources.Load<Texture2D>( "stalls/maps/signs" );
			if ( signs == null ) { Debug.LogError( "StallKit: signs.png is missing" ); return null; }
			material.SetTexture( "_SkSigns", signs );
			return material;
		}

		// one texture array from same-sized imported maps (GPU copies: the maps are imported compressed and not readable)
		static Texture2DArray Array( string prefix, string[] names, string suffix, bool srgb )
		{
			var src = new Texture2D[ names.Length ];
			for ( int i = 0; i < names.Length; i ++ )
			{
				src[ i ] = Resources.Load<Texture2D>( "stalls/maps/" + prefix + names[ i ] + suffix );
				if ( src[ i ] == null ) return null;
				if ( src[ i ].width != src[ 0 ].width || src[ i ].height != src[ 0 ].height || src[ i ].graphicsFormat != src[ 0 ].graphicsFormat ) { Debug.LogError( "StallKit: " + names[ i ] + suffix + " differs from the first map of its array" ); return null; }
			}

			var arr = new Texture2DArray( src[ 0 ].width, src[ 0 ].height, src.Length, src[ 0 ].graphicsFormat, UnityEngine.Experimental.Rendering.TextureCreationFlags.MipChain ) { name = "stall" + prefix + suffix };
			for ( int i = 0; i < src.Length; i ++ )
				for ( int m = 0; m < src[ i ].mipmapCount; m ++ ) Graphics.CopyTexture( src[ i ], 0, m, arr, i, m );
			arr.wrapMode = TextureWrapMode.Repeat; arr.filterMode = FilterMode.Trilinear; arr.anisoLevel = 8;
			owned.Add( arr );
			return arr;
		}

		public static void Release()
		{
			if ( material != null ) { UnityEngine.Object.DestroyImmediate( material ); material = null; }
			foreach ( var o in owned ) if ( o != null ) UnityEngine.Object.DestroyImmediate( o );
			owned.Clear();
		}
	}
}
