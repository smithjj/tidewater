using System;
using System.Collections.Generic;
using System.Linq;

// Port of src/game/FishTable.js: the catchable fish, game data for the species the world already swims (World/Fish/FishSpecies.cs).
//
//   name      display name
//   sci       scientific name
//   lw        W (g) = a * L (cm, total length) ^ b (FishBase-style values)
//   model     key in the species table (the swimming model / skin)
//   habitat   weights per water type (see Bites.habitatAt): shallows (sand, < 3 m), reef (over the reef), pier (around the piles),
//             bay (open water 3-20 m), deep (offshore, > 20 m); in the order the JS lists them (the guide's ranking is stable on it)
//   kg        [ min, max ] weight; sizes follow a skewed distribution (most fish are small)
//   price     $ per kg at the fish stand
//   fight     0..1 how hard it pulls (surge strength and frequency in the catch mini-game)
//   stamina   seconds of good pressure it takes to tire a typical one
//   time      activity by time of day: 'day', 'dawnDusk', 'night' or 'any'
//   rarity    0..1, scales how often it bites relative to the others in the same water
namespace Tidewater.Game
{
	public readonly struct Hab
	{
		public readonly string key; public readonly double w;
		public Hab( string key, double w ) { this.key = key; this.w = w; }
	}

	public sealed class Fish
	{
		public readonly string id, name, sci, model, kind, time;
		public readonly double lwA, lwB, kgMin, kgMax, price, fight, stamina, rarity;
		public readonly Hab[] habitat;

		public Fish( string id, string name, string sci, double lwA, double lwB, string model, string kind, Hab[] habitat, double kgMin, double kgMax,
			double price, double fight, double stamina, string time, double rarity )
		{
			this.id = id; this.name = name; this.sci = sci; this.lwA = lwA; this.lwB = lwB; this.model = model; this.kind = kind; this.habitat = habitat;
			this.kgMin = kgMin; this.kgMax = kgMax; this.price = price; this.fight = fight; this.stamina = stamina; this.time = time; this.rarity = rarity;
		}

		public double Habitat( string key ) { foreach ( var h in habitat ) if ( h.key == key ) return h.w; return 0; }
	}

	public static class FishTable
	{
		// Trap catch (lobster): never taken on a line, so its habitat is empty and pickSpecies can never roll it. `kind` names the model in
		// FishProps (the 'whole' fish build is the default).
		public static readonly Fish[] ALL =
		{
			new Fish( "silverside", "Hardhead silverside", "Atherinomorus stipes", 0.0074, 3.1, "silverside", null, new Hab[] { new Hab( "shallows", 1 ), new Hab( "pier", 0.6 ), new Hab( "bay", 0.2 ) }, 0.02, 0.08, 3, 0.05, 1.5, "any", 0.2 ),
			new Fish( "mullet", "Striped mullet", "Mugil cephalus", 0.0112, 2.98, "mullet", null, new Hab[] { new Hab( "shallows", 1 ), new Hab( "pier", 0.4 ) }, 0.4, 2.2, 5, 0.3, 5, "day", 0.8 ),
			new Fish( "needlefish", "Houndfish", "Tylosurus crocodilus", 0.0012, 3.1, "needlefish", null, new Hab[] { new Hab( "shallows", 0.7 ), new Hab( "bay", 0.5 ), new Hab( "pier", 0.3 ) }, 0.5, 2.5, 4, 0.45, 5, "day", 0.5 ),
			new Fish( "sergeant", "Sergeant major", "Abudefduf saxatilis", 0.0234, 3, "sergeant", null, new Hab[] { new Hab( "pier", 1 ), new Hab( "reef", 0.8 ) }, 0.1, 0.35, 6, 0.15, 2.5, "day", 1 ),
			new Fish( "grunt", "Bluestriped grunt", "Haemulon sciurus", 0.0145, 3.06, "grunt", null, new Hab[] { new Hab( "pier", 1 ), new Hab( "reef", 0.8 ), new Hab( "bay", 0.2 ) }, 0.3, 1.2, 7, 0.25, 4, "any", 1 ),
			new Fish( "yellowtail", "Yellowtail snapper", "Ocyurus chrysurus", 0.0137, 2.98, "yellowtail", null, new Hab[] { new Hab( "reef", 1 ), new Hab( "pier", 0.4 ), new Hab( "bay", 0.4 ) }, 0.4, 1.6, 12, 0.4, 5, "dawnDusk", 0.9 ),
			new Fish( "chromis", "Blue chromis", "Chromis cyanea", 0.0209, 3, "chromis", null, new Hab[] { new Hab( "reef", 1 ) }, 0.03, 0.1, 4, 0.05, 1.5, "day", 0.4 ),
			new Fish( "tang", "Blue tang", "Acanthurus coeruleus", 0.0282, 2.95, "tang", null, new Hab[] { new Hab( "reef", 1 ) }, 0.2, 0.6, 8, 0.2, 3, "day", 0.6 ),
			new Fish( "wrasse", "Hogfish", "Lachnolaimus maximus", 0.0151, 3.07, "wrasse", null, new Hab[] { new Hab( "reef", 0.8 ), new Hab( "bay", 0.3 ) }, 0.5, 4, 16, 0.35, 6, "day", 0.35 ),
			new Fish( "parrot", "Stoplight parrotfish", "Sparisoma viride", 0.0151, 3.06, "parrot", null, new Hab[] { new Hab( "reef", 1 ) }, 0.8, 4, 9, 0.35, 6, "day", 0.5 ),
			new Fish( "angel", "Queen angelfish", "Holacanthus ciliaris", 0.0302, 2.94, "angel", null, new Hab[] { new Hab( "reef", 1 ) }, 0.4, 1.6, 14, 0.25, 4, "day", 0.3 ),
			new Fish( "jack", "Crevalle jack", "Caranx hippos", 0.02, 2.93, "jack", null, new Hab[] { new Hab( "shallows", 0.5 ), new Hab( "pier", 0.7 ), new Hab( "bay", 0.8 ) }, 1, 9, 6, 0.75, 12, "dawnDusk", 0.5 ),
			new Fish( "barracuda", "Great barracuda", "Sphyraena barracuda", 0.0051, 3.08, "barracuda", null, new Hab[] { new Hab( "reef", 0.5 ), new Hab( "bay", 0.8 ), new Hab( "deep", 0.5 ) }, 2, 16, 5, 0.7, 11, "any", 0.45 ),
			new Fish( "grouper", "Nassau grouper", "Epinephelus striatus", 0.0107, 3.07, "grouper", null, new Hab[] { new Hab( "reef", 0.6 ), new Hab( "deep", 0.6 ) }, 3, 14, 15, 0.6, 12, "any", 0.3 ),
			new Fish( "redSnapper", "Red snapper", "Lutjanus campechanus", 0.0137, 2.98, "redSnapper", null, new Hab[] { new Hab( "deep", 1 ), new Hab( "bay", 0.2 ) }, 1.5, 9, 18, 0.5, 9, "any", 0.7 ),
			new Fish( "tuna", "Blackfin tuna", "Thunnus atlanticus", 0.0145, 3, "tuna", null, new Hab[] { new Hab( "deep", 1 ) }, 3, 14, 16, 0.85, 16, "dawnDusk", 0.5 ),
			new Fish( "mahi", "Mahi-mahi", "Coryphaena hippurus", 0.0079, 3, "mahi", null, new Hab[] { new Hab( "deep", 0.8 ) }, 4, 18, 14, 0.8, 15, "day", 0.4 ),
			new Fish( "tarpon", "Tarpon", "Megalops atlanticus", 0.0077, 3.02, "tarpon", null, new Hab[] { new Hab( "pier", 0.35 ), new Hab( "bay", 0.5 ), new Hab( "shallows", 0.15 ) }, 10, 45, 4, 1, 24, "night", 0.2 ),
			new Fish( "lobster", "Caribbean spiny lobster", "Panulirus argus", 0.02, 2.9, "lobster", "lobster", new Hab[ 0 ], 0.4, 2.4, 30, 0.5, 6, "any", 0 ),
		};

		public static readonly string[] FISH_IDS = ALL.Select( f => f.id ).ToArray();
		static readonly Dictionary<string, Fish> byId = ALL.ToDictionary( f => f.id );

		public static Fish Get( string id ) => id != null && byId.TryGetValue( id, out var f ) ? f : null;
		public static bool Has( string id ) => id != null && byId.ContainsKey( id );

		// $ value of a fish; trophy-sized ones fetch a bit more per kg
		public static double fishValue( string id, double kg )
		{
			var f = byId[ id ];
			double t = ( kg - f.kgMin ) / Math.Max( f.kgMax - f.kgMin, 1e-6 );
			return Math.Max( 1, Tidewater.Engine.JS.Round( f.price * kg * ( 1 + 0.25 * Math.Max( 0, t - 0.7 ) / 0.3 ) ) );
		}

		// total length (cm) of a fish of `kg`, from its species' length-weight relation
		public static double fishLengthCm( string id, double kg )
		{
			var f = byId[ id ];
			return Math.Pow( Math.Max( kg, 0.001 ) * 1000 / f.lwA, 1 / f.lwB );
		}
	}
}
