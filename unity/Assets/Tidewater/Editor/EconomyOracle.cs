using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Tidewater.Game;
using UnityEditor;
using UnityEngine;

// Compares the C# economy (Game/: FishTable, Gear, Orders, Codex, GameState) with the JS original:
//   node unity/tools/dump-economy.mjs unity/Temp/oracle/economy
//   unity/tools/economy-oracle.sh
// The file holds a seeded script of operations with the result of each, the scalars after each, save snapshots, the market rolls, Joe's orders for days
// 1..400 and the guide's knowledge of every species; this replays the script on a GameState and diffs everything (exact for numbers).
namespace Tidewater.EditorTools
{
	public static class EconomyOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/economy" ) );

		sealed class Diffs
		{
			public int compared, bad; public double maxAbs; public readonly List<string> first = new List<string>();
			public void Note( string path, string why ) { bad ++; if ( first.Count < 6 ) first.Add( path + ": " + why ); }
		}

		static bool IsNull( JToken t ) => t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined || ( t is JValue v && v.Value == null );

		// exact structural comparison; null and a missing key are the same (JSON drops undefined)
		static void Diff( JToken a, JToken b, string path, Diffs d )
		{
			d.compared ++;
			if ( IsNull( a ) && IsNull( b ) ) return;
			if ( IsNull( a ) || IsNull( b ) ) { d.Note( path, $"{a?.ToString( Newtonsoft.Json.Formatting.None ) ?? "null"} vs JS {b?.ToString( Newtonsoft.Json.Formatting.None ) ?? "null"}" ); return; }
			if ( a is JObject oa && b is JObject ob )
			{
				foreach ( var k in oa.Properties().Select( p => p.Name ).Union( ob.Properties().Select( p => p.Name ) ) ) Diff( oa[ k ], ob[ k ], path + "." + k, d );
				return;
			}

			if ( a is JArray aa && b is JArray ab )
			{
				if ( aa.Count != ab.Count ) { d.Note( path, $"length {aa.Count} vs JS {ab.Count}" ); return; }
				for ( int i = 0; i < aa.Count; i ++ ) Diff( aa[ i ], ab[ i ], path + "[" + i + "]", d );
				return;
			}

			bool na = a.Type == JTokenType.Integer || a.Type == JTokenType.Float, nb = b.Type == JTokenType.Integer || b.Type == JTokenType.Float;
			if ( na && nb )
			{
				double x = ( double ) a, y = ( double ) b, e = Math.Abs( x - y );
				if ( ! ( e <= d.maxAbs ) && ! double.IsNaN( e ) ) d.maxAbs = e;
				if ( x != y ) d.Note( path, $"{x:R} vs JS {y:R}" );
				return;
			}

			if ( ! JToken.DeepEquals( a, b ) ) d.Note( path, $"{a} vs JS {b}" );
		}

		static JToken Num( double? v ) => v == null ? JValue.CreateNull() : new JValue( v.Value );

		static JObject Slim( InventoryFish f ) => f == null ? null : new JObject { [ "id" ] = f.id, [ "species" ] = f.species, [ "kg" ] = f.kg, [ "cm" ] = f.cm, [ "value" ] = f.value, [ "caughtAt" ] = f.caughtAt, [ "record" ] = f.record };

		static JToken OrderJson( Order o ) => o == null ? JValue.CreateNull() : new JObject { [ "day" ] = o.day, [ "species" ] = o.species, [ "minKg" ] = o.minKg, [ "filled" ] = o.filled, [ "bonus" ] = o.bonus };

		static JToken RangeJson( ValueRange r ) => r == null ? JValue.CreateNull() : new JObject { [ "lo" ] = r.lo, [ "hi" ] = r.hi, [ "exact" ] = r.exact };

		static JToken KnowledgeJson( Knowledge k )
		{
			var j = new JObject
			{
				[ "id" ] = k.id, [ "name" ] = k.name, [ "sci" ] = k.sci, [ "caught" ] = k.caught, [ "level" ] = k.level, [ "levelName" ] = k.levelName, [ "toNext" ] = Num( k.toNext ),
				[ "sold" ] = k.sold, [ "soldLevel" ] = k.soldLevel, [ "earned" ] = k.earned, [ "blurb" ] = k.blurb, [ "size" ] = RangeJson( k.size ), [ "price" ] = RangeJson( k.price ),
				[ "facts" ] = new JArray( k.facts ), [ "mapBlock" ] = k.mapBlock,
			};
			j[ "habitat" ] = k.habitat == null ? JValue.CreateNull() : new JObject
			{
				[ "list" ] = new JArray( k.habitat.list.Select( h => new JObject { [ "key" ] = h.key, [ "label" ] = h.label, [ "seen" ] = h.seen, [ "strength" ] = Num( h.strength ) } ) ),
				[ "exact" ] = k.habitat.exact, [ "none" ] = k.habitat.none,
			};
			j[ "time" ] = k.time == null ? JValue.CreateNull() : new JObject { [ "seen" ] = new JArray( k.time.seen ), [ "pref" ] = k.time.pref, [ "text" ] = k.time.text };
			j[ "best" ] = k.best == null ? JValue.CreateNull() : new JObject { [ "kg" ] = k.best.Value.kg, [ "cm" ] = Num( k.best.Value.cm ) };
			j[ "first" ] = k.first == null ? JValue.CreateNull() : new JObject { [ "day" ] = k.first.day, [ "hour" ] = k.first.hour };
			j[ "catches" ] = new JArray( k.catches.Select( c =>
			{
				var o = new JObject { [ "kg" ] = c.kg, [ "day" ] = c.day, [ "hour" ] = c.hour };
				if ( c.x != null ) { o[ "x" ] = c.x; o[ "z" ] = c.z; }
				if ( c.hab != null ) o[ "hab" ] = c.hab;
				return o;
			} ) );
			return j;
		}

		static JToken Apply( GameState s, MemorySaveStore store, JObject o )
		{
			switch ( ( string ) o[ "op" ] )
			{
				case "money": s.money = ( double ) o[ "v" ]; return null;
				case "day": s.day = ( int ) o[ "v" ]; return null;
				case "advanceDay": return s.advanceDay();
				case "clock": s.setClock( ( double ) o[ "v" ] ); return null;
				case "addFish":
				{
					CatchSpot where = null;
					if ( o[ "where" ] is JObject w ) where = new CatchSpot { x = ( double ) w[ "x" ], z = ( double ) w[ "z" ], hab = w[ "hab" ] == null || w[ "hab" ].Type == JTokenType.Null ? null : ( string ) w[ "hab" ] };
					var f = s.addFish( ( string ) o[ "species" ], ( double ) o[ "kg" ], ( double ) o[ "hour" ], where );
					var l = s.lastCatch;
					return new JObject { [ "f" ] = Slim( f ), [ "last" ] = new JObject { [ "species" ] = l.species, [ "kg" ] = l.kg, [ "cm" ] = l.cm, [ "value" ] = l.value, [ "newSpecies" ] = l.newSpecies, [ "record" ] = l.record,
						[ "prevBestKg" ] = l.prevBestKg, [ "prevBestCm" ] = l.prevBestCm, [ "kept" ] = l.kept } };
				}
				case "sell":
				{
					var ids = o[ "ids" ] is JArray a ? a.Select( t => ( int ) t ).ToList() : null;
					var r = s.sell( ids );
					return new JObject { [ "total" ] = r.total, [ "count" ] = r.count, [ "bonus" ] = r.bonus, [ "filled" ] = r.filled };
				}
				case "release": s.release( ( int ) o[ "id" ] ); return null;
				case "buy": { var n = s.buy( ( string ) o[ "key" ] ); return n == null ? null : new JObject { [ "index" ] = n.index, [ "cost" ] = n.cost, [ "label" ] = n.label }; }
				case "refuel": return s.refuel();
				case "burn": return s.burn( ( double ) o[ "v" ] );
				case "refuelCost": return s.refuelCost();
				case "spend": return s.spend( ( double ) o[ "v" ] );
				case "buyTraps": return s.buyTraps( ( int ) o[ "n" ] );
				case "setTrap": { var t = s.setTrap( ( double ) o[ "x" ], ( double ) o[ "z" ], ( double ) o[ "hour" ] ); return t == null ? null : new JObject { [ "id" ] = t.id, [ "x" ] = t.x, [ "z" ] = t.z, [ "day" ] = t.day, [ "clock" ] = t.clock }; }
				case "haulTrap": { var t = s.haulTrap( ( int ) o[ "id" ] ); return t == null ? null : new JObject { [ "id" ] = t.id, [ "x" ] = t.x, [ "z" ] = t.z, [ "day" ] = t.day, [ "clock" ] = t.clock }; }
				case "nearestSet": { var t = s.nearestSet( ( double ) o[ "x" ], ( double ) o[ "z" ], ( double ) o[ "max" ] ); return t == null ? null : ( JToken ) t.id; }
				case "priceOfInv": return new JArray( s.inventory.Select( f => s.priceOf( f ) ) );
				case "mulFor": return new JArray( FishTable.FISH_IDS.Select( id => s.mulFor( id ) ) );
				case "order": return OrderJson( s.todaysOrder );
				case "reload": { var t = new GameState( store ); t.load(); return t.toJSON(); }
			}

			throw new Exception( "unknown op " + o );
		}

		public static string Compare( string dir = null )
		{
			dir = dir ?? DefaultDir;
			var js = JObject.Parse( File.ReadAllText( Path.Combine( dir, "economy.json" ) ) );
			var sb = new StringBuilder();
			void Report( string name, Diffs d ) => sb.Append( $"  {name}: {d.compared} compared, {d.bad} differ, max abs {d.maxAbs:E2}" + ( d.first.Count > 0 ? "\n    " + string.Join( "\n    ", d.first ) : "" ) + "\n" );

			// ---- the script
			var store = new MemorySaveStore();
			var s = new GameState( store );
			var ops = ( JArray ) js[ "ops" ]; var results = ( JArray ) js[ "results" ]; var scalars = ( JArray ) js[ "scalars" ]; var snaps = ( JObject ) js[ "snaps" ];
			Diffs dRes = new Diffs(), dSc = new Diffs(), dSnap = new Diffs();
			var opCount = new Dictionary<string, int>();
			for ( int i = 0; i < ops.Count; i ++ )
			{
				var o = ( JObject ) ops[ i ];
				string name = ( string ) o[ "op" ];
				opCount[ name ] = ( opCount.TryGetValue( name, out var c ) ? c : 0 ) + 1;
				var r = Apply( s, store, o );
				Diff( r, results[ i ], $"result[{i}:{name}]", dRes );
				var sc = new JObject { [ "money" ] = s.money, [ "fuelL" ] = s.fuelL, [ "holdKg" ] = s.holdKg, [ "holdValue" ] = s.holdValue, [ "day" ] = s.day, [ "traps" ] = s.traps, [ "sets" ] = s.sets.Count, [ "mayTrap" ] = s.mayTrap, [ "inv" ] = s.inventory.Count };
				Diff( sc, scalars[ i ], $"scalars[{i}:{name}]", dSc );
				if ( ( i + 1 ) % 25 == 0 ) Diff( s.toJSON(), snaps[ ( i + 1 ).ToString() ], $"snap[{i + 1}]", dSnap );
			}

			Diff( s.toJSON(), snaps[ "final" ], "snap[final]", dSnap );
			sb.Append( "script: " + string.Join( " ", opCount.OrderBy( kv => kv.Key ).Select( kv => kv.Key + "=" + kv.Value ) ) + "\n" );
			Report( "result of each operation", dRes );
			Report( "scalars after each operation", dSc );
			Report( "save snapshots (the JSON schema)", dSnap );

			// ---- a browser save loads: JS save -> C# load -> C# save is the same JSON
			var dRt = new Diffs();
			foreach ( var kv in snaps )
			{
				var t = new GameState( null );
				if ( ! t.fromJSON( ( JObject ) kv.Value ) ) { dRt.Note( kv.Key, "fromJSON refused" ); continue; }
				var back = t.toJSON();
				// JS leaves a stale market / order in the save as it was; the load keeps them as well
				Diff( back, kv.Value, "roundtrip[" + kv.Key + "]", dRt );
			}

			Report( "load a JS save, save it back", dRt );

			// ---- values, market, orders
			var dVal = new Diffs();
			foreach ( JArray v in ( JArray ) js[ "values" ] )
			{
				string id = ( string ) v[ 0 ]; double kg = ( double ) v[ 1 ];
				Diff( new JArray( id, kg, FishTable.fishValue( id, kg ), FishTable.fishLengthCm( id, kg ) ), v, $"value[{id},{kg}]", dVal );
			}

			Report( "fishValue / fishLengthCm (126 points)", dVal );
			var dMk = new Diffs();
			foreach ( var kv in ( JObject ) js[ "market" ] )
			{
				var t = new GameState( null ); t.day = int.Parse( kv.Key );
				Diff( new JArray( FishTable.FISH_IDS.Select( id => t.mulFor( id ) ) ), kv.Value, "market[" + kv.Key + "]", dMk );
			}

			Report( "market rolls", dMk );
			var dOr = new Diffs();
			foreach ( JObject row in ( JArray ) js[ "orders" ] )
			{
				int day = ( int ) row[ "day" ];
				Diff( OrderOrNull( Orders.orderFor( day, false ) ), row[ "a" ], $"order[{day}]", dOr );
				Diff( OrderOrNull( Orders.orderFor( day, true ) ), row[ "b" ], $"orderLobster[{day}]", dOr );
			}

			Diff( new JArray( new JArray( Orders.orderPool( false ) ), new JArray( Orders.orderPool( true ) ) ), js[ "pools" ], "pools", dOr );
			Report( "Joe's orders, days 1..400 (with and without lobster) and the pools", dOr );

			// ---- the guide
			var dK = new Diffs();
			foreach ( var kv in ( JObject ) js[ "know" ] )
			{
				var p = kv.Key.Split( ':' ); string id = p[ 0 ]; int count = int.Parse( p[ 1 ] ), sold = int.Parse( p[ 2 ] );
				var t = new GameState( null ); t.day = 3;
				for ( int i = 0; i < count; i ++ )
					t.addFish( id, 0.5 + i * 0.37 + ( i % 3 ) * 0.11, new[] { 6.0, 12, 18, 23.5 }[ i % 4 ], i % 2 == 1 ? new CatchSpot { x = 10 * i, z = - 20 * i, hab = new[] { "reef", "pier", "deep" }[ i % 3 ] } : null );
				t.log.TryGetValue( id, out var e );
				if ( e != null ) { e.sold = sold; e.earned = sold * 7.5; }
				var k = Codex.knowledge( id, e );
				var j = new JObject { [ "k" ] = KnowledgeJson( k ), [ "sizeText" ] = Codex.sizeText( k.size ), [ "priceText" ] = Codex.priceText( k.price ) };
				Diff( j, kv.Value, "know[" + kv.Key + "]", dK );
			}

			Report( "guide knowledge (19 species x 9 states) and the texts", dK );

			// ---- the habitat map helpers
			var dM = new Diffs();
			var mp = ( JObject ) js[ "maps" ];
			var grid = Codex.habitatGrid( "tuna", new MapBox { x0 = 0, z0 = 0, size = 100 }, 6, ( x, z ) => new Dictionary<string, double> { [ "deep" ] = x / 100, [ "reef" ] = z / 100 } );
			Diff( new JArray( grid.Select( f => ( double ) f ) ), mp[ "grid" ], "maps.grid", dM );
			var src = new float[ 36 ]; for ( int i = 0; i < 36; i ++ ) src[ i ] = i % 7;
			Diff( new JArray( Codex.coarsen( src, 6, 4 ).Select( f => ( double ) f ) ), mp[ "coarse" ], "maps.coarse", dM );
			JObject Box( MapBox b ) => new JObject { [ "x0" ] = b.x0, [ "z0" ] = b.z0, [ "size" ] = b.size };
			Diff( new JArray( Box( Codex.mapBox( new CatchRecord[ 0 ] ) ),
				Box( Codex.mapBox( new[] { new CatchRecord { x = 100, z = - 200 }, new CatchRecord { x = 160, z = - 150 } } ) ),
				Box( Codex.mapBox( new[] { new CatchRecord { x = - 600, z = 300 }, new CatchRecord { x = 600, z = - 800 } } ) ) ), mp[ "box" ], "maps.box", dM );
			Diff( new JArray( new[] { 0, 4.99, 5, 7.9, 8, 16.99, 17, 19.99, 20, 24, - 1, - 5 }.Select( h => Codex.periodOf( h ) ) ), mp[ "periods" ], "maps.periods", dM );
			Diff( new JArray( new[] { 0.5, 0.999, 1, 1.04, 1.05, 12.345, 100 }.Select( k => Orders.fmtKg( k ) ) ), mp[ "fmt" ], "maps.fmt", dM );
			Report( "map helpers, periodOf, fmtKg", dM );
			return sb.ToString();
		}

		static JToken OrderOrNull( Order o ) => o == null ? JValue.CreateNull() : new JObject { [ "day" ] = o.day, [ "species" ] = o.species, [ "minKg" ] = o.minKg };
	}
}
