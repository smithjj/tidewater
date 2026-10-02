// Sea-state conditions: the presets the settings panel offers and the weather walks, plus the
// one function that knows how a condition reaches the simulation.
//
// Two cadences matter. The light uniforms (wind, choppiness, foam, shore waves) are plain values and
// can be written as often as you like. The wave spectrum costs three small compute dispatches, which
// is nothing — but rebuilding it used to clear the accumulated foam buffer as a side effect, so it had
// to stay rare. That is now separable (`resetFoam`): the weather drifts the spectrum continuously and
// keeps the foam, while a jump — a preset click, a load — rebuilds it and clears the foam, because the
// old foam belongs to a sea that no longer exists.
//
// Cloud cover is the remaining rare one: changing it drops the volumetric clouds' temporal history, so
// it rides on the whole-step writes only (`cover`).
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
// opts.spectrum: rebuild the wave spectrum (cheap; follows the weather smoothly)
// opts.resetFoam: clear the foam too — only for a jump in the sea, not for a drift (see the header)
// opts.cover: set the cloud cover (drops the clouds' temporal history: whole steps only)
export function writeConditions( { fft, shore, clouds }, v, { spectrum = true, resetFoam = true, cover = spectrum } = {} ) {

	if ( spectrum ) {

		fft.local.windSpeed = v.wind;
		fft.local.windDirection = v.windDir;
		fft.local.fetch = v.fetch;
		fft.swell.scale = v.swell;
		fft.updateSpectrumUniforms( { resetFoam } );

	}

	if ( cover && clouds && clouds.coverage && v.cover !== undefined ) clouds.coverage.value = v.cover;

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
