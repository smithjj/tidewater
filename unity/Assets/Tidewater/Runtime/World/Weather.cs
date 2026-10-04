using System;
using Newtonsoft.Json.Linq;
using Tidewater.Engine;
using Tidewater.Ocean;

// Port of src/world/Weather.js: the weather, a slow walk up and down the sea-state ladder on in-game time, so the sea turns over a couple of times a day
// and a blow is something you can see coming. Purely atmospheric: nothing here can damage the player, the boat or the gear. A day is 24 in-game hours
// (20 real minutes), so the constants below are in in-game hours and the real-time figures in the comments are at the default pace: one rung takes about
// two minutes of real time, and a condition then holds for three to ten.
//
// Cadence (see Ocean/Conditions.cs): the wave field follows the *fractional* level, so it drifts with the weather instead of stepping up a rung at a time, and
// it keeps the foam while it does (only a jump clears that). Cloud cover is the one thing still written on a whole step, because changing it drops the
// volumetric clouds' temporal history.
//
// The JS reaches the sea through app.fft / app.shore / app.clouds and the clock through app.settings; here those are delegates, so the walk itself is plain
// numbers and the oracle (tools/dump-weather.mjs -> Editor/WeatherOracle.cs) can run it against the JS.
namespace Tidewater.World
{
	public sealed class Weather
	{
		const double STEP_HOURS = 2.2; // in-game hours to cross one step of the ladder (~110 s of real time)
		static readonly double[] HOLD = { 3.5, 12 }; // in-game hours a condition holds before the next pick (~3 to 10 real minutes)

		// where a condition goes: ( condition, spectrum, resetFoam, cover ) -> OceanRenderer.ApplyConditions
		public delegate void WriteFn( Condition v, bool spectrum, bool resetFoam, bool cover );

		readonly Func<double> timeOfDay, timeSpeed;
		readonly WriteFn writeFn;
		Func<double> rnd;

		public Action<string> onAnnounce; // the game layer toasts it
		public bool enabled = true;
		public double pace = 1;
		public double level = 1; // Breezy
		public double target = 1;
		public int seed;
		public double windDir;
		double dirBase, dirTarget, hold;
		int step = 1; // last whole step written to the spectrum

		public double HoldHours => hold;
		public int Step => step;

		// `windDir0`: fft.local.windDirection, the wind the sea starts with
		public Weather( Func<double> timeOfDay, Func<double> timeSpeed, double windDir0, WriteFn write, int seed )
		{
			this.timeOfDay = timeOfDay; this.timeSpeed = timeSpeed; writeFn = write;
			this.seed = seed;
			rnd = Mulberry32( seed );
			windDir = windDir0;
			dirBase = windDir;
			dirTarget = windDir;
			hold = NextHold();
			// match the sea to the level from the very first frame (nothing else applies a preset at startup, which otherwise leaves the wind on the water
			// disagreeing with the waves)
			Write( true );
		}

		// the condition to show in the HUD
		public string name => Conditions.CONDITIONS[ Math.Max( 0, Math.Min( Conditions.CONDITIONS.Length - 1, ( int ) JS.Round( level ) ) ) ];

		public void update( double dt )
		{
			if ( ! enabled ) return;
			// weather runs on in-game time: pausing the clock holds the weather too
			double hours = Math.Max( 0, dt * timeSpeed() * pace );
			if ( hours > 0 )
			{
				double k = 1 - Math.Exp( - hours / STEP_HOURS );
				level += ( target - level ) * k;
				if ( Math.Abs( target - level ) < 0.015 ) level = target;
				windDir += ( dirTarget - windDir ) * k;
				hold -= hours;
				if ( hold <= 0 ) Pick();
			}

			// a whole step: only the cloud cover rides on it (the sea itself drifts with the level)
			int s = ( int ) JS.Round( level );
			bool crossed = s != step;
			if ( crossed ) step = s;

			// The whole sea state is written every frame, not on a slow cadence (see Weather.js: a 0.25 s cadence would step the whitecap edges, the crest
			// folding, the wind and the surf height, which reads as the water flickering)
			Write( true, false, crossed );
		}

		// `spectrum`: rebuild the wave field from the current level (see the header). `resetFoam` is for a jump (a load): the accumulated foam belongs to the old sea.
		public void Write( bool spectrum, bool resetFoam = true, bool? cover = null )
		{
			var v = Conditions.ConditionAt( level, windDir );
			writeFn( v, spectrum, resetFoam, cover ?? spectrum );
		}

		// pick the next condition: the island's own rhythm, calm mornings and storms rare
		void Pick()
		{
			double hour = timeOfDay();
			// the next condition: nearby levels are likely and a bigger swing is rare (a turn in the weather should be something that happens to you now and
			// then, not every time)
			Func<int, double> away = l =>
			{
				int d = Math.Abs( l - step );
				return d == 0 ? 0.5 : d == 1 ? 1 : d == 2 ? 0.22 : 0.06;
			};
			var w = new double[ 4 ];
			for ( int l = 0; l < 4; l ++ ) w[ l ] = Weight( l, hour ) * away( l );
			double r = rnd() * ( w[ 0 ] + w[ 1 ] + w[ 2 ] + w[ 3 ] );
			int next = 1;
			for ( int l = 0; l < Conditions.CONDITIONS.Length; l ++ )
			{
				r -= w[ l ];
				if ( r <= 0 ) { next = l; break; }
			}

			int rise = next - step;
			target = next;
			hold = NextHold();
			// the wind backs or veers a few degrees as the weather turns
			dirTarget = dirBase + ( rnd() * 2 - 1 ) * 22;
			if ( onAnnounce != null && Math.Abs( rise ) >= 2 ) onAnnounce( rise > 0
				? "The wind's backing — it's blowing up out there"
				: "The wind's dropping away" );
		}

		static double Weight( int level, double hour )
		{
			switch ( level )
			{
				case 0: return hour < 11 ? 3 : 1.1;
				case 1: return 3;
				case 2: return hour >= 10 && hour < 20 ? 1.7 : 0.7;
				default: return hour >= 11 && hour < 19 ? 0.14 : 0.02;
			}
		}

		double NextHold() => HOLD[ 0 ] + rnd() * ( HOLD[ 1 ] - HOLD[ 0 ] );

		// Debug.js weather( name ): stand on a rung of the ladder
		public void Jump( int rung )
		{
			enabled = true;
			level = target = rung;
			step = rung; // the rung we are standing on, so the write uses its own values
			Write( true );
		}

		public void setEnabled( bool on ) { enabled = on; }

		// a manual change to the sea controls hands the wheel back to the player
		public bool setManual()
		{
			if ( ! enabled ) return false;
			setEnabled( false );
			return true;
		}

		// the save (browser-compatible: the same keys)
		public JObject state() => new JObject
		{
			[ "enabled" ] = enabled, [ "pace" ] = pace, [ "seed" ] = seed, [ "level" ] = level,
			[ "target" ] = target, [ "hold" ] = hold, [ "windDir" ] = windDir, [ "dirBase" ] = dirBase,
		};

		public void restore( JObject d )
		{
			if ( d == null ) return;
			if ( d[ "enabled" ] != null && d[ "enabled" ].Type == JTokenType.Boolean ) enabled = ( bool ) d[ "enabled" ];
			double v;
			if ( Finite( d, "pace", out v ) ) pace = v;
			// (JS: d.seed | 0, and the generator restarts from the seed)
			if ( Finite( d, "seed", out v ) ) { seed = ToInt32( v ); rnd = Mulberry32( seed ); }
			if ( Finite( d, "level", out v ) ) level = v;
			if ( Finite( d, "target", out v ) ) target = v;
			if ( Finite( d, "hold", out v ) ) hold = v;
			if ( Finite( d, "windDir", out v ) ) { windDir = v; dirTarget = v; }
			if ( Finite( d, "dirBase", out v ) ) dirBase = v;
			step = ( int ) JS.Round( level );
			Write( true );
		}

		// Number.isFinite( d[ key ] ): a JSON number (not a string or null)
		static bool Finite( JObject d, string key, out double v )
		{
			v = 0;
			var t = d[ key ];
			if ( t == null || ( t.Type != JTokenType.Integer && t.Type != JTokenType.Float ) ) return false;
			v = ( double ) t;
			return ! double.IsNaN( v ) && ! double.IsInfinity( v );
		}

		// the JS ToInt32 (x | 0)
		static int ToInt32( double x ) => unchecked( ( int ) ( long ) Math.Truncate( x ) );

		// mulberry32( seed ): the JS generator, in unsigned 32-bit arithmetic (Math.imul is the low 32 bits of the product)
		public static Func<double> Mulberry32( int seed )
		{
			uint a = unchecked( ( uint ) seed );
			return () =>
			{
				unchecked
				{
					a += 0x6D2B79F5u;
					uint t = a;
					t = ( t ^ ( t >> 15 ) ) * ( t | 1u );
					t ^= t + ( t ^ ( t >> 7 ) ) * ( t | 61u );
					return ( ( t ^ ( t >> 14 ) ) ) / 4294967296.0;
				}
			};
		}
	}
}
