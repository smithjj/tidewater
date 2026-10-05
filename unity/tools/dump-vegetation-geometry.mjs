// Writes the vegetation meshes for Unity: Assets/Tidewater/Resources/vegetation/<name>.bytes, from the JS plant builders (src/world/vegetation/PlantGeometry.js), so the Unity
// plants are the very geometry the browser builds. The file:
//   int32 magic 'VEG1', int32 vertexCount, int32 indexCount, then vertexCount x ( px py pz nx ny nz u v | aVeg x4 | aMat x4 | aLobe x4 ) float32 and indexCount x uint32
// (sim frame, plant local: y up; the C# loader mirrors z and flips the winding). Also vegetation.json: counts, bounds, the lobe table and the impostor groups' frames
// (axisSphere of Vegetation.js) for the oracle and the impostor bake.
//   node unity/tools/dump-vegetation-geometry.mjs [outDir]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const outDir = process.argv[ 2 ] || path.join( root, 'unity/Temp/oracle/vegetation' );
const resDir = path.join( root, 'unity/Assets/Tidewater/Resources/vegetation' );
fs.mkdirSync( outDir, { recursive: true } );
fs.mkdirSync( resDir, { recursive: true } );

await import( '../../test/headless.mjs' );
const { GPU } = await import( root + '/src/engine/gpu/GPU.js' );
await GPU.init( { headless: true } );

const PG = await import( root + '/src/world/vegetation/PlantGeometry.js' );

const meshes = {
	palmNear: PG.buildPalmNear(), palmFar: PG.buildPalmFar(), understory: PG.buildUnderstory(), bananas: PG.buildBananas(),
	monstera: PG.buildMonsteraMesh(), broadleaf: PG.buildBroadleaf(), canopy: PG.buildCanopyNear(),
	tree: PG.buildTreeNear(), shrub: PG.buildShrubNear(),
};

const NAMES = [ 'position', 'normal', 'uv', 'aVeg', 'aMat', 'aLobe' ];
const SIZES = [ 3, 3, 2, 4, 4, 4 ];
const F = 20;
const summary = { meshes: {} };

for ( const [ name, m ] of Object.entries( meshes ) ) {

	const g = m.geometry, A = g.attributes;
	const n = A.position.count, ni = g.index.count;
	const head = Buffer.alloc( 12 );
	head.writeInt32LE( 0x31474556, 0 ); head.writeInt32LE( n, 4 ); head.writeInt32LE( ni, 8 );
	const vf = new Float32Array( n * F );
	let sum = 0;
	for ( let i = 0; i < n; i ++ ) {

		let o = i * F;
		NAMES.forEach( ( a, k ) => {

			const at = A[ a ];
			const get = [ 'getX', 'getY', 'getZ', 'getW' ];
			for ( let c = 0; c < SIZES[ k ]; c ++ ) vf[ o ++ ] = at[ get[ c ] ]( i );

		} );

	}

	for ( let i = 0; i < vf.length; i ++ ) sum += vf[ i ] * ( 1 + ( i % 7 ) * 0.01 );
	const idx = new Uint32Array( ni );
	for ( let i = 0; i < ni; i ++ ) idx[ i ] = g.index.getX( i );
	const buf = Buffer.concat( [ head, Buffer.from( vf.buffer ), Buffer.from( idx.buffer ) ] );
	fs.writeFileSync( path.join( resDir, name + '.bytes' ), buf );
	let imax = 0;
	for ( let i = 0; i < ni; i ++ ) imax = Math.max( imax, idx[ i ] );
	g.computeBoundingBox();
	const bb = g.boundingBox;
	summary.meshes[ name ] = { vertices: n, indices: ni, maxIndex: imax, checksum: sum, min: [ bb.min.x, bb.min.y, bb.min.z ], max: [ bb.max.x, bb.max.y, bb.max.z ] };

}

// the impostor frames: bounding sphere about the vertical axis of the tree / shrub meshes (Vegetation.js axisSphere)
function axisSphere( geometry ) {

	const p = geometry.attributes.position;
	let y0 = Infinity, y1 = - Infinity;
	for ( let i = 0; i < p.count; i ++ ) { y0 = Math.min( y0, p.getY( i ) ); y1 = Math.max( y1, p.getY( i ) ); }
	const cy = ( y0 + y1 ) * 0.5;
	let r = 0, rh = 0;
	for ( let i = 0; i < p.count; i ++ ) {

		r = Math.max( r, Math.hypot( p.getX( i ), p.getY( i ) - cy, p.getZ( i ) ) );
		rh = Math.max( rh, Math.hypot( p.getX( i ), p.getZ( i ) ) );

	}

	return { centerY: cy, radius: r * 1.02, rh: rh * 1.03, hv: ( y1 - y0 ) * 0.5 * 1.03 };

}

summary.groups = [ axisSphere( meshes.tree.geometry ), axisSphere( meshes.shrub.geometry ) ];
summary.lobeTable = Array.from( PG.LOBE_TABLE );
summary.treeVariants = PG.TREE_VARIANTS;
summary.shrubVariants = PG.SHRUB_VARIANTS;
summary.treeLobes = PG.TREE_LOBES; summary.shrubLobes = PG.SHRUB_LOBES;
summary.treeH = PG.TREE_H; summary.shrubH = PG.SHRUB_H;
fs.writeFileSync( path.join( outDir, 'vegetation.json' ), JSON.stringify( summary, null, 1 ) );
// what the canopy shaders and the impostor bake need at run time
fs.writeFileSync( path.join( resDir, 'canopyInfo.json' ), JSON.stringify( { groups: summary.groups, lobeTable: summary.lobeTable, treeVariants: summary.treeVariants, shrubVariants: summary.shrubVariants } ) );
console.log( Object.entries( summary.meshes ).map( ( [ k, v ] ) => `${ k } ${ v.vertices }v ${ v.indices / 3 }t` ).join( '\n' ) );
