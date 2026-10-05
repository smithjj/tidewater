// The modelled southern stingray (assets/stingray-family.glb, baked into src/world/fish/StingrayData.js by
// tools/creatures/bake-stingray-family.mjs): all four levels of detail decode into well-formed fish-frame geometry
// (nose +z, span 1) of the same size, each lighter than the last.
//   node test/stingray.mjs
import { stingrayGeometry } from '../src/world/fish/CreatureGeometry.js';
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

const lods = [ 0, 1, 2, 3 ].map( ( lod ) => stats( stingrayGeometry( { lod } ) ) ), [ lod0, lod1, lod2, lod3 ] = lods;
for ( const [ name, s ] of lods.map( ( l, i ) => [ 'lod ' + i, l ] ) ) {

	check( s.finite, `${ name }: positions and normals are finite` );
	check( Math.abs( s.max[ 0 ] - 0.5 ) < 0.02 && Math.abs( s.min[ 0 ] + 0.5 ) < 0.02, `${ name }: the span is 1 (x ${ s.min[ 0 ].toFixed( 3 ) } .. ${ s.max[ 0 ].toFixed( 3 ) })` );
	check( Math.abs( s.max[ 2 ] - 0.5 ) < 0.01 && s.min[ 2 ] < - 1.55 && s.min[ 2 ] > - 1.8, `${ name }: snout at z = 0.5, tail tip at z = ${ s.min[ 2 ].toFixed( 2 ) }` );
	check( s.parts[ PART.DISC ] > 0 && s.parts[ PART.WHIP ] > 0, `${ name }: disc ${ s.parts[ PART.DISC ] } and whip / fin ${ s.parts[ PART.WHIP ] } vertices` );
	check( s.dzMax > 0.99 && s.dzMax <= 1, `${ name }: the flap reach runs to 1 at the wing tip (${ s.dzMax.toFixed( 3 ) })` );
	check( s.wSign / s.wN > 0.5, `${ name }: the back is +1, the belly -1 (agreement with the surface normal ${ ( s.wSign / s.wN ).toFixed( 2 ) })` );
	check( s.uMax > 2.0 && s.uMax < 2.4, `${ name }: x grows to ${ s.uMax.toFixed( 2 ) } along the tail` );

}

check( lod0.tris > 5000 && lod0.tris < 6500, `lod 0 is ${ lod0.tris } triangles` );
check( lod1.tris > 900 && lod1.tris < 1300, `lod 1 is ${ lod1.tris } triangles` );
check( lod2.tris > 300 && lod2.tris < 550, `lod 2 is ${ lod2.tris } triangles` );
check( lod3.tris > 200 && lod3.tris < 400, `lod 3 is ${ lod3.tris } triangles` );
console.log( fails ? `stingray: ${ fails } FAILED` : 'stingray: all passed' );
process.exit( fails ? 1 : 0 );
