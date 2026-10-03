using System;
using UnityEngine;

// Port of src/ocean/Conditions.js: sea-state conditions, the presets the settings panel offers and the weather walks.
//
// Two cadences matter. The light uniforms (wind, choppiness, foam) are plain values and can be written as often as you
// like. The wave spectrum costs three small compute dispatches, which is nothing, but rebuilding it clears the
// accumulated foam buffer unless told not to (resetFoam): the weather drifts the spectrum continuously and keeps the
// foam, while a jump (a preset click, a load) rebuilds it and clears the foam, because the old foam belongs to a sea
// that no longer exists.
//
// Not ported yet (their systems do not exist): the shore waves' amplitude / period (v.surf, v.period) and the cloud
// cover. WriteConditions leaves them out and says so below.
namespace Tidewater.Ocean
{
	public struct Condition
	{
		public double wind, fetch, chop, swell, surf, period, whitecaps, cover, windDir;

		public Condition( double wind, double fetch, double chop, double swell, double surf, double period, double whitecaps, double cover )
		{
			this.wind = wind; this.fetch = fetch; this.chop = chop; this.swell = swell; this.surf = surf;
			this.period = period; this.whitecaps = whitecaps; this.cover = cover; windDir = 0;
		}
	}

	public static class Conditions
	{
		// the ladder, calmest first (Storm last): Weather moves along it, the settings panel picks from it
		public static readonly string[] CONDITIONS = { "Calm", "Breezy", "Choppy", "Storm" };

		public static readonly Condition[] SEA =
		{
			new Condition( 3.5, 40, 0.75, 0.28, 0.18, 11, 0.2, 0.2 ), // Calm
			new Condition( 7, 120, 0.9, 0.48, 0.34, 9, 0.5, 0.45 ), // Breezy
			new Condition( 12, 300, 1.05, 0.68, 0.56, 8.5, 0.75, 0.7 ), // Choppy
			new Condition( 20, 900, 1.2, 1.0, 0.9, 12, 1, 0.95 ), // Storm
		};

		// v: { wind, windDir, fetch, chop, swell, whitecaps, surf, period, cover }
		// spectrum: rebuild the wave spectrum (cheap; follows the weather smoothly)
		// resetFoam: clear the foam too, only for a jump in the sea, not for a drift (see the header)
		public static void WriteConditions( OceanFFT fft, Condition v, out Vector2 windDir, out float windSpeed, bool spectrum = true, bool resetFoam = true )
		{
			if ( spectrum )
			{
				fft.local.windSpeed = v.wind;
				fft.local.windDirection = v.windDir;
				fft.local.fetch = v.fetch;
				fft.swell.scale = v.swell;
				fft.UpdateSpectrumUniforms( resetFoam );
			}

			// G.windDir / G.windSpeed (sim xz): for the caller to publish
			double a = v.windDir * Math.PI / 180;
			windDir = new Vector2( ( float ) Math.Cos( a ), ( float ) Math.Sin( a ) );
			windSpeed = ( float ) v.wind;
			fft.choppiness = ( float ) v.chop;
			// more whitecaps: foam starts at less compression (and more of it in fresh wind), lasts longer
			fft.foamBias = ( float ) ( 0.5 + 0.16 * v.whitecaps + 0.01 * Math.Max( - 5, Math.Min( 12, v.wind - 7 ) ) );
			fft.foamDecay = ( float ) ( 0.6 - 0.35 * v.whitecaps );
			// shore.amplitude / shore.period (v.surf, v.period) and the cloud cover: not ported yet
		}

		// a condition interpolated along the ladder, for a fractional level (0 Calm .. 3 Storm)
		public static Condition ConditionAt( double level, double windDir )
		{
			int i = Math.Max( 0, Math.Min( CONDITIONS.Length - 2, ( int ) Math.Floor( level ) ) );
			double f = Math.Max( 0, Math.Min( 1, level - i ) );
			var a = SEA[ i ]; var b = SEA[ i + 1 ];
			double mix( double x, double y ) => x + ( y - x ) * f;
			return new Condition( mix( a.wind, b.wind ), mix( a.fetch, b.fetch ), mix( a.chop, b.chop ), mix( a.swell, b.swell ),
				mix( a.surf, b.surf ), mix( a.period, b.period ), mix( a.whitecaps, b.whitecaps ), mix( a.cover, b.cover ) ) { windDir = windDir };
		}
	}
}
