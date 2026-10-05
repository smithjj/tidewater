// Dumps the trap line of the JS game (src/game/Traps.js) for Unity:
//   node unity/tools/dump-traps.mjs [outDir]
//  - Assets/Tidewater/Resources/traps/{pot,buoy}.bytes: the modelled pot and its marker as Traps.bake makes them from public/models/props (the real GLBs through the
//    real loader), one welded geometry each. The file:
//      int32 magic 'TRP1', int32 vertexCount, int32 indexCount, then vertexCount x ( px py pz nx ny nz u v r g b rough metal pattern anim ) float32 and indexCount x uint32
//    (the JS frame, x toward port, base on y = 0; the C# loader mirrors x like every boat mesh).
//  - <outDir>/traps.json for the oracle (Editor/TrapsOracle.cs): the mesh counts / bounds / checksums, the rules (trapTriggered, stackVisible, soakHours), 400 haulYield rolls
//    with an injected lcg, and four scripted sessions of the real Traps class (a pot set over the stern, a pot hauled aboard, a haul with every place full, a pot that
//    is set and then the boat turns) with the holder's pose every frame, plus the buoy and rope of a set pot over a synthetic sea.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import * as E from '../../src/engine/index.js';
import { loadStaticModel } from '../../src/world/StaticGLB.js';
import { Traps, bake, trapTriggered, stackVisible, soakHours, haulYield, SOAK_MIN, MAX_KEEP, TRAP_MAX_SPEED, SET_ASTERN } from '../../src/game/Traps.js';
import { habitatAt } from '../../src/game/Bites.js';
import { GameState } from '../../src/game/GameState.js';
import { TRAPS, TRAP } from '../../src/world/boat/DeckGear.js';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const outDir = process.argv[ 2 ] || path.join( root, 'unity/Temp/oracle/traps' );
const resDir = path.join( root, 'unity/Assets/Tidewater/Resources/traps' );
fs.mkdirSync( outDir, { recursive: true } );
fs.mkdirSync( resDir, { recursive: true } );

globalThis.fetch = async ( url ) => {

	const b = fs.readFileSync( new URL( url, import.meta.url ) );
	return { ok: true, status: 200, arrayBuffer: async () => b.buffer.slice( b.byteOffset, b.byteOffset + b.byteLength ) };

};

// the same families and paint Traps.loadModels gives them (they are module constants there, not exported)
const dir = '../../public/models/props/';
const SKIP = ( n ) => /^SETTING \|/.test( n );
const potFile = await loadStaticModel( dir + 'lobster_trap_decimated.glb', { skip: SKIP, name: 'trap' } );
const buoyFile = await loadStaticModel( dir + 'lobster_trap_buoy.glb', { skip: SKIP, name: 'buoy' } );
const pot = bake( potFile, /^(WOOD|TWINE|CORD|IRON) \|/, 0.98, true, { paint: { match: /^WOOD \| Salt-silvered/, body: 0xa89a86 } } );
const buoy = bake( buoyFile, /./, 0.34, false, { flip: true, paint: { match: /^FLOAT \|/, body: 0xb0392a, band: 0xe3b93c, bandFrom: 0.55 } } );

const sum = ( a ) => { let s = 0; for ( let i = 0; i < a.length; i ++ ) s += a[ i ] * ( 1 + ( i % 7 ) * 0.125 ); return s; };
const out = {};
const F = 15;
for ( const [ name, g ] of [ [ 'pot', pot ], [ 'buoy', buoy ] ] ) {

	const A = g.attributes, n = A.position.count, ni = g.index.count;
	const head = Buffer.alloc( 12 );
	head.writeInt32LE( 0x31505254, 0 ); head.writeInt32LE( n, 4 ); head.writeInt32LE( ni, 8 );
	const vf = new Float32Array( n * F );
	for ( let i = 0; i < n; i ++ ) {

		vf.set( [ A.position.getX( i ), A.position.getY( i ), A.position.getZ( i ), A.normal.getX( i ), A.normal.getY( i ), A.normal.getZ( i ), A.uv ? A.uv.getX( i ) : 0, A.uv ? A.uv.getY( i ) : 0,
			A.color.getX( i ), A.color.getY( i ), A.color.getZ( i ), A.aux.getX( i ), A.aux.getY( i ), A.aux.getZ( i ), A.aux.getW( i ) ], i * F );

	}

	const idx = new Uint32Array( ni );
	for ( let i = 0; i < ni; i ++ ) idx[ i ] = g.index.getX( i );
	fs.writeFileSync( path.join( resDir, name + '.bytes' ), Buffer.concat( [ head, Buffer.from( vf.buffer ), Buffer.from( idx.buffer ) ] ) );
	g.computeBoundingBox();
	out[ name ] = { vertices: n, indices: ni, min: g.boundingBox.min.toArray(), max: g.boundingBox.max.toArray(), sumVerts: sum( vf ), sumIdx: sum( idx ), attributes: Object.keys( A ) };
	console.log( name, n, 'vertices', ni / 3, 'triangles', ( 12 + vf.byteLength + idx.byteLength ) / 1e6, 'MB', Object.keys( A ).join() );

}

// ---- the rules
const lcg = ( seed ) => () => ( seed = ( seed * 16807 ) % 2147483647 ) / 2147483647;
out.consts = { SOAK_MIN, MAX_KEEP, TRAP_MAX_SPEED, SET_ASTERN, TRAP: { L: TRAP.L, W: TRAP.W, H: TRAP.H } };
out.trigger = [];
for ( const mode of [ 'walk', 'swim', 'boat', 'deck' ] ) for ( const interact of [ false, true ] ) for ( const work of [ false, true ] ) for ( const live of [ false, true ] ) for ( const modeChanged of [ false, true ] )
	out.trigger.push( { mode, interact, work, live, modeChanged, v: trapTriggered( { mode, interact, work, live, modeChanged } ) } );
out.stack = [];
for ( const aboard of [ 0, 1, 2, 3, 4, 5, 6 ] ) for ( const held of [ - 1, 0, 2, 3 ] ) out.stack.push( { aboard, slots: 4, held, v: stackVisible( aboard, 4, held ) } );
out.soak = [];
{

	const r = lcg( 777 );
	for ( let i = 0; i < 60; i ++ ) {

		const set = { day: Math.floor( r() * 5 ), clock: r() * 24 }, now = { day: Math.floor( r() * 6 ), hour: r() * 24 };
		out.soak.push( { set, now, v: soakHours( set, now ) } );

	}

}

out.yields = [];
{

	const r = lcg( 20261005 );
	for ( let i = 0; i < 400; i ++ ) {

		const soak = i % 9 === 0 ? r() * SOAK_MIN : 1 + r() * 14;
		const depth = [ 0.5, 1.5, 3, 8, 20, 36, 44 ][ Math.floor( r() * 7 ) ] + r();
		const reefDist = r() < 0.5 ? r() * 40 - 10 : 40 + r() * 200, pierDist = r() < 0.2 ? r() * 20 : 20 + r() * 300;
		const hour = r() * 24, seed = 1 + Math.floor( r() * 2e9 );
		const habitat = habitatAt( { depth, reefDist, pierDist } );
		out.yields.push( { soak, depth, reefDist, pierDist, hour, seed, out: haulYield( { soak, depth, habitat, hour, rng: lcg( seed ) } ).map( ( a ) => [ a.species, a.kg ] ) } );

	}

}

// ---- sessions of the real Traps: the holder's pose every frame
const mkBoat = ( { x, z, yaw, speed = 0, sternZ = - 3.9, pitch = 0, roll = 0 } ) => {

	const q = new E.Quaternion().setFromEuler( new E.Euler( pitch, yaw, roll ) );
	return {
		position: new E.Vector3( x, 0.2, z ), quaternion: q, speed, model: { sternZ },
		toWorld( l, o ) { return o.copy( l ).applyQuaternion( q ).add( this.position ); },
		sampleWaterAt: ( p ) => 0.3 * Math.sin( p.x * 0.2 ),
	};

};

console.warn = () => {}; // (Traps tries to fetch the models from a URL: the pot here is the procedural stand-in, which has the same places)
function session( name, { aboard, boat, script } ) {

	const state = new GameState();
	state.upgrades.trapLicence = 1; state.money = 1e6; state.buyTraps( aboard );
	state.day = 2; state.clock = 9;
	const group = new E.Group();
	const tr = new Traps( { scene: new E.Group(), terrain: { heightAt: ( x, z ) => - 6 - 0.01 * x }, query: null, state, boat: { group, lines: { deckY: 0.35 } }, toast: () => {}, splash: () => { tr.splashes ++; } } );
	tr.splashes = 0;
	const frames = [], vis = () => tr.stack.map( ( m ) => m.visible ? 1 : 0 ).join( '' );
	const log = ( ev ) => frames.push( { ev, vis: vis(), holder: tr.holder.visible ? 1 : 0, busy: tr.busy ? 1 : 0, splashes: tr.splashes, p: tr.holder.position.toArray(), q: tr.holder.quaternion.toArray() } );
	script( { tr, state, boat, log, vis } );
	out[ name ] = { aboard, boat: { x: boat.position.x, z: boat.position.z, y: boat.position.y, q: boat.quaternion.toArray(), sternZ: boat.model.sternZ }, frames, stackPos: tr.stack.map( ( m ) => m.position.toArray() ), stackYaw: tr.stack.map( ( m ) => m.rotation.y ) };

}

const runUntilIdle = ( tr, log, dt = 1 / 60 ) => { let n = 0; while ( tr.busy && n ++ < 900 ) { tr.update( dt ); log( 'step' ); } };

session( 'setPot', { aboard: 3, boat: mkBoat( { x: 20, z: 30, yaw: 0.7 } ), script: ( { tr, state, boat, log } ) => {

	log( 'start' );
	state.setTrap( 0, 0, 12 );
	log( 'set' );
	tr.setVisual( boat, 2 );
	log( 'visual' );
	runUntilIdle( tr, log );

} } );

session( 'haulPot', { aboard: 2, boat: mkBoat( { x: 20, z: 30, yaw: 0.7, pitch: 0.03, roll: - 0.05 } ), script: ( { tr, state, boat, log } ) => {

	const pot = state.setTrap( 0, 0, 6 );
	log( 'start' );
	state.haulTrap( pot.id );
	tr.haulVisual( boat );
	log( 'visual' );
	runUntilIdle( tr, log, 1 / 30 );

} } );

session( 'haulFull', { aboard: 6, boat: mkBoat( { x: - 80, z: 12, yaw: - 2.2 } ), script: ( { tr, state, boat, log } ) => {

	const pot = state.setTrap( 0, 0, 6 );
	state.haulTrap( pot.id );
	tr.haulVisual( boat );
	log( 'visual' );
	runUntilIdle( tr, log );

} } );

session( 'setSix', { aboard: 6, boat: mkBoat( { x: 5, z: - 7, yaw: 3.0, sternZ: - 4.2 } ), script: ( { tr, state, boat, log } ) => {

	state.setTrap( 1, 1, 12 );
	tr.setVisual( boat, 99 ); // beyond the places: the last one
	log( 'visual' );
	runUntilIdle( tr, log, 1 / 45 );

} } );

// a set pot's buoy and rope over a synthetic sea (the water query is a stub with a readback of its own)
{

	const state = new GameState();
	state.upgrades.trapLicence = 1; state.money = 1e6; state.buyTraps( 6 );
	const cpu = new Float32Array( 8 * 4 ), pts = [];
	const query = { allocate: () => 0, setPoint: ( i, x, z ) => { pts[ i ] = [ x, z ]; cpu[ i * 4 ] = 0.4 * Math.sin( x * 0.07 + z * 0.05 ); }, cpu };
	const tr = new Traps( { scene: new E.Group(), terrain: { heightAt: ( x, z ) => - 4 - 0.02 * Math.abs( x ) + 0.01 * z }, query, state, boat: null, toast: () => {}, splash: () => {} } );
	const sets = [ [ 10, 5 ], [ - 40, 20 ], [ 120, - 60 ] ].map( ( [ x, z ], i ) => state.setTrap( x, z, 6 + i ) );
	tr.update( 1 / 60 );
	out.buoys = sets.map( ( s ) => {

		const v = tr.views.get( s.id );
		return { id: s.id, x: s.x, z: s.z, yaw: v.yaw, slot: v.slot, pot: v.pot.position.toArray(), potYaw: v.pot.rotation.y, float: v.float.position.toArray(), floatRotZ: v.float.rotation.z, rope: v.rope.position.toArray(), ropeScaleY: v.rope.scale.y };

	} );

}

fs.writeFileSync( path.join( outDir, 'traps.json' ), JSON.stringify( out ) );
console.log( 'frames:', [ 'setPot', 'haulPot', 'haulFull', 'setSix' ].map( ( k ) => `${ k } ${ out[ k ].frames.length }` ).join( ', ' ), '| yields:', out.yields.length, '| buoys:', out.buoys.length );
