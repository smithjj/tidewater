// The wildlife visit (src/player/WildlifeVisit.js), run on the real fish simulation and terrain (and the
// real humpback behaviour, without its model), with a stub input and a real camera: G visits the eagle ray,
// the three stingrays, the turtle and the humpback in turn and wraps; the camera follows the animal and looks at it; the mouse turns the view the way it goes; Esc
// and the free camera end it; and while it lasts the fish are calm (the camera, under water, is not a
// diver they flee from).
//   node test/wildlife-visit.mjs
import * as E from '../src/engine/index.js';
import { TerrainData } from '../src/world/TerrainData.js';
import { WORLD } from '../src/world/WorldLayout.js';
import { FishSchools } from '../src/world/Fish.js';
import { WhaleBrain } from '../src/world/marine/WhaleBrain.js';
import { WildlifeVisit } from '../src/player/WildlifeVisit.js';
import { ACTIONS, Bindings } from '../src/core/Bindings.js';

let fails = 0;
const ok = ( c, msg ) => {

	if ( ! c ) { fails ++; console.log( 'FAIL', msg ); } else console.log( 'ok  ', msg );

};

// ---- the binding
const wild = ACTIONS.find( ( a ) => a.id === 'wildlife' );
ok( !! wild && wild.kb.length === 1 && wild.kb[ 0 ] === 'KeyG' && wild.pad.length === 0, 'G is the default for the wildlife action, and the pad has nothing for it' );
ok( wild.group === 'Interface' && wild.menu === 'block' && !! wild.help, 'it is on the controls sheet, and blocked while a panel has the input' );
ok( ACTIONS.filter( ( a ) => ( a.kb || [] ).includes( 'KeyG' ) ).length === 1, 'and no other action has G' );
ok( new Bindings().list( 'wildlife', 'kb' )[ 0 ].v === 'KeyG', 'the Bindings class lists it' );

// ---- the world
const terrain = new TerrainData();
const rc = WORLD.reef.center;
const schools = new FishSchools( { parent: new E.Scene(), terrain, center: rc.clone(), radius: WORLD.reef.radius + 10 } );
const models = schools.groups.map( ( g ) => g.sp.model ).filter( ( m ) => [ 'eagleRay', 'stingray', 'turtle' ].includes( m ) );
console.log( '     the animals in the sim:', models.join( ', ' ) );

// the humpback: the real behaviour with the model's length (the visit reads the position, the velocity, the
// heading and the length), as the app's Whale has them once loaded
const brain = new WhaleBrain( { terrain } );
for ( let i = 0; i < 30; i ++ ) brain.update( 1 / 30 );
const whale = { ready: true, brain, manifest: { length: 14.5 } };

const camera = new E.PerspectiveCamera( 60, 16 / 9, 0.05, 2000 );
const hits = new Set();
let look = { x: 0, y: 0 }, wheel = 0, freeCam = false;
const input = {
	actHit: ( id ) => hits.has( id ),
	consumeLook: () => { const l = look; look = { x: 0, y: 0 }; return l; },
	consumeWheel: () => { const w = wheel; wheel = 0; return w; },
};
const toasts = [];
const v = new WildlifeVisit( { camera, input, schools, whale: () => whale, terrain, waterHeight: () => 0, toast: ( t ) => toasts.push( t ) } );
const press = ( id ) => { hits.add( id ); v.handle( freeCam ); hits.delete( id ); };
const pos = ( g ) => g.whale ? brain.position.clone() : new E.Vector3( schools.pos[ g.offset * 3 ], schools.pos[ g.offset * 3 + 1 ], schools.pos[ g.offset * 3 + 2 ] );
const lengthOf = ( g ) => g.whale ? 14.5 : schools.size[ g.offset ];
const velOf = ( g ) => g.whale ? { x: Math.sin( brain.yaw ) * brain.speed, z: Math.cos( brain.yaw ) * brain.speed } : { x: schools.vel[ g.offset * 3 ], z: schools.vel[ g.offset * 3 + 2 ] };
// the sim as the app runs it: stepped with the camera as the diver, a frame at a time
const frames = ( n, dt = 1 / 30 ) => { for ( let i = 0; i < n; i ++ ) { brain.update( dt ); schools.update( dt, camera.position ); if ( v.active ) v.update( dt ); } };
const facing = () => camera.getWorldDirection( new E.Vector3() );

// ---- G cycles (the whale last)
ok( ! v.active && schools.calm === false, 'no visit to begin with, and the fish are as they always are' );
const seen = [];
for ( let i = 0; i < 7; i ++ ) {

	press( 'wildlife' );
	seen.push( v.target.sp.model );
	const p = pos( v.target );
	ok( v.active && schools.calm === true, `G ${ i + 1 }: the visit is on (${ v.target.sp.model }) and the fish are calm` );
	const d = camera.position.distanceTo( p );
	const L = lengthOf( v.target );
	ok( d > L && d < L * 6 + 6, `   the camera stands ${ d.toFixed( 1 ) } m from a ${ L.toFixed( 1 ) } m animal` );
	const to = p.clone().sub( camera.position ).normalize();
	ok( facing().dot( to ) > 0.999, '   and looks at it' );
	ok( camera.position.y >= terrain.heightAt( camera.position.x, camera.position.z ) + 0.29, '   above the seabed' );
	{

		// behind the animal, not in its way: the camera's offset from it against the way it is heading
		const vel = velOf( v.target );
		const hd = Math.atan2( vel.z, vel.x );
		const ahead = ( camera.position.x - p.x ) * Math.cos( hd ) + ( camera.position.z - p.z ) * Math.sin( hd );
		ok( ahead < - d * 0.5, `   and behind it (${ ( - ahead ).toFixed( 1 ) } m of the ${ d.toFixed( 1 ) })` );

	}

}

ok( seen.join() === [ 'eagleRay', 'stingray', 'stingray', 'stingray', 'turtle', 'humpback', 'eagleRay' ].join(), `the order is the eagle ray, the stingrays, the turtle, the humpback, and round again (${ seen.join( ', ' ) })` );
ok( /^Eagle ray · \d\.\d m · /.test( v.caption ), `the caption names it (“${ v.caption }”)` );
{

	// the whale: its caption, and the camera stays with it as it swims on
	press( 'wildlife' ); press( 'wildlife' ); press( 'wildlife' ); press( 'wildlife' ); press( 'wildlife' ); // the stingrays, the turtle, the humpback
	ok( v.target.whale === true && /^Humpback whale · 14\.5 m · /.test( v.caption ), `the whale's caption (“${ v.caption }”)` );
	ok( brain.position.y < - 1.2 && camera.position.y < 0, `the whale is down at ${ brain.position.y.toFixed( 1 ) } m, so the camera is under water too (${ camera.position.y.toFixed( 1 ) } m) and not looking down on the surface` );
	const w0 = brain.position.clone();
	frames( 150 );
	const moved = w0.distanceTo( brain.position ), d = camera.position.distanceTo( brain.position );
	ok( moved > 5, `the whale swam on (${ moved.toFixed( 1 ) } m in 5 s)` );
	ok( d < 14.5 * 2.3 * 1.6 + 1 && d > 14.5, `and the camera stayed with it (${ d.toFixed( 1 ) } m away)` );
	ok( facing().dot( brain.position.clone().sub( camera.position ).normalize() ) > 0.99, 'looking at it still' );
	press( 'wildlife' ); // round to the eagle ray again, as the fish sections below expect
	ok( v.target.sp.model === 'eagleRay', 'and G goes on round to the eagle ray' );

}

// ---- following: the animal moves, the camera goes with it
const g = v.target;
const p0 = pos( g ), c0 = camera.position.clone();
frames( 150 );
const p1 = pos( g );
const moved = p0.distanceTo( p1 );
ok( moved > 1, `the animal swam on (${ moved.toFixed( 1 ) } m in 5 s)` );
const dFollow = camera.position.distanceTo( p1 ), L = schools.size[ g.offset ];
ok( dFollow < Math.max( 2.4, L * 2.3 ) * 1.6 + 1, `and the camera stayed with it (${ dFollow.toFixed( 1 ) } m away)` );
ok( facing().dot( p1.clone().sub( camera.position ).normalize() ) > 0.99, 'looking at it still' );
ok( camera.position.distanceTo( c0 ) > 0.5, 'having moved to do so' );

// ---- the animals are not frightened while the visit lasts
{

	// the same stretch of sim with the camera as a diver, to see what calm is for
	const alarmed = ( calm ) => {

		const s = new FishSchools( { parent: new E.Scene(), terrain, center: rc.clone(), radius: WORLD.reef.radius + 10 } );
		s.calm = calm;
		const gr = s.groups.find( ( q ) => q.sp.model === 'eagleRay' );
		const k = gr.offset * 3;
		const cam = new E.Vector3( s.pos[ k ] + 3, s.pos[ k + 1 ] + 0.3, s.pos[ k + 2 ] );
		let n = 0;
		for ( let i = 0; i < 1800; i ++ ) {

			s.update( 1 / 30, cam );
			cam.set( s.pos[ k ] - Math.cos( gr.heading.x ) * 3, s.pos[ k + 1 ] + 0.3, s.pos[ k + 2 ] - Math.sin( gr.heading.z ) * 3 );
			if ( gr.alarm > 0 ) n ++;

		}

		return n;

	};

	const a0 = alarmed( false ), a1 = alarmed( true );
	ok( a0 > 0, `without calm, a camera that close raises the alarm (${ a0 } of 1800 frames)` );
	ok( a1 === 0, `with calm it never does (${ a1 } of 1800 frames)` );

}

// ---- the mouse turns the view the way it goes: the camera swings round the animal the other way
{

	press( 'wildlife' ); press( 'wildlife' ); press( 'wildlife' ); press( 'wildlife' ); // the turtle
	frames( 5 );
	// the camera's bearing from the animal, measured from the way the animal heads (so its own turns do not count)
	const rel = () => {

		const t = pos( v.target );
		const a = Math.atan2( camera.position.z - t.z, camera.position.x - t.x ) - v.heading;
		return Math.atan2( Math.sin( a ), Math.cos( a ) );

	};

	const wrap = ( a ) => Math.atan2( Math.sin( a ), Math.cos( a ) );
	const before = rel();
	look = { x: 120, y: 0 };
	frames( 90 );
	const turn = wrap( rel() - before );
	ok( Math.abs( turn - 120 * 0.0035 ) < 0.08, `moving the mouse right orbits the camera by that much (${ turn.toFixed( 2 ) } rad for ${ ( 120 * 0.0035 ).toFixed( 2 ) })` );
	// seen from above (x east, z south) the camera going round to the animal's left is its bearing growing from behind:
	// the view turns right, as the mouse does
	ok( turn > 0, 'the camera swings round to the animal\'s left, so the view turns to the right' );
	const t2 = pos( v.target );
	look = { x: 0, y: 400 };
	frames( 60 );
	ok( camera.position.y > t2.y + 0.3 || camera.position.y > terrain.heightAt( camera.position.x, camera.position.z ) + 0.3, 'mouse down raises the camera over the animal' );
	const d1 = camera.position.distanceTo( pos( v.target ) );
	wheel = - 5;
	frames( 60 );
	const d2 = camera.position.distanceTo( pos( v.target ) );
	ok( d2 < d1 * 0.85, `the wheel zooms (${ d1.toFixed( 1 ) } m to ${ d2.toFixed( 1 ) } m)` );

}

// ---- leaving
press( 'cancel' );
ok( ! v.active && schools.calm === false, 'Esc ends it and the fish are afraid again' );
press( 'wildlife' );
ok( v.active && v.target.whale === true, 'G after a visit goes on from the last animal (the turtle, so the humpback)' );
freeCam = true;
v.handle( freeCam );
ok( ! v.active && schools.calm === false, 'the free camera ends it too' );
press( 'wildlife' );
ok( ! v.active, 'and G does nothing in the free camera' );
freeCam = false;

// ---- nothing to visit
{

	const e = new WildlifeVisit( { camera, input, schools: { groups: [] }, terrain, toast: ( t ) => toasts.push( t ) } );
	hits.add( 'wildlife' ); e.handle( false ); hits.delete( 'wildlife' );
	ok( ! e.active && toasts.includes( 'No wildlife about' ), 'with no animals about it says so' );

}

console.log( fails ? `wildlife-visit: ${ fails } FAILED` : 'wildlife-visit: all passed' );
process.exit( fails ? 1 : 0 );
