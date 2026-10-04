using System;
using System.Collections.Generic;
using Tidewater.Util;
using UnityEngine;

// What plays SoundScape's decisions in Unity (the Web Audio graph in SoundScape.js): AudioSources on the listener's scene, in Unity world space
// (Util/Sim.cs: x, y, -z).
//
//  - Clips load asynchronously from Resources/audio (ClipInfo is null until a clip has loaded); one-shot sprites are cut into one AudioClip per
//    slice when they arrive, so a slice is a whole clip (no scheduled end times).
//  - The smoothed parameters (a setTargetAtTime in the JS) are stepped here every frame, v += (target - v) * (1 - exp(-dt / tau)).
//  - Level: the JS gains are a quarter-scale here (HEADROOM; MasterOut brings them back), and a mono clip is played with the -3 dB the JS
//    trims for it taken out again, because Unity's 2D / 3D pan law already puts a mono source on both channels at -3 dB (the Web Audio graph
//    copies it at 0 dB).
//  - Placed sources: the 3D panner with the inverse distance model of the Web panner (ref / (ref + rolloff (max(d, ref) - ref))) as a custom
//    rolloff curve. No HRTF (Unity's built-in panner only pans), and no head shadow.
//  - Filter: one AudioLowPassFilter per source (Unity allows one), at the lower of the source's own cutoff (air absorption, the wind's, the
//    engine's, the whale song's, else the second muffle stage) and the underwater muffle's first stage; the JS has two cascaded biquads.
//  - The boat's "at the helm / from outside" crossfade is the source's spatial blend (1 outside, 0 inside), not two gains into two panners.
namespace Tidewater.Audio
{
	public sealed class UnitySoundBackend : ISoundBackend
	{
		public const float HEADROOM = 0.25f;
		const float MAX_DIST = 600;
		static readonly double MONO_FIX = 1 / Math.Pow( 10, - 3.0 / 20 );

		sealed class P
		{
			public double v, target, tau = 0.05;
			public void Step( double dt ) { v = tau <= 0 ? target : v + ( target - v ) * ( 1 - Math.Exp( - dt / tau ) ); }
			public P( double v ) { this.v = target = v; }
		}

		sealed class Loaded { public ClipInfo info; public AudioClip clip; public AudioClip[] slices; }

		sealed class BedSrc
		{
			public AudioSource src; public AudioLowPassFilter a; public Dest dest; public bool mono;
			public P gain = new P( 0 ), rate;
		}

		sealed class VoiceSrc
		{
			public AudioSource src; public AudioLowPassFilter a; public Voice v; public Dest dest; public bool placed, mono, fading;
			public Spatial sp; public double gain, fadeFrom;
		}

		readonly Transform root;
		readonly MasterOut master;
		readonly Dictionary<string, Loaded> loaded = new Dictionary<string, Loaded>();
		readonly Dictionary<string, ResourceRequest> pending = new Dictionary<string, ResourceRequest>();
		readonly List<BedSrc> beds = new List<BedSrc>();
		readonly List<VoiceSrc> voices = new List<VoiceSrc>();
		readonly Stack<VoiceSrc> freeVoices = new Stack<VoiceSrc>();
		readonly Dictionary<(double, double), AnimationCurve> curves = new Dictionary<(double, double), AnimationCurve>();
		readonly P[] par = new P[ Enum.GetValues( typeof( Par ) ).Length ];
		readonly Vector3[] pan = new Vector3[ Enum.GetValues( typeof( Pan ) ).Length ];

		public UnitySoundBackend( Transform parent, MasterOut master )
		{
			this.master = master;
			root = new GameObject( "Sound" ) { hideFlags = HideFlags.DontSave }.transform;
			root.SetParent( parent, false );
			par[ ( int ) Par.Master ] = new P( 0 );
			par[ ( int ) Par.AboveOut ] = new P( 1 );
			par[ ( int ) Par.Under ] = new P( 0 );
			par[ ( int ) Par.Muffle0 ] = new P( 20000 );
			par[ ( int ) Par.Muffle1 ] = new P( 20000 );
			par[ ( int ) Par.WindLP ] = new P( 1400 );
			par[ ( int ) Par.EngineLP ] = new P( 1200 );
			par[ ( int ) Par.BoatIn ] = new P( 0 );
			par[ ( int ) Par.BoatOut ] = new P( 1 );
			par[ ( int ) Par.SongLP ] = new P( 420 );
			par[ ( int ) Par.SongOut ] = new P( Math.Pow( 10, - 20.0 / 20 ) );
		}

		public bool Alive => root != null;

		public double Now => AudioSettings.dspTime;
		public bool Running => root != null && ! AudioListener.pause;

		public void Dispose()
		{
			foreach ( var l in loaded.Values ) { if ( l.slices != null ) foreach ( var s in l.slices ) if ( s != null ) UnityEngine.Object.Destroy( s ); }
			loaded.Clear();
			if ( root != null ) UnityEngine.Object.Destroy( root.gameObject );
		}

		// ------------------------------------------------------------------ loading

		public ClipInfo Want( string name )
		{
			if ( loaded.TryGetValue( name, out var l ) ) return l.info;
			if ( ! pending.ContainsKey( name ) ) pending[ name ] = Resources.LoadAsync<AudioClip>( "audio/" + SoundBank.BANK[ name ].file );
			return null;
		}

		void PollLoads()
		{
			if ( pending.Count == 0 ) return;
			List<string> done = null;
			foreach ( var kv in pending ) if ( kv.Value.isDone ) ( done ?? ( done = new List<string>() ) ).Add( kv.Key );
			if ( done == null ) return;
			foreach ( var name in done )
			{
				var req = pending[ name ]; pending.Remove( name );
				var clip = req.asset as AudioClip;
				if ( clip == null ) { Debug.LogWarning( "[SoundScape] audio/" + SoundBank.BANK[ name ].file + " did not load" ); continue; }
				var l = new Loaded { clip = clip, info = new ClipInfo { duration = clip.length, channels = clip.channels } };
				var bank = SoundBank.BANK[ name ];
				if ( ! bank.loop )
				{
					l.slices = new AudioClip[ bank.sliceCount ];
					for ( int i = 0; i < l.slices.Length; i ++ )
					{
						int from = ( int ) Math.Round( bank.slices[ i, 0 ] * clip.frequency );
						int n = Math.Min( ( int ) Math.Round( bank.slices[ i, 1 ] * clip.frequency ), clip.samples - from );
						var data = new float[ n * clip.channels ];
						clip.GetData( data, from );
						var s = AudioClip.Create( name + "#" + i, n, clip.channels, clip.frequency, false );
						s.SetData( data, 0 );
						l.slices[ i ] = s;
					}
				}

				loaded[ name ] = l;
			}
		}

		// ------------------------------------------------------------------ parameters and positions

		public void Listener( double lx, double ly, double lz, double fx, double fy, double fz, double ux, double uy, double uz ) { } // the AudioListener is on the camera

		public void Ramp( Par p, double v, double tau )
		{
			if ( double.IsNaN( v ) || double.IsInfinity( v ) ) return;
			var q = par[ ( int ) p ]; q.target = v; q.tau = tau;
		}

		public void Pos( Pan p, double x, double y, double z ) { pan[ ( int ) p ] = Sim.ToUnity( x, y, z ); }

		// ------------------------------------------------------------------ sources

		AudioSource NewSource( Transform parent, string name, out AudioLowPassFilter a )
		{
			var go = new GameObject( name ) { hideFlags = HideFlags.DontSave };
			go.transform.SetParent( parent, false );
			var s = go.AddComponent<AudioSource>();
			s.playOnAwake = false; s.dopplerLevel = 0; s.spread = 0; s.rolloffMode = AudioRolloffMode.Custom; s.maxDistance = MAX_DIST;
			a = go.AddComponent<AudioLowPassFilter>();
			a.cutoffFrequency = 22000;
			return s;
		}

		// the Web panner's inverse model as a rolloff curve over 0..MAX_DIST
		AnimationCurve Curve( double refDistance, double rolloff )
		{
			if ( curves.TryGetValue( ( refDistance, rolloff ), out var c ) ) return c;
			var pts = new List<(float d, float g)> { ( 0, 1 ) };
			for ( double d = Math.Max( 0.5, refDistance ); d < MAX_DIST; d *= 1.35 ) pts.Add( ( ( float ) d, ( float ) ( refDistance / ( refDistance + rolloff * ( d - refDistance ) ) ) ) );
			pts.Add( ( MAX_DIST, ( float ) ( refDistance / ( refDistance + rolloff * ( MAX_DIST - refDistance ) ) ) ) );
			var keys = new Keyframe[ pts.Count ];
			for ( int i = 0; i < pts.Count; i ++ )
			{
				float slope = i == 0 ? 0 : ( pts[ i ].g - pts[ i - 1 ].g ) / ( ( pts[ i ].d - pts[ i - 1 ].d ) / MAX_DIST );
				if ( i == 0 && pts.Count > 1 ) slope = ( pts[ 1 ].g - pts[ 0 ].g ) / ( ( pts[ 1 ].d - pts[ 0 ].d ) / MAX_DIST );
				keys[ i ] = new Keyframe( pts[ i ].d / MAX_DIST, pts[ i ].g, slope, slope );
			}

			return curves[ ( refDistance, rolloff ) ] = new AnimationCurve( keys );
		}

		// where a bed plays: ( placed at, ref, rolloff ), or 2D
		static bool Placement( Dest d, out Pan at, out double refDistance, out double rolloff )
		{
			switch ( d )
			{
				case Dest.SurfFar: at = Pan.Surf; refDistance = 1; rolloff = 0; return true;
				case Dest.EngineLP: case Dest.BoatSum: at = Pan.Boat; refDistance = 3; rolloff = 1; return true;
				case Dest.PierPan: at = Pan.Pier; refDistance = 3; rolloff = 1.3; return true;
				case Dest.SongPan: at = Pan.Song; refDistance = 30; rolloff = 0.5; return true;
				default: at = Pan.Surf; refDistance = 1; rolloff = 1; return false;
			}
		}

		// the bus the destination ends in: the above-water mix (x aboveOut), the underwater bed (x under), unfiltered, or the whale song's own
		double Bus( Dest d )
		{
			switch ( d )
			{
				case Dest.Under: return par[ ( int ) Par.Under ].v;
				case Dest.Near: return 1;
				case Dest.SongPan: return par[ ( int ) Par.SongOut ].v;
				default: return par[ ( int ) Par.AboveOut ].v;
			}
		}

		// the filter: Unity allows one AudioLowPassFilter per source, so the source's own low-pass and the underwater muffle (two stages in the JS)
		// become one at the lower of the two cutoffs; none for the unfiltered buses
		void Filters( Dest d, Spatial sp, AudioLowPassFilter a )
		{
			double m0 = par[ ( int ) Par.Muffle0 ].v, m1 = par[ ( int ) Par.Muffle1 ].v, f;
			switch ( d )
			{
				case Dest.Under: case Dest.Near: f = 22000; break;
				case Dest.SongPan: f = par[ ( int ) Par.SongLP ].v; break;
				case Dest.WindLP: f = Math.Min( par[ ( int ) Par.WindLP ].v, m0 ); break;
				case Dest.EngineLP: f = Math.Min( par[ ( int ) Par.EngineLP ].v, m0 ); break;
				case Dest.Air: f = Math.Min( sp.airHz, m0 ); break;
				default: f = Math.Min( m1, m0 ); break;
			}

			a.cutoffFrequency = ( float ) Math.Max( 10, Math.Min( 22000, f ) );
		}

		// ------------------------------------------------------------------ beds

		public object StartBed( string name, Dest dest, double at, double offset, double rate )
		{
			var l = loaded[ name ];
			var bed = new BedSrc { dest = dest, mono = l.info.channels == 1, rate = new P( rate ) { tau = 0.15 } };
			bed.src = NewSource( root, "bed-" + name, out bed.a );
			bed.src.clip = l.clip; bed.src.loop = true; bed.src.pitch = ( float ) rate; bed.src.volume = 0;
			bed.src.timeSamples = Math.Min( l.clip.samples - 1, ( int ) ( offset * l.clip.frequency ) );
			bed.src.PlayScheduled( Math.Max( at, AudioSettings.dspTime + 0.01 ) );
			if ( Placement( dest, out var pa, out var rd, out var ro ) ) { bed.src.spatialBlend = 1; bed.src.SetCustomCurve( AudioSourceCurveType.CustomRolloff, Curve( rd, ro ) ); bed.src.maxDistance = MAX_DIST; }
			else { bed.src.spatialBlend = 0; if ( dest == Dest.Rod ) bed.src.panStereo = 0.25f; }
			beds.Add( bed );
			return bed;
		}

		public void BedGain( object bed, double g, double tau ) { var b = ( BedSrc ) bed; b.gain.target = g; b.gain.tau = tau; }
		public void BedRate( object bed, double rate, double tau ) { var b = ( BedSrc ) bed; b.rate.target = rate; b.rate.tau = tau; }

		// ------------------------------------------------------------------ one-shots

		public Voice Play( string bank, Dest dest, bool placed, Spatial sp, string cat, int slice, double start, double dur, double gain, double rate, double t )
		{
			if ( ! loaded.TryGetValue( bank, out var l ) || l.slices == null ) return null;
			var vs = freeVoices.Count > 0 ? freeVoices.Pop() : null;
			if ( vs == null )
			{
				vs = new VoiceSrc();
				vs.src = NewSource( root, "voice", out vs.a );
			}

			vs.src.gameObject.SetActive( true );
			vs.v = new Voice { h = vs }; vs.dest = dest; vs.placed = placed; vs.sp = sp; vs.gain = gain; vs.fading = false;
			vs.mono = l.info.channels == 1;
			var s = vs.src;
			s.clip = l.slices[ slice ]; s.loop = false; s.pitch = ( float ) rate; s.panStereo = dest == Dest.Rod ? 0.25f : 0;
			if ( placed )
			{
				s.spatialBlend = 1;
				s.SetCustomCurve( AudioSourceCurveType.CustomRolloff, Curve( sp.refDistance, sp.rolloff ) );
				s.transform.position = Sim.ToUnity( sp.x, sp.y, sp.z );
			}
			else if ( Placement( dest, out var pa, out var rd, out var ro ) )
			{
				// the boat's own sounds (hull slaps): placed at the boat, or at the helm
				s.spatialBlend = ( float ) par[ ( int ) Par.BoatOut ].v;
				s.SetCustomCurve( AudioSourceCurveType.CustomRolloff, Curve( rd, ro ) );
				s.transform.position = pan[ ( int ) pa ];
			}
			else s.spatialBlend = 0;

			Filters( dest, sp, vs.a );
			s.volume = ( float ) ( gain * HEADROOM * ( vs.mono ? MONO_FIX : 1 ) * Bus( dest ) );
			s.PlayScheduled( t );
			voices.Add( vs );
			return vs.v;
		}

		public void Fade( Voice v )
		{
			var vs = v.h as VoiceSrc;
			if ( vs == null || vs.fading ) return;
			vs.fading = true; vs.fadeFrom = AudioSettings.dspTime;
			v.end = Math.Min( v.end, vs.fadeFrom + 0.3 );
		}

		void Release( VoiceSrc vs )
		{
			vs.src.Stop(); vs.src.clip = null;
			vs.src.gameObject.SetActive( false );
			var cb = vs.v.onEnded; vs.v.onEnded = null;
			cb?.Invoke();
			freeVoices.Push( vs );
		}

		// ------------------------------------------------------------------ per frame

		public void Frame( double dt )
		{
			PollLoads();
			foreach ( var p in par ) p.Step( dt );
			double now = AudioSettings.dspTime;
			master.gain = ( float ) ( par[ ( int ) Par.Master ].v / HEADROOM );

			foreach ( var b in beds )
			{
				if ( b.src == null ) continue;
				b.gain.Step( dt ); b.rate.Step( dt );
				b.src.volume = ( float ) Math.Min( 1, b.gain.v * HEADROOM * ( b.mono ? MONO_FIX : 1 ) * Bus( b.dest ) );
				b.src.pitch = ( float ) b.rate.v;
				Filters( b.dest, default, b.a );
				if ( Placement( b.dest, out var at, out _, out _ ) )
				{
					b.src.transform.position = pan[ ( int ) at ];
					if ( b.dest == Dest.EngineLP || b.dest == Dest.BoatSum ) b.src.spatialBlend = ( float ) par[ ( int ) Par.BoatOut ].v;
				}
			}

			for ( int i = voices.Count - 1; i >= 0; i -- )
			{
				var vs = voices[ i ];
				double bus = Bus( vs.dest ), fade = 1;
				if ( vs.fading ) fade = Math.Max( 0, 1 - ( now - vs.fadeFrom ) / 0.3 );
				vs.src.volume = ( float ) Math.Min( 1, vs.gain * HEADROOM * ( vs.mono ? MONO_FIX : 1 ) * bus * fade );
				Filters( vs.dest, vs.sp, vs.a );
				if ( ! vs.placed && Placement( vs.dest, out var at, out _, out _ ) )
				{
					vs.src.transform.position = pan[ ( int ) at ];
					vs.src.spatialBlend = ( float ) par[ ( int ) Par.BoatOut ].v;
				}

				if ( now > vs.v.end + 0.1 ) { voices.RemoveAt( i ); Release( vs ); }
			}
		}
	}
}
