using System;
using Tidewater.Core;
using Tidewater.Ocean;
using Tidewater.Player;
using Tidewater.World;
using Tidewater.World.Fish;
using UnityEngine;
using Engine3 = Tidewater.Engine.Vector3;

// The fishing half of src/game/Game.js on top of the ported world:
//   R          take out / put away the rod (on foot, on the pier, on the boat's deck, or seated at the mini boat's helm in first person)
//   hold LMB   wind up, release to cast (hold longer = farther)
//   LMB        strike when a fish takes the bobber ("!"); then hold LMB to reel, let go to ease off
//   RMB        reel an empty line back in
// It owns the rod (FishingRod.cs, with its view), the bite and the fight (Bites.cs, CatchMinigame.cs) and the landed fish. GameHost ticks it where
// Game.update runs the same code, between the cooler key and the boat's fuel.
//
// The bite goes wait -> nibble -> take: after the cast lands (onBobberLanded) a delay rolls from the habitat under the bobber, the hour and any
// school the bobber is over; then the species and weight are rolled, the bobber dips (nibbles), and at "take" the strike window is open for
// 2.4 - fight * 0.5 s. A strike starts the fight (the line-tension minigame); caught, the fish swings in on the line and the catch card comes up.
//
// The HUD (the fight meter, the cast power bar, the strike cue, the aiming dot) and the catch card are IMGUI until the HTML UI (GameHUD.js) is
// ported. The card is CatchCard (the full-screen layout, the splash burst) around FishPortrait (the studio portrait of the fish); the landed fish hangs
// on the line only until the card is up. Not ported: the pad rumble (every IGameAudio hook is optional).
namespace Tidewater.Game
{
	public sealed class Bite
	{
		public string phase; // "wait" | "nibble" | "take"
		public double t, pulse;
		public int nibbles;
		public string species;
		public double kg;
	}

	// the fish swinging in on the line, and its card
	public sealed class Landing
	{
		public string species; public double kg;
		public LastCatch card; // GameState.lastCatch
		public bool shown; public double cardT;
	}

	public sealed class FishingGame
	{
		// how long the catch card stays up unless dismissed (s)
		const double CATCH_CARD_S = 9;

		readonly GameHost game;
		readonly PlayerHost host;
		readonly System.Random random = new System.Random();
		readonly Func<double> rng;
		readonly Engine3 _tmp = new Engine3();

		public readonly FishingRod rod;
		public FishingRodView view { get; private set; }
		public CatchDisplay display { get; private set; }
		public CatchCard card { get; private set; }
		readonly Transform parent;
		public CatchMinigame fight;           // while a fish is on
		public Bite bite;
		public Landing landing;               // while the caught fish swings in view
		public bool cardDismissed;            // this frame's E / click belonged to the card
		public bool catchOpen { get; private set; }
		// the card of a trap haul: not part of the landing flow, so it times out on its own (HAUL_CARD_S), or E / click / Esc takes it away early (Game._haulCard)
		const double HAUL_CARD_S = 7;
		LastCatch haulInfo; double haulT;
		public bool haulCardOpen => haulInfo != null;
		bool lastCan;

		// the sound hooks (forwarded to the current SoundScape, if any)
		IGameAudio Audio => audio ?? ( audio = new Tidewater.Audio.GameAudioProxy( host ) );
		IGameAudio audio;

		public FishingGame( GameHost game, PlayerHost host, Transform parent )
		{
			this.game = game; this.host = host; this.parent = parent;
			rng = () => random.NextDouble();
			var ocean = OceanRenderer.instance;
			rod = new FishingRod( host.simCamera, ocean.query, host.terrainData.HeightAt, Audio );
			rod.onLand = where => OnBobberLanded( where );
			view = FishingRodView.Create( parent );
			display = new CatchDisplay( parent );
			card = new CatchCard( parent );
			ApplyGear();
		}

		public void Dispose()
		{
			view.Dispose();
			display.Dispose();
			card.Dispose();
		}

		// Game.applyGear: the rod's cast distance and reel speed from the gear
		public void ApplyGear()
		{
			var g = game.state.stats;
			rod.setGear( g.castM, g.reelSpeed );
		}

		// the player can fish on foot and on the boat's deck, and seated at the deckless mini boat's helm in the first-person view (the rod hangs in front of the eye);
		// not swimming, at another helm or in the free camera
		bool CanFish
		{
			get
			{
				var p = host.player;
				bool seated = p.mode == "boat" && p.boat.boatModel.deck == null && p.cam.firstPerson;
				return ! host.freeCam && ( p.mode == "walk" || p.mode == "deck" || seated );
			}
		}

		double hour => game.clock.hour;

		// ---- per frame (after the player update)
		public void Update( double dt )
		{
			var p = host.player; var inp = host.input;
			// the Editor destroys the scene objects it made when Play starts; build them again
			if ( ! view.Alive ) { view.Dispose(); view = FishingRodView.Create( parent ); }
			if ( ! display.Alive ) { display.Dispose(); display = new CatchDisplay( parent ); }
			if ( ! card.Alive ) { card.Dispose(); card = new CatchCard( parent ); if ( catchOpen && landing != null && landing.card != null ) ShowCard( landing.card ); else if ( haulInfo != null ) ShowCard( haulInfo ); }
			cardDismissed = false;
			bool can = CanFish;
			lastCan = can;
			hintDt = dt;
			if ( rod.equipped ) rodTaught = true;

			if ( inp.actHit( "rod" ) && can && fight == null )
			{
				rod.equip( ! rod.equipped );
				if ( ! rod.equipped ) CancelLine();
				game.Toast( rod.equipped ? $"Rod out · hold {inp.label( "rodUse" )} to cast" : "Rod away", 1.6f );
			}

			if ( ! can && rod.equipped )
			{
				// swimming, driving, free camera: the line comes in and the rod goes away
				CancelLine( true );
				rod.equip( false );
			}

			// the cast / reel button and its edges (the pad trigger and the left mouse both land here)
			bool lmb = inp.act( "rodUse" );
			bool lDown = inp.actHit( "rodUse" ), lUp = inp.actReleased( "rodUse" ), rDown = inp.actHit( "rodIn" );
			bool panelOpen = game.inventoryOpen || game.openVendor != null || haulInfo != null;

			if ( rod.equipped && ! panelOpen )
			{
				if ( rod.state == "idle" && lDown ) rod.startWindup();
				else if ( rod.state == "windup" && lUp ) rod.release();
				else if ( rod.state == "floating" )
				{
					if ( lDown ) Strike();
					else if ( rDown )
					{
						rod.retrieve();
						bite = null;
					}
				}
				else if ( rod.state == "flying" && rDown ) rod.retrieve();
			}

			// bites and the fight
			if ( rod.state == "floating" ) UpdateBite( dt );
			else if ( fight == null ) rod.dip = Math.Max( 0, rod.dip - dt * 4 );
			if ( fight != null ) UpdateFight( dt, lmb && ! panelOpen );

			rod.update( dt, can, fight );
			view.Sync( rod, host.simCamera );

			// the landed fish hangs on the end of the line, turned to face you, then goes in the cooler. The catch card comes up once the fish has
			// swung in; while it is up the fish is on the card's studio portrait (as in the JS, the hanging fish is hidden then) until the card is
			// dismissed (click, E, Esc) or times out. Should the portrait not draw, the fish stays on the line, slowly turning.
			var L = landing;
			if ( L != null && rod.state == "landing" && can )
			{
				var c = host.simCamera.position; var m = rod.bobber;
				double yaw = Math.Atan2( c.x - m.x, c.z - m.z );
				if ( L.card != null )
				{
					if ( ! L.shown && rod.t > 0.3 ) { L.shown = true; catchOpen = true; ShowCard( L.card ); }
					if ( L.shown )
					{
						// a slow turn so both flanks show
						L.cardT += dt;
						card.Tick( dt );
						yaw += Math.Sin( L.cardT * 0.7 ) * 0.55;
						if ( lDown || inp.actHit( "interact" ) || inp.actHit( "cancel" ) || L.cardT > CATCH_CARD_S )
						{
							cardDismissed = true; // this frame's E / click belong to the card
							EndLanding();
						}
					}
				}

				if ( landing != null )
				{
					if ( L.shown && card.PortraitOk ) display.hide();
					else display.show( L.species, L.kg, m, yaw, dt );
					if ( L.card == null && rod.t > 2.8 ) EndLanding();
				}
			}
			else if ( landing != null || display.shown != null ) EndLanding();

			// a catch card from a haul dismisses itself: it times out on its own, or E / click / Esc takes it away early
			if ( haulInfo != null )
			{
				haulT += dt;
				card.Tick( dt );
				if ( haulT >= HAUL_CARD_S || inp.actHit( "interact" ) || inp.actHit( "cancel" ) || lDown )
				{
					EndHaulCard();
					cardDismissed = true; // this frame's E / click belonged to the card
				}
			}

			p.busy = rod.lineInWater || rod.state == "windup";
			// E closes the catch card; at the helm it must not also leave it (read by Player.updateBoat next frame)
			p.blockLeaveHelm = catchOpen;
		}

		// a vendor must not also take this frame's E (Game.updateVendors)
		public bool blocksVendors => fight != null || cardDismissed || catchOpen;

		const double ROD_HINT_S = 8; // how long "Take out the rod" stays up each time you come to the water
		double rodHintT, hintDt; bool rodTaught;

		public bool SeatedWithRod => lastCan && rod.equipped;

		// what the player is told when nothing else has a prompt (Game.prompt)
		public Prompt Prompt()
		{
			if ( ! lastCan ) return null;
			var p = host.player; var inp = host.input;
			// a boat in reach that is not yours yet: say where to buy it
			if ( p.lockedBoat != null && ! rod.equipped )
				return new Prompt { key = "$", text = $"{Gear.Boat( game.BoatId( p.lockedBoat ) ).name} · for sale at Marta's chandlery" };
			if ( ! rod.equipped )
			{
				// by the water (boat deck, pier, the wet beach, wading): suggest the rod
				bool byWater = p.mode == "deck" || ( p.mode == "walk" && ( p.surface == "wood" || p.surface == "wetsand" || p.surface == "water" ) );
				// said for a few seconds each time you come to the water, and not at all once you have had the rod out (the guide covers the key)
				rodHintT = byWater ? rodHintT + hintDt : 0;
				return byWater && ! rodTaught && rodHintT < ROD_HINT_S ? new Prompt { action = "rod", text = "Take out the rod" } : null;
			}

			var b = bite;
			string use = inp.label( "rodUse" ), inHit = inp.label( "rodIn" );
			switch ( rod.state )
			{
				case "idle": return new Prompt { action = "rodUse", text = $"Hold to wind up, release to cast   ·   {inp.label( "rod" )}  put the rod away" };
				case "windup": return new Prompt { action = "rodUse", text = "Release to cast (hold longer to cast farther)" };
				case "floating":
					if ( b != null && b.phase == "take" ) return new Prompt { action = "rodUse", text = "Strike now!" };
					if ( b != null && b.phase == "nibble" ) return new Prompt { key = "…", text = "Something's nibbling · wait until the bobber is pulled under" };
					return new Prompt { action = "rodIn", text = $"Waiting for a bite · {inHit} to reel the line in" };
				case "retrieving": return new Prompt { action = "rodIn", text = "Reeling in" };
				case "fighting":
					return fight != null && fight.tension > fight.band[ 1 ]
						? new Prompt { action = "rodUse", text = "Too much tension · let go!" }
						: new Prompt { action = "rodUse", text = $"Hold to reel ({use}) · let go when the tension goes red" };
				default: return null;
			}
		}

		// ---- bites

		// the habitat weights under the bobber
		Habitat HabitatHere() => HabitatAtPoint( rod.bobber.x, rod.bobber.z, rod.depth );

		// the shoal the cast is sitting on, if any: the schools the reef is really simulating, so fishing the school you can see (or the finder found)
		// bites sooner and favours its species
		SchoolBiteInfo SchoolNear()
		{
			var view = FishSchoolsView.instance;
			return Sonar.schoolBite( view != null && view.schools != null ? view.schools.groups : null, rod.bobber.x, rod.bobber.z );
		}

		public static Habitat HabitatAtPoint( double x, double z, double depth )
		{
			double reefDist = Tidewater.Engine.JS.Hypot( x - WorldLayout.Reef.x, z - WorldLayout.Reef.z ) - WorldLayout.Reef.radius;
			double Rect( double x0, double x1, double z0, double z1 ) => Tidewater.Engine.JS.Hypot( Math.Max( x0 - x, Math.Max( 0, x - x1 ) ), Math.Max( z0 - z, Math.Max( 0, z - z1 ) ) );
			var P = WorldLayout.Pier.x; var W = WorldLayout.Pier.width; var H = WorldLayout.Pier.headWidth;
			double walk = Rect( P - W / 2, P + W / 2, WorldLayout.Pier.zStart, WorldLayout.Pier.zEnd );
			double head = Rect( P - H / 2, P + H / 2, WorldLayout.Pier.zEnd - WorldLayout.Pier.headDepth, WorldLayout.Pier.zEnd );
			return Bites.habitatAt( depth, reefDist, Math.Min( walk, head ) );
		}

		void OnBobberLanded( string where )
		{
			if ( where != "water" )
			{
				game.Toast( "Landed on the sand", 1.4f );
				return;
			}

			bite = NewWait();
		}

		Bite NewWait()
		{
			var s = SchoolNear();
			return new Bite { phase = "wait", t = Bites.biteDelay( HabitatHere(), hour, rng, s != null ? s.influence : 0 ) };
		}

		void UpdateBite( double dt )
		{
			var b = bite;
			if ( b == null ) return;
			b.t -= dt;
			// bobber motion for the cues
			if ( b.phase == "nibble" ) rod.dip = Math.Max( 0, Math.Sin( Math.Min( 1, b.pulse ) * Math.PI ) * 0.45 );
			else if ( b.phase == "take" ) rod.dip += ( 1.4 - rod.dip ) * ( 1 - Math.Exp( - dt * 14 ) );
			else rod.dip = Math.Max( 0, rod.dip - dt * 3 );
			if ( b.phase == "nibble" ) b.pulse += dt * 3.2;

			if ( b.t > 0 ) return;
			if ( b.phase == "wait" )
			{
				var h = HabitatHere();
				var s = SchoolNear();
				string species = Bites.pickSpecies( h, hour, rng, s != null ? new SpeciesBias { id = s.model, k = s.bias } : null );
				if ( species == null )
				{
					b.t = 8;
					return;
				}

				b.species = species;
				b.kg = Bites.rollWeight( species, rng );
				b.phase = "nibble";
				b.nibbles = 1 + ( int ) Math.Floor( rng() * 3 );
				b.t = 0.7 + rng() * 0.8;
				b.pulse = 0;
			}
			else if ( b.phase == "nibble" )
			{
				b.nibbles --;
				b.pulse = 0;
				if ( b.nibbles > 0 ) b.t = 0.6 + rng() * 1.0;
				else
				{
					b.phase = "take";
					// big, strong fish give a (slightly) shorter window
					b.t = 2.4 - FishTable.Get( b.species ).fight * 0.5;
					Audio?.fishSplash( rod.bobber, 0.35 );
				}
			}
			else if ( b.phase == "take" )
			{
				game.Toast( "It took the bait and ran", 1.8f );
				bite = NewWait();
			}
		}

		public void Strike()
		{
			var b = bite;
			if ( b == null || b.phase == "wait" || b.phase == "nibble" )
			{
				// too early: a nibbling fish isn't hooked yet; it keeps nibbling (a hint, no penalty)
				if ( b != null && b.phase == "nibble" ) game.Toast( "Not yet · wait for it to pull under", 1.5f );
				return;
			}

			var g = game.state.stats;
			_splashed = false;
			fight = new CatchMinigame( b.species, b.kg, g.lineKg, g.reelSpeed, Math.Max( 3, rod.lineOut ), rng );
			bite = null;
			rod.hook();
			game.Toast( "Fish on!", 1.2f );
		}

		bool _splashed; // the surge has already splashed (Game.updateFight's fight._splashed)

		void UpdateFight( double dt, bool reeling )
		{
			var f = fight;
			string st = f.update( dt, reeling );
			// the fish thrashes at the surface as each run starts
			if ( f.surge > 0.6 && ! _splashed ) Audio?.fishSplash( rod.bobber, 0.3 + 0.5 * Math.Min( 1, f.kg / 8 ) );
			_splashed = f.surge > 0.6 ? true : f.surge < 0.3 ? false : _splashed;
			if ( st == "fighting" ) return;
			fight = null;
			rod.dip = 0;
			string name = FishTable.Get( f.species ).name;
			var s = game.state;
			if ( st == "caught" ) Land( f.species, f.kg );
			else if ( st == "snapped" )
			{
				game.Toast( "Snap! The line broke", 2.4f );
				rod.setState( "idle" );
			}
			else
			{
				game.Toast( "It threw the hook", 2f );
				rod.endFight();
			}
		}

		// the fish is in: it goes in the log, splashes, and comes up on the line for its card (also GameDebug.Land)
		public void Land( string species, double kg )
		{
			var s = game.state;
			var entry = s.addFish( species, kg, hour, new CatchSpot { x = rod.bobber.x, z = rod.bobber.z, hab = Codex.dominantHabitat( HabitatHere() ) } );
			var info = s.lastCatch;
			Audio?.fishSplash( rod.bobber, 0.8 );
			Audio?.fishFlop();
			landing = new Landing { species = species, kg = kg, card = info };
			rod.land();
		}

		// the landed fish goes in the cooler: card, fish and line away
		void EndLanding()
		{
			landing = null;
			display.hide();
			card.Hide();
			catchOpen = false;
			if ( rod.state == "landing" ) rod.setState( "idle" );
		}

		// line in at once (mode change)
		public void CancelLine( bool silent = false )
		{
			if ( fight != null && ! silent ) game.Toast( "Lost it", 1.4f );
			fight = null;
			bite = null;
			if ( rod.state != "stowed" ) rod.setState( rod.equipped ? "idle" : "stowed" );
		}

		// ---- the HUD (GameHUD.js: the fight meter, the cast bar, the strike cue, the aiming dot, the catch card)

		// the fight meter and the cue fade in and out like the DOM's opacity transitions; the last fight is kept for the fade out
		float fightFade, biteFade, castFade;
		float lastTension, lastStamina, lastDistance, lastSurge; double[] lastBand = { 0.4, 0.7 };

		public void OnGUI()
		{
			float w = Screen.width, h = Screen.height, u = UIKit.U;
			bool panels = game.inventoryOpen || game.openVendor != null;
			bool repaint = Event.current.type == EventType.Repaint;
			float dt = Time.unscaledDeltaTime;

			if ( repaint )
			{
				// the aiming dot (.gm-dot)
				if ( rod.equipped && ! panels )
				{
					UIKit.Rounded( new Rect( w / 2 - 3.5f, h / 2 - 3.5f, 7, 7 ), new Color( 0, 0, 0, 0.25f ), 3.5f );
					UIKit.Rounded( new Rect( w / 2 - 2, h / 2 - 2, 4, 4 ), new Color( 1, 1, 1, 0.7f ), 2 );
				}

				// the cast power bar (.gm-cast): 140u by 5u at 58 %, aqua to sun over what is filled
				castFade = UIKit.Approach( castFade, rod.state == "windup" ? 1 : 0, 0.14f, dt );
				if ( castFade > 0.001f )
				{
					var r = new Rect( w / 2 - 70 * u, h * 0.58f, 140 * u, 5 * u );
					UIKit.Rounded( r, UIKit.FILL2.WithAlpha( castFade ), 3 * u );
					if ( rod.state == "windup" || castFade > 0 ) UIKit.RampPill( new Rect( r.x, r.y, r.width * ( float ) Math.Min( 1, Math.Max( 0, rod.power ) ), r.height ), UIKit.AQUA, UIKit.SUN, castFade );
				}

				// the strike cue (.gm-bite): "!" when a fish takes the bobber, scaling in from 0.6
				bool take = bite != null && bite.phase == "take";
				biteFade = UIKit.Approach( biteFade, take ? 1 : 0, take ? 0.2f : 0.12f, dt );
				if ( biteFade > 0.001f )
				{
					float sc = 0.6f + 0.4f * UIScale.Ease( biteFade ), px = 56 * u * sc;
					var bs = UIKit.Style( UIFonts.InterBold, 56 * sc, TextAnchor.MiddleCenter );
					var rr = new Rect( 0, h * 0.42f - px, w, px * 2 );
					for ( int i = 0; i < 8; i ++ ) { float an = i * Mathf.PI / 4; bs.normal.textColor = UIKit.SUN.WithAlpha( 0.1f * biteFade ); GUI.Label( new Rect( rr.x + Mathf.Cos( an ) * 4 * u, rr.y + Mathf.Sin( an ) * 4 * u, rr.width, rr.height ), "!", bs ); }
					bs.normal.textColor = new Color( 0, 0, 0, 0.5f * biteFade ); GUI.Label( new Rect( rr.x, rr.y + 2 * u, rr.width, rr.height ), "!", bs );
					bs.normal.textColor = UIKit.SUN.WithAlpha( biteFade ); GUI.Label( rr, "!", bs );
				}

				// the fight: line tension with its safe band, the fish's stamina, line out
				if ( fight != null ) { lastTension = ( float ) fight.tension; lastStamina = ( float ) fight.stamina; lastDistance = ( float ) fight.distance; lastSurge = ( float ) fight.surge; lastBand = fight.band; }
				fightFade = UIKit.Approach( fightFade, fight != null ? 1 : 0, 0.24f, dt );
				if ( fightFade > 0.001f ) DrawFight( w, h, UIScale.Ease( fightFade ) );
			}

			if ( catchOpen && landing != null && landing.card != null ) card.OnGUI( ( float ) Math.Max( 0, 1 - landing.cardT / CATCH_CARD_S ), host.input.label( "rodUse" ), host.input.label( "interact" ) );
			else if ( haulInfo != null ) card.OnGUI( ( float ) Math.Max( 0, 1 - haulT / HAUL_CARD_S ), host.input.label( "rodUse" ), host.input.label( "interact" ) );
		}

		// .gm-fight: 360u wide, 58u above the prompt line; the call, the line out, the tension bar and the fish's stamina
		void DrawFight( float w, float h, float a )
		{
			float u = UIKit.U;
			float ph = 12 * u + 12.5f * u * 1.21f + 8 * u + 12 * u + 8 * u + 11.5f * u * 1.21f + 12 * u;
			var r = new Rect( w / 2 - 180 * u, h - ( Mathf.Max( 72 * u, 0.13f * h ) + 58 * u ) - ph, 360 * u, ph );
			UIKit.Glass( r, 16 * u, a );
			float x = r.x + 16 * u, iw = r.width - 32 * u, y = r.y + 12 * u;
			string call = "Reel in"; Color col = UIKit.INK;
			if ( lastTension > 0.88 ) { call = "Ease off!"; col = UIKit.CORAL; }
			else if ( lastSurge > 0.55 ) { call = "It's running!"; col = UIKit.CORAL; }
			else if ( lastTension < 0.15 ) { call = "Slack line!"; col = UIKit.CORAL; }
			else if ( lastTension >= lastBand[ 0 ] && lastTension <= lastBand[ 1 ] ) { call = "Good pressure"; col = UIKit.AQUA; }
			float lh = 12.5f * u * 1.21f;
			var cs = UIKit.Style( UIFonts.InterSemi, 12.5f, TextAnchor.UpperLeft ); cs.normal.textColor = col.WithAlpha( a );
			GUI.Label( new Rect( x, y, iw * 0.7f, lh + 2 ), call, cs );
			var ds = UIKit.Style( UIFonts.Mono, 12.5f, TextAnchor.UpperRight ); ds.normal.textColor = UIKit.INK2.WithAlpha( a );
			GUI.Label( new Rect( x, y, iw, lh + 2 ), $"{Fmt( lastDistance, "F1" )} m", ds );
			y += lh + 8 * u;

			// the tension bar: the band the line is safe in, the danger zone at the end, the needle
			var bar = new Rect( x, y, iw, 12 * u );
			UIKit.Rounded( bar, UIKit.FILL2.WithAlpha( a ), 6 * u );
			float s = bar.width / 1.05f, b0 = ( float ) lastBand[ 0 ] * s, b1 = ( float ) lastBand[ 1 ] * s;
			var aq = UIKit.AQUA;
			GUI.DrawTexture( new Rect( bar.x + b0, bar.y, b1 - b0, bar.height ), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, aq.WithAlpha( 0.28f * a ), 0, 0 );
			GUI.DrawTexture( new Rect( bar.x + b0, bar.y, 1, bar.height ), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, aq.WithAlpha( 0.6f * a ), 0, 0 );
			GUI.DrawTexture( new Rect( bar.x + b1 - 1, bar.y, 1, bar.height ), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, aq.WithAlpha( 0.6f * a ), 0, 0 );
			UIKit.Rounded( new Rect( bar.xMax - bar.width * 0.08f, bar.y, bar.width * 0.08f, bar.height ), UIKit.CORAL.WithAlpha( 0.35f * a ), 6 * u );
			bool hot = lastTension > lastBand[ 1 ];
			float nx = bar.x + Mathf.Min( lastTension, 1.05f ) * s;
			UIKit.Rounded( new Rect( nx - 5, bar.y - 2, 10, bar.height + 4 ), ( hot ? UIKit.CORAL : Color.white ).WithAlpha( 0.18f * a ), 5 );
			UIKit.Rounded( new Rect( nx - 1.5f, bar.y, 3, bar.height ), ( hot ? UIKit.CORAL : UIKit.INK ).WithAlpha( a ), 1.5f );
			y += bar.height + 8 * u;

			// the fish's stamina
			float sl = 11.5f * u * 1.21f;
			var fs = UIKit.Style( UIFonts.Inter, 11.5f, TextAnchor.UpperLeft ); fs.normal.textColor = UIKit.INK3.WithAlpha( a );
			float fw = fs.CalcSize( new GUIContent( "Fish" ) ).x;
			GUI.Label( new Rect( x, y, fw + 2, sl + 2 ), "Fish", fs );
			var sb = new Rect( x + fw + 8 * u, y + sl / 2 - 2 * u, iw - fw - 8 * u, 4 * u );
			UIKit.Rounded( sb, UIKit.FILL2.WithAlpha( a ), 2 * u );
			if ( lastStamina > 0 ) UIKit.Rounded( new Rect( sb.x, sb.y, Mathf.Max( 4 * u, sb.width * Mathf.Clamp01( lastStamina ) ), sb.height ), UIKit.SUN.WithAlpha( a ), 2 * u );
		}

		static string Fmt( double v, string f = "F2" ) => v.ToString( f, System.Globalization.CultureInfo.InvariantCulture );

		// the catch card of a trap haul (Game.haulTrap: hud.showCatch( lastCatch, 7000 )): the best of the haul, with no fish on a line
		public void ShowHaulCard( LastCatch info )
		{
			if ( info == null ) return;
			haulInfo = info; haulT = 0; catchOpen = true;
			ShowCard( info );
		}

		void EndHaulCard()
		{
			haulInfo = null; haulT = 0;
			card.Hide();
			if ( landing == null ) catchOpen = false;
		}

		// GameHUD.showCatch: the card's text (the note about the log, the price) and its start; the card itself (CatchCard) draws the layout and the fish portrait
		void ShowCard( LastCatch info )
		{
			var st = game.state;
			string note;
			if ( ! info.kept ) note = $"No room in the {( st.upgrades[ "hold" ] > 0 ? "hold" : "cooler" )} · you let it go";
			else if ( info.record ) note = $"Previous best <color=#f0c46a><b>{Fmt( info.prevBestKg )} kg</b></color> · {info.prevBestCm} cm. Beaten by {Fmt( info.kg - info.prevBestKg )} kg.";
			else if ( info.newSpecies ) note = "First one in your fish log.";
			else note = $"Your best: {Fmt( info.prevBestKg )} kg · {info.prevBestCm} cm";
			var probe = new InventoryFish { species = info.species, kg = info.kg, value = info.value };
			double price = st.priceOf( probe );
			if ( info.kept && st.orderMulFor( probe ) > 1 ) note += $"\n<color=#f0c46a>★</color> Joe's order: he pays ×{Orders.ORDER_MULT} for this one today.";
			card.Show( info, price, note );
		}
	}
}
