// Oracle for the C# boat port: builds the original JS BoatModel and records every geometry added to the GeoKit (bucket, vertex /
// index counts and per-attribute checksums, in order), the merged buckets and the model's numbers.
//   node unity/tools/dump-boat.mjs <outDir>
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const { GeoKit } = await import( root + '/src/world/boat/GeoKit.js' );
const out = process.argv[ 2 ] || '.';
fs.mkdirSync( out, { recursive: true } );

const sums = ( g ) => {

	const r = { nv: g.attributes.position.count, ni: g.index ? g.index.count : 0 };
	for ( const name of [ 'position', 'normal', 'uv', 'color', 'aux' ] ) {

		const a = g.attributes[ name ];
		let s = 0, abs = 0;
		for ( let i = 0; i < a.array.length; i ++ ) { s += a.array[ i ]; abs += Math.abs( a.array[ i ] ); }
		r[ name ] = [ s, abs ];

	}

	let ix = 0;
	if ( g.index ) for ( let i = 0; i < g.index.count; i ++ ) ix += g.index.array[ i ] * ( ( i % 7 ) + 1 );
	r.indexHash = ix;
	return r;

};

const adds = [];
const add = GeoKit.prototype.add;
GeoKit.prototype.add = function ( bucket, geometry, opts ) {

	const g = add.call( this, bucket, geometry, opts );
	adds.push( { bucket, ...sums( g ) } );
	return g;

};

const { BoatModel } = await import( root + '/src/world/BoatModel.js' );
const b = new BoatModel();
const merged = {};
for ( const [ k, m ] of Object.entries( b.meshes ) ) merged[ k ] = sums( m.geometry );
const v3 = ( v ) => [ v.x, v.y, v.z ];
const model = {
	dimensions: b.dimensions,
	hydro: { ...b.hydro, centerOfBuoyancy: v3( b.hydro.centerOfBuoyancy ), centerOfMass: v3( b.hydro.centerOfMass ), inertia: v3( b.hydro.inertia ) },
	helmEye: v3( b.helmEye ), helmPoint: [ b.helmPoint.x, b.helmPoint.z ], boardPoint: v3( b.boardPoint ),
	exitPoints: b.exitPoints.map( v3 ), bowSprayPoints: b.bowSprayPoints.map( v3 ),
	hullSamples: b.hullSamples.map( ( q ) => [ ...v3( q.position ), q.area, q.depth, q.bottomY ] ),
	colliders: b.colliders.map( ( c ) => ( { tag: c.tag, center: v3( c.center ), half: v3( c.half ), walkable: c.walkable, solid: c.solid } ) ),
	triangles: b.triangleCount,
	pivots: { wheel: v3( b.wheelPivot.position ), wheelQ: [ b.wheelPivot.quaternion.x, b.wheelPivot.quaternion.y, b.wheelPivot.quaternion.z, b.wheelPivot.quaternion.w ], throttle: v3( b.throttleMesh.position ), radar: v3( b.radarMesh.position ), prop: v3( b.propMesh.position ), rudder: v3( b.rudderMesh.position ) },
};
fs.writeFileSync( path.join( out, 'boat.json' ), JSON.stringify( { adds, merged, model } ) );
console.log( 'adds', adds.length, 'buckets', Object.keys( merged ).join( ',' ) );
