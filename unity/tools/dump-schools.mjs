// Oracle for the C# swimming schools (FishSchools.cs): builds the real FishSchools of src/world/Fish.js on TerrainData( seed ) with the world's
// shore field, writes the layout after construction and, for a few scenarios (nobody near; the swimmer inside the bait ball; someone wading;
// the swimmer at the pier piles), the state after N updates and culls (positions, velocities, heading, bank, bend, phase, the groups, and the
// instance records and lists of the batch).    node unity/tools/dump-schools.mjs <outDir> [seed]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const out = process.argv[ 2 ] || '.';
const seed = + ( process.argv[ 3 ] ?? 7 );
fs.mkdirSync( out, { recursive: true } );

const { FishSchools } = await import( root + '/src/world/Fish.js' );
const { TerrainData } = await import( root + '/src/world/TerrainData.js' );
const { computeShoreField } = await import( root + '/src/world/ShoreField.js' );
const { WORLD } = await import( root + '/src/world/WorldLayout.js' );
const { Group, Vector3, PerspectiveCamera, Matrix4, Frustum } = await import( root + '/src/engine/index.js' );

// no GPU in node: the batch's buffer writes do nothing (its CPU lists and records are what is compared)
const { StorageBuffer } = await import( root + '/src/engine/gpu/Texture.js' );
StorageBuffer.prototype.write = function () {};

const terrain = new TerrainData( seed );
const shoreField = computeShoreField( terrain, { res: 512, swellDir: [ WORLD.swellDir.x, WORLD.swellDir.y ] } );
const make = () => new FishSchools( { parent: new Group(), terrain, center: WORLD.reef.center.clone(), radius: WORLD.reef.radius + 10, anchors: [], seed: 23, bay: true, shoreField } );
const arr = ( a ) => Array.from( a );
const v3 = ( v ) => [ v.x, v.y, v.z ];

const groupState = ( g ) => ( {
	name: g.sp.name, count: g.count, offset: g.offset, zone: { x: g.zone.x, z: g.zone.z, r: g.zone.r, band: g.zone.band, sand: !! g.zone.sand, path: g.zone.path ? g.zone.path.length : 0, piles: g.zone.piles ? g.zone.piles.length : 0, anchors: g.zone.anchors ? g.zone.anchors.length : 0 },
	home: v3( g.home ), goal: v3( g.goal ), center: v3( g.center ), heading: v3( g.heading ), timer: g.timer, alarm: g.alarm, spin: g.spin, active: g.active,
	radius: g.radius, ball: g.ball, rest: g.rest, breath: g.breath, jumpTimer: g.jumpTimer, pathIndex: g.pathIndex, pathDir: g.pathDir,
} );

const fishState = ( f ) => ( {
	pos: arr( f.pos ), vel: arr( f.vel ), head: arr( f.head ), panic: arr( f.panic ), jump: arr( f.jump ), roll: arr( f.roll ), bend: arr( f.bend ), phase: arr( f.phase ),
	floorC: arr( f.floorC ), shallowC: arr( f.shallowC ),
} );

const f0 = make();
const layout = {
	count: f0.fishCount, models: [ ...new Set( f0.groups.map( ( g ) => g.sp.model ) ) ], dropPath: f0.dropPath, groups: f0.groups.map( groupState ),
	fish: { ...fishState( f0 ), size: arr( f0.size ), speedMul: arr( f0.speedMul ), seed: arr( f0.seed ), kind: arr( f0.kind ), pattern: arr( f0.pattern ), slot: arr( f0.slot ) },
};

// cameras: a wide view over the reef towards the south east, 1.5 m above the sea; and one for each of the other scenarios
const cameraAt = ( pos, target ) => {

	const c = new PerspectiveCamera( 60, 16 / 9, 0.05, 2000 );
	c.position.set( ...pos );
	c.lookAt( new Vector3( ...target ) );
	c.updateMatrixWorld();
	c.updateProjectionMatrix();
	return c;

};

const frustumOf = ( camera ) => {

	const m = new Matrix4().multiplyMatrices( camera.projectionMatrix, camera.matrixWorldInverse );
	const fr = new Frustum().setFromProjectionMatrix( m, camera.coordinateSystem, camera.reversedDepth );
	return fr.planes.flatMap( ( p ) => [ p.normal.x, p.normal.y, p.normal.z, p.constant ] );

};

const wadeSpot = () => {

	for ( let z = - 20; z > - 70; z -= 0.5 ) if ( terrain.heightAt( 0, z ) > - 1.0 ) return [ 0, 0.9, z + 6 ];
	return [ 0, 0.9, - 40 ];

};

const scenarios = [];
const run = ( name, steps, playerOf, cam ) => {

	const f = make();
	f.viewHeight = 1080;
	let airSteps = 0;
	for ( let s = 0; s < steps; s ++ ) {

		const p = playerOf ? playerOf( f ) : null;
		f.update( 1 / 60, p ? new Vector3( ...p ) : null );
		f.cull( cam );
		if ( f.jump.some( ( j ) => j > 0.5 ) ) airSteps ++;

	}

	const b = f.batch;
	const ids = new Set();
	for ( let i = 0; i < b.visibleInstances; i ++ ) ids.add( b.list[ i ] );
	for ( let i = 0; i < b.fadeInstances; i ++ ) ids.add( b.fadeList[ i ] & 0xffffff );
	const sorted = [ ...ids ].sort( ( x, y ) => x - y );
	scenarios.push( {
		name, steps, player: playerOf ? playerOf( f ) : null, time: f.time, airSteps,
		camera: { pos: v3( cam.position ), planes: frustumOf( cam ), pxScale: cam.projectionMatrix.elements[ 5 ] * f.viewHeight * 0.5 },
		active: f.groups.filter( ( g ) => g.active ).length, moved: Math.max( ...f.groups.map( ( g, i ) => Math.hypot( g.center.x - f0.groups[ i ].home.x, g.center.z - f0.groups[ i ].home.z ) ) ),
		fish: fishState( f ), groups: f.groups.map( groupState ),
		batch: { visible: b.visibleInstances, fade: b.fadeInstances, base: arr( b.baseArray ).slice( 0, f.batch.kinds.length ), fadeBase: arr( b.fadeBaseArray ).slice( 0, f.batch.kinds.length ),
			list: arr( b.list.subarray( 0, b.visibleInstances ) ), fadeList: arr( b.fadeList.subarray( 0, b.fadeInstances ) ), ids: sorted, data: sorted.map( ( i ) => arr( b.data.subarray( i * 16, i * 16 + 16 ) ) ) },
	} );

};

const bait0 = f0.baitGroups[ 0 ].home;
run( 'quiet', 300, null, cameraAt( [ - 70, 1.5, 72 ], [ - 50, - 3, 40 ] ) );
run( 'bait', 600, ( f ) => { const c = f.baitGroups[ 0 ].center; return [ c.x + 4, c.y, c.z ]; }, cameraAt( [ bait0.x + 9, bait0.y + 1, bait0.z + 6 ], [ bait0.x, bait0.y, bait0.z ] ) );
{
	const w = wadeSpot();
	run( 'wading', 300, () => w, cameraAt( [ w[ 0 ], 1.7, w[ 2 ] ], [ w[ 0 ], - 1, w[ 2 ] + 25 ] ) );
}
// a long quiet run: the mullet leap now and then
run( 'long', 3000, null, cameraAt( [ 20, 1.5, 40 ], [ 20, - 1, 5 ] ) );
run( 'piles', 400, () => [ WORLD.pier.x + 1, - 1.5, WORLD.pier.zEnd - 14 ], cameraAt( [ WORLD.pier.x + 3, 1.5, WORLD.pier.zEnd - 16 ], [ WORLD.pier.x, - 2, WORLD.pier.zEnd - 6 ] ) );
fs.writeFileSync( path.join( out, 'schools.json' ), JSON.stringify( { seed, layout, scenarios } ) );
console.log( 'schools:', layout.count, 'fish,', layout.groups.length, 'groups,', scenarios.length, 'scenarios ->', out );
