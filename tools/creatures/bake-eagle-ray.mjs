// Bakes the spotted eagle ray (assets/eagle-ray.glb) into the fish frame and four levels of detail
// (src/world/fish/EagleRayData.js), for CreatureGeometry.eagleRayGeometry.
//
// The glb is one 130,000-triangle study: a continuous sculpted disc (69,408 triangles, welded, closed), a
// five-metre tapering whip (bent sideways, posed), a dorsal fin and two pelvic fins, and all the small anatomy
// (eyes, gills, spiracles, mouth, 32 rostral pores, nostrils, barbs). The game's ray swims in the fish frame
// (CreatureGeometry.js: nose +z, total span 1, aData per vertex), with the swimming in the vertex shader
// (FishMaterial.js fishSwimOffset), so what is kept is the shape:
//   - the disc, as PART.DISC: aData.x = 0.5 - z, aData.z = |x| / 0.5 (distance from the midline, 1 at the wing tip,
//     which the margins' flap scales with), aData.w = +1 on the back, -1 on the belly (the surface normal's side);
//   - the whip, straightened (the file's is bent 0.6 m sideways) and the fins, as PART.WHIP;
//   - the eyes are rebuilt with the game's own eye shape at the glb's eye positions (CreatureGeometry), everything
//     fine is dropped (the colour comes from the fish material: black back with white rings, white belly).
// The decimation is a quadric error edge collapse (Garland & Heckbert, with boundary planes and a flip check)
// run per part: the disc to 8,000 / 1,200 / 350 / 140 triangles, the whip, the dorsal and pelvic fins to a few hundred and fewer (the whip of the last two levels is a tube of a few rings that keeps
// the tail's length: the collapse eats its thin tip).
//
//   node tools/creatures/bake-eagle-ray.mjs [assets/eagle-ray.glb] [src/world/fish/EagleRayData.js]
import { readFileSync, writeFileSync } from 'node:fs';
import { parseGLB } from '../../src/engine/loaders/GLTF.js';
import { Matrix4, Quaternion, Vector3 } from '../../src/engine/math/index.js';
import { weld, simplify, tubeWhip, pack, SCALE } from './ray-common.mjs';

const SRC = process.argv[ 2 ] || new URL( '../../assets/eagle-ray.glb', import.meta.url ).pathname;
const OUT = process.argv[ 3 ] || new URL( '../../src/world/fish/EagleRayData.js', import.meta.url ).pathname;
const PART_DISC = 15, PART_WHIP = 16; // FishGeometry.js PART
// triangles per part at each level of detail: disc, whip, dorsal fin, pelvic fin (each)
const LODS = [ { disc: 8000, whip: 700, dorsal: 200, pelvic: 200 }, { disc: 1200, tube: [ 12, 5 ], dorsal: 50, pelvic: 50 }, { disc: 350, tube: [ 8, 4 ], dorsal: 20, pelvic: 14 }, { disc: 140, tube: [ 5, 3 ], dorsal: 10, pelvic: 8 } ];

// ------------------------------------------------------------------ read the glb: world-space triangles per part
const buf = readFileSync( SRC );
const gltf = parseGLB( buf.buffer.slice( buf.byteOffset, buf.byteOffset + buf.byteLength ) );
const root = gltf.roots.find( ( i ) => /EAGLE RAY/i.test( gltf.nodes[ i ].name ) );
const found = {};
const visit = ( i, parent ) => {

	const n = gltf.nodes[ i ];
	const local = new Matrix4().compose( new Vector3( ...n.t ), new Quaternion( ...n.r ), new Vector3( ...n.s ) );
	const world = parent ? new Matrix4().multiplyMatrices( parent, local ) : local;
	const key = /^Ray \| continuous/.test( n.name ) ? 'disc' : /^Tail \| continuous/.test( n.name ) ? 'whip' : /^Dorsal fin/.test( n.name ) ? 'dorsal' : /^[LR] \| rounded pelvic/.test( n.name ) ? 'pelvic' : null;
	if ( key && n.mesh !== undefined ) for ( const prim of gltf.meshes[ n.mesh ] ) {

		const e = world.elements, p = prim.attributes.POSITION.array;
		const pos = new Float64Array( p.length );
		for ( let k = 0; k < p.length; k += 3 ) {

			pos[ k ] = e[ 0 ] * p[ k ] + e[ 4 ] * p[ k + 1 ] + e[ 8 ] * p[ k + 2 ] + e[ 12 ];
			pos[ k + 1 ] = e[ 1 ] * p[ k ] + e[ 5 ] * p[ k + 1 ] + e[ 9 ] * p[ k + 2 ] + e[ 13 ];
			pos[ k + 2 ] = e[ 2 ] * p[ k ] + e[ 6 ] * p[ k + 1 ] + e[ 10 ] * p[ k + 2 ] + e[ 14 ];

		}

		( found[ key ] ||= [] ).push( { pos, idx: Uint32Array.from( prim.indices ) } );

	}

	for ( const c of n.children ) visit( c, world );

};

visit( root, null );
const eyes = [];
const eyeVisit = ( i, parent ) => {

	const n = gltf.nodes[ i ];
	const local = new Matrix4().compose( new Vector3( ...n.t ), new Quaternion( ...n.r ), new Vector3( ...n.s ) );
	const world = parent ? new Matrix4().multiplyMatrices( parent, local ) : local;
	if ( /^[LR] \| eye globe/.test( n.name ) ) eyes.push( new Vector3().setFromMatrixPosition( world ) );
	for ( const c of n.children ) eyeVisit( c, world );

};

eyeVisit( root, null );
if ( ! found.disc || ! found.whip || ! found.dorsal || ! found.pelvic || eyes.length !== 2 ) throw new Error( 'the glb does not have the expected parts' );


const parts = {};
for ( const k of Object.keys( found ) ) parts[ k ] = weld( found[ k ] );

// ------------------------------------------------------------------ into the fish frame
// the file: nose -z, tail +z, back +y, span +-2.3 m. The fish frame: nose +z, back +y, span 1 (half 0.5), the snout at z = 0.5
let zNose = Infinity, xMin = Infinity, xMax = - Infinity, yMin = Infinity, yMax = - Infinity;
for ( let i = 0; i < parts.disc.pos.length; i += 3 ) {

	xMin = Math.min( xMin, parts.disc.pos[ i ] ); xMax = Math.max( xMax, parts.disc.pos[ i ] );
	yMin = Math.min( yMin, parts.disc.pos[ i + 1 ] ); yMax = Math.max( yMax, parts.disc.pos[ i + 1 ] );
	zNose = Math.min( zNose, parts.disc.pos[ i + 2 ] );

}

const S = 1 / ( xMax - xMin ), xc = ( xMax + xMin ) / 2, yc = ( yMax + yMin ) / 2;
// the whip's own centre line (it is posed bent): x of its vertices averaged per slice of z, subtracted from every tail vertex
const whip = parts.whip.pos;
const slice = 0.05, bins = new Map();
for ( let i = 0; i < whip.length; i += 3 ) {

	const b = Math.round( whip[ i + 2 ] / slice );
	const e = bins.get( b ) || { s: 0, n: 0 };
	e.s += whip[ i ]; e.n ++;
	bins.set( b, e );

}

const centre = ( z ) => {

	const b = z / slice, b0 = Math.floor( b ), f = b - b0;
	const a = bins.get( b0 ), c = bins.get( b0 + 1 );
	const va = a ? a.s / a.n : c ? c.s / c.n : 0, vc = c ? c.s / c.n : va;
	return va + ( vc - va ) * f;

};

for ( const k of Object.keys( parts ) ) {

	const p = parts[ k ].pos;
	for ( let i = 0; i < p.length; i += 3 ) {

		const x = p[ i ] - ( k === 'whip' ? centre( p[ i + 2 ] ) : 0 );
		p[ i ] = - ( x - xc ) * S; // a half turn about y (nose to +z)
		p[ i + 1 ] = ( p[ i + 1 ] - yc ) * S;
		p[ i + 2 ] = 0.5 - ( p[ i + 2 ] - zNose ) * S;

	}

}

const eyeFrame = eyes.map( ( e ) => [ - ( e.x - xc ) * S, ( e.y - yc ) * S, 0.5 - ( e.z - zNose ) * S ] );

const outLods = [];
const report = [];
for ( const lod of LODS ) {

	const meshes = [];
	for ( const [ k, part ] of [ [ 'disc', PART_DISC ], [ 'whip', PART_WHIP ], [ 'dorsal', PART_WHIP ] ] ) {

		// the far levels' whip is a tube that keeps the tail's length (the collapse would eat the thin tip)
		const mesh = k === 'whip' && lod.tube ? tubeWhip( parts.whip, ...lod.tube ) : simplify( parts[ k ], lod[ k ] );
		meshes.push( { mesh, part, k } );

	}

	// the two pelvic fins are one welded part that is not connected: a target for each
	meshes.push( { mesh: simplify( parts.pelvic, lod.pelvic * 2 ), part: PART_WHIP, k: 'pelvic' } );
	const bytes = pack( meshes );
	outLods.push( bytes.toString( 'base64' ) );
	report.push( meshes.map( ( m ) => m.k + ' ' + m.mesh.idx.length / 3 ).join( ', ' ) + ' -> ' + bytes.length + ' bytes' );

}

// the planform for the procedural far levels (CreatureGeometry): the outline of the disc, nose to the rear, right half
const disc = parts.disc.pos;
const outline = [];
for ( let z = 0.5; z > - 0.1; z -= 0.05 ) {

	let mx = 0;
	for ( let i = 0; i < disc.length; i += 3 ) if ( Math.abs( disc[ i + 2 ] - z ) < 0.025 ) mx = Math.max( mx, Math.abs( disc[ i ] ) );
	outline.push( [ + mx.toFixed( 3 ), + z.toFixed( 3 ) ] );

}

const wide = outline.reduce( ( m, p ) => ( p[ 0 ] > m[ 0 ] ? p : m ), [ 0, 0 ] );
const wing = disc.reduce( ( m, _, i ) => ( i % 3 === 0 && Math.abs( disc[ i ] ) > m.x ? { x: Math.abs( disc[ i ] ), z: disc[ i + 2 ] } : m ), { x: 0, z: 0 } );
let zTail = Infinity, zRear = Infinity;
for ( let i = 2; i < parts.whip.pos.length; i += 3 ) zTail = Math.min( zTail, parts.whip.pos[ i ] );
for ( let i = 2; i < disc.length; i += 3 ) zRear = Math.min( zRear, disc[ i ] );
// the whip and the fins wave from where they leave the disc: aData.x of those vertices is measured from the root (and stretched so the tip
// keeps its place), so the root stays on the disc instead of swinging sideways with the wave (the shader's envelope grows with x)
let zRoot = - Infinity;
for ( let i = 2; i < parts.whip.pos.length; i += 3 ) zRoot = Math.max( zRoot, parts.whip.pos[ i ] );
const uRoot = 0.5 - zRoot, uTip = 0.5 - zTail;
const whipU = { root: + uRoot.toFixed( 4 ), k: + ( uTip / ( uTip - uRoot ) ).toFixed( 4 ) };
const meta = { whipU, discZ: [ + zRear.toFixed( 3 ), 0.5 ], tailEndZ: + zTail.toFixed( 3 ), wingTip: { x: + wing.x.toFixed( 3 ), z: + wing.z.toFixed( 3 ) }, eyes: eyeFrame.map( ( e ) => e.map( ( v ) => + v.toFixed( 4 ) ) ), planform: outline, widest: wide };

const chunk = ( s ) => s.match( /.{1,110}/g ).map( ( l ) => `\t'${ l }',` ).join( '\n' );
const js = `// GENERATED by tools/creatures/bake-eagle-ray.mjs from assets/eagle-ray.glb: do not edit.
// The spotted eagle ray in the fish frame (nose +z, span 1, snout at z = 0.5), decimated to four levels of detail:
// ${ report.map( ( r, i ) => 'lod ' + i + ': ' + r ).join( '; ' ) }.
// Per level: uint32 vertex count, uint32 triangle count, then per vertex int16 x y z (/ ${ SCALE }), uint8 part, int8 w (+1 back, -1 belly), then uint16 indices.
export const EAGLE_RAY = {
	scale: ${ SCALE },
	meta: ${ JSON.stringify( meta ) },
	lods: [
${ outLods.map( ( l ) => '\t[\n' + chunk( l ).replace( /^\t/gm, '\t\t' ) + '\n\t].join( \'\' ),' ).join( '\n' ) }
	],
};
`;
writeFileSync( OUT, js );
console.log( report.join( '\n' ) );
console.log( 'meta', JSON.stringify( meta ).slice( 0, 400 ) );
console.log( 'wrote', OUT, js.length, 'bytes' );
