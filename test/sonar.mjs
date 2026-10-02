// The fish finder reading (src/game/Sonar.js): it reports the schools the reef is actually
// simulating, not habitat. Pure data, so no GPU is needed.
import { fishNear, SONAR_RANGE, SONAR_FULL } from '../src/game/Sonar.js';

let fails = 0;
const ok = ( c, msg ) => {

	if ( ! c ) { fails ++; console.log( 'FAIL', msg ); } else console.log( 'ok  ', msg );

};

const group = ( x, y, z, over = {} ) => ( {
	center: { x, y, z }, count: 20, radius: 4, active: true,
	sp: { name: 'chromis', model: 'chromis', mode: 'hover', band: [ 4, 16 ] },
	...over,
} );

const G = [
	group( 0, -6, 10 ), // 10 m north
	group( 0, -22, 30 ), // 30 m north, deep
	group( 0, -3, 200 ), // far out of range
	group( 12, -8, 5, { sp: { name: 'bait', model: 'silverside', mode: 'ball' } } ),
	group( 0, 200, 1, { sp: { name: 'remora', model: 'remora', mode: 'remora' } } ), // whale escort
	group( 0, -9, 40, { sp: { name: 'stingray', model: 'stingray', mode: 'ray' } } ), // no FISH entry
	{ center: null, sp: null, count: 5 }, // malformed: must not throw
];

const near = fishNear( G, 0, 0, SONAR_RANGE );
const names = near.map( ( q ) => q.name );
console.log( `     in range: ${ near.map( ( q ) => `${ q.name } ${ q.dist.toFixed( 0 ) }m ${ q.depth.toFixed( 0 ) }m` ).join( ' | ' ) }` );

ok( near.length === 4, `every real school in range is reported (${ near.length }: the far one, the whale escort and the malformed group dropped)` );
ok( near.every( ( q ) => q.dist <= SONAR_RANGE ), 'and nothing beyond the range' );
ok( ! names.includes( 'remora' ), 'the whale escort is not fish to find' );
ok( near[ 0 ].dist < near[ 1 ].dist && near[ 1 ].dist < near[ 2 ].dist, 'nearest first' );
const chromis = near.find( ( q ) => q.name === 'Blue chromis' );
ok( !! chromis && Math.abs( chromis.depth - 6 ) < 1e-6, `depth is the school's, below the surface (${ chromis && chromis.depth } m for a school at y -6)` );
ok( chromis.count === 20, 'and its fish count comes through' );
ok( names.includes( 'stingray' ), 'a species with no catch entry still names itself (stingray)' );

const two = fishNear( G, 0, 0, SONAR_RANGE, 2, 25 );
ok( two.length === 2, `the map asks for two (${ two.length })` );
ok( Math.hypot( two[ 0 ].x - two[ 1 ].x, two[ 0 ].z - two[ 1 ].z ) >= 25, 'and they are kept apart, so the rings do not overlap' );

ok( fishNear( null, 0, 0, SONAR_RANGE ).length === 0, 'no reef, no reading (no throw)' );
ok( fishNear( [ { center: null } ], 0, 0, SONAR_RANGE ).length === 0, 'and a malformed group is skipped' );
ok( SONAR_FULL > 0 && SONAR_RANGE > 0, 'the constants are sane' );

console.log( fails ? `\n${ fails } FAILED` : '\nsonar: all passed' );
process.exit( fails ? 1 : 0 );
