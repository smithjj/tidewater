using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Tidewater.Ocean;
using Tidewater.World;
using UnityEditor;
using UnityEngine;

// Compares Weather (Runtime/World/Weather.cs) with src/world/Weather.js (unity/tools/dump-weather.mjs -> Temp/oracle/weather): four scripted scenarios of 12,000 to
// 40,000 frames each (uneven frame times, a paused clock, a seed past 2^31, saves loaded, the wheel handed back), frame by frame the level, the target, the
// hold, the wind direction, the whole step, whether the cloud cover was written and the clock; every 25th frame what the sea received (wind, fetch, swell,
// choppiness, foam, surf, period, cover, G); every announcement; and the final save.
//   node unity/tools/dump-weather.mjs unity/Temp/oracle/weather && unity/tools/weather-oracle.sh
// `negative`: the same run with the generator's seed nudged by one on the C# side, which must be reported as a mismatch.
namespace Tidewater.EditorTools
{
	public static class WeatherOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/weather" ) );

		[MenuItem( "Tidewater/Compare weather with JS oracle" )]
		static void Menu() => UnityEngine.Debug.Log( Compare() );

		sealed class Tally
		{
			public int compared, bad, big; public double maxAbs; public readonly List<string> bigFirst = new List<string>();
			public void Num( string at, double a, double b )
			{
				compared ++;
				double e = Math.Abs( a - b );
				if ( e > maxAbs || double.IsNaN( e ) ) maxAbs = double.IsNaN( e ) ? double.PositiveInfinity : e;
				if ( a != b && ! ( double.IsNaN( a ) && double.IsNaN( b ) ) ) bad ++;
				if ( ! ( e <= 1e-9 * ( 1 + Math.Abs( b ) ) ) && ! ( double.IsNaN( a ) && double.IsNaN( b ) ) ) { big ++; if ( bigFirst.Count < 4 ) bigFirst.Add( $"{at}: {a:R} vs JS {b:R}" ); }
			}
			public void Eq( string at, string a, string b )
			{
				compared ++;
				if ( a != b ) { bad ++; big ++; if ( bigFirst.Count < 4 ) bigFirst.Add( $"{at}: '{a}' vs JS '{b}'" ); }
			}
			public string Line( string name ) => $"  {name}: {compared} compared, {bad} differ in the last bits (max abs {maxAbs:E2}), {big} beyond 1e-9" + ( big > 0 ? "  <-- MISMATCH" : "" ) + "\n" + string.Concat( bigFirst.Select( f => "      " + f + "\n" ) );
		}

		// what the JS stand-ins for fft / shore / clouds / G hold
		sealed class Sea
		{
			public double wind, windDir, fetch, swell, chop, foamBias, foamDecay, surf, period, cover = double.NaN, gx, gy, gspeed;
			public bool? resetFoam; public int spectrumCalls, coverCalls;
			public void Write( Condition v, bool spectrum, bool resetFoamArg, bool coverArg )
			{
				// Conditions.writeConditions
				if ( spectrum ) { wind = v.wind; windDir = v.windDir; fetch = v.fetch; swell = v.swell; resetFoam = resetFoamArg; spectrumCalls ++; }
				if ( coverArg ) { cover = v.cover; coverCalls ++; }
				double a = v.windDir * Math.PI / 180;
				gx = Math.Cos( a ); gy = Math.Sin( a ); gspeed = v.wind;
				chop = v.chop; foamBias = Conditions.FoamBias( v ); foamDecay = Conditions.FoamDecay( v );
				surf = v.surf; period = v.period;
			}
		}

		public static string Compare( string dir = null, bool negative = false )
		{
			dir = dir ?? DefaultDir;
			var j = JObject.Parse( File.ReadAllText( dir + "/weather.json" ) );
			int sample = ( int ) j[ "sample" ];
			var report = new System.Text.StringBuilder();
			var all = new Tally();
			foreach ( JObject sc in ( JArray ) j[ "scenarios" ] )
			{
				var spec = ( JObject ) sc[ "spec" ];
				string name = ( string ) spec[ "name" ];
				int stride = ( int ) sc[ "stride" ], frames = ( int ) spec[ "frames" ];
				var f = ( ( JArray ) sc[ "frames" ] ).Select( x => ( double ) x ).ToArray();
				var samples = ( JArray ) sc[ "samples" ];
				var ann = ( JArray ) sc[ "announcements" ];

				double hour = ( double ) spec[ "startHour" ], speed = 0;
				var sea = new Sea();
				var dtSpec = ( JObject ) spec[ "dt" ];
				Func<double> dtRnd = ( string ) dtSpec[ "kind" ] == "rand" ? Weather.Mulberry32( ( int ) dtSpec[ "seed" ] ) : null;
				var speeds = ( ( JArray ) spec[ "speed" ] ).Select( p => ( frame: ( int ) p[ 0 ], v: ( double ) p[ 1 ] ) ).ToList();
				var events = ( JArray ) spec[ "events" ];
				int seed = ( int ) spec[ "seed" ] + ( negative ? 1 : 0 );
				var w = new Weather( () => hour, () => speed, ( double ) spec[ "windDir0" ], sea.Write, seed );
				w.pace = ( double ) spec[ "pace" ];
				var got = new List<(int, string)>();
				int frame = 0;
				w.onAnnounce = text => got.Add( ( frame, text ) );

				var tFrame = new Tally(); var tSample = new Tally(); var tAnn = new Tally(); var tFinal = new Tally();
				int speedIdx = 0;
				for ( frame = 0; frame < frames; frame ++ )
				{
					while ( speedIdx < speeds.Count && speeds[ speedIdx ].frame <= frame ) speed = speeds[ speedIdx ++ ].v;
					foreach ( JObject e in events )
					{
						if ( ( int ) e[ "frame" ] != frame ) continue;
						if ( e[ "restore" ] != null ) w.restore( ( JObject ) e[ "restore" ] );
						if ( e[ "enabled" ] != null ) w.setEnabled( ( bool ) e[ "enabled" ] );
					}

					double dt = dtRnd != null ? ( double ) dtSpec[ "lo" ] + dtRnd() * ( ( double ) dtSpec[ "hi" ] - ( double ) dtSpec[ "lo" ] ) : ( double ) dtSpec[ "dt" ];
					if ( speed != 0 ) hour = ( hour + dt * speed + 24 ) % 24;
					int before = sea.coverCalls;
					w.update( dt );
					int o = frame * stride;
					string at = $"{name} f{frame}";
					tFrame.Num( at + " level", w.level, f[ o ] ); tFrame.Num( at + " target", w.target, f[ o + 1 ] ); tFrame.Num( at + " hold", w.HoldHours, f[ o + 2 ] );
					tFrame.Num( at + " windDir", w.windDir, f[ o + 3 ] ); tFrame.Num( at + " step", w.Step, f[ o + 4 ] );
					tFrame.Num( at + " coverWrites", sea.coverCalls - before, f[ o + 5 ] ); tFrame.Num( at + " hour", hour, f[ o + 6 ] );
					if ( frame % sample == 0 )
					{
						var s = ( JObject ) samples[ frame / sample ];
						if ( ( int ) s[ "frame" ] != frame ) throw new Exception( "sample index" );
						tSample.Eq( at + " name", w.name, ( string ) s[ "name" ] );
						double[] mine = { sea.wind, sea.windDir, sea.fetch, sea.swell, sea.chop, sea.foamBias, sea.foamDecay, sea.surf, sea.period, sea.cover, sea.gx, sea.gy, sea.gspeed, sea.spectrumCalls, sea.resetFoam == true ? 1 : 0 };
						string[] keys = { "wind", "windDir", "fetch", "swell", "chop", "foamBias", "foamDecay", "surf", "period", "cover", "gx", "gy", "gspeed", "spectrumCalls", "resetFoam" };
						for ( int k = 0; k < keys.Length; k ++ )
						{
							var tok = s[ keys[ k ] ];
							double want = keys[ k ] == "resetFoam" ? ( ( bool ) tok ? 1 : 0 ) : tok.Type == JTokenType.Null ? double.NaN : ( double ) tok;
							tSample.Num( at + " " + keys[ k ], mine[ k ], want );
						}
					}
				}

				tAnn.Num( name + " announcement count", got.Count, ann.Count );
				for ( int i = 0; i < Math.Min( got.Count, ann.Count ); i ++ )
				{
					tAnn.Num( $"{name} announcement {i} frame", got[ i ].Item1, ( int ) ann[ i ][ 0 ] );
					tAnn.Eq( $"{name} announcement {i} text", got[ i ].Item2, ( string ) ann[ i ][ 1 ] );
				}

				var fs = ( JObject ) sc[ "finalState" ]; var ms = w.state();
				tFinal.Num( name + " enabled", ( bool ) ms[ "enabled" ] ? 1 : 0, ( bool ) fs[ "enabled" ] ? 1 : 0 );
				foreach ( var key in new[] { "pace", "seed", "level", "target", "hold", "windDir", "dirBase" } ) tFinal.Num( name + " final " + key, ( double ) ms[ key ], ( double ) fs[ key ] );
				tFinal.Num( name + " spectrum writes", sea.spectrumCalls, ( int ) sc[ "spectrumCalls" ] ); tFinal.Num( name + " cover writes", sea.coverCalls, ( int ) sc[ "coverCalls" ] );

				report.Append( $"{name}: {frames} frames, {got.Count} announcements, {sea.coverCalls} cover writes, final level {w.level:0.000}\n" );
				report.Append( tFrame.Line( "the walk, every frame" ) ).Append( tSample.Line( "what the sea received, every 25th" ) ).Append( tAnn.Line( "announcements" ) ).Append( tFinal.Line( "the save" ) );
				foreach ( var t in new[] { tFrame, tSample, tAnn, tFinal } ) { all.compared += t.compared; all.bad += t.bad; all.big += t.big; all.maxAbs = Math.Max( all.maxAbs, t.maxAbs ); }
			}

			report.Append( all.Line( "ALL" ) );
			return report.ToString();
		}
	}
}
