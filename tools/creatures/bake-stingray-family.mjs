// Bakes the southern stingray (assets/stingray-family.glb) into the fish frame and four levels of detail
// (src/world/fish/StingrayData.js), for CreatureGeometry.stingrayGeometry.
//
// The glb is a study of three southern stingrays side by side on a seafloor under a water box (01 moss olive, 02 silty taupe,
// 03 kelp slate): each a closed 31,000-triangle disc with the blunt head, a 5,000-triangle tapering whip, two pelvic fins and
// the small anatomy (eyes, spiracles, gills, mouth, nostrils, serrations). The three discs are one shape: the same y and z, the
// x scaled 1 : 0.945 : 1.055, and the whips differ by 6% in length, so one is baked (01, the middle one) and the three
// colourings are the fish material's (FishMaterial.js: the dorsal colours of the three in the glb are procedural and were not
// exported, so they follow the names). The seafloor and the water are left out.
// As in the eagle ray (bake-eagle-ray.mjs) what is kept is the shape, in the fish frame (nose +z, span 1, snout at z = 0.5), with the
// swimming in the vertex shader: the disc as PART.DISC (aData.x = 0.5 - z, aData.z = |x| / 0.5, aData.w = +1 back / -1 belly), the
// whip and the pelvic fins as PART.WHIP; the eyes are rebuilt with the game's own eye shape at the glb's eye positions.
//
//   node tools/creatures/bake-stingray-family.mjs [assets/stingray-family.glb] [src/world/fish/StingrayData.js]
import { readFileSync, writeFileSync } from 'node:fs';
import { parseGLB } from '../../src/engine/loaders/GLTF.js';
import { Matrix4, Quaternion, Vector3 } from '../../src/engine/math/index.js';
import { weld, simplify, tubeWhip, pack, SCALE } from './ray-common.mjs';

const SRC = process.argv[ 2 ] || new URL( '../../assets/stingray-family.glb', import.meta.url ).pathname;
const OUT = process.argv[ 3 ] || new URL( '../../src/world/fish/StingrayData.js', import.meta.url ).pathname;
const PART_DISC = 15, PART_WHIP = 16; // FishGeometry.js PART
// triangles per part at each level of detail: disc, whip, pelvic fin (each)
const LODS = [ { disc: 5000, whip: 500, pelvic: 60 }, { disc: 900, tube: [ 12, 5 ], pelvic: 24 }, { disc: 300, tube: [ 8, 4 ], pelvic: 12 }, { disc: 200, tube: [ 5, 3 ], pelvic: 8 } ];

// ------------------------------------------------------------------ read the glb: world-space triangles per part
const buf = readFileSync( SRC );
const gltf = parseGLB( buf.buffer.slice( buf.byteOffset, buf.byteOffset + buf.byteLength ) );
const root = gltf.roots.find( ( i ) => /^STINGRAY 01/i.test( gltf.nodes[ i ].name ) );
const found = {}, eyes = [];
const visit = ( i, parent ) => {

	const n = gltf.nodes[ i ];
	const local = new Matrix4().compose( new Vector3( ...n.t ), new Quaternion( ...n.r ), new Vector3( ...n.s ) );
	const world = parent ? new Matrix4().multiplyMatrices( parent, local ) : local;
	const key = /^Disc \|/.test( n.name ) ? 'disc' : /^Tail \| tapering whip/.test( n.name ) ? 'whip' : /^[LR] \| pelvic fin/.test( n.name ) ? 'pelvic' : null;
	if ( /^[LR] \| black-brown eye/.test( n.name ) ) eyes.push( new Vector3().setFromMatrixPosition( world ) );
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
if ( ! found.disc || ! found.whip || ! found.pelvic || eyes.length !== 2 ) throw new Error( 'the glb does not have the expected parts' );
const parts = {};
for ( const k of Object.keys( found ) ) parts[ k ] = weld( found[ k ] );

// ------------------------------------------------------------------ into the fish frame
// the file: nose -z, tail +z, back +y, span +-1.27 m. The fish frame: nose +z, back +y, span 1 (half 0.5), the snout at z = 0.5
let zNose = Infinity, xMin = Infinity, xMax = - Infinity, yMin = Infinity, yMax = - Infinity;
for ( let i = 0; i < parts.disc.pos.length; i += 3 ) {

	xMin = Math.min( xMin, parts.disc.pos[ i ] ); xMax = Math.max( xMax, parts.disc.pos[ i ] );
	yMin = Math.min( yMin, parts.disc.pos[ i + 1 ] ); yMax = Math.max( yMax, parts.disc.pos[ i + 1 ] );
	zNose = Math.min( zNose, parts.disc.pos[ i + 2 ] );

}

const S = 1 / ( xMax - xMin ), xc = ( xMax + xMin ) / 2, yc = ( yMax + yMin ) / 2;
// the whip is straight (within 5 cm of the midline): no straightening as with the eagle ray's
for ( const k of Object.keys( parts ) ) {

	const p = parts[ k ].pos;
	for ( let i = 0; i < p.length; i += 3 ) {

		const x = p[ i ];
		p[ i ] = - ( x - xc ) * S; // a half turn about y (nose to +z)
		p[ i + 1 ] = ( p[ i + 1 ] - yc ) * S;
		p[ i + 2 ] = 0.5 - ( p[ i + 2 ] - zNose ) * S;

	}

}

const eyeFrame = eyes.map( ( e ) => [ - ( e.x - xc ) * S, ( e.y - yc ) * S, 0.5 - ( e.z - zNose ) * S ] );

// ------------------------------------------------------------------ levels of detail
const outLods = [];
const report = [];
for ( const lod of LODS ) {

	// the far levels' whip is a tube that keeps the tail's length (the collapse would eat the thin tip)
	const whip = lod.tube ? tubeWhip( parts.whip, ...lod.tube ) : simplify( parts.whip, lod.whip );
	const meshes = [ { mesh: simplify( parts.disc, lod.disc ), part: PART_DISC, k: 'disc' }, { mesh: whip, part: PART_WHIP, k: 'whip' } ];
	// the two pelvic fins are one welded part that is not connected: a target for each
	meshes.push( { mesh: simplify( parts.pelvic, lod.pelvic * 2 ), part: PART_WHIP, k: 'pelvic' } );
	const bytes = pack( meshes );
	outLods.push( bytes.toString( 'base64' ) );
	report.push( meshes.map( ( m ) => m.k + ' ' + m.mesh.idx.length / 3 ).join( ', ' ) + ' -> ' + bytes.length + ' bytes' );

}

// the whip and the fins wave from where they leave the disc: aData.x of those vertices is measured from the root (and stretched so the tip
// keeps its place), so the root stays on the disc instead of swinging sideways with the wave (the shader's envelope grows with x)
const disc = parts.disc.pos;
let zTail = Infinity, zRear = Infinity, zRoot = - Infinity;
for ( let i = 2; i < parts.whip.pos.length; i += 3 ) { zTail = Math.min( zTail, parts.whip.pos[ i ] ); zRoot = Math.max( zRoot, parts.whip.pos[ i ] ); }
for ( let i = 2; i < disc.length; i += 3 ) zRear = Math.min( zRear, disc[ i ] );
const uRoot = 0.5 - zRoot, uTip = 0.5 - zTail;
const whipU = { root: + uRoot.toFixed( 4 ), k: + ( uTip / ( uTip - uRoot ) ).toFixed( 4 ) };
const wing = disc.reduce( ( m, _, i ) => ( i % 3 === 0 && Math.abs( disc[ i ] ) > m.x ? { x: Math.abs( disc[ i ] ), z: disc[ i + 2 ] } : m ), { x: 0, z: 0 } );
const meta = { whipU, discZ: [ + zRear.toFixed( 3 ), 0.5 ], tailEndZ: + zTail.toFixed( 3 ), wingTip: { x: + wing.x.toFixed( 3 ), z: + wing.z.toFixed( 3 ) }, eyes: eyeFrame.map( ( e ) => e.map( ( v ) => + v.toFixed( 4 ) ) ) };

const chunk = ( s ) => s.match( /.{1,110}/g ).map( ( l ) => `\t'${ l }',` ).join( '\n' );
const js = `// GENERATED by tools/creatures/bake-stingray-family.mjs from assets/stingray-family.glb: do not edit.
// The southern stingray in the fish frame (nose +z, span 1, snout at z = 0.5), decimated to four levels of detail:
// ${ report.map( ( r, i ) => 'lod ' + i + ': ' + r ).join( '; ' ) }.
// Per level: uint32 vertex count, uint32 triangle count, then per vertex int16 x y z (/ ${ SCALE }), uint8 part, int8 w (+1 back, -1 belly), then uint16 indices.
export const STINGRAY = {
	scale: ${ SCALE },
	meta: ${ JSON.stringify( meta ) },
	lods: [
${ outLods.map( ( l ) => '\t[\n' + chunk( l ).replace( /^\t/gm, '\t\t' ) + '\n\t].join( \'\' ),' ).join( '\n' ) }
	],
};
`;
writeFileSync( OUT, js );
console.log( report.join( '\n' ) );
console.log( 'meta', JSON.stringify( meta ) );
console.log( 'wrote', OUT, js.length, 'bytes' );
