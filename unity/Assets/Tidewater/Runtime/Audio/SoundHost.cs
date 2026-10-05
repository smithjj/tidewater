using System;
using Tidewater.Core;
using Tidewater.Ocean;
using Tidewater.Player;
using Tidewater.Util;
using Tidewater.World;
using UnityEngine;
using Vec3 = Tidewater.Engine.Vector3;

// Owns the island's sound: the backend (AudioSources), the SoundScape (the mixer's decisions) and the end of the chain on the camera, and gives it
// the game's state every frame (App.updateAudio). Built by PlayerHost once the player exists (PlayerHost.noAudio is the JS ?noAudio).
namespace Tidewater.Audio
{
	public sealed class SoundHost : MonoBehaviour
	{
		public SoundScape scape { get; private set; }
		public UnitySoundBackend backend { get; private set; }
		MasterOut master;
		readonly ShoreSource shore = new ShoreSource();
		readonly SoundState state = new SoundState();
		bool shoreAttached;

		sealed class TerrainAdapter : IAudioTerrain
		{
			readonly Tidewater.World.TerrainData t;
			public TerrainAdapter( Tidewater.World.TerrainData t ) { this.t = t; }
			public double origin => t.origin;
			public double size => t.size;
			public double heightAt( double x, double z ) => t.HeightAt( x, z );
			public double coastDistance( double x, double z ) => t.CoastDistance( x, z ).d;
		}

		public static SoundHost Create( PlayerHost host )
		{
			var old = UnityEngine.Object.FindAnyObjectByType<SoundHost>();
			if ( old != null ) UnityEngine.Object.DestroyImmediate( old.gameObject );
			var go = new GameObject( "SoundHost" ) { hideFlags = HideFlags.DontSave };
			var h = go.AddComponent<SoundHost>();
			h.Build( host );
			return h;
		}

		public bool Alive => this != null && backend != null && backend.Alive;

		void Build( PlayerHost host )
		{
			var cam = Camera.main;
			var listener = UnityEngine.Object.FindAnyObjectByType<AudioListener>();
			if ( listener == null && cam != null ) listener = cam.gameObject.AddComponent<AudioListener>();
			var lg = listener != null ? listener.gameObject : gameObject;
			master = lg.GetComponent<MasterOut>() ?? lg.AddComponent<MasterOut>();
			backend = new UnitySoundBackend( transform, master );
			scape = new SoundScape( backend );
			if ( host.terrainData != null ) scape.terrain = new TerrainAdapter( host.terrainData );
			scape.whale = () => Tidewater.World.Marine.WhaleView.instance != null && Tidewater.World.Marine.WhaleView.instance.whale != null ? Tidewater.World.Marine.WhaleView.instance.audio : null;
			scape.start();
		}

		void OnDestroy() { backend?.Dispose(); }

		// App.updateAudio: the listener, the water, the shore, the wind, the light, the active boat
		public void Tick( double dt, PlayerHost host )
		{
			if ( ! Alive ) return;
			var s = state;
			var cam = host.simCamera; var p = cam.position;
			var f = cam.viewDir( new Vec3() );
			var up = new Vec3( 0, 1, 0 ).applyQuaternion( cam.quaternion );
			double h = G.cameraWaterHeight;
			var terrain = host.terrainData;
			double coast = terrain != null ? terrain.CoastDistance( p.x, p.z ).d : double.NaN;
			var ocean = OceanRenderer.instance;
			var sw = ocean != null ? ocean.shore : null;
			s.lx = p.x; s.ly = p.y; s.lz = p.z; s.fx = f.x; s.fy = f.y; s.fz = f.z; s.ux = up.x; s.uy = up.y; s.uz = up.z;
			s.underwater = p.y < h ? 1 : 0;
			s.depthBelowSurface = Math.Max( 0, h - p.y );
			s.surfIntensity = sw != null ? Math.Min( 1, sw.amplitude / 0.6 ) : double.NaN;
			s.distanceToShore = Math.Abs( coast );
			s.coastDistance = coast;
			s.windSpeed = G.windSpeed;
			s.daylight = 1 - G.night;
			s.nearPier = Math.Abs( p.x - WorldLayout.Pier.x ) < 12 && p.z > WorldLayout.Pier.zStart - 5 && p.z < WorldLayout.Pier.zEnd + 8;
			s.timeOfDay = host.game != null ? host.game.clock.hour : double.NaN;

			// the boat everything player-facing follows: the one you are aboard, else the lobster boat (App.boatCtl). Its position, too:
			// the JS gives the lobster boat's model position whichever boat is active, which is wrong once there are three
			var pl = host.player;
			var boat = pl != null && ( pl.mode == "boat" || pl.mode == "deck" ) && pl.boat != null ? pl.boat : host.driver.controller;
			var b = s.boat;
			b.active = boat.driven; b.rpm = boat.rpm; b.throttle = boat.throttle; b.speed = boat.velocity.length();
			b.x = boat.position.x; b.y = boat.position.y; b.z = boat.position.z;
			b.listenerInside = pl != null && pl.mode == "boat" && pl.cam.firstPerson;

			// the shore waves' numbers (the surf is wave by wave: the phase comes from the same clock the rendering uses)
			var tr = UnityEngine.Object.FindAnyObjectByType<TerrainRenderer>();
			if ( ! shoreAttached && sw != null && tr != null && tr.shoreField != null && terrain != null )
			{
				shore.fieldData = tr.shoreField.data; shore.fieldRes = tr.shoreField.res; shore.terrain = new TerrainAdapter( terrain );
				scape.attachShore( shore );
				shoreAttached = true;
			}

			if ( sw != null )
			{
				shore.enabled = sw.enabled; shore.amplitude = sw.amplitude; shore.variation = sw.variation; shore.period = sw.period; shore.gamma = sw.gamma;
				shore.time = sw.phase * sw.period;
			}

			backend.Frame( dt );
			scape.update( dt, s );
		}

		// App.js: the mute action
		public void ToggleMute( PlayerHost host )
		{
			scape.setMuted( ! scape.muted );
			host.Toast( scape.muted ? "Sound off" : "Sound on" );
		}
	}
}
