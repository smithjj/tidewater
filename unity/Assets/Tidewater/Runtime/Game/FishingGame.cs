using System;
using Tidewater.Core;
using Tidewater.Ocean;
using Tidewater.Player;
using Tidewater.World;
using Tidewater.World.Fish;
using UnityEngine;
using Engine3 = Tidewater.Engine.Vector3;

// The fishing half of src/game/Game.js on top of the ported world:
//   R          take out / put away the rod (on foot, on the pier, on the boat's deck)
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
// ported. Not ported with them: the card's studio fish portrait (the landed fish hangs on the line while the card is up instead), the splash
// burst, the pad rumble and the sounds (every IGameAudio hook is optional).
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
		readonly Transform parent;
		public CatchMinigame fight;           // while a fish is on
		public Bite bite;
		public Landing landing;               // while the caught fish swings in view
		public bool cardDismissed;            // this frame's E / click belonged to the card
		public bool catchOpen { get; private set; }
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
			ApplyGear();
		}

		public void Dispose()
		{
			view.Dispose();
			display.Dispose();
		}

		// Game.applyGear: the rod's cast distance and reel speed from the gear
		public void ApplyGear()
		{
			var g = game.state.stats;
			rod.setGear( g.castM, g.reelSpeed );
		}

		// the player can fish on foot and on the boat's deck, not swimming, at the helm or in the free camera
		bool CanFish
		{
			get { var p = host.player; return ! host.freeCam && ( p.mode == "walk" || p.mode == "deck" ); }
		}

		double hour => game.clock.hour;

		// ---- per frame (after the player update)
		public void Update( double dt )
		{
			var p = host.player; var inp = host.input;
			// the Editor destroys the scene objects it made when Play starts; build them again
			if ( ! view.Alive ) { view.Dispose(); view = FishingRodView.Create( parent ); }
			if ( ! display.Alive ) { display.Dispose(); display = new CatchDisplay( parent ); }
			cardDismissed = false;
			bool can = CanFish;
			lastCan = can;

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
			bool panelOpen = game.inventoryOpen || game.openVendor != null;

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
			// swung in, and the fish stays (slowly turning) until the card is dismissed (click, E, Esc) or times out.
			var L = landing;
			if ( L != null && rod.state == "landing" && can )
			{
				var c = host.simCamera.position; var m = rod.bobber;
				double yaw = Math.Atan2( c.x - m.x, c.z - m.z );
				if ( L.card != null )
				{
					if ( ! L.shown && rod.t > 0.3 ) { L.shown = true; catchOpen = true; }
					if ( L.shown )
					{
						// a slow turn so both flanks show
						L.cardT += dt;
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
					// (the JS shows the fish on the card's studio portrait while the card is up; here it stays on the line)
					display.show( L.species, L.kg, m, yaw, dt );
					if ( L.card == null && rod.t > 2.8 ) EndLanding();
				}
			}
			else if ( landing != null || display.shown != null ) EndLanding();

			p.busy = rod.lineInWater || rod.state == "windup";
			// E closes the catch card; at the helm it must not also leave it (read by Player.updateBoat next frame)
			p.blockLeaveHelm = catchOpen;
		}

		// a vendor must not also take this frame's E (Game.updateVendors)
		public bool blocksVendors => fight != null || cardDismissed || catchOpen;

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
				return byWater ? new Prompt { action = "rod", text = "Take out the rod" } : null;
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
			if ( st == "caught" )
			{
				var entry = s.addFish( f.species, f.kg, hour, new CatchSpot { x = rod.bobber.x, z = rod.bobber.z, hab = Codex.dominantHabitat( HabitatHere() ) } );
				var info = s.lastCatch;
				// the catch card while the fish hangs on the line
				Audio?.fishSplash( rod.bobber, 0.8 );
				Audio?.fishFlop();
				landing = new Landing { species = f.species, kg = f.kg, card = info };
				rod.land();
			}
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

		// the landed fish goes in the cooler: card, fish and line away
		void EndLanding()
		{
			landing = null;
			display.hide();
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

		GUIStyle callStyle, bigStyle, cardTitle, cardSmall, cardStat, cardNote;

		void Styles()
		{
			if ( callStyle != null && callStyle.fontSize == 15 ) return; // (see GameHost.Styles: the Editor resets GUI styles between Play sessions)
			callStyle = new GUIStyle( GUI.skin.label ) { fontSize = 15, fontStyle = FontStyle.Bold };
			bigStyle = new GUIStyle( GUI.skin.label ) { fontSize = 92, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
			cardTitle = new GUIStyle( GUI.skin.label ) { fontSize = 26, fontStyle = FontStyle.Bold }; cardTitle.normal.textColor = Color.white;
			cardSmall = new GUIStyle( GUI.skin.label ) { fontSize = 12, wordWrap = true }; cardSmall.normal.textColor = new Color( 1, 1, 1, 0.75f );
			cardStat = new GUIStyle( GUI.skin.label ) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter }; cardStat.normal.textColor = Color.white;
			cardNote = new GUIStyle( GUI.skin.label ) { fontSize = 13, wordWrap = true }; cardNote.normal.textColor = Color.white;
		}

		static void Fill( Rect r, Color c ) { var o = GUI.color; GUI.color = c; GUI.DrawTexture( r, Texture2D.whiteTexture ); GUI.color = o; }

		public void OnGUI()
		{
			Styles();
			float w = Screen.width, h = Screen.height;
			bool panels = game.inventoryOpen || game.openVendor != null;

			// the aiming dot
			if ( rod.equipped && ! panels ) Fill( new Rect( w / 2 - 2, h / 2 - 2, 4, 4 ), new Color( 1, 1, 1, 0.7f ) );

			// the cast power bar
			if ( rod.state == "windup" )
			{
				var r = new Rect( w / 2 - 120, h * 0.8f, 240, 10 );
				Fill( r, new Color( 0, 0, 0, 0.5f ) );
				Fill( new Rect( r.x, r.y, r.width * ( float ) rod.power, r.height ), Color.Lerp( new Color( 0.45f, 0.85f, 1f ), new Color( 1f, 0.7f, 0.25f ), ( float ) rod.power ) );
			}

			// the strike cue: "!" when a fish takes the bobber
			if ( bite != null && bite.phase == "take" )
			{
				bigStyle.normal.textColor = new Color( 1f, 0.82f, 0.2f );
				GUI.Label( new Rect( 0, h * 0.28f, w, 120 ), "!", bigStyle );
			}

			// the fight: line tension with its safe band, the fish's stamina, line out
			if ( fight != null ) DrawFight( fight, w, h );
			if ( catchOpen && landing != null && landing.card != null ) DrawCatch( landing, w, h );
		}

		void DrawFight( CatchMinigame f, float w, float h )
		{
			var r = new Rect( w / 2 - 190, h * 0.58f, 380, 84 );
			GUI.Box( r, GUIContent.none );
			string call = "Reel in"; Color col = Color.white;
			if ( f.tension > 0.88 ) { call = "Ease off!"; col = new Color( 1f, 0.55f, 0.3f ); }
			else if ( f.surge > 0.55 ) { call = "It's running!"; col = new Color( 1f, 0.55f, 0.3f ); }
			else if ( f.tension < 0.15 ) { call = "Slack line!"; col = new Color( 1f, 0.55f, 0.3f ); }
			else if ( f.tension >= f.band[ 0 ] && f.tension <= f.band[ 1 ] ) { call = "Good pressure"; col = new Color( 0.5f, 0.95f, 0.55f ); }
			callStyle.normal.textColor = col;
			GUI.Label( new Rect( r.x + 14, r.y + 6, 220, 24 ), call, callStyle );
			var right = new GUIStyle( callStyle ) { alignment = TextAnchor.MiddleRight }; right.normal.textColor = Color.white;
			GUI.Label( new Rect( r.x + r.width - 134, r.y + 6, 120, 24 ), $"{f.distance:F1} m", right );
			// the tension bar: the band the line is safe in, the danger zone above it, the needle
			var bar = new Rect( r.x + 14, r.y + 36, r.width - 28, 12 );
			Fill( bar, new Color( 1, 1, 1, 0.12f ) );
			float s = bar.width / 1.05f;
			Fill( new Rect( bar.x + ( float ) f.band[ 0 ] * s, bar.y, ( float ) ( f.band[ 1 ] - f.band[ 0 ] ) * s, bar.height ), new Color( 0.35f, 0.85f, 0.45f, 0.45f ) );
			Fill( new Rect( bar.x + ( float ) f.band[ 1 ] * s, bar.y, bar.width - ( float ) f.band[ 1 ] * s, bar.height ), new Color( 0.95f, 0.3f, 0.25f, 0.4f ) );
			bool hot = f.tension > f.band[ 1 ];
			Fill( new Rect( bar.x + ( float ) Math.Min( f.tension, 1.05 ) * s - 1.5f, bar.y - 3, 3, bar.height + 6 ), hot ? new Color( 1f, 0.3f, 0.25f ) : Color.white );
			// the fish's stamina
			GUI.Label( new Rect( r.x + 14, r.y + 54, 50, 20 ), "Fish", cardSmall );
			var sb = new Rect( r.x + 56, r.y + 62, r.width - 70, 6 );
			Fill( sb, new Color( 1, 1, 1, 0.12f ) );
			Fill( new Rect( sb.x, sb.y, sb.width * ( float ) Math.Max( 0, f.stamina ), sb.height ), new Color( 0.45f, 0.75f, 1f ) );
		}

		static string Fmt( double v, string f = "F2" ) => v.ToString( f, System.Globalization.CultureInfo.InvariantCulture );

		// GameHUD.showCatch: the species, the badge, the length / weight / value, and what it means for the log
		void DrawCatch( Landing L, float w, float h )
		{
			var info = L.card; var f = FishTable.Get( info.species ); var st = game.state;
			double inch = info.cm / 2.54, lb = info.kg * 2.20462;
			string badge = info.record ? "★ New record" : info.newSpecies ? "New species" : "Catch";
			string note;
			if ( ! info.kept ) note = $"No room in the {( st.upgrades[ "hold" ] > 0 ? "hold" : "cooler" )} · you let it go";
			else if ( info.record ) note = $"Previous best {Fmt( info.prevBestKg )} kg · {info.prevBestCm} cm. Beaten by {Fmt( info.kg - info.prevBestKg )} kg.";
			else if ( info.newSpecies ) note = "First one in your fish log.";
			else note = $"Your best: {Fmt( info.prevBestKg )} kg · {info.prevBestCm} cm";
			var probe = new InventoryFish { species = info.species, kg = info.kg, value = info.value };
			double price = st.priceOf( probe );
			if ( info.kept && st.orderMulFor( probe ) > 1 ) note += $"\n★ Joe's order: he pays ×{Orders.ORDER_MULT} for this one today.";

			var r = new Rect( w - 360, h * 0.16f, 330, 330 );
			GUI.Box( r, GUIContent.none );
			GUILayout.BeginArea( new Rect( r.x + 16, r.y + 12, r.width - 32, r.height - 24 ) );
			GUILayout.Label( badge.ToUpperInvariant(), cardSmall );
			GUILayout.Label( f.name, cardTitle );
			GUILayout.Label( f.sci ?? "", cardSmall );
			GUILayout.Space( 10 );
			GUILayout.BeginHorizontal();
			StatBox( "Length", $"{info.cm:F0} cm", $"{Fmt( inch, "F1" )} in" );
			StatBox( "Weight", $"{( info.kg < 1 ? Fmt( info.kg ) : Fmt( info.kg, "F1" ) )} kg", $"{Fmt( lb, "F1" )} lb" );
			StatBox( "Value", $"${price:F0}", info.kept ? "in the cooler" : "let go" );
			GUILayout.EndHorizontal();
			GUILayout.Space( 8 );
			GUILayout.Label( note, cardNote );
			GUILayout.FlexibleSpace();
			GUILayout.Label( $"{host.input.label( "rodUse" )} or {host.input.label( "interact" )} to continue", cardSmall );
			GUILayout.EndArea();
			// the time left on the card
			Fill( new Rect( r.x + 16, r.yMax - 12, ( r.width - 32 ) * ( float ) Math.Max( 0, 1 - L.cardT / CATCH_CARD_S ), 3 ), new Color( 1, 1, 1, 0.5f ) );
		}

		void StatBox( string label, string value, string sub )
		{
			GUILayout.BeginVertical( GUILayout.Width( 96 ) );
			GUILayout.Label( label, cardSmall );
			GUILayout.Label( value, cardStat );
			GUILayout.Label( sub, cardSmall );
			GUILayout.EndVertical();
		}
	}
}
