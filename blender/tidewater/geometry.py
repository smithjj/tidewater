# The engine's buffer geometries, ported for the pure-Python boat builder.
#
# This is src/engine/geometry (which mirrors three.js): the attribute / index
# containers, the parametric primitives GeoKit builds on, TubeGeometry, the custom
# RoundedBoxGeometry, and the merge helpers GeoKit uses for its material buckets.
# Only the classes GeoKit and HullLines touch are here -- PlaneGeometry, ConeGeometry,
# CircleGeometry, PolyhedronGeometry and the instanced/interleaved machinery are not.
#
# Vertex order, index winding and UV conventions are copied exactly: the whole point of
# the port is that the vertex / index counts and bounding boxes come out identical.
#
# Pure Python 3, standard library only (no numpy, no bpy).

import math
import sys

from mathutil import Float32Array, Matrix3, Matrix4, Vector2, Vector3


def arrayNeedsUint32( a ):
	"""True when any index needs more than 16 bits (three uses Uint16 up to 65534)."""

	for i in range( len( a ) - 1, - 1, - 1 ):
		if a[ i ] >= 65535:
			return True

	return False


# ------------------------------------------------------------------ attributes

class BufferAttribute:
	"""Vertex attribute storage (three.js BufferAttribute-compatible subset).

	`array` is the backing sequence: a Float32Array for float attributes (so every store
	rounds to float32, exactly like JS) or a plain list of ints for index attributes.
	"""

	def __init__( self, array, itemSize, normalized = False ):

		self.name = ''
		self.array = array
		self.itemSize = itemSize
		self.count = len( array ) // itemSize if array is not None else 0
		self.normalized = normalized
		self.version = 0
		self.isInterleavedBufferAttribute = False

	@property
	def needsUpdate( self ):
		return False

	@needsUpdate.setter
	def needsUpdate( self, v ):
		"""Setting `needsUpdate = True` bumps the version (three.js upload accounting)."""

		if v is True:
			self.version += 1

	def _idx( self, i, c ):
		return i * self.itemSize + c

	def getComponent( self, i, c ):
		return self.array[ self._idx( i, c ) ]

	def setComponent( self, i, c, v ):
		self.array[ self._idx( i, c ) ] = v
		return self

	def getX( self, i ): return self.getComponent( i, 0 )

	def getY( self, i ): return self.getComponent( i, 1 )

	def getZ( self, i ): return self.getComponent( i, 2 )

	def getW( self, i ): return self.getComponent( i, 3 )

	def setX( self, i, x ): return self.setComponent( i, 0, x )

	def setY( self, i, y ): return self.setComponent( i, 1, y )

	def setZ( self, i, z ): return self.setComponent( i, 2, z )

	def setW( self, i, w ): return self.setComponent( i, 3, w )

	def setXY( self, i, x, y ):
		self.setComponent( i, 0, x )
		return self.setComponent( i, 1, y )

	def setXYZ( self, i, x, y, z ):
		self.setComponent( i, 0, x )
		self.setComponent( i, 1, y )
		return self.setComponent( i, 2, z )

	def setXYZW( self, i, x, y, z, w ):
		self.setComponent( i, 0, x )
		self.setComponent( i, 1, y )
		self.setComponent( i, 2, z )
		return self.setComponent( i, 3, w )

	def applyMatrix4( self, m ):

		_v = Vector3()

		for i in range( self.count ):
			_v.fromBufferAttribute( self, i ).applyMatrix4( m )
			self.setXYZ( i, _v.x, _v.y, _v.z )

		return self

	def applyNormalMatrix( self, m ):

		_v = Vector3()

		for i in range( self.count ):
			_v.fromBufferAttribute( self, i ).applyNormalMatrix( m )
			self.setXYZ( i, _v.x, _v.y, _v.z )

		return self

	def transformDirection( self, m ):

		_v = Vector3()

		for i in range( self.count ):
			_v.fromBufferAttribute( self, i ).transformDirection( m )
			self.setXYZ( i, _v.x, _v.y, _v.z )

		return self

	def copy( self, src ):

		self.name = src.name
		self.array = type( src.array )( src.array )
		self.itemSize = src.itemSize
		self.count = src.count
		self.normalized = src.normalized
		return self

	def copyAt( self, i1, attr, i2 ):

		s = self.itemSize
		i1 *= s; i2 *= attr.itemSize

		for i in range( s ):
			self.array[ i1 + i ] = attr.array[ i2 + i ]

		return self

	def copyArray( self, a ):
		self.array.set( a )
		return self

	def set( self, value, offset = 0 ):
		self.array.set( value, offset )
		return self

	def clone( self ):
		return type( self )( self.array, self.itemSize ).copy( self )


class Float32BufferAttribute( BufferAttribute ):
	"""Float attribute; the constructor wraps any sequence in a Float32Array, like JS."""

	def __init__( self, array, itemSize, normalized = False ):

		super().__init__( array if isinstance( array, Float32Array ) else Float32Array( array ), itemSize, normalized )


class Uint16BufferAttribute( BufferAttribute ):

	def __init__( self, array, itemSize, normalized = False ):

		super().__init__( [ int( v ) for v in array ], itemSize, normalized )


class Uint32BufferAttribute( BufferAttribute ):

	def __init__( self, array, itemSize, normalized = False ):

		super().__init__( [ int( v ) for v in array ], itemSize, normalized )


# ------------------------------------------------------------------ bounds

class Box3:
	"""Axis-aligned bounding box (three.js Box3-compatible subset)."""

	def __init__( self, min_ = None, max_ = None ):

		self.min = min_ if min_ is not None else Vector3( math.inf, math.inf, math.inf )
		self.max = max_ if max_ is not None else Vector3( - math.inf, - math.inf, - math.inf )

	def set( self, min_, max_ ):

		self.min.copy( min_ ); self.max.copy( max_ )
		return self

	def makeEmpty( self ):

		self.min.x = self.min.y = self.min.z = math.inf
		self.max.x = self.max.y = self.max.z = - math.inf
		return self

	def isEmpty( self ):
		return self.max.x < self.min.x or self.max.y < self.min.y or self.max.z < self.min.z

	def getCenter( self, t ):

		if self.isEmpty():
			return t.set( 0, 0, 0 )

		return t.addVectors( self.min, self.max ).multiplyScalar( 0.5 )

	def getSize( self, t ):

		if self.isEmpty():
			return t.set( 0, 0, 0 )

		return t.subVectors( self.max, self.min )

	def expandByPoint( self, p ):

		self.min.min( p ); self.max.max( p )
		return self

	def setFromBufferAttribute( self, attr ):

		self.makeEmpty()
		_v = Vector3()

		for i in range( attr.count ):
			self.expandByPoint( _v.fromBufferAttribute( attr, i ) )

		return self

	def setFromPoints( self, pts ):

		self.makeEmpty()

		for p in pts:
			self.expandByPoint( p )

		return self

	def setFromArray( self, a ):

		self.makeEmpty()
		_v = Vector3()

		for i in range( 0, len( a ), 3 ):
			self.expandByPoint( _v.fromArray( a, i ) )

		return self

	def union( self, b ):

		self.min.min( b.min ); self.max.max( b.max )
		return self

	def applyMatrix4( self, m ):
		"""Transform the box: transform its 8 corners and re-fit (three.js does the same)."""

		if self.isEmpty():
			return self

		a = self.min; b = self.max
		pts = [
			Vector3( a.x, a.y, a.z ).applyMatrix4( m ),
			Vector3( a.x, a.y, b.z ).applyMatrix4( m ),
			Vector3( a.x, b.y, a.z ).applyMatrix4( m ),
			Vector3( a.x, b.y, b.z ).applyMatrix4( m ),
			Vector3( b.x, a.y, a.z ).applyMatrix4( m ),
			Vector3( b.x, a.y, b.z ).applyMatrix4( m ),
			Vector3( b.x, b.y, a.z ).applyMatrix4( m ),
			Vector3( b.x, b.y, b.z ).applyMatrix4( m ),
		]
		return self.setFromPoints( pts )

	def copy( self, b ):

		self.min.copy( b.min ); self.max.copy( b.max )
		return self

	def clone( self ):
		return Box3().copy( self )


class Sphere:
	"""Bounding sphere (only what BufferGeometry.computeBoundingSphere needs)."""

	def __init__( self, center = None, radius = - 1 ):

		self.center = center if center is not None else Vector3()
		self.radius = radius

	def makeEmpty( self ):

		self.center.set( 0, 0, 0 ); self.radius = - 1
		return self

	def set( self, center, radius ):

		self.center.copy( center ); self.radius = radius
		return self

	def copy( self, s ):

		self.center.copy( s.center ); self.radius = s.radius
		return self

	def clone( self ):
		return Sphere().copy( self )


# ------------------------------------------------------------------ BufferGeometry

class BufferGeometry:
	"""Indexed / non-indexed vertex data (three.js BufferGeometry-compatible subset)."""

	def __init__( self ):

		self.name = ''
		self.type = 'BufferGeometry'
		self.index = None
		self.attributes = {}
		self.morphAttributes = {}
		self.morphTargetsRelative = False
		self.groups = []
		self.boundingBox = None
		self.boundingSphere = None
		self.userData = {}

	def getIndex( self ):
		return self.index

	def setIndex( self, index ):

		if isinstance( index, list ):
			self.index = ( Uint32BufferAttribute if arrayNeedsUint32( index ) else Uint16BufferAttribute )( index, 1 )
		else:
			self.index = index

		return self

	def getAttribute( self, name ):
		return self.attributes.get( name )

	def setAttribute( self, name, attr ):

		self.attributes[ name ] = attr
		return self

	def deleteAttribute( self, name ):

		self.attributes.pop( name, None )
		return self

	def hasAttribute( self, name ):
		return name in self.attributes

	def addGroup( self, start, count, materialIndex = 0 ):

		self.groups.append( { 'start': start, 'count': count, 'materialIndex': materialIndex } )

	def clearGroups( self ):

		self.groups = []

	def applyMatrix4( self, m ):

		pos = self.attributes.get( 'position' )

		if pos is not None:
			pos.applyMatrix4( m ); pos.needsUpdate = True

		nrm = self.attributes.get( 'normal' )

		if nrm is not None:
			nrm.applyNormalMatrix( Matrix3().getNormalMatrix( m ) ); nrm.needsUpdate = True

		tan = self.attributes.get( 'tangent' )

		if tan is not None:
			tan.transformDirection( m ); tan.needsUpdate = True

		if self.boundingBox is not None:
			self.computeBoundingBox()

		if self.boundingSphere is not None:
			self.computeBoundingSphere()

		return self

	# placement helpers (three.js BufferGeometry.rotateX/rotateY/rotateZ/translate/scale):
	# added for the builders ported after HullLines / GeoKit, each is applyMatrix4 of the
	# matching Matrix4 constructor, exactly like src/engine/geometry/BufferGeometry.js.
	def rotateX( self, a ):
		return self.applyMatrix4( Matrix4().makeRotationX( a ) )

	def rotateY( self, a ):
		return self.applyMatrix4( Matrix4().makeRotationY( a ) )

	def rotateZ( self, a ):
		return self.applyMatrix4( Matrix4().makeRotationZ( a ) )

	def translate( self, x, y = None, z = None ):
		return self.applyMatrix4( Matrix4().makeTranslation( x, y, z ) )

	def scale( self, x, y, z ):
		return self.applyMatrix4( Matrix4().makeScale( x, y, z ) )

	def computeBoundingBox( self ):

		if self.boundingBox is None:
			self.boundingBox = Box3()

		pos = self.attributes.get( 'position' )

		if pos is None:
			self.boundingBox.makeEmpty()
			return

		self.boundingBox.setFromBufferAttribute( pos )

	def computeBoundingSphere( self ):

		if self.boundingSphere is None:
			self.boundingSphere = Sphere()

		pos = self.attributes.get( 'position' )

		if pos is None:
			self.boundingSphere.makeEmpty()
			return

		c = self.boundingSphere.center
		Box3().setFromBufferAttribute( pos ).getCenter( c )
		r2 = 0
		_v = Vector3()

		for i in range( pos.count ):
			r2 = max( r2, c.distanceToSquared( _v.fromBufferAttribute( pos, i ) ) )

		self.boundingSphere.radius = math.sqrt( r2 )

	# Area-weighted smooth normals (indexed) or flat face normals (non-indexed).
	def computeVertexNormals( self ):

		index = self.index
		pos = self.getAttribute( 'position' )

		if pos is None:
			return

		nrm = self.getAttribute( 'normal' )

		if nrm is None or nrm.count != pos.count:

			nrm = BufferAttribute( Float32Array( pos.count * 3 ), 3 )
			self.setAttribute( 'normal', nrm )

		else:

			for i in range( nrm.count ):
				nrm.setXYZ( i, 0, 0, 0 )

		pA = Vector3(); pB = Vector3(); pC = Vector3()
		cb = Vector3(); ab = Vector3()
		nA = Vector3(); nB = Vector3(); nC = Vector3()

		if index:

			for i in range( 0, index.count, 3 ):

				vA = index.getX( i ); vB = index.getX( i + 1 ); vC = index.getX( i + 2 )
				pA.fromBufferAttribute( pos, vA ); pB.fromBufferAttribute( pos, vB ); pC.fromBufferAttribute( pos, vC )
				cb.subVectors( pC, pB ); ab.subVectors( pA, pB ); cb.cross( ab )
				nA.fromBufferAttribute( nrm, vA ); nB.fromBufferAttribute( nrm, vB ); nC.fromBufferAttribute( nrm, vC )
				nA.add( cb ); nB.add( cb ); nC.add( cb )
				nrm.setXYZ( vA, nA.x, nA.y, nA.z ); nrm.setXYZ( vB, nB.x, nB.y, nB.z ); nrm.setXYZ( vC, nC.x, nC.y, nC.z )

		else:

			for i in range( 0, pos.count, 3 ):

				pA.fromBufferAttribute( pos, i ); pB.fromBufferAttribute( pos, i + 1 ); pC.fromBufferAttribute( pos, i + 2 )
				cb.subVectors( pC, pB ); ab.subVectors( pA, pB ); cb.cross( ab )
				nrm.setXYZ( i, cb.x, cb.y, cb.z ); nrm.setXYZ( i + 1, cb.x, cb.y, cb.z ); nrm.setXYZ( i + 2, cb.x, cb.y, cb.z )

		self.normalizeNormals()
		nrm.needsUpdate = True

	def normalizeNormals( self ):

		n = self.attributes[ 'normal' ]
		_v = Vector3()

		for i in range( n.count ):

			_v.fromBufferAttribute( n, i ).normalize()
			n.setXYZ( i, _v.x, _v.y, _v.z )

	def toNonIndexed( self ):
		"""Expand to flat per-triangle vertices (drops the index, keeps groups)."""

		if self.index is None:
			return self

		g = BufferGeometry()
		idx = self.index

		def expand( attr ):

			size = attr.itemSize
			out = type( attr.array )( idx.count * size )

			for i in range( idx.count ):

				s = idx.getX( i )

				for c in range( size ):
					out[ i * size + c ] = attr.array[ attr._idx( s, c ) ]

			return BufferAttribute( out, size, attr.normalized )

		for name in self.attributes:
			g.setAttribute( name, expand( self.attributes[ name ] ) )

		g.morphTargetsRelative = self.morphTargetsRelative

		for gr in self.groups:
			g.addGroup( gr[ 'start' ], gr[ 'count' ], gr[ 'materialIndex' ] )

		return g

	def copy( self, src ):

		self.index = None
		self.attributes = {}
		self.morphAttributes = {}
		self.groups = []
		self.boundingBox = None
		self.boundingSphere = None
		self.name = src.name

		if src.index is not None:
			self.setIndex( src.index.clone() )

		for name in src.attributes:
			self.setAttribute( name, src.attributes[ name ].clone() )

		self.morphTargetsRelative = src.morphTargetsRelative

		for gr in src.groups:
			self.addGroup( gr[ 'start' ], gr[ 'count' ], gr[ 'materialIndex' ] )

		if src.boundingBox is not None:
			self.boundingBox = src.boundingBox.clone()

		if src.boundingSphere is not None:
			self.boundingSphere = src.boundingSphere.clone()

		self.userData = src.userData
		return self

	def clone( self ):
		return BufferGeometry().copy( self )


def _finish( g, indices, vertices, normals, uvs ):
	"""Attribute order the generic primitives use: index, position, normal, uv."""

	if indices is not None:
		g.setIndex( indices )

	g.setAttribute( 'position', Float32BufferAttribute( vertices, 3 ) )
	g.setAttribute( 'normal', Float32BufferAttribute( normals, 3 ) )
	g.setAttribute( 'uv', Float32BufferAttribute( uvs, 2 ) )


# ------------------------------------------------------------------ primitives

class PlaneGeometry( BufferGeometry ):
	"""Flat quad in the XY plane, three.js vertex order and UVs.

	Added for the builders ported after HullLines / GeoKit (the wheelhouse walls, screens
	and labels use it); copied from src/engine/geometry/PrimitiveGeometries.js.
	"""

	def __init__( self, width = 1, height = 1, widthSegments = 1, heightSegments = 1 ):

		super().__init__()
		self.type = 'PlaneGeometry'
		self.parameters = { 'width': width, 'height': height, 'widthSegments': widthSegments, 'heightSegments': heightSegments }

		hw = width / 2; hh = height / 2
		gx = math.floor( widthSegments ); gy = math.floor( heightSegments )
		gx1 = gx + 1; gy1 = gy + 1
		sw = width / gx; sh = height / gy
		indices = []; vertices = []; normals = []; uvs = []

		for iy in range( gy1 ):

			y = iy * sh - hh

			for ix in range( gx1 ):

				vertices.extend( ( ix * sw - hw, - y, 0 ) )
				normals.extend( ( 0, 0, 1 ) )
				uvs.extend( ( ix / gx, 1 - ( iy / gy ) ) )

		for iy in range( gy ):

			for ix in range( gx ):

				a = ix + gx1 * iy; b = ix + gx1 * ( iy + 1 ); c = ( ix + 1 ) + gx1 * ( iy + 1 ); d = ( ix + 1 ) + gx1 * iy
				indices.extend( ( a, b, d, b, c, d ) )

		_finish( self, indices, vertices, normals, uvs )


class CircleGeometry( BufferGeometry ):
	"""Disc in the XY plane as a triangle fan from the centre, three.js vertex order.

	Added for the builders ported after HullLines / GeoKit (gauges, coffee, spotlight
	lenses); copied from src/engine/geometry/PrimitiveGeometries.js.
	"""

	def __init__( self, radius = 1, segments = 32, thetaStart = 0, thetaLength = math.pi * 2 ):

		super().__init__()
		self.type = 'CircleGeometry'
		self.parameters = { 'radius': radius, 'segments': segments, 'thetaStart': thetaStart, 'thetaLength': thetaLength }

		segments = max( 3, segments )
		indices = []; vertices = [ 0, 0, 0 ]; normals = [ 0, 0, 1 ]; uvs = [ 0.5, 0.5 ]

		for s in range( segments + 1 ):

			a = thetaStart + s / segments * thetaLength
			x = radius * math.cos( a ); y = radius * math.sin( a )
			vertices.extend( ( x, y, 0 ) )
			normals.extend( ( 0, 0, 1 ) )
			uvs.extend( ( ( x / radius + 1 ) / 2, ( y / radius + 1 ) / 2 ) )

		for i in range( 1, segments + 1 ):
			indices.extend( ( i, i + 1, 0 ) )

		_finish( self, indices, vertices, normals, uvs )


class BoxGeometry( BufferGeometry ):
	"""Box with per-face UVs and material groups, three.js vertex order."""

	def __init__( self, width = 1, height = 1, depth = 1, widthSegments = 1, heightSegments = 1, depthSegments = 1 ):

		super().__init__()
		self.type = 'BoxGeometry'
		self.parameters = { 'width': width, 'height': height, 'depth': depth, 'widthSegments': widthSegments, 'heightSegments': heightSegments, 'depthSegments': depthSegments }

		widthSegments = math.floor( widthSegments )
		heightSegments = math.floor( heightSegments )
		depthSegments = math.floor( depthSegments )

		indices = []; vertices = []; normals = []; uvs = []
		state = { 'numberOfVertices': 0, 'groupStart': 0 }
		vec = Vector3()

		def plane( u, v, w, udir, vdir, pw, ph, pd, gx, gy, materialIndex ):

			sw = pw / gx; sh = ph / gy; hw = pw / 2; hh = ph / 2; hd = pd / 2
			gx1 = gx + 1; gy1 = gy + 1
			count = 0; groupCount = 0

			for iy in range( gy1 ):

				y = iy * sh - hh

				for ix in range( gx1 ):

					x = ix * sw - hw
					vec[ u ] = x * udir; vec[ v ] = y * vdir; vec[ w ] = hd
					vertices.append( vec.x ); vertices.append( vec.y ); vertices.append( vec.z )
					vec[ u ] = 0; vec[ v ] = 0; vec[ w ] = 1 if pd > 0 else - 1
					normals.append( vec.x ); normals.append( vec.y ); normals.append( vec.z )
					uvs.append( ix / gx ); uvs.append( 1 - ( iy / gy ) )
					count += 1

			for iy in range( gy ):

				for ix in range( gx ):

					n = state[ 'numberOfVertices' ]
					a = n + ix + gx1 * iy; b = n + ix + gx1 * ( iy + 1 ); c = n + ( ix + 1 ) + gx1 * ( iy + 1 ); d = n + ( ix + 1 ) + gx1 * iy
					indices.extend( ( a, b, d, b, c, d ) )
					groupCount += 6

			self.addGroup( state[ 'groupStart' ], groupCount, materialIndex )
			state[ 'groupStart' ] += groupCount
			state[ 'numberOfVertices' ] += count

		plane( 'z', 'y', 'x', - 1, - 1, depth, height, width, depthSegments, heightSegments, 0 ) # px
		plane( 'z', 'y', 'x', 1, - 1, depth, height, - width, depthSegments, heightSegments, 1 ) # nx
		plane( 'x', 'z', 'y', 1, 1, width, depth, height, widthSegments, depthSegments, 2 ) # py
		plane( 'x', 'z', 'y', 1, - 1, width, depth, - height, widthSegments, depthSegments, 3 ) # ny
		plane( 'x', 'y', 'z', 1, - 1, width, height, depth, widthSegments, heightSegments, 4 ) # pz
		plane( 'x', 'y', 'z', - 1, - 1, width, height, - depth, widthSegments, heightSegments, 5 ) # nz

		_finish( self, indices, vertices, normals, uvs )


class SphereGeometry( BufferGeometry ):

	def __init__( self, radius = 1, widthSegments = 32, heightSegments = 16, phiStart = 0, phiLength = math.pi * 2, thetaStart = 0, thetaLength = math.pi ):

		super().__init__()
		self.type = 'SphereGeometry'
		self.parameters = { 'radius': radius, 'widthSegments': widthSegments, 'heightSegments': heightSegments, 'phiStart': phiStart, 'phiLength': phiLength, 'thetaStart': thetaStart, 'thetaLength': thetaLength }

		widthSegments = max( 3, math.floor( widthSegments ) )
		heightSegments = max( 2, math.floor( heightSegments ) )
		thetaEnd = min( thetaStart + thetaLength, math.pi )
		index = 0
		grid = []; indices = []; vertices = []; normals = []; uvs = []
		v3 = Vector3()

		for iy in range( heightSegments + 1 ):

			row = []; v = iy / heightSegments
			uOffset = 0

			if iy == 0 and thetaStart == 0:
				uOffset = 0.5 / widthSegments
			elif iy == heightSegments and thetaEnd == math.pi:
				uOffset = - 0.5 / widthSegments

			for ix in range( widthSegments + 1 ):

				u = ix / widthSegments
				st = math.sin( thetaStart + v * thetaLength )
				v3.set(
					- radius * math.cos( phiStart + u * phiLength ) * st,
					radius * math.cos( thetaStart + v * thetaLength ),
					radius * math.sin( phiStart + u * phiLength ) * st
				)
				vertices.append( v3.x ); vertices.append( v3.y ); vertices.append( v3.z )
				v3.normalize()
				normals.append( v3.x ); normals.append( v3.y ); normals.append( v3.z )
				uvs.append( u + uOffset ); uvs.append( 1 - v )
				row.append( index )
				index += 1

			grid.append( row )

		for iy in range( heightSegments ):

			for ix in range( widthSegments ):

				a = grid[ iy ][ ix + 1 ]; b = grid[ iy ][ ix ]; c = grid[ iy + 1 ][ ix ]; d = grid[ iy + 1 ][ ix + 1 ]

				if iy != 0 or thetaStart > 0:
					indices.extend( ( a, b, d ) )

				if iy != heightSegments - 1 or thetaEnd < math.pi:
					indices.extend( ( b, c, d ) )

		_finish( self, indices, vertices, normals, uvs )


class CylinderGeometry( BufferGeometry ):

	def __init__( self, radiusTop = 1, radiusBottom = 1, height = 1, radialSegments = 32, heightSegments = 1, openEnded = False, thetaStart = 0, thetaLength = math.pi * 2 ):

		super().__init__()
		self.type = 'CylinderGeometry'
		self.parameters = { 'radiusTop': radiusTop, 'radiusBottom': radiusBottom, 'height': height, 'radialSegments': radialSegments, 'heightSegments': heightSegments, 'openEnded': openEnded, 'thetaStart': thetaStart, 'thetaLength': thetaLength }

		radialSegments = math.floor( radialSegments )
		heightSegments = math.floor( heightSegments )
		indices = []; vertices = []; normals = []; uvs = []
		indexArray = []; halfHeight = height / 2
		# vertex cursor and group cursor live in a dict so the nested cap() can advance them
		state = { 'index': 0, 'groupStart': 0 }
		n = Vector3()

		# torso
		groupCount = 0
		slope = ( radiusBottom - radiusTop ) / height

		for y in range( heightSegments + 1 ):

			row = []; v = y / heightSegments
			radius = v * ( radiusBottom - radiusTop ) + radiusTop

			for x in range( radialSegments + 1 ):

				u = x / radialSegments; theta = u * thetaLength + thetaStart
				s = math.sin( theta ); c = math.cos( theta )
				vertices.extend( ( radius * s, - v * height + halfHeight, radius * c ) )
				n.set( s, slope, c ).normalize()
				normals.extend( ( n.x, n.y, n.z ) )
				uvs.extend( ( u, 1 - v ) )
				row.append( state[ 'index' ] )
				state[ 'index' ] += 1

			indexArray.append( row )

		for x in range( radialSegments ):

			for y in range( heightSegments ):

				a = indexArray[ y ][ x ]; b = indexArray[ y + 1 ][ x ]; c = indexArray[ y + 1 ][ x + 1 ]; d = indexArray[ y ][ x + 1 ]

				if radiusTop > 0 or y != 0:
					indices.extend( ( a, b, d ) ); groupCount += 3

				if radiusBottom > 0 or y != heightSegments - 1:
					indices.extend( ( b, c, d ) ); groupCount += 3

		self.addGroup( state[ 'groupStart' ], groupCount, 0 )
		state[ 'groupStart' ] += groupCount

		def cap( top ):

			centerStart = state[ 'index' ]
			radius = radiusTop if top is True else radiusBottom
			sign = 1 if top is True else - 1
			groupCount = 0

			for x in range( 1, radialSegments + 1 ):

				vertices.extend( ( 0, halfHeight * sign, 0 ) )
				normals.extend( ( 0, sign, 0 ) )
				uvs.extend( ( 0.5, 0.5 ) )
				state[ 'index' ] += 1

			centerEnd = state[ 'index' ]

			for x in range( radialSegments + 1 ):

				u = x / radialSegments; theta = u * thetaLength + thetaStart
				c = math.cos( theta ); s = math.sin( theta )
				vertices.extend( ( radius * s, halfHeight * sign, radius * c ) )
				normals.extend( ( 0, sign, 0 ) )
				uvs.extend( ( ( c * 0.5 ) + 0.5, ( s * 0.5 * sign ) + 0.5 ) )
				state[ 'index' ] += 1

			for x in range( radialSegments ):

				c = centerStart + x; i = centerEnd + x

				if top is True:
					indices.extend( ( i, i + 1, c ) )
				else:
					indices.extend( ( i + 1, i, c ) )

				groupCount += 3

			self.addGroup( state[ 'groupStart' ], groupCount, 1 if top is True else 2 )
			state[ 'groupStart' ] += groupCount

		if openEnded is False:

			if radiusTop > 0:
				cap( True )

			if radiusBottom > 0:
				cap( False )

		_finish( self, indices, vertices, normals, uvs )


class ConeGeometry( CylinderGeometry ):
	"""A cylinder tapering to a point at the top (src/engine/geometry/PrimitiveGeometries.js)."""

	def __init__( self, radius = 1, height = 1, radialSegments = 32, heightSegments = 1, openEnded = False, thetaStart = 0, thetaLength = math.pi * 2 ):

		super().__init__( 0, radius, height, radialSegments, heightSegments, openEnded, thetaStart, thetaLength )
		self.type = 'ConeGeometry'
		self.parameters = { 'radius': radius, 'height': height, 'radialSegments': radialSegments, 'heightSegments': heightSegments, 'openEnded': openEnded, 'thetaStart': thetaStart, 'thetaLength': thetaLength }


class TorusGeometry( BufferGeometry ):

	def __init__( self, radius = 1, tube = 0.4, radialSegments = 12, tubularSegments = 48, arc = math.pi * 2, thetaStart = 0, thetaLength = math.pi * 2 ):

		super().__init__()
		self.type = 'TorusGeometry'
		self.parameters = { 'radius': radius, 'tube': tube, 'radialSegments': radialSegments, 'tubularSegments': tubularSegments, 'arc': arc, 'thetaStart': thetaStart, 'thetaLength': thetaLength }

		radialSegments = math.floor( radialSegments )
		tubularSegments = math.floor( tubularSegments )
		indices = []; vertices = []; normals = []; uvs = []
		p = Vector3(); c = Vector3()

		for j in range( radialSegments + 1 ):

			v = thetaStart + ( j / radialSegments ) * thetaLength

			for i in range( tubularSegments + 1 ):

				u = i / tubularSegments * arc
				p.set( ( radius + tube * math.cos( v ) ) * math.cos( u ), ( radius + tube * math.cos( v ) ) * math.sin( u ), tube * math.sin( v ) )
				vertices.extend( ( p.x, p.y, p.z ) )
				c.set( radius * math.cos( u ), radius * math.sin( u ), 0 )
				p.sub( c ).normalize()
				normals.extend( ( p.x, p.y, p.z ) )
				uvs.extend( ( i / tubularSegments, j / radialSegments ) )

		for j in range( 1, radialSegments + 1 ):

			for i in range( 1, tubularSegments + 1 ):

				t1 = tubularSegments + 1
				a = t1 * j + i - 1; b = t1 * ( j - 1 ) + i - 1; cc = t1 * ( j - 1 ) + i; d = t1 * j + i
				indices.extend( ( a, b, d, b, cc, d ) )

		_finish( self, indices, vertices, normals, uvs )


class LatheGeometry( BufferGeometry ):

	def __init__( self, points = None, segments = 12, phiStart = 0, phiLength = math.pi * 2 ):

		super().__init__()
		self.type = 'LatheGeometry'
		self.parameters = { 'points': points, 'segments': segments, 'phiStart': phiStart, 'phiLength': phiLength }

		if points is None:
			points = [ Vector2( 0, - 0.5 ), Vector2( 0.5, 0 ), Vector2( 0, 0.5 ) ]

		segments = math.floor( segments )
		phiLength = max( 0, min( math.pi * 2, phiLength ) )
		indices = []; vertices = []; uvs = []; initNormals = []; normals = []
		inv = 1.0 / segments
		normal = Vector3(); cur = Vector3(); prev = Vector3()
		n = len( points )

		# profile normals: perpendicular of each edge, averaged at interior points
		for j in range( n ):

			if j == 0:

				dx = points[ 1 ].x - points[ 0 ].x; dy = points[ 1 ].y - points[ 0 ].y
				normal.set( dy, - dx, 0 )
				prev.copy( normal )
				normal.normalize()
				initNormals.extend( ( normal.x, normal.y, normal.z ) )

			elif j == n - 1:

				initNormals.extend( ( prev.x, prev.y, prev.z ) )

			else:

				dx = points[ j + 1 ].x - points[ j ].x; dy = points[ j + 1 ].y - points[ j ].y
				normal.set( dy, - dx, 0 )
				cur.copy( normal )
				normal.add( prev ).normalize()
				initNormals.extend( ( normal.x, normal.y, normal.z ) )
				prev.copy( cur )

		for i in range( segments + 1 ):

			phi = phiStart + i * inv * phiLength
			s = math.sin( phi ); c = math.cos( phi )

			for j in range( n ):

				vertices.extend( ( points[ j ].x * s, points[ j ].y, points[ j ].x * c ) )
				uvs.extend( ( i / segments, j / ( n - 1 ) ) )
				normals.extend( ( initNormals[ 3 * j ] * s, initNormals[ 3 * j + 1 ], initNormals[ 3 * j ] * c ) )

		for i in range( segments ):

			for j in range( n - 1 ):

				base = j + i * n
				a = base; b = base + n; c = base + n + 1; d = base + 1
				indices.extend( ( a, b, d, c, d, b ) )

		# three.js order for lathes: position, uv, normal
		self.setIndex( indices )
		self.setAttribute( 'position', Float32BufferAttribute( vertices, 3 ) )
		self.setAttribute( 'uv', Float32BufferAttribute( uvs, 2 ) )
		self.setAttribute( 'normal', Float32BufferAttribute( normals, 3 ) )


class TubeGeometry( BufferGeometry ):
	"""Tube swept along a curve using its Frenet frames."""

	def __init__( self, path, tubularSegments = 64, radius = 1, radialSegments = 8, closed = False ):

		super().__init__()
		self.type = 'TubeGeometry'
		self.parameters = { 'path': path, 'tubularSegments': tubularSegments, 'radius': radius, 'radialSegments': radialSegments, 'closed': closed }

		frames = path.computeFrenetFrames( tubularSegments, closed )
		self.tangents = frames[ 'tangents' ]
		self.normals = frames[ 'normals' ]
		self.binormals = frames[ 'binormals' ]

		vertices = []; normals = []; uvs = []; indices = []
		P = Vector3(); n = Vector3(); v3 = Vector3()

		def segment( i ):

			path.getPointAt( i / tubularSegments, P )
			N = frames[ 'normals' ][ i ]; B = frames[ 'binormals' ][ i ]

			for j in range( radialSegments + 1 ):

				v = j / radialSegments * math.pi * 2
				s = math.sin( v ); c = - math.cos( v )
				n.set( c * N.x + s * B.x, c * N.y + s * B.y, c * N.z + s * B.z ).normalize()
				normals.extend( ( n.x, n.y, n.z ) )
				v3.copy( P ).addScaledVector( n, radius )
				vertices.extend( ( v3.x, v3.y, v3.z ) )

		for i in range( tubularSegments ):
			segment( i )

		segment( tubularSegments if closed is False else 0 )

		for i in range( tubularSegments + 1 ):
			for j in range( radialSegments + 1 ):
				uvs.extend( ( i / tubularSegments, j / radialSegments ) )

		for j in range( 1, tubularSegments + 1 ):

			for i in range( 1, radialSegments + 1 ):

				r1 = radialSegments + 1
				a = r1 * ( j - 1 ) + ( i - 1 ); b = r1 * j + ( i - 1 ); c = r1 * j + i; d = r1 * ( j - 1 ) + i
				indices.extend( ( a, b, d, b, c, d ) )

		self.setIndex( indices )
		self.setAttribute( 'position', Float32BufferAttribute( vertices, 3 ) )
		self.setAttribute( 'normal', Float32BufferAttribute( normals, 3 ) )
		self.setAttribute( 'uv', Float32BufferAttribute( uvs, 2 ) )


def _sign( v ):
	"""JS Math.sign (returns 0 for 0, NaN for NaN)."""

	if v != v:
		return v

	return 1 if v > 0 else - 1 if v < 0 else v


def _arcUv( faceDir, normal, uvAxis, projectionAxis, radius, sideLength ):
	"""UV coordinate along `uvAxis` for a rounded-box face, arc-length parameterized so
	the rounded strip and the flat center share texel density.
	"""

	totArc = 2 * math.pi * radius / 4
	center = max( sideLength - 2 * radius, 0 )
	halfArc = math.pi / 4
	_n = Vector3().copy( normal )
	_n[ projectionAxis ] = 0
	_n.normalize()
	arcUvRatio = 0.5 * totArc / ( totArc + center )
	arcAngleRatio = 1.0 - ( _n.angleTo( faceDir ) / halfArc )

	if _sign( _n[ uvAxis ] ) == 1:
		return arcAngleRatio * arcUvRatio

	lenUv = center / ( totArc + center )
	return lenUv + arcUvRatio + arcUvRatio * ( 1.0 - arcAngleRatio )


class RoundedBoxGeometry( BoxGeometry ):
	"""Box with rounded edges and corners: a non-indexed unit box with an odd segment
	count whose vertices are pushed onto an inner box offset by `radius` along a
	smoothed normal.
	"""

	def __init__( self, width = 1, height = 1, depth = 1, segments = 2, radius = 0.1 ):

		segments = segments * 2 + 1
		radius = min( width / 2, height / 2, depth / 2, radius )
		super().__init__( 1, 1, 1, segments, segments, segments )
		self.type = 'RoundedBoxGeometry'
		self.parameters = { 'width': width, 'height': height, 'depth': depth, 'segments': segments, 'radius': radius }

		if segments == 1:
			return

		flat = self.toNonIndexed()
		self.index = None
		self.attributes[ 'position' ] = flat.attributes[ 'position' ]
		self.attributes[ 'normal' ] = flat.attributes[ 'normal' ]
		self.attributes[ 'uv' ] = flat.attributes[ 'uv' ]

		position = Vector3(); normal = Vector3(); faceDir = Vector3()
		box = Vector3( width, height, depth ).divideScalar( 2 ).subScalar( radius )
		P = self.attributes[ 'position' ].array
		N = self.attributes[ 'normal' ].array
		U = self.attributes[ 'uv' ].array
		perFace = len( P ) / 3 / 6
		halfSeg = 0.5 / segments

		j = 0

		for i in range( len( P ) // 3 ):

			position.fromArray( P, j )
			normal.copy( position )
			normal.x -= _sign( normal.x ) * halfSeg
			normal.y -= _sign( normal.y ) * halfSeg
			normal.z -= _sign( normal.z ) * halfSeg
			normal.normalize()

			P[ j ] = box.x * _sign( position.x ) + normal.x * radius
			P[ j + 1 ] = box.y * _sign( position.y ) + normal.y * radius
			P[ j + 2 ] = box.z * _sign( position.z ) + normal.z * radius
			N[ j ] = normal.x; N[ j + 1 ] = normal.y; N[ j + 2 ] = normal.z

			k = i * 2
			face = math.floor( i / perFace )

			if face == 0: # +x

				faceDir.set( 1, 0, 0 )
				U[ k ] = _arcUv( faceDir, normal, 'z', 'y', radius, depth )
				U[ k + 1 ] = 1.0 - _arcUv( faceDir, normal, 'y', 'z', radius, height )

			elif face == 1: # -x

				faceDir.set( - 1, 0, 0 )
				U[ k ] = 1.0 - _arcUv( faceDir, normal, 'z', 'y', radius, depth )
				U[ k + 1 ] = 1.0 - _arcUv( faceDir, normal, 'y', 'z', radius, height )

			elif face == 2: # +y

				faceDir.set( 0, 1, 0 )
				U[ k ] = 1.0 - _arcUv( faceDir, normal, 'x', 'z', radius, width )
				U[ k + 1 ] = _arcUv( faceDir, normal, 'z', 'x', radius, depth )

			elif face == 3: # -y

				faceDir.set( 0, - 1, 0 )
				U[ k ] = 1.0 - _arcUv( faceDir, normal, 'x', 'z', radius, width )
				U[ k + 1 ] = 1.0 - _arcUv( faceDir, normal, 'z', 'x', radius, depth )

			elif face == 4: # +z

				faceDir.set( 0, 0, 1 )
				U[ k ] = 1.0 - _arcUv( faceDir, normal, 'x', 'y', radius, width )
				U[ k + 1 ] = 1.0 - _arcUv( faceDir, normal, 'y', 'x', radius, height )

			elif face == 5: # -z

				faceDir.set( 0, 0, - 1 )
				U[ k ] = _arcUv( faceDir, normal, 'x', 'y', radius, width )
				U[ k + 1 ] = 1.0 - _arcUv( faceDir, normal, 'y', 'x', radius, height )

			j += 3


# ------------------------------------------------------------------ merging

def _console_error( *parts ):
	"""Stand-in for console.error: the JS merge helpers log and return null on bad input."""

	sys.stderr.write( ' '.join( str( p ) for p in parts ) + '\n' )


def mergeAttributes( attributes ):
	"""Concatenate attributes of equal itemSize / normalized / array type."""

	first = attributes[ 0 ]
	itemSize = first.itemSize; normalized = first.normalized
	total = 0

	for a in attributes:

		if isinstance( a.array, Float32Array ) != isinstance( first.array, Float32Array ) or a.itemSize != itemSize or a.normalized != normalized:

			_console_error( 'mergeAttributes(): attributes differ in array type, itemSize or normalized.' )
			return None

		total += a.count * itemSize

	out = Float32Array( total ) if isinstance( first.array, Float32Array ) else [ 0 ] * total
	off = 0

	for a in attributes:

		out.set( a.array.subarray( 0, a.count * itemSize ), off )
		off += a.count * itemSize

	return BufferAttribute( out, itemSize, normalized )


def mergeGeometries( geometries, useGroups = False ):
	"""Merge a list of geometries into one (three.js addons mergeGeometries subset)."""

	isIndexed = geometries[ 0 ].index is not None
	names = list( geometries[ 0 ].attributes.keys() )
	attrs = {}; merged = BufferGeometry()
	offset = 0

	for i, g in enumerate( geometries ):

		if isIndexed != ( g.index is not None ):

			_console_error( f'mergeGeometries(): geometry { i } index mismatch; all or none must be indexed.' )
			return None

		n = 0

		for name in g.attributes:

			if name not in names:

				_console_error( f'mergeGeometries(): geometry { i } has attribute "{ name }" missing from geometry 0.' )
				return None

			attrs.setdefault( name, [] ).append( g.attributes[ name ] )
			n += 1

		if n != len( names ):

			_console_error( f'mergeGeometries(): geometry { i } is missing attributes.' )
			return None

		if useGroups:

			count = g.index.count if isIndexed else g.attributes[ 'position' ].count
			merged.addGroup( offset, count, i )
			offset += count

	if isIndexed:

		indexOffset = 0
		idx = []

		for g in geometries:

			index = g.index

			for j in range( index.count ):
				idx.append( index.getX( j ) + indexOffset )

			indexOffset += g.attributes[ 'position' ].count

		merged.setIndex( idx )

	for name in attrs:

		a = mergeAttributes( attrs[ name ] )

		if a is None:

			_console_error( f'mergeGeometries(): failed merging attribute "{ name }".' )
			return None

		merged.setAttribute( name, a )

	return merged
