using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tidewater.Util;
using Tidewater.World;
using Tidewater.World.Terrain;
using UnityEditor;
using UnityEngine;
using TerrainData = Tidewater.World.TerrainData;

// Compares the C# terrain port with the JS original. The JS side is unity/tools/dump-terrain.mjs:
//   node unity/tools/dump-terrain.mjs unity/Temp/oracle/terrain
// then run  Tidewater.EditorTools.TerrainOracle.Compare( "<abs path to that folder>" )  (also Tidewater/Terrain oracle menu item).
namespace Tidewater.EditorTools
{
	public static class TerrainOracle
	{
		[Serializable] class Sample { public double x, z, noise, fbm, ridged, hash, env, erosion, coast, height, heightFn; }
		[Serializable] class SampleFile { public Sample[] samples; }

		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/terrain" ) );

		[MenuItem( "Tidewater/Compare terrain with JS oracle" )]
		static void Menu() => UnityEngine.Debug.Log( Compare( DefaultDir ) );

		public static string Compare( string dir = null )
		{
			dir = string.IsNullOrEmpty( dir ) ? DefaultDir : dir;
			var sb = new StringBuilder();
			var T = new TerrainData( 7 );
			sb.AppendLine( "C# generate ms: " + string.Join( ", ", Fmt( T.timings ) ) );

			CompareF( sb, "heights", T.heights, dir + "/heights.f32" );
			CompareF( sb, "rock", T.rock, dir + "/rock.f32" );
			CompareB( sb, "sand", T.sand, dir + "/sand.u8" );
			CompareB( sb, "path", T.path, dir + "/path.u8" );
			CompareB( sb, "gully", T.gully, dir + "/gully.u8" );
			CompareB( sb, "seagrass", T.seagrass, dir + "/seagrass.u8" );
			CompareB( sb, "rubble", T.rubble, dir + "/rubble.u8" );
			CompareB( sb, "scarp", T.scarp, dir + "/scarp.u8" );

			var maps = TerrainBake.BakeTerrainMaps( T );
			sb.AppendLine( $"bake ms: ao={maps.aoMs:F0} maps={maps.mapsMs:F0}" );
			CompareB( sb, "normal", maps.normal, dir + "/normal.u8" );
			CompareB( sb, "splat", maps.splat, dir + "/splat.u8" );
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var detail = DetailTexture.Get();
			sb.AppendLine( $"detail texture ms: {sw.ElapsedMilliseconds}" );
			CompareB( sb, "detail", detail, dir + "/detail.u8" );

			var file = JsonUtility.FromJson<SampleFile>( File.ReadAllText( dir + "/noise.json" ) );
			var n = new Noise2D( 7 );
			var worst = new Dictionary<string, double>();
			void W( string k, double a, double b ) { double d = Math.Abs( a - b ); if ( ! worst.TryGetValue( k, out double w ) || d > w ) worst[ k ] = d; }
			foreach ( var s in file.samples )
			{
				W( "noise", n.Noise( s.x / 37, s.z / 37 ), s.noise );
				W( "fbm", n.Fbm( s.x / 100, s.z / 100, 4 ), s.fbm );
				W( "ridged", n.Ridged( s.x / 25, s.z / 25, 4 ), s.ridged );
				W( "hash2", TerrainNoise.Hash2( ( int ) Math.Floor( s.x ), ( int ) Math.Floor( s.z ), 11 ), s.hash );
				W( "ridgeEnvelope", IslandShape.RidgeEnvelope( s.x, s.z ), s.env );
				W( "erosionNoise", TerrainNoise.ErosionNoise( s.x, s.z, 0.4, - 0.3 ), s.erosion );
				W( "coastDistance", T.CoastDistance( s.x, s.z ).d, s.coast );
				W( "heightAt", T.HeightAt( s.x, s.z ), s.height );
				W( "heightFn", T.HeightFn( s.x, s.z ).h, s.heightFn );
			}

			sb.AppendLine( "function samples (" + file.samples.Length + "), worst abs diff:" );
			foreach ( var kv in worst ) sb.AppendLine( $"  {kv.Key,-14} {kv.Value:E3}" );
			var b = T.BoundsFor( - 100, - 100, 100, 100 );
			sb.AppendLine( $"boundsFor(-100..100) = {b.min:R}, {b.max:R}   mmLevels={T.mmLevels.Count} rockSites={T.rockSites.Count}" );
			return sb.ToString();
		}

		static IEnumerable<string> Fmt( Dictionary<string, long> t ) { foreach ( var kv in t ) yield return kv.Key + "=" + kv.Value; }

		static void CompareF( StringBuilder sb, string name, float[] a, string file )
		{
			var bytes = File.ReadAllBytes( file );
			int n = bytes.Length / 4;
			if ( n != a.Length ) { sb.AppendLine( $"{name}: LENGTH MISMATCH {a.Length} vs {n}" ); return; }
			int diff = 0, big = 0; double max = 0; int first = -1;
			for ( int i = 0; i < n; i ++ )
			{
				float r = BitConverter.ToSingle( bytes, i * 4 );
				if ( a[ i ] == r ) continue;
				double d = Math.Abs( ( double ) a[ i ] - r );
				diff ++;
				if ( d > 1e-4 ) big ++;
				if ( d > max ) max = d;
				if ( first < 0 ) first = i;
			}

			sb.AppendLine( $"{name,-9} {n} texels: exact {n - diff}, differ {diff} ({100.0 * diff / n:F3}%), > 1e-4: {big}, max abs diff {max:E3}" + ( first >= 0 ? $", first at ({first % 2048}, {first / 2048}) cs={a[ first ]:R} js={BitConverter.ToSingle( bytes, first * 4 ):R}" : "" ) );
		}

		static void CompareB( StringBuilder sb, string name, byte[] a, string file )
		{
			var r = File.ReadAllBytes( file );
			if ( r.Length != a.Length ) { sb.AppendLine( $"{name}: LENGTH MISMATCH" ); return; }
			int diff = 0, max = 0, first = -1;
			for ( int i = 0; i < a.Length; i ++ )
			{
				if ( a[ i ] == r[ i ] ) continue;
				diff ++;
				max = Math.Max( max, Math.Abs( a[ i ] - r[ i ] ) );
				if ( first < 0 ) first = i;
			}

			sb.AppendLine( $"{name,-9} {a.Length} bytes: exact {a.Length - diff}, differ {diff} ({100.0 * diff / a.Length:F3}%), max diff {max}" + ( first >= 0 ? $", first at index {first} cs={a[ first ]} js={r[ first ]}" : "" ) );
		}
	}
}
