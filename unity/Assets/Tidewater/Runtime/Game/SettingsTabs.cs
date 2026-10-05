using System;
using System.Collections.Generic;
using System.Globalization;
using Tidewater.Core;
using Tidewater.Ocean;
using Tidewater.Sky;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// The seven tabs of the settings panel (src/ui/AppUI.js), bound to the Unity systems. Each control reads and writes the live value through a getter and a setter, as AppUI's `s` object
// does for the JS; the sea's own values (wind, fetch, chop, swell, whitecaps, surf, period, cloud cover) are kept in one Condition and go through the one write path
// (OceanRenderer.ApplyConditions, the port of writeConditions), so these sliders and the weather that walks the same values cannot drift apart.
//
// Settings the JS has and HDRP has no counterpart for are not here: Cirrus (the volumetric clouds have no cirrus layer), Haze and Sun shafts (the scene's fog is off; the
// aerial perspective is the physically based sky's), Air particles (not ported), Bounce light (GroundBounce is not ported: HDRP's own GI does it). Anti-aliasing is HDRP's TAA
// at its three quality levels (the JS jitters 2..16 positions). The post-processing sliders keep the JS ranges but start at what the scene already renders with, so opening the
// panel changes nothing.
namespace Tidewater.Game
{
	public static class SettingsTabs
	{
		sealed class SeaState { public Condition c; }

		public static void Build( SettingsUI ui, GameHost game )
		{
			var ocean = OceanRenderer.instance;
			var weather = game.weather;
			var host = game.Host;
			var sea = new SeaState();
			sea.c = weather != null ? Conditions.ConditionAt( weather.level, weather.windDir ) : WithDir( Conditions.SEA[ 1 ], ocean != null ? ocean.windDirection : 25 );
			var bind = game.Input != null ? game.Input.bindings : null;

			// the sea controls take the weather out of the loop; every write is the one path
			void manual() { if ( weather != null && weather.setManual() ) game.Toast( "Weather: yours to steer" ); }
			void apply( bool spectrum = true ) { if ( ocean != null ) ocean.ApplyConditions( sea.c, spectrum, true, spectrum ); }

			// ---------------------------------------------------------------- Ocean
			var oceanTab = ui.AddTab( "ocean", "Ocean", "ocean" );
			var seaF = oceanTab.AddFolder( "Sea state", "wind" );
			var presetIcons = new string[ Conditions.CONDITIONS.Length ];
			for ( int i = 0; i < presetIcons.Length; i++ ) presetIcons[ i ] = Conditions.CONDITIONS[ i ].ToLowerInvariant();
			seaF.AddPresets( "Conditions", Conditions.CONDITIONS, presetIcons, i =>
			{
				manual();
				var p = Conditions.SEA[ i ];
				double dir = sea.c.windDir;
				sea.c = p; sea.c.windDir = dir;
				apply();
			}, 1 );
			seaF.AddSlider( "Wind speed", () => sea.c.wind, v => { manual(); sea.c.wind = v; apply(); }, 0.5, 30, 0.1, "m/s", false, null, "Wind 10 m above the sea. Drives the local wind waves, whitecaps and spray." );
			seaF.AddSlider( "Wind direction", () => sea.c.windDir, v => { manual(); sea.c.windDir = v; apply(); }, 0, 360, 1, "°" );
			seaF.AddSlider( "Fetch", () => sea.c.fetch, v => { manual(); sea.c.fetch = v; apply(); }, 5, 2000, 0, "km", true, null, "Distance the wind has blown over open water: longer fetch, longer and higher waves." );
			seaF.AddSlider( "Choppiness", () => sea.c.chop, v => { manual(); sea.c.chop = v; apply( false ); }, 0, 1.6, 0.01, "", false, null, "Horizontal displacement: sharp crests, wide troughs." );
			seaF.AddSlider( "Ocean swell", () => sea.c.swell, v => { manual(); sea.c.swell = v; apply(); }, 0, 2, 0.01 );
			seaF.AddSlider( "Whitecaps", () => sea.c.whitecaps, v => { manual(); sea.c.whitecaps = v; apply( false ); }, 0, 1, 0.01 );
			// the weather: it rises and falls on in-game time, independent of the sliders above
			var weatherF = seaF.AddFolder( "Weather", "clock" );
			if ( weather != null )
			{
				weatherF.AddToggle( "Dynamic weather", () => weather.enabled, v => weather.setEnabled( v ), "The sea state walks the condition ladder on its own, on in-game time. Touch any control above and it hands the sea back to you." );
				weatherF.AddSlider( "Weather pace", () => weather.pace, v => weather.pace = v, 0.25, 4, 0.05, "×", false, null, "How quickly conditions change." );
			}

			double clarity = 1;
			var water = oceanTab.AddFolder( "Water", "droplet" );
			water.AddSlider( "Clarity", () => clarity, v =>
			{
				clarity = v;
				// scale absorption / scattering around the tropical defaults
				float k = 1f / Mathf.Max( 0.2f, ( float ) v );
				G.waterAbsorption = new Vector3( 0.42f, 0.075f, 0.035f ) * ( 0.6f + 0.4f * k );
				G.waterScattering = new Vector3( 0.012f, 0.018f, 0.024f ) * ( k * k );
			}, 0.3, 2, 0.01, "", false, null, "Lower = more suspended sediment and plankton (greener, murkier)." );

			// ---------------------------------------------------------------- Shore
			var shoreTab = ui.AddTab( "shore", "Shore", "shore" );
			var surf = shoreTab.AddFolder( "Surf", "wave" );
			surf.AddSlider( "Wave height", () => sea.c.surf, v => { manual(); sea.c.surf = v; apply( false ); }, 0, 1.4, 0.01, "m", false, v => ( v * 2 ).ToString( "F2", CultureInfo.InvariantCulture ) + " m" );
			surf.AddSlider( "Wave period", () => sea.c.period, v => { manual(); sea.c.period = v; apply( false ); }, 5, 16, 0.1, "s" );
			if ( ocean != null && ocean.shore != null )
			{
				var shore = ocean.shore;
				surf.AddSlider( "Breaking depth ratio", () => shore.gamma, v => shore.gamma = ( float ) v, 0.5, 1.1, 0.01, "", false, null, "Waves break when height exceeds this fraction of the depth." );
				surf.AddSlider( "Curl", () => shore.curl, v => shore.curl = ( float ) v, 0, 1.5, 0.01 );
			}

			if ( ocean != null && ocean.breakers != null )
			{
				var br = ocean.breakers;
				surf.AddSlider( "Spray", () => br.sprayAmount, v => br.sprayAmount = ( float ) v, 0, 2, 0.01, "", false, null, "Droplets and mist thrown off breaking crests." );
				surf.AddSlider( "Lip sheet", () => br.sheet, v => br.sheet = ( float ) v, 0, 1.5, 0.01, "", false, null, "The thin sheet of water thrown forward by plunging breakers." );
			}

			var wakeF = shoreTab.AddFolder( "Boat wake", "wave", false );
			wakeF.visibleIf = () => WakeSim.current != null;
			wakeF.AddSlider( "Wake height", () => WakeSim.current != null ? WakeSim.current.amplitude : 1, v => { if ( WakeSim.current != null ) WakeSim.current.amplitude = ( float ) v; }, 0, 2, 0.01 );
			wakeF.AddSlider( "Wake foam", () => WakeSim.current != null ? WakeSim.current.foamGain : 0.35, v => { if ( WakeSim.current != null ) WakeSim.current.foamGain = ( float ) v; }, 0, 1.5, 0.01 );
			if ( ocean != null && ocean.caustics != null )
			{
				var caus = ocean.caustics;
				var light = shoreTab.AddFolder( "Caustics", "sun", false );
				light.AddSlider( "Intensity", () => caus.strength, v => caus.strength = ( float ) v, 0, 2, 0.01 );
			}

			// ---------------------------------------------------------------- Sky
			var skyTab = ui.AddTab( "sky", "Sky", "sky" );
			var sun = skyTab.AddFolder( "Sun", "clock" );
			sun.AddTimeOfDay( () => game.clock.hour, v => game.clock.Set( v ) );
			sun.AddSlider( "Sun azimuth", () => DayNight.instance != null ? DayNight.instance.sunAzimuth : 0, v => { if ( DayNight.instance != null ) DayNight.instance.sunAzimuth = v; }, -180, 180, 1, "", false,
				v => Math.Round( v ).ToString( CultureInfo.InvariantCulture ) + "°", "Turns the sun's path around the island (0 = the real path: rises in the east, sets in the west)." );
			double timeSpeed = game.clock.timeSpeed != 0 ? game.clock.timeSpeed : 0.02;
			sun.AddToggle( "Advance time", () => game.clock.timeSpeed != 0, v => game.clock.timeSpeed = v ? timeSpeed : 0 );
			var speed = sun.AddSlider( "Time speed", () => timeSpeed, v => { timeSpeed = v; if ( game.clock.timeSpeed != 0 ) game.clock.timeSpeed = v; }, 0.002, 1, 0, "h/s", true );
			speed.visibleIf = () => game.clock.timeSpeed != 0;
			var atmo = skyTab.AddFolder( "Atmosphere", "cloud" );
			atmo.AddSlider( "Cloud cover", () => sea.c.cover, v => { sea.c.cover = v; G.cover = ( float ) v; }, 0, 1, 0.01, "", false, v => Math.Round( v * 100 ).ToString( CultureInfo.InvariantCulture ) + "%" );
			atmo.AddSlider( "Exposure", () => DayNight.instance != null ? DayNight.instance.exposureEV : 0, v => { if ( DayNight.instance != null ) { DayNight.instance.exposureEV = v; DayNight.instance.Apply(); } }, -3, 3, 0.1, "EV" );

			// ---------------------------------------------------------------- Camera
			var camTab = ui.AddTab( "camera", "Camera", "camera" );
			var view = camTab.AddFolder( "View", "camera" );
			if ( game.Player != null ) view.AddSelect( "Boat camera", new[] { "1st person", "3rd person" }, () => game.Player.cam.firstPerson ? 0 : 1, i => game.Player.cam.firstPerson = i == 0 );
			view.AddSlider( "Field of view", () => Camera.main != null ? Camera.main.fieldOfView : 60, v => { if ( Camera.main != null ) Camera.main.fieldOfView = ( float ) v; }, 35, 100, 1, "°" );
			view.AddButton( "Free camera (F)", () => { if ( host != null ) host.SetFreeCam( ! host.freeCam ); }, "camera" );

			// ---------------------------------------------------------------- Controls
			// The binding table (core/Bindings) is the single source of truth: every row edits it in place, and the prompts, the help sheet and the guide read their glyphs out of it.
			var controls = ui.AddTab( "controls", "Controls", "keyboard" );
			if ( bind != null )
			{
				var opts = bind.opts;
				void save() => ui.SaveSoon( bind.save );
				var pad = controls.AddFolder( "Controller", "gamepad" );
				pad.AddToggle( "Gamepad", () => opts.padEnabled, v => { opts.padEnabled = v; save(); }, "Read the connected controller. Off leaves the keyboard and mouse only." );
				pad.AddSlider( "Stick deadzone", () => opts.deadzone, v => { opts.deadzone = v; save(); }, 0, 0.4, 0.01, "", false, v => v.ToString( "F2", CultureInfo.InvariantCulture ), "How far a stick must be pushed before it reads as input." );
				pad.AddSlider( "Look sensitivity", () => opts.lookSensitivity, v => { opts.lookSensitivity = v; save(); }, 0.3, 3, 0.05, "×", false, null, "How fast the right stick turns the camera." );
				pad.AddToggle( "Invert look", () => opts.invertY, v => { opts.invertY = v; save(); }, "Push the stick up to look down." );
				pad.AddSlider( "Rumble", () => opts.rumble, v => { opts.rumble = v; save(); }, 0, 1, 0.05, "", false, v => v > 0 ? Math.Round( v * 100 ).ToString( CultureInfo.InvariantCulture ) + "%" : "Off" );
				pad.AddInfo( "Detected", () =>
				{
					string name = game.Input != null ? game.Input.padName : null;
					if ( string.IsNullOrEmpty( name ) ) return "No controller";
					return ( name.Length > 34 ? name.Substring( 0, 34 ) : name ) + " · " + ( game.Input.padLayout == "ps" ? "PlayStation" : "Xbox" ) + " layout";
				} );

				// one row per action, grouped as the help sheet groups them
				var groupIcon = new Dictionary<string, string> { { "Movement", "move" }, { "Fishing", "boat" }, { "Interact", "help" }, { "Interface", "sliders" } };
				var folders = new Dictionary<string, SFolder>();
				foreach ( var g in Bindings.GROUPS ) folders[ g ] = controls.AddFolder( g, groupIcon.TryGetValue( g, out var gi ) ? gi : "sliders", g == "Movement" );
				foreach ( var a in Bindings.ACTIONS )
				{
					if ( a.noRebind ) continue; // the look stick is not a button: it has sensitivity, not a binding
					( folders.TryGetValue( a.group, out var f ) ? ( SBox ) f : controls ).AddBinding( bind, a.id, a.label, () => game.Input != null ? game.Input.padLayout : "xbox" );
				}

				controls.AddButton( "Reset all controls", () => { bind.resetAll(); bind.save(); game.Toast( "Controls back to their defaults" ); }, "reset", "ghost" );
			}

			// ---------------------------------------------------------------- Effects
			var fx = SettingsFx.Create( game.transform );
			var fxTab = ui.AddTab( "effects", "Effects", "effects" );
			var post = fxTab.AddFolder( "Post-processing", "sparkles" );
			post.AddSlider( "Ambient occlusion", () => fx.ao, v => fx.ao = v, 0, 1.5, 0.01 );
			post.AddSlider( "Sharpen", () => fx.Sharpen, v => fx.Sharpen = v, 0, 1, 0.01, "", false, null, "Contrast-adaptive sharpening after the temporal anti-aliasing." );
			post.AddSlider( "Motion blur", () => fx.motionBlur, v => fx.motionBlur = v, 0, 1, 0.05, "", false, v => v > 0 ? Math.Round( v * 360 ).ToString( CultureInfo.InvariantCulture ) + "°" : "Off", "Camera and object motion blur, as a shutter angle (180° = film look). 0 turns it off." );
			post.AddSlider( "Bloom", () => fx.bloom, v => fx.bloom = v, 0, 0.3, 0.005 );
			post.AddSlider( "Lens flare", () => fx.flare, v => fx.flare = v, 0, 2, 0.05 );
			post.AddSlider( "Saturation", () => fx.saturation, v => fx.saturation = v, 0.5, 1.5, 0.01 );
			post.AddSlider( "Contrast", () => fx.contrast, v => fx.contrast = v, 0.8, 1.3, 0.01 );
			post.AddSlider( "Vignette", () => fx.vignette, v => fx.vignette = v, 0, 1, 0.01 );
			post.AddSlider( "Film grain", () => fx.grain, v => fx.grain = v, 0, 0.06, 0.001 );

			// ---------------------------------------------------------------- Performance
			var perf = ui.AddTab( "performance", "Performance", "performance" );
			var live = perf.AddFolder( "Live", "gauge" );
			live.AddInfo( "Frame rate", () => ( 1f / Mathf.Max( 1e-4f, Time.smoothDeltaTime ) ).ToString( "F0", CultureInfo.InvariantCulture ) + " fps" );
			live.AddInfo( "Frame time", () => ( Time.smoothDeltaTime * 1000 ).ToString( "F2", CultureInfo.InvariantCulture ) + " ms" );
			live.AddInfo( "Render size", () => RenderScale.SizeText() );
			var quality = perf.AddFolder( "Quality", "layers" );
			var rs = quality.AddSlider( "Render scale", () => RenderScale.scale, v => RenderScale.scale = ( float ) v, 0.5, 1, 0.05, "", false, v => Math.Round( v * 100 ).ToString( CultureInfo.InvariantCulture ) + "%", "Internal resolution; the upscaler reconstructs the full output resolution." );
			rs.visibleIf = () => RenderScale.Ready();
			quality.AddSelect( "Anti-aliasing", new[] { "Off", "TAA low", "TAA medium", "TAA high" }, () => fx.Aa, i => fx.Aa = i, "Temporal anti-aliasing: each pixel averages sub-pixel sample positions over successive frames (it also smooths dithered fades and shadow noise). Higher quality costs a little more per frame." );
			quality.AddToggle( "Shadows", () => DayNight.instance == null || DayNight.instance.shadows, v => { if ( DayNight.instance != null ) { DayNight.instance.shadows = v; DayNight.instance.Apply(); } } );
			if ( ocean != null ) quality.AddToggle( "Water reflections", () => ocean.ssr, v => ocean.ssr = v, "Screen-space reflections of the pier, boats and hills on the water." );
		}

		static Condition WithDir( Condition c, double dir ) { c.windDir = dir; return c; }
	}

	// The panel's post-processing: one global Volume above the scene's, its values the settings (JS ranges: PostFX.js params) mapped to HDRP's. Created with the values the scene
	// already renders with (read from the volume stack), so nothing changes until a slider moves.
	public sealed class SettingsFx
	{
		Volume vol; VolumeProfile prof;
		Bloom bloomC; Vignette vigC; FilmGrain grainC; ColorAdjustments adjC; MotionBlur mbC; ScreenSpaceAmbientOcclusion aoC; ScreenSpaceLensFlare flareC;
		HDAdditionalCameraData cam;

		// the values, in the JS units
		double _ao, _bloom, _flare, _sat = 1, _con = 1, _vig, _grain, _mb;

		public static SettingsFx Create( Transform parent )
		{
			var f = new SettingsFx();
			f.Build( parent );
			return f;
		}

		void Build( Transform parent )
		{
			var main = Camera.main;
			cam = main != null ? main.GetComponent<HDAdditionalCameraData>() : null;
			// what the scene's volumes (and the pipeline's defaults) give the camera now: a stack of its own, resolved before this panel's volume exists
			var stack = VolumeManager.instance.CreateStack();
			VolumeManager.instance.Update( stack, main != null ? main.transform : null, ~0 );

			var b0 = stack?.GetComponent<Bloom>(); var v0 = stack?.GetComponent<Vignette>(); var g0 = stack?.GetComponent<FilmGrain>(); var a0 = stack?.GetComponent<ColorAdjustments>();
			var m0 = stack?.GetComponent<MotionBlur>(); var o0 = stack?.GetComponent<ScreenSpaceAmbientOcclusion>(); var l0 = stack?.GetComponent<ScreenSpaceLensFlare>();
			_bloom = b0 != null && b0.active ? b0.intensity.value : 0;
			_vig = v0 != null && v0.active ? v0.intensity.value : 0;
			_grain = g0 != null && g0.active ? g0.intensity.value * 0.06 / 0.8 : 0;
			_sat = a0 != null && a0.active ? 1 + a0.saturation.value / 100 : 1;
			_con = a0 != null && a0.active ? 1 + a0.contrast.value / 100 : 1;
			_mb = m0 != null && m0.active ? Math.Min( 1, m0.intensity.value / 2 ) : 0;
			_ao = o0 != null && o0.active ? o0.intensity.value : 0;
			_flare = l0 != null && l0.active ? l0.intensity.value : 0;
			VolumeManager.instance.DestroyStack( stack );

			var go = new GameObject( "Settings Fx" ) { hideFlags = HideFlags.DontSave };
			go.transform.SetParent( parent, false );
			vol = go.AddComponent<Volume>();
			vol.isGlobal = true;
			vol.priority = 300;
			prof = ScriptableObject.CreateInstance<VolumeProfile>();
			prof.hideFlags = HideFlags.DontSave;
			bloomC = prof.Add<Bloom>( false ); bloomC.active = true;
			vigC = prof.Add<Vignette>( false ); vigC.active = true;
			grainC = prof.Add<FilmGrain>( false ); grainC.active = true;
			adjC = prof.Add<ColorAdjustments>( false ); adjC.active = true;
			mbC = prof.Add<MotionBlur>( false ); mbC.active = true;
			aoC = prof.Add<ScreenSpaceAmbientOcclusion>( false ); aoC.active = true;
			flareC = prof.Add<ScreenSpaceLensFlare>( false ); flareC.active = true;
			vol.sharedProfile = prof;
			// nothing is overridden until a slider moves: the scene's volumes keep every value they have
		}

		public double ao { get => _ao; set { _ao = value; if ( aoC != null ) aoC.intensity.Override( ( float ) value ); } }
		public double bloom { get => _bloom; set { _bloom = value; if ( bloomC != null ) bloomC.intensity.Override( ( float ) value ); } }
		public double flare { get => _flare; set { _flare = value; if ( flareC != null ) flareC.intensity.Override( ( float ) value ); } }
		public double vignette { get => _vig; set { _vig = value; if ( vigC != null ) vigC.intensity.Override( ( float ) value ); } }
		// JS: the grain's noise amplitude, 0..0.06; HDRP's intensity is 0..1 (the full range reads as the strongest grain the JS allows, a little under it)
		public double grain { get => _grain; set { _grain = value; if ( grainC != null ) { grainC.intensity.Override( ( float ) ( value / 0.06 * 0.8 ) ); grainC.type.Override( FilmGrainLookup.Thin1 ); } } }
		public double saturation { get => _sat; set { _sat = value; if ( adjC != null ) adjC.saturation.Override( ( float ) ( ( value - 1 ) * 100 ) ); } }
		public double contrast { get => _con; set { _con = value; if ( adjC != null ) adjC.contrast.Override( ( float ) ( ( value - 1 ) * 100 ) ); } }
		// JS: the shutter, 0..1 of a turn (0.5 = 180 degrees); HDRP's intensity 1 is a film shutter
		public double motionBlur { get => _mb; set { _mb = value; if ( mbC != null ) mbC.intensity.Override( ( float ) ( value * 2 ) ); } }

		public double Sharpen
		{
			get => cam != null ? cam.taaSharpenStrength : 0.5;
			set { if ( cam != null ) cam.taaSharpenStrength = ( float ) value; }
		}

		// 0 off, 1..3 TAA at low / medium / high quality
		public int Aa
		{
			get
			{
				if ( cam == null || cam.antialiasing != HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing ) return 0;
				return 1 + ( int ) cam.TAAQuality;
			}
			set
			{
				if ( cam == null ) return;
				if ( value <= 0 ) { cam.antialiasing = HDAdditionalCameraData.AntialiasingMode.None; return; }
				cam.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
				cam.TAAQuality = ( HDAdditionalCameraData.TAAQualityLevel ) ( value - 1 );
			}
		}
	}

	// the internal resolution (HDRP dynamic resolution held at one scale: the JS app.setRenderScale); available only where the pipeline asset has dynamic resolution on
	public static class RenderScale
	{
		public static float scale = 1;
		static bool tried, ready;

		public static bool Ready()
		{
			if ( tried ) return ready;
			tried = true;
			var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
			var cam = Camera.main != null ? Camera.main.GetComponent<HDAdditionalCameraData>() : null;
			if ( asset == null || cam == null || ! asset.currentPlatformRenderPipelineSettings.dynamicResolutionSettings.enabled ) return false;
			cam.allowDynamicResolution = true;
			DynamicResolutionHandler.SetDynamicResScaler( () => scale * 100f, DynamicResScalePolicyType.ReturnsPercentage );
			ready = true;
			return true;
		}

		public static string SizeText()
		{
			var cam = Camera.main;
			if ( cam == null ) return "—";
			float s = Ready() ? scale : 1;
			return Mathf.RoundToInt( cam.pixelWidth * s ) + " × " + Mathf.RoundToInt( cam.pixelHeight * s );
		}
	}
}
