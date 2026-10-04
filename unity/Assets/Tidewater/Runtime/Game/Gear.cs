using System;
using System.Collections.Generic;
using System.Linq;

// Port of src/game/Gear.js: gear and upgrade data. Everything the game reads goes through gearStats( state.upgrades ), so buying a level is just
// upgrades[ key ]++ and the stats follow.
//
// Each track: levels[ 0 ] is what you start with; cost is the price of that level (0 for the first).
namespace Tidewater.Game
{
	// one level of a track: the JS object { cost, label, <stat>: value }, with the one stat it sets
	public sealed class GearLevel
	{
		public int index;
		public double cost;
		public string label;
		public string stat;   // lineKg | reelSpeed | castM | holdKg | fuelL | speedMul | finder | deckLights | trapLicence
		public double value;  // the number (1 / 0 for the flags)
	}

	public sealed class UpgradeTrack
	{
		public string key, name;
		public GearLevel[] levels;
	}

	// merged stats of the current levels (the JS object s)
	public sealed class GearStats
	{
		public double lineKg, reelSpeed, castM, holdKg, fuelL, speedMul;
		public bool finder, deckLights, trapLicence;
	}

	public static class Gear
	{
		static GearLevel L( double cost, string label, string stat, double value ) => new GearLevel { cost = cost, label = label, stat = stat, value = value };

		public static readonly UpgradeTrack[] UPGRADES_LIST =
		{
			// rod and reel
			new UpgradeTrack { key = "line", name = "Fishing line", levels = new[] {
				L( 0, "8 lb mono", "lineKg", 7 ),
				L( 60, "15 lb mono", "lineKg", 13 ),
				L( 180, "30 lb braid", "lineKg", 26 ),
				L( 450, "60 lb braid", "lineKg", 50 ),
			} },
			new UpgradeTrack { key = "reel", name = "Reel", levels = new[] {
				L( 0, "Old spinning reel", "reelSpeed", 1.1 ),
				L( 90, "Smooth spinning reel", "reelSpeed", 1.6 ),
				L( 320, "Conventional reel", "reelSpeed", 2.2 ),
			} },
			new UpgradeTrack { key = "rod", name = "Rod", levels = new[] {
				L( 0, "Hand-me-down rod", "castM", 22 ),
				L( 75, "7 ft graphite rod", "castM", 32 ),
				L( 260, "9 ft surf rod", "castM", 45 ),
			} },
			// boat
			new UpgradeTrack { key = "hold", name = "Fish hold", levels = new[] {
				L( 0, "Cooler", "holdKg", 30 ),
				L( 120, "Ice chest", "holdKg", 70 ),
				L( 400, "Insulated fish hold", "holdKg", 160 ),
			} },
			new UpgradeTrack { key = "fuel", name = "Fuel tank", levels = new[] {
				L( 0, "40 L tank", "fuelL", 40 ),
				L( 150, "80 L tank", "fuelL", 80 ),
				L( 380, "150 L tank", "fuelL", 150 ),
			} },
			new UpgradeTrack { key = "engine", name = "Engine", levels = new[] {
				L( 0, "Tired diesel", "speedMul", 1 ),
				L( 300, "Rebuilt diesel", "speedMul", 1.15 ),
				L( 700, "Turbo diesel", "speedMul", 1.3 ),
			} },
			new UpgradeTrack { key = "fishFinder", name = "Fish finder", levels = new[] {
				L( 0, "None", "finder", 0 ),
				L( 250, "Fish finder (depth and fish on the HUD)", "finder", 1 ),
			} },
			new UpgradeTrack { key = "lights", name = "Boat lights", levels = new[] {
				L( 0, "Nav lights only", "deckLights", 0 ),
				L( 140, "Deck floodlights for night fishing", "deckLights", 1 ),
			} },
			// the trap line: the licence is the gate, the traps themselves are a consumable stock
			new UpgradeTrack { key = "trapLicence", name = "Trap licence", levels = new[] {
				L( 0, "None", "trapLicence", 0 ),
				L( 450, "Commercial trap licence", "trapLicence", 1 ),
			} },
		};

		static Gear() { foreach ( var t in UPGRADES_LIST ) for ( int i = 0; i < t.levels.Length; i ++ ) t.levels[ i ].index = i; }

		public static UpgradeTrack Track( string key ) => UPGRADES_LIST.FirstOrDefault( t => t.key == key );
		public static bool IsTrack( string key ) => Track( key ) != null;

		public const double FUEL_PRICE = 1.5; // $ per litre of diesel at the chandlery
		public const double TRAP_PRICE = 90;  // $ per lobster trap (wooden, wire, ready to fish)
		public const int TRAP_LIMIT = 6;      // most traps one licence will let you fish at a time

		// litres per second at the helm: idle plus a lot more at full rpm (40 L lasts ~25 min flat out)
		public static double fuelBurn( double rpm ) => 0.0025 + 0.024 * rpm * rpm;

		// next level of a track, or null when maxed
		public static GearLevel nextLevel( Dictionary<string, int> upgrades, string key )
		{
			var lv = Track( key ).levels;
			int i = ( upgrades.TryGetValue( key, out var v ) ? v : 0 ) + 1;
			return i < lv.Length ? lv[ i ] : null;
		}

		public static Dictionary<string, int> defaultUpgrades() => UPGRADES_LIST.ToDictionary( t => t.key, t => 0 );

		public static GearStats gearStats( Dictionary<string, int> upgrades )
		{
			var s = new GearStats();
			foreach ( var t in UPGRADES_LIST )
			{
				int i = Math.Max( 0, Math.Min( t.levels.Length - 1, upgrades.TryGetValue( t.key, out var v ) ? v : 0 ) );
				var l = t.levels[ i ];
				switch ( l.stat )
				{
					case "lineKg": s.lineKg = l.value; break;
					case "reelSpeed": s.reelSpeed = l.value; break;
					case "castM": s.castM = l.value; break;
					case "holdKg": s.holdKg = l.value; break;
					case "fuelL": s.fuelL = l.value; break;
					case "speedMul": s.speedMul = l.value; break;
					case "finder": s.finder = l.value != 0; break;
					case "deckLights": s.deckLights = l.value != 0; break;
					case "trapLicence": s.trapLicence = l.value != 0; break;
				}
			}

			return s;
		}
	}
}
