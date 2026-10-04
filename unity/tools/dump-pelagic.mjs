// Writes the Pelagic 30's geometry for Unity: Assets/Tidewater/Resources/pelagic-30.bytes, from the JS Pelagic30.load() (the file format is
// described in lib/factor-boat.mjs), so the Unity boat is the very geometry the browser builds. Also writes the physics adapter's numbers and
// the deck facade (lines, colliders) as <outDir>/pelagic.json (the oracle).
//   node unity/tools/dump-pelagic.mjs [outDir]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { writeFactorBoat, v3 } from './lib/factor-boat.mjs';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const outDir = process.argv[ 2 ] || path.join( root, 'unity/Temp/oracle/pelagic' );
fs.mkdirSync( outDir, { recursive: true } );

globalThis.__assetFile = async ( url ) => {

	const b = fs.readFileSync( path.join( root, 'public', url ) );
	return b.buffer.slice( b.byteOffset, b.byteOffset + b.byteLength );

};

const { Pelagic30 } = await import( root + '/src/world/boats/Pelagic30.js' );
const boat = new Pelagic30();
await boat.load();
writeFactorBoat( boat, root, 'pelagic-30', { weldMask: true } );

// the deck facade sampled on a grid, and the colliders
const L = boat.lines, facade = [];
for ( let z = - 5.2; z <= 4.5; z += 0.35 ) for ( const y of [ 0.7, 0.9, 1.4 ] ) {

	const t = L.tAtSheerZ( z );
	facade.push( [ z, y, t, L.halfBreadth( t, y ) ] );

}

fs.writeFileSync( path.join( outDir, 'pelagic.json' ), JSON.stringify( {
	hullSamples: boat.hullSamples.map( ( s ) => ( { p: v3( s.position ), area: s.area, bottomY: s.bottomY } ) ),
	hydro: { suggestedMass: boat.hydro.suggestedMass, centerOfMass: v3( boat.hydro.centerOfMass ), inertia: v3( boat.hydro.inertia ) },
	propeller: v3( boat.propeller ), rudderZ: boat.rudder.z, maxThrust: boat.maxThrust, pitchSpeed: boat.pitchSpeed,
	reverseFactor: boat.reverseFactor, stations: boat.stations, lateralY: boat.lateralY, hullLift: boat.hullLift, rudderLift: boat.rudderLift,
	contactPoints: boat.contactPoints.map( v3 ), outline: boat.outline.map( v3 ), bowZ: boat.bowZ, sternZ: boat.sternZ,
	helmEye: v3( boat.helmEye ), helmPoint: v3( boat.helmPoint ), boardPoint: v3( boat.boardPoint ), exitPoints: boat.exitPoints.map( v3 ),
	lines: { deckY: L.deckY, shell: L.shell, zAft: L.zAft, zFwd: L.zFwd, facade },
	colliders: boat.colliders.map( ( c ) => ( { center: v3( c.center ), half: v3( c.half ), walkable: c.walkable, solid: c.solid } ) ),
} ) );
