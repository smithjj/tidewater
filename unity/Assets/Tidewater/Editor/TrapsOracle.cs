using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Tidewater.Game;
using Tidewater.Player;
using UnityEngine;
using Vector3 = Tidewater.Engine.Vector3;
using Quaternion = Tidewater.Engine.Quaternion;

// Compares the C# trap line (Game/Traps.cs, TrapsView.LoadGeometry) with the JS original:
//   node unity/tools/dump-traps.mjs unity/Temp/oracle/traps
//   unity/tools/traps-oracle.sh
// The file holds the mesh counts / bounds / checksums of the two baked files, the rules (which button fires, the deck stack, the soak), 400 haul rolls with an injected lcg, four
// scripted sessions of the real Traps class (a pot over the stern, a pot hauled aboard, a haul with every place full, a pot set from a six-pot stack) with the holder's pose every
// frame, and the buoy and line of three set pots over a synthetic sea.
namespace Tidewater.EditorTools
{
	public static class TrapsOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/traps" ) );

		static Func<double> Lcg( double seed ) => () => { seed = ( seed * 16807 ) % 2147483647; return seed / 2147483647; };

		sealed class Tally
		{
			public int compared, bad, big; public double maxAbs; public string worstAt = ""; public readonly List<string> first = new List<string>();
			public void Num( string at, double a, double b )
			{
				compared ++;
				double e = Math.Abs( a - b );
				if ( e > maxAbs || double.IsNaN( e ) ) { maxAbs = double.IsNaN( e ) ? double.PositiveInfinity : e; worstAt = $"{at}: {a:R} vs JS {b:R}"; }
				if ( a != b ) { bad ++; if ( first.Count < 5 ) first.Add( $"{at}: {a:R} vs JS {b:R}" ); }
				if ( ! ( e <= 1e-9 * ( 1 + Math.Abs( b ) ) ) ) big ++;
			}
			public void Str( string at, string a, string b )
			{
				compared ++;
				if ( a != b ) { bad ++; big ++; if ( first.Count < 5 ) first.Add( $"{at}: '{a}' vs JS '{b}'" ); }
			}
			public string Line( string name ) => $"  {name}: {compared} compared, {bad} differ in the last bits (max abs {maxAbs:E2}), {big} beyond 1e-9" + ( big > 0 ? "  <-- MISMATCH" : "" ) + "\n" + string.Concat( first.Select( f => "      " + f + "\n" ) );
		}

		static double[] A( JToken t ) => t.Select( x => ( double ) x ).ToArray();

		public static string Compare( string dir = null, string only = null )
		{
			dir = dir ?? DefaultDir;
			var js = JObject.Parse( File.ReadAllText( Path.Combine( dir, "traps.json" ) ) );
			var sb = new StringBuilder();
			if ( only == null || only == "mesh" ) Meshes( js, sb );
			if ( only == null || only == "rules" ) Rules( js, sb );
			if ( only == null || only == "yields" ) Yields( js, sb );
			if ( only == null || only == "sessions" ) Sessions( js, sb );
			if ( only == null || only == "buoys" ) Buoys( js, sb );
			return sb.ToString();
		}

		static void Meshes( JObject js, StringBuilder sb )
		{
			var t = new Tally();
			foreach ( var name in new[] { "pot", "buoy" } )
			{
				var j = js[ name ];
				var g = TrapsView.LoadGeometry( name );
				if ( g == null ) { t.Str( name, "missing", "loaded" ); continue; }
				var A5 = new[] { "position", "normal", "uv", "color", "aux" };
				int n = g.attributes[ "position" ].count;
				// the checksum runs over the vertex floats in file order
				double sum = 0; int k = 0;
				for ( int i = 0; i < n; i ++ )
					foreach ( var an in A5 ) { var a = g.attributes[ an ]; for ( int c = 0; c < a.itemSize; c ++ ) { sum += ( double ) a.array[ i * a.itemSize + c ] * ( 1 + ( k % 7 ) * 0.125 ); k ++; } }
				double sumI = 0; for ( int i = 0; i < g.index.count; i ++ ) sumI += g.index.array[ i ] * ( 1 + ( i % 7 ) * 0.125 );
				double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue }, mx = { double.MinValue, double.MinValue, double.MinValue };
				var pa = g.attributes[ "position" ];
				for ( int i = 0; i < n; i ++ ) for ( int c = 0; c < 3; c ++ ) { double v = pa.array[ i * 3 + c ]; mn[ c ] = Math.Min( mn[ c ], v ); mx[ c ] = Math.Max( mx[ c ], v ); }
				t.Num( name + ".vertices", n, ( double ) j[ "vertices" ] );
				t.Num( name + ".indices", g.index.count, ( double ) j[ "indices" ] );
				t.Num( name + ".sumVerts", sum, ( double ) j[ "sumVerts" ] );
				t.Num( name + ".sumIdx", sumI, ( double ) j[ "sumIdx" ] );
				for ( int c = 0; c < 3; c ++ ) { t.Num( $"{name}.min[{c}]", mn[ c ], ( double ) j[ "min" ][ c ] ); t.Num( $"{name}.max[{c}]", mx[ c ], ( double ) j[ "max" ][ c ] ); }
				// the file's triangles must index real vertices
				bool inRange = g.index.array.All( ix => ix >= 0 && ix < n );
				t.Str( name + ".indicesInRange", inRange.ToString(), "True" );
			}
			sb.Append( t.Line( "meshes (pot 85,616 and buoy 92,180 triangles)" ) );
		}

		static void Rules( JObject js, StringBuilder sb )
		{
			var t = new Tally();
			var c = js[ "consts" ];
			t.Num( "SOAK_MIN", Traps.SOAK_MIN, ( double ) c[ "SOAK_MIN" ] ); t.Num( "MAX_KEEP", Traps.MAX_KEEP, ( double ) c[ "MAX_KEEP" ] );
			t.Num( "TRAP_MAX_SPEED", Traps.TRAP_MAX_SPEED, ( double ) c[ "TRAP_MAX_SPEED" ] ); t.Num( "SET_ASTERN", Traps.SET_ASTERN, ( double ) c[ "SET_ASTERN" ] );
			t.Num( "TRAP.H", Tidewater.World.Boat.TRAP.H, ( double ) c[ "TRAP" ][ "H" ] );
			foreach ( var r in js[ "trigger" ] )
				t.Str( $"trigger[{r[ "mode" ]},{r[ "interact" ]},{r[ "work" ]},{r[ "live" ]},{r[ "modeChanged" ]}]",
					Traps.trapTriggered( ( string ) r[ "mode" ], ( bool ) r[ "interact" ], ( bool ) r[ "work" ], ( bool ) r[ "live" ], ( bool ) r[ "modeChanged" ] ).ToString(), ( ( bool ) r[ "v" ] ).ToString() );
			foreach ( var r in js[ "stack" ] )
				t.Str( $"stack[{r[ "aboard" ]},{r[ "held" ]}]", string.Join( ",", Traps.stackVisible( ( int ) r[ "aboard" ], ( int ) r[ "slots" ], ( int ) r[ "held" ] ) ), string.Join( ",", r[ "v" ].Select( x => ( ( bool ) x ).ToString() ) ) );
			foreach ( var r in js[ "soak" ] )
			{
				var set = new TrapSet { day = ( int ) r[ "set" ][ "day" ], clock = ( double ) r[ "set" ][ "clock" ] };
				t.Num( "soak", Traps.soakHours( set, ( double ) r[ "now" ][ "hour" ], ( int ) r[ "now" ][ "day" ] ), ( double ) r[ "v" ] );
			}
			sb.Append( t.Line( "rules (which button fires, the deck stack, the soak)" ) );
		}

		static void Yields( JObject js, StringBuilder sb )
		{
			var t = new Tally(); int pots = 0, animals = 0, lobsters = 0;
			foreach ( var r in js[ "yields" ] )
			{
				var h = Bites.habitatAt( ( double ) r[ "depth" ], ( double ) r[ "reefDist" ], ( double ) r[ "pierDist" ] );
				var got = Traps.haulYield( ( double ) r[ "soak" ], ( double ) r[ "depth" ], h, ( double ) r[ "hour" ], Lcg( ( double ) r[ "seed" ] ) );
				var want = ( JArray ) r[ "out" ];
				t.Num( "count", got.Count, want.Count );
				for ( int i = 0; i < Math.Min( got.Count, want.Count ); i ++ ) { t.Str( "species", got[ i ].species, ( string ) want[ i ][ 0 ] ); t.Num( "kg", got[ i ].kg, ( double ) want[ i ][ 1 ] ); }
				pots ++; animals += want.Count; lobsters += want.Count( w => ( string ) w[ 0 ] == "lobster" );
			}
			sb.Append( t.Line( $"haulYield ({pots} pots, {animals} animals, {lobsters} lobsters)" ) );
		}

		// the stand-in for a boat: a pose and a synthetic sea
		sealed class StubBoat : ITrapBoat
		{
			public Vector3 position { get; } = new Vector3();
			public Quaternion quaternion { get; } = new Quaternion();
			public double sternZ { get; set; }
			public Vector3 toWorld( Vector3 local, Vector3 o ) => o.copy( local ).applyQuaternion( quaternion ).add( position );
			public double sampleWaterAt( Vector3 p ) => 0.3 * Math.Sin( p.x * 0.2 );
		}

		static void Sessions( JObject js, StringBuilder sb )
		{
			var script = new Dictionary<string, (int aboard, double dt)>
			{
				[ "setPot" ] = ( 3, 1.0 / 60 ), [ "haulPot" ] = ( 2, 1.0 / 30 ), [ "haulFull" ] = ( 6, 1.0 / 60 ), [ "setSix" ] = ( 6, 1.0 / 45 ),
			};
			foreach ( var kv in script )
			{
				var j = js[ kv.Key ];
				var t = new Tally();
				var b = new StubBoat { sternZ = ( double ) j[ "boat" ][ "sternZ" ] };
				b.position.set( ( double ) j[ "boat" ][ "x" ], ( double ) j[ "boat" ][ "y" ], ( double ) j[ "boat" ][ "z" ] );
				var q = A( j[ "boat" ][ "q" ] ); b.quaternion.set( q[ 0 ], q[ 1 ], q[ 2 ], q[ 3 ] );

				var state = new GameState( new MemorySaveStore() );
				state.upgrades[ "trapLicence" ] = 1; state.money = 1e6; state.buyTraps( kv.Value.aboard );
				var tr = new Traps( state, ( x, z ) => - 6 - 0.01 * x, null, 0.35 );
				int splashes = 0; tr.onSplash = () => splashes ++;
				var frames = new List<(string ev, string vis, int holder, int busy, int splashes, Vector3 p, Quaternion q)>();
				void Log( string ev ) => frames.Add( ( ev, string.Join( "", tr.stackShown.Select( s => s ? "1" : "0" ) ), tr.holderVisible ? 1 : 0, tr.busy ? 1 : 0, splashes, tr.holderPos.clone(), tr.holderQuat.clone() ) );
				void Run( double dt ) { int n = 0; while ( tr.busy && n ++ < 900 ) { tr.update( dt ); Log( "step" ); } }

				switch ( kv.Key )
				{
					case "setPot": Log( "start" ); state.setTrap( 0, 0, 12 ); Log( "set" ); tr.setVisual( b, 2 ); Log( "visual" ); Run( kv.Value.dt ); break;
					case "haulPot": { var pot = state.setTrap( 0, 0, 6 ); Log( "start" ); state.haulTrap( pot.id ); tr.haulVisual( b ); Log( "visual" ); Run( kv.Value.dt ); break; }
					case "haulFull": { var pot = state.setTrap( 0, 0, 6 ); state.haulTrap( pot.id ); tr.haulVisual( b ); Log( "visual" ); Run( kv.Value.dt ); break; }
					case "setSix": state.setTrap( 1, 1, 12 ); tr.setVisual( b, 99 ); Log( "visual" ); Run( kv.Value.dt ); break;
				}

				var want = ( JArray ) j[ "frames" ];
				t.Num( "frames", frames.Count, want.Count );
				for ( int i = 0; i < Math.Min( frames.Count, want.Count ); i ++ )
				{
					var w = want[ i ]; var f = frames[ i ];
					string at = $"{kv.Key}[{i}]";
					t.Str( at + ".ev", f.ev, ( string ) w[ "ev" ] );
					t.Str( at + ".stack", f.vis, ( string ) w[ "vis" ] );
					t.Num( at + ".holder", f.holder, ( double ) w[ "holder" ] ); t.Num( at + ".busy", f.busy, ( double ) w[ "busy" ] ); t.Num( at + ".splashes", f.splashes, ( double ) w[ "splashes" ] );
					var wp = A( w[ "p" ] ); var wq = A( w[ "q" ] );
					t.Num( at + ".px", f.p.x, wp[ 0 ] ); t.Num( at + ".py", f.p.y, wp[ 1 ] ); t.Num( at + ".pz", f.p.z, wp[ 2 ] );
					t.Num( at + ".qx", f.q.x, wq[ 0 ] ); t.Num( at + ".qy", f.q.y, wq[ 1 ] ); t.Num( at + ".qz", f.q.z, wq[ 2 ] ); t.Num( at + ".qw", f.q.w, wq[ 3 ] );
				}

				var sp = j[ "stackPos" ]; var sy = A( j[ "stackYaw" ] );
				t.Num( "stackPlaces", tr.stackCount, sp.Count() );
				var tmp = new Vector3();
				for ( int i = 0; i < Math.Min( tr.stackCount, sp.Count() ); i ++ )
				{
					tr.slotAt( i, tmp ); var wp = A( sp[ i ] );
					t.Num( $"stack[{i}].x", tmp.x, wp[ 0 ] ); t.Num( $"stack[{i}].y", tmp.y, wp[ 1 ] ); t.Num( $"stack[{i}].z", tmp.z, wp[ 2 ] ); t.Num( $"stack[{i}].yaw", tr.slotYaw( i ), sy[ i ] );
				}

				tr.Dispose();
				sb.Append( t.Line( $"session {kv.Key} ({frames.Count} frames, {splashes} splash)" ) );
			}
		}

		sealed class StubQuery : IWaterQuery
		{
			public float latency => 0; public bool cpuValid => true; public int version => 0; public double resultTime => 0;
			public float[] cpu { get; } = new float[ 8 * 4 ];
			public float[] resultInputs { get; } = new float[ 8 * 2 ];
			public int Allocate( string name, int n ) => 0;
			public void SetPoint( int i, float x, float z ) { cpu[ i * 4 ] = ( float ) ( 0.4 * Math.Sin( x * 0.07 + z * 0.05 ) ); }
		}

		static void Buoys( JObject js, StringBuilder sb )
		{
			var t = new Tally();
			var state = new GameState( new MemorySaveStore() );
			state.upgrades[ "trapLicence" ] = 1; state.money = 1e6; state.buyTraps( 6 );
			var tr = new Traps( state, ( x, z ) => - 4 - 0.02 * Math.Abs( x ) + 0.01 * z, new StubQuery(), null );
			var sets = new[] { ( 10.0, 5.0 ), ( - 40.0, 20.0 ), ( 120.0, - 60.0 ) }.Select( ( p, i ) => state.setTrap( p.Item1, p.Item2, 6 + i ) ).ToList();
			tr.update( 1.0 / 60 );
			var want = ( JArray ) js[ "buoys" ];
			t.Num( "views", tr.views.Count, want.Count );
			for ( int i = 0; i < Math.Min( tr.views.Count, want.Count ); i ++ )
			{
				var v = tr.views[ i ]; var w = want[ i ]; string at = $"buoy[{i}]";
				t.Num( at + ".id", v.id, ( double ) w[ "id" ] ); t.Num( at + ".slot", v.slot, ( double ) w[ "slot" ] ); t.Num( at + ".yaw", v.yaw, ( double ) w[ "yaw" ] );
				var pp = A( w[ "pot" ] ); t.Num( at + ".pot.x", v.pot.x, pp[ 0 ] ); t.Num( at + ".pot.y", v.pot.y, pp[ 1 ] ); t.Num( at + ".pot.z", v.pot.z, pp[ 2 ] ); t.Num( at + ".potYaw", v.potYaw, ( double ) w[ "potYaw" ] );
				var fp = A( w[ "float" ] ); t.Num( at + ".buoy.x", v.buoy.x, fp[ 0 ] ); t.Num( at + ".buoy.y", v.buoy.y, fp[ 1 ] ); t.Num( at + ".buoy.z", v.buoy.z, fp[ 2 ] ); t.Num( at + ".buoyRotZ", v.buoyRotZ, ( double ) w[ "floatRotZ" ] );
				var rp = A( w[ "rope" ] ); t.Num( at + ".rope.x", v.rope.x, rp[ 0 ] ); t.Num( at + ".rope.y", v.rope.y, rp[ 1 ] ); t.Num( at + ".rope.z", v.rope.z, rp[ 2 ] ); t.Num( at + ".ropeScaleY", v.ropeScaleY, ( double ) w[ "ropeScaleY" ] );
			}

			tr.Dispose();
			sb.Append( t.Line( "buoys and lines (three set pots over a synthetic sea)" ) );
		}
	}
}
