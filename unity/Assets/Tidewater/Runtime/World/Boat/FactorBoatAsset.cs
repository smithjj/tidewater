using System;
using System.Collections.Generic;
using System.Text;
using Tidewater.Engine;
using UnityEngine;

// The geometry of a glTF factor boat (MiniFishingBoat.js, Pelagic30.js) as the browser builds it from the file: node transforms baked in, the
// studio extras left out, one merged mesh per glTF material, the hull shell as the water-exclusion mask. Dumped by unity/tools/lib/factor-boat.mjs
// into Resources/<name>.bytes: uint32 header length, the JSON header, then per block float32 position, normal (3), uv (2), uint32 indices.
namespace Tidewater.World.Boat
{
	public sealed class FactorMaterial
	{
		public string name; public double[] color, emissive; public double roughness, metalness, clearcoat, clearcoatRoughness, opacity = 1;
		public bool glass; // the windshield: blended at `opacity`, no shadow (the JS names its mesh 'boat-glass')
		public BufferGeometry geometry;
	}

	public static class FactorBoatAsset
	{
		[Serializable] class Block { public int vertices, indices; public long offset; }
		[Serializable] class MatBlock : Block { public string name; public double[] color, emissive; public double roughness, metalness, clearcoat, clearcoatRoughness, opacity; public int glass; }
		[Serializable] class Header { public MatBlock[] materials; public Block mask; }

		// reads Resources/<resource>.bytes: fills `materials`, returns the mask geometry (null if the file is missing)
		public static BufferGeometry Load( string resource, List<FactorMaterial> materials, string dumpScript )
		{
			var asset = Resources.Load<TextAsset>( resource );
			if ( asset == null ) { UnityEngine.Debug.LogError( "FactorBoatAsset: Resources/" + resource + ".bytes is missing (node unity/tools/" + dumpScript + ")" ); return null; }
			var bytes = asset.bytes;
			int headLen = ( int ) BitConverter.ToUInt32( bytes, 0 );
			var head = JsonUtility.FromJson<Header>( Encoding.UTF8.GetString( bytes, 4, headLen ) );
			int data = 4 + headLen;

			BufferGeometry read( Block b )
			{
				int nv = b.vertices, ni = b.indices; long o = data + b.offset;
				var pos = new float[ nv * 3 ]; var nrm = new float[ nv * 3 ]; var uv = new float[ nv * 2 ]; var idx = new int[ ni ];
				Buffer.BlockCopy( bytes, ( int ) o, pos, 0, nv * 12 ); o += nv * 12;
				Buffer.BlockCopy( bytes, ( int ) o, nrm, 0, nv * 12 ); o += nv * 12;
				Buffer.BlockCopy( bytes, ( int ) o, uv, 0, nv * 8 ); o += nv * 8;
				Buffer.BlockCopy( bytes, ( int ) o, idx, 0, ni * 4 );
				var g = new BufferGeometry();
				g.setAttribute( "position", new BufferAttribute( pos, 3 ) );
				g.setAttribute( "normal", new BufferAttribute( nrm, 3 ) );
				g.setAttribute( "uv", new BufferAttribute( uv, 2 ) );
				g.setIndex( idx );
				g.computeBoundingSphere();
				return g;
			}

			foreach ( var m in head.materials )
				materials.Add( new FactorMaterial
				{
					name = m.name, color = m.color, emissive = m.emissive, roughness = m.roughness, metalness = m.metalness,
					clearcoat = m.clearcoat, clearcoatRoughness = m.clearcoatRoughness, glass = m.glass != 0, opacity = m.opacity == 0 ? 1 : m.opacity, geometry = read( m ),
				} );
			return read( head.mask );
		}
	}
}
