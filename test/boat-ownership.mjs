// Plain-node test of boarding with owned and unowned boats (Player.nearBoat / lockedBoat). No GPU.
import { Player } from '../src/player/Player.js';

let fails = 0;
const ok = ( c, msg ) => { if ( ! c ) { fails ++; console.log( 'FAIL', msg ); } else console.log( 'ok  ', msg ); };

// a boat is just a board point in world space
const boat = ( name, x, z ) => ( { name, model: { boardPoint: { x, y: 0, z } }, toWorld: ( p, out ) => Object.assign( out, p ) } );
const mini = boat( 'mini', 0, 0 ), lobster = boat( 'lobster', 6, 0 ), pelagic = boat( 'pelagic', 100, 0 );
const p = Object.create( Player.prototype );
p.boats = [ mini, lobster, pelagic ];
p.position = { x: 3, y: 0, z: 0 };
p.owns = null;
ok( p.nearBoat() && p.lockedBoat === null, 'with no ownership rule every boat in reach can be boarded' );

p.owns = ( b ) => b === mini;
p.position.x = 2.5;
ok( p.nearBoat() && p._boardable === mini && p.lockedBoat === lobster, 'the owned boat can be boarded, the other in reach is locked' );
p.position.x = 5.9;
ok( ! p.nearBoat() && p._boardable === null && p.lockedBoat === lobster, 'beside a boat you have not bought you cannot board it' );
p.position.x = 50;
ok( ! p.nearBoat() && p.lockedBoat === null, 'out of reach of every boat: nothing to board and nothing locked' );
p.position.x = 100;
ok( ! p.nearBoat() && p.lockedBoat === pelagic, 'the Pelagic is locked until bought' );
p.owns = () => true;
ok( p.nearBoat() && p._boardable === pelagic && p.lockedBoat === null, 'bought, it boards' );

console.log( fails ? `${ fails } FAILED` : 'boat-ownership: all passed' );
process.exit( fails ? 1 : 0 );
