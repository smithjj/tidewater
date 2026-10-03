// The modelled pot and its marker, as the game loads them from public/models/props: the real files
// through the real loader and Traps.bake, no GPU. Guards the pot against a re-export that changes it by
// accident (a second scene, a rescale, a renamed material family).
//   node test/trap-model.mjs
import fs from 'node:fs';
import { loadStaticModel } from '../src/world/StaticGLB.js';
import { bake } from '../src/game/Traps.js';

let fails = 0;
const ok = ( c, msg ) => {

	if ( ! c ) { fails ++; console.log( 'FAIL', msg ); } else console.log( 'ok  ', msg );

};

globalThis.fetch = async ( url ) => {

	const b = fs.readFileSync( new URL( url, import.meta.url ) );
	return { ok: true, status: 200, arrayBuffer: async () => b.buffer.slice( b.byteOffset, b.byteOffset + b.byteLength ) };

};
const dir = '../public/models/props/';
const tris = ( m ) => m.root.children.reduce( ( n, c ) => n + c.geometry.index.count / 3, 0 );

const potFile = await loadStaticModel( dir + 'lobster_trap_decimated.glb', { skip: ( n ) => /^SETTING \|/.test( n ), name: 'trap' } );
const pot = bake( potFile, /^(WOOD|TWINE|CORD|IRON) \|/, 0.98, true );
ok( potFile.info.json.scenes.length === 1, 'the trap file is the trap alone (one scene)' );
ok( tris( potFile ) === 85616 && pot.index.count / 3 === 85616, `the pot is 85,616 triangles (${ pot.index.count / 3 })` );
const sz = pot.boundingBox.getSize( new ( pot.boundingBox.min.constructor )() );
ok( Math.abs( sz.z - 0.98 ) < 1e-3 && sz.x < sz.z && sz.x > 0.5, `it is scaled to 0.98 m with its length fore-aft (${ sz.x.toFixed( 3 ) } x ${ sz.y.toFixed( 3 ) } x ${ sz.z.toFixed( 3 ) })` );
ok( Math.abs( pot.boundingBox.min.y ) < 1e-6, 'and stands on y = 0' );

const buoyFile = await loadStaticModel( dir + 'lobster_trap_buoy.glb', { skip: ( n ) => /^SETTING \|/.test( n ), name: 'buoy' } );
const buoy = bake( buoyFile, /./, 0.34, false, { flip: true } );
ok( tris( buoyFile ) === 92180, `the marker float is 92,180 triangles (${ tris( buoyFile ) })` );
ok( buoyFile.root.children.some( ( c ) => /FLOAT \|/.test( c.name ) ), 'and has the FLOAT material the game paints' );
ok( Math.abs( buoy.boundingBox.min.y ) < 1e-6 && buoy.boundingBox.max.y > 0.34, 'baked it stands on y = 0, taller than it is wide' );

console.log( fails ? `${ fails } FAILED` : 'all ok' );
process.exit( fails ? 1 : 0 );
