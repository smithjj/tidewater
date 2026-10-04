// Oracle for the C# boat port: HullLines hydrostatics, from the original JS.
//   node unity/tools/dump-boat-hull.mjs <outDir>
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const { HullLines } = await import( root + '/src/world/boat/HullLines.js' );
const out = process.argv[ 2 ] || '.';
fs.mkdirSync( out, { recursive: true } );
const L = new HullLines();
const samples = L.buildHullSamples( 8 );
const j = {
	stem: L.stem, wlStart: L.wlStart, wlEnd: L.wlEnd, waterplaneArea: L.waterplaneArea, canoeVolume: L.canoeVolume,
	cofZ: L.centerOfFlotationZ, cob: [ L.centerOfBuoyancy.x, L.centerOfBuoyancy.y, L.centerOfBuoyancy.z ],
	stationParams: L.stationParams( 60 ),
	section05: Array.from( L.sectionPoints( 0.5, 1 ) ), section1: Array.from( L.sectionPoints( 1, 2 ) ),
	beam: [ -3, -2, -1, 0, 1, 2, 3, 4 ].map( ( z ) => L.halfBeamAt( z ) ), draft: [ -3.5, -2, 0, 2, 3.5 ].map( ( z ) => L.draftAt( z ) ),
	bottomAt: [ [ 0.3, 0 ], [ 0.7, 1.5 ], [ 0, -3 ] ].map( ( [ x, z ] ) => L.bottomAt( x, z ) ),
	hullXAt: [ [ 0, 0.5 ], [ -2, 0.2 ], [ 3, 0.8 ] ].map( ( [ z, y ] ) => L.hullXAt( z, y ) ),
	samples: samples.map( ( s ) => ( { p: [ s.position.x, s.position.y, s.position.z ], area: s.area, depth: s.depth, bottomY: s.bottomY } ) ),
};
fs.writeFileSync( path.join( out, 'hull.json' ), JSON.stringify( j ) );
console.log( 'wl', L.wlStart, L.wlEnd, 'volume', L.canoeVolume, 'samples', samples.length );
