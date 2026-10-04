// The modelled eagle ray (assets/eagle-ray.glb, baked into src/world/fish/EagleRayData.js by
// tools/creatures/bake-eagle-ray.mjs): both levels of detail decode into well-formed fish-frame geometry
// (nose +z, span 1), and the far procedural level matches its size.
//   node test/eagle-ray.mjs
import { eagleRayGeometry, rayGeometry } from '../src/world/fish/CreatureGeometry.js';
import { PART } from '../src/world/fish/FishGeometry.js';

let fails = 0;
const check = ( ok, msg ) => {

	console.log( ( ok ? 'ok   ' : 'FAIL ' ) + msg );
	if ( ! ok ) fails ++;

};

const stats = ( g ) => {

	const p = g.getAttribute( 'position' ).array, a = g.getAttribute( 'aData' ).array, n = g.getAttribute( 'normal' ).array;
	const s = { min: [ Infinity, Infinity, Infinity ], max: [ - Infinity, - Infinity, - Infinity ], finite: true, parts: {}, wSign: 0, wN: 0, uMax: 0, dzMax: 0 };
	for ( let i = 0; i < p.length / 3; i ++ ) {

		for ( let k = 0; k < 3; k ++ ) {

			if ( ! Number.isFinite( p[ i * 3 + k ] ) || ! Number.isFinite( n[ i * 3 + k ] ) ) s.finite = false;
			s.min[ k ] = Math.min( s.min[ k ], p[ i * 3 + k ] ); s.max[ k ] = Math.max( s.max[ k ], p[ i * 3 + k ] );

		}

		const part = a[ i * 4 + 1 ];
		s.parts[ part ] = ( s.parts[ part ] || 0 ) + 1;
		s.uMax = Math.max( s.uMax, a[ i * 4 ] );
		if ( part === PART.DISC ) { s.dzMax = Math.max( s.dzMax, a[ i * 4 + 2 ] ); s.wSign += a[ i * 4 + 3 ] * Math.sign( n[ i * 3 + 1 ] || 0 ); s.wN ++; }

	}

	s.tris = g.index.array.length / 3;
	return s;

};

const lod0 = stats( eagleRayGeometry( { lod: 0 } ) ), lod1 = stats( eagleRayGeometry( { lod: 1 } ) );
const far = stats( rayGeometry( { lod: 1, eagle: true } ) );
for ( const [ name, s ] of [ [ 'lod 0', lod0 ], [ 'lod 1', lod1 ] ] ) {

	check( s.finite, `${ name }: positions and normals are finite` );
	check( Math.abs( s.max[ 0 ] - 0.5 ) < 0.01 && Math.abs( s.min[ 0 ] + 0.5 ) < 0.01, `${ name }: the span is 1 (x ${ s.min[ 0 ].toFixed( 3 ) } .. ${ s.max[ 0 ].toFixed( 3 ) })` );
	check( Math.abs( s.max[ 2 ] - 0.5 ) < 0.01 && s.min[ 2 ] < - 0.9 && s.min[ 2 ] > - 1.2, `${ name }: snout at z = 0.5, tail tip at z = ${ s.min[ 2 ].toFixed( 2 ) } (the thin end of the whip is decimated away a little)` );
	check( s.parts[ PART.DISC ] > 0 && s.parts[ PART.WHIP ] > 0, `${ name }: disc ${ s.parts[ PART.DISC ] } and whip / fin ${ s.parts[ PART.WHIP ] } vertices` );
	check( s.dzMax > 0.99 && s.dzMax <= 1, `${ name }: the flap reach runs to 1 at the wing tip (${ s.dzMax.toFixed( 3 ) })` );
	check( s.wSign / s.wN > 0.5, `${ name }: the back is +1, the belly -1 (agreement with the surface normal ${ ( s.wSign / s.wN ).toFixed( 2 ) })` );
	check( s.uMax > 1.3 && s.uMax < 1.7, `${ name }: x grows to ${ s.uMax.toFixed( 2 ) } along the tail` );

}

check( lod0.tris > 8000 && lod0.tris < 11000, `lod 0 is ${ lod0.tris } triangles` );
check( lod1.tris > 1200 && lod1.tris < 1700, `lod 1 is ${ lod1.tris } triangles` );
check( Math.abs( far.max[ 0 ] - 0.5 ) < 0.01 && Math.abs( far.min[ 2 ] - lod0.min[ 2 ] ) < 0.1, `the procedural far level has the same span and length (z ${ far.min[ 2 ].toFixed( 2 ) } .. ${ far.max[ 2 ].toFixed( 2 ) })` );
console.log( fails ? `eagle-ray: ${ fails } FAILED` : 'eagle-ray: all passed' );
process.exit( fails ? 1 : 0 );
