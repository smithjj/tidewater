// Oracle for the C# Breakers.BuildStations: runs the original JS buildStations (src/ocean/Breakers.js) on the original TerrainData.
//   node unity/tools/dump-breaker-stations.mjs <outDir>
// Writes stations.f32 (x, z, nx, nz per station, little endian float32). Breakers.js imports the whole WebGPU engine, so the
// function is cut out of the source text and run on its own.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const { TerrainData } = await import( root + '/src/world/TerrainData.js' );

const src = fs.readFileSync( root + '/src/ocean/Breakers.js', 'utf8' );
const at = src.indexOf( 'export function buildStations' );
if ( at < 0 ) throw new Error( 'buildStations not found' );
const buildStations = new Function( src.slice( at ).replace( 'export function buildStations', 'function buildStations' ) + '\nreturn buildStations;' )();

const out = process.argv[ 2 ] || '.';
fs.mkdirSync( out, { recursive: true } );
const T = new TerrainData( 7 );
const st = buildStations( T );
fs.writeFileSync( path.join( out, 'stations.f32' ), Buffer.from( st.data.buffer, st.data.byteOffset, st.data.byteLength ) );
fs.writeFileSync( path.join( out, 'summary.json' ), JSON.stringify( { count: st.count, spacing: st.spacing, first: Array.from( st.data.slice( 0, 4 ) ), last: Array.from( st.data.slice( - 4 ) ) } ) );
console.log( 'stations', st.count, 'spacing', st.spacing );
