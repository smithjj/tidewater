using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

// The plant models baked from the JS (unity/tools/dump-vegetation-geometry.mjs -> Resources/vegetation/<name>.bytes: PlantGeometry.js's builders). The vertices stay in
// the plant's frame, the SIM frame (the vertex stage of Shaders/Vegetation poses them and mirrors z at the very end); the winding is reversed so the faces the JS
// calls front are Unity's front in the mirrored image. Channels: tangent = aVeg, color = aMat, uv0 = ( u, v ), uv1 / uv2 = aLobe.
namespace Tidewater.World.Vegetation
{
	public static class VegMesh
	{
		const int MAGIC = 0x31474556; // 'VEG1'

		[StructLayout( LayoutKind.Sequential )]
		struct Vert { public Vector3 p, n; public Vector4 aVeg, aMat; public Vector2 uv, lobeA, lobeB; }

		// lobeTable / lobeOffset: a crown variant (PlantGeometry.js lobeVariantGeometry): the leaf cards move towards their lobe centre by the variant's lobe scale, for the impostor bake
		public static Mesh Load( string name, float[] lobeTable = null, int lobeOffset = 0 )
		{
			var ta = Resources.Load<TextAsset>( "vegetation/" + name );
			if ( ta == null ) throw new Exception( "vegetation mesh " + name + " is not baked (node unity/tools/dump-vegetation-geometry.mjs)" );
			var b = ta.bytes;
			if ( BitConverter.ToInt32( b, 0 ) != MAGIC ) throw new Exception( "vegetation mesh " + name + ": bad magic" );
			int vc = BitConverter.ToInt32( b, 4 ), ic = BitConverter.ToInt32( b, 8 );
			var f = new float[ vc * 20 ];
			Buffer.BlockCopy( b, 12, f, 0, vc * 80 );
			var v = new Vert[ vc ];
			for ( int i = 0; i < vc; i ++ )
			{
				int o = i * 20;
				v[ i ].p = new Vector3( f[ o ], f[ o + 1 ], f[ o + 2 ] );
				v[ i ].n = new Vector3( f[ o + 3 ], f[ o + 4 ], f[ o + 5 ] );
				v[ i ].uv = new Vector2( f[ o + 6 ], f[ o + 7 ] );
				v[ i ].aVeg = new Vector4( f[ o + 8 ], f[ o + 9 ], f[ o + 10 ], f[ o + 11 ] );
				v[ i ].aMat = new Vector4( f[ o + 12 ], f[ o + 13 ], f[ o + 14 ], f[ o + 15 ] );
				v[ i ].lobeA = new Vector2( f[ o + 16 ], f[ o + 17 ] );
				v[ i ].lobeB = new Vector2( f[ o + 18 ], f[ o + 19 ] );
				if ( lobeTable != null && f[ o + 19 ] >= 0 )
				{
					float k = 1f - lobeTable[ lobeOffset + ( int ) f[ o + 19 ] ];
					v[ i ].p += new Vector3( f[ o + 16 ], f[ o + 17 ], f[ o + 18 ] ) * k;
				}
			}

			var idx = new uint[ ic ];
			Buffer.BlockCopy( b, 12 + vc * 80, idx, 0, ic * 4 );
			for ( int i = 0; i + 2 < ic; i += 3 ) { var t = idx[ i + 1 ]; idx[ i + 1 ] = idx[ i + 2 ]; idx[ i + 2 ] = t; }

			var m = new Mesh { name = "veg-" + name, indexFormat = vc > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16, hideFlags = HideFlags.HideAndDontSave };
			m.SetVertexBufferParams( vc,
				new VertexAttributeDescriptor( VertexAttribute.Position, VertexAttributeFormat.Float32, 3 ),
				new VertexAttributeDescriptor( VertexAttribute.Normal, VertexAttributeFormat.Float32, 3 ),
				new VertexAttributeDescriptor( VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4 ),
				new VertexAttributeDescriptor( VertexAttribute.Color, VertexAttributeFormat.Float32, 4 ),
				new VertexAttributeDescriptor( VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2 ),
				new VertexAttributeDescriptor( VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2 ),
				new VertexAttributeDescriptor( VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 2 ) );
			m.SetVertexBufferData( v, 0, 0, vc );
			m.SetIndexBufferParams( ic, m.indexFormat );
			if ( m.indexFormat == IndexFormat.UInt16 )
			{
				var s = new ushort[ ic ];
				for ( int i = 0; i < ic; i ++ ) s[ i ] = ( ushort ) idx[ i ];
				m.SetIndexBufferData( s, 0, 0, ic );
			}
			else m.SetIndexBufferData( idx, 0, 0, ic );
			m.subMeshCount = 1;
			m.SetSubMesh( 0, new SubMeshDescriptor( 0, ic, MeshTopology.Triangles ), MeshUpdateFlags.DontRecalculateBounds );
			m.bounds = new Bounds( Vector3.zero, Vector3.one * 100f );
			return m;
		}
	}
}
