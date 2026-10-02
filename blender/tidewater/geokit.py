# Geometry helpers for the procedural boat -- pure-Python port of
# src/world/boat/GeoKit.js.
#
# Every geometry is normalized to the same attribute layout so parts can be merged per
# material:
#   position, normal, uv (meters where it matters), color (linear RGB),
#   aux = (roughness, metalness, pattern id, animation weight)
#
# Same function names, same defaults, same vertex ordering as the JS: the port exists so
# that a Blender-side build reproduces the JS geometry exactly, so nothing here is
# re-derived or tidied up.
#
# Pure Python 3, standard library only (no numpy, no bpy).

import math

from mathutil import (
	CatmullRomCurve3, Color, Euler, Float32Array, Matrix4, Quaternion, ShapeUtils, Vector2,
	Vector3, js_hypot,
)
from geometry import (
	BoxGeometry, BufferGeometry, CylinderGeometry, Float32BufferAttribute, LatheGeometry,
	RoundedBoxGeometry, SphereGeometry, TorusGeometry, TubeGeometry, mergeGeometries,
)

KEEP = { 'position', 'normal', 'uv', 'color', 'aux' }
_color = Color()
_v = Vector3()
_q = Quaternion()
_e = Euler()
_s = Vector3()


def mat4( x = 0, y = 0, z = 0, rx = 0, ry = 0, rz = 0, sx = 1, sy = None, sz = None, order = 'XYZ' ):
	"""Matrix from position, Euler rotation and (uniform by default) scale."""

	if sy is None:
		sy = sx

	if sz is None:
		sz = sx

	_e.set( rx, ry, rz, order )
	_q.setFromEuler( _e )
	return Matrix4().compose( _v.set( x, y, z ), _q, _s.set( sx, sy, sz ) )


def alignY( pos, dir, roll = 0 ):
	"""Matrix placing the local +Y axis along `dir` at `pos`."""

	d = dir.clone().normalize()
	q = Quaternion().setFromUnitVectors( Vector3( 0, 1, 0 ), d )

	if roll:
		q.multiply( Quaternion().setFromAxisAngle( Vector3( 0, 1, 0 ), roll ) )

	return Matrix4().compose( pos, q, Vector3( 1, 1, 1 ) )


def linearColor( c ):

	return c if isinstance( c, Color ) else _color.set( c ).clone()


def prepare( geo, opts = None ):
	"""Give `geo` the uniform attribute layout, an index and default values.

	Mirrors the JS `prepare( geo, { color = 0xffffff, rough = 0.5, metal = 0,
	pattern = 0, anim = 0, matrix = null } = {} )`; the second argument is a dict with
	those keys (any missing key takes the JS default).
	"""

	if opts is None:
		opts = {}

	color = opts.get( 'color', 0xffffff )
	rough = opts.get( 'rough', 0.5 )
	metal = opts.get( 'metal', 0 )
	pattern = opts.get( 'pattern', 0 )
	anim = opts.get( 'anim', 0 )
	matrix = opts.get( 'matrix', None )

	for name in list( geo.attributes.keys() ):
		if name not in KEEP:
			geo.deleteAttribute( name )

	geo.morphAttributes = {}
	geo.clearGroups()

	n = geo.attributes[ 'position' ].count

	if not geo.index:

		idx = list( range( n ) )
		geo.setIndex( idx )

	if 'normal' not in geo.attributes:
		geo.computeVertexNormals()

	if 'uv' not in geo.attributes:
		geo.setAttribute( 'uv', Float32BufferAttribute( Float32Array( n * 2 ), 2 ) )

	if 'color' not in geo.attributes:

		c = linearColor( color )
		arr = Float32Array( n * 3 )

		for i in range( n ):

			arr[ i * 3 ] = c.r; arr[ i * 3 + 1 ] = c.g; arr[ i * 3 + 2 ] = c.b

		geo.setAttribute( 'color', Float32BufferAttribute( arr, 3 ) )

	if 'aux' not in geo.attributes:

		arr = Float32Array( n * 4 )

		for i in range( n ):

			arr[ i * 4 ] = rough; arr[ i * 4 + 1 ] = metal; arr[ i * 4 + 2 ] = pattern; arr[ i * 4 + 3 ] = anim

		geo.setAttribute( 'aux', Float32BufferAttribute( arr, 4 ) )

	# uniform float layout for merging
	for name in list( geo.attributes.keys() ):

		a = geo.attributes[ name ]

		if not isinstance( a.array, Float32Array ) or a.isInterleavedBufferAttribute:

			geo.setAttribute( name, Float32BufferAttribute( Float32Array( [ a.array[ i ] for i in range( a.count * a.itemSize ) ] ), a.itemSize ) )

	if matrix:
		geo.applyMatrix4( matrix )

	return geo


class GeoKit:
	"""Collects geometries per material bucket and merges them."""

	def __init__( self ):

		self.buckets = {}

	def add( self, bucket, geometry, opts = None ):

		g = prepare( geometry, opts )

		if bucket not in self.buckets:
			self.buckets[ bucket ] = []

		self.buckets[ bucket ].append( g )
		return g

	def merged( self, bucket ):

		list_ = self.buckets.get( bucket )

		if not list_ or len( list_ ) == 0:
			return None

		g = list_[ 0 ] if len( list_ ) == 1 else mergeGeometries( list_, False )

		if g is None:
			raise ValueError( 'BoatModel: failed to merge bucket ' + str( bucket ) )

		g.computeBoundingBox()
		g.computeBoundingSphere()
		return g


def mergePrepared( list_ ):
	"""Merge a list of prepared geometries into one (for animated parts)."""

	g = list_[ 0 ] if len( list_ ) == 1 else mergeGeometries( list_, False )
	g.computeBoundingBox()
	g.computeBoundingSphere()
	return g


# ------------------------------------------------------------------ primitives

def box( w, h, d ):
	"""Box with UVs in meters."""

	g = BoxGeometry( w, h, d )
	uv = g.attributes[ 'uv' ]
	# face order: px, nx, py, ny, pz, nz (4 vertices each)
	dims = [ [ d, h ], [ d, h ], [ w, d ], [ w, d ], [ w, h ], [ w, h ] ]

	for f in range( 6 ):

		for i in range( 4 ):

			k = f * 4 + i
			uv.setXY( k, uv.getX( k ) * dims[ f ][ 0 ], uv.getY( k ) * dims[ f ][ 1 ] )

	return g


def roundedBox( w, h, d, radius = 0.02, segments = 2 ):

	g = RoundedBoxGeometry( w, h, d, segments, min( radius, w / 2 - 1e-4, h / 2 - 1e-4, d / 2 - 1e-4 ) )
	uv = g.attributes[ 'uv' ]

	for i in range( uv.count ):
		uv.setXY( i, uv.getX( i ) * max( w, d ), uv.getY( i ) * h )

	return g


def cylinder( rTop, rBottom, h, radial = 12, heightSegs = 1, open = False, thetaStart = 0, thetaLength = math.pi * 2 ):

	g = CylinderGeometry( rTop, rBottom, h, radial, heightSegs, open, thetaStart, thetaLength )
	uv = g.attributes[ 'uv' ]
	circ = max( rTop, rBottom ) * thetaLength

	for i in range( uv.count ):
		uv.setXY( i, uv.getX( i ) * circ, uv.getY( i ) * h )

	return g


def rod( a, b, radius, radial = 8, rEnd = None ):
	"""Cylinder between two points."""

	if rEnd is None:
		rEnd = radius

	dir = Vector3().subVectors( b, a )
	len_ = dir.length()
	g = cylinder( rEnd, radius, len_, radial, 1, False )
	g.applyMatrix4( alignY( Vector3().addVectors( a, b ).multiplyScalar( 0.5 ), dir ) )
	return g


def sphere( r, w = 12, h = 8, phiStart = 0, phiLength = math.pi * 2, thetaStart = 0, thetaLength = math.pi ):

	return SphereGeometry( r, w, h, phiStart, phiLength, thetaStart, thetaLength )


def torus( R, r, radial = 8, tubular = 24, arc = math.pi * 2 ):

	g = TorusGeometry( R, r, radial, tubular, arc )
	uv = g.attributes[ 'uv' ]

	for i in range( uv.count ):
		uv.setXY( i, uv.getX( i ) * R * arc, uv.getY( i ) * 2 * math.pi * r )

	return g


def lathe( profile, segments = 16 ):
	"""Lathe around +Y from [ [r, y], ... ] (bottom to top)."""

	pts = [ Vector2( max( 0, p[ 0 ] ), p[ 1 ] ) for p in profile ]
	g = LatheGeometry( pts, segments )
	# three's lathe winds so normals face outward for bottom-to-top profiles
	return g


def tube( points, radius, tubular = 32, radial = 6, closed = False, tension = 0.5 ):

	curve = CatmullRomCurve3( points, closed, 'catmullrom', tension )
	g = TubeGeometry( curve, tubular, radius, radial, closed )
	len_ = curve.getLength()
	uv = g.attributes[ 'uv' ]

	for i in range( uv.count ):
		uv.setXY( i, uv.getX( i ) * len_, uv.getY( i ) )

	return g


def gridSurface( rows, opts = None ):
	"""Indexed surface from rows[i][j] (Vector3). `flip` reverses the winding.

	UVs: u = distance along i (first column), v = distance along j (first row).
	Options dict mirrors the JS `{ flip = false, uvFn = null, closeJ = false }`.
	"""

	if opts is None:
		opts = {}

	flip = opts.get( 'flip', False )
	uvFn = opts.get( 'uvFn', None )
	closeJ = opts.get( 'closeJ', False )

	ni = len( rows ); nj = len( rows[ 0 ] )
	pos = Float32Array( ni * nj * 3 )
	uvs = Float32Array( ni * nj * 2 )
	u = 0

	for i in range( ni ):

		if i > 0:
			u += rows[ i ][ 0 ].distanceTo( rows[ i - 1 ][ 0 ] )

		v = 0

		for j in range( nj ):

			p = rows[ i ][ j ]

			if j > 0:
				v += p.distanceTo( rows[ i ][ j - 1 ] )

			k = i * nj + j
			pos[ k * 3 ] = p.x; pos[ k * 3 + 1 ] = p.y; pos[ k * 3 + 2 ] = p.z

			if uvFn:

				t = uvFn( p, i, j, u, v )
				uvs[ k * 2 ] = t[ 0 ]; uvs[ k * 2 + 1 ] = t[ 1 ]

			else:

				uvs[ k * 2 ] = u; uvs[ k * 2 + 1 ] = v

	idx = []
	jmax = nj if closeJ else nj - 1

	for i in range( ni - 1 ):

		for j in range( jmax ):

			j1 = ( j + 1 ) % nj
			a = i * nj + j; b = ( i + 1 ) * nj + j; c = ( i + 1 ) * nj + j1; d = i * nj + j1

			if flip:
				idx.extend( ( a, b, d, b, c, d ) )
			else:
				idx.extend( ( a, d, b, d, c, b ) )

	g = BufferGeometry()
	g.setAttribute( 'position', Float32BufferAttribute( pos, 3 ) )
	g.setAttribute( 'uv', Float32BufferAttribute( uvs, 2 ) )
	g.setIndex( idx )
	g.computeVertexNormals()
	return g


def fanCap( loop, normalHint ):
	"""Flat polygon (Vector3 loop, assumed planar-ish and convex-ish) as a fan from its
	centroid.
	"""

	c = Vector3()

	for p in loop:
		c.add( p )

	c.divideScalar( len( loop ) )
	pos = [ c.x, c.y, c.z ]

	for p in loop:
		pos.extend( ( p.x, p.y, p.z ) )

	idx = []

	for i in range( len( loop ) ):
		idx.extend( ( 0, 1 + i, 1 + ( ( i + 1 ) % len( loop ) ) ) )

	g = BufferGeometry()
	g.setAttribute( 'position', Float32BufferAttribute( pos, 3 ) )
	g.setIndex( idx )
	orientTowards( g, normalHint )
	g.computeVertexNormals()
	planarUV( g, normalHint )
	return g


def orientTowards( g, dir ):
	"""Flip all triangles if their average normal disagrees with `dir`."""

	p = g.attributes[ 'position' ]; idx = g.index.array
	a = Vector3(); b = Vector3(); c = Vector3(); n = Vector3(); acc = Vector3()

	for i in range( 0, len( idx ), 3 ):

		a.fromBufferAttribute( p, idx[ i ] ); b.fromBufferAttribute( p, idx[ i + 1 ] ); c.fromBufferAttribute( p, idx[ i + 2 ] )
		n.subVectors( b, a ).cross( c.sub( a ) )
		acc.add( n )

	if acc.dot( dir ) < 0:

		arr = list( idx )

		for i in range( 0, len( arr ), 3 ):

			t = arr[ i + 1 ]; arr[ i + 1 ] = arr[ i + 2 ]; arr[ i + 2 ] = t

		g.setIndex( arr )

	return g


def planarUV( g, normal ):
	"""Planar projection UVs (meters) perpendicular to `normal`."""

	n = normal.clone().normalize()
	t = Vector3( 1, 0, 0 ) if abs( n.y ) > 0.9 else Vector3( 0, 1, 0 ).cross( n ).normalize()
	b = Vector3().crossVectors( n, t )
	p = g.attributes[ 'position' ]
	uv = Float32Array( p.count * 2 )
	v = Vector3()

	for i in range( p.count ):

		v.fromBufferAttribute( p, i )
		uv[ i * 2 ] = v.dot( t ); uv[ i * 2 + 1 ] = v.dot( b )

	g.setAttribute( 'uv', Float32BufferAttribute( uv, 2 ) )
	return g


def slab( outline, holes, map, opts = None ):
	"""Solid panel from a 2D outline (u, v in meters) with optional holes.

	map( u, v, side ) -> Vector3 gives the point on the front (side 0) or back (side 1)
	face. Faces are oriented automatically (front faces away from the back face, edges
	outward). Options dict mirrors `{ edges = true, back = true, front = true }`.
	"""

	if opts is None:
		opts = {}

	edges = opts.get( 'edges', True )
	back = opts.get( 'back', True )
	front = opts.get( 'front', True )

	contour = [ Vector2( p[ 0 ], p[ 1 ] ) for p in outline ]
	holeVs = [ [ Vector2( p[ 0 ], p[ 1 ] ) for p in h ] for h in holes ]

	if ShapeUtils.isClockWise( contour ):
		contour.reverse()

	for h in holeVs:
		if not ShapeUtils.isClockWise( h ):
			h.reverse()

	tris = ShapeUtils.triangulateShape( contour, holeVs )
	all_ = list( contour )

	for h in holeVs:
		all_ += h

	parts = []

	def faceGeo( side ):

		pos = []; uvs = []

		for p in all_:

			q = map( p.x, p.y, side )
			pos.extend( ( q.x, q.y, q.z ) )
			uvs.extend( ( p.x, p.y ) )

		# drop zero-area triangles (earcut emits them for collinear outline points)
		idx = []

		for t in tris:

			a = all_[ t[ 0 ] ]; b = all_[ t[ 1 ] ]; c = all_[ t[ 2 ] ]

			if abs( ( b.x - a.x ) * ( c.y - a.y ) - ( c.x - a.x ) * ( b.y - a.y ) ) > 1e-10:
				idx.extend( ( t[ 0 ], t[ 1 ], t[ 2 ] ) )

		g = BufferGeometry()
		g.setAttribute( 'position', Float32BufferAttribute( pos, 3 ) )
		g.setAttribute( 'uv', Float32BufferAttribute( uvs, 2 ) )
		g.setIndex( idx )
		# orient: front normal points from back face toward front face
		c = contour[ 0 ]
		dir = map( c.x, c.y, side ).sub( map( c.x, c.y, 1 - side ) )
		orientTowards( g, dir )
		g.computeVertexNormals()
		# vertices left without triangles get the face direction
		nrm = g.attributes[ 'normal' ]
		dir.normalize()

		for i in range( nrm.count ):

			if js_hypot( nrm.getX( i ), nrm.getY( i ), nrm.getZ( i ) ) < 0.5:
				nrm.setXYZ( i, dir.x, dir.y, dir.z )

		return g

	if front:
		parts.append( faceGeo( 0 ) )

	if back:
		parts.append( faceGeo( 1 ) )

	if edges:

		pos = []; uvs = []; idx = []
		loops = [ contour ] + holeVs
		a0 = Vector3(); a1 = Vector3(); b0 = Vector3(); b1 = Vector3()
		n = Vector3(); out = Vector3()

		for loop in loops:

			len_ = 0

			for i in range( len( loop ) ):

				p = loop[ i ]; q = loop[ ( i + 1 ) % len( loop ) ]
				el = p.distanceTo( q )

				if el < 1e-6:
					continue

				a0.copy( map( p.x, p.y, 0 ) ); a1.copy( map( p.x, p.y, 1 ) )
				b0.copy( map( q.x, q.y, 0 ) ); b1.copy( map( q.x, q.y, 1 ) )
				th = a0.distanceTo( a1 )
				base = len( pos ) // 3
				pos.extend( ( a0.x, a0.y, a0.z, b0.x, b0.y, b0.z, b1.x, b1.y, b1.z, a1.x, a1.y, a1.z ) )
				uvs.extend( ( len_, 0, len_ + el, 0, len_ + el, th, len_, th ) )
				len_ += el
				# outward direction: right-hand side of the edge for CCW outline / CW holes
				mx = ( p.x + q.x ) / 2; my = ( p.y + q.y ) / 2
				ox = ( q.y - p.y ) / el * 0.01; oy = - ( q.x - p.x ) / el * 0.01
				out.copy( map( mx + ox, my + oy, 0 ) ).sub( map( mx, my, 0 ) )
				n.subVectors( b0, a0 ).cross( _v.subVectors( a1, a0 ) )

				if n.dot( out ) >= 0:
					idx.extend( ( base, base + 1, base + 2, base, base + 2, base + 3 ) )
				else:
					idx.extend( ( base, base + 2, base + 1, base, base + 3, base + 2 ) )

		if pos:

			g = BufferGeometry()
			g.setAttribute( 'position', Float32BufferAttribute( pos, 3 ) )
			g.setAttribute( 'uv', Float32BufferAttribute( uvs, 2 ) )
			g.setIndex( idx )
			g.computeVertexNormals()
			parts.append( g )

	return parts[ 0 ] if len( parts ) == 1 else mergeGeometries( parts, False )


def loft( profiles, opts = None ):
	"""Loft of profile loops/strips: profiles[i] = array of Vector3 (same length).

	`closed` joins the last profile point to the first (duplicating the seam so UVs stay
	continuous; seam normals are averaged). Options dict mirrors `{ closed = false,
	flip = false }`.
	"""

	if opts is None:
		opts = {}

	closed = opts.get( 'closed', False )
	flip = opts.get( 'flip', False )

	rows = [ list( r ) + [ r[ 0 ].clone() ] for r in profiles ] if closed else profiles
	g = gridSurface( rows, { 'flip': flip } )

	if closed:

		nj = len( rows[ 0 ] )
		nrm = g.attributes[ 'normal' ]
		a = Vector3(); b = Vector3()

		for i in range( len( rows ) ):

			i0 = i * nj; i1 = i * nj + nj - 1
			a.fromBufferAttribute( nrm, i0 ); b.fromBufferAttribute( nrm, i1 )
			a.add( b ).normalize()
			nrm.setXYZ( i0, a.x, a.y, a.z ); nrm.setXYZ( i1, a.x, a.y, a.z )

	return g


def paintVertices( g, fn ):
	"""Assign per-vertex colors with a function of the vertex position."""

	p = g.attributes[ 'position' ]
	arr = Float32Array( p.count * 3 )
	v = Vector3()

	for i in range( p.count ):

		v.fromBufferAttribute( p, i )
		c = linearColor( fn( v, i ) )
		arr[ i * 3 ] = c.r; arr[ i * 3 + 1 ] = c.g; arr[ i * 3 + 2 ] = c.b

	g.setAttribute( 'color', Float32BufferAttribute( arr, 3 ) )
	return g


def auxVertices( g, fn ):
	"""Per-vertex aux attribute with a function returning [rough, metal, pattern, anim]."""

	p = g.attributes[ 'position' ]
	arr = Float32Array( p.count * 4 )
	v = Vector3()

	for i in range( p.count ):

		v.fromBufferAttribute( p, i )
		a = fn( v, i )
		arr[ i * 4 ] = a[ 0 ]; arr[ i * 4 + 1 ] = a[ 1 ]; arr[ i * 4 + 2 ] = a[ 2 ]; arr[ i * 4 + 3 ] = a[ 3 ]

	g.setAttribute( 'aux', Float32BufferAttribute( arr, 4 ) )
	return g


def triangleCount( g ):

	return g.index.count / 3 if g.index else g.attributes[ 'position' ].count / 3
