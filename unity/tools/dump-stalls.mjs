// Writes the two stalls' geometry for Unity: Assets/Tidewater/Resources/stalls/{stand,chandlery}.bytes, from the JS FishStand / Chandlery (StallKit's KitBuilder
// merges every plank, post, prop, rope and chain of a stall into one mesh), so the Unity stalls are the very geometry the browser builds. The file:
//   int32 magic 'STL1', int32 vertexCount, int32 indexCount, then vertexCount x ( px py pz nx ny nz u v r g b layer u2 v2 ) float32 and indexCount x uint32
// (sim-local frame: x right, y up, z toward the customer; the C# loader mirrors z). Also <outDir>/stalls.json: counts, bounds and checksums for the oracle.
//   node unity/tools/dump-stalls.mjs [outDir]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const outDir = process.argv[ 2 ] || path.join( root, 'unity/Temp/oracle/stalls' );
const resDir = path.join( root, 'unity/Assets/Tidewater/Resources/stalls' );
fs.mkdirSync( outDir, { recursive: true } );
fs.mkdirSync( resDir, { recursive: true } );

globalThis.__assetFile = async ( url ) => {

	const b = fs.readFileSync( path.join( root, 'public', url ) );
	return b.buffer.slice( b.byteOffset, b.byteOffset + b.byteLength );

};

// the geometry does not depend on the pictures: 1 x 1 stand-ins for the textures (a headless GPU makes the texture arrays)
globalThis.__assetImage = async () => ( { data: new Uint8Array( [ 128, 128, 128, 255 ] ), width: 1, height: 1 } );
const warn = console.warn; console.warn = ( ...a ) => { if ( ! String( a[ 0 ] ).startsWith( 'Vendor: character' ) ) warn( ...a ); };

await import( '../../test/headless.mjs' );
const { GPU } = await import( root + '/src/engine/gpu/GPU.js' );
await GPU.init( { headless: true } );

const { FishStand } = await import( root + '/src/game/FishStand.js' );
const { Chandlery } = await import( root + '/src/game/Chandlery.js' );

const stub = { add() {} };
const terrain = { heightAt: () => 0 };
const out = {};
for ( const [ name, make, mesh ] of [
	[ 'stand', () => new FishStand( { scene: stub, terrain, colliders: null } ), 'FishStandStall' ],
	[ 'chandlery', () => new Chandlery( { scene: stub, terrain, colliders: null } ), 'ChandleryStall' ],
] ) {

	const s = make();
	await s.ready;
	const m = s.group.children.find( ( c ) => c.name === mesh );
	if ( ! m ) throw new Error( name + ': the kit mesh was not built (the plain stall is the fallback)' );
	const g = m.geometry, A = g.attributes;
	const n = A.position.count, ni = g.index.count;
	const F = 14;
	const head = Buffer.alloc( 12 );
	head.writeInt32LE( 0x314c5453, 0 ); head.writeInt32LE( n, 4 ); head.writeInt32LE( ni, 8 );
	const vf = new Float32Array( n * F );
	for ( let i = 0; i < n; i ++ ) {

		const o = i * F;
		vf.set( [ A.position.getX( i ), A.position.getY( i ), A.position.getZ( i ), A.normal.getX( i ), A.normal.getY( i ), A.normal.getZ( i ), A.uv.getX( i ), A.uv.getY( i ),
			A.color.getX( i ), A.color.getY( i ), A.color.getZ( i ), A.aLayer.getX( i ), A.aUV2.getX( i ), A.aUV2.getY( i ) ], o );

	}

	const idx = new Uint32Array( ni );
	for ( let i = 0; i < ni; i ++ ) idx[ i ] = g.index.getX( i );
	fs.writeFileSync( path.join( resDir, name + '.bytes' ), Buffer.concat( [ head, Buffer.from( vf.buffer ), Buffer.from( idx.buffer ) ] ) );
	g.computeBoundingBox();
	const sum = ( a ) => { let s = 0; for ( let i = 0; i < a.length; i ++ ) s += a[ i ] * ( 1 + ( i % 7 ) * 0.125 ); return s; };
	const layers = {};
	for ( let i = 0; i < n; i ++ ) layers[ A.aLayer.getX( i ) ] = ( layers[ A.aLayer.getX( i ) ] || 0 ) + 1;
	out[ name ] = { vertices: n, indices: ni, min: g.boundingBox.min.toArray(), max: g.boundingBox.max.toArray(), sumVerts: sum( vf ), sumIdx: sum( idx ), layers };
	console.log( name, n, 'vertices', ni / 3, 'triangles', ( 12 + vf.byteLength + idx.byteLength ) / 1e6, 'MB' );

}

fs.writeFileSync( path.join( outDir, 'stalls.json' ), JSON.stringify( out ) );
