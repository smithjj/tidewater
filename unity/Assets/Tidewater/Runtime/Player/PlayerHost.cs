using Tidewater.Core;
using Tidewater.Ocean;
using Tidewater.Util;
using Tidewater.World;
using UnityEngine;
using Engine3 = Tidewater.Engine.Vector3;

// Hosts the ported Player (Player.js) and FlyCamera in Unity: owns the input, builds the player once the sea's water queries, the terrain and the
// boat exist, runs the frame in the App.js order (the boat first, then the player or the free camera) and applies the camera pose to the Unity
// camera. F toggles the free camera (App.setFreeCam / dropPlayerAtCamera). A click in the Game view captures the mouse (Esc releases it).
// The prompts are drawn with IMGUI until the HUD (ui/) is ported.
namespace Tidewater.Player
{
	public sealed class PlayerHost : MonoBehaviour
	{
		public bool freeCam;                 // start in the free camera (the JS ?fly)
		public bool startAboard;             // start on the boat's deck instead of on the beach (until the pier is ported, the boat is a swim away)
		public bool showPrompts = true;
		public bool noAudio;                 // the JS ?noAudio: no sound
		public GameInput input { get; private set; }
		public Player player { get; private set; }
		public FlyCamera fly { get; private set; }
		public SimCamera simCamera { get; } = new SimCamera();
		public BoatDriver driver { get; private set; }
		public Pelagic30Driver pelagicDriver { get; private set; } // the second boat (null when the scene has none)
		public MiniBoatDriver miniDriver { get; private set; } // the third boat (null when the scene has none)
		public Colliders colliders { get; private set; }
		TerrainRenderer terrain;
		public Tidewater.World.TerrainData terrainData => terrain != null ? terrain.data : null; // the island's heights (the traders stand on it)
		public Tidewater.Game.GameHost game { get; private set; } // the economy (null when the scene has none)
		public Tidewater.Audio.SoundHost sound { get; private set; } // the island's sound (null with noAudio)
		bool flashSeeded;
		string toast; float toastUntil;

		// a short message at the top of the view (until the HUD's toasts are ported)
		public void Toast( string text, float seconds = 2.2f ) { toast = text; toastUntil = Time.unscaledTime + seconds; }

		bool Build()
		{
			var ocean = OceanRenderer.instance;
			terrain = FindAnyObjectByType<TerrainRenderer>();
			driver = FindAnyObjectByType<BoatDriver>();
			if ( ocean == null || ocean.query == null || terrain == null || terrain.data == null || driver == null || ! driver.Ensure() ) return false;
			driver.external = true; // the player drives the boat now (Player.updateBoat)
			input = new GameInput();
			colliders = driver.colliders;
			// the boats the player can board (App.js order): the lobster boat and, when the scene has them, the Pelagic 30 and the mini fishing boat
			var boats = new System.Collections.Generic.List<BoatController> { driver.controller };
			pelagicDriver = FindAnyObjectByType<Pelagic30Driver>();
			if ( pelagicDriver != null && pelagicDriver.Ensure() ) boats.Add( pelagicDriver.controller );
			miniDriver = FindAnyObjectByType<MiniBoatDriver>();
			if ( miniDriver != null && miniDriver.Ensure() ) boats.Add( miniDriver.controller );
			player = new Player( simCamera, input, terrain.data.HeightAt, colliders, ocean.query, driver.controller, boats );
			game = FindAnyObjectByType<Tidewater.Game.GameHost>();
			fly = new FlyCamera( simCamera, input );
			fly.setPose( new Engine3( 20, 6, - 20 ), System.Math.PI * 0.9, - 0.12 );
			if ( startAboard ) { player.boardBoat( driver.controller ); }
			allBoats = boats;
			if ( ! noAudio ) BuildSound();
			return true;
		}

		// the sound (App.js: new SoundScape, player.audio, the boat's onSlam)
		System.Collections.Generic.List<BoatController> allBoats;

		void BuildSound()
		{
			sound = Tidewater.Audio.SoundHost.Create( this );
			player.audio = sound.scape;
			// a slam is heard from the boat you are on (the JS hooks the lobster boat's only)
			foreach ( var b in allBoats )
			{
				var ctl = b;
				ctl.onSlam = s => { if ( sound != null && sound.Alive && ActiveBoat() == ctl ) sound.scape.hullSlap( s ); };
			}
		}

		// the boat everything player-facing follows: the one you are aboard, else the lobster boat (App.boatCtl)
		public BoatController ActiveBoat() => player != null && ( player.mode == "boat" || player.mode == "deck" ) && player.boat != null ? player.boat : driver.controller;

		void Update()
		{
			if ( ! Application.isPlaying ) return;
			Step( Mathf.Min( Time.deltaTime, 0.1f ) );
		}

		// one frame of App.frame (Update's body; the Editor tools call it directly, since the Editor does not tick Update when it is not playing)
		public void Step( double dt )
		{
			if ( player == null && ! Build() ) return;
			input.Poll( dt );
			// a click captures the mouse (the pointer lock)
			if ( ! input.locked && input.mouseDown && input.enabled && ! input.menuMode ) input.RequestLock();

			if ( input.actHit( "freeCam" ) ) SetFreeCam( ! freeCam );

			// the torch remembers what it was left as (the controls option); L toggles it (App.js: the flashlight action)
			var lamps = LocalLightsView.instance;
			if ( lamps != null )
			{
				if ( ! flashSeeded ) { flashSeeded = true; lamps.ToggleFlashlight( input.bindings.opts.flashlightOn ); }
				if ( input.actHit( "flashlight" ) )
				{
					bool on = lamps.ToggleFlashlight();
					input.bindings.opts.flashlightOn = on;
					Toast( on ? "Flashlight on" : "Flashlight off" );
				}
			}

			// App.frame: the boats step, then the player (which sets the boat's controls) or the free camera
			driver.Tick( dt );
			if ( pelagicDriver != null ) pelagicDriver.Tick( dt );
			if ( miniDriver != null ) miniDriver.Tick( dt );
			if ( freeCam ) fly.update( dt );
			else player.update( dt );
			// Game.update: the world clock, the traders, the fuel (after the player, which sets the boat's controls and the prompt)
			if ( game != null && game.Ensure( this ) ) game.Tick( dt );
			// the sky follows the world clock (App.updateSun)
			var sky = Tidewater.Sky.DayNight.instance;
			if ( sky != null && game != null && game.clock != null ) { sky.hour = game.clock.hour; sky.Apply(); }
			Apply();
			// App.updateAudio, and the mute action
			if ( ! noAudio )
			{
				// (the Editor destroys DontSave objects when a Play session starts while this object lives on: build the sound again)
				if ( sound == null || ! sound.Alive ) BuildSound();
				else
				{
					if ( input.actHit( "mute" ) ) sound.ToggleMute( this );
					sound.Tick( dt, this );
				}
			}

			input.endFrame();
		}

		// Free (debug) camera on F; the walker / boat resumes where it was left.
		public void SetFreeCam( bool on )
		{
			if ( on == freeCam ) return;
			freeCam = on;
			if ( on )
			{
				var e = new Tidewater.Engine.Euler().setFromQuaternion( simCamera.quaternion, "YXZ" );
				fly.setPose( simCamera.position.clone(), e.y, e.x );
				fly.velocity.set( 0, 0, 0 );
			}
			else if ( player.mode != "boat" && player.mode != "deck" ) DropPlayerAtCamera();
		}

		// Leaving the free camera: the player continues from where the camera is, facing the same way, and falls from there (swimming at once if the
		// camera is under water).
		public void DropPlayerAtCamera()
		{
			var p = player; var c = simCamera.position;
			var e = new Tidewater.Engine.Euler().setFromQuaternion( simCamera.quaternion, "YXZ" );
			p.yaw = e.y;
			p.pitch = Tidewater.Engine.MathUtils.clamp( e.x, - 1.5, 1.5 );
			p.velocity.set( 0, 0, 0 );
			double ground = System.Math.Max( terrain.data.HeightAt( c.x, c.z ), colliders.groundHeightAt( c.x, c.z, c.y ) );
			double water = G.cameraWaterHeight;
			if ( c.y < water )
			{
				p.mode = "swim";
				p.position.set( c.x, System.Math.Max( c.y - 0.16, ground + 0.3 ), c.z );
			}
			else
			{
				// drop from where the camera is: gravity brings you down onto the ground or a deck, or into the sea (the walker starts swimming once
				// it is out of its depth)
				p.mode = "walk";
				p.position.set( c.x, System.Math.Max( c.y - 1.62, ground ), c.z );
				p.grounded = false;
			}

			p.waterH = water;
			p.waterMean = water;
		}

		// the sim camera -> the Unity camera
		void Apply()
		{
			var main = Camera.main;
			if ( main == null ) return;
			var q = simCamera.quaternion;
			var f = simCamera.viewDir( new Engine3() );
			var up = new Engine3( 0, 1, 0 ).applyQuaternion( q );
			Tidewater.Player.BoatCamera.Apply( main.transform, simCamera.position, f, up );
		}

		void OnGUI()
		{
			if ( ! showPrompts || player == null || ! Application.isPlaying ) return;
			var style = new GUIStyle( GUI.skin.label ) { alignment = TextAnchor.MiddleCenter, font = Tidewater.Game.UIFonts.InterBold, fontSize = 18 };
			style.normal.textColor = Color.white;
			float w = Screen.width, h = Screen.height;
			if ( ! freeCam && player.prompt != null )
				GUI.Label( new Rect( 0, h * 0.72f, w, 30 ), "[" + ( player.prompt.key ?? input.label( player.prompt.action ) ) + "]  " + player.prompt.text, style );
			if ( toast != null && Time.unscaledTime < toastUntil ) GUI.Label( new Rect( 0, h * 0.08f, w, 30 ), toast, style );
			var small = new GUIStyle( GUI.skin.label ) { alignment = TextAnchor.LowerLeft, font = Tidewater.Game.UIFonts.Inter, fontSize = 12 };
			small.normal.textColor = new Color( 1, 1, 1, 0.7f );
			string hint = ! input.locked ? "Click the view to capture the mouse  ·  Esc releases it  ·  " : "";
			GUI.Label( new Rect( 10, h - 28, w, 24 ), hint + ( freeCam ? "free camera (F)" : player.mode ) + "   WASD move · Shift sprint · Space jump / up · C dive · E interact · V boat camera · F free camera", small );
		}
	}
}
