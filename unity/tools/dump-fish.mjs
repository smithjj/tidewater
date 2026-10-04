// Oracle for the C# fish port: writes the JS models of FishGeometry.js / FishProps.js (position, normal, aData, index) for a list of
// cases (every species at every level of detail and pose, the cut and split fish, the ice, the leaf and the lobster) and the
// FishProps items the Village adds (props.json).    node unity/tools/dump-fish.mjs <outDir> [seed]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const out = process.argv[ 2 ] || '.';
const seed = + ( process.argv[ 3 ] ?? 7 );
fs.mkdirSync( out, { recursive: true } );

const { SPECIES } = await import( root + '/src/world/fish/FishSpecies.js' );
const { fishGeometry, splitFishGeometry } = await import( root + '/src/world/fish/FishGeometry.js' );
const { iceGeometry, leafGeometry, lobsterGeometry, FishProps } = await import( root + '/src/world/fish/FishProps.js' );
const { Group } = await import( root + '/src/engine/index.js' );

const cases = [];
const bin = [];
let off = 0;
const add = ( name, g ) => {

	const A = g.attributes, n = A.position.count;
	const parts = [ A.position.array, A.normal.array, A.aData.array ].map( ( a ) => Float32Array.from( a ) );
	const idx = Uint32Array.from( g.index.array );
	for ( const p of [ ...parts, idx ] ) bin.push( Buffer.from( p.buffer, p.byteOffset, p.byteLength ) );
	cases.push( { name, vertices: n, indices: idx.length, offset: off } );
	off += parts.reduce( ( s, p ) => s + p.byteLength, 0 ) + idx.byteLength;

};

const fishes = Object.keys( SPECIES ).filter( ( k ) => SPECIES[ k ].top );
for ( const k of fishes ) {

	const S = SPECIES[ k ];
	for ( let lod = 0; lod < 4; lod ++ ) {

		add( `whole:${ k }:${ lod }:dead`, fishGeometry( S, { lod, pose: 'dead', eyes: lod === 0 } ) );
		add( `whole:${ k }:${ lod }:swim`, fishGeometry( S, { lod, pose: 'swim' } ) );

	}

	for ( let lod = 0; lod < 2; lod ++ ) {

		add( `split:${ k }:${ lod }`, splitFishGeometry( S, { lod } ) );
		add( `head:${ k }:${ lod }`, fishGeometry( S, { lod, pose: 'dead', u1: S.opercle + 0.02, eyes: lod === 0 } ) );
		add( `trunk:${ k }:${ lod }`, fishGeometry( S, { lod, pose: 'dead', u0: S.opercle + 0.02 } ) );

	}

}

for ( let lod = 0; lod < 2; lod ++ ) {

	add( `ice::${ lod }`, iceGeometry( lod ) );
	add( `leaf::${ lod }`, leafGeometry( lod ) );
	add( `lobster::${ lod }`, lobsterGeometry( lod ) );

}

fs.writeFileSync( path.join( out, 'models.bin' ), Buffer.concat( bin ) );

// the items the village adds
const { TerrainData } = await import( root + '/src/world/TerrainData.js' );
const { Colliders } = await import( root + '/src/world/Colliders.js' );
const { Village } = await import( root + '/src/world/Village.js' );
let items = null;
FishProps.prototype.build = function () { items = this.items; return new Group(); };
new Village( { scene: { add() {} }, terrain: new TerrainData( seed ), colliders: new Colliders() } );
// the order of Math.random seeds is not reproducible: those items (seed given as undefined) are marked
fs.writeFileSync( path.join( out, 'props.json' ), JSON.stringify( { seed, items } ) );
fs.writeFileSync( path.join( out, 'models.json' ), JSON.stringify( cases ) );
console.log( cases.length, 'models,', bin.reduce( ( s, b ) => s + b.length, 0 ), 'bytes;', items.length, 'fish items ->', out );
