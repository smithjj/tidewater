// The fish finder's reading: the schools the reef is actually simulating (world/Fish.js), not the
// habitat model it used to sample. Given the FishSchools group list and a point, it says which
// schools are in range, where they are, how deep and how many fish — nearest first.
//
// Pure (no scene, no GPU) so the reading can be tested on its own: the groups are read, never
// touched. FishSchools freezes groups beyond SIM_RANGE but keeps their positions, so a frozen
// school is still reported; it just is not swimming.
import { FISH } from './FishTable.js';

// the whale's escorts sit at a seed point until a whale exists (Fish.js attachEscort), so they are
// not fish to be found yet
const SKIP = new Set( [ 'escort', 'remora' ] );

export const SONAR_RANGE = 65; // m: the simulation's own range (Fish.js SIM_RANGE): what is being simulated
export const SONAR_FULL = 250; // fish in range that fill the HUD gauge

// Every school within r of ( x, z ), nearest first. max > 0 returns at most that many, skipping any
// that comes closer than minSep to one already picked (so two rings on the map do not sit on top of
// each other); max = 0 returns all of them.
export function fishNear( groups, x, z, r, max = 0, minSep = 0 ) {

	const out = [];
	if ( ! groups ) return out;
	for ( const g of groups ) {

		if ( ! g || ! g.center ) continue;
		if ( g.sp && SKIP.has( g.sp.mode ) ) continue;
		const d = Math.hypot( g.center.x - x, g.center.z - z );
		if ( d > r ) continue;
		out.push( {
			x: g.center.x, z: g.center.z,
			depth: Math.max( 0, - g.center.y ), // below the surface, metres
			count: g.count || 0,
			radius: g.radius || 0,
			dist: d,
			// FISH is keyed by model and holds only catchable species, so a ray or turtle falls back
			// to its behaviour name ('stingray', 'pierGrunt', ...)
			model: ( g.sp && g.sp.model ) || null, // the FISH key: what a cast from here could catch
			name: ( g.sp && FISH[ g.sp.model ] && FISH[ g.sp.model ].name ) || ( g.sp && g.sp.name ) || 'fish',
		} );

	}

	out.sort( ( a, b ) => a.dist - b.dist );
	if ( ! max ) return out;
	const picked = [];
	for ( const q of out ) {

		if ( minSep && picked.some( ( o ) => Math.hypot( o.x - q.x, o.z - q.z ) < minSep ) ) continue;
		picked.push( q );
		if ( picked.length >= max ) break;

	}
	return picked;

}

// The shoal a cast is sitting on, as an influence the bite model can use: the nearest school within
// r, scaled by how close it is (1 under the shoal, 0 at the edge) and how many fish are in it (a
// pair of grouper is not a bait ball). Null when there is nothing there. This is what makes casting
// into a school you can see - or found with the finder - actually pay.
export function schoolBite( groups, x, z, r = 12 ) {

	const near = fishNear( groups, x, z, r, 1 )[ 0 ];
	if ( ! near ) return null;
	const closeness = 1 - near.dist / r;
	const size = Math.min( 1, Math.max( 0.35, near.count / 40 ) );
	return {
		model: near.model, name: near.name, dist: near.dist, count: near.count,
		influence: closeness * size, // 0..1, for biteDelay
		bite: 1 + 0.9 * closeness * size, // the rate the wait is divided by
		bias: 1 + 2.5 * closeness * size, // the weight its species is multiplied by
	};

}
