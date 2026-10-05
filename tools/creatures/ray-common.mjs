// Shared by the ray bakes (bake-eagle-ray.mjs, bake-stingray-family.mjs): welding, quadric edge collapse decimation and the packing of
// a level of detail into bytes.

// weld the vertices of a part that share a position (the glb splits at UV seams) and join its primitives
export function weld( list ) {

	const map = new Map(), pos = [], idx = [];
	for ( const { pos: p, idx: ix } of list ) {

		const remap = new Int32Array( p.length / 3 );
		for ( let i = 0; i < remap.length; i ++ ) {

			const key = Math.round( p[ i * 3 ] * 1e5 ) + ',' + Math.round( p[ i * 3 + 1 ] * 1e5 ) + ',' + Math.round( p[ i * 3 + 2 ] * 1e5 );
			let id = map.get( key );
			if ( id === undefined ) { id = pos.length / 3; map.set( key, id ); pos.push( p[ i * 3 ], p[ i * 3 + 1 ], p[ i * 3 + 2 ] ); }
			remap[ i ] = id;

		}

		for ( let i = 0; i < ix.length; i += 3 ) {

			const a = remap[ ix[ i ] ], b = remap[ ix[ i + 1 ] ], c = remap[ ix[ i + 2 ] ];
			if ( a !== b && b !== c && a !== c ) idx.push( a, b, c );

		}

	}

	return { pos: Float64Array.from( pos ), idx: Uint32Array.from( idx ) };

}

// ------------------------------------------------------------------ quadric edge collapse
export function simplify( mesh, target ) {

	const pos = Float64Array.from( mesh.pos );
	const nV = pos.length / 3;
	const tri = Uint32Array.from( mesh.idx );
	const nT = tri.length / 3;
	const alive = new Uint8Array( nT ).fill( 1 );
	let live = nT;
	const vt = Array.from( { length: nV }, () => new Set() ); // triangles of each vertex
	for ( let t = 0; t < nT; t ++ ) for ( let k = 0; k < 3; k ++ ) vt[ tri[ t * 3 + k ] ].add( t );
	const Q = new Float64Array( nV * 10 ); // symmetric 4x4: xx xy xz xw yy yz yw zz zw ww
	const addPlane = ( v, a, b, c, d, w ) => {

		const o = v * 10;
		Q[ o ] += w * a * a; Q[ o + 1 ] += w * a * b; Q[ o + 2 ] += w * a * c; Q[ o + 3 ] += w * a * d;
		Q[ o + 4 ] += w * b * b; Q[ o + 5 ] += w * b * c; Q[ o + 6 ] += w * b * d;
		Q[ o + 7 ] += w * c * c; Q[ o + 8 ] += w * c * d; Q[ o + 9 ] += w * d * d;

	};

	const faceNormal = ( t, out = [ 0, 0, 0 ] ) => {

		const a = tri[ t * 3 ] * 3, b = tri[ t * 3 + 1 ] * 3, c = tri[ t * 3 + 2 ] * 3;
		const ux = pos[ b ] - pos[ a ], uy = pos[ b + 1 ] - pos[ a + 1 ], uz = pos[ b + 2 ] - pos[ a + 2 ];
		const vx = pos[ c ] - pos[ a ], vy = pos[ c + 1 ] - pos[ a + 1 ], vz = pos[ c + 2 ] - pos[ a + 2 ];
		out[ 0 ] = uy * vz - uz * vy; out[ 1 ] = uz * vx - ux * vz; out[ 2 ] = ux * vy - uy * vx;
		return out;

	};

	for ( let t = 0; t < nT; t ++ ) {

		const n = faceNormal( t ), area2 = Math.hypot( n[ 0 ], n[ 1 ], n[ 2 ] );
		if ( area2 < 1e-18 ) continue;
		const a = n[ 0 ] / area2, b = n[ 1 ] / area2, c = n[ 2 ] / area2, p0 = tri[ t * 3 ] * 3;
		const d = - ( a * pos[ p0 ] + b * pos[ p0 + 1 ] + c * pos[ p0 + 2 ] );
		for ( let k = 0; k < 3; k ++ ) addPlane( tri[ t * 3 + k ], a, b, c, d, area2 * 0.5 );

	}

	// boundary edges (one triangle): a plane through the edge square to the face, heavily weighted
	const edgeCount = new Map();
	const ekey = ( a, b ) => ( a < b ? a * nV + b : b * nV + a );
	for ( let t = 0; t < nT; t ++ ) for ( let k = 0; k < 3; k ++ ) {

		const key = ekey( tri[ t * 3 + k ], tri[ t * 3 + ( k + 1 ) % 3 ] );
		edgeCount.set( key, ( edgeCount.get( key ) || 0 ) + 1 );

	}

	for ( let t = 0; t < nT; t ++ ) {

		const n = faceNormal( t ), l = Math.hypot( n[ 0 ], n[ 1 ], n[ 2 ] );
		if ( l < 1e-18 ) continue;
		for ( let k = 0; k < 3; k ++ ) {

			const a = tri[ t * 3 + k ], b = tri[ t * 3 + ( k + 1 ) % 3 ];
			if ( edgeCount.get( ekey( a, b ) ) !== 1 ) continue;
			const ex = pos[ b * 3 ] - pos[ a * 3 ], ey = pos[ b * 3 + 1 ] - pos[ a * 3 + 1 ], ez = pos[ b * 3 + 2 ] - pos[ a * 3 + 2 ];
			let px = ey * n[ 2 ] - ez * n[ 1 ], py = ez * n[ 0 ] - ex * n[ 2 ], pz = ex * n[ 1 ] - ey * n[ 0 ];
			const pl = Math.hypot( px, py, pz );
			if ( pl < 1e-18 ) continue;
			px /= pl; py /= pl; pz /= pl;
			const d = - ( px * pos[ a * 3 ] + py * pos[ a * 3 + 1 ] + pz * pos[ a * 3 + 2 ] );
			const w = 1e3 * Math.hypot( ex, ey, ez ) ** 2;
			addPlane( a, px, py, pz, d, w ); addPlane( b, px, py, pz, d, w );

		}

	}

	const quadricError = ( q, x, y, z ) => q[ 0 ] * x * x + 2 * q[ 1 ] * x * y + 2 * q[ 2 ] * x * z + 2 * q[ 3 ] * x + q[ 4 ] * y * y + 2 * q[ 5 ] * y * z + 2 * q[ 6 ] * y + q[ 7 ] * z * z + 2 * q[ 8 ] * z + q[ 9 ];
	const sum = new Float64Array( 10 );
	// the best place for the merged vertex of ( u, v ): the minimiser of the summed quadric if it is well conditioned, else the best of the ends and the middle
	const place = ( u, v ) => {

		for ( let i = 0; i < 10; i ++ ) sum[ i ] = Q[ u * 10 + i ] + Q[ v * 10 + i ];
		const [ a, b, c, d, e, f, g, h, i2 ] = [ sum[ 0 ], sum[ 1 ], sum[ 2 ], sum[ 3 ], sum[ 4 ], sum[ 5 ], sum[ 6 ], sum[ 7 ], sum[ 8 ] ];
		const det = a * ( e * h - f * f ) - b * ( b * h - f * c ) + c * ( b * f - e * c );
		const ux = pos[ u * 3 ], uy = pos[ u * 3 + 1 ], uz = pos[ u * 3 + 2 ], vx = pos[ v * 3 ], vy = pos[ v * 3 + 1 ], vz = pos[ v * 3 + 2 ];
		const cand = [ [ ux, uy, uz ], [ vx, vy, vz ], [ ( ux + vx ) / 2, ( uy + vy ) / 2, ( uz + vz ) / 2 ] ];
		const span = Math.hypot( ux - vx, uy - vy, uz - vz );
		if ( Math.abs( det ) > 1e-12 ) {

			// solve [ a b c; b e f; c f h ] x = -[ d g i2 ]
			const bx = - d, by = - g, bz = - i2;
			const x = ( bx * ( e * h - f * f ) - b * ( by * h - f * bz ) + c * ( by * f - e * bz ) ) / det;
			const y = ( a * ( by * h - f * bz ) - bx * ( b * h - f * c ) + c * ( b * bz - by * c ) ) / det;
			const z = ( a * ( e * bz - by * f ) - b * ( b * bz - by * c ) + bx * ( b * f - e * c ) ) / det;
			if ( Math.hypot( x - ( ux + vx ) / 2, y - ( uy + vy ) / 2, z - ( uz + vz ) / 2 ) < 2 * span ) cand.push( [ x, y, z ] );

		}

		let best = cand[ 0 ], be = Infinity;
		for ( const p of cand ) {

			const err = quadricError( sum, p[ 0 ], p[ 1 ], p[ 2 ] );
			if ( err < be ) { be = err; best = p; }

		}

		return { p: best, err: Math.max( 0, be ) };

	};

	// min-heap of candidate collapses
	const heap = [];
	const ver = new Uint32Array( nV );
	const push = ( item ) => {

		heap.push( item );
		let i = heap.length - 1;
		while ( i > 0 ) {

			const p = ( i - 1 ) >> 1;
			if ( heap[ p ].err <= heap[ i ].err ) break;
			[ heap[ p ], heap[ i ] ] = [ heap[ i ], heap[ p ] ];
			i = p;

		}

	};

	const pop = () => {

		const top = heap[ 0 ], last = heap.pop();
		if ( heap.length ) {

			heap[ 0 ] = last;
			let i = 0;
			for ( ;; ) {

				const l = i * 2 + 1, r = l + 1;
				let m = i;
				if ( l < heap.length && heap[ l ].err < heap[ m ].err ) m = l;
				if ( r < heap.length && heap[ r ].err < heap[ m ].err ) m = r;
				if ( m === i ) break;
				[ heap[ m ], heap[ i ] ] = [ heap[ i ], heap[ m ] ];
				i = m;

			}

		}

		return top;

	};

	const queueEdge = ( u, v ) => {

		const { p, err } = place( u, v );
		push( { u, v, vu: ver[ u ], vv: ver[ v ], err, p } );

	};

	const neighbours = ( v ) => {

		const out = new Set();
		for ( const t of vt[ v ] ) for ( let k = 0; k < 3; k ++ ) { const w = tri[ t * 3 + k ]; if ( w !== v ) out.add( w ); }
		return out;

	};

	const queued = new Set();
	for ( let t = 0; t < nT; t ++ ) for ( let k = 0; k < 3; k ++ ) {

		const a = tri[ t * 3 + k ], b = tri[ t * 3 + ( k + 1 ) % 3 ], key = ekey( a, b );
		if ( queued.has( key ) ) continue;
		queued.add( key );
		queueEdge( a, b );

	}

	// would moving u and v to p flip (or collapse) a triangle that survives?
	const nA = [ 0, 0, 0 ], nB = [ 0, 0, 0 ];
	const flips = ( u, v, p ) => {

		for ( const w of [ u, v ] ) for ( const t of vt[ w ] ) {

			const a = tri[ t * 3 ], b = tri[ t * 3 + 1 ], c = tri[ t * 3 + 2 ];
			if ( ( a === u || b === u || c === u ) && ( a === v || b === v || c === v ) ) continue; // this triangle goes
			faceNormal( t, nA );
			const old = Math.hypot( nA[ 0 ], nA[ 1 ], nA[ 2 ] );
			if ( old < 1e-18 ) continue;
			const keep = [ pos[ w * 3 ], pos[ w * 3 + 1 ], pos[ w * 3 + 2 ] ];
			pos[ w * 3 ] = p[ 0 ]; pos[ w * 3 + 1 ] = p[ 1 ]; pos[ w * 3 + 2 ] = p[ 2 ];
			faceNormal( t, nB );
			pos[ w * 3 ] = keep[ 0 ]; pos[ w * 3 + 1 ] = keep[ 1 ]; pos[ w * 3 + 2 ] = keep[ 2 ];
			const now = Math.hypot( nB[ 0 ], nB[ 1 ], nB[ 2 ] );
			if ( now < 1e-18 || ( nA[ 0 ] * nB[ 0 ] + nA[ 1 ] * nB[ 1 ] + nA[ 2 ] * nB[ 2 ] ) / ( old * now ) < 0.2 ) return true;

		}

		return false;

	};

	while ( live > target && heap.length ) {

		const it = pop();
		if ( ver[ it.u ] !== it.vu || ver[ it.v ] !== it.vv ) continue;
		const { u, v, p } = it;
		if ( flips( u, v, p ) ) continue;
		// merge u into v
		pos[ v * 3 ] = p[ 0 ]; pos[ v * 3 + 1 ] = p[ 1 ]; pos[ v * 3 + 2 ] = p[ 2 ];
		for ( let i = 0; i < 10; i ++ ) Q[ v * 10 + i ] += Q[ u * 10 + i ];
		for ( const t of [ ...vt[ u ] ] ) {

			let has = false;
			for ( let k = 0; k < 3; k ++ ) if ( tri[ t * 3 + k ] === v ) has = true;
			if ( has ) {

				alive[ t ] = 0; live --;
				for ( let k = 0; k < 3; k ++ ) vt[ tri[ t * 3 + k ] ].delete( t );

			} else {

				for ( let k = 0; k < 3; k ++ ) if ( tri[ t * 3 + k ] === u ) tri[ t * 3 + k ] = v;
				vt[ v ].add( t );

			}

		}

		vt[ u ].clear();
		ver[ u ] ++; ver[ v ] ++;
		for ( const w of neighbours( v ) ) queueEdge( v, w );

	}

	// compact
	const remap = new Int32Array( nV ).fill( - 1 );
	const outPos = [], outIdx = [];
	for ( let t = 0; t < nT; t ++ ) {

		if ( ! alive[ t ] ) continue;
		for ( let k = 0; k < 3; k ++ ) {

			const w = tri[ t * 3 + k ];
			if ( remap[ w ] < 0 ) { remap[ w ] = outPos.length / 3; outPos.push( pos[ w * 3 ], pos[ w * 3 + 1 ], pos[ w * 3 + 2 ] ); }
			outIdx.push( remap[ w ] );

		}

	}

	return { pos: Float64Array.from( outPos ), idx: Uint32Array.from( outIdx ) };

}

// ------------------------------------------------------------------ quantise and write
export const SCALE = 20000; // frame units -> int16 (range +-1.6)
const q16 = ( v ) => Math.max( - 32767, Math.min( 32767, Math.round( v * SCALE ) ) );

export function pack( meshes ) {

	// vertices: int16 x y z, uint8 part, int8 w; then the index list (uint16, three per triangle)
	const verts = [], idx = [];
	for ( const { mesh, part } of meshes ) {

		const base = verts.length, n = mesh.pos.length / 3;
		const nrm = new Float64Array( n * 3 );
		for ( let t = 0; t < mesh.idx.length; t += 3 ) {

			const a = mesh.idx[ t ], b = mesh.idx[ t + 1 ], c = mesh.idx[ t + 2 ];
			const ux = mesh.pos[ b * 3 ] - mesh.pos[ a * 3 ], uy = mesh.pos[ b * 3 + 1 ] - mesh.pos[ a * 3 + 1 ], uz = mesh.pos[ b * 3 + 2 ] - mesh.pos[ a * 3 + 2 ];
			const vx = mesh.pos[ c * 3 ] - mesh.pos[ a * 3 ], vy = mesh.pos[ c * 3 + 1 ] - mesh.pos[ a * 3 + 1 ], vz = mesh.pos[ c * 3 + 2 ] - mesh.pos[ a * 3 + 2 ];
			const nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
			for ( const w of [ a, b, c ] ) { nrm[ w * 3 ] += nx; nrm[ w * 3 + 1 ] += ny; nrm[ w * 3 + 2 ] += nz; }

		}

		for ( let i = 0; i < n; i ++ ) verts.push( [ q16( mesh.pos[ i * 3 ] ), q16( mesh.pos[ i * 3 + 1 ] ), q16( mesh.pos[ i * 3 + 2 ] ), part, nrm[ i * 3 + 1 ] >= 0 ? 1 : - 1 ] );
		for ( const i of mesh.idx ) idx.push( base + i );

	}

	if ( verts.length > 65535 ) throw new Error( 'too many vertices for 16-bit indices' );
	const bytes = Buffer.alloc( 4 + 4 + verts.length * 8 + idx.length * 2 );
	bytes.writeUInt32LE( verts.length, 0 );
	bytes.writeUInt32LE( idx.length / 3, 4 );
	let o = 8;
	for ( const v of verts ) { bytes.writeInt16LE( v[ 0 ], o ); bytes.writeInt16LE( v[ 1 ], o + 2 ); bytes.writeInt16LE( v[ 2 ], o + 4 ); bytes.writeUInt8( v[ 3 ], o + 6 ); bytes.writeInt8( v[ 4 ], o + 7 ); o += 8; }
	for ( const i of idx ) { bytes.writeUInt16LE( i, o ); o += 2; }
	return bytes;

}


// The whip as a short elliptical tube for the far levels of detail (the edge collapse eats the thin tip of the whip and shortens the tail):
// `rings` rings of `sides` vertices from the root to the tip (z along the whip), each ring the centre and the half extents of the original
// whip's vertices near that z, the last one a point at the tip. Wound outward.
export function tubeWhip( mesh, rings, sides ) {

	const p = mesh.pos, n = p.length / 3;
	let z0 = - Infinity, z1 = Infinity;
	for ( let i = 0; i < n; i ++ ) { z0 = Math.max( z0, p[ i * 3 + 2 ] ); z1 = Math.min( z1, p[ i * 3 + 2 ] ); }
	const pos = [], idx = [], centres = [];
	const dz = ( z0 - z1 ) / ( rings - 1 );
	for ( let r = 0; r < rings; r ++ ) {

		const z = z0 - r * dz;
		let sx = 0, sy = 0, c = 0;
		for ( let i = 0; i < n; i ++ ) if ( Math.abs( p[ i * 3 + 2 ] - z ) <= dz * 0.75 ) { sx += p[ i * 3 ]; sy += p[ i * 3 + 1 ]; c ++; }
		const cx = c ? sx / c : 0, cy = c ? sy / c : 0;
		let rx = 0, ry = 0;
		for ( let i = 0; i < n; i ++ ) if ( Math.abs( p[ i * 3 + 2 ] - z ) <= dz * 0.75 ) { rx = Math.max( rx, Math.abs( p[ i * 3 ] - cx ) ); ry = Math.max( ry, Math.abs( p[ i * 3 + 1 ] - cy ) ); }
		const last = r === rings - 1;
		centres.push( [ cx, cy, z ] );
		if ( last ) { pos.push( cx, cy, z ); continue; }
		for ( let k = 0; k < sides; k ++ ) {

			const a = k / sides * Math.PI * 2;
			pos.push( cx + Math.cos( a ) * rx, cy + Math.sin( a ) * ry, z );

		}

	}

	const at = ( r, k ) => ( r === rings - 1 ? ( rings - 1 ) * sides : r * sides + ( k % sides ) );
	const tri = ( a, b, c, centre ) => {

		const ux = pos[ b * 3 ] - pos[ a * 3 ], uy = pos[ b * 3 + 1 ] - pos[ a * 3 + 1 ], uz = pos[ b * 3 + 2 ] - pos[ a * 3 + 2 ];
		const vx = pos[ c * 3 ] - pos[ a * 3 ], vy = pos[ c * 3 + 1 ] - pos[ a * 3 + 1 ], vz = pos[ c * 3 + 2 ] - pos[ a * 3 + 2 ];
		const nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
		const mx = ( pos[ a * 3 ] + pos[ b * 3 ] + pos[ c * 3 ] ) / 3 - centre[ 0 ], my = ( pos[ a * 3 + 1 ] + pos[ b * 3 + 1 ] + pos[ c * 3 + 1 ] ) / 3 - centre[ 1 ];
		if ( nx * mx + ny * my + nz * 0 >= 0 ) idx.push( a, b, c ); else idx.push( a, c, b );

	};

	for ( let r = 0; r < rings - 1; r ++ ) for ( let k = 0; k < sides; k ++ ) {

		const a = at( r, k ), b = at( r, k + 1 ), c = at( r + 1, k ), d = at( r + 1, k + 1 );
		tri( a, b, c, centres[ r ] );
		if ( r < rings - 2 ) tri( b, d, c, centres[ r ] );

	}

	return { pos: Float64Array.from( pos ), idx: Uint32Array.from( idx ) };

}
