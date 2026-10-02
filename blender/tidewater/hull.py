# Hull builder -- pure-Python port of src/world/boat/HullBuilder.js.
#
# Everything buildHull() adds to the boat's material buckets: the outer shell + transom,
# the keel/skeg with its shoe and heel bearing, the inner lining (bulwarks), the cockpit
# sole and cambered foredeck, the gunwale caps and the rubrail (wood + stainless strip).
#
# Also the two closed-geometry helpers that are not buckets -- buildHullVolume() (the
# low-poly water-exclusion envelope) and keelVolume() (the keel/skeg appendage volume
# BoatModel folds into the displaced volume) -- and the two exports the wheelhouse and deck
# gear build on: foredeckY() and houseHalfWidth(). KEEL and PALETTE are read by the boat's
# dimensions and materials too.
#
# Fidelity notes, all copied deliberately from the JS:
#   - every primitive (gridSurface, loft, fanCap, slab, box, rod, tube, auxVertices,
#     orientTowards) comes from the ported GeoKit, so vertex emission order, index winding
#     and uv conventions are already the JS ones. Nothing here re-derives geometry.
#   - `orientOutward` and `fixUnusedNormals` exist only in this file, ported as they stand
#     (orientOutward weights every vertex normal against a position-dependent outward
#     direction and re-computes the normals after flipping the winding; fixUnusedNormals
#     patches the normals of vertices no triangle references -- the transom's inner
#     corners).
#   - the grid uvFn callbacks and the slab/auxVertices callbacks are JS arrows that take
#     fewer parameters than the callback site passes; they accept the extra ones here.
#   - `Math.hypot` is the reimplemented js_hypot: it feeds fixUnusedNormals' threshold and
#     the rubrail's rail-line normal. `Math.pow` is math.pow -- whole-number exponents on
#     values in [ 0, 1 ] here, so any libm last-ulp difference is far below the float32
#     vertex spacing and no vertex moves.
#
# Pure Python 3, standard library only (no numpy, no bpy).

import math

from mathutil import Vector3, js_hypot
from geometry import BufferGeometry, Float32BufferAttribute
from geokit import (
	auxVertices, box, fanCap, gridSurface, loft, orientTowards, rod, slab, tube,
)
from hull_lines import lerp, sstep


# Colors (sRGB hex; GeoKit converts to linear)
PALETTE = {
	'gelcoat': 0xf1eee6,
	'lining': 0xe9e6dc,
	'deck': 0xe2ddcf,
	'antifouling': 0x7a1d15,
	'stainless': 0xd0d3d6,
}


def V( x, y, z ):

	return Vector3( x, y, z )


def buildHull( kit, L ):
	"""Add the shell, keel, lining, decks, gunwale caps and rubrail to the kit."""

	buildShell( kit, L )
	buildKeel( kit, L )
	buildLining( kit, L )
	buildDecks( kit, L )
	buildGunwale( kit, L )
	buildRubrail( kit, L )


# ------------------------------------------------------------------ outer shell + transom

def buildShell( kit, L ):

	ts = L.stationParams( 60 )
	rows = [ L.station( t, 1 ) for t in ts ]
	uvFn = lambda p, *rest: [ p.z, p.y ]
	kit.add( 'hull', gridSurface( rows, { 'uvFn': uvFn } ) )
	mirrored = [ [ V( - p.x, p.y, p.z ) for p in r ] for r in rows ]
	kit.add( 'hull', gridSurface( mirrored, { 'flip': True, 'uvFn': uvFn } ) )

	# transom: strips between the port and starboard halves of the t = 0 section
	sec = rows[ 0 ]
	pos = []; idx = []; uvs = []

	for p in sec:

		pos.extend( ( p.x, p.y, p.z, - p.x, p.y, p.z ) )
		uvs.extend( ( p.x, p.y, - p.x, p.y ) )

	for j in range( len( sec ) - 1 ):

		a = 2 * j; b = 2 * j + 1; c = 2 * j + 2; d = 2 * j + 3

		if sec[ j ].x > 1e-5:
			idx.extend( ( a, b, c ) )

		idx.extend( ( b, d, c ) )

	g = BufferGeometry()
	g.setAttribute( 'position', Float32BufferAttribute( pos, 3 ) )
	g.setAttribute( 'uv', Float32BufferAttribute( uvs, 2 ) )
	g.setIndex( idx )
	orientTowards( g, V( 0, 0, - 1 ) )
	g.computeVertexNormals()
	fixUnusedNormals( g, V( 0, 0, - 1 ) )
	kit.add( 'hull', g )


# Closed low-poly hull envelope: both shell halves, transom and a flat lid at the sheer.
def buildHullVolume( L ):

	ts = L.stationParams( 28 )
	rows = []

	for t in ts:

		sect = L.station( t, 1 )
		rows.append( [ p for j, p in enumerate( sect ) if j % 2 == 0 or j == len( sect ) - 1 ] )

	nj = len( rows[ 0 ] )
	pos = []; idx = []

	def vert( p ):

		pos.extend( ( p.x, p.y, p.z ) )
		return len( pos ) // 3 - 1

	port = [ [ vert( p ) for p in r ] for r in rows ]
	star = [ [ vert( V( - p.x, p.y, p.z ) ) for p in r ] for r in rows ]

	for i in range( len( rows ) - 1 ):

		for j in range( nj - 1 ):

			a = port[ i ][ j ]; b = port[ i + 1 ][ j ]; c = port[ i + 1 ][ j + 1 ]; d = port[ i ][ j + 1 ]
			idx.extend( ( a, d, b, d, c, b ) )
			a2 = star[ i ][ j ]; b2 = star[ i + 1 ][ j ]; c2 = star[ i + 1 ][ j + 1 ]; d2 = star[ i ][ j + 1 ]
			idx.extend( ( a2, b2, d2, b2, c2, d2 ) )

	# transom
	for j in range( nj - 1 ):
		idx.extend( ( port[ 0 ][ j ], star[ 0 ][ j ], port[ 0 ][ j + 1 ], star[ 0 ][ j ], star[ 0 ][ j + 1 ], port[ 0 ][ j + 1 ] ) )

	# lid: strip between the port and starboard sheer lines
	top = nj - 1

	for i in range( len( rows ) - 1 ):
		idx.extend( ( port[ i ][ top ], star[ i ][ top ], port[ i + 1 ][ top ], star[ i ][ top ], star[ i + 1 ][ top ], port[ i + 1 ][ top ] ) )

	# drop zero-area triangles where the halves meet (keel line, stem head)
	def same( i, k ):

		return abs( pos[ 3 * i ] - pos[ 3 * k ] ) + abs( pos[ 3 * i + 1 ] - pos[ 3 * k + 1 ] ) + abs( pos[ 3 * i + 2 ] - pos[ 3 * k + 2 ] ) < 1e-7

	clean = []

	for i in range( 0, len( idx ), 3 ):

		a = idx[ i ]; b = idx[ i + 1 ]; c = idx[ i + 2 ]

		if not same( a, b ) and not same( b, c ) and not same( a, c ):
			clean.extend( ( a, b, c ) )

	g = BufferGeometry()
	g.setAttribute( 'position', Float32BufferAttribute( pos, 3 ) )
	g.setIndex( clean )
	g.computeVertexNormals()
	g.computeBoundingBox()
	g.computeBoundingSphere()
	return g


# ------------------------------------------------------------------ keel, shoe, shaft log

# Keel bottom profile (z, y): shallow at the forefoot, deepest at the prop aperture.
KEEL_PROFILE = [ [ 3.74, - 0.093 ], [ 3.3, - 0.2 ], [ 3.0, - 0.265 ], [ 1.5, - 0.5 ], [ 0, - 0.62 ], [ - 1.5, - 0.7 ], [ - 3.05, - 0.77 ] ]


def keelBottomAt( z ):
	"""Keel bottom height (boat frame) at longitudinal position z (uniform Catmull-Rom)."""

	P = KEEL_PROFILE

	if z >= P[ 0 ][ 0 ]:
		return P[ 0 ][ 1 ]

	if z <= P[ len( P ) - 1 ][ 0 ]:
		return P[ len( P ) - 1 ][ 1 ]

	i = 0

	while i < len( P ) - 2 and z < P[ i + 1 ][ 0 ]:
		i += 1

	p0 = P[ max( 0, i - 1 ) ]; p1 = P[ i ]; p2 = P[ i + 1 ]; p3 = P[ min( len( P ) - 1, i + 2 ) ]
	f = ( p1[ 0 ] - z ) / ( p1[ 0 ] - p2[ 0 ] )
	# uniform Catmull-Rom on y
	f2 = f * f; f3 = f2 * f
	return 0.5 * ( 2 * p1[ 1 ] + ( - p0[ 1 ] + p2[ 1 ] ) * f + ( 2 * p0[ 1 ] - 5 * p1[ 1 ] + 4 * p2[ 1 ] - p3[ 1 ] ) * f2 + ( - p0[ 1 ] + 3 * p1[ 1 ] - 3 * p2[ 1 ] + p3[ 1 ] ) * f3 )


KEEL = { 'zFront': 3.74, 'zAft': - 3.05, 'shoeAft': - 3.68, 'bottom': - 0.77, 'shoeTop': - 0.74 }


def buildKeel( kit, L ):

	N = 32
	profiles = []

	for i in range( N + 1 ):

		f = i / N
		z = lerp( KEEL[ 'zAft' ], KEEL[ 'zFront' ], math.pow( f, 0.9 ) )
		yB = keelBottomAt( z )
		canoe = - L.draftAt( z )
		yTop = max( canoe + 0.06, yB + 0.02 )
		fwd = sstep( 2.6, KEEL[ 'zFront' ], z )
		wt = lerp( 0.065, 0.012, fwd )
		wb = lerp( 0.042, 0.008, fwd )
		mid = lerp( yTop, yB, 0.55 )
		half = [
			[ wt, yTop ], [ lerp( wt, wb, 0.6 ), mid ], [ wb, yB + 0.035 ], [ wb * 0.75, yB + 0.012 ], [ wb * 0.35, yB + 0.002 ],
		]
		prof = []

		for h in half:
			prof.append( V( h[ 0 ], h[ 1 ], z ) )

		prof.append( V( 0, yB, z ) )

		for k in range( len( half ) - 1, - 1, - 1 ):
			prof.append( V( - half[ k ][ 0 ], half[ k ][ 1 ], z ) )

		profiles.append( prof )

	g = loft( profiles )
	orientOutward( g, lambda p: V( p.x, 0, 0 ), V( 0, - 1, 0 ) )
	kit.add( 'hull', g )
	kit.add( 'hull', fanCap( profiles[ 0 ], V( 0, 0, - 1 ) ) )
	kit.add( 'hull', fanCap( profiles[ N ], V( 0, 0, 1 ) ) )

	# shoe under the prop aperture carrying the rudder heel
	shoeLen = KEEL[ 'zAft' ] - KEEL[ 'shoeAft' ]
	shoe = box( 0.075, KEEL[ 'shoeTop' ] - KEEL[ 'bottom' ], shoeLen )
	shoe.translate( 0, ( KEEL[ 'shoeTop' ] + KEEL[ 'bottom' ] ) / 2, ( KEEL[ 'zAft' ] + KEEL[ 'shoeAft' ] ) / 2 )
	kit.add( 'hull', shoe )

	# heel bearing and shaft
	kit.add( 'fittings', rod( V( 0, KEEL[ 'shoeTop' ], - 3.58 ), V( 0, KEEL[ 'shoeTop' ] + 0.03, - 3.58 ), 0.03, 10 ), { 'color': 0xb0764a, 'rough': 0.4, 'metal': 1 } )
	kit.add( 'fittings', rod( V( 0, - 0.53, KEEL[ 'zAft' ] + 0.02 ), V( 0, - 0.53, - 3.27 ), 0.024, 10 ), { 'color': 0xc9ccd0, 'rough': 0.25, 'metal': 1 } )
	# stern tube boss where the shaft leaves the keel
	kit.add( 'hull', rod( V( 0, - 0.53, KEEL[ 'zAft' ] + 0.06 ), V( 0, - 0.53, KEEL[ 'zAft' ] - 0.03 ), 0.05, 12, 0.04 ) )


# Volume of the keel appendage and shoe below the canoe body (m^3).
def keelVolume( L ):

	N = 240
	dz = ( KEEL[ 'zFront' ] - KEEL[ 'zAft' ] ) / N
	v = 0

	for i in range( N ):

		z = KEEL[ 'zAft' ] + ( i + 0.5 ) * dz
		h = max( 0, - L.draftAt( z ) - keelBottomAt( z ) )
		fwd = sstep( 2.6, KEEL[ 'zFront' ], z )
		v += ( lerp( 0.065, 0.012, fwd ) + lerp( 0.042, 0.008, fwd ) ) * h * dz

	return v + 0.075 * ( KEEL[ 'shoeTop' ] - KEEL[ 'bottom' ] ) * ( KEEL[ 'zAft' ] - KEEL[ 'shoeAft' ] )


# Vertices not referenced by any triangle get a sane normal.
def fixUnusedNormals( g, n ):

	nrm = g.attributes[ 'normal' ]

	for i in range( nrm.count ):

		if js_hypot( nrm.getX( i ), nrm.getY( i ), nrm.getZ( i ) ) < 0.5:
			nrm.setXYZ( i, n.x, n.y, n.z )


# Flip a geometry so normals point along outward(p) on average.
def orientOutward( g, outwardFn, fallback ):

	p = g.attributes[ 'position' ]; n = g.attributes[ 'normal' ]
	dot = 0
	a = V( 0, 0, 0 ); b = V( 0, 0, 0 )

	for i in range( p.count ):

		a.fromBufferAttribute( p, i )
		b.fromBufferAttribute( n, i )
		o = outwardFn( a )
		dot += b.dot( o ) if o.lengthSq() > 1e-10 else b.dot( fallback )

	if dot < 0:

		idx = list( g.index.array )

		for i in range( 0, len( idx ), 3 ):

			t = idx[ i + 1 ]; idx[ i + 1 ] = idx[ i + 2 ]; idx[ i + 2 ] = t

		g.setIndex( idx )
		g.computeVertexNormals()

	return g


# ------------------------------------------------------------------ inner lining (bulwarks)

def innerX( L, t, y ):
	"""Inner face half-breadth: the outer half-breadth less the shell thickness."""

	return L.halfBreadth( t, y ) - L.shell


def buildLining( kit, L ):

	tA = L.shell / L.length
	tF = L.tAtSheerZ( L.houseFront )
	NS = 40; NY = 7
	rows = []

	for i in range( NS + 1 ):

		t = lerp( tA, tF, i / NS )
		z = L.sheerZ( t )
		yTop = L.sheerY( t )
		row = []

		for k in range( NY + 1 ):

			y = lerp( L.deckY, yTop, k / NY )
			row.append( V( innerX( L, t, y ), y, z ) )

		rows.append( row )

	uvFn = lambda p, *rest: [ p.z, p.y ]
	# port lining faces -x (inboard)
	port = gridSurface( rows, { 'uvFn': uvFn, 'flip': True } )
	star = gridSurface( [ [ V( - p.x, p.y, p.z ) for p in r ] for r in rows ], { 'uvFn': uvFn } )
	kit.add( 'gelcoat', port, { 'color': PALETTE[ 'lining' ], 'rough': 0.4 } )
	kit.add( 'gelcoat', star, { 'color': PALETTE[ 'lining' ], 'rough': 0.4 } )

	# inner face of the transom
	zT = L.zAft + L.shell
	pos = []; uvs = []; idx = []

	for k in range( NY + 1 ):

		y = lerp( L.deckY, L.sheerY( tA ), k / NY )
		x = innerX( L, tA, y )
		pos.extend( ( x, y, zT, - x, y, zT ) )
		uvs.extend( ( x, y, - x, y ) )

	for k in range( NY ):

		a = 2 * k; b = 2 * k + 1; c = 2 * k + 2; d = 2 * k + 3
		idx.extend( ( a, c, b, b, c, d ) )

	g = BufferGeometry()
	g.setAttribute( 'position', Float32BufferAttribute( pos, 3 ) )
	g.setAttribute( 'uv', Float32BufferAttribute( uvs, 2 ) )
	g.setIndex( idx )
	orientTowards( g, V( 0, 0, 1 ) )
	g.computeVertexNormals()
	kit.add( 'gelcoat', g, { 'color': PALETTE[ 'lining' ], 'rough': 0.4 } )


# ------------------------------------------------------------------ decks

def foredeckY( L, t, x ):
	"""Cambered foredeck height at station t and half-breadth x."""

	xe = max( 1e-3, L.sheerX( t ) - L.shell )
	f = min( 1, abs( x ) / xe )
	return L.sheerY( t ) + 0.06 * ( 1 - f * f )


def buildDecks( kit, L ):

	tA = L.shell / L.length
	tF = L.tAtSheerZ( L.houseFront )
	grip = { 'color': PALETTE[ 'deck' ], 'rough': 0.5, 'pattern': 1 }
	uvFn = lambda p, *rest: [ p.x, p.z ]

	# cockpit / wheelhouse sole (y = deckY), edges follow the lining
	NS = 40; NX = 8
	sole = []

	for i in range( NS + 1 ):

		t = lerp( tA, tF, i / NS )
		z = L.sheerZ( t )
		xe = innerX( L, t, L.deckY )
		row = []

		for k in range( NX + 1 ):
			row.append( V( lerp( xe, - xe, k / NX ), L.deckY, z ) )

		sole.append( row )

	gs = gridSurface( sole, { 'uvFn': uvFn } )
	orientTowards( gs, V( 0, 1, 0 ) )
	gs.computeVertexNormals()
	kit.add( 'gelcoat', gs, grip )

	# cambered foredeck from the wheelhouse front to the stem head
	NF = 30; NFX = 12
	tDeckEnd = 1

	while L.sheerX( tDeckEnd ) - L.shell < 0.012:
		tDeckEnd -= 0.0005

	fore = []

	for i in range( NF + 1 ):

		t = lerp( tF, tDeckEnd, 1 - math.pow( 1 - i / NF, 1.3 ) )
		z = L.sheerZ( t )
		xe = max( 0.012, L.sheerX( t ) - L.shell )
		row = []

		for k in range( NFX + 1 ):

			x = lerp( xe, - xe, k / NFX )
			row.append( V( x, foredeckY( L, t, x ), z ) )

		fore.append( row )

	gf = gridSurface( fore, { 'uvFn': uvFn } )
	orientTowards( gf, V( 0, 1, 0 ) )
	gf.computeVertexNormals()
	kit.add( 'gelcoat', gf, grip )

	# side decks (washboards) alongside the wheelhouse
	for s in [ 1, - 1 ]:

		outline = []
		NZ = 12
		z0 = L.houseBack; z1 = L.houseFront

		for i in range( NZ + 1 ):

			z = lerp( z0, z1, i / NZ )
			outline.append( [ z, houseHalfWidth( L, z ) - 0.045 ] )

		for i in range( NZ, - 1, - 1 ):

			z = lerp( z0, z1, i / NZ )
			outline.append( [ z, L.sheerX( L.tAtSheerZ( z ) ) - L.shell + 0.01 ] )

		def mapFn( u, v, side ):

			t = L.tAtSheerZ( u )
			return V( s * v, L.sheerY( t ) - ( 0.04 if side else 0 ), u )

		g = slab( outline, [], mapFn )
		auxVertices( g, lambda p, i: [ 0.5, 0, 1 if p.y > L.sheerY( L.tAtSheerZ( p.z ) ) - 0.02 else 0, 0 ] )
		kit.add( 'gelcoat', g, { 'color': PALETTE[ 'deck' ] } )

	# deck hatch frame
	hy = L.deckY + 0.008

	for w, d, x, z in [ [ 0.74, 0.03, 0, - 1.25 ], [ 0.74, 0.03, 0, - 1.95 ], [ 0.03, 0.73, 0.355, - 1.6 ], [ 0.03, 0.73, - 0.355, - 1.6 ] ]:

		b = box( w, 0.016, d )
		b.translate( x, hy, z )
		kit.add( 'gelcoat', b, { 'color': 0xd6d1c4, 'rough': 0.45 } )

	# flush hatch lifting ring
	kit.add( 'fittings', rod( V( - 0.05, L.deckY + 0.004, - 1.45 ), V( 0.05, L.deckY + 0.004, - 1.45 ), 0.006, 6 ), { 'color': PALETTE[ 'stainless' ], 'rough': 0.3, 'metal': 1 } )


def houseHalfWidth( L, z ):
	"""Outer half-width of the wheelhouse side wall at longitudinal position z."""

	return L.sheerX( L.tAtSheerZ( z ) ) - 0.2


# ------------------------------------------------------------------ gunwale caps (wood)

CAP_H = 0.045


def capProfile( xi, xo, y0, z, sign ):
	"""The gunwale cap's 8-point cross-section, mirrored by `sign` and clamped at x = 0."""

	xm = ( xi + xo ) / 2
	pts = [
		[ xi, y0 - 0.012 ], [ xi, y0 + CAP_H - 0.012 ], [ xi + 0.01, y0 + CAP_H - 0.002 ], [ xm, y0 + CAP_H + 0.004 ],
		[ xo - 0.01, y0 + CAP_H - 0.002 ], [ xo, y0 + CAP_H - 0.012 ], [ xo, y0 - 0.026 ], [ xo - 0.018, y0 - 0.03 ],
	]
	# float() matters: JS Math.max returns a double, so sign * Math.max( 0, x ) is -0 for
	# sign = -1 at x <= 0, and that -0 reaches the position attribute. Python's max returns
	# an int 0 there and int has no signed zero.
	return [ V( sign * float( max( 0, p[ 0 ] ) ), p[ 1 ], z ) for p in pts ]


def buildGunwale( kit, L ):

	tStart = ( L.shell + 0.03 ) / L.length
	N = 60

	for s in [ 1, - 1 ]:

		profiles = []

		for i in range( N + 1 ):

			t = lerp( tStart, 1, 1 - math.pow( 1 - i / N, 1.25 ) )
			xs = L.sheerX( t )
			z = L.sheerZ( t )
			xo = xs + 0.022

			if i == N:

				xo = 0; z += 0.03

			profiles.append( capProfile( max( 0, xs - L.shell - 0.03 ), xo, L.sheerY( t ), z, s ) )

		g = loft( profiles )
		orientOutward( g, lambda p: V( p.x - s * ( L.sheerX( L.tAtSheerZ( p.z ) ) - 0.03 ), p.y - L.sheerY( L.tAtSheerZ( p.z ) ) - 0.01, 0 ), V( 0, 1, 0 ) )
		woodUV( g, profiles )
		kit.add( 'wood', g, { 'rough': 0.32 } )
		kit.add( 'wood', fanCap( profiles[ 0 ], V( 0, 0, - 1 ) ), { 'rough': 0.32 } )

	# transom cap
	y0 = L.sheerY( 0 )
	xo = L.sheerX( 0 ) + 0.022
	zi = L.zAft + L.shell + 0.03; zo = L.zAft - 0.022
	prof = lambda x: [ V( x, y0 + p.y, zi - p.x ) for p in capProfile( 0, zi - zo, 0, 0, 1 ) ]
	tprofiles = []

	for i in range( 9 ):
		tprofiles.append( prof( lerp( - xo, xo, i / 8 ) ) )

	tg = loft( tprofiles )
	orientOutward( tg, lambda p: V( 0, p.y - y0 - 0.01, p.z - ( zi + zo ) / 2 ), V( 0, 1, 0 ) )
	woodUV( tg, tprofiles )
	kit.add( 'wood', tg, { 'rough': 0.32 } )
	kit.add( 'wood', fanCap( tprofiles[ 0 ], V( - 1, 0, 0 ) ), { 'rough': 0.32 } )
	kit.add( 'wood', fanCap( tprofiles[ 8 ], V( 1, 0, 0 ) ), { 'rough': 0.32 } )


# u along the sweep (meters), v around the profile (meters) so grain runs lengthwise.
def woodUV( g, profiles ):

	nj = len( profiles[ 0 ] )
	uv = g.attributes[ 'uv' ]
	u = 0

	for i in range( len( profiles ) ):

		if i > 0:
			u += profiles[ i ][ 3 ].distanceTo( profiles[ i - 1 ][ 3 ] )

		v = 0

		for j in range( nj ):

			if j > 0:
				v += profiles[ i ][ j ].distanceTo( profiles[ i ][ j - 1 ] )

			uv.setXY( i * nj + j, u, v )


# ------------------------------------------------------------------ rubrail (wood + stainless strip)

def buildRubrail( kit, L ):

	N = 50
	ANG = [ a * math.pi / 180 for a in [ - 90, - 60, - 30, 0, 30, 60, 90 ] ]

	for s in [ 1, - 1 ]:

		centers = []

		for i in range( N + 1 ):

			t = lerp( 0.0, 0.992, 1 - math.pow( 1 - i / N, 1.25 ) )
			y = L.sheerY( t ) - 0.07
			x = L.halfBreadth( t, y )
			centers.append( V( x, y, L.zOnStation( t, y ) ) )

		profiles = []
		strip = []

		for i in range( N + 1 ):

			c = centers[ i ]
			a = centers[ max( 0, i - 1 ) ]; b = centers[ min( N, i + 1 ) ]
			# horizontal outward normal of the rail line
			dz = b.z - a.z; dx = b.x - a.x
			ln = js_hypot( dx, dz ) or 1
			nx = dz / ln; nz = - dx / ln
			prof = []

			for ang in ANG:

				o = 0.036 * math.cos( ang ) - 0.006
				prof.append( V( s * ( c.x + nx * o ), c.y + 0.03 * math.sin( ang ), c.z + nz * o ) )

			profiles.append( prof )
			strip.append( V( s * ( c.x + nx * 0.034 ), c.y, c.z + nz * 0.034 ) )

		g = loft( profiles )
		orientOutward( g, lambda p: V( s, 0, 0 ), V( s, 0, 0 ) )
		woodUV( g, profiles )
		kit.add( 'wood', g, { 'rough': 0.35 } )
		kit.add( 'wood', fanCap( profiles[ 0 ], V( 0, 0, - 1 ) ), { 'rough': 0.35 } )
		kit.add( 'wood', fanCap( profiles[ N ], V( 0, 0, 1 ) ), { 'rough': 0.35 } )
		kit.add( 'fittings', tube( strip, 0.0065, 64, 4 ), { 'color': PALETTE[ 'stainless' ], 'rough': 0.22, 'metal': 1 } )

	# across the transom
	y = L.sheerY( 0 ) - 0.07
	xo = L.sheerX( 0 ) + 0.015
	tprof = []

	for i in range( 7 ):

		x = lerp( - xo, xo, i / 6 )
		tprof.append( [ V( x, y + 0.03 * math.sin( ang ), L.zAft - ( 0.036 * math.cos( ang ) - 0.006 ) ) for ang in ANG ] )

	tg = loft( tprof )
	orientOutward( tg, lambda p: V( 0, 0, p.z - L.zAft + 0.001 ), V( 0, 0, - 1 ) )
	woodUV( tg, tprof )
	kit.add( 'wood', tg, { 'rough': 0.35 } )
	kit.add( 'wood', fanCap( tprof[ 0 ], V( - 1, 0, 0 ) ), { 'rough': 0.35 } )
	kit.add( 'wood', fanCap( tprof[ 6 ], V( 1, 0, 0 ) ), { 'rough': 0.35 } )
