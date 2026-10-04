using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Tidewater.Sky;
using UnityEditor;
using UnityEngine;
using Vector3 = Tidewater.Engine.Vector3;

// Compares SkyMath (Runtime/Sky/SkyMath.cs) with the JS (unity/tools/dump-sky.mjs -> Temp/oracle/sky): for 1452 (hour, azimuth) cases, the sun, the moon,
// the key light, which of them is the key, G.night, the key light colour, the night ambient, the star darkness and the sun's transmittance.
//   node unity/tools/dump-sky.mjs unity/Temp/oracle/sky && unity/tools/sky-oracle.sh
// `negative`: the same comparison with the sun azimuth nudged by a degree on the C# side, which must be reported as a mismatch.
namespace Tidewater.EditorTools
{
	public static class SkyOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/sky" ) );

		[MenuItem( "Tidewater/Compare sky maths with JS oracle" )]
		static void Menu() => UnityEngine.Debug.Log( Compare() );

		sealed class Tally
		{
			public int compared, bad, big; public double maxAbs; public readonly List<string> bigFirst = new List<string>();
			public void Num( string at, double a, double b )
			{
				compared ++;
				double e = Math.Abs( a - b );
				if ( e > maxAbs || double.IsNaN( e ) ) maxAbs = double.IsNaN( e ) ? double.PositiveInfinity : e;
				if ( a != b ) bad ++;
				if ( ! ( e <= 1e-9 * ( 1 + Math.Abs( b ) ) ) ) { big ++; if ( bigFirst.Count < 6 ) bigFirst.Add( $"{at}: {a:R} vs JS {b:R}" ); }
			}
			public string Line( string name ) => $"  {name}: {compared} compared, {bad} differ in the last bits (max abs {maxAbs:E2}), {big} beyond 1e-9" + ( big > 0 ? "  <-- MISMATCH" : "" ) + "\n" + string.Concat( bigFirst.Select( f => "      " + f + "\n" ) );
		}

		public static string Compare( string dir = null, bool negative = false )
		{
			dir = dir ?? DefaultDir;
			var j = JObject.Parse( File.ReadAllText( dir + "/sky.json" ) );
			int fields = ( int ) j[ "fields" ], n = ( int ) j[ "cases" ];
			var d = ( ( JArray ) j[ "data" ] ).Select( t => ( double ) t ).ToArray();
			string[] names = { "sun.x", "sun.y", "sun.z", "moon.x", "moon.y", "moon.z", "key.x", "key.y", "key.z", "sunIsKey", "night", "keyColor.r", "keyColor.g", "keyColor.b", "nightAmbient", "starDark", "T.r", "T.g", "T.b" };
			var t = names.ToDictionary( s => s, s => new Tally() );
			var st = new SkyState();
			var col = new Vector3(); var tr = new Vector3();
			for ( int i = 0; i < n; i ++ )
			{
				int o = i * ( fields + 2 );
				double hour = d[ o ], az = d[ o + 1 ];
				SkyMath.At( hour, az + ( negative ? 1 : 0 ), st );
				SkyMath.keyColor( st, col );
				SkyMath.sunTransmittance( st.sun.y, tr );
				double[] got = { st.sun.x, st.sun.y, st.sun.z, st.moon.x, st.moon.y, st.moon.z, st.key.x, st.key.y, st.key.z, st.sunIsKey ? 1 : 0, st.night, col.x, col.y, col.z, SkyMath.nightAmbient( st.night ), SkyMath.starDark( st.sun.y ), tr.x, tr.y, tr.z };
				for ( int k = 0; k < fields; k ++ ) t[ names[ k ] ].Num( $"h={hour} az={az} {names[ k ]}", got[ k ], d[ o + 2 + k ] );
			}

			// the eye (PostFX.js auto exposure): the target multiplier, and the HDRP curve built from it (float keys: a looser bound)
			var ae = ( ( JArray ) j[ "ae" ] ).Select( x => ( double ) x ).ToArray();
			var teye = new Tally(); var tcurve = new Tally();
			double ev0 = DayNight.ExposureEV( 0.55 );
			var curves = new Dictionary<double, AnimationCurve>();
			for ( int i = 0; i < ae.Length; i += 3 )
			{
				double avg = ae[ i ], night = ae[ i + 1 ], want = ae[ i + 2 ];
				teye.Num( $"avg={avg} night={night}", SkyMath.exposureTarget( avg, night ), want );
				if ( ! curves.TryGetValue( night, out var c ) ) curves[ night ] = c = DayNight.ExposureCurve( night );
				// the multiplier the curve exposes at, from its EV: m = 2^( ev0 - EV )
				double ev = c.Evaluate( ( float ) DayNight.SceneEVOf( avg ) );
				double got = Math.Pow( 2, ev0 - ev );
				tcurve.compared ++;
				double e = Math.Abs( got - want ) / want;
				if ( e > tcurve.maxAbs ) tcurve.maxAbs = e;
				if ( e > 2e-3 ) { tcurve.big ++; if ( tcurve.bigFirst.Count < 6 ) tcurve.bigFirst.Add( $"avg={avg} night={night}: curve m={got:R} vs JS {want:R}" ); }
			}

			var all = new Tally();
			foreach ( var kv in t ) { all.compared += kv.Value.compared; all.bad += kv.Value.bad; all.big += kv.Value.big; all.maxAbs = Math.Max( all.maxAbs, kv.Value.maxAbs ); }
			var sb = new System.Text.StringBuilder();
			sb.AppendLine( $"sky maths vs JS{( negative ? " (NEGATIVE CONTROL: azimuth +1 deg, must fail)" : "" )}: {n} cases" );
			foreach ( var kv in t ) if ( kv.Value.big > 0 || negative ) sb.Append( kv.Value.Line( kv.Key ) );
			sb.Append( all.Line( "ALL (sun, moon, key light)" ) );
			sb.Append( teye.Line( "eye target (exposureTarget)" ) );
			sb.AppendLine( $"  eye curve vs target: {tcurve.compared} compared, max relative error {tcurve.maxAbs:E2} (bound 2e-3), {tcurve.big} beyond" + ( tcurve.big > 0 ? "  <-- MISMATCH" : "" ) );
			foreach ( var f in tcurve.bigFirst ) sb.AppendLine( "      " + f );
			return sb.ToString();
		}
	}
}
