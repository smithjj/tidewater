using System.Collections.Generic;
using UnityEngine;

// BufferGeometry (Tidewater.Engine, the JS three.js-style geometry) -> a UnityEngine.Mesh.
// Handedness: JS models are right-handed, Unity's are left-handed. `mirrorX` flips x (positions, normals) and reverses the winding, so a
// model built in the JS boat frame (+X port) sits in Unity with +X starboard and still faces the same way.
// Channels: position, normal, uv0 (uv), uv1 / uv2 = aux (rough, metal | pattern, anim; HDRP interpolates float2 texcoords), colors = the
// linear colour attribute.
namespace Tidewater.Engine
{
	public static class UnityMesh
	{
		public static Mesh Create( BufferGeometry g, string name, bool mirrorX = true )
		{
			var pos = g.attributes[ "position" ]; var nrm = g.getAttribute( "normal" ); var uv = g.getAttribute( "uv" );
			var col = g.getAttribute( "color" ); var aux = g.getAttribute( "aux" );
			int n = pos.count;
			float sx = mirrorX ? -1f : 1f;
			var v = new UnityEngine.Vector3[ n ]; var nn = nrm != null ? new UnityEngine.Vector3[ n ] : null; var uv0 = uv != null ? new UnityEngine.Vector2[ n ] : null;
			var cc = col != null ? new UnityEngine.Color[ n ] : null; var ux = aux != null ? new UnityEngine.Vector2[ n ] : null; var uz = aux != null ? new UnityEngine.Vector2[ n ] : null;
			for ( int i = 0; i < n; i ++ )
			{
				v[ i ] = new UnityEngine.Vector3( sx * pos.array[ i * 3 ], pos.array[ i * 3 + 1 ], pos.array[ i * 3 + 2 ] );
				if ( nn != null ) nn[ i ] = new UnityEngine.Vector3( sx * nrm.array[ i * 3 ], nrm.array[ i * 3 + 1 ], nrm.array[ i * 3 + 2 ] );
				if ( uv0 != null ) uv0[ i ] = new UnityEngine.Vector2( uv.array[ i * 2 ], uv.array[ i * 2 + 1 ] );
				if ( cc != null ) cc[ i ] = new UnityEngine.Color( col.array[ i * 3 ], col.array[ i * 3 + 1 ], col.array[ i * 3 + 2 ], 1f );
				if ( ux != null ) { ux[ i ] = new UnityEngine.Vector2( aux.array[ i * 4 ], aux.array[ i * 4 + 1 ] ); uz[ i ] = new UnityEngine.Vector2( aux.array[ i * 4 + 2 ], aux.array[ i * 4 + 3 ] ); }
			}

			var m = new Mesh { name = name, indexFormat = n > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
			m.SetVertices( v );
			if ( nn != null ) m.SetNormals( nn );
			if ( uv0 != null ) m.SetUVs( 0, uv0 );
			if ( ux != null ) { m.SetUVs( 1, ux ); m.SetUVs( 2, uz ); }
			if ( cc != null ) m.SetColors( cc );
			var idx = g.index != null ? g.index.array : null;
			var tri = new List<int>();
			if ( idx != null ) { for ( int i = 0; i + 2 < idx.Length; i += 3 ) { tri.Add( idx[ i ] ); if ( mirrorX ) { tri.Add( idx[ i + 2 ] ); tri.Add( idx[ i + 1 ] ); } else { tri.Add( idx[ i + 1 ] ); tri.Add( idx[ i + 2 ] ); } } }
			else { for ( int i = 0; i + 2 < n; i += 3 ) { tri.Add( i ); if ( mirrorX ) { tri.Add( i + 2 ); tri.Add( i + 1 ); } else { tri.Add( i + 1 ); tri.Add( i + 2 ); } } }
			m.SetTriangles( tri, 0 );
			m.RecalculateBounds();
			return m;
		}
	}
}
