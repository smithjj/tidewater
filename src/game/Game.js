import { Vector3, Color } from '../engine/index.js';
import { WORLD } from '../world/WorldLayout.js';
import { FISH } from './FishTable.js';
import { habitatAt, pickSpecies, rollWeight, biteDelay } from './Bites.js';
import { CatchMinigame } from './CatchMinigame.js';
import { GameState } from './GameState.js';
import { FishingRod } from './FishingRod.js';
import { FishStand } from './FishStand.js';
import { Chandlery } from './Chandlery.js';
import { CatchDisplay } from './CatchDisplay.js';
import { UPGRADES, BOATS, fuelBurn, TRAP_PRICE, TRAP_LIMIT } from './Gear.js';
import { AnchorGear } from './AnchorGear.js';
import { Traps, soakHours, haulYield, SOAK_MIN, TRAP_MAX_SPEED, SET_ASTERN, trapTriggered } from './Traps.js';
import { GameHUD } from './GameHUD.js';
import { Minimap } from './Minimap.js';
import { dominantHabitat } from './Codex.js';
import { Guide } from './Guide.js';

import { fishNear, schoolBite, SONAR_RANGE, SONAR_FULL } from './Sonar.js';
// how long the catch card stays up unless dismissed (ms)
const CATCH_CARD_MS = 9000;

// a clock hour as a person would say it: 6 → "6 am", 18.5 → "6:30 pm"
const hourLabel = ( h ) => {

	const hr = Math.floor( h ) % 24, m = Math.round( ( h - Math.floor( h ) ) * 60 );
	const h12 = hr % 12 === 0 ? 12 : hr % 12;
	return m ? `${ h12 }:${ String( m ).padStart( 2, '0' ) } ${ hr < 12 ? 'am' : 'pm' }` : `${ h12 } ${ hr < 12 ? 'am' : 'pm' }`;

};

// The fishing game on top of the world:
//   R          take out / put away the rod (on foot, on the pier, on the boat's deck)
//   hold LMB   wind up, release to cast (hold longer = farther)
//   LMB        strike when a fish takes the bobber ("!"); then hold LMB to reel, let go to ease off
//   RMB        reel an empty line back in
//   I / Tab    cooler / hold contents and the fish log
//   E          at the fish stand: sell your catch
export class Game {

	constructor( app ) {

		this.app = app;
		this.state = new GameState();
		this.state.load();
		// only the boats you have bought can be boarded; the rest wait at their moorings
		app.player.owns = ( b ) => this.state.ownsBoat( this.boatId( b ) );
		this.rod = new FishingRod( { scene: app.scene, camera: app.camera, query: app.query, terrain: app.terrainData, audio: app.audio } );
		this.rod.onLand = ( where ) => this.onBobberLanded( where );
		this.stand = new FishStand( { scene: app.scene, terrain: app.terrainData, colliders: app.colliders } );
		this.display = new CatchDisplay( { scene: app.scene, stall: this.stand.iceFish() } );
		this.landing = null; // { species, kg } while the caught fish swings in view
		this.chandlery = new Chandlery( { scene: app.scene, terrain: app.terrainData, colliders: app.colliders, material: this.stand.material } );
		// the trap line: pots on the seabed with a buoy on each (see game/Traps.js)
		// the working boat is handed over so the modelled pots can stand on its deck (see Traps._buildStack)
		this.traps = new Traps( { scene: app.scene, terrain: app.terrainData, query: app.query, state: this.state, boat: app.boat, toast: ( t, ms ) => this.toast( t, ms ), splash: () => app.audio && app.audio.splash && app.audio.splash( 0.5 ) } );
		// the anchor on the seabed and its line to the bow, for whichever boat has one down
		this.anchors = new AnchorGear( { scene: app.scene, terrain: app.terrainData, query: app.query, boats: [ app.lobsterCtl, app.pelagicCtl ] } );
		this.vendors = [ this.stand.vendor, this.chandlery.vendor ];
		// boat upgrades: engine (thrust / top speed) and deck floodlights for night fishing.
		// The rebuilt engine is the lobster boat's: always target it, not whichever boat is active.
		const b = app.lobsterCtl ?? app.boatCtl;
		this._engineBase = { maxThrust: b.maxThrust, pitchSpeed: b.pitchSpeed };
		this.floods = [];
		if ( app.localLights ) this.addFloodlights( app.localLights, app.boat );
		this._fuelOut = false;
		this._sonarT = 0;
		this._sonar = null;
		this.hud = null;
		this._haulCard = 0; // seconds the haul's catch card stays before it dismisses itself
		this.fight = null; // CatchMinigame while a fish is on
		this.bite = null; // { phase: 'wait' | 'nibble' | 'take', t, nibbles, species, kg }
		this._hookedSpecies = null;
		this._pier = WORLD.pier;
		this._tmp = new Vector3();
		this.applyGear();
		this.state.onChange( () => this.applyGear() );

	}

	applyGear() {

		const g = this.state.stats;
		this.rod.setGear( { castM: g.castM, reelSpeed: g.reelSpeed } );
		const b = this.app.lobsterCtl ?? this.app.boatCtl;
		if ( b && this._engineBase ) {

			b.maxThrust = this._engineBase.maxThrust * g.speedMul * g.speedMul;
			b.pitchSpeed = this._engineBase.pitchSpeed * g.speedMul;

		}

		for ( const f of this.floods || [] ) f.scale = g.deckLights ? 1 : 0;

	}

	// two floodlights under the back of the wheelhouse roof, lighting the cockpit and the water
	// astern (night only, like every local light; switched by the lights upgrade)
	addFloodlights( lights, boat ) {

		const obj = boat.group;
		for ( const x of [ - 0.8, 0.8 ] ) {

			const local = new Vector3( x, 2.25, - 1.0 );
			const localDir = new Vector3( x * 0.25, - 0.75, - 0.62 ).normalize();
			const src = {
				position: new Vector3(), color: new Color( 1.0, 0.93, 0.8 ), intensity: 3.2, range: 14, kind: 'boatFlood',
				dir: new Vector3(), cosInner: 0.8, cosOuter: 0.45, scale: 0,
				update() {

					obj.updateWorldMatrix( true, false );
					this.position.copy( local ).applyMatrix4( obj.matrixWorld );
					this.dir.copy( localDir ).transformDirection( obj.matrixWorld );

				},
			};
			src.update();
			this.floods.push( src );
			lights.add( src );

		}

	}

	// which of the three boats a controller is (the ids of Gear.BOATS)
	boatId( ctl ) {

		const app = this.app;
		return ctl === app.lobsterCtl ? 'lobster' : ctl === app.pelagicCtl ? 'pelagic' : ctl === app.miniCtl ? 'mini' : null;

	}

	buyBoat( id ) {

		const r = this.state.buyBoat( id );
		if ( r ) this.toast( `${ r.name } is yours · she's at her mooring by the pier` );
		return r;

	}

	buy( key ) {

		const r = this.state.buy( key );
		if ( r ) this.toast( `${ UPGRADES[ key ].name }: ${ r.label }` );
		// the modelled pot is a 23 MB fetch: only start it once there is a trap line to show

		return r;

	}

	buyTraps( n = 1 ) {

		const r = this.state.buyTraps( n );
		if ( r ) {

			this.toast( n === 1 ? `Trap aboard · $${ TRAP_PRICE }` : `${ n } traps aboard · $${ TRAP_PRICE * n }` );
	

		}

		return r;

	}

	refuel() {

		const l = this.state.refuel();
		if ( l > 0 ) {

			this._fuelOut = false;
			this.toast( `Filled up · ${ l.toFixed( 0 ) } L` );

		}

		return l;

	}

	toast( text, ms = 2600 ) {

		if ( this.hud ) this.hud.toast( text, ms );
		else console.log( '[game]', text );

	}

	// A controller pulse: magnitudes 0..1, milliseconds. Silent without a pad, and `cooldown` keeps a
	// sustained event (a screaming reel) from becoming a continuous buzz.
	rumble( strong, weak, ms, cooldown = 90 ) {

		const inp = this.app && this.app.input;
		if ( inp && inp.rumble ) inp.rumble( { strong, weak, ms, cooldown } );

	}

	// midnight: the day ticks over, and the world clock / weather go into the save
	newDay() {

		const s = this.state;
		s.advanceDay();
		s.setClock( this.hour );
		if ( this.app.weather ) s.setWeather( this.app.weather.state() );
		s.save();
		s.emit();
		this.toast( `Day ${ s.day }`, 3400 );

	}

	get canFish() {

		const app = this.app, p = app.player;
		return ! app.freeCam && ( p.mode === 'walk' || p.mode === 'deck' ) && ! ( app.ui && app.ui.ui && app.ui.ui._photo );

	}

	// ---- per frame (after the player / camera update)
	update( dt ) {

		const app = this.app, p = app.player, inp = app.input, rod = this.rod;
		this._cardDismissed = false;
		// the player has already been updated: a mode that differs from last frame's changed in this frame's
		// input (E brought the player aboard, or off the helm), and that press is not also a pot
		this._modeChanged = this._lastMode !== undefined && this._lastMode !== p.mode;
		this._lastMode = p.mode;
		if ( ! this.hud && app.ui && app.ui.ui && typeof document !== 'undefined' && document.head ) {

			const ui = app.ui.ui;
			this.hud = new GameHUD( ui, this );
			// the minimap (lower right) and the first-play guide (intro, one-time tips; replay from F1)
			this.minimap = new Minimap( ui.hud || ui.root, this );
			this.guide = new Guide( ui, this, this.minimap );
			ui.onReplayGuide = () => this.guide.replay();

		}

		const can = this.canFish;
		if ( inp.actHit( 'rod' ) && can && ! this.fight ) {

			rod.equip( ! rod.equipped );
			if ( ! rod.equipped ) this.cancelLine();
			this.toast( rod.equipped ? `Rod out · hold ${ inp.label( 'rodUse' ) } to cast` : 'Rod away', 1600 );

		}

		if ( ! can && rod.equipped ) {

			// swimming, driving, free camera: the line comes in and the rod goes away
			this.cancelLine( true );
			rod.equip( false );

		}

		if ( this.hud && inp.actHit( 'cooler' ) ) this.hud.toggleInventory();
		if ( this.hud && inp.actHit( 'codex' ) ) this.hud.fishGuide.toggle();
		if ( this.minimap && inp.actHit( 'map' ) ) this.minimap.toggleBig( inp.label( 'map' ) );
		if ( this.minimap && inp.actHit( 'cancel' ) ) this.minimap.toggleBig( '', false );
		if ( this.hud && inp.actHit( 'cancel' ) ) {

			this.hud.toggleInventory( false );
			this.hud.closeStand();
			this.hud.fishGuide.toggle( false );

		}

		// the cast / reel button and its edges (the pad trigger and the left mouse both land here)
		const lmb = inp.act( 'rodUse' ), rmb = inp.act( 'rodIn' );
		const lDown = inp.actHit( 'rodUse' ), lUp = inp.actReleased( 'rodUse' ), rDown = inp.actHit( 'rodIn' );
		const panelOpen = this.hud && ( this.hud.invOpen || this.hud.standOpen || this.hud.guideOpen );

		if ( rod.equipped && ! panelOpen ) {

			if ( rod.state === 'idle' && lDown ) rod.startWindup();
			else if ( rod.state === 'windup' && lUp ) rod.release();
			else if ( rod.state === 'floating' ) {

				if ( lDown ) this.strike();
				else if ( rDown ) {

					rod.retrieve();
					this.bite = null;

				}

			} else if ( rod.state === 'flying' && rDown ) rod.retrieve();

		}

		// bites and the fight
		if ( rod.state === 'floating' ) this.updateBite( dt );
		else if ( ! this.fight ) rod.dip = Math.max( 0, rod.dip - dt * 4 );
		if ( this.fight ) this.updateFight( dt, lmb && ! panelOpen );

		rod.update( dt, { visible: can, fight: this.fight } );
		// the landed fish hangs on the end of the line, turned to face you, then goes in the cooler. With
		// the HUD the catch card comes up once the fish has swung in, and the fish stays (slowly turning)
		// until the card is dismissed (click, E, Esc) or times out.
		const L = this.landing;
		if ( L && rod.state === 'landing' && can ) {

			const c = app.camera.position, m = rod.bobber;
			let yaw = Math.atan2( c.x - m.x, c.z - m.z );
			if ( L.card ) {

				if ( ! L.shown && rod.t > 0.3 ) {

					L.shown = true;
					this.hud.showCatch( L.card, CATCH_CARD_MS );

				}

				if ( L.shown ) {

					// a slow turn so both flanks show
					L.cardT += dt;
					yaw += Math.sin( L.cardT * 0.7 ) * 0.55;
					if ( lDown || inp.actHit( 'interact' ) || inp.actHit( 'cancel' ) || L.cardT > CATCH_CARD_MS / 1000 ) {

						this._cardDismissed = true; // this frame's E / click belong to the card
						this.endLanding();

					}

				}

			}

			if ( this.landing ) {

				// with the HUD the fish is shown on the full-screen catch card (studio portrait), not on
				// the line; without it (headless) it hangs on the line for a moment
				if ( ! L.card ) this.display.show( L.species, L.kg, m, yaw, dt );
				else if ( this.display.shown ) this.display.hide();
				if ( ! L.card && rod.t > 2.8 ) this.endLanding();

			}

		} else if ( this.landing || this.display.shown ) this.endLanding();

		p.busy = rod.lineInWater || rod.state === 'windup';

		this.updateBoat( dt );

		// the world clock and the weather ride along in the save (every 20 s, and at midnight)
		this._clockT = ( this._clockT ?? 20 ) - dt;
		if ( this._clockT <= 0 ) {

			this._clockT = 20;
			const s = this.state;
			s.setClock( this.hour );
			if ( app.weather ) s.setWeather( app.weather.state() );
			s.save();

		}

		// a catch card from a haul is not part of the landing flow, so it dismisses itself: it times
		// out on its own (matching the card's own timer), or E / click / Esc takes it away early
		if ( this._haulCard > 0 ) {

			this._haulCard -= dt;
			if ( this._haulCard <= 0 || inp.actHit( 'interact' ) || inp.actHit( 'cancel' ) || lDown ) {

				if ( this.hud ) this.hud.hideCatch();
				this._haulCard = 0;
				this._cardDismissed = true; // this frame's E / click belonged to the card

			}

		}

		// the traders
		for ( const v of this.vendors ) v.update( dt, p.mode === 'walk' ? p.position : null );
		this.updateVendors( inp, p );
		// the trap line (buoys ride the water; setting and hauling are E on the working boat)
		this.traps.update( dt );
		this.updateTraps( inp, p );
		// the anchor (X, aboard either boat) and what it looks like
		if ( inp.actHit( 'anchor' ) ) this.toggleAnchor();
		this.anchorHint( dt, p );
		if ( this.anchors ) this.anchors.update( p.boats, dt );

		// prompts when the player has nothing to say
		if ( ! p.prompt ) p.prompt = this.trapPrompt( p ) || ( can ? this.prompt() : null );
		if ( p.mode === 'boat' ) {

			// at the helm the player's own prompt is "leave helm": the pots ride on the cast button there
			const tp = this.trapPrompt( p );
			if ( tp ) p.prompt = { action: 'rodUse', text: `${ tp.text }   ·   ${ inp.label( 'interact' ) }  leave helm` };

		}

		// E closes the catch card; at the helm it must not also leave it (read by Player.updateBoat next frame)
		p.blockLeaveHelm = this._haulCard > 0 || !! ( this.hud && this.hud.catchOpen );

		const aboard = p.mode === 'boat' || p.mode === 'deck';
		// the catch card's live fish portrait (or one queued thumbnail)
		if ( this.hud && this.hud.portrait ) this.hud.portrait.update( dt );
		if ( this.hud ) this.hud.update( {
			fuel: aboard ? { litres: this.state.fuelL, tank: this.state.stats.fuelL } : null,
			sonar: aboard && this.state.stats.finder ? this._sonar : null,
			// the world clock: which day, the hour, and what the sea is doing
			clock: { day: this.state.day, hour: this.hour, sea: app.weather ? app.weather.name : null },
			// the trap line, once there is one
			traps: this.state.mayTrap || this.state.sets.length ? { aboard: this.state.traps, set: this.state.sets.length } : null,
			fight: this.fight,
			casting: rod.state === 'windup',
			power: rod.power,
			bite: this.bite && this.bite.phase === 'take',
			aiming: rod.equipped,
		} );
		if ( this.minimap ) this.minimap.update( dt );
		if ( this.guide ) this.guide.update( dt );

	}

	prompt() {

		const rod = this.rod, p = this.app.player;
		// a boat in reach that is not yours yet: say where to buy it
		if ( p.lockedBoat && ! rod.equipped ) return { key: '$', text: `${ BOATS[ this.boatId( p.lockedBoat ) ].name } · for sale at Marta's chandlery` };
		if ( ! rod.equipped ) {

			// by the water (boat deck, pier, the wet beach, wading): suggest the rod
			const byWater = p.mode === 'deck' || ( p.mode === 'walk' && [ 'wood', 'wetsand', 'water' ].includes( p.surface ) );
			return byWater ? { action: 'rod', text: 'Take out the rod' } : null;

		}

		const b = this.bite;
		const use = this.app.input.label( 'rodUse' ), inHit = this.app.input.label( 'rodIn' );
		switch ( rod.state ) {

			case 'idle': return { action: 'rodUse', text: `Hold to wind up, release to cast   ·   ${ this.app.input.label( 'rod' ) }  put the rod away` };
			case 'windup': return { action: 'rodUse', text: 'Release to cast (hold longer to cast farther)' };
			case 'flying': return null;
			case 'floating':
				if ( b && b.phase === 'take' ) return { action: 'rodUse', text: 'Strike now!' };
				if ( b && b.phase === 'nibble' ) return { key: '…', text: 'Something\'s nibbling · wait until the bobber is pulled under' };
				return { action: 'rodIn', text: `Waiting for a bite · ${ inHit } to reel the line in` };
			case 'retrieving': return { action: 'rodIn', text: 'Reeling in' };
			case 'fighting': return this.fight && this.fight.tension > this.fight.band[ 1 ]
				? { action: 'rodUse', text: 'Too much tension · let go!' }
				: { action: 'rodUse', text: `Hold to reel (${ use }) · let go when the tension goes red` };
			case 'landing': return null;
			default: return null;

		}

	}

	// fuel burn at the helm (the engine stops when the tank is dry) and the fish finder
	updateBoat( dt ) {

		const app = this.app, b = app.boatCtl, p = app.player, s = this.state;
		if ( b.driven ) {

			const left = s.burn( fuelBurn( b.rpm ) * dt );
			if ( left <= 0 ) {

				b.throttle = 0;
				if ( ! this._fuelOut ) this.toast( 'Out of fuel · buy diesel at the chandlery by the boathouse', 4000 );
				this._fuelOut = true;

			}

		} else if ( this._wasDriven ) s.save();
		this._wasDriven = b.driven;

		if ( ( p.mode === 'boat' || p.mode === 'deck' ) && s.stats.finder ) {

			this._sonarT -= dt;
			if ( this._sonarT <= 0 ) {

				this._sonarT = 0.5;
				const x = b.position.x, z = b.position.z;
				const depth = Math.max( 0, - app.terrainData.heightAt( x, z ) );
				// the schools the reef is really simulating, under and around the boat: the same range the
				// simulation itself uses, so the reading is fish that are alive rather than habitat
				const reef = app.reef;
				const schools = fishNear( reef && reef.fish && reef.fish.groups, x, z, SONAR_RANGE );
				let n = 0;
				for ( const q of schools ) n += q.count;
				this._sonar = { depth, fish: Math.min( 1, n / SONAR_FULL ), schools };

			}

		}

	}

	updateVendors( inp, p ) {

		const hud = this.hud;
		const hour = this.hour;
		let near = null, shut = null;
		if ( p.mode === 'walk' ) for ( const v of this.vendors ) {

			if ( ! v.inRange( p.position ) ) { v._shutTold = false; continue; }
			if ( v.openAt( hour ) ) near = v;
			else if ( ! v._shutTold ) { v._shutTold = true; shut = v; }

		}

		// the stalls keep island hours: closed at night, say so once as you walk up
		if ( shut ) this.toast( `${ shut.name.split( ' ·' )[ 0 ] } is shut — back at ${ hourLabel( shut.hours[ 0 ] ) }`, 3200 );
		for ( const v of this.vendors ) v.talking = !! ( hud && hud.standOpen && hud.vendor === v );
		if ( hud && hud.standOpen && ( ! near || near !== hud.vendor ) ) hud.closeStand();
		if ( ! near || this.fight || this._cardDismissed || ( hud && hud.catchOpen ) ) return;
		if ( ! p.prompt ) p.prompt = { action: 'interact', text: hud && hud.standOpen ? 'Leave' : `Talk to ${ near.name.split( ' ·' )[ 0 ] }` };
		if ( inp.actHit( 'interact' ) ) {

			if ( ! hud ) {

				if ( near.kind === 'buyer' ) this.sellAll(); // headless: straight sale

			} else if ( hud.standOpen ) hud.closeStand();
			else hud.openStand( near );

		}

	}

	sellAll() {

		const r = this.state.sell();
		if ( r.count ) this.toast( `Sold ${ r.count } fish for $${ r.total }${ r.bonus > 0 ? ` · Joe's order +$${ r.bonus }` : '' }` );
		if ( this.app.audio && this.app.audio.coin ) this.app.audio.coin();
		return r;

	}

	// ---- the anchor
	// Down or up for the boat the player is aboard (at the helm or on deck). It lands under the bow chock.
	toggleAnchor() {

		const p = this.app.player;
		if ( p.mode !== 'boat' && p.mode !== 'deck' ) return null;
		const b = p.boat;
		if ( b.anchor.down ) {

			b.weighAnchor();
			this.toast( 'Anchor up', 2200 );
			return { weighed: true };

		}

		const c = b.toWorld( b.chock, this._tmp );
		const depth = Math.max( 0, - this.app.terrainData.heightAt( c.x, c.z ) );
		const r = b.dropAnchor( depth );
		if ( ! r.ok ) {

			this.toast( r.reason, 2400 );
			return null;

		}

		if ( this.app.audio && this.app.audio.splash ) this.app.audio.splash( 0.6 );
		this.toast( `Anchor down · ${ Math.round( r.rode ) } m of line`, 2800 );
		return r;

	}

	// driving against the anchor: say why the boat will not go (once in a while, not every frame)
	anchorHint( dt, p ) {

		this._anchorHintT = Math.max( 0, ( this._anchorHintT || 0 ) - dt );
		if ( p.mode !== 'boat' || ! p.boat.anchor.down || this._anchorHintT > 0 ) return;
		if ( p.boat.throttle > 0.4 && p.boat.anchor.tension > 2000 ) {

			this._anchorHintT = 12;
			this.toast( `The anchor is holding · ${ this.app.input.label( 'anchor' ) } to weigh it`, 2600 );

		}

	}

	// ---- the trap line
	// Setting and hauling happen from the working boat (it is the one with the hauler): stand
	// anywhere aboard, E. Traps soak on the world clock, so the day running is what fills them.

	get aboardWorkingBoat() {

		const p = this.app.player;
		return p.boat === this.app.lobsterCtl && ( p.mode === 'boat' || p.mode === 'deck' );

	}

	// the prompt (and the action it implies) for the trap line, or null
	trapPrompt( p ) {

		if ( ! this.aboardWorkingBoat ) return null;
		if ( this._haulCard > 0 || ( this.hud && this.hud.catchOpen ) || this.traps.busy ) return null;
		const s = this.state, b = this.app.lobsterCtl;
		const near = s.nearestSet( b.position.x, b.position.z, 12 );
		const canSet = s.mayTrap && s.traps > 0 && s.sets.length < TRAP_LIMIT;
		const crawl = b.speed > TRAP_MAX_SPEED ? ' · slow down' : '';
		if ( near ) {

			const soak = soakHours( near, { hour: this.hour, day: s.day } );
			// A pot that has not soaked yet is not worth hauling, and E should keep laying the line
			// instead (a gear of pots goes in a line, so the last pot is always in reach). It only
			// comes back up if there is nothing left to set — no pots aboard, or the water full.
			if ( soak >= SOAK_MIN || ! canSet ) {

				const when = soak < SOAK_MIN ? 'just set' : `soaked ${ soak < 10 ? soak.toFixed( 1 ) : Math.round( soak ) } h`;
				return { action: 'interact', text: `Haul trap · ${ when }${ crawl }`, act: 'haul' };

			}

		}

		if ( ! canSet ) return null;
		return { action: 'interact', text: `Set a trap · ${ s.traps } aboard${ crawl }`, act: 'set' };

	}

	updateTraps( inp, p ) {

		if ( this._cardDismissed || this._haulCard > 0 || ( this.hud && this.hud.catchOpen ) ) return;
		// the mouse is captured (or a pad is in hand): the click that grabs the pointer must not also set a pot
		const ui = this.app.ui && this.app.ui.ui;
		const live = ( inp.locked || inp.device === 'pad' ) && ! inp.menuMode && ! ( ui && ui._photo );
		if ( ! trapTriggered( { mode: p.mode, interact: inp.actHit( 'interact' ), work: inp.actHit( 'rodUse' ), live, modeChanged: this._modeChanged } ) ) return;
		const pr = this.trapPrompt( p );
		if ( ! pr ) return;
		if ( pr.act === 'haul' ) this.haulTrap();
		else this.setTrap();

	}

	// put a pot over the side at (x, z) — the boat's position when they are not given. The caller
	// decides whether the player is in a position to do this (see updateTraps), so the console can
	// drive it too.
	setTrap( x = null, z = null ) {

		const app = this.app, s = this.state, b = app.lobsterCtl;
		if ( b.speed > TRAP_MAX_SPEED ) {

			this.toast( 'Slow down to set a pot', 2200 );
			return null;

		}

		// a pot goes over the stern, not under the keel: just astern of the transom
		const fromBoat = x === null; // (a console caller names the place, and the boat does not carry it there)
		if ( fromBoat ) {

			const sternZ = b.model && b.model.sternZ !== undefined ? b.model.sternZ : - 3.8;
			b.toWorld( this._tmp.set( 0, 0, sternZ - SET_ASTERN ), this._tmp );
			x = this._tmp.x; z = this._tmp.z;

		}

		const depth = Math.max( 0, - app.terrainData.heightAt( x, z ) );
		if ( depth < 2 ) {

			this.toast( 'Too shallow here — the pot would show at low water', 3000 );
			return null;

		}

		if ( depth > 45 ) {

			this.toast( 'Too deep — the warp would not reach the bottom', 3000 );
			return null;

		}

		// a gear of pots goes in a line, not in a heap
		for ( const o of s.sets ) {

			if ( Math.hypot( o.x - x, o.z - z ) < 8 ) {

				this.toast( 'There is already a pot here — move along a bit', 2600 );
				return null;

			}

		}

		const before = s.traps; // pots aboard: the one that goes over is the top of the stack
		const set = s.setTrap( x, z, this.hour );
		if ( ! set ) {

			this.toast( 'No pots aboard — Marta sells traps', 3000 );
			return null;

		}

		this.toast( `Pot set in ${ depth.toFixed( 0 ) } m · give it a few hours`, 3200 );
		// the pot lifts off the stack and drops over the stern; the splash is heard when it lands
		if ( ! ( fromBoat && this.traps.setVisual( b, Math.min( before, this.traps.stack.length ) - 1 ) ) && app.audio && app.audio.splash ) app.audio.splash( 0.5 );
		return set;

	}

	// haul a pot: the given one, or the nearest to the boat (or to you, when you are not aboard).
	// Returns what came up, or null when there is nothing in reach.
	haulTrap( set = null ) {

		const app = this.app, s = this.state, b = app.lobsterCtl;
		if ( ! set && this.aboardWorkingBoat ) {

			// one pot at a time, and only at a crawl (a caller that names the set, like the console, is not held to it)
			if ( this.traps.busy ) return null;
			if ( b.speed > TRAP_MAX_SPEED ) {

				this.toast( 'Slow down to haul a pot', 2200 );
				return null;

			}

		}

		if ( ! set ) {

			const ref = this.aboardWorkingBoat ? b.position : app.player.position;
			set = s.nearestSet( ref.x, ref.z, 12 );

		}

		if ( ! set ) {

			this.toast( 'No pot in reach', 2200 );
			return null;

		}

		const depth = Math.max( 0, - app.terrainData.heightAt( set.x, set.z ) );
		const soak = soakHours( set, { hour: this.hour, day: s.day } );
		const habitat = this.habitatAtPoint( set.x, set.z, depth );
		const animals = haulYield( { soak, depth, habitat, hour: this.hour } );
		const where = { x: set.x, z: set.z, hab: dominantHabitat( habitat ) }; // for the fish guide's map
		if ( ! s.haulTrap( set.id ) ) return null;
		this.traps.haulVisual( b );
		this.rumble( 0.85, 0.4, 220 );
		if ( app.audio && app.audio.fishFlop ) app.audio.fishFlop();

		if ( ! animals.length ) {

			this.toast( soak < SOAK_MIN
				? 'Nothing yet — give it a few hours'
				: 'The pot came up empty · try deeper ground or the reef edge', 3600 );
			return [];

		}

		// smallest first, so the catch card ends up showing the best of the haul
		animals.sort( ( a, c ) => a.kg - c.kg );
		const tally = new Map();
		let kept = 0;
		for ( const a of animals ) {

			const f = s.addFish( a.species, a.kg, this.hour, where );
			if ( f ) kept ++;
			const t = tally.get( a.species ) || { n: 0, kg: 0 };
			t.n ++; t.kg += a.kg;
			tally.set( a.species, t );

		}

		const list = [ ... tally ].map( ( [ id, t ] ) => `${ t.n } × ${ FISH[ id ].name } (${ t.kg.toFixed( 1 ) } kg)` ).join( ', ' );
		this.toast( kept < animals.length
			? `Pot hauled: ${ list } · ${ animals.length - kept } did not fit` : `Pot hauled: ${ list }`, 5000 );
		if ( this.hud ) {

			this.hud.showCatch( s.lastCatch, 7000 );
			this._haulCard = 7;

		}

		if ( app.audio && app.audio.coin ) app.audio.coin();
		return animals;

	}

	sell( ids ) {

		const r = this.state.sell( ids );
		if ( r.count ) this.toast( `Sold for $${ r.total }${ r.bonus > 0 ? ` · Joe's order +$${ r.bonus }` : '' }` );
		return r;

	}

	// ---- bites
	habitat() {

		return this.habitatAtPoint( this.rod.bobber.x, this.rod.bobber.z, this.rod.depth );

	}

	// the shoal the cast is sitting on, if any: the schools the reef is really simulating, so
	// fishing the school you can see (or the finder found) bites sooner and favours its species
	schoolNear() {

		const reef = this.app.reef;
		return schoolBite( reef && reef.fish && reef.fish.groups, this.rod.bobber.x, this.rod.bobber.z );

	}

	habitatAtPoint( x, z, depth ) {

		const b = { x, z };
		const reef = WORLD.reef;
		const reefDist = Math.hypot( b.x - reef.center.x, b.z - reef.center.z ) - reef.radius;
		const P = this._pier;
		const rect = ( x0, x1, z0, z1 ) => Math.hypot( Math.max( x0 - b.x, 0, b.x - x1 ), Math.max( z0 - b.z, 0, b.z - z1 ) );
		const walk = rect( P.x - P.width / 2, P.x + P.width / 2, P.zStart, P.zEnd );
		const head = rect( P.x - P.headWidth / 2, P.x + P.headWidth / 2, P.zEnd - P.headDepth, P.zEnd );
		return habitatAt( { depth, reefDist, pierDist: Math.min( walk, head ) } );

	}

	get hour() {

		return this.app.settings.timeOfDay;

	}

	onBobberLanded( where ) {

		if ( where !== 'water' ) {

			this.toast( 'Landed on the sand', 1400 );
			return;

		}

		const s = this.schoolNear();
		this.bite = { phase: 'wait', t: biteDelay( this.habitat(), this.hour, Math.random, s ? s.influence : 0 ) };

	}

	updateBite( dt ) {

		const rod = this.rod;
		const b = this.bite;
		if ( ! b ) return;
		b.t -= dt;
		// bobber motion for the cues
		if ( b.phase === 'nibble' ) rod.dip = Math.max( 0, Math.sin( Math.min( 1, ( b.pulse || 0 ) ) * Math.PI ) * 0.45 );
		else if ( b.phase === 'take' ) rod.dip += ( 1.4 - rod.dip ) * ( 1 - Math.exp( - dt * 14 ) );
		else rod.dip = Math.max( 0, rod.dip - dt * 3 );
		if ( b.phase === 'nibble' ) b.pulse = ( b.pulse || 0 ) + dt * 3.2;

		if ( b.t > 0 ) return;
		if ( b.phase === 'wait' ) {

			const h = this.habitat();
			const s = this.schoolNear();
			const species = pickSpecies( h, this.hour, Math.random, s ? { id: s.model, k: s.bias } : null );
			if ( ! species ) {

				b.t = 8;
				return;

			}

			b.species = species;
			b.kg = rollWeight( species );
			b.phase = 'nibble';
			this.rumble( 0.18, 0.3, 70 );
			b.nibbles = 1 + Math.floor( Math.random() * 3 );
			b.t = 0.7 + Math.random() * 0.8;
			b.pulse = 0;

		} else if ( b.phase === 'nibble' ) {

			b.nibbles --;
			b.pulse = 0;
			if ( b.nibbles > 0 ) b.t = 0.6 + Math.random() * 1.0;
			else {

				b.phase = 'take';
				this.rumble( 0.45, 0.65, 160 );
				// big, strong fish give a (slightly) shorter window
				b.t = 2.4 - FISH[ b.species ].fight * 0.5;
				if ( this.app.audio && this.app.audio.fishSplash ) this.app.audio.fishSplash( rod.bobber, 0.35 );

			}

		} else if ( b.phase === 'take' ) {

			this.toast( 'It took the bait and ran', 1800 );
			const s = this.schoolNear();
		this.bite = { phase: 'wait', t: biteDelay( this.habitat(), this.hour, Math.random, s ? s.influence : 0 ) };

		}

	}

	strike() {

		const b = this.bite;
		if ( ! b || b.phase === 'wait' || b.phase === 'nibble' ) {

			// too early: a nibbling fish isn't hooked yet; it keeps nibbling (a hint, no penalty)
			if ( b && b.phase === 'nibble' ) this.toast( 'Not yet · wait for it to pull under', 1500 );
			return;

		}

		const g = this.state.stats;
		this.fight = new CatchMinigame( { species: b.species, kg: b.kg, lineKg: g.lineKg, reelSpeed: g.reelSpeed, distance: Math.max( 3, this.rod.lineOut ) } );
		this.bite = null;
		this.rod.hook();
		this.rumble( 0.6, 0.5, 180 );
		this.toast( 'Fish on!', 1200 );

	}

	updateFight( dt, reeling ) {

		const f = this.fight;
		const st = f.update( dt, reeling );
		// the fish thrashes at the surface as each run starts
		const au = this.app.audio;
		if ( f.surge > 0.6 && ! f._splashed && au && au.fishSplash ) au.fishSplash( this.rod.bobber, 0.3 + 0.5 * Math.min( 1, f.kg / 8 ) );
		f._splashed = f.surge > 0.6 ? true : f.surge < 0.3 ? false : f._splashed;
		if ( st === 'fighting' ) {

			if ( f.tension > f.band[ 1 ] ) this.rumble( 0.3, 0.75, 90, 220 ); // too much tension: it is complaining
			else if ( f.surge > 0.55 ) this.rumble( 0.7, 0.8, 120 ); // it is running
			return;

		}
		this.fight = null;
		this.rod.dip = 0;
		const name = FISH[ f.species ].name;
		if ( st === 'caught' ) {

			const entry = this.state.addFish( f.species, f.kg, this.hour, { x: this.rod.bobber.x, z: this.rod.bobber.z, hab: dominantHabitat( this.habitat() ) } );
			const info = this.state.lastCatch;
			if ( au && au.fishSplash ) au.fishSplash( this.rod.bobber, 0.8 );
			if ( au && au.fishFlop ) au.fishFlop();
			// the catch card (with a HUD) while the fish hangs on the line; a toast otherwise
			if ( this.hud ) this.landing = { species: f.species, kg: f.kg, card: info, cardT: 0 };
			else {

				if ( entry ) this.toast( `${ info.record ? 'New record! ' : '' }${ name } · ${ entry.kg.toFixed( 2 ) } kg · $${ this.state.priceOf( entry ) }${ this.state.orderMulFor( entry ) > 1 ? ' · Joe\'s order!' : '' }`, 3600 );
				else this.toast( `${ name } · ${ f.kg.toFixed( 1 ) } kg · no room in the ${ this.state.upgrades.hold > 0 ? 'hold' : 'cooler' }, let it go`, 3600 );
				this.landing = { species: f.species, kg: f.kg };

			}

			this.rod.land();
			this.rumble( 0.25, 0.9, 160 );

		} else if ( st === 'snapped' ) {

			this.rumble( 1, 0.2, 240 );
			this.toast( 'Snap! The line broke', 2400 );
			if ( au && au.lineSnap ) au.lineSnap();
			this.rod.setState( 'idle' );

		} else {

			this.toast( 'It threw the hook', 2000 );
			this.rod.endFight();

		}

	}

	// the landed fish goes in the cooler: card, fish and line away
	endLanding() {

		this.landing = null;
		this.display.hide();
		if ( this.hud && this.hud.catchOpen ) this.hud.hideCatch();
		if ( this.rod.state === 'landing' ) this.rod.setState( 'idle' );

	}

	// line in at once (mode change)
	cancelLine( silent = false ) {

		if ( this.fight && ! silent ) this.toast( 'Lost it', 1400 );
		this.fight = null;
		this.bite = null;
		if ( this.rod.state !== 'stowed' ) this.rod.setState( this.rod.equipped ? 'idle' : 'stowed' );

	}

}
