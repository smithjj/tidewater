using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Tidewater.World;
using Tidewater.World.Vegetation;
using Tidewater.World.Village;
using UnityEditor;
using UnityEngine;
using TerrainData = Tidewater.World.TerrainData;

// Compares the C# vegetation placement (Runtime/World/Vegetation/VegScatter.cs) with the JS original (unity/tools/dump-vegetation.mjs -> Temp/oracle/vegetation): the
// land cover on a grid of points, every plant of every type (the random numbers are drawn in the same order, so the records are the same plants) and the grass mask.
//   node unity/tools/dump-vegetation.mjs unity/Temp/oracle/vegetation && unity/tools/run-oracle.sh VegetationOracle
namespace Tidewater.EditorTools
{
	public static class VegetationOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/vegetation" ) );

		[MenuItem( "Tidewater/Compare vegetation with JS oracle" )]
		static void Menu() => Debug.Log( Compare( DefaultDir ) );

		static double[] ReadD( string p ) { var b = File.ReadAllBytes( p ); var a = new double[ b.Length / 8 ]; Buffer.BlockCopy( b, 0, a, 0, b.Length ); return a; }

		// the placement inputs the way Vegetation.js gives them (villageObstacles)
		public static VegSite MakeSite( TerrainData terrain, VillageBuild v ) { return VegSites.FromVillage( terrain, v.village ); }

		public sealed class VillageBuild { public Tidewater.World.Village.Village village; public Colliders colliders; }

		public static string Compare( string dir = null )
		{
			dir = string.IsNullOrEmpty( dir ) ? DefaultDir : dir;
			var sb = new StringBuilder();
			var sum = JObject.Parse( File.ReadAllText( dir + "/summary.json" ) );
			var terrain = new TerrainData( ( int ) sum[ "seed" ] );
			var colliders = new Colliders();
			var vb = new VillageBuild { village = new Tidewater.World.Village.Village( terrain, colliders ), colliders = colliders };
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var site = MakeSite( terrain, vb );
			sb.AppendLine( $"site: {sum[ "footprints" ]} footprints and {sum[ "paths" ]} paths in the JS" );

			// 1) the land cover on the grid of points
			{
				var want = ReadD( dir + "/cover.f64" );
				var keys = ( ( JArray ) sum[ "coverKeys" ] ).Select( k => ( string ) k ).ToArray();
				int stride = 2 + keys.Length, n = want.Length / stride;
				var worst = new double[ keys.Length ];
				var c = new VegCover();
				for ( int i = 0; i < n; i ++ )
				{
					site.Cover( want[ i * stride ], want[ i * stride + 1 ], c );
					double[] got = { c.h, c.ny, c.slope, c.macro, c.mA, c.mB, c.gully, c.forest, c.rock, c.bare, c.sand, c.path, c.scarp };
					for ( int k = 0; k < keys.Length; k ++ ) worst[ k ] = Math.Max( worst[ k ], Math.Abs( got[ k ] - want[ i * stride + 2 + k ] ) );
				}

				sb.AppendLine( $"land cover at {n} points: worst diff " + string.Join( ", ", keys.Select( ( k, i ) => $"{k} {worst[ i ]:E1}" ) ) );
			}

			// 2) the plants
			var rec = VegScatter.Scatter( site );
			var lists = new Dictionary<string, List<VegRec>> { { "palms", rec.palms }, { "trees", rec.trees }, { "bananas", rec.bananas }, { "shrubs", rec.shrubs }, { "youngPalms", rec.youngPalms },
				{ "ferns", rec.ferns }, { "monsteras", rec.monsteras }, { "elephantEars", rec.elephantEars }, { "heliconias", rec.heliconias }, { "strelitzias", rec.strelitzias } };
			{
				var want = ReadD( dir + "/records.f64" );
				var types = ( ( JArray ) sum[ "types" ] ).Select( k => ( string ) k ).ToArray();
				int at = 0; bool allSame = true;
				foreach ( var t in types )
				{
					int cnt = ( int ) sum[ "counts" ][ t ];
					var L = lists[ t ];
					double worst = 0; int bad = 0;
					for ( int i = 0; i < Math.Min( cnt, L.Count ); i ++ )
					{
						var r = L[ i ];
						double[] got = { r.x, r.y, r.z, r.s, r.sy, r.yaw, r.la, r.l, r.H, r.seed };
						for ( int k = 0; k < 10; k ++ )
						{
							// (the JS leaves `sy` undefined for the types with none: written as 0)
							double d = Math.Abs( got[ k ] - want[ ( at + i ) * 10 + k ] );
							if ( k == 4 && ! ( t == "trees" || t == "shrubs" ) ) d = 0;
							worst = Math.Max( worst, d );
							if ( d > 1e-9 ) bad ++;
						}
					}

					bool same = cnt == L.Count && bad == 0;
					allSame &= same;
					sb.AppendLine( $"{t}: {L.Count} plants (JS {cnt}), worst field diff {worst:E1}" + ( same ? "" : "  <-- DIFFERS" ) );
					at += cnt;
				}

				sb.AppendLine( $"village palms {rec.villagePalms} (JS {sum[ "villagePalms" ]}); all types equal: {allSame}" );
			}

			// 3) the grass mask
			{
				var want = File.ReadAllBytes( dir + "/grass.u8" );
				var got = VegScatter.BuildGrassMask( site, out int res );
				int bad = 0, maxD = 0;
				for ( int i = 0; i < want.Length; i ++ ) { int d = Math.Abs( got[ i ] - want[ i ] ); if ( d > 0 ) bad ++; maxD = Math.Max( maxD, d ); }
				sb.AppendLine( $"grass mask {res}^2 (JS {sum[ "grassRes" ]}): {bad} of {want.Length} bytes differ, largest difference {maxD}" );
			}

			sb.AppendLine( $"C# time {sw.ElapsedMilliseconds} ms" );
			return sb.ToString();
		}
	}
}
