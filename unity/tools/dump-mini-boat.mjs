// Writes the mini fishing boat's geometry for Unity: Assets/Tidewater/Resources/mini-fishing-boat.bytes, from the JS MiniFishingBoat.load()
// (the glb read with the node transforms baked in, the studio extras left out, one merged mesh per glTF material, the hull mask triangles),
// so the Unity boat is the very geometry the browser builds. Also writes the physics adapter's numbers as <outDir>/mini-boat.json (the oracle).
//   node unity/tools/dump-mini-boat.mjs [outDir]
// (the file format is described in lib/factor-boat.mjs)
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { writeFactorBoat } from './lib/factor-boat.mjs';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const outDir = process.argv[ 2 ] || path.join( root, 'unity/Temp/oracle/mini' );
fs.mkdirSync( outDir, { recursive: true } );

globalThis.__assetFile = async ( url ) => {

	const b = fs.readFileSync( path.join( root, 'public', url ) );
	return b.buffer.slice( b.byteOffset, b.byteOffset + b.byteLength );

};

const { MiniFishingBoat } = await import( root + '/src/world/boats/MiniFishingBoat.js' );
const boat = new MiniFishingBoat();
await boat.load();

writeFactorBoat( boat, root, 'mini-fishing-boat' );

// the physics adapter (what BoatController and the Player read), for MiniBoatOracle
const v = ( p ) => [ p.x, p.y, p.z ];
fs.writeFileSync( path.join( outDir, 'mini-boat.json' ), JSON.stringify( {
	hullSamples: boat.hullSamples.map( ( s ) => ( { p: v( s.position ), area: s.area, bottomY: s.bottomY } ) ),
	reserveSamples: boat.reserveSamples.map( ( s ) => ( { p: v( s.position ), area: s.area } ) ),
	hydro: { suggestedMass: boat.hydro.suggestedMass, centerOfMass: v( boat.hydro.centerOfMass ), inertia: v( boat.hydro.inertia ) },
	forceScale: boat.forceScale, propeller: v( boat.propeller ), rudderZ: boat.rudder.z, maxThrust: boat.maxThrust, pitchSpeed: boat.pitchSpeed,
	reverseFactor: boat.reverseFactor, stations: boat.stations, lateralY: boat.lateralY, hullLift: boat.hullLift, rudderLift: boat.rudderLift,
	contactPoints: boat.contactPoints.map( v ), outline: boat.outline.map( v ), bowZ: boat.bowZ, sternZ: boat.sternZ, chockY: boat.chockY,
	helmEye: v( boat.helmEye ), helmPoint: v( boat.helmPoint ), boardPoint: v( boat.boardPoint ), exitPoints: boat.exitPoints.map( v ),
} ) );
