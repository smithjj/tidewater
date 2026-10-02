// Gate for the wheelhouse port: build the boat's static geometry one builder at a time,
// exactly as src/world/BoatModel.js:55-57 does, and print the triangle / vertex count of
// every static bucket (from kit.merged( name )) after each step. The difference between
// the snapshot before and after buildWheelhouse is the wheelhouse's exact contribution --
// that is what blender/tidewater/wheelhouse_check.py has to reproduce.
//
//   node tools/boat/stats.mjs                  # table + JSON
//   node tools/boat/stats.mjs --json out.json  # also write the JSON to out.json
//
// The builders are pure maths/geometry (no scene, no renderer), but the engine modules
// import the WebGPU globals, so test/headless.mjs runs first -- same pattern as
// tools/boat/export.mjs.
import '../../test/headless.mjs';
import { writeFileSync } from 'node:fs';
import { GeoKit, triangleCount } from '../../src/world/boat/GeoKit.js';
import { HullLines } from '../../src/world/boat/HullLines.js';
import { buildHull } from '../../src/world/boat/HullBuilder.js';
import { buildWheelhouse, wheelGeometry, throttleGeometry, radarArrayGeometry } from '../../src/world/boat/Wheelhouse.js';
import { buildDeckGear } from '../../src/world/boat/DeckGear.js';

// BoatModel.js:11
const BUCKETS = [ 'hull', 'gelcoat', 'wood', 'fittings', 'trap', 'glow', 'glass' ];

function snapshot( kit ) {

	const out = {};

	for ( const name of BUCKETS ) {

		const g = kit.merged( name );
		out[ name ] = g ? { tris: triangleCount( g ), verts: g.attributes.position.count } : { tris: 0, verts: 0 };

	}

	return out;

}

// ---- the builders, one at a time (BoatModel.js:50-57)
const lines = new HullLines();
const kit = new GeoKit();
const parts = {};

const stages = [];
const push = ( label, counts ) => stages.push( { label, counts } );

push( 'start', snapshot( kit ) );
buildHull( kit, lines );
push( 'buildHull', snapshot( kit ) );
buildWheelhouse( kit, lines, parts );
push( 'buildWheelhouse', snapshot( kit ) );
buildDeckGear( kit, lines, parts );
push( 'buildDeckGear', snapshot( kit ) );

// ---- the wheelhouse's own contribution
const before = stages.find( ( s ) => s.label === 'buildHull' ).counts;
const after = stages.find( ( s ) => s.label === 'buildWheelhouse' ).counts;
const wheelhouse = {};

for ( const name of BUCKETS ) wheelhouse[ name ] = {
	tris: after[ name ].tris - before[ name ].tris,
	verts: after[ name ].verts - before[ name ].verts,
};

// ---- wheelhouse only, in its own kit: bounding boxes + FNV-1a 32 over the raw float32
// attribute bytes and the uint32 index bytes. Counts alone would not catch a reordered
// vertex stream or a flipped triangle; the hashes do.
function fnv( bytes ) {

	let h = 0x811c9dc5;
	for ( let i = 0; i < bytes.length; i ++ ) h = Math.imul( h ^ bytes[ i ], 16777619 ) >>> 0;
	return h;

}

function hashBucket( g ) {

	const out = {};

	for ( const attr of [ 'position', 'normal', 'uv', 'color', 'aux' ] ) {

		const a = g.attributes[ attr ];
		if ( ! a ) continue;
		out[ attr ] = fnv( new Uint8Array( a.array.buffer, a.array.byteOffset, a.array.byteLength ) );

	}

	if ( g.index ) {

		const n = g.index.count, u32 = new Uint32Array( n );
		for ( let i = 0; i < n; i ++ ) u32[ i ] = g.index.getX( i );
		out.index = fnv( new Uint8Array( u32.buffer ) );

	}

	return out;

}

function boxOf( g ) {

	g.computeBoundingBox();
	const bb = g.boundingBox;
	return { min: [ bb.min.x, bb.min.y, bb.min.z ], max: [ bb.max.x, bb.max.y, bb.max.z ] };

}

const solo = new GeoKit();
buildWheelhouse( solo, lines, {} );
const wheelhouseOnly = {};

for ( const name of BUCKETS ) {

	const g = solo.merged( name );

	if ( ! g ) continue;

	wheelhouseOnly[ name ] = {
		tris: triangleCount( g ),
		verts: g.attributes.position.count,
		box: boxOf( g ),
		hash: hashBucket( g ),
	};

}

// ---- plain table
const col = ( s ) => String( s ).padStart( 11 );

console.log( 'buckets after each builder (tris/verts)' );
console.log( 'stage'.padEnd( 18 ) + BUCKETS.map( ( b ) => col( b ) ).join( '' ) );

for ( const s of stages ) {

	console.log( s.label.padEnd( 18 ) + BUCKETS.map( ( b ) => col( s.counts[ b ].tris + '/' + s.counts[ b ].verts ) ).join( '' ) );

}

// ---- the three animated geometries (BoatModel attaches them to their own meshes)
const animated = {};

for ( const [ name, make ] of [ [ 'wheel', wheelGeometry ], [ 'throttle', throttleGeometry ], [ 'radar', radarArrayGeometry ] ] ) {

	const g = make();
	animated[ name ] = { tris: triangleCount( g ), verts: g.attributes.position.count, box: boxOf( g ), hash: hashBucket( g ) };

}

console.log( '' );
console.log( 'wheelhouse delta  (after buildWheelhouse - after buildHull)' );
console.log( 'bucket'.padEnd( 12 ) + col( 'tris' ) + col( 'verts' ) );

for ( const b of BUCKETS ) console.log( b.padEnd( 12 ) + col( wheelhouse[ b ].tris ) + col( wheelhouse[ b ].verts ) );

const total = BUCKETS.reduce( ( s, b ) => s + wheelhouse[ b ].tris, 0 );
const totalV = BUCKETS.reduce( ( s, b ) => s + wheelhouse[ b ].verts, 0 );
console.log( 'TOTAL'.padEnd( 12 ) + col( total ) + col( totalV ) );
console.log( '' );
console.log( 'wheelhouse only (own kit): counts, bounding box, FNV-1a 32 hashes' );

for ( const b of BUCKETS ) {

	const w = wheelhouseOnly[ b ];
	if ( ! w ) continue;

	const bb = w.box;
	console.log( '  ' + b.padEnd( 11 ) + ( w.tris + '/' + w.verts ).padEnd( 12 )
		+ [ ...bb.min, ...bb.max ].map( ( v ) => v.toFixed( 5 ) ).join( ' ' )
		+ '   ' + Object.entries( w.hash ).map( ( [ k, v ] ) => k + '=' + v ).join( ' ' ) );

}

console.log( '' );
console.log( 'animated geometries: counts, bounding box, FNV-1a 32 hashes' );

for ( const name of [ 'wheel', 'throttle', 'radar' ] ) {

	const a = animated[ name ];
	const bb = a.box;
	console.log( '  ' + name.padEnd( 11 ) + ( a.tris + '/' + a.verts ).padEnd( 12 )
		+ [ ...bb.min, ...bb.max ].map( ( v ) => v.toFixed( 5 ) ).join( ' ' )
		+ '   ' + Object.entries( a.hash ).map( ( [ k, v ] ) => k + '=' + v ).join( ' ' ) );

}

console.log( '' );
console.log( 'parts keys: ' + Object.keys( parts ).sort().join( ', ' ) );

// ---- JSON
const json = { tool: 'js', buckets: BUCKETS, stages, wheelhouse, wheelhouseOnly, animated, parts: Object.keys( parts ).sort() };
const text = JSON.stringify( json );
console.log( '' );
console.log( 'JSON' );
console.log( text );

const at = process.argv.indexOf( '--json' );
if ( at >= 0 && process.argv[ at + 1 ] ) writeFileSync( process.argv[ at + 1 ], text + '\n' );
