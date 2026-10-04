using System;
using System.IO;
using System.Text;
using Tidewater.Ocean;
using UnityEditor;
using UnityEngine;
using TerrainData = Tidewater.World.TerrainData;

// Compares the C# Breakers.BuildStations (the shoreline stations the crest finder marches from) with the JS original:
//   node unity/tools/dump-breaker-stations.mjs unity/Temp/oracle/stations
//   unity/tools/run-oracle.sh BreakersOracle
namespace Tidewater.EditorTools
{
	public static class BreakersOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/stations" ) );

		[MenuItem( "Tidewater/Compare breaker stations with JS oracle" )]
		static void Menu() => Debug.Log( Compare( DefaultDir ) );

		public static string Compare( string dir = null )
		{
			dir = string.IsNullOrEmpty( dir ) ? DefaultDir : dir;
			var bytes = File.ReadAllBytes( dir + "/stations.f32" );
			var js = new float[ bytes.Length / 4 ];
			Buffer.BlockCopy( bytes, 0, js, 0, bytes.Length );
			var sb = new StringBuilder();
			var st = Breakers.BuildStations( new TerrainData( 7 ) );
			sb.AppendLine( $"stations: C# {st.count}, JS {js.Length / 4}" );
			int n = Math.Min( st.data.Length, js.Length ), exact = 0; double maxErr = 0;
			for ( int i = 0; i < n; i ++ )
			{
				if ( st.data[ i ] == js[ i ] ) exact ++;
				maxErr = Math.Max( maxErr, Math.Abs( st.data[ i ] - js[ i ] ) );
			}

			sb.AppendLine( $"values: {exact} / {n} bit-exact, max abs diff {maxErr:E2}" );
			return sb.ToString();
		}
	}
}
