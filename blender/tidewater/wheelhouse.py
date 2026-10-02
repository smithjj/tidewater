# Wheelhouse builder -- pure-Python port of src/world/boat/Wheelhouse.js.
#
# The biggest builder of the boat: the side walls (with their window frames, rubber
# gaskets and pop rivets), the raked windshield and its wipers, the roof, the console with
# the instrument panel, gauges, switch panel, helm, compass, electronics and overhead VHF,
# the helm seat and companion bench, the lived-in cabin interior (teak sole, headliner,
# rod rack, oilskins, extinguisher, charts and mug) and the roof gear (mast, radome, open
# array radar, spreader lights, VHF whips, spotlight, horn, buoy, floodlights, life ring).
#
# Also the three animated geometries: the wheel, the throttle lever and the radar array.
#
# Fidelity notes, all copied deliberately from the JS:
#   - `slab`, `loft`, `fanCap`, `gridSurface`, `rod`, `lathe`, ... come from the ported
#     GeoKit, so the vertex emission order, index winding and uv conventions are already
#     the JS ones. Nothing here re-derives geometry.
#   - the local helpers offsetPoly/area2/quad, the roof region's private orientOutwardFn
#     and metricUV, and the mug's orientTowardsAxis all exist only in this file and are
#     ported as they stand (orientOutwardFn is *not* GeoKit's orientTowards: it weights
#     the vertex normals by a position-dependent direction and re-computes normals after
#     flipping; orientTowardsAxis flips the winding and negates the normals instead).
#   - `%` on floats is JS remainder (math.fmod), not Python's floored modulo: the life
#     ring's paint pattern depends on it.
#   - `Math.hypot` is the reimplemented js_hypot and `Math.round` is js_round, because
#     offsetPoly and the rivet spacing feed vertex attributes.
#
# Pure Python 3, standard library only (no numpy, no bpy).

import math

from mathutil import Color, Float32Array, Matrix4, Vector2, Vector3, js_hypot, js_round
from geometry import BufferGeometry, CircleGeometry, Float32BufferAttribute, PlaneGeometry
from geokit import (
	alignY, auxVertices, box, cylinder, fanCap, lathe, loft, mat4, mergePrepared,
	paintVertices, prepare, rod, roundedBox, slab, sphere, torus, tube,
)
from hull import PALETTE, foredeckY, houseHalfWidth
from hull_lines import lerp
from deckgear import buoyGeometry


def V( x, y, z ):
	return Vector3( x, y, z )


# JS `%` (remainder taking the sign of the dividend), not Python's floored modulo.
# The life ring's paint pattern depends on it.
def fmod( a, b ):

	return math.fmod( a, b )


# Wheelhouse dimensions (boat frame)
HOUSE = {
	'wallT': 0.045,
	'wsBottomY': 1.48, # windshield base
	'wsTopY': 2.24,
	'roofUnderY': 2.31,
	'roofZ0': - 0.95,
	'roofZ1': 1.34,
	'winBottom': 1.56,
	'winTop': 2.12,
	'dash': { 'zFace': 0.98, 'yKnee': 1.08, 'zTop': 1.16, 'yTop': 1.4, 'zBack': 1.41, 'halfW': 1.1 },
	'helmX': - 0.55,
	'seatZ': 0.22,
}

STAINLESS = { 'color': PALETTE[ 'stainless' ], 'rough': 0.22, 'metal': 1 }
BLACK_PLASTIC = { 'color': 0x1a1b1d, 'rough': 0.55, 'metal': 0 }
WHITE_PAINT = { 'color': 0xf1f0eb, 'rough': 0.35, 'metal': 0 }
FRAME = { 'color': 0xa4a8ab, 'rough': 0.55, 'metal': 1 } # weathered, oxidised aluminium
VINYL = { 'color': 0x1f2a3a, 'rough': 0.6, 'metal': 0 }


def wsZ( L, y ):

	return L.houseFront - ( y - HOUSE[ 'wsBottomY' ] ) * ( 0.25 / 0.76 )


def wallX( L, z, y ):

	return houseHalfWidth( L, z ) - max( 0, y - 1.15 ) * 0.04


def roofTopY( x ):

	return 2.375 + 0.035 * ( 1 - ( x / 1.35 ) ** 2 )


# Offset a convex polygon (list of [u, v]) outward by d.
def offsetPoly( poly, d ):

	n = len( poly )
	ccw = area2( poly ) > 0
	lines = []

	for i in range( n ):

		a = poly[ i ]; b = poly[ ( i + 1 ) % n ]
		ex = b[ 0 ] - a[ 0 ]; ey = b[ 1 ] - a[ 1 ]
		l = js_hypot( ex, ey )
		nx = ey / l; ny = - ex / l # right-hand normal (outward for CCW)

		if not ccw:

			nx = - nx; ny = - ny

		lines.append( [ a[ 0 ] + nx * d, a[ 1 ] + ny * d, ex, ey ] )

	out = []

	for i in range( n ):

		l1 = lines[ ( i + n - 1 ) % n ]; l2 = lines[ i ]
		den = l1[ 2 ] * l2[ 3 ] - l1[ 3 ] * l2[ 2 ]
		s = ( ( l2[ 0 ] - l1[ 0 ] ) * l2[ 3 ] - ( l2[ 1 ] - l1[ 1 ] ) * l2[ 2 ] ) / den
		out.append( [ l1[ 0 ] + l1[ 2 ] * s, l1[ 1 ] + l1[ 3 ] * s ] )

	return out


def area2( poly ):

	a = 0

	for i in range( len( poly ) ):

		p = poly[ i ]; q = poly[ ( i + 1 ) % len( poly ) ]
		a += p[ 0 ] * q[ 1 ] - q[ 0 ] * p[ 1 ]

	return a


# Quad from four corner points (counter-clockwise seen from the front) with 0..1 or metric UVs.
def quad( a, b, c, d, uvs = None ):

	if uvs is None:
		uvs = [ 0, 0, 1, 0, 1, 1, 0, 1 ]

	g = BufferGeometry()
	g.setAttribute( 'position', Float32BufferAttribute( [ a.x, a.y, a.z, b.x, b.y, b.z, c.x, c.y, c.z, d.x, d.y, d.z ], 3 ) )
	g.setAttribute( 'uv', Float32BufferAttribute( uvs, 2 ) )
	g.setIndex( [ 0, 1, 2, 0, 2, 3 ] )
	g.computeVertexNormals()
	return g


def buildWheelhouse( kit, L, parts ):

	buildWalls( kit, L )
	buildWindshield( kit, L )
	buildRoof( kit, L )
	buildConsole( kit, L, parts )
	buildSeating( kit, L )
	buildCabinDetail( kit, L )
	buildRoofGear( kit, L, parts )


# ------------------------------------------------------------------ side walls

def sideWindowHoles( L ):

	y0 = HOUSE[ 'winBottom' ]; y1 = HOUSE[ 'winTop' ]

	def front( y ):
		return wsZ( L, y ) - 0.1

	return [
		[ [ 0.3, y0 ], [ 0.78, y0 ], [ 0.78, y1 ], [ 0.3, y1 ] ],
		[ [ 0.84, y0 ], [ front( y0 ), y0 ], [ front( y1 ), y1 ], [ 0.84, y1 ] ],
	]


def buildWalls( kit, L ):

	wallT = HOUSE[ 'wallT' ]; roofUnderY = HOUSE[ 'roofUnderY' ]
	z0 = L.houseBack; z1 = L.houseFront
	outline = []
	NB = 8

	for i in range( NB + 1 ):

		z = lerp( z0, z1, i / NB )
		outline.append( [ z, L.sheerY( L.tAtSheerZ( z ) ) ] )

	outline.append( [ z1, HOUSE[ 'wsBottomY' ] ] )
	outline.append( [ wsZ( L, roofUnderY ), roofUnderY ] )
	outline.append( [ z0, roofUnderY ] )
	holes = sideWindowHoles( L )

	for s in [ 1, - 1 ]:

		def map_( u, v, side ):
			return V( s * ( wallX( L, u, v ) - side * wallT ), v, u )

		# the inner face is painted panelling (seams, screws, grime); the outside stays glossy gelcoat
		wall = slab( outline, holes, map_ )
		auxVertices( wall, lambda p, i: [ 0.5, 0, 2, 0 ] if abs( p.x ) < wallX( L, p.z, p.y ) - wallT * 0.5 else [ 0.3, 0, 0, 0 ] )
		kit.add( 'gelcoat', wall, { 'color': PALETTE[ 'gelcoat' ] } )

		for h in holes:

			# aluminum frame ring through the wall, proud of both faces
			outer = offsetPoly( h, 0.024 )

			def fmap( u, v, side ):
				return V( s * ( wallX( L, u, v ) + 0.008 - side * ( wallT + 0.016 ) ), v, u )

			def gmap( u, v, side ):
				return V( s * ( wallX( L, u, v ) + 0.004 - side * ( wallT + 0.008 ) ), v, u )

			kit.add( 'fittings', slab( outer, [ offsetPoly( h, - 0.006 ) ], fmap ), FRAME )

			# (no glass in the openings: the windows are left open)

			# black rubber gasket lining the opening, inside the frame ring
			kit.add( 'fittings', slab( offsetPoly( h, - 0.005 ), [ offsetPoly( h, - 0.014 ) ], gmap ), { 'color': 0x0e0f10, 'rough': 0.8 } )

			# pop rivets around the frame on the inside face
			ring = offsetPoly( h, 0.011 )

			for i in range( len( ring ) ):

				a = ring[ i ]; b = ring[ ( i + 1 ) % len( ring ) ]
				size = js_hypot( b[ 0 ] - a[ 0 ], b[ 1 ] - a[ 1 ] )
				n = max( 2, js_round( size / 0.075 ) )

				for k in range( n ):

					u = lerp( a[ 0 ], b[ 0 ], ( k + 0.5 ) / n )
					v = lerp( a[ 1 ], b[ 1 ], ( k + 0.5 ) / n )
					xr = s * ( wallX( L, u, v ) + 0.008 - ( wallT + 0.016 ) - 0.001 )
					rv = cylinder( 0.0045, 0.0045, 0.003, 6 )
					rv.applyMatrix4( mat4( xr, v, u, 0, 0, math.pi / 2 ) )
					kit.add( 'fittings', rv, { 'color': 0xc9ccd0, 'rough': 0.35, 'metal': 1 } )

		# sliding-window latch
		lz = 0.81; ly = 1.84
		lb = box( 0.02, 0.06, 0.025 )
		lb.translate( s * ( wallX( L, lz, ly ) - wallT - 0.012 ), ly, lz )
		kit.add( 'fittings', lb, BLACK_PLASTIC )

	# house front below the windshield (coaming)
	tF = L.tAtSheerZ( L.houseFront )
	hw = houseHalfWidth( L, L.houseFront )
	cOutline = []

	for i in range( 11 ):

		x = lerp( - hw, hw, i / 10 )
		cOutline.append( [ x, foredeckY( L, tF, x ) - 0.012 ] )

	cOutline.append( [ hw, HOUSE[ 'wsBottomY' ] ] )
	cOutline.append( [ - hw, HOUSE[ 'wsBottomY' ] ] )
	coaming = slab( cOutline, [], lambda u, v, side: V( u, v, L.houseFront - side * 0.04 ) )
	auxVertices( coaming, lambda p, i: [ 0.5, 0, 2, 0 ] if p.z < L.houseFront - 0.02 else [ 0.3, 0, 0, 0 ] )
	kit.add( 'gelcoat', coaming, { 'color': PALETTE[ 'gelcoat' ] } )


# ------------------------------------------------------------------ windshield

def buildWindshield( kit, L ):

	wsBottomY = HOUSE[ 'wsBottomY' ]; roofUnderY = HOUSE[ 'roofUnderY' ]
	rake = Vector3( 0, 0.76, - 0.25 ).normalize()
	normal = Vector3( 0, 0.25, 0.76 ).normalize()
	base = V( 0, wsBottomY, L.houseFront )
	vTop = ( roofUnderY - wsBottomY ) / rake.y

	def at( u, v ):
		p = base.clone().addScaledVector( rake, v )
		p.x = u
		return p

	def halfAt( v ):

		p = at( 0, v )
		return wallX( L, p.z, p.y )

	xb = halfAt( 0 ); xt = halfAt( vTop )
	outline = [ [ - xb, 0 ], [ xb, 0 ], [ xt, vTop ], [ - xt, vTop ] ]
	v0 = 0.07; v1 = 0.75
	holes = [
		[ [ - 0.36, v0 ], [ 0.36, v0 ], [ 0.36, v1 ], [ - 0.36, v1 ] ],
		[ [ 0.42, v0 ], [ halfAt( v0 ) - 0.09, v0 ], [ halfAt( v1 ) - 0.09, v1 ], [ 0.42, v1 ] ],
		[ [ - 0.42, v0 ], [ - 0.42, v1 ], [ - halfAt( v1 ) + 0.09, v1 ], [ - halfAt( v0 ) + 0.09, v0 ] ],
	]
	T = 0.05
	kit.add( 'gelcoat', slab( outline, holes, lambda u, v, side: at( u, v ).addScaledVector( normal, - side * T ) ), { 'color': PALETTE[ 'gelcoat' ], 'rough': 0.3 } )

	for h in holes:

		outer = offsetPoly( h, 0.022 )
		kit.add( 'fittings', slab( outer, [ offsetPoly( h, - 0.006 ) ], lambda u, v, side: at( u, v ).addScaledVector( normal, 0.008 - side * ( T + 0.016 ) ) ), FRAME )
		# (no glass: open windows)

	# pantograph wipers on the centre and starboard panes
	for u, ang in [ [ 0.0, 0.25 ], [ - 0.72, 0.2 ] ]:

		pivot = at( u, v0 + 0.03 ).addScaledVector( normal, 0.012 )
		dir = rake.clone().applyAxisAngle( normal, ang )
		blade = rod( pivot, pivot.clone().addScaledVector( dir, 0.52 ), 0.006, 5 )
		kit.add( 'fittings', blade, BLACK_PLASTIC )
		kit.add( 'fittings', rod( pivot.clone().addScaledVector( normal, - 0.01 ), pivot.clone().addScaledVector( normal, 0.012 ), 0.014, 8 ), BLACK_PLASTIC )


# ------------------------------------------------------------------ roof

def roofHalf( L, z ):

	zc = min( max( z, L.houseBack ), 1.18 )
	hw = wallX( L, zc, HOUSE[ 'roofUnderY' ] ) + 0.09
	rc = 0.22
	d = min( z - HOUSE[ 'roofZ0' ], HOUSE[ 'roofZ1' ] - z )

	if d < rc:
		hw -= rc - math.sqrt( max( 0, rc * rc - ( rc - d ) ** 2 ) )

	return hw


def buildRoof( kit, L ):

	yb = HOUSE[ 'roofUnderY' ]
	N = 22
	profiles = []
	# cluster stations near both ends for the rounded corners, plus a pair on each end wall of the
	# wheelhouse so the headliner pattern (per-vertex aux) switches inside the wall, not across the roof
	zs = []

	for i in range( N + 1 ):

		f = i / N
		g = 0.5 - 0.5 * math.cos( math.pi * f )
		zs.append( lerp( HOUSE[ 'roofZ0' ], HOUSE[ 'roofZ1' ], lerp( f, g, 0.6 ) ) )

	for zw in [ L.houseBack, L.houseFront ]:
		for dz in [ - 0.012, 0.012 ]:

			z = zw + dz

			if z > HOUSE[ 'roofZ0' ] + 0.02 and z < HOUSE[ 'roofZ1' ] - 0.02:
				zs.append( z )

	zs.sort()

	for z in zs:

		hw = roofHalf( L, z )
		# inner face of the side wall under the roof (the headliner ends there)
		wIn = wallX( L, min( max( z, L.houseBack ), L.houseFront ), yb ) - HOUSE[ 'wallT' ]
		half = [
			[ 0, yb ], [ wIn * 0.5, yb ], [ wIn - 0.01, yb ], [ wIn + 0.01, yb ], [ hw - 0.03, yb ], [ hw - 0.008, yb + 0.008 ], [ hw, yb + 0.025 ],
			[ hw - 0.004, roofTopY( hw ) - 0.012 ], [ hw - 0.02, roofTopY( hw ) ], [ hw * 0.75, roofTopY( hw * 0.75 ) ],
			[ hw * 0.5, roofTopY( hw * 0.5 ) ], [ hw * 0.25, roofTopY( hw * 0.25 ) ], [ 0, roofTopY( 0 ) ],
		]
		loop = [ V( p[ 0 ], p[ 1 ], z ) for p in half ]

		for k in range( len( half ) - 2, 0, - 1 ):
			loop.append( V( - half[ k ][ 0 ], half[ k ][ 1 ], z ) )

		profiles.append( loop )

	N2 = len( profiles ) - 1
	g = loft( profiles, { 'closed': True } )
	orientOutwardFn( g, lambda p: V( p.x, p.y - 2.345, 0 ) )
	metricUV( g, lambda p: [ p.x, p.z ] )
	# non-skid on top; the underside is headliner over the wheelhouse, gelcoat on the overhangs
	auxVertices( g, lambda p, i: roofAux( L, yb, p ) )
	kit.add( 'gelcoat', g, { 'color': PALETTE[ 'gelcoat' ] } )
	kit.add( 'gelcoat', fanCap( profiles[ 0 ], V( 0, 0, - 1 ) ), { 'color': PALETTE[ 'gelcoat' ], 'rough': 0.3 } )
	kit.add( 'gelcoat', fanCap( profiles[ N2 ], V( 0, 0, 1 ) ), { 'color': PALETTE[ 'gelcoat' ], 'rough': 0.3 } )

	# stainless grab rails along the roof edges
	for s in [ 1, - 1 ]:

		x = s * 1.1
		y = roofTopY( 1.1 ) + 0.07
		kit.add( 'fittings', rod( V( x, y, - 0.7 ), V( x, y, 0.95 ), 0.013, 8 ), STAINLESS )

		for z in [ - 0.7, - 0.12, 0.45, 0.95 ]:
			kit.add( 'fittings', rod( V( x, roofTopY( 1.1 ) - 0.01, z ), V( x, y, z ), 0.011, 6 ), STAINLESS )


def roofAux( L, yb, p ):

	if p.y > yb + 0.03:
		return [ 0.35, 0, 1, 0 ]

	inside = p.z > L.houseBack and p.z < L.houseFront and abs( p.x ) < wallX( L, min( max( p.z, L.houseBack ), L.houseFront ), yb ) - HOUSE[ 'wallT' ]
	return [ 0.35, 0, 3 if inside else 0, 0 ]


def orientOutwardFn( g, fn ):

	p = g.attributes[ 'position' ]; n = g.attributes[ 'normal' ]
	dot = 0
	a = V( 0, 0, 0 ); b = V( 0, 0, 0 )

	for i in range( p.count ):

		a.fromBufferAttribute( p, i ); b.fromBufferAttribute( n, i )
		dot += b.dot( fn( a ) )

	if dot < 0:

		idx = list( g.index.array )

		for i in range( 0, len( idx ), 3 ):

			t = idx[ i + 1 ]; idx[ i + 1 ] = idx[ i + 2 ]; idx[ i + 2 ] = t

		g.setIndex( idx )
		g.computeVertexNormals()


def metricUV( g, fn ):

	p = g.attributes[ 'position' ]
	uv = Float32Array( p.count * 2 )
	v = V( 0, 0, 0 )

	for i in range( p.count ):

		v.fromBufferAttribute( p, i )
		t = fn( v )
		uv[ i * 2 ] = t[ 0 ]; uv[ i * 2 + 1 ] = t[ 1 ]

	g.setAttribute( 'uv', Float32BufferAttribute( uv, 2 ) )


# ------------------------------------------------------------------ console, helm, instruments

# Slope of the instrument panel (from the knee to the dash top).
def panelFrame():

	d = HOUSE[ 'dash' ]
	B = Vector2( d[ 'zFace' ], d[ 'yKnee' ] ); C = Vector2( d[ 'zTop' ], d[ 'yTop' ] )
	dir = C.clone().sub( B ).normalize() # (dz, dy)
	along = V( 0, dir.y, dir.x ) # up the panel
	normal = V( 0, dir.x, - dir.y ) # toward the helmsman (up and aft)

	def at( x, f ):
		return V( x, lerp( B.y, C.y, f ), lerp( B.x, C.x, f ) )

	return { 'along': along, 'normal': normal, 'at': at, 'length': B.distanceTo( C ), 'angle': math.atan2( dir.x, dir.y ) }


def buildConsole( kit, L, parts ):

	d = HOUSE[ 'dash' ]
	# console body; its ends follow the hull lining where the flared hull narrows toward the bow
	corners = [ [ d[ 'zFace' ], L.deckY ], [ d[ 'zFace' ], d[ 'yKnee' ] ], [ d[ 'zTop' ], d[ 'yTop' ] ], [ d[ 'zBack' ], d[ 'yTop' ] ], [ d[ 'zBack' ], L.deckY ] ]
	outline = []

	for i in range( len( corners ) ):

		a = corners[ i ]; b = corners[ ( i + 1 ) % len( corners ) ]

		for k in range( 6 ):
			outline.append( [ lerp( a[ 0 ], b[ 0 ], k / 6 ), lerp( a[ 1 ], b[ 1 ], k / 6 ) ] )

	def endX( z, y ):
		return min( d[ 'halfW' ], L.halfBreadth( L.tAtSheerZ( z ), min( y, L.sheerY( L.tAtSheerZ( z ) ) ) ) - L.shell - 0.004 )

	body = slab( outline, [], lambda u, v, side: V( ( - 1 if side else 1 ) * endX( u, v ), v, u ) )
	auxVertices( body, lambda p, i: [ 0.5, 0, 2, 0 ] )
	kit.add( 'gelcoat', body, { 'color': PALETTE[ 'gelcoat' ] } )

	# anti-glare dash top and instrument panel
	top = box( d[ 'halfW' ] * 2 - 0.01, 0.01, d[ 'zBack' ] - d[ 'zTop' ] )
	top.translate( 0, d[ 'yTop' ] + 0.005, ( d[ 'zBack' ] + d[ 'zTop' ] ) / 2 )
	kit.add( 'gelcoat', top, { 'color': 0x2a2c2f, 'rough': 0.85 } )

	pf = panelFrame()
	panel = box( d[ 'halfW' ] * 2 - 0.04, pf[ 'length' ] - 0.02, 0.012 )
	panel.applyMatrix4( mat4( 0, 0, 0, pf[ 'angle' ], 0, 0 ) )
	panel.translate( *pf[ 'at' ]( 0, 0.5 ).addScaledVector( pf[ 'normal' ], 0.004 ).toArray() )
	kit.add( 'fittings', panel, { 'color': 0x1d1f22, 'rough': 0.7, 'pattern': 4 } )

	# teak fiddle rail along the dash top edge
	fr = box( d[ 'halfW' ] * 2 - 0.02, 0.03, 0.022 )
	fr.translate( 0, d[ 'yTop' ] + 0.02, d[ 'zTop' ] + 0.011 )
	kit.add( 'wood', fr, { 'rough': 0.3 } )

	# gauges (bezel + backlit dial)
	for x, f in [ [ - 0.76, 0.72 ], [ - 0.34, 0.72 ], [ - 0.55, 0.9 ], [ - 0.12, 0.5 ], [ 0.02, 0.5 ] ]:

		c = pf[ 'at' ]( x, f ).addScaledVector( pf[ 'normal' ], 0.012 )
		m = Matrix4().makeBasis( V( - 1, 0, 0 ), pf[ 'along' ], pf[ 'normal' ] ).setPosition( c )
		bezel = torus( 0.043, 0.006, 4, 16 )
		bezel.applyMatrix4( m )
		kit.add( 'fittings', bezel, STAINLESS )
		dial = CircleGeometry( 0.042, 20 )
		# 3 mm proud of the panel face (was coplanar with it: z-fighting)
		dial.applyMatrix4( Matrix4().makeBasis( V( - 1, 0, 0 ), pf[ 'along' ], pf[ 'normal' ] ).setPosition( c.clone().addScaledVector( pf[ 'normal' ], 0.001 ) ) )
		kit.add( 'glow', dial, { 'color': 0xffffff, 'rough': 0.2, 'pattern': 3 } )

	# switch panel with rocker switches
	sw = pf[ 'at' ]( 0.55, 0.45 ).addScaledVector( pf[ 'normal' ], 0.012 )
	swm = Matrix4().makeBasis( V( - 1, 0, 0 ), pf[ 'along' ], pf[ 'normal' ] ).setPosition( sw )
	swPlate = box( 0.34, 0.1, 0.008 )
	swPlate.applyMatrix4( swm )
	kit.add( 'fittings', swPlate, { 'color': 0x2d3036, 'rough': 0.5, 'pattern': 4 } )
	tape = box( 0.3, 0.014, 0.002 )
	tape.applyMatrix4( Matrix4().makeBasis( V( - 1, 0, 0 ), pf[ 'along' ], pf[ 'normal' ] ).setPosition( sw.clone().addScaledVector( pf[ 'along' ], - 0.038 ).addScaledVector( pf[ 'normal' ], 0.005 ) ) )
	kit.add( 'fittings', tape, { 'color': 0xffffff, 'rough': 0.5, 'pattern': 8 } )

	for i in range( 6 ):

		r = box( 0.03, 0.045, 0.015 )
		r.applyMatrix4( Matrix4().makeBasis( V( - 1, 0, 0 ), pf[ 'along' ], pf[ 'normal' ] ).setPosition( sw.clone().add( V( - 0.13 + i * 0.052, 0, 0 ) ).addScaledVector( pf[ 'normal' ], 0.008 ) ) )
		kit.add( 'fittings', r, { 'color': 0x111214, 'rough': 0.4 } )
		led = box( 0.008, 0.008, 0.004 )
		led.applyMatrix4( Matrix4().makeBasis( V( - 1, 0, 0 ), pf[ 'along' ], pf[ 'normal' ] ).setPosition( sw.clone().add( V( - 0.13 + i * 0.052, 0, 0 ) ).addScaledVector( pf[ 'along' ], 0.035 ).addScaledVector( pf[ 'normal' ], 0.006 ) ) )
		kit.add( 'glow', led, { 'color': 0x33ff66 if i % 3 == 0 else 0xff5522, 'rough': 0.3, 'pattern': 6 } )

	# helm: wheel shaft (static) and the wheel pivot (animated part built in BoatModel)
	hub = pf[ 'at' ]( HOUSE[ 'helmX' ], 0.45 )
	center = hub.clone().addScaledVector( pf[ 'normal' ], 0.14 )
	kit.add( 'fittings', rod( hub.clone().addScaledVector( pf[ 'normal' ], - 0.01 ), center.clone().addScaledVector( pf[ 'normal' ], - 0.03 ), 0.02, 10 ), STAINLESS )
	helmBoss = cylinder( 0.05, 0.055, 0.025, 16 )
	helmBoss.applyMatrix4( alignY( hub.clone().addScaledVector( pf[ 'normal' ], 0.008 ), pf[ 'normal' ] ) )
	kit.add( 'fittings', helmBoss, { 'color': 0x2b2d30, 'rough': 0.5 } )
	parts[ 'wheelCenter' ] = center
	parts[ 'wheelAxis' ] = pf[ 'normal' ].clone().negate() # local +Z of the wheel points into the dash

	# throttle / shift control on the dash top, starboard of the wheel
	tx = - 0.93; tz = 1.24
	tb = roundedBox( 0.1, 0.075, 0.15, 0.015, 1 )
	tb.translate( tx, d[ 'yTop' ] + 0.01 + 0.0375, tz )
	kit.add( 'fittings', tb, BLACK_PLASTIC )
	tp = box( 0.085, 0.004, 0.13 )
	tp.translate( tx, d[ 'yTop' ] + 0.01 + 0.077, tz )
	kit.add( 'fittings', tp, STAINLESS )
	parts[ 'throttlePivot' ] = V( tx, d[ 'yTop' ] + 0.09, tz )

	# compass binnacle with a glass dome
	cx = HOUSE[ 'helmX' ]; cz = 1.3
	cb = lathe( [ [ 0, 0 ], [ 0.07, 0 ], [ 0.072, 0.02 ], [ 0.062, 0.045 ], [ 0, 0.045 ] ], 20 )
	cb.translate( cx, d[ 'yTop' ] + 0.01, cz )
	kit.add( 'fittings', cb, BLACK_PLASTIC )
	card = cylinder( 0.05, 0.05, 0.01, 20 )
	card.translate( cx, d[ 'yTop' ] + 0.06, cz )
	kit.add( 'fittings', card, { 'color': 0x2c2c2a, 'rough': 0.5 } )
	lubber = box( 0.004, 0.02, 0.008 )
	lubber.translate( cx, d[ 'yTop' ] + 0.07, cz + 0.045 )
	kit.add( 'fittings', lubber, { 'color': 0xd0402a, 'rough': 0.5 } )
	dome = sphere( 0.058, 20, 8, 0, math.pi * 2, 0, math.pi / 2 )
	dome.translate( cx, d[ 'yTop' ] + 0.053, cz )
	kit.add( 'glass', dome )

	# electronics: radar display (centre) and chart plotter (port)
	for x, mode in [ [ - 0.06, 1 ], [ 0.38, 2 ] ]:

		tilt = 0.3 # lean back so the screen faces the helmsman
		w = 0.36; h = 0.27
		bodyM = mat4( x, d[ 'yTop' ] + 0.01 + h / 2 + 0.03, 1.29, tilt, 0, 0 )
		body = roundedBox( w, h, 0.07, 0.018, 1 )
		body.applyMatrix4( bodyM )
		kit.add( 'fittings', body, BLACK_PLASTIC )
		mount = box( 0.1, 0.05, 0.08 )
		mount.translate( x, d[ 'yTop' ] + 0.035, 1.3 )
		kit.add( 'fittings', mount, BLACK_PLASTIC )
		screen = PlaneGeometry( w - 0.05, h - 0.06 )
		screen.applyMatrix4( Matrix4().makeRotationY( math.pi ) )
		screen.applyMatrix4( mat4( 0, 0.012, - 0.037 ) ) # 2 mm proud of the bezel face
		screen.applyMatrix4( bodyM )
		# PlaneGeometry uvs are flipped horizontally after the Y rotation; restore them
		uv = screen.attributes[ 'uv' ]

		for i in range( uv.count ):
			uv.setX( i, 1 - uv.getX( i ) )

		kit.add( 'glow', screen, { 'color': 0xffffff, 'rough': 0.15, 'pattern': mode } )

	# overhead console with VHF radio above the windshield
	oz = 1.02; oy = HOUSE[ 'roofUnderY' ] - 0.07
	oc = roundedBox( 0.9, 0.13, 0.3, 0.02, 1 )
	oc.applyMatrix4( mat4( - 0.15, oy, oz, - 0.25, 0, 0 ) )
	kit.add( 'fittings', oc, { 'color': 0x24262a, 'rough': 0.6 } )
	face = mat4( - 0.15, oy, oz, - 0.25, 0, 0 ) # aft face tilted down toward the helm
	vhf = box( 0.2, 0.06, 0.02 )
	vhf.applyMatrix4( Matrix4().makeTranslation( - 0.18, 0, - 0.155 ) )
	vhf.applyMatrix4( face )
	kit.add( 'fittings', vhf, { 'color': 0x111214, 'rough': 0.4 } )
	lcd = PlaneGeometry( 0.09, 0.03 )
	lcd.applyMatrix4( Matrix4().makeRotationY( math.pi ) )
	lcd.applyMatrix4( Matrix4().makeTranslation( - 0.2, 0.005, - 0.168 ) ) # 3 mm proud of the radio face
	lcd.applyMatrix4( face )
	kit.add( 'glow', lcd, { 'color': 0x7dff9a, 'rough': 0.2, 'pattern': 6 } )

	for k in [ - 0.06, 0.1, 0.26 ]:

		knob = cylinder( 0.012, 0.012, 0.02, 10 )
		knob.applyMatrix4( Matrix4().makeRotationX( math.pi / 2 ) )
		knob.applyMatrix4( Matrix4().makeTranslation( k, 0, - 0.16 ) )
		knob.applyMatrix4( face )
		kit.add( 'fittings', knob, STAINLESS )

	# microphone hanging from the VHF
	mic = roundedBox( 0.05, 0.08, 0.03, 0.01, 1 )
	mic.translate( - 0.02, oy - 0.2, oz - 0.12 )
	kit.add( 'fittings', mic, BLACK_PLASTIC )
	kit.add( 'fittings', tube( [ V( - 0.05, oy - 0.04, oz - 0.15 ), V( - 0.08, oy - 0.1, oz - 0.14 ), V( - 0.04, oy - 0.14, oz - 0.13 ), V( - 0.02, oy - 0.16, oz - 0.12 ) ], 0.004, 16, 4 ), BLACK_PLASTIC )

	# cabin dome light
	dl = cylinder( 0.08, 0.085, 0.012, 20 )
	dl.translate( 0, HOUSE[ 'roofUnderY' ] - 0.006, 0.42 )
	kit.add( 'glow', dl, { 'color': 0xffe6b8, 'rough': 0.4, 'pattern': 5 } )

	# cuddy door in the console face (port side)
	door = box( 0.5, 0.62, 0.01 )
	door.translate( 0.55, 0.72, d[ 'zFace' ] - 0.004 )
	kit.add( 'fittings', door, { 'color': 0x121315, 'rough': 0.9 } )

	for w, h, x, y in [ [ 0.56, 0.03, 0.55, 1.045 ], [ 0.56, 0.03, 0.55, 0.395 ], [ 0.03, 0.68, 0.285, 0.72 ], [ 0.03, 0.68, 0.815, 0.72 ] ]:

		b = box( w, h, 0.022 )
		b.translate( x, y, d[ 'zFace' ] - 0.011 )
		kit.add( 'wood', b, { 'rough': 0.3 } )


# ------------------------------------------------------------------ seating

def buildSeating( kit, L ):

	x = HOUSE[ 'helmX' ]; z = HOUSE[ 'seatZ' ]; y0 = L.deckY
	baseP = cylinder( 0.18, 0.19, 0.012, 20 )
	baseP.translate( x, y0 + 0.006, z )
	kit.add( 'fittings', baseP, STAINLESS )
	kit.add( 'fittings', rod( V( x, y0, z ), V( x, 0.92, z ), 0.042, 14 ), STAINLESS )
	ring = torus( 0.17, 0.011, 6, 28 )
	ring.applyMatrix4( mat4( x, 0.62, z, math.pi / 2, 0, 0 ) )
	kit.add( 'fittings', ring, STAINLESS )

	for i in range( 3 ):

		a = i * math.pi * 2 / 3 + 0.5
		kit.add( 'fittings', rod( V( x + math.cos( a ) * 0.04, 0.62, z + math.sin( a ) * 0.04 ), V( x + math.cos( a ) * 0.165, 0.62, z + math.sin( a ) * 0.165 ), 0.008, 6 ), STAINLESS )

	pan = roundedBox( 0.44, 0.045, 0.4, 0.015, 1 )
	pan.translate( x, 0.94, z )
	kit.add( 'fittings', pan, { 'color': 0x2b2e33, 'rough': 0.5 } )
	cushion = roundedBox( 0.46, 0.085, 0.42, 0.035, 1 )
	cushion.translate( x, 1.0, z )
	kit.add( 'fittings', cushion, VINYL )
	back = roundedBox( 0.44, 0.3, 0.075, 0.03, 1 )
	back.applyMatrix4( mat4( x, 1.22, z - 0.2, - 0.17, 0, 0 ) )
	kit.add( 'fittings', back, VINYL )
	kit.add( 'fittings', rod( V( x, 0.96, z - 0.17 ), V( x, 1.12, z - 0.2 ), 0.018, 8 ), STAINLESS )

	# companion bench on the port side
	bench = roundedBox( 0.42, 0.4, 0.75, 0.02, 1 )
	bench.translate( 0.86, y0 + 0.2, 0.45 )
	kit.add( 'gelcoat', bench, { 'color': PALETTE[ 'gelcoat' ], 'rough': 0.35 } )
	bc = roundedBox( 0.42, 0.07, 0.73, 0.03, 1 )
	bc.translate( 0.86, y0 + 0.435, 0.45 )
	kit.add( 'fittings', bc, VINYL )


# ------------------------------------------------------------------ lived-in detail: sole, headliner, props

# inner face of the wheelhouse side wall (s = 1 port, -1 starboard) and the inward normal
def wallIn( L, s, z, y ):

	return s * ( wallX( L, z, y ) - HOUSE[ 'wallT' ] )


def buildCabinDetail( kit, L ):

	d = HOUSE[ 'dash' ]
	y0 = L.deckY
	yb = HOUSE[ 'roofUnderY' ]

	def hullIn( z ):
		return min( houseHalfWidth( L, z ) - HOUSE[ 'wallT' ], L.halfBreadth( L.tAtSheerZ( z ), y0 ) - L.shell ) - 0.015

	# ---- teak-and-holly sole from the house back to the console (planks fore and aft)
	soleOut = []
	zA = L.houseBack - 0.02; zB = d[ 'zFace' ] - 0.005

	for i in range( 7 ):

		z = lerp( zA, zB, i / 6 )
		soleOut.append( [ z, - hullIn( z ) ] )

	for i in range( 6, - 1, - 1 ):

		z = lerp( zA, zB, i / 6 )
		soleOut.append( [ z, hullIn( z ) ] )

	sole = slab( soleOut, [], lambda u, v, side: V( v, y0 + 0.009 - side * 0.009, u ), { 'back': False } )
	kit.add( 'wood', sole, { 'rough': 0.5, 'pattern': 2 } )

	# ---- headliner battens (varnished) across the roof, and a teak grab rail overhead
	for z in [ - 0.22, 0.26, 0.74 ]:

		w = 2 * ( wallX( L, z, yb ) - HOUSE[ 'wallT' ] ) - 0.02
		b = box( 0.045, 0.016, w )
		b.applyMatrix4( mat4( 0, yb - 0.008, z, 0, math.pi / 2, 0 ) )
		kit.add( 'wood', b, { 'rough': 0.35 } )

	gy = yb - 0.06
	kit.add( 'wood', rod( V( - 0.32, gy, - 0.2 ), V( - 0.32, gy, 0.9 ), 0.016, 10 ), { 'rough': 0.35 } )

	for z in [ - 0.12, 0.35, 0.82 ]:
		kit.add( 'fittings', rod( V( - 0.32, gy, z ), V( - 0.32, yb - 0.004, z ), 0.011, 8 ), STAINLESS )

	# ---- overhead rod rack on the port side: three rods with cork grips and reels
	for z in [ - 0.3, 0.35, 0.95 ]:

		br = box( 0.36, 0.012, 0.03 )
		br.translate( 0.64, yb - 0.08, z )
		kit.add( 'fittings', br, BLACK_PLASTIC )

		for x in [ 0.48, 0.8 ]:
			kit.add( 'fittings', rod( V( x, yb - 0.08, z ), V( x, yb - 0.002, z ), 0.005, 6 ), STAINLESS )

	for i, x in enumerate( [ 0.54, 0.64, 0.74 ] ):

		y = yb - 0.066
		z0 = - 0.42 + i * 0.05; z1 = 1.12
		kit.add( 'fittings', rod( V( x, y, z0 ), V( x, y, z1 ), 0.007, 6, 0.0025 ), { 'color': [ 0x1b1c1e, 0x2a3a52, 0x4a1f1a ][ i ], 'rough': 0.3 } )
		kit.add( 'fittings', rod( V( x, y, z0 ), V( x, y, z0 + 0.24 ), 0.013, 8 ), { 'color': 0xb48a5c, 'rough': 0.85 } )
		reel = cylinder( 0.034, 0.034, 0.03, 12 )
		reel.applyMatrix4( mat4( x, y - 0.045, z0 + 0.3, 0, 0, math.pi / 2 ) )
		kit.add( 'fittings', reel, { 'color': [ 0x8a8d91, 0xb89040, 0x2b2d30 ][ i ], 'rough': 0.3, 'metal': 1 } )
		kit.add( 'fittings', rod( V( x, y - 0.012, z0 + 0.3 ), V( x, y - 0.03, z0 + 0.3 ), 0.004, 5 ), { 'color': 0x2b2d30, 'rough': 0.4 } )

	# ---- port wall: breaker panel below the forward window, oilskin and lifejacket on hooks aft
	z = 0.56; y = 1.33; x = wallIn( L, 1, z, y )
	pnl = box( 0.014, 0.22, 0.34 )
	pnl.translate( x - 0.007, y, z )
	kit.add( 'fittings', pnl, { 'color': 0x3a3d42, 'rough': 0.5, 'pattern': 4 } )

	for r in range( 2 ):

		ry = y + 0.045 - r * 0.1
		lt = box( 0.002, 0.014, 0.3 )
		lt.translate( x - 0.015, ry + 0.035, z )
		kit.add( 'fittings', lt, { 'color': 0xffffff, 'rough': 0.5, 'pattern': 8 } )

		for k in range( 8 ):

			bk = box( 0.016, 0.034, 0.02 )
			bk.translate( x - 0.021, ry, z - 0.135 + k * 0.0386 )
			kit.add( 'fittings', bk, { 'color': 0x121314, 'rough': 0.4 } )
			tog = box( 0.012, 0.012, 0.008 )
			tog.translate( x - 0.034, ry + ( - 0.007 if ( k * 7 + r * 3 ) % 5 == 0 else 0.007 ), z - 0.135 + k * 0.0386 )
			kit.add( 'fittings', tog, { 'color': 0xb02a22 if ( k * 7 + r * 3 ) % 5 == 0 else 0x1e1f21, 'rough': 0.4 } )

	def hang( z, yTop, color, scale ):

		x = wallIn( L, 1, z, yTop )
		kit.add( 'fittings', rod( V( x, yTop + 0.02, z ), V( x - 0.05, yTop + 0.04, z ), 0.006, 6 ), STAINLESS )
		# garment hanging by its collar: narrow at the hook, sloping shoulders, a slightly flared hem
		# with an open bottom, pressed flat against the wall; sleeves hang down the front
		prof = [ [ 0.17, 0.0 ], [ 0.2, 0.03 ], [ 0.2, 0.3 ], [ 0.21, 0.5 ], [ 0.2, 0.56 ], [ 0.12, 0.62 ], [ 0.05, 0.66 ], [ 0.001, 0.665 ] ]
		g = lathe( [ [ r * scale, h * scale ] for r, h in prof ], 20 )
		g.applyMatrix4( mat4( x - 0.06, yTop - 0.66 * scale, z, 0, 0, 0, 0.3, 1, 1 ) )
		kit.add( 'fittings', g, { 'color': color, 'rough': 0.6, 'pattern': 9 } )

		for sd in [ - 1, 1 ]:

			sh = V( x - 0.07, yTop - 0.12 * scale, z + sd * 0.17 * scale )
			sleeve = tube( [ sh, V( x - 0.1, yTop - 0.3 * scale, z + sd * 0.2 * scale ), V( x - 0.11, yTop - 0.52 * scale, z + sd * 0.16 * scale ) ], 0.045 * scale, 10, 8 )
			kit.add( 'fittings', sleeve, { 'color': color, 'rough': 0.6, 'pattern': 9 } )

		hood = sphere( 0.1 * scale, 12, 8, 0, math.pi * 2, 0, math.pi * 0.6 )
		hood.applyMatrix4( mat4( x - 0.04, yTop - 0.03, z, 0.5, 0, 0, 0.5, 0.8, 1 ) )
		kit.add( 'fittings', hood, { 'color': Color( color ).multiplyScalar( 0.85 ).getHex(), 'rough': 0.6, 'pattern': 9 } )

	hang( - 0.12, 1.92, 0xe0a81c, 1.0 ) # yellow oilskin
	hang( 0.14, 1.9, 0xe2531a, 0.8 ) # orange lifejacket

	# ---- starboard wall: extinguisher on its bracket, photos and the tide table, torch in a clip
	z = - 0.18; yb0 = y0 + 0.2; x = wallIn( L, - 1, z, 0.8 )
	ex = x + 0.075
	body = lathe( [ [ 0, 0 ], [ 0.058, 0.0 ], [ 0.062, 0.02 ], [ 0.062, 0.3 ], [ 0.05, 0.35 ], [ 0.018, 0.37 ], [ 0, 0.37 ] ], 18 )
	body.translate( ex, yb0, z )
	kit.add( 'fittings', body, { 'color': 0xb3150f, 'rough': 0.35 } )
	valve = box( 0.03, 0.05, 0.05 )
	valve.translate( ex, yb0 + 0.39, z )
	kit.add( 'fittings', valve, { 'color': 0x1a1a1a, 'rough': 0.4 } )
	kit.add( 'fittings', rod( V( ex, yb0 + 0.41, z ), V( ex + 0.02, yb0 + 0.42, z + 0.09 ), 0.007, 6 ), { 'color': 0x8c8f93, 'rough': 0.3, 'metal': 1 } )
	kit.add( 'fittings', tube( [ V( ex, yb0 + 0.38, z + 0.02 ), V( ex + 0.03, yb0 + 0.3, z + 0.07 ), V( ex + 0.05, yb0 + 0.15, z + 0.06 ), V( ex + 0.06, yb0 + 0.08, z + 0.03 ) ], 0.008, 12, 5 ), { 'color': 0x141414, 'rough': 0.6 } )

	for h in [ 0.12, 0.26 ]:

		strap = torus( 0.064, 0.004, 4, 20 )
		strap.applyMatrix4( mat4( ex, yb0 + h, z, math.pi / 2, 0, 0 ) )
		kit.add( 'fittings', strap, STAINLESS )

	plate = box( 0.006, 0.3, 0.07 )
	plate.translate( x + 0.003, yb0 + 0.19, z )
	kit.add( 'fittings', plate, STAINLESS )
	# instruction label on the cylinder
	lab = cylinder( 0.0625, 0.0625, 0.1, 18, 1, True, - 0.9, 1.8 )
	lab.translate( ex, yb0 + 0.19, z )
	kit.add( 'fittings', lab, { 'color': 0xffffff, 'rough': 0.5, 'pattern': 8 } )

	def onWall( s, z, y, w, h, roll, opts ):

		q = PlaneGeometry( w, h )
		q.applyMatrix4( mat4( 0, 0, 0, 0, ( - math.pi / 2 if s > 0 else math.pi / 2 ), 0 ) )
		q.applyMatrix4( Matrix4().makeRotationX( roll ) )
		q.translate( 0, y, z )
		# follow the wall (it curves with the hull and leans in above the rail): each vertex sits
		# 12 mm off the analytic inner face (the wall mesh is faceted: its chords stand ~1 cm proud of
		# the curve), so no part of the card can dip behind it
		pos = q.getAttribute( 'position' )

		for i in range( pos.count ):
			pos.setX( i, wallIn( L, s, pos.getZ( i ), pos.getY( i ) ) - s * 0.012 )

		pos.needsUpdate = True
		q.computeVertexNormals()
		kit.add( 'fittings', q, opts )

	onWall( - 1, 0.02, 1.47, 0.1, 0.13, 0.06, { 'color': 0xffffff, 'rough': 0.3, 'pattern': 7, 'anim': 0.15 } )
	onWall( - 1, 0.15, 1.42, 0.12, 0.09, - 0.1, { 'color': 0xffffff, 'rough': 0.3, 'pattern': 7, 'anim': 0.7 } )
	onWall( - 1, 0.36, 1.36, 0.19, 0.25, 0.02, { 'color': 0xffffff, 'rough': 0.9, 'pattern': 5 } )

	# masking tape holding the tide table
	for dz, dy in [ [ - 0.09, 0.12 ], [ 0.09, 0.12 ] ]:
		onWall( - 1, 0.36 + dz, 1.36 + dy, 0.05, 0.018, 0.5, { 'color': 0xd8cfa8, 'rough': 0.8 } )

	# torch in its clip, forward on the starboard wall
	z = 0.85; y = 1.3; x = wallIn( L, - 1, z, y ) + 0.035
	kit.add( 'fittings', rod( V( x, y, z - 0.1 ), V( x, y, z + 0.1 ), 0.02, 12 ), { 'color': 0xf2c21b, 'rough': 0.45 } )
	kit.add( 'fittings', rod( V( x, y, z + 0.1 ), V( x, y, z + 0.13 ), 0.026, 12 ), { 'color': 0x1a1a1a, 'rough': 0.4 } )

	for dz in [ - 0.06, 0.05 ]:

		clip = torus( 0.023, 0.003, 4, 12, math.pi * 1.3 )
		clip.applyMatrix4( mat4( x, y, z + dz, 0, math.pi / 2, - 0.65 ) )
		kit.add( 'fittings', clip, BLACK_PLASTIC )

	# ---- dash top: clipboard with the fishing log, mug of coffee in a holder
	ytop = d[ 'yTop' ] + 0.01
	m = mat4( 0.74, ytop + 0.004, 1.26, 0, 0.18, 0 )
	board = box( 0.23, 0.005, 0.31 )
	board.applyMatrix4( m )
	kit.add( 'fittings', board, { 'color': 0x6b4a2b, 'rough': 0.7 } )
	paper = PlaneGeometry( 0.21, 0.28 )
	paper.applyMatrix4( mat4( 0, 0.0035, 0.012, - math.pi / 2, 0, 0 ) )
	paper.applyMatrix4( m )
	kit.add( 'fittings', paper, { 'color': 0xffffff, 'rough': 0.9, 'pattern': 5 } )
	clip = box( 0.08, 0.014, 0.03 )
	clip.applyMatrix4( mat4( 0, 0.008, 0.14 ) )
	clip.applyMatrix4( m )
	kit.add( 'fittings', clip, STAINLESS )

	mx = - 0.76; mz = 1.34
	holder = torus( 0.05, 0.006, 6, 18 )
	holder.applyMatrix4( mat4( mx, ytop + 0.035, mz, math.pi / 2, 0, 0 ) )
	kit.add( 'fittings', holder, BLACK_PLASTIC )
	mug = cylinder( 0.042, 0.038, 0.095, 18, 1, True )
	mug.translate( mx, ytop + 0.048, mz )
	kit.add( 'fittings', mug, { 'color': 0x1f4a3a, 'rough': 0.25 } )
	inner = cylinder( 0.039, 0.036, 0.09, 18, 1, True )
	inner.translate( mx, ytop + 0.05, mz )
	orientTowardsAxis( inner )
	kit.add( 'fittings', inner, { 'color': 0xe9e4d8, 'rough': 0.25 } )
	coffee = CircleGeometry( 0.039, 18 )
	coffee.applyMatrix4( mat4( mx, ytop + 0.082, mz, - math.pi / 2, 0, 0 ) )
	kit.add( 'fittings', coffee, { 'color': 0x2a160b, 'rough': 0.1 } )
	base = CircleGeometry( 0.038, 18 )
	base.applyMatrix4( mat4( mx, ytop + 0.0005, mz, math.pi / 2, 0, 0 ) )
	kit.add( 'fittings', base, { 'color': 0x1f4a3a, 'rough': 0.4 } )
	handle = torus( 0.026, 0.006, 6, 12, math.pi )
	handle.applyMatrix4( mat4( mx - 0.042, ytop + 0.048, mz, 0, 0, math.pi / 2 ) )
	kit.add( 'fittings', handle, { 'color': 0x1f4a3a, 'rough': 0.25 } )

	# ---- port bench: lifejackets, a folded chart; coiled line on the sole aft
	top = y0 + 0.47

	for i, dy in enumerate( [ 0, 0.05 ] ):

		v = roundedBox( 0.34, 0.05, 0.4, 0.02, 1 )
		v.applyMatrix4( mat4( 0.86, top + 0.025 + dy, 0.28 + i * 0.02, 0, 0.12 * i - 0.05, 0 ) )
		kit.add( 'fittings', v, { 'color': 0xe2531a, 'rough': 0.6, 'pattern': 9 } )

		for dz in [ - 0.08, 0.08 ]:

			st = box( 0.345, 0.052, 0.025 )
			st.applyMatrix4( mat4( 0.86, top + 0.025 + dy, 0.28 + i * 0.02 + dz, 0, 0.12 * i - 0.05, 0 ) )
			kit.add( 'fittings', st, { 'color': 0x151515, 'rough': 0.7 } )

	chart = PlaneGeometry( 0.3, 0.22 )
	chart.applyMatrix4( mat4( 0.85, top + 0.002, 0.64, - math.pi / 2, 0, 0.3 ) )
	kit.add( 'fittings', chart, { 'color': 0xffffff, 'rough': 0.85, 'pattern': 6 } )

	for k in range( 5 ):

		loop = torus( 0.15 - k * 0.004, 0.011, 6, 28 )
		loop.applyMatrix4( mat4( 0.62 + k * 0.004, y0 + 0.02 + k * 0.02, - 0.14 + k * 0.006, math.pi / 2 + ( k % 2 ) * 0.06, 0, 0 ) )
		uv = loop.attributes[ 'uv' ]

		for j in range( uv.count ):
			uv.setXY( j, uv.getX( j ) * 0.94, uv.getY( j ) )

		kit.add( 'fittings', loop, { 'color': 0x2c5a8c, 'rough': 0.8, 'pattern': 1 } )


# flip a lathe / open cylinder's winding so its faces point inward (the inside of the mug)
def orientTowardsAxis( g ):

	idx = list( g.index.array )

	for i in range( 0, len( idx ), 3 ):

		t = idx[ i + 1 ]; idx[ i + 1 ] = idx[ i + 2 ]; idx[ i + 2 ] = t

	g.setIndex( idx )
	n = g.attributes[ 'normal' ]

	for i in range( n.count ):
		n.setXYZ( i, - n.getX( i ), - n.getY( i ), - n.getZ( i ) )


# ------------------------------------------------------------------ roof gear: mast, radars, lights, antennas

def buildRoofGear( kit, L, parts ):

	yr = roofTopY( 0 )
	mz = - 0.5

	# mast
	mb = box( 0.18, 0.025, 0.18 )
	mb.translate( 0, yr + 0.005, mz )
	kit.add( 'fittings', mb, WHITE_PAINT )
	kit.add( 'fittings', rod( V( 0, yr, mz ), V( 0, 3.9, mz ), 0.042, 14, 0.03 ), WHITE_PAINT )

	# radome on a forward bracket
	ry = 3.02
	arm = box( 0.1, 0.035, 0.34 )
	arm.translate( 0, ry - 0.02, mz + 0.19 )
	kit.add( 'fittings', arm, WHITE_PAINT )
	plate = box( 0.34, 0.015, 0.34 )
	plate.translate( 0, ry, mz + 0.3 )
	kit.add( 'fittings', plate, WHITE_PAINT )
	kit.add( 'fittings', rod( V( 0, ry - 0.02, mz + 0.04 ), V( 0, ry - 0.2, mz + 0.01 ), 0.012, 6 ), WHITE_PAINT )
	radome = lathe( [ [ 0, 0 ], [ 0.285, 0 ], [ 0.3, 0.018 ], [ 0.3, 0.095 ], [ 0.29, 0.14 ], [ 0.245, 0.188 ], [ 0.16, 0.222 ], [ 0.07, 0.234 ], [ 0, 0.236 ] ], 24 )
	radome.translate( 0, ry + 0.008, mz + 0.3 )
	kit.add( 'fittings', radome, { 'color': 0xf3f2ee, 'rough': 0.4 } )
	band = cylinder( 0.302, 0.302, 0.02, 24, 1, True )
	band.translate( 0, ry + 0.03, mz + 0.3 )
	kit.add( 'fittings', band, { 'color': 0x3a3d42, 'rough': 0.5 } )

	# spreader with side lights, masthead light
	sy = 3.45
	spreader = box( 0.78, 0.03, 0.045 )
	spreader.translate( 0, sy - 0.05, mz )
	kit.add( 'fittings', spreader, WHITE_PAINT )

	for s in [ 1, - 1 ]:

		hx = s * 0.4
		housing = box( 0.055, 0.07, 0.11 )
		housing.translate( hx, sy, mz + 0.02 )
		kit.add( 'fittings', housing, BLACK_PLASTIC )
		lens = box( 0.014, 0.05, 0.085 )
		lens.translate( hx + s * 0.033, sy, mz + 0.03 )
		kit.add( 'glow', lens, { 'color': 0xff1a0e if s > 0 else 0x14ff5a, 'rough': 0.2, 'pattern': 0 } )
		# inboard screen
		scr = box( 0.004, 0.07, 0.12 )
		scr.translate( hx - s * 0.03, sy, mz + 0.02 )
		kit.add( 'fittings', scr, BLACK_PLASTIC )

	mhBase = cylinder( 0.036, 0.036, 0.03, 12 )
	mhBase.translate( 0, 3.915, mz )
	kit.add( 'fittings', mhBase, BLACK_PLASTIC )
	mhLens = cylinder( 0.03, 0.03, 0.065, 12 )
	mhLens.translate( 0, 3.962, mz )
	kit.add( 'glow', mhLens, { 'color': 0xfff3dc, 'rough': 0.2, 'pattern': 0 } )
	mhCap = cylinder( 0.034, 0.038, 0.016, 12 )
	mhCap.translate( 0, 4.003, mz )
	kit.add( 'fittings', mhCap, BLACK_PLASTIC )

	# VHF whips on ratchet mounts at the aft roof corners (sway in the vertex shader)
	for s in [ 1, - 1 ]:

		x = s * 1.02; z = - 0.78; y = roofTopY( 1.02 )
		mount = roundedBox( 0.06, 0.07, 0.06, 0.01, 1 )
		mount.translate( x, y + 0.035, z )
		kit.add( 'fittings', mount, STAINLESS )
		length = 2.4 if s > 0 else 1.2
		whip = cylinder( 0.005, 0.013, length, 8, 10 )
		whip.translate( x, y + 0.07 + length / 2, z )
		auxVertices( whip, lambda p, i: [ 0.3, 0, 3, max( 0, ( p.y - y - 0.07 ) / length ) ] )
		kit.add( 'fittings', whip, { 'color': 0xf4f4f1 } )

	# open-array radar pedestal (array is animated, built in BoatModel)
	pz = 0.8
	ped = roundedBox( 0.26, 0.28, 0.3, 0.03, 1 )
	ped.translate( 0, yr + 0.14, pz )
	kit.add( 'fittings', ped, { 'color': 0xf3f2ee, 'rough': 0.4 } )
	parts[ 'radarPivot' ] = V( 0, yr + 0.3, pz )

	# spotlight and horn on the front of the roof
	sx = 0.42; szz = 1.05; syy = roofTopY( 0.42 )
	kit.add( 'fittings', rod( V( sx, syy, szz ), V( sx, syy + 0.1, szz ), 0.03, 10 ), BLACK_PLASTIC )
	head = cylinder( 0.065, 0.06, 0.15, 16 )
	head.applyMatrix4( mat4( sx, syy + 0.17, szz, math.pi / 2, 0, 0 ) )
	kit.add( 'fittings', head, STAINLESS )
	sl = CircleGeometry( 0.058, 16 )
	sl.translate( sx, syy + 0.17, szz + 0.0755 )
	kit.add( 'glow', sl, { 'color': 0xfff1d6, 'rough': 0.2, 'pattern': 4 } )
	horn = lathe( [ [ 0.0, 0 ], [ 0.018, 0.0 ], [ 0.016, 0.1 ], [ 0.024, 0.17 ], [ 0.05, 0.22 ], [ 0.046, 0.222 ], [ 0.0, 0.2 ] ], 16 )
	horn.applyMatrix4( mat4( - sx, roofTopY( 0.42 ) + 0.07, szz - 0.12, math.pi / 2, 0, 0 ) )
	kit.add( 'fittings', horn, STAINLESS )
	kit.add( 'fittings', rod( V( - sx, roofTopY( 0.42 ), szz ), V( - sx, roofTopY( 0.42 ) + 0.07, szz ), 0.012, 6 ), STAINLESS )

	# the owner's buoy colours displayed on the roof
	bx = 0.62; bz = - 0.72
	bracket = box( 0.12, 0.03, 0.12 )
	bracket.translate( bx, roofTopY( bx ) + 0.015, bz )
	kit.add( 'fittings', bracket, STAINLESS )

	for g, opts in buoyGeometry():

		g.translate( bx, roofTopY( bx ) + 0.03, bz )
		kit.add( 'fittings', g, opts )

	# deck floodlights under the roof overhang, aimed at the work deck
	for s in [ 1, - 1 ]:

		fl = roundedBox( 0.13, 0.07, 0.08, 0.01, 1 )
		fl.translate( s * 0.55, HOUSE[ 'roofUnderY' ] - 0.035, HOUSE[ 'roofZ0' ] + 0.14 )
		kit.add( 'fittings', fl, BLACK_PLASTIC )
		lens = PlaneGeometry( 0.11, 0.06 )
		lens.applyMatrix4( mat4( s * 0.55, HOUSE[ 'roofUnderY' ] - 0.071, HOUSE[ 'roofZ0' ] + 0.14, math.pi / 2 + 0.35, 0, 0 ) )
		kit.add( 'glow', lens, { 'color': 0xfff1d6, 'rough': 0.2, 'pattern': 4 } )

	# life ring on the port house side
	lx = wallX( L, - 0.02, 1.72 ) + 0.05
	ringG = torus( 0.235, 0.05, 8, 24 )
	ringG.applyMatrix4( mat4( lx, 1.72, - 0.02, 0, math.pi / 2, 0 ) )
	paintVertices( ringG, lambda p, i: 0xf2f0ea if abs( fmod( fmod( math.atan2( p.y - 1.72, p.z + 0.02 ) / ( math.pi / 2 ), 1 ) + 1, 1 ) - 0.5 ) > 0.4 else 0xf25a12 )
	kit.add( 'fittings', ringG, { 'rough': 0.6 } )
	hook = box( 0.03, 0.05, 0.08 )
	hook.translate( lx - 0.03, 1.72 + 0.26, - 0.02 )
	kit.add( 'fittings', hook, STAINLESS )


# Steering wheel: varnished mahogany destroyer wheel with a brass hub.
# Local frame: +Z is the shaft axis (into the dash), spokes in the XY plane.
def wheelGeometry():

	list_ = []
	wood = { 'rough': 0.3, 'metal': 0 }
	R = 0.2
	list_.append( prepare( torus( R, 0.017, 8, 40 ), dict( wood, color = 0xe8b898 ) ) )

	for i in range( 6 ):

		a = i * math.pi / 3 + math.pi / 6 # king spoke straight up when centred
		dir = V( math.cos( a ), math.sin( a ), 0 )
		spoke = cylinder( 0.009, 0.013, R - 0.05, 8 )
		spoke.applyMatrix4( alignY( dir.clone().multiplyScalar( 0.05 + ( R - 0.05 ) / 2 ), dir ) )
		list_.append( prepare( spoke, dict( wood, color = 0xe8b898 ) ) )
		handle = lathe( [ [ 0.0, 0 ], [ 0.011, 0.0 ], [ 0.013, 0.02 ], [ 0.017, 0.045 ], [ 0.012, 0.06 ], [ 0.01, 0.07 ], [ 0.015, 0.078 ], [ 0.0, 0.086 ] ], 8 )
		handle.applyMatrix4( alignY( dir.clone().multiplyScalar( R - 0.005 ), dir ) )
		list_.append( prepare( handle, dict( wood, color = 0xe8b898 ) ) )

	hub = lathe( [ [ 0, - 0.03 ], [ 0.05, - 0.03 ], [ 0.058, - 0.012 ], [ 0.058, 0.012 ], [ 0.05, 0.028 ], [ 0.0, 0.03 ] ], 20 )
	hub.applyMatrix4( Matrix4().makeRotationX( math.pi / 2 ) )
	list_.append( prepare( hub, dict( wood, color = 0xe8b898 ) ) )
	cap = sphere( 0.032, 16, 6, 0, math.pi * 2, 0, math.pi / 2 )
	cap.applyMatrix4( Matrix4().makeRotationX( - math.pi / 2 ) )
	cap.translate( 0, 0, - 0.028 )
	list_.append( prepare( cap, { 'color': 0xc8a050, 'rough': 0.25, 'metal': 1, 'pattern': 1 } ) )
	return mergePrepared( list_ )


# Throttle lever, local origin at the pivot, lever pointing +Y (neutral).
def throttleGeometry():

	list_ = []
	list_.append( prepare( rod( V( 0, - 0.01, 0 ), V( 0, 0.15, 0.015 ), 0.008, 8 ), STAINLESS ) )
	knob = sphere( 0.022, 14, 10 )
	knob.scale( 1, 1.15, 1 )
	knob.translate( 0, 0.165, 0.016 )
	list_.append( prepare( knob, BLACK_PLASTIC ) )
	boss = cylinder( 0.02, 0.02, 0.05, 12 )
	boss.applyMatrix4( Matrix4().makeRotationZ( math.pi / 2 ) )
	list_.append( prepare( boss, STAINLESS ) )
	return mergePrepared( list_ )


# Open-array radar antenna, local origin on the rotation axis at the pedestal top.
def radarArrayGeometry():

	list_ = []
	list_.append( prepare( cylinder( 0.05, 0.06, 0.06, 14 ), { 'color': 0xf3f2ee, 'rough': 0.4 } ) )
	bar = roundedBox( 1.05, 0.12, 0.1, 0.035, 2 )
	bar.translate( 0, 0.09, 0 )
	list_.append( prepare( bar, { 'color': 0xf3f2ee, 'rough': 0.4 } ) )
	stripe = box( 0.95, 0.028, 0.004 )
	stripe.translate( 0, 0.09, 0.051 )
	list_.append( prepare( stripe, { 'color': 0x2c2f35, 'rough': 0.5 } ) )
	return mergePrepared( list_ )
