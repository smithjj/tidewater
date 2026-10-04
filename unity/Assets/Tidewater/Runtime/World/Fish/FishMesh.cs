using Tidewater.Engine;
using UnityEngine;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;
using UnityEngine.Rendering;

// The fish models (BufferGeometry in the model frame: length 1, snout at z = +0.5) as Unity meshes for the fish shader (TidewaterFish /
// TidewaterFishSwim): z mirrored and the winding reversed (the Unity world is the sim world mirrored in z); uv0 / uv1 = aData, uv2 / uv3 = the rest
// position (the shader poses the vertices: bends, jaw, swimming wave, so the bounds are expanded).
namespace Tidewater.World.Fish
{
	public static class FishMesh
	{
		public static Mesh Build( BufferGeometry g, string name )
		{
			var P = g.getAttribute( "position" ).array; var N = g.getAttribute( "normal" ).array; var D = g.getAttribute( "aData" ).array;
			int n = P.Length / 3;
			var v = new Vector3[ n ]; var nn = new Vector3[ n ]; var uv0 = new Vector2[ n ]; var uv1 = new Vector2[ n ]; var uv2 = new Vector2[ n ]; var uv3 = new Vector2[ n ];
			for ( int i = 0; i < n; i ++ )
			{
				v[ i ] = new Vector3( P[ i * 3 ], P[ i * 3 + 1 ], - P[ i * 3 + 2 ] );
				nn[ i ] = new Vector3( N[ i * 3 ], N[ i * 3 + 1 ], - N[ i * 3 + 2 ] );
				uv0[ i ] = new Vector2( D[ i * 4 ], D[ i * 4 + 1 ] );
				uv1[ i ] = new Vector2( D[ i * 4 + 2 ], D[ i * 4 + 3 ] );
				uv2[ i ] = new Vector2( P[ i * 3 ], P[ i * 3 + 1 ] );
				uv3[ i ] = new Vector2( P[ i * 3 + 2 ], 0f );
			}

			var src = g.index.array;
			var idx = new int[ src.Length ];
			for ( int i = 0; i + 2 < idx.Length; i += 3 ) { idx[ i ] = src[ i ]; idx[ i + 1 ] = src[ i + 2 ]; idx[ i + 2 ] = src[ i + 1 ]; }

			var m = new Mesh { name = name, indexFormat = n > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16, hideFlags = HideFlags.HideAndDontSave };
			m.SetVertices( v ); m.SetNormals( nn ); m.SetUVs( 0, uv0 ); m.SetUVs( 1, uv1 ); m.SetUVs( 2, uv2 ); m.SetUVs( 3, uv3 );
			m.SetTriangles( idx, 0 );
			m.RecalculateBounds();
			var b = m.bounds; b.Expand( 0.6f ); m.bounds = b;
			return m;
		}
	}
}
