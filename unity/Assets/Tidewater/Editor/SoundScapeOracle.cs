using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Tidewater.Audio;
using Tidewater.World;
using UnityEngine;
using Vector3 = Tidewater.Engine.Vector3;

// Compares the C# SoundScape (Runtime/Audio/SoundScape.cs) with src/audio/SoundScape.js:
//   node unity/tools/dump-soundscape.mjs unity/Temp/oracle/soundscape
//   unity/tools/soundscape-oracle.sh
// The JS ran against a recording stand-in for Web Audio; here the same scripted runs (2 x 120 s: the state each frame, the player's and the game's
// calls, the whale and the flock) drive the C# SoundScape through a recording backend, with the same seeded generator in place of the random
// numbers. Every ramp, bed, one-shot (slice, gain, rate, time, placement) and fade must be the same event in the same order.
namespace Tidewater.EditorTools
{
	public static class SoundScapeOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/soundscape" ) );

		static Func<double> Lcg( double seed ) => () => { seed = ( seed * 16807 ) % 2147483647; return seed / 2147483647; };

		sealed class Tally
		{
			public int compared, bad, big; public double maxAbs; public readonly List<string> bigFirst = new List<string>();
			public void Num( string at, double a, double b )
			{
				compared ++;
				if ( double.IsNaN( a ) && double.IsNaN( b ) ) return;
				double e = Math.Abs( a - b );
				if ( e > maxAbs || double.IsNaN( e ) ) maxAbs = double.IsNaN( e ) ? double.PositiveInfinity : e;
				if ( a != b ) bad ++;
				if ( ! ( e <= 1e-9 * ( 1 + Math.Abs( b ) ) ) ) { big ++; if ( bigFirst.Count < 6 ) bigFirst.Add( $"{at}: {a:R} vs JS {b:R}" ); }
			}
			public void Str( string at, string a, string b )
			{
				compared ++;
				if ( a != b ) { bad ++; big ++; if ( bigFirst.Count < 6 ) bigFirst.Add( $"{at}: '{a}' vs JS '{b}'" ); }
			}
			public void Fail( string at ) { compared ++; bad ++; big ++; if ( bigFirst.Count < 6 ) bigFirst.Add( at ); }
			public string Line( string name ) => $"  {name}: {compared} compared, {bad} differ in the last bits (max abs {maxAbs:E2}), {big} beyond 1e-9" + ( big > 0 ? "  <-- MISMATCH" : "" ) + "\n" + string.Concat( bigFirst.Select( f => "      " + f + "\n" ) );
		}

		// ------------------------------------------------------------------ the recording backend

		sealed class Rec : ISoundBackend
		{
			public double now;
			public List<object[]> ev = new List<object[]>();
			public readonly Dictionary<string, double> last = new Dictionary<string, double>();
			readonly Dictionary<string, (double dur, int ch)> info;
			readonly HashSet<string> loaded = new HashSet<string>(), pending = new HashSet<string>();
			sealed class Playing { public Voice v; public double order; }
			readonly List<Playing> playing = new List<Playing>();
			int order;
			sealed class VoiceRec { public string bank; public double start; }

			public Rec( Dictionary<string, (double, int)> info ) { this.info = info; }

			public double Now => now;
			public bool Running => true;
			public void Dispose() { }

			public ClipInfo Want( string name )
			{
				if ( loaded.Contains( name ) ) return new ClipInfo { duration = info[ name ].dur, channels = info[ name ].ch };
				pending.Add( name );
				return null;
			}

			// the loads that were started finish between frames
			public void Settle() { foreach ( var p in pending ) loaded.Add( p ); pending.Clear(); }

			// the JS _ramp: a negligible change is not scheduled
			void R( string name, double v, double tau )
			{
				if ( double.IsNaN( v ) || double.IsInfinity( v ) ) return;
				if ( last.TryGetValue( name, out double l ) && Math.Abs( v - l ) <= Math.Abs( l ) * 0.004 + 1e-6 ) return;
				last[ name ] = v;
				ev.Add( new object[] { "ramp", name, now, v, tau } );
			}

			public void Listener( double lx, double ly, double lz, double fx, double fy, double fz, double ux, double uy, double uz )
			{
				R( "listener.px", lx, 0.03 ); R( "listener.py", ly, 0.03 ); R( "listener.pz", lz, 0.03 );
				R( "listener.fx", fx, 0.02 ); R( "listener.fy", fy, 0.02 ); R( "listener.fz", fz, 0.02 );
				R( "listener.ux", ux, 0.02 ); R( "listener.uy", uy, 0.02 ); R( "listener.uz", uz, 0.02 );
			}

			public void Ramp( Par p, double v, double tau ) => R( p.ToString(), v, tau );

			public void Pos( Pan p, double x, double y, double z ) { R( "pos." + p + ".x", x, 0.05 ); R( "pos." + p + ".y", y, 0.05 ); R( "pos." + p + ".z", z, 0.05 ); }

			sealed class BedRec { public string name; }

			public object StartBed( string name, Dest dest, double at, double offset, double rate )
			{
				ev.Add( new object[] { "bed", name, dest.ToString(), at, offset, rate } );
				return new BedRec { name = name };
			}

			public void BedGain( object bed, double g, double tau ) => R( "bed." + ( ( BedRec ) bed ).name + ".gain", g, tau );
			public void BedRate( object bed, double rate, double tau ) => R( "bed." + ( ( BedRec ) bed ).name + ".rate", rate, tau );

			public Voice Play( string bank, Dest dest, bool placed, Spatial sp, string cat, int slice, double start, double dur, double gain, double rate, double t )
			{
				ev.Add( new object[] { "shot", bank, placed ? "Air" : dest.ToString(), placed, start, dur, gain, rate, t,
					placed ? sp.x : 0, placed ? sp.y : 0, placed ? sp.z : 0, placed ? sp.refDistance : 0, placed ? sp.rolloff : 0, placed ? sp.airHz : 0 } );
				var v = new Voice { h = new VoiceRec { bank = bank, start = start } };
				playing.Add( new Playing { v = v, order = order ++ } );
				return v;
			}

			public void Fade( Voice v ) { var r = ( VoiceRec ) v.h; ev.Add( new object[] { "fade", r.bank, r.start, now } ); }

			// voices that have played out
			public void Ended()
			{
				var done = playing.Where( p => p.v.end <= now ).OrderBy( p => p.order ).ToList();
				foreach ( var p in done ) { playing.Remove( p ); p.v.onEnded?.Invoke(); }
			}
		}

		// ------------------------------------------------------------------ the synthetic world

		static double Zc( double x ) => - 42 + 0.0003 * x * x - 0.02 * x;

		sealed class SynthTerrain : IAudioTerrain
		{
			public double origin => - 400;
			public double size => 800;
			public double heightAt( double x, double z ) { double c = Zc( x ); return z < c ? Math.Min( 8, ( c - z ) * 0.04 ) : Math.Max( - 25, - ( z - c ) * 0.06 ); }
			public double coastDistance( double x, double z ) => z - Zc( x );
		}

		static double N( JToken t, double dflt = double.NaN ) => t == null || t.Type == JTokenType.Null ? dflt : ( double ) t;

		static SoundState State( JToken s )
		{
			var st = new SoundState();
			var L = s[ "listener" ];
			st.lx = N( L[ "position" ][ "x" ] ); st.ly = N( L[ "position" ][ "y" ] ); st.lz = N( L[ "position" ][ "z" ] );
			st.fx = N( L[ "forward" ][ "x" ] ); st.fy = N( L[ "forward" ][ "y" ] ); st.fz = N( L[ "forward" ][ "z" ] );
			st.ux = N( L[ "up" ][ "x" ] ); st.uy = N( L[ "up" ][ "y" ] ); st.uz = N( L[ "up" ][ "z" ] );
			st.underwater = N( s[ "underwater" ] ); st.depthBelowSurface = N( s[ "depthBelowSurface" ] ); st.surfIntensity = N( s[ "surfIntensity" ] );
			st.distanceToShore = N( s[ "distanceToShore" ] ); st.coastDistance = N( s[ "coastDistance" ] ); st.windSpeed = N( s[ "windSpeed" ] );
			st.daylight = N( s[ "daylight" ] ); st.timeOfDay = N( s[ "timeOfDay" ] ); st.nearPier = ( bool ) s[ "nearPier" ];
			var b = s[ "boat" ];
			st.boat.active = ( bool ) b[ "active" ]; st.boat.rpm = N( b[ "rpm" ] ); st.boat.speed = N( b[ "speed" ] ); st.boat.listenerInside = ( bool ) b[ "listenerInside" ];
			var bp = b[ "position" ];
			if ( bp != null ) { st.boat.x = N( bp[ "x" ] ); st.boat.y = N( bp[ "y" ] ); st.boat.z = N( bp[ "z" ] ); }
			return st;
		}

		static void Call( SoundScape sc, JArray c )
		{
			string n = ( string ) c[ 0 ];
			Vector3 P( JToken p ) => new Vector3( ( double ) p[ "x" ], ( double ) p[ "y" ], ( double ) p[ "z" ] );
			switch ( n )
			{
				case "footstep": sc.footstep( ( string ) c[ 1 ] ); break;
				case "splash": sc.splash( ( double ) c[ 1 ] ); break;
				case "swimStroke": sc.swimStroke(); break;
				case "submerge": sc.submerge(); break;
				case "emerge": sc.emerge(); break;
				case "setMuted": sc.setMuted( ( bool ) c[ 1 ] ); break;
				case "setMasterVolume": sc.setMasterVolume( ( double ) c[ 1 ] ); break;
				case "rodReady": sc.rodReady(); break;
				case "bail": sc.bail( ( bool ) c[ 1 ] ); break;
				case "whoosh": sc.whoosh( ( double ) c[ 1 ] ); break;
				case "lineOut": sc.lineOut( ( double ) c[ 1 ] ); break;
				case "plop": sc.plop( P( c[ 1 ] ) ); break;
				case "fishSplash": sc.fishSplash( P( c[ 1 ] ), ( double ) c[ 2 ] ); break;
				case "fishFlop": sc.fishFlop(); break;
				case "lineSnap": sc.lineSnap(); break;
				case "coin": sc.coin(); break;
				case "engineStart": sc.engineStart(); break;
				case "engineStop": sc.engineStop(); break;
				case "hullSlap": sc.hullSlap( ( double ) c[ 1 ] ); break;
				case "rodLoop": sc.rodLoop( ( double ) c[ 1 ], ( double ) c[ 2 ], ( double ) c[ 3 ] ); break;
				default: throw new Exception( "unknown call " + n );
			}
		}

		// ------------------------------------------------------------------ the comparison

		static JObject cache; static DateTime cacheTime;
		static JObject Load( string dir )
		{
			string f = Path.Combine( dir, "soundscape.json" );
			var t = File.GetLastWriteTimeUtc( f );
			if ( cache == null || t != cacheTime ) { cache = JObject.Parse( File.ReadAllText( f ) ); cacheTime = t; }
			return cache;
		}

		static string Fmt( object[] e ) => string.Join( " ", e.Select( x => x is double d ? d.ToString( "R" ) : x.ToString() ) );
		static string Fmt( JArray e ) => string.Join( " ", e.Select( x => x.Type == JTokenType.Float ? ( ( double ) x ).ToString( "R" ) : x.ToString() ) );

		static void Events( string at, List<object[]> mine, JArray js, Tally ty, StringBuilder note )
		{
			if ( mine.Count != js.Count )
			{
				ty.Fail( $"{at}: {mine.Count} events vs JS {js.Count}\n         C#: {string.Join( " | ", mine.Select( Fmt ).Take( 6 ) )}\n         JS: {string.Join( " | ", js.Select( e => Fmt( ( JArray ) e ) ).Take( 6 ) )}" );
			}

			for ( int i = 0; i < Math.Min( mine.Count, js.Count ); i ++ )
			{
				var a = mine[ i ]; var b = ( JArray ) js[ i ];
				if ( a.Length != b.Count || ( string ) b[ 0 ] != ( string ) a[ 0 ] ) { ty.Fail( $"{at}[{i}]: {Fmt( a )}  vs JS  {Fmt( b )}" ); continue; }
				for ( int k = 0; k < a.Length; k ++ )
				{
					string w = $"{at}[{i}] {a[ 0 ]}.{k} ({Fmt( a )})";
					if ( a[ k ] is string s ) ty.Str( w, s, ( string ) b[ k ] );
					else if ( a[ k ] is bool bo ) ty.Str( w, bo.ToString().ToLower(), ( ( bool ) b[ k ] ).ToString().ToLower() );
					else ty.Num( w, Convert.ToDouble( a[ k ] ), ( double ) b[ k ] );
				}
			}
		}

		public static string Compare( string dir = null, string only = "static" )
		{
			dir = dir ?? DefaultDir;
			var js = Load( dir );
			var sb = new StringBuilder();
			if ( only == "static" ) Static( js, sb );
			else Run( js, sb, only );
			return sb.ToString();
		}

		static void Static( JObject js, StringBuilder sb )
		{
			// the mixing constants
			var t = new Tally();
			foreach ( var f in typeof( SoundScape.MIX ).GetFields() )
			{
				var j = js[ "mix" ][ f.Name ];
				if ( j == null ) { t.Fail( "MIX." + f.Name + " is not in the JS" ); continue; }
				t.Num( "MIX." + f.Name, ( double ) f.GetValue( null ), ( double ) j );
			}

			t.Num( "MIX count", typeof( SoundScape.MIX ).GetFields().Length, ( ( JObject ) js[ "mix" ] ).Count );
			sb.Append( t.Line( "mixing constants" ) );

			// the sound bank
			t = new Tally();
			foreach ( var kv in ( JObject ) js[ "bank" ] )
			{
				if ( ! SoundBank.BANK.TryGetValue( kv.Key, out var c ) ) { t.Fail( "bank " + kv.Key + " is missing" ); continue; }
				var j = kv.Value;
				t.Str( kv.Key + " file", c.file, ( ( string ) j[ "file" ] ).Replace( ".ogg", "" ) );
				t.Str( kv.Key + " loop", c.loop.ToString(), ( ( bool ) j[ "loop" ] ).ToString() );
				if ( c.loop ) t.Num( kv.Key + " lufs", c.lufs, ( double ) j[ "lufs" ] );
				else
				{
					var sl = ( JArray ) j[ "slices" ]; var lu = ( JArray ) j[ "lufs" ];
					t.Num( kv.Key + " slices", c.sliceCount, sl.Count );
					for ( int i = 0; i < Math.Min( c.sliceCount, sl.Count ); i ++ )
					{
						t.Num( $"{kv.Key} slice {i} start", c.slices[ i, 0 ], ( double ) sl[ i ][ 0 ] );
						t.Num( $"{kv.Key} slice {i} dur", c.slices[ i, 1 ], ( double ) sl[ i ][ 1 ] );
						t.Num( $"{kv.Key} slice {i} lufs", c.lufsAt[ i ], ( double ) lu[ i ] );
					}
				}
			}

			t.Num( "bank count", SoundBank.BANK.Count, ( ( JObject ) js[ "bank" ] ).Count );
			sb.Append( t.Line( "sound bank (loudness and slices)" ) );

			// the world
			t = new Tally();
			var pier = js[ "world" ][ "pier" ];
			t.Num( "pier.x", WorldLayout.Pier.x, ( double ) pier[ "x" ] ); t.Num( "pier.zStart", WorldLayout.Pier.zStart, ( double ) pier[ "zStart" ] ); t.Num( "pier.zEnd", WorldLayout.Pier.zEnd, ( double ) pier[ "zEnd" ] );
			var sw = js[ "world" ][ "swellDir" ]; double l = Math.Sqrt( ( double ) sw[ "x" ] * ( double ) sw[ "x" ] + ( double ) sw[ "y" ] * ( double ) sw[ "y" ] );
			t.Num( "swell x", WorldLayout.SwellDirX, ( double ) sw[ "x" ] / l ); t.Num( "swell z", WorldLayout.SwellDirZ, ( double ) sw[ "y" ] / l );
			sb.Append( t.Line( "world (pier, swell)" ) );

			// the clips Unity imported against the lengths the JS ran with
			t = new Tally();
			foreach ( var kv in ( JObject ) js[ "clipInfo" ] )
			{
				var clip = Resources.Load<AudioClip>( "audio/" + ( string ) kv.Value[ "file" ] );
				if ( clip == null ) { t.Fail( kv.Key + ": no clip imported" ); continue; }
				t.Num( kv.Key + " channels", clip.channels, ( double ) kv.Value[ "ch" ] );
				double d = clip.length - ( double ) kv.Value[ "dur" ];
				t.Num( kv.Key + " length within 5 ms", Math.Abs( d ) < 0.005 ? 0 : d, 0 );
			}

			sb.Append( t.Line( "imported clips (channels, length within 5 ms of the browser's)" ) );
		}

		static void Run( JObject js, StringBuilder sb, string name )
		{
			var run = js[ "runs" ].First( r => ( string ) r[ "name" ] == name );
			bool bare = ( bool ) run[ "bare" ];
			var info = new Dictionary<string, (double, int)>();
			foreach ( var kv in ( JObject ) js[ "clipInfo" ] ) info[ kv.Key ] = ( ( double ) kv.Value[ "dur" ], ( int ) kv.Value[ "ch" ] );
			var rec = new Rec( info );
			var sc = new SoundScape( rec, Lcg( bare ? 777 : 555 ) );
			var terr = new SynthTerrain();
			var shore = new ShoreSource();
			var sp = js[ "shore" ][ "params" ];
			shore.fieldRes = ( int ) js[ "shore" ][ "res" ];
			shore.fieldData = ( ( JArray ) js[ "shore" ][ "data" ] ).Select( x => ( float ) x ).ToArray();
			shore.terrain = terr;
			shore.enabled = ( double ) sp[ "enabled" ]; shore.amplitude = ( double ) sp[ "amplitude" ]; shore.variation = ( double ) sp[ "variation" ];
			shore.period = ( double ) sp[ "period" ]; shore.gamma = ( double ) sp[ "gamma" ];
			WhaleBrain wb = null; List<FlockBird> flock = null;
			if ( ! bare )
			{
				sc.attachShore( shore );
				sc.whale = () => wb;
				sc.flock = () => flock;
			}

			sc.start();
			rec.Settle();
			var t = new Tally();
			Events( "init", rec.ev, ( JArray ) run[ "init" ], t, sb );
			rec.ev = new List<object[]>();
			var steps = ( JArray ) run[ "steps" ]; var events = ( JArray ) run[ "events" ];
			int stepsRun = 0, perStepBad = 0;
			var tt = new Tally();
			for ( int i = 0; i < steps.Count; i ++ )
			{
				var s = steps[ i ];
				rec.now += ( double ) s[ "dt" ];
				rec.Ended();
				var w = s[ "whale" ];
				wb = w == null || w.Type == JTokenType.Null ? null : new WhaleBrain
				{
					x = ( double ) w[ "brain" ][ "position" ][ "x" ], y = ( double ) w[ "brain" ][ "position" ][ "y" ], z = ( double ) w[ "brain" ][ "position" ][ "z" ], state = ( string ) w[ "brain" ][ "state" ],
					water = ( double ) w[ "brain" ][ "water" ], yaw = ( double ) w[ "brain" ][ "yaw" ], blow = ( double ) w[ "brain" ][ "blow" ], flukeUp = ( double ) w[ "brain" ][ "flukeUp" ],
					breaches = ( double ) w[ "brain" ][ "breaches" ], splashes = ( double ) w[ "brain" ][ "splashes" ],
				};
				var f = s[ "flock" ];
				flock = f == null || f.Type == JTokenType.Null ? null : f.Select( b => new FlockBird { kind = ( string ) b[ "kind" ], visible = ( bool ) b[ "visible" ], x = ( double ) b[ "f" ][ "P" ][ "pos" ][ 0 ], y = ( double ) b[ "f" ][ "P" ][ "pos" ][ 1 ], z = ( double ) b[ "f" ][ "P" ][ "pos" ][ 2 ] } ).ToList();
				shore.time = ( double ) s[ "shore" ][ "time" ];
				foreach ( JArray c in s[ "calls" ] ) Call( sc, c );
				sc.update( ( double ) s[ "dt" ], State( s[ "state" ] ) );
				rec.Settle();
				int before = tt.big;
				Events( $"step {i} (t={( double ) s[ "t" ]:0.000})", rec.ev, ( JArray ) events[ i ], tt, sb );
				if ( tt.big > before ) perStepBad ++;
				rec.ev = new List<object[]>();
				stepsRun ++;
				if ( perStepBad >= 3 ) break;
			}

			sb.Append( t.Line( $"{name}: start" ) );
			sb.Append( tt.Line( $"{name}: {stepsRun} of {steps.Count} steps, every ramp / bed / one-shot / fade" ) );
		}
	}
}
