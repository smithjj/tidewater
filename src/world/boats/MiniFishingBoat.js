// The mini fishing boat, a 3 m one-man fishing punt with a flat bottom, two swivel seats, a bow trolling motor
// and an aft drive: a static glTF boat (public/models/boats/mini_fishing_boat.glb), the third boat of the game
// and the one the player is meant to start with. Like the Pelagic 30 it is a factor-based PBR asset whose node
// transforms we bake into the vertices; the geometry is merged into one mesh per glTF material. Unlike the
// Pelagic it has no deck to walk (no `lines`): boarding takes you straight to the helm, the forward seat.
//
// The export lays the boat along X with the bow on -X, keel on y = 0.07 (the keel runners reach 0.03), beam
// +-0.70 on Z, and carries three studio extras that are left out: the ground shadow plane, the reference
// photograph and the big text decals (3 meshes, 60,000 triangles that cannot be seen at game scale).
// Two things the export lost: the hull's marsh camouflage (the material has no colour, so it is a plain
// fog-green here) and the reed upholstery texture of the seats (a straw-green factor stands in).

import * as THREE from '../../engine/index.js';
import { loadGLB } from '../../engine/loaders/GLTF.js';
import { physical } from '../../materials/Materials.js';

const MODEL_URL = 'models/boats/mini_fishing_boat.glb';

// Model-space fixups. The bow is on -X: a +90 deg yaw turns -X to +Z (BoatModel convention). The origin is moved
// so the design waterline (y = 0.18 in the file) is y = 0 in the boat frame and the hull centre is at x = z = 0.
const BOW_TO_FORWARD = Math.PI / 2;
const WATERLINE_OFFSET = - 0.18; // metres: the hull bottom (y 0.072) draws 0.11 m with ~330 kg aboard
const CENTER_X = - 0.03; // the file's x of the middle of the waterline (model frame, before the yaw)

// nodes of the file that are not the boat (or are too fine to see): ground plane, reference photograph, the text decals
const SKIP = /^(Ground|REFERENCE)|specification decal|Hull model decal|Model suffix/i;
// the hull surface that hides the sea inside the boat: the hull shell and the cockpit sole
const MASK = /^(HULL|Cockpit sole|Port keel runner|Starboard keel runner)/i;

// glTF material colours the file has no factor for (the camouflage hull, the reed seat texture)
const FALLBACK = {
	'HULL': { color: [ 0.2, 0.25, 0.19 ], roughness: 0.47 }, // marsh green / fog gray
	'CHAIRS': { color: [ 0.22, 0.24, 0.13 ], roughness: 0.88 }, // woven reeds
};

export class MiniFishingBoat {

	constructor() {

		this.group = new THREE.Group();
		this.group.name = 'MiniFishingBoat';
		this.query = null;
		this.slot = - 1;
		this._y = 0; // last valid water height, keeps the boat level while the query catches up

		// ------------------------------------------------------------------ physics adapter
		// Everything BoatController reads off BoatModel, measured from the hull of the file (a slice at the
		// waterline: 1.12 m wide over the middle two thirds, 0.66 m at the bow tip, 0.97 m at the square
		// transom): 12 waterplane patches (2 lateral x 6 longitudinal, 0.49 m long, 0.97 of the plan area, the
		// lateral position at 0.75 of the half breadth so the roll stiffness equals the real I_T = 0.34 m^4 after
		// the controller's 0.79 narrowing) and the draft per station from the hull bottom (y 0.072 -> -0.11 m).
		// ~330 kg: 130 kg of boat, an angler and gear. The 6 stations (z = bow first, boat frame +Z forward).
		const V = ( x, y, z ) => new THREE.Vector3( x, y, z );
		const ST = [ // [ z, half breadth at the waterline ]
			[ 1.22, 0.45 ], [ 0.73, 0.555 ], [ 0.23, 0.56 ], [ - 0.27, 0.56 ], [ - 0.76, 0.545 ], [ - 1.25, 0.5 ],
		];
		this.hullSamples = [];
		for ( const [ z, hb ] of ST ) for ( const sd of [ - 1, 1 ] ) this.hullSamples.push( { position: V( sd * hb * 0.75, - 0.095, z ), area: hb * 0.49 * 0.97, bottomY: - 0.11 } );
		this.hydro = {
			suggestedMass: 330,
			centerOfMass: V( 0, 0.32, 0.05 ), // the angler sits a little forward of the middle, the battery aft and low
			inertia: V( 250, 270, 85 ), // pitch, yaw, roll: 0.26 L, 0.27 L, 0.36 B radii of gyration
		};
		this.forceScale = 0.12; // the controller's damping / resistance / mooring constants are for a 3 t boat
		// reserve buoyancy of the flared topsides: pushes only once the water reaches the rail (see BoatController)
		this.reserveSamples = [];
		for ( const z of [ - 0.9, - 0.45, 0, 0.45, 0.9 ] ) for ( const sd of [ - 1, 1 ] ) this.reserveSamples.push( { position: V( sd * 0.62, 0.12, z ), area: 0.35 } );
		this.propeller = V( 0, - 0.12, - 1.5 ); // the aft drive, one thrust point under the stern
		this.rudder = { z: - 1.45 }; // the drive turns: approximated with the rudder-force model
		this.maxThrust = 700; // N bollard pull of a 55 lb thrust / pedal drive plus the trolling motor
		this.pitchSpeed = 6.5; // m/s: thrust falls to nothing there, so ~4.5 m/s flat out
		this.reverseFactor = 0.55;
		this.stations = [ [ - 1.3, 0.16 ], [ - 0.6, 0.2 ], [ 0.1, 0.2 ], [ 0.8, 0.18 ], [ 1.3, 0.08 ] ]; // (z, lateral area m^2): a shallow flat hull
		this.lateralY = - 0.02;
		this.hullLift = 0.15; // flat bottom and no keel: it slides in turns
		this.rudderLift = 5;
		this.contactPoints = [
			V( 0, - 0.12, 1.3 ), V( 0, - 0.12, 0 ), V( 0, - 0.12, - 1.35 ),
			V( 0.5, - 0.1, 0.6 ), V( - 0.5, - 0.1, 0.6 ), V( 0.5, - 0.1, - 1.2 ), V( - 0.5, - 0.1, - 1.2 ),
			V( 0, 0.1, 1.5 ),
		];
		this.outline = [
			V( 0, 0.2, 1.55 ), V( 0.6, 0.2, 0.9 ), V( - 0.6, 0.2, 0.9 ),
			V( 0.66, 0.2, - 0.5 ), V( - 0.66, 0.2, - 0.5 ), V( 0.6, 0.2, - 1.5 ), V( - 0.6, 0.2, - 1.5 ),
		];
		this.bowZ = 1.6;
		this.chockY = 0.45; // the bow eye
		this.sternZ = - 1.5;
		// helm: the forward seat (z 0.35 .. 0.96, back to the aft seat, facing the bow and the bow control that steers
		// the trolling motor); the player boards over the middle of the boat and sits down there
		this.helmEye = V( 0, 1.5, 0.62 );
		this.helmPoint = V( 0, 0.78, 0.62 );
		this.boardPoint = V( 0, 0.5, 0 );
		this.exitPoints = [ V( - 0.65, 0.5, 0.1 ), V( 0.65, 0.5, 0.1 ) ]; // both gunwales, amidships
		this.colliders = [];

	}

	// the controller animates the lobster boat's lever / wheel / propeller: nothing to move here
	setThrottle() {}
	setSteering() {}
	setPropellerRPM() {}

	async load() {

		const gltf = await loadGLB( MODEL_URL );
		// the boat's root (the other scene roots are the studio ground and the reference photograph)
		const root = gltf.roots.find( ( i ) => /MINI BOAT/i.test( gltf.nodes[ i ].name ) ) ?? gltf.roots[ 1 ];
		const buckets = new Map();
		this._collect( gltf, root, buckets );

		for ( const [ mi, b ] of buckets ) {

			if ( ! b.pos.length ) continue;
			const geo = new THREE.BufferGeometry();
			geo.setAttribute( 'position', new THREE.Float32BufferAttribute( b.pos, 3 ) );
			geo.setAttribute( 'normal', new THREE.Float32BufferAttribute( b.nor, 3 ) );
			geo.setAttribute( 'uv', new THREE.Float32BufferAttribute( b.uv, 2 ) );
			geo.setIndex( b.pos.length / 3 > 65535 ? new THREE.Uint32BufferAttribute( b.idx, 1 ) : new THREE.Uint16BufferAttribute( b.idx, 1 ) );
			geo.computeBoundingSphere();

			const gmat = gltf.materials[ mi ] || {};
			const mesh = new THREE.Mesh( geo, this._material( gmat ) );
			mesh.name = 'mini-' + ( gmat.name || mi );
			mesh.castShadow = true;
			mesh.receiveShadow = true;
			this.group.add( mesh );

		}

		this._maskGeometry = this._buildMaskGeometry();

	}

	_buildMaskGeometry() {

		if ( ! this._mask || ! this._mask.idx.length ) return null;
		const geo = new THREE.BufferGeometry();
		geo.setAttribute( 'position', new THREE.Float32BufferAttribute( this._mask.pos, 3 ) );
		geo.setAttribute( 'normal', new THREE.Float32BufferAttribute( this._mask.nor, 3 ) );
		geo.setIndex( this._mask.pos.length / 3 > 65535 ? new THREE.Uint32BufferAttribute( this._mask.idx, 1 ) : new THREE.Uint16BufferAttribute( this._mask.idx, 1 ) );
		geo.computeBoundingBox();
		geo.computeBoundingSphere();
		this._mask = null;
		return geo;

	}

	// Depth-first from the boat root, composing node transforms; the studio extras and the decals are skipped.
	_collect( gltf, root, buckets ) {

		const corr = new THREE.Matrix4().compose(
			new THREE.Vector3( 0, WATERLINE_OFFSET, 0 ),
			new THREE.Quaternion().setFromEuler( new THREE.Euler( 0, BOW_TO_FORWARD, 0 ) ),
			new THREE.Vector3( 1, 1, 1 ),
		).multiply( new THREE.Matrix4().makeTranslation( - CENTER_X, 0, 0 ) );
		const visit = ( i, parent ) => {

			const n = gltf.nodes[ i ];
			const local = new THREE.Matrix4().compose(
				new THREE.Vector3( n.t[ 0 ], n.t[ 1 ], n.t[ 2 ] ),
				new THREE.Quaternion( n.r[ 0 ], n.r[ 1 ], n.r[ 2 ], n.r[ 3 ] ),
				new THREE.Vector3( n.s[ 0 ], n.s[ 1 ], n.s[ 2 ] ),
			);
			const world = parent ? new THREE.Matrix4().multiplyMatrices( parent, local ) : local;
			if ( SKIP.test( n.name ) ) return;
			if ( n.mesh !== undefined ) for ( const prim of gltf.meshes[ n.mesh ] ) this._addPrim( prim, corr, world, buckets, MASK.test( n.name ) );
			for ( const c of n.children ) visit( c, world );

		};
		visit( root, null );

	}

	_addPrim( prim, corr, world, buckets, mask ) {

		if ( prim.mode !== 4 || ! prim.indices ) return;
		const mi = prim.material ?? 0;
		let b = buckets.get( mi );
		if ( ! b ) buckets.set( mi, b = { pos: [], nor: [], uv: [], idx: [] } );
		const m = new THREE.Matrix4().multiplyMatrices( corr, world );
		const e = m.elements, ne = new THREE.Matrix3().getNormalMatrix( m ).elements;
		const A = prim.attributes;
		const pos = A.POSITION.array, nor = A.NORMAL ? A.NORMAL.array : null, uv = A.TEXCOORD_0 ? A.TEXCOORD_0.array : null;
		const count = pos.length / 3;
		const base = b.pos.length / 3;
		const start = b.pos.length;
		for ( let i = 0; i < count; i ++ ) {

			const x = pos[ i * 3 ], y = pos[ i * 3 + 1 ], z = pos[ i * 3 + 2 ];
			b.pos.push( e[ 0 ] * x + e[ 4 ] * y + e[ 8 ] * z + e[ 12 ], e[ 1 ] * x + e[ 5 ] * y + e[ 9 ] * z + e[ 13 ], e[ 2 ] * x + e[ 6 ] * y + e[ 10 ] * z + e[ 14 ] );
			if ( nor ) {

				const nx = nor[ i * 3 ], ny = nor[ i * 3 + 1 ], nz = nor[ i * 3 + 2 ];
				const tx = ne[ 0 ] * nx + ne[ 3 ] * ny + ne[ 6 ] * nz;
				const ty = ne[ 1 ] * nx + ne[ 4 ] * ny + ne[ 7 ] * nz;
				const tz = ne[ 2 ] * nx + ne[ 5 ] * ny + ne[ 8 ] * nz;
				const l = Math.hypot( tx, ty, tz ) || 1;
				b.nor.push( tx / l, ty / l, tz / l );

			} else b.nor.push( 0, 1, 0 );

			b.uv.push( uv ? uv[ i * 2 ] : 0, uv ? uv[ i * 2 + 1 ] : 0 );

		}

		for ( let i = 0; i < prim.indices.length; i ++ ) b.idx.push( base + prim.indices[ i ] );
		if ( mask ) this._maskTriangles( b.pos, b.nor, prim.indices, base, start );

	}

	// the triangles of the hull shell and the sole, for the water-exclusion mask
	_maskTriangles( pos, nor, indices, base, start ) {

		if ( ! this._mask ) this._mask = { pos: [], nor: [], idx: [] };
		const m = this._mask;
		for ( let k = 0; k < indices.length; k += 3 ) {

			const at = m.pos.length / 3;
			for ( let j = 0; j < 3; j ++ ) {

				const q = ( base + indices[ k + j ] ) * 3;
				m.pos.push( pos[ q ], pos[ q + 1 ], pos[ q + 2 ] );
				m.nor.push( nor[ q ], nor[ q + 1 ], nor[ q + 2 ] );

			}

			m.idx.push( at, at + 1, at + 2 );

		}

	}

	// glTF material factors -> ScenePhysicalMaterial (the engine has no textures here: see FALLBACK for the two
	// materials whose colour the file keeps in a texture or not at all)
	_material( g ) {

		g = g || {};
		const pbr = g.pbrMetallicRoughness || {};
		const ext = g.extensions || {};
		const cc = ext.KHR_materials_clearcoat;
		const em = g.emissiveFactor || [ 0, 0, 0 ];
		const es = ( ext.KHR_materials_emissive_strength && ext.KHR_materials_emissive_strength.emissiveStrength ) || 1;
		const fb = FALLBACK[ ( g.name || '' ).split( ' | ' )[ 0 ] ];
		const c = pbr.baseColorFactor || ( fb ? [ ...fb.color, 1 ] : [ 0.8, 0.8, 0.8, 1 ] );
		const m = physical( {
			color: new THREE.Color().setRGB( c[ 0 ], c[ 1 ], c[ 2 ] ),
			roughness: fb ? fb.roughness : pbr.roughnessFactor ?? 1,
			metalness: pbr.metallicFactor ?? 0,
			emissive: new THREE.Color().setRGB( em[ 0 ] * es, em[ 1 ] * es, em[ 2 ] * es ),
			side: THREE.DoubleSide, // the file is all doubleSided
			...cc && cc.clearcoatFactor > 0 ? { clearcoat: cc.clearcoatFactor, clearcoatRoughness: cc.clearcoatRoughnessFactor ?? 0.1 } : {},
		} );
		m.name = g.name || 'miniBoatMat';
		return m;

	}

	// The water-exclusion mask for the depth pre-pass: the hull shell and the sole, collected in load()
	createHullVolumeGeometry() {

		return this._maskGeometry;

	}

	// Moored on the swell: a single water query under the boat, read a frame behind (only until it has a controller).
	init( query ) {

		this.query = query;
		this.slot = query.allocate( 'miniBoat', 1 );

	}

	update() {

		if ( ! this.query || this.slot < 0 ) return;
		this.query.setPoint( this.slot, this.group.position.x, this.group.position.z );
		if ( this.query.cpuValid ) {

			const o = this.slot * 4;
			const h = this.query.cpu[ o ], nx = this.query.cpu[ o + 1 ], nz = this.query.cpu[ o + 2 ];
			if ( Number.isFinite( h ) ) this._y = h;
			const k = 0.5, s = 0.05;
			this.group.rotation.x = THREE.MathUtils.damp( this.group.rotation.x, nz * k, s, 1 / 60 );
			this.group.rotation.z = THREE.MathUtils.damp( this.group.rotation.z, - nx * k, s, 1 / 60 );

		}

		this.group.position.y = this._y;

	}

}
