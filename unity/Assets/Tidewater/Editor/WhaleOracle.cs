using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using Tidewater.World.Marine;
using UnityEditor;
using UnityEngine;
using TerrainData = Tidewater.World.TerrainData;

// Compares the C# whale behaviour (Runtime/World/Marine/WhaleBrain.cs) with the JS original:
//   node unity/tools/dump-whale-brain.mjs unity/Temp/oracle/whale
//   unity/tools/whale-oracle.sh
// The file holds three runs of the brain (no water query, the sea at 0) with the state every 20th frame and every blow / breach / splash / slap listed with its frame.
namespace Tidewater.EditorTools
{
	public static class WhaleOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/whale" ) );

		static readonly string[] COLS = { "u", "x", "y", "z", "yaw", "pitch", "roll", "breachRoll", "speed", "vy", "arch", "follow", "strokePhase", "strokeAmp", "bob", "headPitch", "blow", "flukeUp",
			"yawRate", "arc", "f0.sweep", "f0.lift", "f0.twist", "f1.sweep", "f1.lift", "f1.twist", "qx", "qy", "qz", "qw", "pqx", "pqy", "pqz", "pqw", "wetAge" };

		public static string Compare( string dir = null )
		{
			dir = string.IsNullOrEmpty( dir ) ? DefaultDir : dir;
			var sb = new StringBuilder();
			var runs = JArray.Parse( File.ReadAllText( dir + "/whale-brain.json" ) );
			var terrain = new TerrainData();
			var q = new Tidewater.Engine.Quaternion();
			double worst = 0; string worstAt = ""; int rowsBad = 0, evBad = 0;
			foreach ( var run in runs )
			{
				string name = ( string ) run[ "name" ]; int frames = ( int ) run[ "frames" ], sample = ( int ) run[ "sample" ]; double dt = ( double ) run[ "dt" ];
				var b = new WhaleBrain( terrain, null, ( uint ) ( long ) run[ "seed" ] ) { forceBreach = ( bool ) run[ "forceBreach" ] };
				int blows = 0; b.onBlow = () => blows ++;
				var rows = ( JArray ) run[ "rows" ]; var events = ( JArray ) run[ "events" ];
				double lenDiff = Math.Abs( b.length - ( double ) run[ "length" ] );
				int ev = 0, bad = 0; double maxD = 0; string maxAt = "";
				double lb = 0, ls = 0, lsl = 0; int lbl = 0; string lst = b.state;
				for ( int f = 0; f < frames; f ++ )
				{
					b.update( dt );
					if ( b.breaches != lb || b.splashes != ls || b.slaps != lsl || blows != lbl || b.state != lst )
					{
						var e = ( JArray ) events[ ev < events.Count ? ev : events.Count - 1 ];
						bool same = ev < events.Count && ( int ) e[ 0 ] == f && ( double ) e[ 1 ] == b.breaches && ( double ) e[ 2 ] == b.splashes && ( double ) e[ 3 ] == b.slaps && ( int ) e[ 4 ] == blows && ( string ) e[ 5 ] == b.state;
						if ( ! same ) { evBad ++; if ( evBad < 5 ) sb.AppendLine( $"  {name}: event {ev} differs at frame {f} (state {b.state}, blows {blows})" ); }
						ev ++; lb = b.breaches; ls = b.splashes; lsl = b.slaps; lbl = blows; lst = b.state;
					}

					if ( f % sample == 0 )
					{
						var r = ( JArray ) rows[ f / sample ];
						b.pathRotation( 12, q );
						double[] got = { b.u, b.position.x, b.position.y, b.position.z, b.yaw, b.pitch, b.roll, b.breachRoll, b.speed, b.vy, b.arch, b.follow, b.strokePhase, b.strokeAmp, b.bob, b.headPitch, b.blow, b.flukeUp,
							b.yawRate, b.arc, b.fin[ 0 ].sweep, b.fin[ 0 ].lift, b.fin[ 0 ].twist, b.fin[ 1 ].sweep, b.fin[ 1 ].lift, b.fin[ 1 ].twist,
							b.quaternion.x, b.quaternion.y, b.quaternion.z, b.quaternion.w, q.x, q.y, q.z, q.w, b.wetAge };
						bool rb = false;
						for ( int c = 0; c < got.Length; c ++ )
						{
							double d = Math.Abs( got[ c ] - ( double ) r[ c ] );
							if ( d > maxD ) { maxD = d; maxAt = $"{COLS[ c ]} at frame {f}: {got[ c ]:R} vs JS {( double ) r[ c ]:R}"; }
							if ( d > 1e-6 ) rb = true;
						}

						if ( rb ) bad ++;
					}
				}

				sb.AppendLine( $"{name}: route length diff {lenDiff:E2}, {frames / sample} sampled rows ({bad} beyond 1e-6), max diff {maxD:E2} ({maxAt}), events {ev} of {events.Count}, blows {blows} of {( int ) run[ "blows" ]}" );
				if ( maxD > worst ) { worst = maxD; worstAt = name; }
				rowsBad += bad;
			}

			sb.AppendLine( rowsBad == 0 && evBad == 0 ? $"whale brain: identical to the JS (worst {worst:E2})" : $"whale brain: {rowsBad} rows and {evBad} events differ (worst {worst:E2} in {worstAt})" );
			return sb.ToString();
		}

		[MenuItem( "Tidewater/Compare whale with JS oracle" )]
		static void Menu() => UnityEngine.Debug.Log( Compare() );
	}
}
