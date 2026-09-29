// The Pelagic 30, an offshore center console: a static glTF boat (public/models/boats/pelagic_30.glb)
// moored next to the lobster boat. The asset is factor-based PBR only (no textures, no skin), so the
// engine's loadGLB gives us everything: we bake the node transforms into the vertices, merge the 515
// primitives into one geometry per glTF material and translate the glTF material factors into
// ScenePhysicalMaterials. Unlike BoatModel the hull is not procedural, so there are no part handles.

import * as THREE from '../../engine/index.js';
import { loadGLB } from '../../engine/loaders/GLTF.js';
import { physical } from '../../materials/Materials.js';

const MODEL_URL = 'models/boats/pelagic_30.glb';

// Model-space fixups, tuned against the lobster boat by eye. Despite the root node's "+Y bow"
// label (Blender convention), the export is a normal upright glTF asset: keel on y = 0, length
// along Z with the bow on -Z. A 180 deg yaw points the bow on +Z (BoatModel convention).
const BOW_TO_FORWARD = Math.PI; // yaw 180: -Z bow -> +Z forward
const MODEL_SCALE = 1; // glTF units -> metres
const WATERLINE_OFFSET = - 0.55; // metres: keel sits this far below the surface (draft)

export class Pelagic30 {

	constructor() {

		this.group = new THREE.Group();
		this.group.name = 'Pelagic30';
		this.query = null;
		this.slot = - 1;
		this._y = 0; // last valid water height, keeps the boat level while the query catches up

		// ------------------------------------------------------------------ physics adapter
		// Everything BoatController and the boarding flow read off BoatModel, measured from the
		// loaded geometry (see the mooring note in WorldLayout): 12 waterplane patches (2 lateral
		// x 6 longitudinal) from slicing the hull, anchors from the material-bucket bounds. Patch
		// y is the per-station draft, calibrated so ~2.9 t of displacement floats the origin on
		// the design waterline (same semantics as HullLines.buildHullSamples). The absence of
		// `lines` is what opts this boat out of deck-walking (Player boards straight to the helm).
		const V = ( x, y, z ) => new THREE.Vector3( x, y, z );
		this.hullSamples = [
			{ position: V( - 0.83, - 0.21, - 4.51 ), area: 2.27, bottomY: - 0.76 }, { position: V( 0.83, - 0.21, - 4.51 ), area: 2.27, bottomY: - 0.76 },
			{ position: V( - 0.9, - 0.12, - 2.73 ), area: 2.47, bottomY: - 0.45 }, { position: V( 0.9, - 0.12, - 2.73 ), area: 2.47, bottomY: - 0.45 },
			{ position: V( - 0.91, - 0.12, - 0.94 ), area: 2.5, bottomY: - 0.45 }, { position: V( 0.91, - 0.12, - 0.94 ), area: 2.5, bottomY: - 0.45 },
			{ position: V( - 0.91, - 0.12, 0.84 ), area: 2.5, bottomY: - 0.42 }, { position: V( 0.91, - 0.12, 0.84 ), area: 2.5, bottomY: - 0.42 },
			{ position: V( - 0.82, - 0.07, 2.62 ), area: 2.25, bottomY: - 0.27 }, { position: V( 0.82, - 0.07, 2.62 ), area: 2.25, bottomY: - 0.27 },
			{ position: V( - 0.41, 0, 4.41 ), area: 1.14, bottomY: 0.31 }, { position: V( 0.41, 0, 4.41 ), area: 1.14, bottomY: 0.31 },
		];
		// ~2.9 t with twin outboards aft: CoM a little astern of midship, gear high under the T-top
		this.hydro = {
			suggestedMass: 2900,
			centerOfMass: V( 0, 0.55, - 0.45 ),
			inertia: V( 13000, 16000, 3600 ),
		};
		this.propeller = V( 0, - 0.3, - 5.3 ); // twin outboards on the transom, one thrust point
		this.rudder = { z: - 4.9 }; // outboard steering, approximated with the rudder-force model
		this.maxThrust = 20000;
		this.pitchSpeed = 17;
		this.reverseFactor = 0.3; // outboards astern
		this.stations = [ [ - 4.4, 0.7 ], [ - 2.8, 0.8 ], [ - 1.2, 0.85 ], [ 0.5, 0.85 ], [ 2.2, 0.6 ], [ 3.6, 0.2 ] ]; // (z, lateral area)
		this.lateralY = 0.1;
		this.hullLift = 0.65; // more drift drag than the full-keel lobster boat: speed bleeds off in turns
		this.rudderLift = 1.7; // outboards deflect less than a hull-mounted rudder in the race
		this.contactPoints = [
			V( 0, - 0.75, 3.4 ), V( 0, - 0.8, 0 ), V( 0, - 0.75, - 3.6 ),
			V( 1.2, - 0.4, 1.6 ), V( - 1.2, - 0.4, 1.6 ), V( 1.3, - 0.35, - 2.8 ), V( - 1.3, - 0.35, - 2.8 ),
			V( 0, 0.2, 4.6 ),
		];
		this.outline = [
			V( 0, 0.3, 4.8 ), V( 1.4, 0.3, 2.4 ), V( - 1.4, 0.3, 2.4 ),
			V( 1.6, 0.3, - 1.2 ), V( - 1.6, 0.3, - 1.2 ), V( 1.4, 0.3, - 4.2 ), V( - 1.4, 0.3, - 4.2 ),
		];
		this.bowZ = 5.2;
		this.sternZ = - 5.1;
		// helm: standing at the wheel on the console deck (chart screen at z ≈ -0.58)
		this.helmEye = V( 0, 2.45, - 1.35 );
		this.helmPoint = V( 0.4, 1.2, - 1.35 );
		this.boardPoint = V( 0, 0.78, - 4.4 ); // on the aft cockpit sole, inside the transom bench
		this.exitPoints = [ V( - 1.5, 0.9, 0.3 ), V( 1.5, 0.9, 0.3 ) ]; // both rails, midship

		// ------------------------------------------------------------------ deck walking
		// `lines` facade (Player.updateDeck contract) + axis-aligned deck colliders, from the
		// measured layout: teak sole y 0.70 aft (z -4.6..-2.5) and forward (1.4..4.0); the console
		// deck spans the full beam between them (y 0.90, z -2.5..1.4) and is stepped up onto; the
		// windshield box (x ±0.55) blocks its middle, passed on the open sides.
		this.lines = {
			deckY: 0.7,
			shell: 0.07,
			zAft: - 4.6,
			zFwd: 4.0,
			tAtSheerZ: ( z ) => Math.min( 1, Math.max( 0, ( z - this.lines.zAft ) / ( this.lines.zFwd - this.lines.zAft ) ) ),
			// half beam at hull parameter t and height y: piecewise-linear over the measured
			// topside outline (max |x| below the rail), inset ~3%, slight flare with height
			halfBreadth: ( t, y ) => {

				const BEAM = [ [ - 5.4, 0.7 ], [ - 5.1, 0.9 ], [ - 4.85, 1.36 ], [ - 4.6, 1.42 ], [ - 4.0, 1.38 ], [ - 3.4, 1.4 ], [ - 2.8, 1.44 ], [ - 2.2, 1.48 ], [ - 1.6, 1.51 ], [ - 1.0, 1.52 ], [ - 0.4, 1.52 ], [ 0.2, 1.52 ], [ 0.8, 1.5 ], [ 1.4, 1.44 ], [ 1.9, 1.2 ], [ 2.4, 1.07 ], [ 2.9, 0.9 ], [ 3.4, 0.7 ], [ 3.9, 0.34 ], [ 4.15, 0.05 ] ];
				const z = this.lines.zAft + t * ( this.lines.zFwd - this.lines.zAft );
				let b0 = BEAM[ 0 ][ 1 ], z0 = BEAM[ 0 ][ 0 ];
				for ( const [ bz, bb ] of BEAM ) {

					if ( bz >= z ) return ( b0 + ( bb - b0 ) * ( ( z - z0 ) / Math.max( 1e-9, bz - z0 ) ) ) * 0.97 * ( 1 + 0.06 * Math.max( 0, y - this.lines.deckY ) );
					b0 = bb; z0 = bz;

				}

				return b0 * 0.97;

			},
		};
		const box = ( x0, y0, z0, x1, y1, z1, walkable = false, solid = true ) => ( {
			center: new THREE.Vector3( ( x0 + x1 ) / 2, ( y0 + y1 ) / 2, ( z0 + z1 ) / 2 ),
			half: new THREE.Vector3( Math.abs( x1 - x0 ) / 2, Math.abs( y1 - y0 ) / 2, Math.abs( z1 - z0 ) / 2 ),
			walkable, solid,
		} );
		this.colliders = [
			// the console deck: step up onto it (0.2 m), stand at the helm
			box( - 1.3, 0.7, - 2.5, 1.3, 0.9, 1.4, true, false ),
			// windshield box around the helm: solid, passed on the open sides of the console deck
			box( - 0.55, 0.9, - 0.75, 0.55, 1.8, 0.7 ),
			// helm seat pedestal (the ring aft of the wheel)
			box( - 0.5, 0.9, 0.05, 0.5, 1.35, 0.55 ),
			// aft cockpit side cushions and transom bench (solid; tops too high to step)
			box( 0.9, 0.7, - 4.0, 1.45, 1.08, - 2.5 ),
			box( - 1.45, 0.7, - 4.0, - 0.9, 1.08, - 2.5 ),
			box( - 0.8, 0.7, - 5.45, 0.8, 1.4, - 4.95 ),
			// forward cockpit cushions, port and starboard (center teak walkway stays open)
			box( 0.7, 0.7, 1.5, 1.4, 1.5, 3.3 ),
			box( - 1.4, 0.7, 1.5, - 0.7, 1.5, 3.3 ),
		];

	}

	// the controller animates the lobster boat's lever / wheel / propeller: nothing to move here
	setThrottle() {}
	setSteering() {}
	setPropellerRPM() {}

	async load() {

		const gltf = await loadGLB( MODEL_URL );
		// the boat's root (the other scene root is the studio backdrop)
		const root = gltf.roots.find( ( i ) => /root|pelagic/i.test( gltf.nodes[ i ].name ) ) ?? gltf.roots[ 0 ];
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
			const glass = !! ( gmat.extensions && gmat.extensions.KHR_materials_transmission );
			const mesh = new THREE.Mesh( geo, this._material( gmat ) );
			mesh.name = glass ? 'boat-glass' : 'pelagic-' + ( gmat.name || mi );
			mesh.castShadow = ! glass;
			mesh.receiveShadow = true;
			this.group.add( mesh );

			// the water-exclusion mask is the boat's real surface (see createHullVolumeGeometry)
			this._maskTriangles( b.pos, b.nor, b.idx );

		}

		this._maskGeometry = this._buildMaskGeometry();

	}

	// Collect the real hull/deck/engine triangles (below the T-top) for the water mask.
	_maskTriangles( pos, nor, idx ) {

		if ( ! this._mask ) this._mask = { pos: [], nor: [], idx: [] };
		const m = this._mask;
		for ( let k = 0; k < idx.length; k += 3 ) {

			const a = idx[ k ] * 3, b = idx[ k + 1 ] * 3, c = idx[ k + 2 ] * 3;
			// skip the T-top / rail / antenna triangles: the waterline never reaches them
			const hi = Math.min( pos[ a + 1 ], pos[ b + 1 ], pos[ c + 1 ] );
			if ( hi > 1.4 ) continue;
			const base = m.pos.length / 3;
			for ( const j of [ a, b, c ] ) {

				m.pos.push( pos[ j ], pos[ j + 1 ], pos[ j + 2 ] );
				m.nor.push( nor[ j ], nor[ j + 1 ], nor[ j + 2 ] );

			}

			m.idx.push( base, base + 1, base + 2 );

		}

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

	// Depth-first from the boat root, composing node transforms; the studio ground subtree is skipped.
	_collect( gltf, root, buckets ) {

		const corr = new THREE.Matrix4().compose(
			new THREE.Vector3( 0, WATERLINE_OFFSET, 0 ),
			new THREE.Quaternion().setFromEuler( new THREE.Euler( 0, BOW_TO_FORWARD, 0 ) ),
			new THREE.Vector3( MODEL_SCALE, MODEL_SCALE, MODEL_SCALE ),
		);
		const visit = ( i, parent ) => {

			const n = gltf.nodes[ i ];
			const local = new THREE.Matrix4().compose(
				new THREE.Vector3( n.t[ 0 ], n.t[ 1 ], n.t[ 2 ] ),
				new THREE.Quaternion( n.r[ 0 ], n.r[ 1 ], n.r[ 2 ], n.r[ 3 ] ),
				new THREE.Vector3( n.s[ 0 ], n.s[ 1 ], n.s[ 2 ] ),
			);
			const world = parent ? new THREE.Matrix4().multiplyMatrices( parent, local ) : local;
			if ( ! /studio/i.test( n.name ) ) {

				if ( n.mesh !== undefined ) for ( const prim of gltf.meshes[ n.mesh ] ) this._addPrim( prim, corr, world, buckets );
				for ( const c of n.children ) visit( c, world );

			}

		};
		visit( root, null );

	}

	_addPrim( prim, corr, world, buckets ) {

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

	}

	// glTF material factors -> ScenePhysicalMaterial. The engine has no transmission, so the
	// windshield glass is approximated like BoatMaterials' glass: blended, faint, drawn late
	// (App moves any mesh named 'boat-glass' onto the transparent layer).
	_material( g ) {

		g = g || {};
		const pbr = g.pbrMetallicRoughness || {};
		const ext = g.extensions || {};
		const cc = ext.KHR_materials_clearcoat;
		const glass = !! ext.KHR_materials_transmission;
		const em = g.emissiveFactor || [ 0, 0, 0 ];
		const es = ( ext.KHR_materials_emissive_strength && ext.KHR_materials_emissive_strength.emissiveStrength ) || 1;
		const c = pbr.baseColorFactor || [ 0.8, 0.8, 0.8, 1 ];
		const m = physical( {
			color: new THREE.Color().setRGB( c[ 0 ], c[ 1 ], c[ 2 ] ),
			roughness: pbr.roughnessFactor ?? 1,
			metalness: pbr.metallicFactor ?? 1,
			emissive: new THREE.Color().setRGB( em[ 0 ] * es, em[ 1 ] * es, em[ 2 ] * es ),
			side: THREE.DoubleSide, // the file is all doubleSided
			...glass ? {
				transparent: true, opacity: 0.25, depthWrite: false, velocityWeight: 0,
				ior: ( ext.KHR_materials_ior && ext.KHR_materials_ior.ior ) || 1.5,
			} : {},
			...cc && cc.clearcoatFactor > 0 ? { clearcoat: cc.clearcoatFactor, clearcoatRoughness: cc.clearcoatRoughnessFactor ?? 0.1 } : {},
		} );
		m.name = g.name || 'pelagicMat';
		return m;

	}

	// The water-exclusion mask for the depth pre-pass: the boat's REAL surface (hull, deck,
	// engines; everything below the T-top), collected in load(). Unlike BoatModel's hand-built
	// hull volume, a watertight studio asset gives an exact fit — no hand-made approximation can
	// sit on the real stem, raked transom and engine brackets, and anywhere the approximation
	// pokes past the hull carves visible patches out of the water. The material is double-sided
	// and only reads the position, so an open shell is fine.
	createHullVolumeGeometry() {

		return this._maskGeometry;

	}

	// Moored on the swell: a single water query under the boat, read a frame behind.
	init( query ) {

		this.query = query;
		this.slot = query.allocate( 'pelagic', 1 );

	}

	update() {

		if ( ! this.query || this.slot < 0 ) return;
		this.query.setPoint( this.slot, this.group.position.x, this.group.position.z );
		if ( this.query.cpuValid ) {

			const o = this.slot * 4;
			const h = this.query.cpu[ o ], nx = this.query.cpu[ o + 1 ], nz = this.query.cpu[ o + 2 ];
			if ( Number.isFinite( h ) ) this._y = h;
			// slope -> gentle pitch/roll, damped so the readback noise doesn't jitter the deck
			const k = 0.5, s = 0.05;
			this.group.rotation.x = THREE.MathUtils.damp( this.group.rotation.x, nz * k, s, 1 / 60 );
			this.group.rotation.z = THREE.MathUtils.damp( this.group.rotation.z, - nx * k, s, 1 / 60 );

		}
		this.group.position.y = this._y;

	}

}
