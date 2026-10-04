// Oracle for the C# terrain port: runs the original JS TerrainData and writes its output to a folder.
//   node unity/tools/dump-terrain.mjs <outDir> [seed]
// Writes heights.f32, rock.f32 (little endian float32), sand/path/gully/seagrass/rubble/scarp .u8,
// noise.json (Noise2D samples) and summary.json.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const { TerrainData } = await import( root + '/src/world/TerrainData.js' );
const { Noise2D, mulberry32 } = await import( root + '/src/util/Noise.js' );
const { hash2, erosionNoise } = await import( root + '/src/world/terrain/TerrainNoise.js' );
const { ridgeEnvelope } = await import( root + '/src/world/terrain/IslandShape.js' );
const { makeLaceTexture } = await import( './lace-data.mjs' );
const { seaDetailNoise } = await import( './seadetail-data.mjs' );
const { computeShoreField } = await import( root + '/src/world/ShoreField.js' );
const { WORLD } = await import( root + '/src/world/WorldLayout.js' );
const { bakeTerrainMaps } = await import( root + '/src/world/terrain/TerrainBake.js' );
// DetailTextures.js builds a GPU texture; its pixel loop is all we need, so run it against a stub Texture
const { getDetailPixels } = await import( './detail-pixels.mjs' );

const out = process.argv[ 2 ] || '.';
const seed = + ( process.argv[ 3 ] ?? 7 );
fs.mkdirSync( out, { recursive: true } );

const t0 = performance.now();
const T = new TerrainData( seed );
const ms = Math.round( performance.now() - t0 );

const w = ( name, arr ) => fs.writeFileSync( path.join( out, name ), Buffer.from( arr.buffer, arr.byteOffset, arr.byteLength ) );
w( 'heights.f32', T.heights ); w( 'rock.f32', T.rock );
for ( const k of [ 'sand', 'path', 'gully', 'seagrass', 'rubble', 'scarp' ] ) w( k + '.u8', T[ k ] );
const maps = bakeTerrainMaps( T );
const shore = computeShoreField( T, { res: 512, swellDir: [ WORLD.swellDir.x, WORLD.swellDir.y ] } );
w( 'shore.f32', shore.data ); w( 'shore_depth.f32', shore.depth );
{ const lace = await makeLaceTexture( root ); w( 'lace.u8', lace ); w( 'seadetail.u16', await seaDetailNoise( root ) ); }
w( 'normal.u8', maps.normal ); w( 'splat.u8', maps.splat );
w( 'detail.u8', await getDetailPixels( root ) );

// small-function samples
const n = new Noise2D( seed ), rand = mulberry32( 12345 );
const samples = [];
for ( let i = 0; i < 200; i ++ ) {

	const x = ( rand() - 0.5 ) * 2000, z = ( rand() - 0.5 ) * 2000;
	samples.push( { x, z, noise: n.noise( x / 37, z / 37 ), fbm: n.fbm( x / 100, z / 100, 4 ), ridged: n.ridged( x / 25, z / 25, 4 ),
		hash: hash2( Math.floor( x ), Math.floor( z ), 11 ), env: ridgeEnvelope( x, z ), erosion: erosionNoise( x, z, 0.4, - 0.3 ),
		coast: T.coastDistance( x, z ).d, height: T.heightAt( x, z ), heightFn: T.heightFn( x, z ).h } );

}

fs.writeFileSync( path.join( out, 'noise.json' ), JSON.stringify( { samples } ) );
fs.writeFileSync( path.join( out, 'summary.json' ), JSON.stringify( { seed, ms, timings: T.timings, rockSites: T.rockSites.length, mmLevels: T.mmLevels.length, bounds: T.boundsFor( - 100, - 100, 100, 100 ) }, null, 1 ) );
console.log( 'terrain generated in', ms, 'ms ->', out );
