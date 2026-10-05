using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Tidewater.World;
using Tidewater.World.Rocks;
using UnityEditor;
using UnityEngine;
using TerrainData = Tidewater.World.TerrainData;

// Compares the C# rocks (Runtime/World/Rocks) with the JS original (unity/tools/dump-rocks.mjs -> Temp/oracle/rocks): every placed rock ( position, size, style, radius, the 16 matrix elements ) and the eight rock
// meshes ( positions, normals, cavity, indices ).
//   node unity/tools/dump-rocks.mjs unity/Temp/oracle/rocks && unity/tools/run-oracle.sh RocksOracle
namespace Tidewater.EditorTools
{
	public static class RocksOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/rocks" ) );

		[MenuItem( "Tidewater/Compare rocks with JS oracle" )]
		static void Menu() => Debug.Log( Compare( DefaultDir ) );

		public static string Compare( string dir = null )
		{
			dir = string.IsNullOrEmpty( dir ) ? DefaultDir : dir;
			var sb = new StringBuilder();
			var sum = JObject.Parse( File.ReadAllText( dir + "/summary.json" ) );
			var terrain = new TerrainData( ( int ) sum[ "seed" ] );
			var colliders = new Colliders();
			var village = new Tidewater.World.Village.Village( terrain, colliders );
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var rocks = RockPlacement.Place( terrain, village );
			sb.AppendLine( $"placed {rocks.Count} rocks (JS {sum[ "count" ]}) in {sw.ElapsedMilliseconds} ms; {village.getFootprints().Count} footprints (JS {sum[ "footprints" ]})" );

			var b = File.ReadAllBytes( dir + "/instances.f64" );
			var want = new double[ b.Length / 8 ]; Buffer.BlockCopy( b, 0, want, 0, b.Length );
			int n = Math.Min( rocks.Count, ( int ) sum[ "count" ] ), bad = 0, badStyle = 0; double worst = 0, worstM = 0;
			for ( int i = 0; i < n; i ++ )
			{
				var r = rocks[ i ];
				double[] got = { r.x, r.y, r.z, r.size, r.sy, r.style, r.radius };
				for ( int k = 0; k < 7; k ++ ) { double d = Math.Abs( got[ k ] - want[ i * 23 + k ] ); if ( k == 5 && d > 0 ) badStyle ++; worst = Math.Max( worst, d ); if ( d > 1e-9 ) bad ++; }
				for ( int k = 0; k < 16; k ++ ) { double d = Math.Abs( r.matrix[ k ] - want[ i * 23 + 7 + k ] ); worstM = Math.Max( worstM, d ); if ( d > 1e-9 ) bad ++; }
			}

			sb.AppendLine( $"instances: worst field diff {worst:E1}, worst matrix element diff {worstM:E1}, {bad} fields differ, {badStyle} styles differ" + ( bad == 0 && rocks.Count == ( int ) sum[ "count" ] ? "" : "  <-- DIFFERS" ) );

			// the meshes
			var g = File.ReadAllBytes( dir + "/geometry.bin" );
			int at = 0;
			foreach ( var lv in ( JArray ) sum[ "levels" ] )
			{
				int style = ( int ) lv[ "style" ], sub = ( int ) lv[ "subdiv" ];
				int vc = BitConverter.ToInt32( g, at ), ic = BitConverter.ToInt32( g, at + 4 ); at += 8;
				var pos = new float[ vc * 3 ]; Buffer.BlockCopy( g, at, pos, 0, vc * 12 ); at += vc * 12;
				var nor = new float[ vc * 3 ]; Buffer.BlockCopy( g, at, nor, 0, vc * 12 ); at += vc * 12;
				var ao = new float[ vc ]; Buffer.BlockCopy( g, at, ao, 0, vc * 4 ); at += vc * 4;
				var idx = new int[ ic ]; Buffer.BlockCopy( g, at, idx, 0, ic * 4 ); at += ic * 4;
				var m = RockGeometry.Build( style, 17 + style * 101, sub );
				double dp = 0, dn = 0, da = 0; int di = 0;
				if ( m.vertexCount != vc || m.index.Length != ic ) { sb.AppendLine( $"{RockGeometry.STYLES[ style ].name} subdiv {sub}: {m.vertexCount} vertices / {m.index.Length} indices (JS {vc} / {ic})  <-- DIFFERS" ); continue; }
				for ( int i = 0; i < vc * 3; i ++ ) { dp = Math.Max( dp, Math.Abs( m.pos[ i ] - pos[ i ] ) ); dn = Math.Max( dn, Math.Abs( m.nor[ i ] - nor[ i ] ) ); }
				for ( int i = 0; i < vc; i ++ ) da = Math.Max( da, Math.Abs( m.ao[ i ] - ao[ i ] ) );
				for ( int i = 0; i < ic; i ++ ) if ( m.index[ i ] != idx[ i ] ) di ++;
				sb.AppendLine( $"{RockGeometry.STYLES[ style ].name} subdiv {sub}: {vc} vertices, {ic / 3} triangles; worst diff position {dp:E1}, normal {dn:E1}, cavity {da:E1}, {di} indices differ" );
			}

			return sb.ToString();
		}
	}
}
