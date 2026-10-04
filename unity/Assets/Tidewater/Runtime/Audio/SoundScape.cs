using System;
using System.Collections.Generic;
using Tidewater.Engine;
using Tidewater.Game;
using Tidewater.Player;
using Tidewater.World;
using Vector3 = Tidewater.Engine.Vector3;

// Port of src/audio/SoundScape.js (the mixer's decisions): every level, filter, rate, timing and random draw, in the same order as the JS, so
// the same seeded generator gives the same event stream (unity/tools/dump-soundscape.mjs, Editor/SoundScapeOracle.cs). What plays them is the
// ISoundBackend (UnityBackend.cs; Web Audio nodes in the JS): this class never touches an AudioSource.
//
// Sample-based sound for the island: real field recordings only (Resources/audio, sources and licences in CREDITS.md). Boat, underwater, pier
// and night sounds load the first time they become audible.
//
// Surf is wave by wave, driven by the game's own shore waves (ShoreWaves): a CPU mirror of their phase (the travel-time field + the per-wave
// heights) at a row of stations along the waterline near the listener tells when each wave breaks (a crash at its break point), when its bore
// reaches the sand (the wash running up) and when the swash turns (the backwash draining down), each a random slice of real recordings,
// placed where it happens. A quiet distant-surf bed carries the sound further.
//
// Birds: songbirds and doves sing short bouts from random perches in the island's trees near the listener (none on the open sea; more
// inland), by time of day - a dawn chorus (plus a diffuse chorus bed), a midday lull, sparse at dusk, silent at night. Gulls and terns call
// from the wildlife's real birds near the listener. The humpback: its song at the whale (clear underwater, faint and dull from above), and
// one-shots on its real events - blow, breach, re-entry, fluke-up dive - all only within 100 m of it.
//
// Signal flow (the backend builds it)
//   one-shots + beds above water -> above -> muffle low-pass x2 -> aboveOut -> master
//     surf events -> placed (+ air absorption) -> above;  engine -> engine LP -> boatSum;  hull / rush / slaps -> boatSum
//     boatSum -> placed at the boat (from outside) or unplaced (at the helm);  pier lapping -> placed under the pier;  footsteps -> above
//     birds, gulls, terns, whale blow / splashes -> placed -> above
//   whale song -> placed at the whale -> song LP + gain (open underwater) -> master
//   underwater bed + strokes -> under (x underwater);  submerge / emerge -> near (unfiltered)
//
// Levels: every sound has a target loudness (LUFS, momentary) for its reference situation in MIX below; the gain is target minus the file's
// measured loudness (SoundBank.cs).
namespace Tidewater.Audio
{
	// where a sound goes (the Web Audio nodes SoundScape.js connects to)
	public enum Dest { Above, Under, Near, Foot, Rod, BoatSum, SurfFar, WindLP, EngineLP, PierPan, SongPan, Air }
	// the smoothed parameters (a setTargetAtTime in the JS)
	public enum Par { Master, AboveOut, Under, Muffle0, Muffle1, WindLP, EngineLP, BoatIn, BoatOut, SongLP, SongOut }
	// the placed sources
	public enum Pan { Surf, Boat, Pier, Song }

	public sealed class ClipInfo { public double duration; public int channels; }

	public struct Spatial { public double x, y, z, refDistance, rolloff, airHz; }

	public sealed class Voice
	{
		public object h;               // the backend's
		public double end;
		public Action onEnded;         // set by the SoundScape; the backend calls it when the voice has finished
	}

	public interface ISoundBackend
	{
		double Now { get; }
		bool Running { get; }
		// the clip's facts once it has loaded; null until then (starts loading)
		ClipInfo Want( string name );
		void Listener( double lx, double ly, double lz, double fx, double fy, double fz, double ux, double uy, double uz );
		void Ramp( Par p, double v, double tau );
		void Pos( Pan p, double x, double y, double z );
		object StartBed( string name, Dest dest, double at, double offset, double rate );
		void BedGain( object bed, double g, double tau );
		void BedRate( object bed, double rate, double tau );
		Voice Play( string bank, Dest dest, bool placed, Spatial sp, string cat, int slice, double start, double dur, double gain, double rate, double t );
		void Fade( Voice v );
		void Dispose();
	}

	public interface IAudioTerrain
	{
		double origin { get; }
		double size { get; }
		double heightAt( double x, double z );
		double coastDistance( double x, double z ); // signed: > 0 water, < 0 land
	}

	// the game's shore waves as the sound reads them (the host fills it every frame; JS: ShoreWaves uniforms + the shore field)
	public sealed class ShoreSource
	{
		public double enabled = 1, amplitude, variation, period, time, gamma;
		public float[] fieldData; public int fieldRes;
		public IAudioTerrain terrain;
	}

	public sealed class WhaleBrain { public double x, y, z; public string state; public double water, yaw, blow, flukeUp, breaches, splashes; }
	public sealed class FlockBird { public string kind; public bool visible = true; public double x, y, z; }

	public sealed class SoundBoat
	{
		public bool active, listenerInside;
		public double rpm = double.NaN, throttle = double.NaN, speed = double.NaN, x = double.NaN, y = double.NaN, z = double.NaN;
	}

	// what the game tells the sound every frame (App.updateAudio); NaN = not given
	public sealed class SoundState
	{
		public double lx = double.NaN, ly = double.NaN, lz = double.NaN, fx = double.NaN, fy = double.NaN, fz = double.NaN, ux = double.NaN, uy = double.NaN, uz = double.NaN;
		public double underwater = double.NaN, depthBelowSurface = double.NaN, surfIntensity = double.NaN, distanceToShore = double.NaN, coastDistance = double.NaN;
		public double windSpeed = double.NaN, daylight = double.NaN, timeOfDay = double.NaN;
		public bool nearPier;
		public SoundBoat boat = new SoundBoat();
	}

	public sealed class SoundScape : IPlayerAudio, IGameAudio
	{
		static double clamp( double v, double a, double b ) => v < a ? a : v > b ? b : v;
		static double lerp( double a, double b, double t ) => a + ( b - a ) * t;
		static double num( double v, double d ) => double.IsNaN( v ) || double.IsInfinity( v ) ? d : v;
		static double smooth( double a, double b, double x )
		{
			double t = clamp( ( x - a ) / ( b - a ), 0, 1 );
			return t * t * ( 3 - 2 * t );
		}

		static double dB( double d ) => Math.Pow( 10, d / 20 );
		static readonly double MONO = dB( - 3 ); // a mono file plays on both channels: +3 dB against its (mono) measured loudness

		// Target loudness (LUFS, momentary) of each sound in its reference situation.
		//   beds: at full weight; one-shots: per event (maximum momentary loudness)
		public static class MIX
		{
			public const double crash = - 15; // a wave breaking 10 m away (average wave; +-6 dB with the wave's height; line source: -4 dB per doubling)
			public const double wash = - 20; // the bore running up the sand, 5 m away
			public const double backwash = - 23; // the swash draining back down, 5 m away
			public const double surfFar = - 31; // distant roar at 50 m from the shore (falls off slowly)
			public const double wind = - 36; // 7 m/s, at the top of a gust (gusts come and go; lulls are near silent)
			public const double palms = - 39; // inland among the trees in a gust
			public const double crickets = - 33; // inland at night
			public const double pierLap = - 32; // water lapping the piles, 3 m away
			public const double reef = - 43; // underwater: snapping-shrimp crackle and low rumble (hydrophone), kept well in the background
			public const double engineIdle = - 30; // at the helm
			public const double engineRun = - 22; // at the helm, full rpm
			public const double boatRush = - 27; // water past the hull at ~9 m/s
			public const double boatLap = - 34; // lapping on the hull, boat at rest
			public const double hullSlap = - 24; // hard slam at the helm (chop slaps while running are 6-14 dB quieter)
			public const double spray = - 30; // spray layer on hard slams
			public const double step = - 56; // footsteps: ~30 dB under the surf at the beach
			public const double swim = - 43; // surface stroke
			public const double uwSwim = - 43; // underwater stroke
			public const double splashSoft = - 38; // wading out of your depth
			public const double splash = - 30; // dropping into the water
			public const double submerge = - 36;
			public const double emerge = - 38;
			public const double gull = - 21; // at 10 m (they call from 25-80 m)
			public const double tern = - 25; // at 10 m
			public const double bird = - 31; // a songbird in the trees, at 10 m (they sing from 10-80 m: ~-35 to -50)
			public const double dove = - 34; // a dove cooing, at 10 m
			public const double birdChorus = - 35; // dawn chorus bed, inland at its peak
			public const double whaleSong = - 26; // humpback song underwater, 30 m from the whale (heard only within WHALE_RANGE)
			public const double whaleBlow = - 20; // the blow at 10 m
			public const double whaleBurst = - 17; // breaching: bursting out of the water, at 10 m
			public const double whaleSplash = - 11; // breach re-entry, at 12 m
			public const double whaleDrip = - 30; // water sheeting off the raised flukes, at 8 m
			public const double whaleFluke = - 22; // the flukes slipping under, at 10 m
			// fishing: the rod and reel are in your hands (close, a little to the right), the bobber out on the water
			public const double rodSwish = - 30; // a full-power cast whooshing past (weaker casts quieter)
			public const double bail = - 40; // the bail wire flipping open / snapping shut
			public const double lineOut = - 38; // line peeling off the spool as the cast flies out
			public const double plop = - 30; // the bobber landing, at 4 m (falls off with distance)
			public const double reelWind = - 36; // cranking steadily
			public const double reelDrag = - 29; // the drag screaming under a fast run
			public const double lineStrain = - 44; // line creaking at the breaking point
			public const double lineSnap = - 24; // the line parting
			public const double fishSplash = - 24; // a hooked fish thrashing at the surface, at 6 m
			public const double fishFlop = - 32; // the landed fish flapping on the line in front of you
			public const double coins = - 30; // paid at the stand
		}

		// the fishing sounds (loaded when the rod comes out)
		static readonly string[] FISHING = { "reel_wind", "reel_drag", "line_strain", "rod_swish", "bail_click", "line_out", "plop", "line_snap", "fish_splash", "fish_flop" };

		// forest bird sprite: slices per source recording (a singer sings from one of them)
		static readonly int[][] FOREST = { new[] { 0, 1, 2, 3, 4, 5, 6 }, new[] { 7, 8, 9, 10, 11, 12 } };
		static readonly string[] WHALE_SET = { "whale_blow", "big_splash", "emerge" };
		// the whale is only heard within this distance (m), faded out over its last 30 m
		const double WHALE_RANGE = 100;

		// per-surface footstep trims (dB)
		static readonly Dictionary<string, (string bank, double trim)> STEP = new Dictionary<string, (string, double)>
		{
			{ "sand", ( "step_sand", 0 ) }, { "wetsand", ( "step_wetsand", 0 ) }, { "wood", ( "step_wood", 2 ) },
			{ "water", ( "step_water", 0 ) }, { "grass", ( "step_grass", 2 ) }, { "rock", ( "step_rock", 1 ) },
		};

		// max simultaneous one-shot voices per category (oldest is faded out beyond this)
		static readonly Dictionary<string, int> LIMITS = new Dictionary<string, int>
		{
			{ "step", 3 }, { "swim", 2 }, { "splash", 3 }, { "trans", 2 }, { "hull", 3 }, { "gull", 2 }, { "crash", 7 }, { "wash", 5 }, { "back", 5 },
			{ "bird", 4 }, { "tern", 2 }, { "whale", 4 }, { "rod", 4 }, { "fish", 3 }, { "coin", 1 },
		};

		// loaded at start; everything else on first use
		static readonly string[] CORE = { "surf_crash", "surf_wash", "surf_backwash", "surf_far", "wind", "palms", "step_sand", "step_wetsand", "step_wood", "step_water", "step_grass", "splash", "swim" };

		// surf stations: a row along the waterline around the listener
		const int ST_N = 9, TRANSECT = 110;
		const double ST_GAP = 11, SWASH_UP = 0.4;

		static readonly Dictionary<string, Dest> DEST = new Dictionary<string, Dest>
		{
			{ "surf_far", Dest.SurfFar }, { "wind", Dest.WindLP }, { "palms", Dest.Above }, { "crickets", Dest.Above }, { "pier_lap", Dest.PierPan },
			{ "under_reef", Dest.Under }, { "birds_dawn", Dest.Above }, { "whale_song", Dest.SongPan },
			{ "boat_engine", Dest.EngineLP }, { "boat_rush", Dest.BoatSum }, { "boat_lap", Dest.BoatSum },
			{ "reel_wind", Dest.Rod }, { "reel_drag", Dest.Rod }, { "line_strain", Dest.Rod },
		};

		sealed class Bed { public object h; public double trim; }
		sealed class Gust { public double v = 0.4, target = 0.6, t; }
		sealed class Wh { public bool blow; public double breaches = - 1, splashes = - 1, fluke, sing = 1; }
		sealed class Reel { public double crank, drag, tension; }
		sealed class Singer { public double x, y, z; public string bank; public int[] group; public int n; public double t, rate, lvl; }
		sealed class Station
		{
			public double x, z, dx, dz, Ts, exposure, along;
			public float[] depth, T;
			public double lastCrash = - 1e9, lastWash = - 1e9, lastBack = - 1e9;
		}

		public sealed class Env
		{
			public double lx = 0, ly = 1.7, lz = 0, fx = 0, fy = 0, fz = - 1, ux = 0, uy = 1, uz = 0;
			public double u = 0, depth = 0, surf = 0.5, shoreDist = 60, wind = 7, day = 1;
			public bool onLand = true, nearPier;
			public double hour = double.NaN; // NaN: unknown
			public double shoreX = 0, shoreZ = 1;
			public SoundBoat boat = new SoundBoat { active = false, rpm = 0, speed = 0, x = 0, y = 0, z = 0, listenerInside = false };
		}

		readonly ISoundBackend backend;
		readonly Func<double> random;
		public readonly Env env = new Env();
		bool _muted;
		double _volume = 0.8;
		readonly Dictionary<string, Bed> _beds = new Dictionary<string, Bed>();
		readonly Dictionary<string, List<Voice>> _voices = new Dictionary<string, List<Voice>>();
		readonly Dictionary<string, int> _last = new Dictionary<string, int>(); // bank -> last slice index
		double _acc, _gullT = 6, _ternT = 4, _birdT = 2, _slapT, _engine, _fakeT = 3;
		readonly List<Singer> _singers = new List<Singer>();
		readonly Wh _wh = new Wh();
		bool _engineOn;
		readonly Gust _gust = new Gust();
		ShoreSource _shore;
		List<Station> _stations;
		double _stAtX = 1e9, _stAtZ = 1e9;
		Reel _rod;
		readonly double _swellX, _swellZ;

		// the terrain for the birds' perches when no shore waves are attached; the wildlife's birds and the whale, when the game has them
		public IAudioTerrain terrain;
		public Func<IList<FlockBird>> flock;
		public Func<WhaleBrain> whale;

		public SoundScape( ISoundBackend backend, Func<double> random = null )
		{
			this.backend = backend;
			this.random = random ?? new System.Random().NextDouble;
			double sx = WorldLayout.SwellDirX, sy = WorldLayout.SwellDirZ; // (JS: swellDir { x, y }, its y is the sim z)
			double l = JS.Hypot( sx, sy ); if ( l == 0 ) l = 1;
			_swellX = sx / l; _swellZ = sy / l; // travel direction of the swell = towards the beach
		}

		// ------------------------------------------------------------------ public API

		// Optional: the game's shore waves (the host keeps its numbers current).
		public void attachShore( ShoreSource s ) { _shore = s != null && s.fieldData != null && s.terrain != null ? s : null; _stations = null; }

		// JS resume(): the context and graph exist, the core sounds start loading
		public void start()
		{
			_applyVolume();
			foreach ( var n in CORE ) _want( n );
		}

		public bool enabled => backend != null && backend.Running;
		public bool muted => _muted;
		public double volume => _volume;

		public void setMuted( bool m ) { _muted = m; _applyVolume(); }

		// 0..1 (perceptual taper)
		public void setMasterVolume( double v ) { _volume = clamp( num( v, _volume ), 0, 1 ); _applyVolume(); }

		// Per frame: listener pose, mixer ramps and surf / life events at ~30 Hz. Cheap.
		public void update( double dt, SoundState state )
		{
			if ( backend == null ) return;
			_readState( state ?? new SoundState() );
			if ( ! backend.Running ) return;
			_acc += clamp( num( dt, 1.0 / 60 ), 0, 0.25 );
			if ( _acc < 1.0 / 30 ) return;
			double step = Math.Min( _acc, 0.25 );
			_acc = 0;
			double now = backend.Now;
			_placeListener();
			_mix( now, step );
			_surf( step );
			_life( step );
			_birds( now, step );
			_whale( now, step );
			_reel( now );
		}

		// 'sand' | 'wetsand' | 'wood' | 'water' | 'grass' | 'rock'
		public void footstep( string surface )
		{
			var s = STEP.TryGetValue( surface ?? "", out var e ) ? e : STEP[ "sand" ];
			_shot( s.bank, "step", Dest.Foot, MIX.step + s.trim + ( random() - 0.5 ) * 3, 0.94 + random() * 0.12 );
		}

		// the player dropping into / wading out of their depth (strength 0..1)
		public void splash( double strength )
		{
			double s = clamp( num( strength, 0.5 ), 0, 1 );
			if ( s < 0.6 ) _shot( "swim", "splash", Dest.Above, MIX.splashSoft + s * 6, 0.9 + random() * 0.1 );
			else _shot( "splash", "splash", Dest.Above, MIX.splash - ( 1 - s ) * 8, 0.95 + random() * 0.1 );
		}

		public void splash( double strength, Vector3 at ) => splash( strength );

		public void swimStroke()
		{
			if ( env.u > 0.5 ) _shot( "uw_swim", "swim", Dest.Under, MIX.uwSwim + ( random() - 0.5 ) * 3, 0.9 + random() * 0.2 );
			else _shot( "swim", "swim", Dest.Above, MIX.swim + ( random() - 0.5 ) * 3, 0.92 + random() * 0.16 );
		}

		public void submerge() => _shot( "submerge", "trans", Dest.Near, MIX.submerge, 0.95 + random() * 0.1 );

		public void emerge() => _shot( "emerge", "trans", Dest.Near, MIX.emerge, 0.95 + random() * 0.1 );

		public void engineStart()
		{
			_engineOn = true;
			foreach ( var n in new[] { "boat_engine", "boat_rush", "boat_lap", "hull_slap" } ) _want( n );
		}

		public void engineStop() { _engineOn = false; }

		// the bow slamming into a sea (strength 0..1): slap on the hull + spray
		public void hullSlap( double strength )
		{
			double s = clamp( num( strength, 0.5 ), 0, 1 );
			_shot( "hull_slap", "hull", Dest.BoatSum, MIX.hullSlap - ( 1 - s ) * 10, 0.85 + random() * 0.2 );
			if ( s > 0.45 ) _shot( "splash", "hull", Dest.BoatSum, MIX.spray - ( 1 - s ) * 6, 1.15 + random() * 0.2 );
		}

		// ------------------------------------------------------------------ fishing (src/game)

		// the rod came out: load its sounds
		public void rodReady() { foreach ( var n in FISHING ) _want( n ); }

		// the cast: the rod whooshing through the air (power 0..1)
		public void whoosh( double power = 1 )
		{
			double p = clamp( num( power, 1 ), 0, 1 );
			_shot( "rod_swish", "rod", Dest.Rod, MIX.rodSwish - ( 1 - p ) * 9, 0.9 + p * 0.2 + random() * 0.06 );
		}

		// the bail wire: flipped open for the cast, snapped shut when reeling starts
		public void bail( bool open )
		{
			_shot( "bail_click", "rod", Dest.Rod, MIX.bail + ( open ? 0 : 2 ), open ? 1.08 + random() * 0.06 : 0.92 + random() * 0.06 );
		}

		// line paying out off the spool as the bobber flies (power 0..1: farther casts run longer)
		public void lineOut( double power = 1 )
		{
			double p = clamp( num( power, 1 ), 0, 1 );
			_shot( "line_out", "rod", Dest.Rod, MIX.lineOut - ( 1 - p ) * 6, 1.25 - p * 0.35 );
		}

		// the bobber landing on the water at p
		public void plop( Vector3 p )
		{
			if ( p == null ) return;
			_shotAt( "plop", "fish", p.x, p.y, p.z, MIX.plop, 0.95 + random() * 0.15, 0, 4, 1 );
		}

		// a hooked fish thrashing at p (strength 0..1)
		public void fishSplash( Vector3 p, double strength = 0.5 )
		{
			if ( p == null ) return;
			double s = clamp( num( strength, 0.5 ), 0, 1 );
			_shotAt( "fish_splash", "fish", p.x, p.y, p.z, MIX.fishSplash - ( 1 - s ) * 10, 0.9 + random() * 0.2 + ( 1 - s ) * 0.15, 0, 6, 1 );
		}

		// the landed fish flapping on the line in front of you
		public void fishFlop() => _shot( "fish_flop", "rod", Dest.Near, MIX.fishFlop, 0.9 + random() * 0.2 );

		public void lineSnap() => _shot( "line_snap", "rod", Dest.Rod, MIX.lineSnap, 0.95 + random() * 0.1 );

		// paid at the fish stand
		public void coin() => _shot( "coins", "coin", Dest.Near, MIX.coins, 0.97 + random() * 0.06 );

		// continuous reel sounds, every frame: crankRate (crank turns / s: the gear ticking follows it), dragSpeed (m/s of line a fish takes
		// against the drag), tension (0..1+, the line creaks near 1)
		public void rodLoop( double crankRate, double dragSpeed, double tension )
		{
			var r = _rod ?? ( _rod = new Reel() );
			r.crank = clamp( num( crankRate, 0 ), 0, 3 );
			r.drag = clamp( num( dragSpeed, 0 ), 0, 5 );
			r.tension = clamp( num( tension, 0 ), 0, 2 );
		}

		void _reel( double now )
		{
			var r = _rod;
			if ( r == null ) return;
			// the recorded crank ticks ~16 times a second: about 1.4 crank turns a second
			double wind = smooth( 0.05, 0.35, r.crank );
			if ( wind > 0 || _beds.ContainsKey( "reel_wind" ) ) _bed( "reel_wind", dB( MIX.reelWind ) * wind * ( 0.8 + 0.2 * Math.Min( 1, r.crank ) ) / dB( SoundBank.BANK[ "reel_wind" ].lufs ), now, 0.08, clamp( r.crank / 1.4, 0.45, 1.3 ) );
			double drag = smooth( 0.05, 0.7, r.drag );
			if ( drag > 0 || _beds.ContainsKey( "reel_drag" ) ) _bed( "reel_drag", dB( MIX.reelDrag ) * drag / dB( SoundBank.BANK[ "reel_drag" ].lufs ), now, 0.06, clamp( 0.7 + r.drag * 0.25, 0.7, 1.25 ) );
			double strain = smooth( 0.7, 1.0, r.tension );
			if ( strain > 0 || _beds.ContainsKey( "line_strain" ) ) _bed( "line_strain", dB( MIX.lineStrain ) * strain / dB( SoundBank.BANK[ "line_strain" ].lufs ), now, 0.1, 0.9 + 0.2 * strain );
		}

		public void dispose() { backend?.Dispose(); }

		// ------------------------------------------------------------------ graph

		void _applyVolume()
		{
			double v = _muted ? 0 : _volume * _volume;
			backend.Ramp( Par.Master, v, 0.05 );
		}

		// ------------------------------------------------------------------ loading

		// the clip if it has loaded; otherwise starts loading it (once) and returns null
		ClipInfo _want( string name ) => SoundBank.BANK.ContainsKey( name ) ? backend.Want( name ) : null;

		// ------------------------------------------------------------------ beds

		// sets a looping bed's gain (linear) and playback rate; starts it (random offset) when first audible
		void _bed( string name, double g, double now, double tau = 0.25, double rate = 1 )
		{
			if ( ! _beds.TryGetValue( name, out var bed ) )
			{
				if ( g < 1e-4 ) return;
				var buf = _want( name );
				if ( buf == null ) return;
				var dest = DEST[ name ];
				bed = new Bed { h = backend.StartBed( name, dest, now + 0.02, random() * buf.duration, rate ), trim = buf.channels == 1 ? MONO : 1 };
				_beds[ name ] = bed;
				if ( dest != Dest.Rod ) tau = Math.Max( tau, 0.8 ); // fade in on first start (the reel follows the crank at once)
			}

			backend.BedGain( bed.h, g * bed.trim, tau );
			backend.BedRate( bed.h, rate, 0.15 );
		}

		// ------------------------------------------------------------------ one-shots

		// plays a random slice of a sprite bank (never the same one twice in a row) at a target loudness;
		// `at`: context time (default now); `pick`: slice index (default random)
		Voice _shot( string bank, string cat, Dest dest, double targetLufs, double rate = 1, double at = 0, int pick = - 1, bool placed = false, Spatial sp = default )
		{
			if ( ! enabled ) return null;
			if ( ! SoundBank.BANK.TryGetValue( bank, out var info ) ) return null;
			var buf = _want( bank );
			if ( buf == null ) return null;
			int n = info.sliceCount;
			int i = pick >= 0 && pick < n ? pick : ( int ) Math.Floor( random() * n );
			if ( pick < 0 && n > 1 && _last.TryGetValue( bank, out int lastI ) && i == lastI ) i = ( i + 1 + ( int ) Math.Floor( random() * ( n - 1 ) ) ) % n;
			_last[ bank ] = i;
			double start = info.slices[ i, 0 ], dur = info.slices[ i, 1 ];
			double t = Math.Max( backend.Now + 0.005, at );
			double gain = dB( clamp( targetLufs - info.lufsAt[ i ], - 80, 24 ) ) * ( buf.channels == 1 ? MONO : 1 );
			var v = backend.Play( bank, dest, placed, sp, cat, i, start, dur, gain, rate, t );
			if ( v == null ) return null;
			v.end = t + dur / rate;

			if ( ! _voices.TryGetValue( cat, out var list ) ) _voices[ cat ] = list = new List<Voice>();
			list.Add( v );
			var self = v;
			v.onEnded = () => list.Remove( self );

			int limit = LIMITS.TryGetValue( cat, out int lim ) ? lim : 3;
			while ( list.Count > limit )
			{
				var old = list[ 0 ]; list.RemoveAt( 0 );
				backend.Fade( old );
			}

			return v;
		}

		// a one-shot placed in the world: placed at (x, y, z) + air absorption with distance
		Voice _shotAt( string bank, string cat, double x, double y, double z, double targetLufs, double rate, double at, double refDistance, double rolloff = 1, int pick = - 1 )
		{
			if ( ! enabled || _want( bank ) == null ) return null;
			double d = JS.Hypot( x - env.lx, y - env.ly, z - env.lz );
			var sp = new Spatial { x = x, y = y, z = z, refDistance = refDistance, rolloff = rolloff, airHz = clamp( 18000 / ( 1 + d / 45 ), 1500, 18000 ) };
			return _shot( bank, cat, Dest.Air, targetLufs, rate, at, pick, true, sp );
		}

		// a gull call somewhere over the shore: 25-80 m out, 6-25 m up
		void _gull()
		{
			var e = env;
			double a = Math.Atan2( e.shoreZ, e.shoreX ) + ( random() - 0.5 ) * 2.4;
			double d = 25 + random() * 55;
			_shotAt( "gull", "gull", e.lx + Math.Cos( a ) * d, e.ly + 6 + random() * 19, e.lz + Math.Sin( a ) * d,
				MIX.gull + ( random() - 0.5 ) * 4, 0.93 + random() * 0.14, 0, 10 );
		}

		// ------------------------------------------------------------------ surf, wave by wave

		// shore field at (x, z): travel time T, direction (dx, dz) x exposure, time to the shoreline Ts
		void _field( double x, double z, double[] o )
		{
			var sh = _shore;
			int res = sh.fieldRes; var data = sh.fieldData; var terrain = sh.terrain;
			double fx = clamp( ( x - terrain.origin ) / terrain.size * res - 0.5, 0, res - 1.001 );
			double fz = clamp( ( z - terrain.origin ) / terrain.size * res - 0.5, 0, res - 1.001 );
			int i = ( int ) Math.Floor( fx ), j = ( int ) Math.Floor( fz ); double tx = fx - i, tz = fz - j;
			int k = ( j * res + i ) * 4, kr = k + res * 4;
			for ( int c = 0; c < 4; c ++ )
			{
				double a = data[ k + c ], b = data[ k + 4 + c ], cc = data[ kr + c ], d = data[ kr + 4 + c ];
				o[ c ] = ( a * ( 1 - tx ) + b * tx ) * ( 1 - tz ) + ( cc * ( 1 - tx ) + d * tx ) * tz;
			}
		}

		// CPU mirrors of ShoreWaves.wobble / waveAmp / break depth
		static double _wobble( double along ) => Math.Sin( along * 0.029 + 0.7 ) * 0.07 + Math.Sin( along * 0.083 + 2.1 ) * 0.035;

		double _waveAmp( double m, double along )
		{
			var sh = _shore;
			double amp = sh.amplitude, vari = sh.variation;
			double set = Math.Abs( Math.Sin( m * Math.PI / 7 ) ) * 0.6 + 0.55;
			double h = Math.Sin( m * 127.1 + 311.7 ) * 43758.5453;
			double rnd = ( h - Math.Floor( h ) - 0.5 ) * 2;
			double warp = Math.Sin( along * 0.016 + m * 0.9 ) * 1.6;
			double a1 = Math.Sin( along * 0.062 + m * 1.7 + warp );
			double a2 = Math.Sin( along * 0.13 + m * 4.1 + 1.3 - warp * 0.7 );
			double alongV = a1 * 0.6 + a2 * 0.4;
			return Math.Max( 0.02, amp * set * ( 1 + rnd * vari * 0.5 + alongV * vari * 0.7 ) );
		}

		// a row of stations along the waterline near the listener, each with its transect out to sea
		void _buildStations()
		{
			var e = env; var terrain = _shore.terrain; var f = new double[ 4 ];
			double h( double x, double z ) => terrain.heightAt( x, z ) - 0;
			_field( e.lx, e.lz, f );
			double ex = JS.Hypot( f[ 1 ], f[ 2 ] );
			double dx = ex > 1e-3 ? f[ 1 ] / ex : _swellX, dz = ex > 1e-3 ? f[ 2 ] / ex : _swellZ;
			// the waterline on the line through the listener (seaward from land, landward from the water)
			double h0 = h( e.lx, e.lz ), sgn = h0 > 0 ? - 1 : 1;
			bool haveW0 = false; double w0x = 0, w0z = 0;
			for ( double r = 1; r < 400; r += 1.5 )
			{
				double x = e.lx + dx * sgn * r, z = e.lz + dz * sgn * r;
				if ( ( h( x, z ) > 0 ) != ( h0 > 0 ) ) { w0x = x; w0z = z; haveW0 = true; break; }
			}

			var @out = new List<Station>();
			if ( haveW0 )
			{
				for ( int k = 0; k < ST_N; k ++ )
				{
					double o = ( k - ( ST_N - 1.0 ) / 2 ) * ST_GAP;
					double x = w0x - dz * o, z = w0z + dx * o;
					// snap to the waterline along the local wave direction
					_field( x, z, f );
					ex = JS.Hypot( f[ 1 ], f[ 2 ] );
					if ( ex < 0.03 ) continue;
					double ldx = f[ 1 ] / ex, ldz = f[ 2 ] / ex;
					double s0 = h( x, z ) > 0 ? - 1 : 1;
					bool ok = false;
					for ( double r = 0; r < 60; r += 0.5 )
					{
						double xx = x + ldx * s0 * r, zz = z + ldz * s0 * r;
						if ( ( h( xx, zz ) > 0 ) != ( s0 < 0 ) ) { x = xx; z = zz; ok = true; break; }
					}

					if ( ! ok ) continue;
					_field( x, z, f );
					var st = new Station
					{
						x = x, z = z, dx = ldx, dz = ldz, Ts = f[ 3 ], exposure = clamp( ex * 1.4, 0, 1 ),
						along = - x * ldz + z * ldx, depth = new float[ TRANSECT ], T = new float[ TRANSECT ],
					};
					for ( int r = 0; r < TRANSECT; r ++ )
					{
						double xx = x - ldx * r, zz = z - ldz * r;
						st.depth[ r ] = ( float ) ( - h( xx, zz ) );
						_field( xx, zz, f );
						st.T[ r ] = ( float ) f[ 0 ];
					}

					if ( st.T[ 0 ] < 1e4 && st.Ts < 1e4 ) @out.Add( st );
				}
			}

			_stations = @out;
		}

		void _surf( double dt )
		{
			var e = env;
			var src = _shore;
			if ( src == null || src.enabled < 0.5 )
			{
				_fakeSurf( dt, src == null );
				return;
			}

			double moved = JS.Hypot( e.lx - _stAtX, e.lz - _stAtZ );
			if ( _stations == null || moved > 12 )
			{
				_stAtX = e.lx;
				_stAtZ = e.lz;
				_buildStations();
			}

			double P = Math.Max( 1, src.period ), tNow = src.time, gamma = src.gamma;
			double audioNow = backend.Now, LOOK = 0.2;
			double A0 = Math.Max( 0.05, src.amplitude );
			foreach ( var st in _stations )
			{
				double d = JS.Hypot( st.x - e.lx, st.z - e.lz );
				if ( d > 160 ) continue;
				double wob = _wobble( st.along );
				double mw = Math.Floor( ( tNow - st.Ts ) / P + wob );
				for ( double m = mw; m <= mw + 2; m ++ )
				{
					double A = _waveAmp( m, st.along );
					double rel = A / A0, lvl = 16 * Math.Log10( clamp( rel * st.exposure, 0.05, 3 ) );
					// breaking: the crest reaches the depth where this wave plunges (H = gamma d, lip lands ~8% in)
					double db = Math.Pow( A * 3.556 / gamma, 0.8 ) * 0.92;
					int rb = 0;
					while ( rb < TRANSECT - 1 && st.depth[ rb ] < db ) rb ++;
					double tc = st.T[ rb ] + ( m - wob ) * P;
					if ( m > st.lastCrash && tc >= tNow - 0.1 && tc < tNow + LOOK )
					{
						st.lastCrash = m;
						double x = st.x - st.dx * rb, z = st.z - st.dz * rb;
						if ( JS.Hypot( x - e.lx, z - e.lz ) < 150 && A > 0.06 )
						{
							_shotAt( "surf_crash", "crash", x, 0.4, z, MIX.crash + lvl + ( random() - 0.5 ) * 3,
								clamp( 1.06 - 0.25 * rel, 0.82, 1.08 ) * ( 0.96 + random() * 0.08 ), audioNow + Math.Max( 0, tc - tNow ), 10, 0.65 );
						}
					}

					// the bore reaches the sand and runs up; then the swash turns and drains back
					double tw = st.Ts + ( m - wob ) * P, tb = tw + SWASH_UP * P;
					if ( m > st.lastWash && tw >= tNow - 0.1 && tw < tNow + LOOK )
					{
						st.lastWash = m;
						if ( d < 70 ) _shotAt( "surf_wash", "wash", st.x - st.dx, 0.1, st.z - st.dz, MIX.wash + lvl + ( random() - 0.5 ) * 3,
							0.95 + random() * 0.1, audioNow + Math.Max( 0, tw - tNow ), 5, 0.8 );
					}

					if ( m > st.lastBack && tb >= tNow - 0.1 && tb < tNow + LOOK )
					{
						st.lastBack = m;
						if ( d < 60 ) _shotAt( "surf_backwash", "back", st.x - st.dx * 2, 0, st.z - st.dz * 2, MIX.backwash + lvl + ( random() - 0.5 ) * 3,
							0.95 + random() * 0.1, audioNow + Math.Max( 0, tb - tNow ), 5, 0.8 );
					}
				}
			}
		}

		// without the game's shore waves (tests, or before init): waves on a ~9 s cycle along the shore direction
		void _fakeSurf( double dt, bool on )
		{
			if ( ! on ) return;
			var e = env;
			_fakeT -= dt;
			if ( _fakeT > 0 ) return;
			_fakeT = 2.5 + random() * 4;
			double d = Math.Max( 6, e.shoreDist ), side = ( random() - 0.5 ) * 60;
			double x = e.lx + e.shoreX * ( d + 12 ) - e.shoreZ * side, z = e.lz + e.shoreZ * ( d + 12 ) + e.shoreX * side;
			double at = backend.Now;
			_shotAt( "surf_crash", "crash", x, 0.4, z, MIX.crash + ( random() - 0.5 ) * 6, 0.92 + random() * 0.12, at, 10 );
			double wx = e.lx + e.shoreX * d - e.shoreZ * side, wz = e.lz + e.shoreZ * d + e.shoreX * side;
			if ( d < 70 )
			{
				_shotAt( "surf_wash", "wash", wx, 0.1, wz, MIX.wash + ( random() - 0.5 ) * 4, 1, at + 3.5, 5 );
				_shotAt( "surf_backwash", "back", wx, 0, wz, MIX.backwash + ( random() - 0.5 ) * 4, 1, at + 7, 5 );
			}
		}

		// ------------------------------------------------------------------ per-frame

		void _readState( SoundState s )
		{
			var e = env;
			e.lx = num( s.lx, e.lx );
			e.ly = num( s.ly, e.ly );
			e.lz = num( s.lz, e.lz );
			double fx = num( s.fx, 0 ), fy = num( s.fy, 0 ), fz = num( s.fz, - 1 );
			double fl = JS.Hypot( fx, fy, fz ); if ( fl == 0 ) fl = 1;
			fx /= fl; fy /= fl; fz /= fl;
			double ux = num( s.ux, 0 ), uy = num( s.uy, 1 ), uz = num( s.uz, 0 );
			double ul = JS.Hypot( ux, uy, uz ); if ( ul == 0 ) ul = 1;
			ux /= ul; uy /= ul; uz /= ul;
			e.fx = fx; e.fy = fy; e.fz = fz; e.ux = ux; e.uy = uy; e.uz = uz;

			e.u = clamp( num( s.underwater, 0 ), 0, 1 );
			e.depth = Math.Max( 0, num( s.depthBelowSurface, 0 ) );
			e.surf = clamp( num( s.surfIntensity, 0.5 ), 0, 1 );
			e.shoreDist = Math.Max( 0, num( s.distanceToShore, 60 ) );
			e.wind = clamp( num( s.windSpeed, 7 ), 0, 40 );
			e.day = clamp( num( s.daylight, 1 ), 0, 1 );
			e.nearPier = s.nearPier;
			e.hour = ! double.IsNaN( s.timeOfDay ) && ! double.IsInfinity( s.timeOfDay ) ? ( ( s.timeOfDay % 24 ) + 24 ) % 24 : double.NaN;
			var b = s.boat ?? new SoundBoat(); var eb = e.boat;
			eb.active = b.active;
			eb.rpm = clamp( num( b.rpm, 0 ), 0, 1 );
			eb.speed = Math.Abs( num( b.speed, 0 ) );
			eb.x = num( b.x, eb.x );
			eb.y = num( b.y, eb.y );
			eb.z = num( b.z, eb.z );
			eb.listenerInside = b.listenerInside;
			e.onLand = ! double.IsNaN( s.coastDistance ) && ! double.IsInfinity( s.coastDistance ) ? s.coastDistance < 0 : true;
			if ( e.u > 0.01 || eb.active ) e.onLand = false;

			// towards the surf: seaward on land, towards the beach from the water (the bay faces the swell)
			bool inland = e.lx * _swellX + ( e.lz + 42 ) * _swellZ > 0;
			e.shoreX = inland ? - _swellX : _swellX;
			e.shoreZ = inland ? - _swellZ : _swellZ;
		}

		void _placeListener()
		{
			var e = env;
			backend.Listener( e.lx, e.ly, e.lz, e.fx, e.fy, e.fz, e.ux, e.uy, e.uz );
			backend.Pos( Pan.Surf, e.lx + e.shoreX * 40, e.ly - 1.5, e.lz + e.shoreZ * 40 );
			backend.Pos( Pan.Boat, e.boat.x, e.boat.y + 0.3, e.boat.z );
			backend.Ramp( Par.BoatIn, e.boat.listenerInside ? 1 : 0, 0.1 );
			backend.Ramp( Par.BoatOut, e.boat.listenerInside ? 0 : 1, 0.1 );
			backend.Pos( Pan.Pier, WorldLayout.Pier.x, 0, clamp( e.lz, Math.Max( WorldLayout.Pier.zStart, - 40 ), WorldLayout.Pier.zEnd ) );
		}

		void _mix( double now, double dt )
		{
			var e = env; double u = e.u, d = e.shoreDist; var eb = e.boat;
			double deep = clamp( e.depth / 12, 0, 1 );

			// underwater: steep low-pass on everything above the surface, the reef bed faded in
			double f = Math.Exp( lerp( Math.Log( 20000 ), Math.Log( lerp( 520, 260, deep ) ), u ) );
			backend.Ramp( Par.Muffle0, f, 0.04 );
			backend.Ramp( Par.Muffle1, Math.Min( 20000, f * 1.4 ), 0.04 );
			backend.Ramp( Par.AboveOut, lerp( 1, 0.4 / ( 1 + e.depth / 5 ), u ), 0.05 );
			backend.Ramp( Par.Under, u, 0.06 );
			bool wantUnder = u > 0 || ( ! e.onLand && e.ly < 2.5 );
			_bed( "under_reef", wantUnder ? dB( MIX.reef ) * ( 0.8 + 0.3 * deep ) / dB( SoundBank.BANK[ "under_reef" ].lufs ) : 0, now, 0.5 );
			if ( wantUnder )
			{
				_want( "uw_swim" );
				_want( "submerge" );
				_want( "emerge" );
			}

			// distant surf (the waves themselves are events, see _surf)
			double lvl = 0.6 + 0.7 * e.surf;
			_bed( "surf_far", dB( MIX.surfFar ) * 1.25 / ( 1 + d / 200 ) * lvl / dB( SoundBank.BANK[ "surf_far" ].lufs ), now, 0.5 );

			// wind in gusts: a random target every 2-8 s (lulls near silent), eased towards
			var g = _gust;
			g.t -= dt;
			if ( g.t <= 0 )
			{
				g.target = random() < 0.35 ? 0.03 + random() * 0.12 : 0.3 + random() * 0.7;
				g.t = 2 + random() * 6;
			}

			g.v += ( g.target - g.v ) * ( 1 - Math.Exp( - dt / 1.4 ) );
			double w = JS.Hypot( e.wind, eb.active ? eb.speed * 0.9 : 0 );
			double gw = eb.active && eb.speed > 3 ? Math.Max( g.v, 0.5 ) : g.v; // apparent wind on a running boat is steady
			_bed( "wind", dB( MIX.wind ) * clamp( w / 7, 0, 2.5 ) * gw / dB( SoundBank.BANK[ "wind" ].lufs ), now, 0.3 );
			backend.Ramp( Par.WindLP, 400 + ( 80 + 60 * gw ) * w, 0.4 );

			// trees inland rustle in the gusts; crickets inland at night (daylight < 0.3)
			double veg = e.onLand ? smooth( 6, 30, d ) : 0;
			_bed( "palms", dB( MIX.palms ) * veg * clamp( e.wind / 7, 0.2, 1.8 ) * g.v * ( 0.3 + 0.7 * e.day ) / dB( SoundBank.BANK[ "palms" ].lufs ), now, 0.5 );
			double night = smooth( 0.3, 0.12, e.day );
			_bed( "crickets", dB( MIX.crickets ) * night * ( e.onLand ? 0.35 + 0.65 * smooth( 5, 40, d ) : 0.1 ) / dB( SoundBank.BANK[ "crickets" ].lufs ), now, 1 );

			// water lapping the pier piles
			_bed( "pier_lap", ( e.nearPier ? dB( MIX.pierLap ) : 0 ) / dB( SoundBank.BANK[ "pier_lap" ].lufs ), now, 0.6 );

			// boat: one engine recording, pitched and opened up with rpm; water past the hull with speed,
			// lapping at rest, chop slapping the hull while running
			_engine += ( ( _engineOn ? 1 : 0 ) - _engine ) * ( 1 - Math.Exp( - dt * ( _engineOn ? 3 : 1.2 ) ) );
			double bd = JS.Hypot( eb.x - e.lx, eb.z - e.lz );
			bool nearBoat = eb.active || bd < 60;
			double eng = _engine, rpm = eb.rpm;
			_bed( "boat_engine", dB( lerp( MIX.engineIdle, MIX.engineRun, Math.Pow( rpm, 0.8 ) ) ) * eng / dB( SoundBank.BANK[ "boat_engine" ].lufs ), now, 0.12,
				( 0.8 + 0.85 * rpm ) * lerp( 0.75, 1, eng ) );
			backend.Ramp( Par.EngineLP, 900 + 6500 * Math.Pow( rpm, 1.3 ), 0.12 );
			double sp = eb.speed;
			_bed( "boat_rush", nearBoat ? dB( MIX.boatRush ) * smooth( 0.4, 9, sp ) / dB( SoundBank.BANK[ "boat_rush" ].lufs ) : 0, now, 0.3, 0.85 + 0.02 * Math.Min( sp, 12 ) );
			_bed( "boat_lap", nearBoat ? dB( MIX.boatLap ) * ( 1 - smooth( 1.5, 5, sp ) ) / dB( SoundBank.BANK[ "boat_lap" ].lufs ) : 0, now, 0.5 );
			if ( nearBoat && sp > 1.2 && e.u < 0.5 )
			{
				_slapT -= dt;
				if ( _slapT <= 0 )
				{
					double k = smooth( 1.2, 9, sp );
					_slapT = 0.3 + random() * ( 2.2 - 1.6 * k );
					_shot( "hull_slap", "hull", Dest.BoatSum, MIX.hullSlap - 14 + 8 * k + ( random() - 0.5 ) * 4, 0.85 + random() * 0.3 );
				}
			}
		}

		// gulls and terns: each call from a real bird of the wildlife near the listener (the nearer, the likelier - and louder); without the
		// wildlife, gulls from somewhere over the shore
		void _life( double dt )
		{
			var e = env;
			if ( e.day < 0.35 || e.u > 0.5 ) return;
			var flock = _flock();
			_gullT -= dt;
			if ( _gullT <= 0 )
			{
				if ( flock != null )
				{
					var a = _pickBird( flock, "gull", 170 );
					_gullT = a != null ? 5 + random() * 15 : 3;
					if ( a != null ) _shotAt( "gull", "gull", a.x, a.y, a.z, MIX.gull + ( random() - 0.5 ) * 4, 0.93 + random() * 0.14, 0, 10 );
				}
				else if ( e.shoreDist < 300 )
				{
					_gullT = 7 + random() * 18 + e.shoreDist * 0.05;
					if ( _want( "gull" ) != null ) _gull();
				}
			}

			_ternT -= dt;
			if ( _ternT <= 0 )
			{
				var a = flock != null ? _pickBird( flock, "tern", 190 ) : null;
				_ternT = a != null ? 7 + random() * 18 : 4;
				if ( a != null ) _shotAt( "tern", "tern", a.x, a.y, a.z, MIX.tern + ( random() - 0.5 ) * 4, 0.95 + random() * 0.1, 0, 10 );
			}
		}

		// the wildlife's flying / perched sea birds, if any
		IList<FlockBird> _flock()
		{
			var b = flock != null ? flock() : null;
			return b != null && b.Count > 0 ? b : null;
		}

		readonly List<FlockBird> _cand = new List<FlockBird>();
		readonly List<double> _candW = new List<double>();

		// a random bird of this kind within `range` m of the listener, weighted to the near ones
		FlockBird _pickBird( IList<FlockBird> agents, string kind, double range )
		{
			var e = env;
			_cand.Clear(); _candW.Clear();
			double tot = 0;
			foreach ( var a in agents )
			{
				if ( a.kind != kind || ! a.visible ) continue;
				double d = JS.Hypot( a.x - e.lx, a.y - e.ly, a.z - e.lz );
				if ( ! ( d < range ) ) continue;
				double w = 1 / ( 15 + d );
				_cand.Add( a ); _candW.Add( w );
				tot += w;
			}

			if ( tot <= 0 ) return null;
			double r = random() * tot;
			for ( int i = 0; i < _cand.Count; i ++ )
			{
				r -= _candW[ i ];
				if ( r <= 0 ) return _cand[ i ];
			}

			return _cand[ _cand.Count - 1 ];
		}

		// ------------------------------------------------------------------ island birds

		// hour of the day (state.timeOfDay); NaN if unknown
		double _hour() => env.hour;

		// songbird activity: { act: singers relative to a normal morning, chorus: dawn chorus 0..1, lull }
		(double act, double chorus, double lull) _birdActivity()
		{
			double h = _hour();
			if ( double.IsNaN( h ) ) return ( smooth( 0.35, 0.6, env.day ), 0, 0 );
			double on = smooth( 4.9, 5.7, h ) * ( 1 - smooth( 18.2, 19.3, h ) ); // awake: first light to dusk
			double chorus = smooth( 5.0, 5.8, h ) * ( 1 - smooth( 6.6, 7.9, h ) );
			double lull = smooth( 10.5, 12, h ) * ( 1 - smooth( 14.5, 16, h ) ); // the heat of the day
			double dusk = smooth( 17.2, 18.6, h );
			return ( on * Math.Max( 0, 1 + 2.5 * chorus - 0.6 * lull - 0.55 * dusk ), on * chorus, lull );
		}

		// a perch in the island's trees near the listener: 10-80 m away, 8 m+ inland (likelier deeper in), 3-12 m above the ground; null when
		// there is no land near (out at sea)
		Singer _perch()
		{
			var e = env;
			var terrain = ( _shore != null ? _shore.terrain : null ) ?? this.terrain;
			for ( int k = 0; k < 8; k ++ )
			{
				double a = random() * Math.PI * 2, r = 10 + random() * 70;
				double x = e.lx + Math.Cos( a ) * r, z = e.lz + Math.Sin( a ) * r;
				double inland, ground;
				if ( terrain != null )
				{
					inland = - num( terrain.coastDistance( x, z ), 1 );
					ground = num( terrain.heightAt( x, z ), 0 );
				}
				else
				{
					// no terrain (tests): the listener's own distance inland stands in
					inland = e.onLand ? e.shoreDist + ( random() - 0.5 ) * r : - 1;
					ground = e.ly - 1.7;
				}

				if ( inland < 8 || random() > 0.25 + 0.75 * smooth( 8, 45, inland ) ) continue;
				return new Singer { x = x, y = ground + 3 + random() * 9, z = z };
			}

			return null;
		}

		// songbirds and doves: a new singer every few seconds (at dawn often, at midday rarely, at night never), each a bout of 1-4 phrases
		// from one perch - a different slice of one recording each time
		void _birds( double now, double dt )
		{
			var e = env; var (act, chorus, lull) = _birdActivity();
			double inland = e.onLand ? smooth( 0, 45, e.shoreDist ) : 0;
			double hab = e.onLand ? 0.25 + 0.75 * inland : 0.25 * ( 1 - smooth( 20, 90, e.shoreDist ) );

			// the dawn chorus: many birds at once, diffuse; inland at full, from the beach distant, gone at sea
			double cg = chorus * ( e.onLand ? 0.3 + 0.7 * inland : 0.3 * ( 1 - smooth( 20, 150, e.shoreDist ) ) ) * ( 1 - e.u );
			_bed( "birds_dawn", dB( MIX.birdChorus ) * cg / dB( SoundBank.BANK[ "birds_dawn" ].lufs ), now, 2 );

			_birdT -= dt;
			double rate = act * hab;
			if ( _birdT <= 0 )
			{
				_birdT = rate > 0.02 ? ( 4 + random() * 20 ) / rate : 3;
				if ( rate > 0.02 && e.u < 0.5 && _singers.Count < 3 )
				{
					var p = _perch();
					if ( p != null )
					{
						bool dove = random() < 0.15 + 0.25 * lull;
						string bank = dove ? "bird_dove" : "bird_forest";
						int[] group = dove ? null : FOREST[ ( int ) Math.Floor( random() * FOREST.Length ) ];
						_want( bank );
						p.bank = bank;
						p.group = group;
						p.n = 1 + ( int ) Math.Floor( random() * ( dove ? 2 : 4 ) );
						p.t = random() * 0.5;
						p.rate = 0.93 + random() * 0.14;
						p.lvl = ( dove ? MIX.dove : MIX.bird ) + ( random() - 0.5 ) * 6;
						_singers.Add( p );
					}
				}
			}

			for ( int i = _singers.Count - 1; i >= 0; i -- )
			{
				var s = _singers[ i ];
				s.t -= dt;
				if ( s.t > 0 ) continue;
				if ( s.n <= 0 || e.u > 0.5 || act < 0.01 )
				{
					_singers.RemoveAt( i );
					continue;
				}

				var info = SoundBank.BANK[ s.bank ];
				if ( _want( s.bank ) == null )
				{
					s.t = 1; // still loading
					continue;
				}

				int k = - 1;
				if ( s.group != null )
				{
					k = s.group[ ( int ) Math.Floor( random() * s.group.Length ) ];
					if ( _last.TryGetValue( s.bank, out int lk ) && k == lk ) k = s.group[ ( Array.IndexOf( s.group, k ) + 1 ) % s.group.Length ];
				}

				var v = _shotAt( s.bank, "bird", s.x, s.y, s.z, s.lvl + ( random() - 0.5 ) * 2, s.rate * ( 0.98 + random() * 0.04 ), 0, 10, 1, k );
				double d = v != null ? info.slices[ _last[ s.bank ], 1 ] / s.rate : 0;
				s.n --;
				s.t = d + 0.8 + random() * 4.5;
			}
		}

		// ------------------------------------------------------------------ the humpback

		void _whale( double now, double dt )
		{
			var e = env; var s = _wh;
			var b = whale != null ? whale() : null;
			if ( b == null )
			{
				if ( _beds.ContainsKey( "whale_song" ) ) _bed( "whale_song", 0, now, 1 );
				return;
			}

			double u = e.u;
			double d = JS.Hypot( b.x - e.lx, b.y - e.ly, b.z - e.lz );

			// song: sung at depth while cruising (it stops to breathe); from above the surface only faint and dull. Only heard near the whale.
			s.sing += ( ( b.state == "cruise" ? 1 : 0.1 ) - s.sing ) * ( 1 - Math.Exp( - dt / 4 ) );
			bool audible = d < WHALE_RANGE;
			if ( audible || _beds.ContainsKey( "whale_song" ) )
			{
				backend.Pos( Pan.Song, b.x, b.y, b.z );
				backend.Ramp( Par.SongLP, Math.Exp( lerp( Math.Log( 420 ), Math.Log( 16000 ), u ) ), 0.08 );
				backend.Ramp( Par.SongOut, lerp( dB( - 20 ), 1, u ), 0.08 );
				_bed( "whale_song", audible ? dB( MIX.whaleSong ) * s.sing * ( 1 - smooth( WHALE_RANGE - 30, WHALE_RANGE, d ) ) / dB( SoundBank.BANK[ "whale_song" ].lufs ) : 0, now, 1.5 );
			}

			double wl = num( b.water, 0 ), fx = Math.Sin( num( b.yaw, 0 ) ), fz = Math.Cos( num( b.yaw, 0 ) );
			bool blowing = b.blow > 0; double fl = num( b.flukeUp, 0 );
			bool near = d < WHALE_RANGE;
			if ( d < WHALE_RANGE * 1.5 ) foreach ( var n in WHALE_SET ) _want( n );
			if ( near && s.breaches >= 0 )
			{
				// the blow: an explosive exhale as the blowholes clear the water, at the head
				if ( blowing && ! s.blow ) _shotAt( "whale_blow", "whale", b.x + fx * 4, wl + 1, b.z + fz * 4, MIX.whaleBlow + ( random() - 0.5 ) * 3, 0.72 + random() * 0.12, 0, 10 );
				// breach: bursting clear of the surface, then the crash back in
				if ( b.breaches > s.breaches ) _shotAt( "big_splash", "whale", b.x, wl + 1, b.z, MIX.whaleBurst, 0.6 + random() * 0.1, 0, 10 );
				if ( b.splashes > s.splashes )
				{
					_shotAt( "big_splash", "whale", b.x, wl + 0.5, b.z, MIX.whaleSplash, 0.5 + random() * 0.08, 0, 12 );
					_shotAt( "surf_crash", "whale", b.x + fx * 3, wl + 0.5, b.z + fz * 3, MIX.whaleSplash - 4, 0.72 + random() * 0.08, now + 0.06, 12 );
				}

				// fluke-up dive: water sheeting off the raised flukes, then the flukes slipping under
				double tx = b.x - fx * 9, tz = b.z - fz * 9;
				if ( fl > 0.05 && s.fluke <= 0.05 ) _shotAt( "emerge", "whale", tx, wl + 2, tz, MIX.whaleDrip, 0.7 + random() * 0.1, 0, 8 );
				if ( fl == 0 && s.fluke > 0.8 ) _shotAt( "big_splash", "whale", tx, wl, tz, MIX.whaleFluke, 0.78 + random() * 0.1, 0, 10 );
			}

			s.blow = blowing;
			s.fluke = fl;
			s.breaches = num( b.breaches, 0 );
			s.splashes = num( b.splashes, 0 );
		}
	}
}
