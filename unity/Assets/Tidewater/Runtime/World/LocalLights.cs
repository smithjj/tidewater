using System;
using System.Collections.Generic;
using Tidewater.Core;
using Tidewater.Util;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using EVector3 = Tidewater.Engine.Vector3;
using EColor = Tidewater.Engine.Color;

// Port of src/materials/LocalLights.js: lanterns, lamp posts, path lights and lit windows.
//
// The JS packs the MAX lights nearest the camera each frame into three uniform arrays that every lit material loops over. HDRP already
// lights every material (terrain, boats, village, water) from its own light list, so here the same selection drives a pool of MAX real
// HDRP lights: the same sources, the same nearest-first pick, the same fade of the farthest selected light as it approaches the cut,
// dusk ramp, flicker, range and spot cones; no shadows. What differs: HDRP's own falloff window and spot profile stand in for the
// hand written ones (inverse square and a smooth range window either way), and a spot is at most 179 degrees (the window lights'
// outer cone is 197 in the JS).
// Units: a source's `intensity` is the JS "illuminance at 1 m in scene units"; the scene's sun is SUN_ILLUMINANCE (11) there and
// the HDRP sun is SUN_LUX, so one scene unit is SUN_LUX / 11 lux, which is also candela for a point light at 1 m.
// The handheld flashlight (slot 0 of the JS pool, so the lamps get MAX - 1 slots while it is on) is one more HDRP spot light, and
// addBoatLights puts the boat's cabin dome, instrument glow and navigation lights in the hull frame.
// The torch's beam in-scatter under water is in the underwater composite (it reads the _TWFlash* globals published here); the marine snow
// is not ported.
namespace Tidewater.World
{
	public sealed class LocalLightSource
	{
		public EVector3 position;       // live (sim space): lanterns swing
		public EColor color;
		public double intensity;
		public double range;
		public string kind;
		public EVector3 dir;            // spot axis (sim), null: point light
		public double? cosInner, cosOuter;
		public double flicker;
		public double phase = double.NaN;
		public double scale = 1;
		public bool enabled = true;
		public Action update;
		public double d2;
	}

	// the handheld torch (LocalLights.js `flashlight`)
	public sealed class Flashlight
	{
		public bool on, primed;
		public double intensity = 55, range = 30;
		public EColor color = new EColor( 1.0, 0.71, 0.49 ); // ~4500 K
		public double cosInner = Math.Cos( 9 * Math.PI / 180 ), cosOuter = Math.Cos( 35 * Math.PI / 180 );
		public readonly EVector3 position = new EVector3(), dir = new EVector3( 0, 0, - 1 );
		public double k; // intensity x the daylight factor of the last update
	}

	public sealed class LocalLights
	{
		public const int MAX = 8;
		// HDRP lux of the scene's sun / its JS illuminance (SUN_ILLUMINANCE in sky/Atmosphere.js): the lux of one scene unit
		public const double SUN_LUX = 130000, SUN_ILLUMINANCE = 11;
		public const double LUX_PER_UNIT = SUN_LUX / SUN_ILLUMINANCE;

		public readonly List<LocalLightSource> sources = new List<LocalLightSource>();
		public bool enabled = true;
		public double strength = 1; // UI multiplier for the lamps (not the flashlight)
		public readonly Flashlight flashlight = new Flashlight();
		public double time;
		[System.NonSerialized] public int active;
		readonly List<LocalLightSource> _list = new List<LocalLightSource>();
		// the selection of the last update (what the pool shows): source, scale k (intensity x dusk x fade x flicker)
		public readonly List<(LocalLightSource src, double k)> selected = new List<(LocalLightSource, double)>();
		static readonly System.Random _rand = new System.Random( 1 );

		static double smooth( double x, double a, double b )
		{
			double t = Math.Min( 1, Math.Max( 0, ( x - a ) / ( b - a ) ) );
			return t * t * ( 3 - 2 * t );
		}

		public bool toggleFlashlight( bool? on = null )
		{
			flashlight.on = on ?? ! flashlight.on;
			flashlight.primed = false;
			return flashlight.on;
		}

		static readonly EVector3 _r = new EVector3(), _u = new EVector3(), _f = new EVector3();

		// intensity: luminous intensity in scene units (illuminance at 1 m); range: cut-off distance (m)
		public LocalLightSource add( LocalLightSource src )
		{
			if ( double.IsNaN( src.phase ) ) src.phase = _rand.NextDouble() * 100;
			sources.Add( src );
			return src;
		}

		// night: G.night (0 by day .. 1); cam: the camera position and right / up / forward axes (sim space); dt: the frame time
		public void update( EVector3 cam, EVector3 right, EVector3 up, EVector3 fwd, double night, double t, double dt )
		{
			time = t;
			selected.Clear();
			int n = 0;
			var fl = flashlight;
			if ( fl.on && enabled )
			{
				// held a little right of and below the eye, lagging the view slightly
				_r.copy( right ).normalize(); _u.copy( up ).normalize(); _f.copy( fwd ).normalize();
				if ( ! fl.primed ) { fl.dir.copy( _f ); fl.primed = true; }
				fl.dir.lerp( _f, 1 - Math.Exp( - dt / 0.06 ) ).normalize();
				fl.position.copy( cam ).addScaledVector( _r, 0.2 ).addScaledVector( _u, - 0.22 ).addScaledVector( _f, 0.15 );
				// faint against daylight (the scene's night is exposed far brighter than physical)
				fl.k = fl.intensity * ( 0.08 + 0.92 * smooth( night, 0.0, 0.6 ) );
				n = 1;
			}

			// lamps: on from dusk (same ramp as the lantern glass), nearest first
			double on = smooth( night, 0.15, 0.75 ) * strength;
			if ( on > 0.002 && enabled )
			{
				var list = _list;
				list.Clear();
				foreach ( var s in sources )
				{
					if ( ! s.enabled ) continue;
					s.update?.Invoke();
					s.d2 = s.position.distanceToSquared( cam );
					list.Add( s );
				}

				list.Sort( ( a, b ) => a.d2.CompareTo( b.d2 ) );
				int slots = MAX - n;
				// fade out the farthest selected lights as they approach the cut (no popping)
				double dCut = list.Count > slots ? Math.Sqrt( list[ slots ].d2 ) : double.PositiveInfinity;
				for ( int i = 0; i < Math.Min( slots, list.Count ); i ++ )
				{
					var s = list[ i ];
					double fade = ! double.IsInfinity( dCut ) ? smooth( Math.Sqrt( s.d2 ), dCut, dCut * 0.8 ) : 1;
					double fl2 = s.flicker != 0 ? 1 + s.flicker * Math.Sin( time * 9 + s.phase ) * Math.Sin( time * 5.3 + s.phase * 0.37 ) : 1;
					double k = s.intensity * on * fade * fl2 * s.scale;
					if ( k <= 1e-4 ) continue;
					selected.Add( ( s, k ) );
				}
			}

			n += selected.Count;
			active = n;
		}

		// Boat lights in the hull frame: warm cabin dome, instrument glow, navigation lights (red port, green starboard, white masthead and
		// stern). Positions follow the boat's pose (the controller's, in sim space).
		public List<LocalLightSource> addBoatLights( Tidewater.World.Boat.HullLines L, Tidewater.Player.BoatController boat )
		{
			double sternY = L.sheerY( 0 ) + 0.1;
			var defs = new[]
			{
				( p: new[] { 0, 2.24, 0.42 }, color: new[] { 1.0, 0.84, 0.62 }, intensity: 0.9, range: 5.5, kind: "boatDome", side: ( double[] ) null ),
				( p: new[] { - 0.45, 1.5, 1.12 }, color: new[] { 0.45, 0.75, 1.0 }, intensity: 0.12, range: 2.2, kind: "boatInstruments", side: null ),
				( p: new[] { 0.47, 3.45, - 0.47 }, color: new[] { 1.0, 0.06, 0.03 }, intensity: 1.4, range: 8.0, kind: "boatNav", side: new[] { 1.0, 0, 0 } ),
				( p: new[] { - 0.47, 3.45, - 0.47 }, color: new[] { 0.05, 1.0, 0.3 }, intensity: 1.4, range: 8.0, kind: "boatNav", side: new[] { - 1.0, 0, 0 } ),
				( p: new[] { 0, 3.97, - 0.5 }, color: new[] { 1.0, 0.95, 0.85 }, intensity: 1.2, range: 8.0, kind: "boatNav", side: null ),
				( p: new[] { 0, sternY, L.zAft - 0.05 }, color: new[] { 1.0, 0.95, 0.85 }, intensity: 0.8, range: 7.0, kind: "boatNav", side: null ),
			};
			var outList = new List<LocalLightSource>();
			foreach ( var d in defs )
			{
				var local = new EVector3( d.p[ 0 ], d.p[ 1 ], d.p[ 2 ] );
				var localDir = d.side != null ? new EVector3( d.side[ 0 ], d.side[ 1 ], d.side[ 2 ] ) : null;
				var src = new LocalLightSource
				{
					position = new EVector3(), color = new EColor( d.color[ 0 ], d.color[ 1 ], d.color[ 2 ] ), intensity = d.intensity, range = d.range, kind = d.kind,
					dir = localDir != null ? new EVector3() : null, cosInner = localDir != null ? 0.25 : ( double? ) null, cosOuter = localDir != null ? - 0.2 : ( double? ) null,
				};
				src.update = () =>
				{
					boat.toWorld( local, src.position );
					if ( localDir != null ) src.dir.copy( localDir ).applyQuaternion( boat.quaternion );
				};
				src.update();
				outList.Add( add( src ) );
			}

			return outList;
		}

		// Village lights (Village.getLightSources()): lanterns, lamp posts, path lights, lit windows. Window lights sit just outside the
		// pane and only light outward (porches); lanterns flicker a little.
		public void addVillageLights( Village.Village village )
		{
			var K = new Dictionary<string, double> { { "lantern", 1.5 }, { "pathLight", 1.0 }, { "window", 0.8 } };
			var R = new Dictionary<string, double> { { "lantern", 11 }, { "pathLight", 7 }, { "window", 6 } };
			foreach ( var l in village.lights )
			{
				string kind = l.kind ?? "lantern";
				var src = new LocalLightSource
				{
					position = l.position, color = l.color,
					intensity = l.intensity * ( K.TryGetValue( kind, out var kk ) ? kk : 1.2 ),
					range = R.TryGetValue( kind, out var rr ) ? rr : 12, kind = kind,
				};
				if ( kind == "window" )
				{
					// outward normal: away from the nearest building centre
					Village.BuildingInfo best = null; double bd = double.PositiveInfinity;
					foreach ( var b in village.buildings )
					{
						double dd = ( b.x - l.position.x ) * ( b.x - l.position.x ) + ( b.z - l.position.z ) * ( b.z - l.position.z );
						if ( dd < bd ) { bd = dd; best = b; }
					}

					var n = best != null ? new EVector3( l.position.x - best.x, 0, l.position.z - best.z ) : new EVector3( 0, 0, 1 );
					if ( n.lengthSq() < 1e-6 ) n.set( 0, 0, 1 );
					n.normalize();
					src.position = l.position.clone().addScaledVector( n, 0.35 );
					src.dir = n.clone();
					src.cosInner = 0.35;
					src.cosOuter = - 0.15;
				}
				else
				{
					src.flicker = 0.08;
				}

				add( src );
			}
		}
	}

	// Drives a LocalLights and shows its selection with the pool of HDRP lights. Updates just before each game camera is culled
	// (RenderPipelineManager.beginCameraRendering), so it also works in the Editor when nothing ticks Update.
	[ExecuteAlways]
	public sealed class LocalLightsView : MonoBehaviour
	{
		// the one in the scene (PlayerHost toggles the flashlight through it)
		public static LocalLightsView instance { get; private set; }
		[System.NonSerialized] public LocalLights lights;
		Tidewater.World.Village.Village villageSeen;
		Tidewater.Player.BoatController boatSeen;
		Light flash;
		readonly Light[] point = new Light[ LocalLights.MAX ];
		readonly Light[] spot = new Light[ LocalLights.MAX ];
		public int active;

		// the torch remembers what it was left as (the controls option); it stays as set while the lights are rebuilt
		bool flashOn = true;

		void OnEnable()
		{
			instance = this;
			RenderPipelineManager.beginCameraRendering += OnBeginCamera;
		}

		void OnDisable()
		{
			RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
			if ( instance == this ) instance = null;
			DestroyPool();
			lights = null; villageSeen = null; boatSeen = null;
		}

		// L: the flashlight (App.js); returns whether it is on now
		public bool ToggleFlashlight( bool? on = null )
		{
			flashOn = lights != null ? lights.toggleFlashlight( on ) : ( on ?? ! flashOn );
			return flashOn;
		}

		public bool flashlightOn => flashOn;

		void DestroyPool()
		{
			for ( int i = 0; i < LocalLights.MAX; i ++ )
			{
				if ( point[ i ] != null ) DestroyImmediate( point[ i ].gameObject );
				if ( spot[ i ] != null ) DestroyImmediate( spot[ i ].gameObject );
				point[ i ] = spot[ i ] = null;
			}

			if ( flash != null ) DestroyImmediate( flash.gameObject );
			flash = null;
		}

		Light MakeLight( string name, LightType type )
		{
			var go = new GameObject( name ) { hideFlags = HideFlags.HideAndDontSave };
			go.transform.SetParent( transform, false );
			var l = go.AddComponent<Light>();
			l.type = type;
			var hd = go.AddComponent<HDAdditionalLightData>();
			HDAdditionalLightData.InitDefaultHDAdditionalLightData( hd );
			l.lightUnit = LightUnit.Candela;
			l.shadows = LightShadows.None;
			l.enabled = false;
			return l;
		}

		static Color Linear( EColor c, double k ) => new Color( ( float ) ( c.r * k ), ( float ) ( c.g * k ), ( float ) ( c.b * k ) );

		void Sync( Tidewater.World.Village.Village village, Tidewater.Player.BoatController boat )
		{
			if ( village == villageSeen && boat == boatSeen && lights != null ) return;
			villageSeen = village; boatSeen = boat;
			lights = new LocalLights();
			if ( village != null ) lights.addVillageLights( village );
			if ( boat != null && boat.boatModel != null ) lights.addBoatLights( boat.boatModel.lines, boat );
			lights.toggleFlashlight( flashOn );
		}

		// sim-space axes of a Unity transform (the world mirrors z: directions map linearly)
		static EVector3 SimDir( Vector3 d ) => new EVector3( d.x, d.y, - d.z );

		void OnBeginCamera( ScriptableRenderContext ctx, Camera cam )
		{
			if ( cam.cameraType != CameraType.Game || Tidewater.Util.Studio.Is( cam ) ) return;
			var terrain = FindAnyObjectByType<TerrainRenderer>();
			var driver = FindAnyObjectByType<Tidewater.Player.BoatDriver>();
			Sync( terrain != null ? terrain.village : null, driver != null ? driver.controller : null );
			if ( lights == null ) return;

			var t = cam.transform;
			var p = Sim.FromUnity( t.position );
			// outside play mode nothing advances G.time: no lag on the torch
			if ( ! Application.isPlaying ) lights.flashlight.primed = false;
			lights.update( new EVector3( p.x, p.y, p.z ), SimDir( t.right ), SimDir( t.up ), SimDir( t.forward ), G.night, G.time, G.dt );
			Show();
		}

		void Show()
		{
			var sel = lights.selected;
			active = lights.active;
			ShowFlash();
			for ( int i = 0; i < LocalLights.MAX; i ++ )
			{
				if ( point[ i ] == null ) point[ i ] = MakeLight( "local-point-" + i, LightType.Point );
				if ( spot[ i ] == null ) spot[ i ] = MakeLight( "local-spot-" + i, LightType.Spot );
				if ( i >= sel.Count ) { point[ i ].enabled = false; spot[ i ].enabled = false; continue; }

				var ( s, k ) = sel[ i ];
				bool isSpot = s.dir != null;
				var l = isSpot ? spot[ i ] : point[ i ];
				( isSpot ? point[ i ] : spot[ i ] ).enabled = false;
				l.transform.position = Sim.ToUnity( s.position.x, s.position.y, s.position.z );
				// colour x intensity (scene units) -> candela; the colour is the tint (Light.color is gamma)
				double maxc = Math.Max( s.color.r, Math.Max( s.color.g, s.color.b ) );
				l.color = Linear( s.color, 1.0 / Math.Max( maxc, 1e-6 ) ).gamma;
				l.intensity = ( float ) ( maxc * k * LocalLights.LUX_PER_UNIT );
				l.range = ( float ) s.range;
				if ( isSpot )
				{
					var d = Sim.DirToUnity( new Vector3( ( float ) s.dir.x, ( float ) s.dir.y, ( float ) s.dir.z ) );
					l.transform.rotation = Quaternion.LookRotation( d );
					// cos cones -> full angles (HDRP spot angle < 180)
					double outer = Math.Acos( Math.Max( -1, Math.Min( 1, s.cosOuter ?? -2 ) ) ) * 2 * Mathf.Rad2Deg;
					double inner = Math.Acos( Math.Max( -1, Math.Min( 1, s.cosInner ?? -1.5 ) ) ) * 2 * Mathf.Rad2Deg;
					l.spotAngle = ( float ) Math.Min( outer, 179 );
					l.innerSpotAngle = ( float ) Math.Min( inner, outer * 0.95 );
				}

				l.enabled = true;
			}
		}

		// the flashlight: a spot of 9 / 35 degrees (half angles), held right of and below the eye
		void ShowFlash()
		{
			if ( flash == null ) flash = MakeLight( "local-flash", LightType.Spot );
			var fl = lights.flashlight;
			if ( ! fl.on || ! lights.enabled ) { flash.enabled = false; Shader.SetGlobalVector( "_TWFlashPos", Vector4.zero ); return; }
			double maxc = Math.Max( fl.color.r, Math.Max( fl.color.g, fl.color.b ) );
			flash.transform.position = Sim.ToUnity( fl.position.x, fl.position.y, fl.position.z );
			flash.transform.rotation = Quaternion.LookRotation( Sim.DirToUnity( new Vector3( ( float ) fl.dir.x, ( float ) fl.dir.y, ( float ) fl.dir.z ) ) );
			flash.color = Linear( fl.color, 1.0 / Math.Max( maxc, 1e-6 ) ).gamma;
			flash.intensity = ( float ) ( maxc * fl.k * LocalLights.LUX_PER_UNIT );
			flash.range = ( float ) fl.range;
			double outer = Math.Acos( fl.cosOuter ) * 2 * Mathf.Rad2Deg, inner = Math.Acos( fl.cosInner ) * 2 * Mathf.Rad2Deg;
			flash.spotAngle = ( float ) outer;
			flash.innerSpotAngle = ( float ) inner;
			flash.enabled = true;

			// the beam in-scatter under water (post/Underwater.js reads FLASH): position (sim) + on, direction, colour x lux, cos cone
			double lux = fl.k * LocalLights.LUX_PER_UNIT;
			Shader.SetGlobalVector( "_TWFlashPos", new Vector4( ( float ) fl.position.x, ( float ) fl.position.y, ( float ) fl.position.z, 1 ) );
			Shader.SetGlobalVector( "_TWFlashDir", new Vector4( ( float ) fl.dir.x, ( float ) fl.dir.y, ( float ) fl.dir.z, 0 ) );
			Shader.SetGlobalVector( "_TWFlashCol", new Vector4( ( float ) ( fl.color.r * lux ), ( float ) ( fl.color.g * lux ), ( float ) ( fl.color.b * lux ), 0 ) );
			Shader.SetGlobalVector( "_TWFlashCone", new Vector4( ( float ) fl.cosInner, ( float ) fl.cosOuter, 0, 0 ) );
		}
	}
}
