// Oracle for the C# village / pier port: builds the original JS Village in Node (no GPU: the fish-prop
// mesh is stubbed) and writes what it generated to a folder.
//   node unity/tools/dump-village.mjs <outDir> [seed]
// Writes, per merged mesh: <name>.{pos,nrm,uv,tint,vdata}.f32, <name>.idx.u32 (little endian), the
// colliders (colliders.json), the lights, footprints, foundation checks and the pier info (summary.json),
// and the terrain heights after the village flattened its pads (heights.f32).
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const out = process.argv[ 2 ] || '.';
const seed = + ( process.argv[ 3 ] ?? 7 );
fs.mkdirSync( out, { recursive: true } );

const { TerrainData } = await import( root + '/src/world/TerrainData.js' );
const { Colliders } = await import( root + '/src/world/Colliders.js' );
const { FishProps } = await import( root + '/src/world/fish/FishProps.js' );
const { Group } = await import( root + '/src/engine/index.js' );
const { Village } = await import( root + '/src/world/Village.js' );

// the fish displays are an instanced GPU mesh (fish/FishProps.js): not part of this port step
let fishAdds = 0;
FishProps.prototype.build = function () { fishAdds += this.count ?? 0; return new Group(); };

const T = new TerrainData( seed );
const C = new Colliders();
const t0 = performance.now();
const v = new Village( { scene: { add() {} }, terrain: T, colliders: C } );
const ms = Math.round( performance.now() - t0 );

const w = ( name, arr ) => fs.writeFileSync( path.join( out, name ), Buffer.from( arr.buffer, arr.byteOffset, arr.byteLength ) );
const meshes = {}, shared = new Map();
const dumpGeo = ( name, geo ) => {

	const A = geo.attributes;
	// the opaque materials share one vertex / index buffer (index ranges per material): written once, as 'opaque'
	const key = name;
	if ( ! shared.has( A.position ) ) {

		shared.set( A.position, key );
		for ( const k of [ 'position', 'normal', 'uv', 'tint', 'vdata' ] ) w( `${ key }.${ k }.f32`, A[ k ].array instanceof Float32Array ? A[ k ].array : new Float32Array( A[ k ].array ) );
		w( `${ key }.idx.u32`, Uint32Array.from( geo.index.array ) );
		shared.set( A.position, name );

	}

	meshes[ name ] = { buffer: shared.get( A.position ), vertices: A.position.count, indices: geo.index.count, drawStart: geo.drawRange.start, drawCount: geo.drawRange.count };

};

for ( const m of v.meshes ) dumpGeo( m.name, m.geometry );
// swinging parts: sign and lanterns
const sign = v.sign ? { pivot: v.sign.position.toArray(), children: v.sign.children.map( ( c ) => c.name ) } : null;
for ( const c of v.sign ? v.sign.children : [] ) dumpGeo( c.name, c.geometry );
const lanterns = v.lanterns.map( ( l, i ) => { l.obj.children.forEach( ( c, j ) => dumpGeo( `lantern${ i }_${ j }_${ c.name }`, c.geometry ) ); return { pivot: l.pivot.toArray(), rest: l.rest.toArray(), children: l.obj.children.map( ( c ) => c.name ) }; } );

const colliders = {
	boxes: C.boxes.map( ( b ) => ( { c: b.center.toArray(), h: b.half.toArray(), rotY: b.rotY, walkable: b.walkable, solid: b.solid, tag: b.tag } ) ),
	cylinders: C.cylinders.map( ( c ) => ( { x: c.x, z: c.z, r: c.radius, y0: c.yMin, y1: c.yMax, tag: c.tag } ) ),
};
fs.writeFileSync( path.join( out, 'colliders.json' ), JSON.stringify( colliders ) );
w( 'heights.f32', T.heights );
fs.writeFileSync( path.join( out, 'summary.json' ), JSON.stringify( {
	seed, ms, meshes, sign, lanterns,
	lights: v.lights.map( ( l ) => ( { p: l.position.toArray ? l.position.toArray() : l.position, c: [ l.color.r, l.color.g, l.color.b ], i: l.intensity, kind: l.kind } ) ),
	footprints: v.footprints, checks: v.foundationChecks, buildings: v.buildings.map( ( b ) => ( { ...b, footprint: undefined } ) ),
	pads: T.pads ?? null,
	pier: { stepFoot: v.pierInfo.stepFoot.toArray(), ladder: v.pierInfo.ladder.toArray(), lamps: v.pierInfo.lamps.map( ( p ) => p.toArray() ), bollards: v.pierInfo.bollards.map( ( p ) => p.toArray() ), hung: v.pierInfo.hung.length, signPivot: v.pierInfo.signPivot.toArray() },
	path: { length: v.path.length, deck: Array.from( v.path.deck.slice( 0, 50 ) ) },
	shadowTriangles: v.shadowTriangles, stats: v.getStats(),
}, null, 1 ) );
console.log( 'village built in', ms, 'ms ->', out, JSON.stringify( meshes ) );
