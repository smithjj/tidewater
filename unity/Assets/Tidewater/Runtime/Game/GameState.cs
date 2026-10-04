using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Tidewater.Engine;
using UnityEngine;

// Port of src/game/GameState.js: everything the player owns: wallet, the fish in the cooler / hold, the fish log and the gear levels. Saved after every
// change (the JS keeps it in localStorage; here a file under Application.persistentDataPath, in the same JSON schema, so a browser save loads); the
// store can be missing or throw, so every access is guarded and the game runs without it.
namespace Tidewater.Game
{
	// where the save lives (JS: localStorage)
	public interface ISaveStore
	{
		string GetItem( string key );
		void SetItem( string key, string value );
	}

	public sealed class FileSaveStore : ISaveStore
	{
		readonly string dir;
		public FileSaveStore( string dir = null ) { this.dir = dir ?? Application.persistentDataPath; }
		string PathOf( string key ) => Path.Combine( dir, key + ".json" );
		public string GetItem( string key ) { var p = PathOf( key ); return File.Exists( p ) ? File.ReadAllText( p ) : null; }
		public void SetItem( string key, string value ) { Directory.CreateDirectory( dir ); File.WriteAllText( PathOf( key ), value ); }
	}

	public sealed class MemorySaveStore : ISaveStore
	{
		public readonly Dictionary<string, string> items = new Dictionary<string, string>();
		public string GetItem( string key ) => items.TryGetValue( key, out var v ) ? v : null;
		public void SetItem( string key, string value ) { items[ key ] = value; }
	}

	// a fish in the cooler / hold: { id, species, kg, cm, value, caughtAt (game hours), record }
	public sealed class InventoryFish
	{
		public int id;
		public string species;
		public double kg, cm, value, caughtAt;
		public bool record;
	}

	// the last addFish: the catch card
	public sealed class LastCatch
	{
		public string species;
		public double kg, cm, value, prevBestKg, prevBestCm;
		public bool newSpecies, record, kept;
	}

	// a trap on the seabed, set at day * 24 + clock game hours
	public sealed class TrapSet { public int id; public double x, z; public int day; public double clock; }

	// where a catch came from (the bobber, or the pot), for the fish guide's map
	public sealed class CatchSpot { public double x, z; public string hab; }

	public sealed class SaleResult { public double total, bonus; public int count, filled; }

	public sealed class GameState
	{
		public const string SAVE_KEY = "tidewater.save.v1";

		public ISaveStore storage;
		public double money;
		public List<InventoryFish> inventory = new List<InventoryFish>();
		public Dictionary<string, LogEntry> log = new Dictionary<string, LogEntry>();   // species -> LogEntry (see Codex.emptyEntry)
		public LastCatch lastCatch;
		public Dictionary<string, int> upgrades = Gear.defaultUpgrades();
		public List<string> boats = new List<string>( Gear.START_BOATS ); // the boats you own (ids of Gear.BOATS), in BOAT_IDS order
		public double? fuel;   // litres left (null = full tank)
		// the world: which day it is, the time of day when the game was last saved, and the weather
		public int day = 1;
		public double? clock;
		public JObject weather;
		// the trap line: traps aboard (bought, not yet set) and the ones fishing on the seabed
		public int traps;
		public List<TrapSet> sets = new List<TrapSet>();
		// Joe's prices move with the day: { day, mul: { species: factor } }
		public int marketDay = 1;
		public Dictionary<string, double> marketMul = new Dictionary<string, double>();
		// Joe's order of the day (see Orders.cs): rolled on first look each day
		public Order order;
		int _nextId = 1;
		public event Action<GameState> listeners;

		public GameState( ISaveStore storage = null ) { this.storage = storage; }

		public GearStats stats => Gear.gearStats( upgrades );

		public double holdKg { get { double kg = 0; foreach ( var f in inventory ) kg += f.kg; return kg; } }

		public double holdValue { get { double v = 0; foreach ( var f in inventory ) v += priceOf( f ); return v; } }

		// ---- the market: what Joe is paying today
		// Prices are per species and per day, so holding a catch overnight is a real decision. Day 1 pays the standard rate (rollMarket leaves it neutral),
		// and the roll is a pure function of the day and the species, so it needs no random state in the save.

		public double mulFor( string species )
		{
			if ( marketDay != day ) rollMarket();
			return marketMul.TryGetValue( species, out var m ) ? m : 1;
		}

		// ---- Joe's order: one species, a size or bigger, paid a multiplier all day (from day 2)

		public Order todaysOrder
		{
			get
			{
				if ( order == null || order.day != day )
				{
					var o = Orders.orderFor( day, mayTrap );
					order = o != null ? new Order { day = o.day, species = o.species, minKg = o.minKg, filled = 0, bonus = 0 } : null;
				}

				return order;
			}
		}

		// ORDER_MULT when this fish fills today's order, else 1
		public double orderMulFor( InventoryFish f ) => Orders.orderMul( todaysOrder, f.species, f.kg );

		// what a landed fish is worth at today's price and today's order (its own `value` is the standard price)
		public double priceOf( InventoryFish f ) => JS.Round( f.value * mulFor( f.species ) * orderMulFor( f ) );

		public void rollMarket() => rollMarket( day );

		public void rollMarket( int d )
		{
			var mul = new Dictionary<string, double>();
			foreach ( var id in FishTable.FISH_IDS )
			{
				if ( d <= 1 ) { mul[ id ] = 1; continue; }
				double x = Math.Sin( d * 127.1 + ( hashStr( id ) % 977 ) ) * 43758.5453;
				double r = x - Math.Floor( x ); // 0..1, same for this day and species every time
				mul[ id ] = JS.Round( ( 0.75 + 0.6 * r ) * 20 ) / 20; // 0.75 .. 1.35 in 5% steps
			}

			marketDay = d;
			marketMul = mul;
		}

		// room in the cooler / hold for a fish of `kg`?
		public bool fits( double kg ) => holdKg + kg <= stats.holdKg + 1e-6;

		// store a caught fish; returns the entry, or null when the hold is full (it is logged either way).
		// A record beats an earlier catch of the species; the first one of a species is a new species.
		public InventoryFish addFish( string species, double kg, double timeOfDay = 12, CatchSpot where = null )
		{
			kg = JS.Round( kg * 100 ) / 100;
			double cm = JS.Round( FishTable.fishLengthCm( species, kg ) );
			var logEntry = entryFor( species );
			bool newSpecies = logEntry.count == 0;
			double prevBestKg = logEntry.bestKg, prevBestCm = logEntry.bestCm ?? ( prevBestKg > 0 ? JS.Round( FishTable.fishLengthCm( species, prevBestKg ) ) : 0 );
			bool record = ! newSpecies && kg > prevBestKg;
			logEntry.count ++;
			noteCatch( logEntry, kg, timeOfDay, where );
			if ( kg > prevBestKg )
			{
				logEntry.bestKg = kg;
				logEntry.bestCm = cm;
			}

			double value = FishTable.fishValue( species, kg );
			bool kept = fits( kg );
			lastCatch = new LastCatch { species = species, kg = kg, cm = cm, value = value, newSpecies = newSpecies, record = record, prevBestKg = prevBestKg, prevBestCm = prevBestCm, kept = kept };
			if ( ! kept )
			{
				save();
				emit();
				return null;
			}

			var f = new InventoryFish { id = _nextId ++, species = species, kg = kg, cm = cm, value = value, caughtAt = timeOfDay, record = record };
			inventory.Add( f );
			save();
			emit();
			return f;
		}

		// the species' log entry, made (with every field the fish guide reads) if it is the first
		public LogEntry entryFor( string species )
		{
			if ( ! log.TryGetValue( species, out var e ) ) log[ species ] = e = Codex.emptyEntry();
			return e;
		}

		// the guide's memory of one catch: the lightest, the first, when (day and hour), where and in what water
		public void noteCatch( LogEntry e, double kg, double timeOfDay, CatchSpot where )
		{
			e.minKg = e.minKg > 0 ? Math.Min( e.minKg, kg ) : kg;
			if ( e.first == null ) e.first = new FirstCatch { day = day, hour = timeOfDay };
			string p = Codex.periodOf( timeOfDay );
			e.periods[ p ] = ( e.periods.TryGetValue( p, out var n ) ? n : 0 ) + 1;
			if ( where != null && ! string.IsNullOrEmpty( where.hab ) ) e.habs[ where.hab ] = ( e.habs.TryGetValue( where.hab, out var h ) ? h : 0 ) + 1;
			var c = new CatchRecord { kg = kg, day = day, hour = JS.Round( timeOfDay * 100 ) / 100 };
			if ( where != null && IsFinite( where.x ) && IsFinite( where.z ) ) { c.x = JS.Round( where.x ); c.z = JS.Round( where.z ); }
			if ( where != null && ! string.IsNullOrEmpty( where.hab ) ) c.hab = where.hab;
			e.catches.Add( c );
			if ( e.catches.Count > Codex.CATCH_CAP ) e.catches.RemoveRange( 0, e.catches.Count - Codex.CATCH_CAP );
		}

		static bool IsFinite( double x ) => ! double.IsNaN( x ) && ! double.IsInfinity( x );

		// sell the given fish ids (all when null); returns the money made
		public SaleResult sell( ICollection<int> ids = null )
		{
			var keep = new List<InventoryFish>(); var sold = new List<InventoryFish>();
			foreach ( var f in inventory ) ( ids == null || ids.Contains( f.id ) ? sold : keep ).Add( f );
			double total = 0, bonus = 0; int filled = 0;
			foreach ( var f in sold )
			{
				double p = priceOf( f );
				total += p;
				if ( log.ContainsKey( f.species ) ) { var e = entryFor( f.species ); e.sold ++; e.earned += p; } // what the guide knows of its price
				if ( orderMulFor( f ) > 1 )
				{
					filled ++;
					bonus += p - JS.Round( f.value * mulFor( f.species ) ); // what the order added
				}
			}

			if ( filled > 0 )
			{
				order.filled += filled;
				order.bonus += bonus;
			}

			inventory = keep;
			money += total;
			save();
			emit();
			return new SaleResult { total = total, count = sold.Count, bonus = bonus, filled = filled };
		}

		public void release( int id )
		{
			inventory = inventory.Where( f => f.id != id ).ToList();
			save();
			emit();
		}

		// spend money (upgrade shop); false when it can't be afforded
		public bool spend( double amount )
		{
			if ( amount > money ) return false;
			money -= amount;
			save();
			emit();
			return true;
		}

		public bool ownsBoat( string id ) => boats.Contains( id );

		// buy a boat at the chandlery; returns its entry, or null (unknown, already yours, or not enough money)
		public BoatDef buyBoat( string id )
		{
			var b = Gear.Boat( id );
			if ( b == null || ownsBoat( id ) || b.cost > money ) return null;
			money -= b.cost;
			boats = Gear.BOAT_IDS.Where( k => k == id || ownsBoat( k ) ).ToList();
			save();
			emit();
			return b;
		}

		// buy the next level of an upgrade track; returns the new level entry or null
		public GearLevel buy( string key )
		{
			if ( ! Gear.IsTrack( key ) ) return null;
			var boat = Gear.Track( key ).boat;
			if ( boat != null && ! ownsBoat( boat ) ) return null; // the lobster boat's gear needs the lobster boat
			var next = Gear.nextLevel( upgrades, key );
			if ( next == null || next.cost > money ) return null;
			money -= next.cost;
			upgrades[ key ] = next.index;
			if ( key == "fuel" ) fuel = null; // a new tank comes full
			save();
			emit();
			return next;
		}

		public double fuelL => fuel == null ? stats.fuelL : Math.Min( fuel.Value, stats.fuelL );

		// burn litres (no save: that happens when the boat stops or at the next sale / purchase)
		public double burn( double litres )
		{
			fuel = Math.Max( 0, fuelL - litres );
			return fuel.Value;
		}

		public double refuelCost() => Math.Ceiling( ( stats.fuelL - fuelL ) * Gear.FUEL_PRICE );

		// fill up as far as the money goes; returns litres bought
		public double refuel()
		{
			double missing = stats.fuelL - fuelL;
			double litres = Math.Min( missing, Math.Floor( money / Gear.FUEL_PRICE ) );
			if ( litres <= 0 ) return 0;
			money -= Math.Ceiling( litres * Gear.FUEL_PRICE );
			fuel = fuelL + litres;
			if ( fuel >= stats.fuelL - 1e-3 ) fuel = null;
			save();
			emit();
			return litres;
		}

		// returns the unsubscribe
		public Action onChange( Action<GameState> fn ) { listeners += fn; return () => listeners -= fn; }

		// ---- the trap line: buy traps, set them, haul them
		// A trap is stock until it goes over the side; setting one spends it and a set comes back aboard when it is hauled, so a gear of traps is a fixed
		// number of pots moving between the two.

		public int trapsSet => sets.Count;

		public bool mayTrap => stats.trapLicence;

		// buy traps (n at a time); false when they are not affordable
		public bool buyTraps( int n = 1 )
		{
			double cost = Gear.TRAP_PRICE * n;
			if ( money < cost ) return false;
			money -= cost;
			traps = Math.Min( traps + n, Gear.TRAP_LIMIT );
			save();
			emit();
			return true;
		}

		// put a trap over the side at (x, z); null when there are none aboard
		public TrapSet setTrap( double x, double z, double timeOfDay )
		{
			if ( traps <= 0 ) return null;
			traps --;
			var s = new TrapSet { id = _nextId ++, x = x, z = z, day = day, clock = timeOfDay };
			sets.Add( s );
			save();
			emit();
			return s;
		}

		// bring a trap back aboard; returns the set, or null when it is not there
		public TrapSet haulTrap( int id )
		{
			int i = sets.FindIndex( s => s.id == id );
			if ( i < 0 ) return null;
			var s = sets[ i ];
			sets.RemoveAt( i );
			traps = Math.Min( traps + 1, Gear.TRAP_LIMIT );
			save();
			emit();
			return s;
		}

		// the set nearest to a point, or null when none is within `maxM`
		public TrapSet nearestSet( double x, double z, double maxM = 12 )
		{
			TrapSet best = null; double bestD = maxM;
			foreach ( var s in sets )
			{
				double d = JS.Hypot( s.x - x, s.z - z );
				if ( d <= bestD ) { bestD = d; best = s; }
			}

			return best;
		}

		// ---- the world clock (written from the app each frame, saved with everything else)

		public void setClock( double hours ) { if ( IsFinite( hours ) ) clock = hours; }

		public void setWeather( JObject w ) { if ( w != null ) weather = w; }

		// midnight: a new day. Phase 2 re-rolls the fish market here.
		public int advanceDay() { day = day + 1; return day; }

		public void emit() { listeners?.Invoke( this ); }

		// ---- the save (the JS toJSON / fromJSON, same schema)

		static JObject EntryJson( LogEntry e )
		{
			var o = new JObject { [ "count" ] = e.count, [ "bestKg" ] = e.bestKg };
			if ( e.bestCm != null ) o[ "bestCm" ] = e.bestCm;
			o[ "minKg" ] = e.minKg;
			o[ "first" ] = e.first == null ? JValue.CreateNull() : new JObject { [ "day" ] = e.first.day, [ "hour" ] = e.first.hour };
			o[ "catches" ] = new JArray( e.catches.Select( c =>
			{
				var j = new JObject { [ "kg" ] = c.kg, [ "day" ] = c.day, [ "hour" ] = c.hour };
				if ( c.x != null ) { j[ "x" ] = c.x; j[ "z" ] = c.z; }
				if ( c.hab != null ) j[ "hab" ] = c.hab;
				return j;
			} ) );
			o[ "habs" ] = JObject.FromObject( e.habs );
			o[ "periods" ] = JObject.FromObject( e.periods );
			o[ "sold" ] = e.sold;
			o[ "earned" ] = e.earned;
			return o;
		}

		public JObject toJSON()
		{
			var j = new JObject
			{
				[ "v" ] = 1, [ "money" ] = money,
				[ "inventory" ] = new JArray( inventory.Select( f => new JObject { [ "id" ] = f.id, [ "species" ] = f.species, [ "kg" ] = f.kg, [ "cm" ] = f.cm, [ "value" ] = f.value, [ "caughtAt" ] = f.caughtAt, [ "record" ] = f.record } ) ),
				[ "log" ] = new JObject( log.Select( kv => new JProperty( kv.Key, EntryJson( kv.Value ) ) ) ),
				[ "upgrades" ] = JObject.FromObject( upgrades ),
				[ "fuel" ] = fuel == null ? JValue.CreateNull() : new JValue( fuel.Value ),
				[ "nextId" ] = _nextId,
				[ "day" ] = day, [ "clock" ] = clock == null ? JValue.CreateNull() : new JValue( clock.Value ),
				[ "weather" ] = weather == null ? JValue.CreateNull() : weather,
				[ "traps" ] = traps,
				[ "sets" ] = new JArray( sets.Select( s => new JObject { [ "id" ] = s.id, [ "x" ] = s.x, [ "z" ] = s.z, [ "day" ] = s.day, [ "clock" ] = s.clock } ) ),
				[ "market" ] = new JObject { [ "day" ] = marketDay, [ "mul" ] = JObject.FromObject( marketMul ) },
				[ "order" ] = order == null ? JValue.CreateNull() : new JObject { [ "day" ] = order.day, [ "species" ] = order.species, [ "minKg" ] = order.minKg, [ "filled" ] = order.filled, [ "bonus" ] = order.bonus },
				[ "boats" ] = new JArray( boats ),
			};
			return j;
		}

		static bool Num( JToken t, out double v )
		{
			v = 0;
			if ( t == null || ( t.Type != JTokenType.Float && t.Type != JTokenType.Integer ) ) return false;
			v = ( double ) t;
			return IsFinite( v );
		}

		static double NumOr( JToken t, double d = 0 ) => Num( t, out var v ) && v >= 0 ? v : d;

		// a saved log entry with every field the guide reads, clamped to what makes sense (a hand-edited or older save)
		static LogEntry sanitizeEntry( JObject v )
		{
			var e = new LogEntry
			{
				count = ( int ) Math.Floor( NumOr( v[ "count" ] ) ), bestKg = NumOr( v[ "bestKg" ] ), minKg = NumOr( v[ "minKg" ] ),
				sold = ( int ) Math.Floor( NumOr( v[ "sold" ] ) ), earned = NumOr( v[ "earned" ] ),
			};
			if ( Num( v[ "bestCm" ], out var bc ) ) e.bestCm = bc;
			if ( v[ "first" ] is JObject fo && Num( fo[ "day" ], out var fd ) && Num( fo[ "hour" ], out var fh ) ) e.first = new FirstCatch { day = ( int ) fd, hour = fh };
			Dictionary<string, int> counts( JToken o ) => o is JObject jo
				? jo.Properties().Where( p => Num( p.Value, out var n ) && n > 0 ).ToDictionary( p => p.Name, p => ( int ) ( double ) p.Value ) : new Dictionary<string, int>();
			e.habs = counts( v[ "habs" ] );
			e.periods = counts( v[ "periods" ] );
			if ( v[ "catches" ] is JArray ca )
				foreach ( var c in ca.OfType<JObject>() )
				{
					if ( ! Num( c[ "kg" ], out var kg ) || ! Num( c[ "day" ], out var d ) || ! Num( c[ "hour" ], out var h ) ) continue;
					var r = new CatchRecord { kg = kg, day = ( int ) d, hour = h };
					if ( Num( c[ "x" ], out var x ) ) r.x = x;
					if ( Num( c[ "z" ], out var z ) ) r.z = z;
					if ( c[ "hab" ]?.Type == JTokenType.String ) r.hab = ( string ) c[ "hab" ];
					e.catches.Add( r );
				}

			if ( e.catches.Count > Codex.CATCH_CAP ) e.catches.RemoveRange( 0, e.catches.Count - Codex.CATCH_CAP );
			return e;
		}

		public bool fromJSON( JObject d )
		{
			if ( d == null || ! ( d[ "v" ] != null && Num( d[ "v" ], out var ver ) && ver == 1 ) ) return false;
			money = Num( d[ "money" ], out var m ) ? m : 0;
			inventory = new List<InventoryFish>();
			if ( d[ "inventory" ] is JArray inv )
				foreach ( var o in inv.OfType<JObject>() )
				{
					string sp = o[ "species" ]?.Type == JTokenType.String ? ( string ) o[ "species" ] : null;
					if ( ! FishTable.Has( sp ) || ! Num( o[ "kg" ], out var kg ) ) continue;
					var f = new InventoryFish { id = Num( o[ "id" ], out var id ) ? ( int ) id : 0, species = sp, kg = kg, value = Num( o[ "value" ], out var val ) ? val : 0,
						caughtAt = Num( o[ "caughtAt" ], out var ca ) ? ca : 0, record = o[ "record" ]?.Type == JTokenType.Boolean && ( bool ) o[ "record" ] };
					// saves from before lengths were recorded
					f.cm = Num( o[ "cm" ], out var cm ) ? cm : JS.Round( FishTable.fishLengthCm( sp, kg ) );
					inventory.Add( f );
				}

			log = new Dictionary<string, LogEntry>();
			if ( d[ "log" ] is JObject lg )
				foreach ( var p in lg.Properties() )
				{
					if ( ! FishTable.Has( p.Name ) || ! ( p.Value is JObject eo ) ) continue;
					var e = sanitizeEntry( eo );
					// entries from before lengths were recorded
					if ( e.bestKg > 0 && e.bestCm == null ) e.bestCm = JS.Round( FishTable.fishLengthCm( p.Name, e.bestKg ) );
					log[ p.Name ] = e;
				}

			upgrades = Gear.defaultUpgrades();
			if ( d[ "upgrades" ] is JObject up ) foreach ( var p in up.Properties() ) if ( Num( p.Value, out var lv ) ) upgrades[ p.Name ] = ( int ) lv;
			fuel = Num( d[ "fuel" ], out var fu ) ? fu : ( double? ) null;
			// saves from before the world clock existed simply start on day 1 at the default time
			day = Num( d[ "day" ], out var dy ) && dy >= 1 ? ( int ) Math.Floor( dy ) : 1;
			clock = Num( d[ "clock" ], out var ck ) ? ck : ( double? ) null;
			weather = d[ "weather" ] as JObject;
			// the trap line and the market (saves from before traps simply have none out)
			traps = Num( d[ "traps" ], out var tr ) && tr > 0 ? ( int ) Math.Floor( tr ) : 0;
			sets = new List<TrapSet>();
			if ( d[ "sets" ] is JArray sa )
				foreach ( var s in sa.OfType<JObject>() )
					if ( Num( s[ "x" ], out var sx ) && Num( s[ "z" ], out var sz ) && Num( s[ "day" ], out var sd ) && Num( s[ "clock" ], out var sc ) )
						sets.Add( new TrapSet { id = Num( s[ "id" ], out var sid ) ? ( int ) sid : 0, x = sx, z = sz, day = ( int ) sd, clock = sc } );
			if ( d[ "market" ] is JObject mk && mk[ "mul" ] is JObject mm && Num( mk[ "day" ], out var md ) )
			{
				marketDay = ( int ) md;
				marketMul = mm.Properties().Where( p => Num( p.Value, out _ ) ).ToDictionary( p => p.Name, p => ( double ) p.Value );
			}
			else { marketDay = day; marketMul = new Dictionary<string, double>(); }
			// today's order, if the save has one (a stale day is simply rolled afresh by the next look)
			order = null;
			if ( d[ "order" ] is JObject o2 && Num( o2[ "day" ], out var od ) && FishTable.Has( o2[ "species" ]?.Type == JTokenType.String ? ( string ) o2[ "species" ] : null ) && Num( o2[ "minKg" ], out var omk ) )
				order = new Order { day = ( int ) od, species = ( string ) o2[ "species" ], minKg = omk, filled = Num( o2[ "filled" ], out var of ) ? ( int ) of : 0, bonus = Num( o2[ "bonus" ], out var ob ) ? ob : 0 };
			// the boats you own: a save from before boats were sold owns them all (nothing is taken away); an unknown id is dropped
			var have = d[ "boats" ] is JArray ba
				? Gear.BOAT_IDS.Where( k => ba.Any( t => t.Type == JTokenType.String && ( string ) t == k ) ).ToList()
				: Gear.BOAT_IDS.ToList();
			boats = have.Count > 0 ? have : new List<string>( Gear.START_BOATS );
			int next = Math.Max( Num( d[ "nextId" ], out var ni ) ? ( int ) ni : 0, 1 );
			foreach ( var f in inventory ) next = Math.Max( next, f.id + 1 );
			foreach ( var s in sets ) next = Math.Max( next, s.id + 1 );
			_nextId = next;
			return true;
		}

		public void save()
		{
			if ( storage == null ) return;
			try { storage.SetItem( SAVE_KEY, toJSON().ToString( Newtonsoft.Json.Formatting.None ) ); }
			catch ( Exception ) { /* storage full or blocked: keep playing */ }
		}

		public bool load()
		{
			if ( storage == null ) return false;
			try
			{
				string raw = storage.GetItem( SAVE_KEY );
				return ! string.IsNullOrEmpty( raw ) && fromJSON( JObject.Parse( raw ) );
			}
			catch ( Exception ) { return false; }
		}

		public void reset()
		{
			money = 0;
			inventory = new List<InventoryFish>();
			log = new Dictionary<string, LogEntry>();
			upgrades = Gear.defaultUpgrades();
			boats = new List<string>( Gear.START_BOATS );
			fuel = null;
			day = 1;
			clock = null;
			weather = null;
			traps = 0;
			sets = new List<TrapSet>();
			marketDay = 1; marketMul = new Dictionary<string, double>();
			order = null;
			save();
			emit();
		}

		// FNV-1a of a species id: the market roll needs a stable number per name, not a random seed
		static uint hashStr( string s )
		{
			uint h = 2166136261;
			unchecked { for ( int i = 0; i < s.Length; i ++ ) { h ^= s[ i ]; h *= 16777619; } }
			return h;
		}
	}
}
