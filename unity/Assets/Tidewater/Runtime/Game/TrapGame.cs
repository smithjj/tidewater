using System;
using System.Collections.Generic;
using System.Linq;
using Tidewater.Core;
using Tidewater.Player;
using Tidewater.World;
using UnityEngine;
using Engine3 = Tidewater.Engine.Vector3;

// The trap line of src/game/Game.js (updateTraps, trapPrompt, setTrap, haulTrap) on top of Traps / TrapsView:
//   on deck of the working boat   E sets a pot over the stern, or hauls the one alongside
//   at the helm                   the cast button (LMB / RT) does the same, at a crawl (E leaves the helm)
// A set pot soaks on the world clock; hauled, it comes up on the hauler and lands on the deck stack, and what crawled into it goes in the cooler with a catch card (the pots'
// catch is logged at the pot, in its water, for the fish guide's map). The working boat is the lobster boat: it has the hauler and the deck stack.
// Not ported: the pad rumble.
namespace Tidewater.Game
{
	public sealed class TrapGame
	{
		readonly GameHost game;
		readonly PlayerHost host;
		readonly BoatController lobster;
		readonly TrapBoat boat;
		readonly Transform parent;
		readonly System.Random random = new System.Random();
		readonly Func<double> rng;
		readonly Engine3 _tmp = new Engine3();
		IGameAudio audio;
		IGameAudio Audio => audio ?? ( audio = new Tidewater.Audio.GameAudioProxy( host ) );
		string lastMode;
		bool modeChanged;

		public Traps traps { get; private set; }
		public TrapsView view { get; private set; }
		public ITrapBoat workingBoat => boat;

		public TrapGame( GameHost game, PlayerHost host, BoatController lobster, Transform parent )
		{
			this.game = game; this.host = host; this.lobster = lobster; this.parent = parent;
			rng = () => random.NextDouble();
			boat = new TrapBoat( lobster );
			var ocean = Tidewater.Ocean.OceanRenderer.instance;
			traps = new Traps( game.state, host.terrainData.HeightAt, ocean != null ? ocean.query : null, lobster.model.deckY );
			traps.onSplash = () => Audio?.splash( 0.5 );
			view = TrapsView.Create( parent, DeckParent );
		}

		// the boat's own transform carries the stack
		Transform DeckParent => lobster.view is Component c ? c.transform : null;

		public void Dispose()
		{
			traps.Dispose();
			view.Dispose();
		}

		double hour => game.clock.hour;

		// ---- the trap line
		// Setting and hauling happen from the working boat (it is the one with the hauler): stand anywhere aboard, E. Traps soak on the world clock, so the day running is what fills them.

		public bool aboardWorkingBoat
		{
			get { var p = host.player; return p.boat == lobster && ( p.mode == "boat" || p.mode == "deck" ); }
		}

		// the prompt (and the action it implies) for the trap line, or null
		public TrapPrompt Prompt( Tidewater.Player.Player p )
		{
			if ( ! aboardWorkingBoat ) return null;
			if ( game.fishing.haulCardOpen || game.fishing.catchOpen || traps.busy ) return null;
			var s = game.state; var b = lobster;
			var near = s.nearestSet( b.position.x, b.position.z, 12 );
			bool canSet = s.mayTrap && s.traps > 0 && s.sets.Count < Gear.TRAP_LIMIT;
			string crawl = b.speed > Traps.TRAP_MAX_SPEED ? " · slow down" : "";
			if ( near != null )
			{
				double soak = Traps.soakHours( near, hour, s.day );
				// A pot that has not soaked yet is not worth hauling, and E should keep laying the line instead (a gear of pots goes in a line, so the last pot is always in reach). It only
				// comes back up if there is nothing left to set — no pots aboard, or the water full.
				if ( soak >= Traps.SOAK_MIN || ! canSet )
				{
					string when = soak < Traps.SOAK_MIN ? "just set" : $"soaked {( soak < 10 ? soak.ToString( "F1", System.Globalization.CultureInfo.InvariantCulture ) : Math.Round( soak ).ToString( System.Globalization.CultureInfo.InvariantCulture ) )} h";
					return new TrapPrompt { action = "interact", text = $"Haul trap · {when}{crawl}", act = "haul" };
				}
			}

			if ( ! canSet ) return null;
			return new TrapPrompt { action = "interact", text = $"Set a trap · {s.traps} aboard{crawl}", act = "set" };
		}

		// per frame (GameHost.Tick, after the vendors): the pots ride the sea, the animations step, and the buttons work the line
		public void Update( double dt, GameInput inp, Tidewater.Player.Player p )
		{
			// the player has already been updated: a mode that differs from last frame's changed in this frame's input (E brought the player aboard, or off the helm), and that
			// press is not also a pot
			modeChanged = lastMode != null && lastMode != p.mode;
			lastMode = p.mode;

			// the Editor destroys the scene objects it made when Play starts; build them again
			if ( ! view.Alive ) { view.Dispose(); view = TrapsView.Create( parent, DeckParent ); }
			traps.update( dt );
			view.Sync( traps );
			UpdateTraps( inp, p );
		}

		void UpdateTraps( GameInput inp, Tidewater.Player.Player p )
		{
			if ( game.fishing.cardDismissed || game.fishing.haulCardOpen || game.fishing.catchOpen ) return;
			// the mouse is captured (or a pad is in hand): the click that grabs the pointer must not also set a pot
			bool live = ( inp.locked || inp.device == Device.pad ) && ! inp.menuMode;
			if ( ! Traps.trapTriggered( p.mode, inp.actHit( "interact" ), inp.actHit( "rodUse" ), live, modeChanged ) ) return;
			var pr = Prompt( p );
			if ( pr == null ) return;
			if ( pr.act == "haul" ) HaulTrap();
			else SetTrap();
		}

		// at the helm the player's own prompt is "leave helm": the pots ride on the cast button there
		public void ApplyPrompt( Tidewater.Player.Player p, GameInput inp )
		{
			var tp = Prompt( p );
			if ( tp == null ) return;
			if ( p.prompt == null ) p.prompt = new Prompt { action = tp.action, text = tp.text };
			if ( p.mode == "boat" ) p.prompt = new Prompt { action = "rodUse", text = $"{tp.text}   ·   {inp.label( "interact" )}  leave helm" };
		}

		// put a pot over the side at (x, z) — the boat's position when they are not given. The caller decides whether the player is in a position to do this (see UpdateTraps), so the
		// console can drive it too.
		public TrapSet SetTrap( double? px = null, double? pz = null )
		{
			var s = game.state; var b = lobster;
			if ( b.speed > Traps.TRAP_MAX_SPEED )
			{
				game.Toast( "Slow down to set a pot", 2.2f );
				return null;
			}

			// a pot goes over the stern, not under the keel: just astern of the transom
			bool fromBoat = px == null; // (a console caller names the place, and the boat does not carry it there)
			double x, z;
			if ( fromBoat )
			{
				b.toWorld( _tmp.set( 0, 0, boat.sternZ - Traps.SET_ASTERN ), _tmp );
				x = _tmp.x; z = _tmp.z;
			}
			else { x = px.Value; z = pz.Value; }

			double depth = Math.Max( 0, - host.terrainData.HeightAt( x, z ) );
			if ( depth < 2 )
			{
				game.Toast( "Too shallow here — the pot would show at low water", 3f );
				return null;
			}

			if ( depth > 45 )
			{
				game.Toast( "Too deep — the warp would not reach the bottom", 3f );
				return null;
			}

			// a gear of pots goes in a line, not in a heap
			foreach ( var o in s.sets )
				if ( Tidewater.Engine.JS.Hypot( o.x - x, o.z - z ) < 8 )
				{
					game.Toast( "There is already a pot here — move along a bit", 2.6f );
					return null;
				}

			int before = s.traps; // pots aboard: the one that goes over is the top of the stack
			var set = s.setTrap( x, z, hour );
			if ( set == null )
			{
				game.Toast( "No pots aboard — Marta sells traps", 3f );
				return null;
			}

			game.Toast( $"Pot set in {depth.ToString( "F0", System.Globalization.CultureInfo.InvariantCulture )} m · give it a few hours", 3.2f );
			// the pot lifts off the stack and drops over the stern; the splash is heard when it lands
			if ( ! ( fromBoat && traps.setVisual( boat, Math.Min( before, traps.stackCount ) - 1 ) ) ) Audio?.splash( 0.5 );
			return set;
		}

		// haul a pot: the given one, or the nearest to the boat (or to you, when you are not aboard). Returns what came up, or null when there is nothing in reach.
		public List<Hauled> HaulTrap( TrapSet set = null )
		{
			var s = game.state; var b = lobster; var p = host.player;
			if ( set == null && aboardWorkingBoat )
			{
				// one pot at a time, and only at a crawl (a caller that names the set, like the console, is not held to it)
				if ( traps.busy ) return null;
				if ( b.speed > Traps.TRAP_MAX_SPEED )
				{
					game.Toast( "Slow down to haul a pot", 2.2f );
					return null;
				}
			}

			if ( set == null )
			{
				var r = aboardWorkingBoat ? b.position : p.position;
				set = s.nearestSet( r.x, r.z, 12 );
			}

			if ( set == null )
			{
				game.Toast( "No pot in reach", 2.2f );
				return null;
			}

			double depth = Math.Max( 0, - host.terrainData.HeightAt( set.x, set.z ) );
			double soak = Traps.soakHours( set, hour, s.day );
			var habitat = FishingGame.HabitatAtPoint( set.x, set.z, depth );
			var animals = Traps.haulYield( soak, depth, habitat, hour, rng );
			var where = new CatchSpot { x = set.x, z = set.z, hab = Codex.dominantHabitat( habitat ) }; // for the fish guide's map
			if ( s.haulTrap( set.id ) == null ) return null;
			traps.haulVisual( boat );
			Audio?.fishFlop();

			if ( animals.Count == 0 )
			{
				game.Toast( soak < Traps.SOAK_MIN
					? "Nothing yet — give it a few hours"
					: "The pot came up empty · try deeper ground or the reef edge", 3.6f );
				return animals;
			}

			// smallest first, so the catch card ends up showing the best of the haul
			animals = animals.OrderBy( a => a.kg ).ToList();
			var tally = new List<KeyValuePair<string, double[]>>();
			int kept = 0;
			foreach ( var a in animals )
			{
				var f = s.addFish( a.species, a.kg, hour, where );
				if ( f != null ) kept ++;
				int at = tally.FindIndex( t => t.Key == a.species );
				if ( at < 0 ) { tally.Add( new KeyValuePair<string, double[]>( a.species, new[] { 0.0, 0.0 } ) ); at = tally.Count - 1; }
				tally[ at ].Value[ 0 ] ++; tally[ at ].Value[ 1 ] += a.kg;
			}

			string list = string.Join( ", ", tally.Select( t => $"{t.Value[ 0 ]} × {FishTable.Get( t.Key ).name} ({t.Value[ 1 ].ToString( "F1", System.Globalization.CultureInfo.InvariantCulture )} kg)" ) );
			game.Toast( kept < animals.Count ? $"Pot hauled: {list} · {animals.Count - kept} did not fit" : $"Pot hauled: {list}", 5f );
			game.fishing.ShowHaulCard( s.lastCatch );
			Audio?.coin();
			return animals;
		}
	}

	// what the trap line would do now (Game.trapPrompt)
	public sealed class TrapPrompt { public string action, text, act; }
}
