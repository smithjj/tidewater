using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Tidewater.Game;
using Tidewater.Player;
using Tidewater.World.Fish;
using UnityEngine;

// Compares the C# fishing (Game/: Bites, CatchMinigame, Sonar, FishingRod) with the JS original:
//   node unity/tools/dump-fishing.mjs unity/Temp/oracle/fishing
//   unity/tools/fishing-oracle.sh
// The file holds the habitat grid, the activity table, 600 bite rolls (seeded), 160 line fights, the fish finder's readings of synthetic schools and
// six rod scenarios (the state every 2nd frame under a scripted camera, a synthetic sea and floor and scripted button events).
namespace Tidewater.EditorTools
{
	public static class FishingOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/fishing" ) );

		static Func<double> Lcg( double seed ) => () => { seed = ( seed * 16807 ) % 2147483647; return seed / 2147483647; };

		sealed class Tally
		{
			public int compared, bad, big; public double maxAbs; public string worstAt = ""; public readonly List<string> first = new List<string>(), bigFirst = new List<string>();
			public void Num( string at, double a, double b )
			{
				compared ++;
				double e = Math.Abs( a - b );
				if ( double.IsNaN( a ) && double.IsNaN( b ) ) return;
				if ( double.IsInfinity( a ) && a == b ) return;
				if ( e > maxAbs || double.IsNaN( e ) ) { maxAbs = double.IsNaN( e ) ? double.PositiveInfinity : e; worstAt = $"{at}: {a:R} vs JS {b:R}"; }
				if ( a != b ) { bad ++; if ( first.Count < 5 ) first.Add( $"{at}: {a:R} vs JS {b:R}" ); }
				if ( ! ( e <= 1e-9 * ( 1 + Math.Abs( b ) ) ) ) { big ++; if ( bigFirst.Count < 5 ) bigFirst.Add( $"{at}: {a:R} vs JS {b:R}" ); }
			}
			public void Str( string at, string a, string b )
			{
				compared ++;
				if ( a != b ) { bad ++; big ++; if ( first.Count < 5 ) first.Add( $"{at}: '{a}' vs JS '{b}'" ); if ( bigFirst.Count < 5 ) bigFirst.Add( $"{at}: '{a}' vs JS '{b}'" ); }
			}
			public string Line( string name ) => $"  {name}: {compared} compared, {bad} differ in the last bits (max abs {maxAbs:E2}), {big} beyond 1e-9" + ( big > 0 ? "  <-- MISMATCH" : "" ) + "\n" + string.Concat( bigFirst.Select( f => "      " + f + "\n" ) );
		}

		static double D( JToken t ) => t.Type == JTokenType.String && ( string ) t == "inf" ? double.PositiveInfinity : ( double ) t;
		static Habitat Hab( JToken a ) => new Habitat { shallows = ( double ) a[ 0 ], reef = ( double ) a[ 1 ], pier = ( double ) a[ 2 ], bay = ( double ) a[ 3 ], deep = ( double ) a[ 4 ] };
		static string S( JToken t ) => t == null || t.Type == JTokenType.Null ? null : ( string ) t;

		public static string Compare( string dir = null, string only = null )
		{
			dir = dir ?? DefaultDir;
			var js = JObject.Parse( File.ReadAllText( Path.Combine( dir, "fishing.json" ) ) );
			var sb = new StringBuilder();
			if ( only == null || only == "bites" ) Bites( js, sb );
			if ( only == null || only == "fights" ) Fights( js, sb );
			if ( only == null || only == "sonar" ) Sonar( js, sb );
			if ( only != null && only.StartsWith( "rod" ) ) Rods( js, sb, only == "rod" ? null : only.Substring( 4 ) );
			else if ( only == null ) Rods( js, sb, null );
			return sb.ToString();
		}

		static void Bites( JObject js, StringBuilder sb )
		{
			var t = new Tally();
			foreach ( var r in js[ "habitat" ] )
			{
				var i = r[ "in" ]; var h = Tidewater.Game.Bites.habitatAt( ( double ) i[ 0 ], ( double ) i[ 1 ], ( double ) i[ 2 ] );
				for ( int k = 0; k < 5; k ++ ) t.Num( $"habitat[{i[ 0 ]},{i[ 1 ]},{i[ 2 ]}].{Habitat.KEYS[ k ]}", h[ Habitat.KEYS[ k ] ], ( double ) r[ "out" ][ k ] );
			}
			sb.Append( t.Line( "habitatAt" ) );

			t = new Tally();
			foreach ( var r in js[ "activity" ] ) t.Num( $"activity[{r[ "pref" ]},{r[ "hour" ]}]", Tidewater.Game.Bites.activity( ( string ) r[ "pref" ], ( double ) r[ "hour" ] ), ( double ) r[ "v" ] );
			sb.Append( t.Line( "activity" ) );

			t = new Tally();
			var b = js[ "bites" ]; var rng = Lcg( ( double ) b[ "seed" ] );
			int n = 0, hits = 0;
			foreach ( var o in b[ "ops" ] )
			{
				var h = Hab( o[ "hab" ] ); double hour = ( double ) o[ "hour" ];
				SpeciesBias bias = o[ "bias" ] == null || o[ "bias" ].Type == JTokenType.Null ? null : new SpeciesBias { id = ( string ) o[ "bias" ][ "id" ], k = ( double ) o[ "bias" ][ "k" ] };
				string sp = Tidewater.Game.Bites.pickSpecies( h, hour, rng, bias );
				t.Str( $"bite[{n}].species", sp, S( o[ "species" ] ) );
				if ( sp != null ) { hits ++; t.Num( $"bite[{n}].kg", Tidewater.Game.Bites.rollWeight( sp, rng ), ( double ) o[ "kg" ] ); }
				t.Num( $"bite[{n}].delay", Tidewater.Game.Bites.biteDelay( h, hour, rng, ( double ) o[ "school" ] ), D( o[ "delay" ] ) );
				n ++;
			}
			sb.Append( t.Line( $"bite rolls ({n}, {hits} with a fish)" ) );
		}

		static void Fights( JObject js, StringBuilder sb )
		{
			var t = new Tally(); var outcomes = new int[ 4 ];
			int n = 0;
			foreach ( var tr in js[ "fights" ] )
			{
				string sp = ( string ) tr[ "species" ];
				var g = new CatchMinigame( sp, ( double ) tr[ "kg" ], ( double ) tr[ "lineKg" ], ( double ) tr[ "reelSpeed" ], ( double ) tr[ "distance" ], Lcg( ( double ) tr[ "seed" ] ) );
				t.Num( $"fight[{n}].power", g.power, ( double ) tr[ "power" ] );
				t.Num( $"fight[{n}].staminaMax", g.staminaMax, ( double ) tr[ "staminaMax" ] );
				t.Num( $"fight[{n}].maxDistance", g.maxDistance, ( double ) tr[ "maxDistance" ] );
				int style = ( int ) tr[ "style" ];
				var segs = ( ( JArray ) tr[ "segs" ] ).Select( s => new[] { ( double ) s[ 0 ], ( double ) s[ 1 ] } ).ToArray();
				const double dt = 1.0 / 60;
				string state = "fighting"; int f6 = 0, si = 0, ri = 0; double left = segs[ 0 ][ 0 ]; bool reeling = true;
				var rows = ( JArray ) tr[ "rows" ];
				while ( state == "fighting" && f6 < 60 * 240 )
				{
					while ( left <= 0 )
					{
						si ++; reeling = si % 2 == 0;
						left = segs[ Math.Min( si / 2, segs.Length - 1 ) ][ reeling ? 0 : 1 ];
						if ( si > 2000 ) break;
					}

					if ( style == 3 ) reeling = false;
					state = g.update( dt, reeling );
					left -= dt;
					if ( f6 % 6 == 0 || state != "fighting" )
					{
						if ( ri >= rows.Count ) { t.Str( $"fight[{n}].rows", "extra row", "" ); break; }
						var r = rows[ ri ++ ];
						int code = state == "fighting" ? 0 : state == "caught" ? 1 : state == "snapped" ? 2 : 3;
						double[] mine = { f6, g.tension, g.distance, g.stamina, g.surge, g.slack, g.overload, code };
						string[] names = { "f", "tension", "distance", "stamina", "surge", "slack", "overload", "state" };
						for ( int k = 0; k < mine.Length; k ++ ) t.Num( $"fight[{n}]({sp}) row {r[ 0 ]} {names[ k ]}", mine[ k ], ( double ) r[ k ] );
					}

					f6 ++;
				}

				t.Num( $"fight[{n}].frames", f6, ( double ) tr[ "frames" ] );
				t.Num( $"fight[{n}].rows", ri, rows.Count );
				outcomes[ ( int ) tr[ "state" ] ] ++;
				n ++;
			}

			sb.Append( t.Line( $"line fights ({n}: {outcomes[ 1 ]} caught, {outcomes[ 2 ]} snapped, {outcomes[ 3 ]} escaped)" ) );
		}

		static void Sonar( JObject js, StringBuilder sb )
		{
			var t = new Tally();
			var groups = new List<FishGroup>();
			int gi = 0;
			foreach ( var g in js[ "sonar" ][ "groups" ] )
			{
				var sp = new Tidewater.World.Fish.Behaviour { name = ( string ) g[ "sp" ][ "name" ], model = S( g[ "sp" ][ "model" ] ), mode = ( string ) g[ "sp" ][ "mode" ] };
				var fg = new FishGroup( sp, ( int ) g[ "count" ], 0, new FishZone { x = 0, z = 0, r = 1 }, new Tidewater.Util.Mulberry32( 1 ) );
				fg.center = new Tidewater.Engine.Vector3( ( double ) g[ "center" ][ "x" ], ( double ) g[ "center" ][ "y" ], ( double ) g[ "center" ][ "z" ] );
				fg.radius = ( double ) g[ "radius" ];
				groups.Add( fg ); gi ++;
			}

			int n = 0;
			foreach ( var q in js[ "sonar" ][ "queries" ] )
			{
				double x = ( double ) q[ "x" ], z = ( double ) q[ "z" ];
				var near = Tidewater.Game.Sonar.fishNear( groups, x, z, ( double ) q[ "r" ], ( int ) q[ "max" ], ( double ) q[ "minSep" ] );
				var jn = ( JArray ) q[ "near" ];
				t.Num( $"sonar[{n}].count", near.Count, jn.Count );
				for ( int i = 0; i < Math.Min( near.Count, jn.Count ); i ++ )
				{
					var a = near[ i ]; var b = jn[ i ];
					double[] mine = { a.x, a.z, a.depth, a.count, a.radius, a.dist };
					for ( int k = 0; k < 6; k ++ ) t.Num( $"sonar[{n}].near[{i}][{k}]", mine[ k ], ( double ) b[ k ] );
					t.Str( $"sonar[{n}].near[{i}].model", a.model, S( b[ 6 ] ) );
					t.Str( $"sonar[{n}].near[{i}].name", a.name, S( b[ 7 ] ) );
				}

				var sbite = Tidewater.Game.Sonar.schoolBite( groups, x, z );
				var jb = q[ "bite" ];
				if ( jb == null || jb.Type == JTokenType.Null ) t.Str( $"sonar[{n}].bite", sbite == null ? "null" : "a school", "null" );
				else if ( sbite == null ) t.Str( $"sonar[{n}].bite", "null", "a school" );
				else
				{
					t.Str( $"sonar[{n}].bite.model", sbite.model, S( jb[ 0 ] ) ); t.Str( $"sonar[{n}].bite.name", sbite.name, S( jb[ 1 ] ) );
					double[] mine = { sbite.dist, sbite.count, sbite.influence, sbite.bite, sbite.bias };
					for ( int k = 0; k < 5; k ++ ) t.Num( $"sonar[{n}].bite[{k}]", mine[ k ], ( double ) jb[ k + 2 ] );
				}

				n ++;
			}

			sb.Append( t.Line( $"fish finder ({n} readings of {groups.Count} schools)" ) );
		}

		static readonly string[] COLS = { "f", "state", "t", "power", "poseElev", "poseSide", "handX", "handY", "handZ", "bend", "load", "bendDirX", "bendDirZ", "rodBendX", "rodBendY", "rodBendZ", "rodBendW", "rodShape",
			"rotor", "bail", "crank", "spool", "spoolOsc", "lineFill", "tipX", "tipY", "tipZ", "bobX", "bobY", "bobZ", "velX", "velY", "velZ", "dip", "lineOut", "waterY", "depth", "fishX", "fishZ",
			"lineAX", "lineAY", "lineAZ", "lineBX", "lineBY", "lineBZ", "lineCX", "lineCY", "lineCZ", "rodVis", "lineVis", "bobVis", "bobScale", "bobTilt",
			"m0", "m1", "m2", "m3", "m4", "m5", "m6", "m7", "m8", "m9", "m10", "m11", "m12", "m13", "m14", "m15", "fightDist", "fightTension", "fightSurge", "fightStamina" };
		static readonly string[] STATES = { "stowed", "idle", "windup", "flick", "flying", "floating", "fighting", "retrieving", "landing" };

		static void Rods( JObject js, StringBuilder sb, string only )
		{
			foreach ( var sc in js[ "rod" ] )
			{
				string name = ( string ) sc[ "name" ];
				if ( only != null && only != name ) continue;
				var cam = new SimCamera();
				var query = new BoatControllerOracle.FakeQuery();
				var rod = new FishingRod( cam, query, BoatControllerOracle.groundAt );
				rod.setGear( ( double ) sc[ "castM" ], 1.1 );
				var lands = new List<string>();
				rod.onLand = w => lands.Add( w );
				var events = ( ( JArray ) sc[ "events" ] ).ToList();
				var F = sc[ "fight" ]; bool hasFight = F != null && F.Type != JTokenType.Null;
				CatchMinigame fight = null;
				int landEnd = - 1;
				var fightLog = new List<string>();
				var camLog = ( JArray ) sc[ "camLog" ];
				var rows = ( JArray ) sc[ "rows" ];
				const double dt = 1.0 / 60;
				int frames = ( int ) sc[ "frames" ], ri = 0;
				var t = new Tally(); var worstCol = new double[ COLS.Length ];
				for ( int f = 0; f < frames; f ++ )
				{
					double tm = f * dt;
					var c = camLog[ f ];
					cam.position.set( ( double ) c[ 0 ], ( double ) c[ 1 ], ( double ) c[ 2 ] );
					cam.quaternion.set( ( double ) c[ 3 ], ( double ) c[ 4 ], ( double ) c[ 5 ], ( double ) c[ 6 ] );
					foreach ( var e in events.Where( x => ( int ) x[ "f" ] == f ) )
					{
						string a = ( string ) e[ "a" ];
						if ( a == "equip" ) rod.equip( e[ "v" ] == null ? true : ( bool ) e[ "v" ] );
						else if ( a == "windup" ) rod.startWindup();
						else if ( a == "release" ) rod.release();
						else if ( a == "retrieve" ) rod.retrieve();
						else if ( a == "dip" ) rod.dip = ( double ) e[ "v" ];
					}

					if ( hasFight && f == ( int ) F[ "f" ] && rod.state == "floating" )
					{
						fight = new CatchMinigame( ( string ) F[ "species" ], ( double ) F[ "kg" ], ( double ) F[ "lineKg" ], ( double ) F[ "reelSpeed" ], Math.Max( 3, rod.lineOut ), Lcg( ( double ) F[ "seed" ] ) );
						rod.hook();
						fightLog.Add( "hook" );
					}

					if ( fight != null )
					{
						double ft = ( f - ( int ) F[ "f" ] ) * dt;
						string mode = ( string ) F[ "reel" ];
						bool reeling = mode == "always" ? true : mode == "never" ? false : ( ft % 1.6 ) < 1.2;
						string st = fight.update( dt, reeling );
						if ( st != "fighting" )
						{
							fightLog.Add( st );
							fight = null; rod.dip = 0;
							if ( st == "caught" ) { rod.land(); landEnd = f + 90; }
							else if ( st == "snapped" ) rod.setState( "idle" );
							else rod.endFight();
						}
					}

					if ( landEnd == f ) { if ( rod.state == "landing" ) rod.setState( "idle" ); landEnd = - 1; }
					query.Update( f, tm );
					rod.update( dt, true, fight );
					if ( f % 2 == 0 )
					{
						if ( ri >= rows.Count ) { t.Str( "rows", "extra", "" ); break; }
						var r = rows[ ri ++ ];
						var m = rod.rodMatrix.elements;
						var mine = new List<double> {
							f, Array.IndexOf( STATES, rod.state ), rod.t, rod.power, rod.poseElev, rod.poseSide, rod.poseHand.x, rod.poseHand.y, rod.poseHand.z,
							rod.bend, rod.load, rod.bendDir.x, rod.bendDir.z, rod.rodBend[ 0 ], rod.rodBend[ 1 ], rod.rodBend[ 2 ], rod.rodBend[ 3 ], rod.rodShapeX,
							rod.reelAnim[ 0 ], rod.reelAnim[ 1 ], rod.reelAnim[ 2 ], rod.reelAnim[ 3 ], rod.reelAnim2[ 0 ], rod.reelAnim2[ 1 ],
							rod.tip.x, rod.tip.y, rod.tip.z, rod.bobber.x, rod.bobber.y, rod.bobber.z, rod.bobberVel.x, rod.bobberVel.y, rod.bobberVel.z,
							rod.dip, rod.lineOut, rod.waterY, rod.depth, rod.fishPos.x, rod.fishPos.z,
							rod.lineA.x, rod.lineA.y, rod.lineA.z, rod.lineB.x, rod.lineB.y, rod.lineB.z, rod.lineCtl.x, rod.lineCtl.y, rod.lineCtl.z,
							rod.rodVisible ? 1 : 0, rod.lineVisible ? 1 : 0, rod.bobberVisible ? 1 : 0, rod.bobberScale, rod.bobberTilt };
						for ( int k = 0; k < 16; k ++ ) mine.Add( m[ k ] );
						mine.Add( fight != null ? fight.distance : - 1 ); mine.Add( fight != null ? fight.tension : - 1 ); mine.Add( fight != null ? fight.surge : - 1 ); mine.Add( fight != null ? fight.stamina : - 1 );
						for ( int k = 0; k < mine.Count; k ++ )
						{
							double jv = ( double ) r[ k ];
							t.Num( $"{name} frame {f} {COLS[ k ]}", mine[ k ], jv );
							worstCol[ k ] = Math.Max( worstCol[ k ], Math.Abs( mine[ k ] - jv ) );
						}
					}
				}

				t.Str( $"{name} fight log", string.Join( ",", fightLog ), string.Join( ",", ( ( JArray ) sc[ "fightLog" ] ).Select( l => ( string ) l[ 1 ] ) ) );
				t.Str( $"{name} landings", string.Join( ",", lands ), string.Join( ",", ( ( JArray ) sc[ "lands" ] ).Select( l => ( string ) l ) ) );
				var bad = Enumerable.Range( 0, COLS.Length ).Where( k => worstCol[ k ] > 0 ).Select( k => COLS[ k ] + "=" + worstCol[ k ].ToString( "E1" ) ).ToList();
				sb.Append( t.Line( $"rod [{name}] {ri} rows" ) );
				if ( bad.Count > 0 ) sb.Append( "      columns off: " + string.Join( " ", bad.Take( 14 ) ) + "\n" );
			}
		}
	}
}
