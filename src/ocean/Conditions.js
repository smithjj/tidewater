// Sea-state conditions: the presets the settings panel offers and the weather walks, plus the
// one function that knows how a condition reaches the simulation.
//
// Two cadences matter. The light uniforms (wind, choppiness, foam, shore waves) are plain values
// and can be written as often as you like. The wave spectrum is not: rebuilding it re-runs the
// FFT initial-spectrum dispatch, and that also *clears the accumulated foam buffer*
// (see OceanFFT.copyH0Kernel), so a rebuild has to stay rare — one per condition step, the same
// cost as a single click on a settings preset. Cloud cover is the same story: changing it drops
// the volumetric clouds' temporal history, so it rides along with the spectrum rebuild only.
import { MathUtils } from '../engine/index.js';
import { G } from '../core/Globals.js';

// the ladder, calmest first (Storm last): Weather moves along it, the settings panel picks from it
export const CONDITIONS = [ 'Calm', 'Breezy', 'Choppy', 'Storm' ];

export const SEA = {
	Calm: { wind: 3.5, fetch: 40, chop: 0.75, swell: 0.28, surf: 0.18, period: 11, whitecaps: 0.2, cover: 0.2 },
	Breezy: { wind: 7, fetch: 120, chop: 0.9, swell: 0.48, surf: 0.34, period: 9, whitecaps: 0.5, cover: 0.45 },
	Choppy: { wind: 12, fetch: 300, chop: 1.05, swell: 0.68, surf: 0.56, period: 8.5, whitecaps: 0.75, cover: 0.7 },
	Storm: { wind: 20, fetch: 900, chop: 1.2, swell: 1.0, surf: 0.9, period: 12, whitecaps: 1, cover: 0.95 },
};

// v: { wind, windDir, fetch, chop, swell, whitecaps, surf, period, cover? }
// opts.spectrum: rebuild the wave spectrum and set the cloud cover (rare; see the header)
export function writeConditions( { fft, shore, clouds }, v, { spectrum = true } = {} ) {

	if ( spectrum ) {

		fft.local.windSpeed = v.wind;
		fft.local.windDirection = v.windDir;
		fft.local.fetch = v.fetch;
		fft.swell.scale = v.swell;
		fft.updateSpectrumUniforms();
		if ( clouds && clouds.coverage && v.cover !== undefined ) clouds.coverage.value = v.cover;

	}

	const a = MathUtils.degToRad( v.windDir );
	G.windDir.value.set( Math.cos( a ), Math.sin( a ) );
	G.windSpeed.value = v.wind;
	fft.choppiness.value = v.chop;
	// more whitecaps: foam starts at less compression (and more of it in fresh wind), lasts longer
	fft.foamBias.value = 0.5 + 0.16 * v.whitecaps + 0.01 * MathUtils.clamp( v.wind - 7, - 5, 12 );
	fft.foamDecay.value = 0.6 - 0.35 * v.whitecaps;
	shore.amplitude.value = v.surf;
	shore.period.value = v.period;

}

// a condition interpolated along the ladder, for a fractional level (0 Calm .. 3 Storm)
export function conditionAt( level, windDir ) {

	const i = Math.max( 0, Math.min( CONDITIONS.length - 2, Math.floor( level ) ) );
	const f = Math.max( 0, Math.min( 1, level - i ) );
	const a = SEA[ CONDITIONS[ i ] ], b = SEA[ CONDITIONS[ i + 1 ] ];
	const mix = ( k ) => a[ k ] + ( b[ k ] - a[ k ] ) * f;
	return { wind: mix( 'wind' ), fetch: mix( 'fetch' ), chop: mix( 'chop' ), swell: mix( 'swell' ),
		surf: mix( 'surf' ), period: mix( 'period' ), whitecaps: mix( 'whitecaps' ), cover: mix( 'cover' ), windDir };

}
