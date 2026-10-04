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
// Not ported: the handheld flashlight (slot 0 of the JS pool) and the boat's lights (addBoatLights).
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

	public sealed class LocalLights
	{
		public const int MAX = 8;
		// HDRP lux of the scene's sun / its JS illuminance (SUN_ILLUMINANCE in sky/Atmosphere.js): the lux of one scene unit
		public const double SUN_LUX = 130000, SUN_ILLUMINANCE = 11;
		public const double LUX_PER_UNIT = SUN_LUX / SUN_ILLUMINANCE;

		public readonly List<LocalLightSource> sources = new List<LocalLightSource>();
		public bool enabled = true;
		public double strength = 1; // UI multiplier for the lamps
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

		// intensity: luminous intensity in scene units (illuminance at 1 m); range: cut-off distance (m)
		public LocalLightSource add( LocalLightSource src )
		{
			if ( double.IsNaN( src.phase ) ) src.phase = _rand.NextDouble() * 100;
			sources.Add( src );
			return src;
		}

		// night: G.night (0 by day .. 1); cam: the camera position (sim space)
		public void update( EVector3 cam, double night, double t )
		{
			time = t;
			selected.Clear();
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
				int slots = MAX;
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

			active = selected.Count;
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
		[System.NonSerialized] public LocalLights lights;
		Tidewater.World.Village.Village villageSeen;
		readonly Light[] point = new Light[ LocalLights.MAX ];
		readonly Light[] spot = new Light[ LocalLights.MAX ];
		public int active;

		void OnEnable()
		{
			RenderPipelineManager.beginCameraRendering += OnBeginCamera;
		}

		void OnDisable()
		{
			RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
			DestroyPool();
			lights = null; villageSeen = null;
		}

		void DestroyPool()
		{
			for ( int i = 0; i < LocalLights.MAX; i ++ )
			{
				if ( point[ i ] != null ) DestroyImmediate( point[ i ].gameObject );
				if ( spot[ i ] != null ) DestroyImmediate( spot[ i ].gameObject );
				point[ i ] = spot[ i ] = null;
			}
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

		void Sync( Tidewater.World.Village.Village village )
		{
			if ( village == villageSeen && lights != null ) return;
			villageSeen = village;
			lights = new LocalLights();
			if ( village != null ) lights.addVillageLights( village );
		}

		void OnBeginCamera( ScriptableRenderContext ctx, Camera cam )
		{
			if ( cam.cameraType != CameraType.Game ) return;
			var terrain = FindAnyObjectByType<TerrainRenderer>();
			Sync( terrain != null ? terrain.village : null );
			if ( lights == null ) return;

			var p = Sim.FromUnity( cam.transform.position );
			lights.update( new EVector3( p.x, p.y, p.z ), G.night, G.time );
			Show();
		}

		void Show()
		{
			var sel = lights.selected;
			active = sel.Count;
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
	}
}
