# Maths for the pure-Python port of the procedural boat builder.
#
# This is the subset of src/engine/math (and of three.js, which that layer mirrors) that
# src/world/boat/GeoKit.js and src/world/boat/HullLines.js actually use, ported line by
# line: same operation order, same double precision, same float32 rounding on every store
# into a vertex attribute. The float32 rounding is not cosmetic -- the JS attribute arrays
# are Float32Array, so the JS vertex positions (and therefore every bounding box) *are*
# float32 values; reproducing them means rounding at the same places.
#
# One deviation, measured rather than assumed: CPython's math.sin/cos/pow call the host
# libm, V8 ships its own fdlibm port, and the two disagree in the last ulp for some
# arguments (cos( 0.1 ) is one of them). That only touches numbers which never reach a
# vertex attribute -- 1 ulp of a double is ~2^29 below the spacing of a float32 vertex, so
# no vertex, index or bounding box can move. V8's Math.hypot *does* feed the hull geometry
# (the section splines call it on every span), so that one is reimplemented exactly, below.
#
# Pure Python 3, standard library only (no numpy, no bpy).

import math
import re
import struct
import sys

# ------------------------------------------------------------------ float width

_F32 = struct.Struct( '<f' )
FLOAT32_MAX = 3.4028234663852886e+38


def f32( v ):
	"""Round a float to the nearest float32 -- what a JS Float32Array stores.

	`struct.pack( 'f' )` uses the same round-to-nearest-even rule as the hardware store
	the JS engine does, and overflow saturates to +/-Infinity there rather than raising,
	so match that by hand.
	"""

	if v != v:
		return v

	if v > FLOAT32_MAX:
		return math.inf

	if v < - FLOAT32_MAX:
		return - math.inf

	return _F32.unpack( _F32.pack( v ) )[ 0 ]


def js_round( v ):
	"""JS Math.round: ties go toward +Infinity (Python's round() goes to even)."""

	return math.floor( v + 0.5 )


def js_hypot( *values ):
	"""JS Math.hypot, operation for operation.

	V8 scales by the largest magnitude and accumulates the scaled squares with Kahan
	compensation (`max * sqrt( sum )`); CPython delegates to the C library's hypot, which
	rounds differently in the last ulp. HullLines' section splines call Math.hypot on every
	span, so without this the hull scalars drift in the 16th digit. Fitted against node:
	0 mismatches over 300k random 2- and 3-argument cases.
	"""

	max_v = 0.0

	for v in values:
		a = abs( v )

		if a > max_v: # NaN never wins, exactly like `if ( abs( v ) > max ) max = |v|`
			max_v = a

	if max_v == math.inf:
		return max_v

	if max_v == 0:
		return 0.0

	sum_ = 0.0; c = 0.0

	for v in values:

		s = v / max_v
		y = s * s - c
		t = sum_ + y
		c = ( t - sum_ ) - y
		sum_ = t

	return max_v * math.sqrt( sum_ )


def _num( v ):
	"""Promote a Python int to a float -- JS has no integers, every number is a double.

	This is not cosmetic: `- 0` is 0 in Python but -0.0 in JS, and the sign of that zero
	survives into a vertex attribute. One wheel-handle normal comes out of
	Quaternion.setFromUnitVectors() as -0.0 that way; keeping the ints would leave a
	+0.0 there and make that bucket's hash differ from the JS.
	"""

	return float( v ) if isinstance( v, int ) else v


class Float32Array( list ):
	"""Stand-in for a JS Float32Array: a list that rounds every value to float32.

	`Float32Array( n )` is a zero-filled buffer of n elements, `Float32Array( seq )`
	fills from any iterable, exactly like the two JS constructors.
	"""

	def __init__( self, source = 0 ):

		if isinstance( source, int ):
			super().__init__( [ 0.0 ] * source )
		else:
			super().__init__( f32( v ) for v in source )

	def __setitem__( self, k, v ):
		super().__setitem__( k, f32( v ) )

	def set( self, src, offset = 0 ):
		"""JS TypedArray.prototype.set()."""

		for i, v in enumerate( src ):
			self[ offset + i ] = v

		return self

	def subarray( self, start, end = None ):
		"""JS subarray() returns a view; a copy is all the ported code needs."""

		return self[ start : end ]

	def fill( self, v ):

		v = f32( v )

		for i in range( len( self ) ):
			list.__setitem__( self, i, v )

		return self


class Float64Array( list ):
	"""Stand-in for a JS Float64Array: no rounding, doubles are already native."""

	def __init__( self, source = 0 ):

		if isinstance( source, int ):
			super().__init__( [ 0.0 ] * source )
		else:
			super().__init__( float( v ) for v in source )


# ------------------------------------------------------------------ Vector2

class Vector2:
	"""2D vector (three.js Vector2-compatible, the parts the ported files use)."""

	def __init__( self, x = 0, y = 0 ):

		self.x = _num( x )
		self.y = _num( y )

	def set( self, x, y ):
		self.x = _num( x ); self.y = _num( y )
		return self

	def copy( self, v ):
		self.x = v.x; self.y = v.y
		return self

	def clone( self ):
		return Vector2( self.x, self.y )

	def add( self, v ):
		self.x += v.x; self.y += v.y
		return self

	def sub( self, v ):
		self.x -= v.x; self.y -= v.y
		return self

	def subVectors( self, a, b ):
		self.x = a.x - b.x; self.y = a.y - b.y
		return self

	def multiplyScalar( self, s ):
		self.x *= s; self.y *= s
		return self

	def divideScalar( self, s ):
		return self.multiplyScalar( 1 / s )

	def dot( self, v ):
		return self.x * v.x + self.y * v.y

	def cross( self, v ):
		return self.x * v.y - self.y * v.x

	def lengthSq( self ):
		return self.x * self.x + self.y * self.y

	def length( self ):
		return math.sqrt( self.x * self.x + self.y * self.y )

	def normalize( self ):
		return self.divideScalar( self.length() or 1 )

	def distanceTo( self, v ):
		return math.sqrt( self.distanceToSquared( v ) )

	def distanceToSquared( self, v ):

		dx = self.x - v.x; dy = self.y - v.y
		return dx * dx + dy * dy

	def lerpVectors( self, a, b, t ):

		self.x = a.x + ( b.x - a.x ) * t
		self.y = a.y + ( b.y - a.y ) * t
		return self

	def equals( self, v ):
		return self.x == v.x and self.y == v.y

	def fromArray( self, a, o = 0 ):

		self.x = a[ o ]; self.y = a[ o + 1 ]
		return self

	def toArray( self, a = None, o = 0 ):

		if a is None:
			a = []

		a[ o ] = self.x; a[ o + 1 ] = self.y
		return a

	def __iter__( self ):

		yield self.x
		yield self.y


# ------------------------------------------------------------------ Vector3

class Vector3:
	"""3D vector (three.js Vector3-compatible, the parts the ported files use).

	Also indexable by int (0, 1, 2) and by axis name ('x', 'y', 'z') because the JS
	geometry generators address components as vec[ u ] with u in 'xyz'.
	"""

	AXIS = { 'x': 0, 'y': 1, 'z': 2 }

	def __init__( self, x = 0, y = 0, z = 0 ):

		self.x = _num( x )
		self.y = _num( y )
		self.z = _num( z )

	def __getitem__( self, k ):
		return self.getComponent( k if isinstance( k, int ) else self.AXIS[ k ] )

	def __setitem__( self, k, v ):
		self.setComponent( k if isinstance( k, int ) else self.AXIS[ k ], v )

	def set( self, x, y, z = None ):

		if z is None:
			z = self.z

		self.x = _num( x ); self.y = _num( y ); self.z = _num( z )
		return self

	def setScalar( self, s ):
		self.x = _num( s ); self.y = _num( s ); self.z = _num( s )
		return self

	def setComponent( self, i, v ):

		v = _num( v )

		if i == 0:
			self.x = v
		elif i == 1:
			self.y = v
		else:
			self.z = v

		return self

	def getComponent( self, i ):

		if i == 0:
			return self.x

		if i == 1:
			return self.y

		return self.z

	def clone( self ):
		return Vector3( self.x, self.y, self.z )

	def copy( self, v ):
		self.x = v.x; self.y = v.y; self.z = v.z
		return self

	def add( self, v ):
		self.x += v.x; self.y += v.y; self.z += v.z
		return self

	def addScalar( self, s ):
		self.x += s; self.y += s; self.z += s
		return self

	def addVectors( self, a, b ):
		self.x = a.x + b.x; self.y = a.y + b.y; self.z = a.z + b.z
		return self

	def addScaledVector( self, v, s ):
		self.x += v.x * s; self.y += v.y * s; self.z += v.z * s
		return self

	def sub( self, v ):
		self.x -= v.x; self.y -= v.y; self.z -= v.z
		return self

	def subScalar( self, s ):
		self.x -= s; self.y -= s; self.z -= s
		return self

	def subVectors( self, a, b ):
		self.x = a.x - b.x; self.y = a.y - b.y; self.z = a.z - b.z
		return self

	def multiply( self, v ):
		self.x *= v.x; self.y *= v.y; self.z *= v.z
		return self

	def multiplyScalar( self, s ):
		self.x *= s; self.y *= s; self.z *= s
		return self

	def multiplyVectors( self, a, b ):
		self.x = a.x * b.x; self.y = a.y * b.y; self.z = a.z * b.z
		return self

	def divide( self, v ):
		self.x /= v.x; self.y /= v.y; self.z /= v.z
		return self

	def divideScalar( self, s ):
		return self.multiplyScalar( 1 / s )

	def negate( self ):
		self.x = - self.x; self.y = - self.y; self.z = - self.z
		return self

	def applyMatrix3( self, m ):

		x = self.x; y = self.y; z = self.z; e = m.elements
		self.x = e[ 0 ] * x + e[ 3 ] * y + e[ 6 ] * z
		self.y = e[ 1 ] * x + e[ 4 ] * y + e[ 7 ] * z
		self.z = e[ 2 ] * x + e[ 5 ] * y + e[ 8 ] * z
		return self

	def applyNormalMatrix( self, m ):
		return self.applyMatrix3( m ).normalize()

	def applyMatrix4( self, m ):

		x = self.x; y = self.y; z = self.z; e = m.elements
		w = 1 / ( e[ 3 ] * x + e[ 7 ] * y + e[ 11 ] * z + e[ 15 ] )
		self.x = ( e[ 0 ] * x + e[ 4 ] * y + e[ 8 ] * z + e[ 12 ] ) * w
		self.y = ( e[ 1 ] * x + e[ 5 ] * y + e[ 9 ] * z + e[ 13 ] ) * w
		self.z = ( e[ 2 ] * x + e[ 6 ] * y + e[ 10 ] * z + e[ 14 ] ) * w
		return self

	def applyQuaternion( self, q ):

		# v' = v + 2w(q x v) + 2 q x (q x v)
		vx = self.x; vy = self.y; vz = self.z; qx = q.x; qy = q.y; qz = q.z; qw = q.w
		tx = 2 * ( qy * vz - qz * vy ); ty = 2 * ( qz * vx - qx * vz ); tz = 2 * ( qx * vy - qy * vx )
		self.x = vx + qw * tx + qy * tz - qz * ty
		self.y = vy + qw * ty + qz * tx - qx * tz
		self.z = vz + qw * tz + qx * ty - qy * tx
		return self

	def applyAxisAngle( self, axis, angle ):
		"""Rotate about `axis` by `angle` (added for the wheelhouse port: the wipers)."""

		return self.applyQuaternion( Quaternion().setFromAxisAngle( axis, angle ) )

	def transformDirection( self, m ):

		x = self.x; y = self.y; z = self.z; e = m.elements
		self.x = e[ 0 ] * x + e[ 4 ] * y + e[ 8 ] * z
		self.y = e[ 1 ] * x + e[ 5 ] * y + e[ 9 ] * z
		self.z = e[ 2 ] * x + e[ 6 ] * y + e[ 10 ] * z
		return self.normalize()

	def min( self, v ):
		self.x = min( self.x, v.x ); self.y = min( self.y, v.y ); self.z = min( self.z, v.z )
		return self

	def max( self, v ):
		self.x = max( self.x, v.x ); self.y = max( self.y, v.y ); self.z = max( self.z, v.z )
		return self

	def dot( self, v ):
		return self.x * v.x + self.y * v.y + self.z * v.z

	def lengthSq( self ):
		return self.x * self.x + self.y * self.y + self.z * self.z

	def length( self ):
		return math.sqrt( self.x * self.x + self.y * self.y + self.z * self.z )

	def manhattanLength( self ):
		return abs( self.x ) + abs( self.y ) + abs( self.z )

	def normalize( self ):
		return self.divideScalar( self.length() or 1 )

	def setLength( self, l ):
		return self.normalize().multiplyScalar( l )

	def lerp( self, v, a ):

		self.x += ( v.x - self.x ) * a; self.y += ( v.y - self.y ) * a; self.z += ( v.z - self.z ) * a
		return self

	def lerpVectors( self, a, b, t ):

		self.x = a.x + ( b.x - a.x ) * t
		self.y = a.y + ( b.y - a.y ) * t
		self.z = a.z + ( b.z - a.z ) * t
		return self

	def cross( self, v ):
		return self.crossVectors( self, v )

	def crossVectors( self, a, b ):

		ax = a.x; ay = a.y; az = a.z; bx = b.x; by = b.y; bz = b.z
		self.x = ay * bz - az * by
		self.y = az * bx - ax * bz
		self.z = ax * by - ay * bx
		return self

	def angleTo( self, v ):

		d = math.sqrt( self.lengthSq() * v.lengthSq() )

		if d == 0:
			return math.pi / 2

		return math.acos( max( - 1, min( 1, self.dot( v ) / d ) ) )

	def distanceTo( self, v ):
		return math.sqrt( self.distanceToSquared( v ) )

	def distanceToSquared( self, v ):

		dx = self.x - v.x; dy = self.y - v.y; dz = self.z - v.z
		return dx * dx + dy * dy + dz * dz

	def setFromMatrixColumn( self, m, i ):
		return self.fromArray( m.elements, i * 4 )

	def equals( self, v ):
		return self.x == v.x and self.y == v.y and self.z == v.z

	def fromArray( self, a, o = 0 ):

		self.x = a[ o ]; self.y = a[ o + 1 ]; self.z = a[ o + 2 ]
		return self

	def toArray( self, a = None, o = 0 ):

		if a is None:
			a = []

		# JS arrays grow on assignment (`array[ offset ] = ...`); a list does not, so pad.
		while len( a ) < o + 3:
			a.append( 0 )

		a[ o ] = self.x; a[ o + 1 ] = self.y; a[ o + 2 ] = self.z
		return a

	def fromBufferAttribute( self, attr, i ):

		self.x = attr.getX( i ); self.y = attr.getY( i ); self.z = attr.getZ( i )
		return self

	def __iter__( self ):

		yield self.x
		yield self.y
		yield self.z


# ------------------------------------------------------------------ Quaternion / Euler

class Quaternion:
	"""Unit quaternion (three.js Quaternion-compatible).

	The change callback three.js uses to keep an Object3D's Euler in sync is left out:
	nothing in GeoKit or HullLines drives an Object3D.
	"""

	def __init__( self, x = 0, y = 0, z = 0, w = 1 ):

		self._x = x
		self._y = y
		self._z = z
		self._w = w

	@property
	def x( self ): return self._x

	@x.setter
	def x( self, v ): self._x = v

	@property
	def y( self ): return self._y

	@y.setter
	def y( self, v ): self._y = v

	@property
	def z( self ): return self._z

	@z.setter
	def z( self, v ): self._z = v

	@property
	def w( self ): return self._w

	@w.setter
	def w( self, v ): self._w = v

	def set( self, x, y, z, w ):

		self._x = x; self._y = y; self._z = z; self._w = w
		return self

	def clone( self ):
		return Quaternion( self._x, self._y, self._z, self._w )

	def copy( self, q ):

		self._x = q.x; self._y = q.y; self._z = q.z; self._w = q.w
		return self

	def identity( self ):
		return self.set( 0, 0, 0, 1 )

	def setFromEuler( self, e, update = True ):

		x = e._x; y = e._y; z = e._z; order = e._order
		# host sin/cos: see the module header for why this can differ from V8 in the last ulp
		c1 = math.cos( x / 2 ); c2 = math.cos( y / 2 ); c3 = math.cos( z / 2 )
		s1 = math.sin( x / 2 ); s2 = math.sin( y / 2 ); s3 = math.sin( z / 2 )

		# sign pattern per order: (x, y, z, w) terms of s1c2c3 / c1s2c3 / c1c2s3 / c1c2c3
		if order == 'XYZ':
			self._x = s1 * c2 * c3 + c1 * s2 * s3; self._y = c1 * s2 * c3 - s1 * c2 * s3
			self._z = c1 * c2 * s3 + s1 * s2 * c3; self._w = c1 * c2 * c3 - s1 * s2 * s3
		elif order == 'YXZ':
			self._x = s1 * c2 * c3 + c1 * s2 * s3; self._y = c1 * s2 * c3 - s1 * c2 * s3
			self._z = c1 * c2 * s3 - s1 * s2 * c3; self._w = c1 * c2 * c3 + s1 * s2 * s3
		elif order == 'ZXY':
			self._x = s1 * c2 * c3 - c1 * s2 * s3; self._y = c1 * s2 * c3 + s1 * c2 * s3
			self._z = c1 * c2 * s3 + s1 * s2 * c3; self._w = c1 * c2 * c3 - s1 * s2 * s3
		elif order == 'ZYX':
			self._x = s1 * c2 * c3 - c1 * s2 * s3; self._y = c1 * s2 * c3 + s1 * c2 * s3
			self._z = c1 * c2 * s3 - s1 * s2 * c3; self._w = c1 * c2 * c3 + s1 * s2 * s3
		elif order == 'YZX':
			self._x = s1 * c2 * c3 + c1 * s2 * s3; self._y = c1 * s2 * c3 + s1 * c2 * s3
			self._z = c1 * c2 * s3 - s1 * s2 * c3; self._w = c1 * c2 * c3 - s1 * s2 * s3
		elif order == 'XZY':
			self._x = s1 * c2 * c3 - c1 * s2 * s3; self._y = c1 * s2 * c3 - s1 * c2 * s3
			self._z = c1 * c2 * s3 + s1 * s2 * c3; self._w = c1 * c2 * c3 + s1 * s2 * s3
		else:
			raise ValueError( 'Quaternion.setFromEuler: unknown order ' + order )

		return self

	def setFromAxisAngle( self, axis, angle ):

		h = angle / 2; s = math.sin( h )
		self._x = axis.x * s; self._y = axis.y * s; self._z = axis.z * s; self._w = math.cos( h )
		return self

	def setFromUnitVectors( self, frm, to ):

		r = frm.x * to.x + frm.y * to.y + frm.z * to.z + 1

		if r < sys.float_info.epsilon:

			# opposite vectors: rotate 180 degrees around any orthogonal axis
			r = 0

			if abs( frm.x ) > abs( frm.z ):
				self._x = - frm.y; self._y = frm.x; self._z = 0; self._w = r
			else:
				self._x = 0; self._y = - frm.z; self._z = frm.y; self._w = r

		else:

			self._x = frm.y * to.z - frm.z * to.y
			self._y = frm.z * to.x - frm.x * to.z
			self._z = frm.x * to.y - frm.y * to.x
			self._w = r

		return self.normalize()

	def conjugate( self ):

		self._x *= - 1; self._y *= - 1; self._z *= - 1
		return self

	def dot( self, q ):
		return self._x * q._x + self._y * q._y + self._z * q._z + self._w * q._w

	def lengthSq( self ):
		return self._x * self._x + self._y * self._y + self._z * self._z + self._w * self._w

	def length( self ):
		return math.sqrt( self.lengthSq() )

	def normalize( self ):

		l = self.length()

		if l == 0:
			self._x = 0; self._y = 0; self._z = 0; self._w = 1
		else:
			l = 1 / l
			self._x *= l; self._y *= l; self._z *= l; self._w *= l

		return self

	def multiply( self, q ):
		return self.multiplyQuaternions( self, q )

	def multiplyQuaternions( self, a, b ):

		ax = a._x; ay = a._y; az = a._z; aw = a._w; bx = b._x; by = b._y; bz = b._z; bw = b._w
		self._x = ax * bw + aw * bx + ay * bz - az * by
		self._y = ay * bw + aw * by + az * bx - ax * bz
		self._z = az * bw + aw * bz + ax * by - ay * bx
		self._w = aw * bw - ax * bx - ay * by - az * bz
		return self

	def equals( self, q ):
		return q._x == self._x and q._y == self._y and q._z == self._z and q._w == self._w

	def fromArray( self, a, o = 0 ):

		self._x = a[ o ]; self._y = a[ o + 1 ]; self._z = a[ o + 2 ]; self._w = a[ o + 3 ]
		return self

	def toArray( self, a = None, o = 0 ):

		if a is None:
			a = []

		a[ o ] = self._x; a[ o + 1 ] = self._y; a[ o + 2 ] = self._z; a[ o + 3 ] = self._w
		return a


class Euler:
	"""Euler angles (intrinsic rotations, default 'XYZ') -- only what mat4() needs."""

	DEFAULT_ORDER = 'XYZ'

	def __init__( self, x = 0, y = 0, z = 0, order = DEFAULT_ORDER ):

		self._x = x
		self._y = y
		self._z = z
		self._order = order

	def set( self, x, y, z, order = None ):

		if order is None:
			order = self._order

		self._x = x; self._y = y; self._z = z; self._order = order
		return self

	def clone( self ):
		return Euler( self._x, self._y, self._z, self._order )

	def copy( self, e ):
		return self.set( e._x, e._y, e._z, e._order )


# ------------------------------------------------------------------ Matrix3 / Matrix4

class Matrix3:
	"""3x3 matrix, column-major `elements` -- used for normal matrices."""

	def __init__( self, n11 = None, n12 = 0, n13 = 0, n21 = 0, n22 = 1, n23 = 0, n31 = 0, n32 = 0, n33 = 1 ):

		self.elements = [ 1, 0, 0, 0, 1, 0, 0, 0, 1 ]

		if n11 is not None:
			self.set( n11, n12, n13, n21, n22, n23, n31, n32, n33 )

	def set( self, n11, n12, n13, n21, n22, n23, n31, n32, n33 ):
		"""Row-major arguments."""

		e = self.elements
		e[ 0 ] = n11; e[ 1 ] = n21; e[ 2 ] = n31
		e[ 3 ] = n12; e[ 4 ] = n22; e[ 5 ] = n32
		e[ 6 ] = n13; e[ 7 ] = n23; e[ 8 ] = n33
		return self

	def copy( self, m ):

		e = self.elements; s = m.elements

		for i in range( 9 ):
			e[ i ] = s[ i ]

		return self

	def clone( self ):
		return Matrix3().fromArray( self.elements )

	def setFromMatrix4( self, m ):

		e = m.elements
		return self.set( e[ 0 ], e[ 4 ], e[ 8 ], e[ 1 ], e[ 5 ], e[ 9 ], e[ 2 ], e[ 6 ], e[ 10 ] )

	def determinant( self ):

		e = self.elements
		a = e[ 0 ]; b = e[ 1 ]; c = e[ 2 ]; d = e[ 3 ]; f = e[ 4 ]; g = e[ 5 ]; h = e[ 6 ]; i = e[ 7 ]; j = e[ 8 ]
		return a * f * j - a * g * i - b * d * j + b * g * h + c * d * i - c * f * h

	def invert( self ):

		e = self.elements
		n11 = e[ 0 ]; n21 = e[ 1 ]; n31 = e[ 2 ]; n12 = e[ 3 ]; n22 = e[ 4 ]; n32 = e[ 5 ]; n13 = e[ 6 ]; n23 = e[ 7 ]; n33 = e[ 8 ]
		t11 = n33 * n22 - n32 * n23; t12 = n32 * n13 - n33 * n12; t13 = n23 * n12 - n22 * n13
		det = n11 * t11 + n21 * t12 + n31 * t13

		if det == 0:
			return self.set( 0, 0, 0, 0, 0, 0, 0, 0, 0 )

		id = 1 / det
		e[ 0 ] = t11 * id; e[ 1 ] = ( n31 * n23 - n33 * n21 ) * id; e[ 2 ] = ( n32 * n21 - n31 * n22 ) * id
		e[ 3 ] = t12 * id; e[ 4 ] = ( n33 * n11 - n31 * n13 ) * id; e[ 5 ] = ( n31 * n12 - n32 * n11 ) * id
		e[ 6 ] = t13 * id; e[ 7 ] = ( n21 * n13 - n23 * n11 ) * id; e[ 8 ] = ( n22 * n11 - n21 * n12 ) * id
		return self

	def transpose( self ):

		m = self.elements
		t = m[ 1 ]; m[ 1 ] = m[ 3 ]; m[ 3 ] = t
		t = m[ 2 ]; m[ 2 ] = m[ 6 ]; m[ 6 ] = t
		t = m[ 5 ]; m[ 5 ] = m[ 7 ]; m[ 7 ] = t
		return self

	def getNormalMatrix( self, m4 ):
		return self.setFromMatrix4( m4 ).invert().transpose()

	def fromArray( self, a, o = 0 ):

		for i in range( 9 ):
			self.elements[ i ] = a[ i + o ]

		return self

	def toArray( self, a = None, o = 0 ):

		if a is None:
			a = []

		for i in range( 9 ):
			a[ o + i ] = self.elements[ i ]

		return a


class Matrix4:
	"""4x4 matrix, column-major `elements` (three.js Matrix4-compatible subset)."""

	def __init__( self, *args ):

		self.elements = [ 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 ]

		if len( args ) == 16:
			self.set( *args )

	def set( self, n11, n12, n13, n14, n21, n22, n23, n24, n31, n32, n33, n34, n41, n42, n43, n44 ):
		"""Row-major arguments."""

		e = self.elements
		e[ 0 ] = n11; e[ 4 ] = n12; e[ 8 ] = n13; e[ 12 ] = n14
		e[ 1 ] = n21; e[ 5 ] = n22; e[ 9 ] = n23; e[ 13 ] = n24
		e[ 2 ] = n31; e[ 6 ] = n32; e[ 10 ] = n33; e[ 14 ] = n34
		e[ 3 ] = n41; e[ 7 ] = n42; e[ 11 ] = n43; e[ 15 ] = n44
		return self

	def identity( self ):
		return self.set( 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 )

	def clone( self ):
		return Matrix4().fromArray( self.elements )

	def copy( self, m ):

		e = self.elements; s = m.elements

		for i in range( 16 ):
			e[ i ] = s[ i ]

		return self

	def multiplyMatrices( self, a, b ):

		ae = a.elements; be = b.elements; te = self.elements
		a11 = ae[ 0 ]; a12 = ae[ 4 ]; a13 = ae[ 8 ]; a14 = ae[ 12 ]
		a21 = ae[ 1 ]; a22 = ae[ 5 ]; a23 = ae[ 9 ]; a24 = ae[ 13 ]
		a31 = ae[ 2 ]; a32 = ae[ 6 ]; a33 = ae[ 10 ]; a34 = ae[ 14 ]
		a41 = ae[ 3 ]; a42 = ae[ 7 ]; a43 = ae[ 11 ]; a44 = ae[ 15 ]
		b11 = be[ 0 ]; b12 = be[ 4 ]; b13 = be[ 8 ]; b14 = be[ 12 ]
		b21 = be[ 1 ]; b22 = be[ 5 ]; b23 = be[ 9 ]; b24 = be[ 13 ]
		b31 = be[ 2 ]; b32 = be[ 6 ]; b33 = be[ 10 ]; b34 = be[ 14 ]
		b41 = be[ 3 ]; b42 = be[ 7 ]; b43 = be[ 11 ]; b44 = be[ 15 ]

		te[ 0 ] = a11 * b11 + a12 * b21 + a13 * b31 + a14 * b41
		te[ 4 ] = a11 * b12 + a12 * b22 + a13 * b32 + a14 * b42
		te[ 8 ] = a11 * b13 + a12 * b23 + a13 * b33 + a14 * b43
		te[ 12 ] = a11 * b14 + a12 * b24 + a13 * b34 + a14 * b44
		te[ 1 ] = a21 * b11 + a22 * b21 + a23 * b31 + a24 * b41
		te[ 5 ] = a21 * b12 + a22 * b22 + a23 * b32 + a24 * b42
		te[ 9 ] = a21 * b13 + a22 * b23 + a23 * b33 + a24 * b43
		te[ 13 ] = a21 * b14 + a22 * b24 + a23 * b34 + a24 * b44
		te[ 2 ] = a31 * b11 + a32 * b21 + a33 * b31 + a34 * b41
		te[ 6 ] = a31 * b12 + a32 * b22 + a33 * b32 + a34 * b42
		te[ 10 ] = a31 * b13 + a32 * b23 + a33 * b33 + a34 * b43
		te[ 14 ] = a31 * b14 + a32 * b24 + a33 * b34 + a34 * b44
		te[ 3 ] = a41 * b11 + a42 * b21 + a43 * b31 + a44 * b41
		te[ 7 ] = a41 * b12 + a42 * b22 + a43 * b32 + a44 * b42
		te[ 11 ] = a41 * b13 + a42 * b23 + a43 * b33 + a44 * b43
		te[ 15 ] = a41 * b14 + a42 * b24 + a43 * b34 + a44 * b44
		return self

	def multiply( self, m ):
		return self.multiplyMatrices( self, m )

	def premultiply( self, m ):
		return self.multiplyMatrices( m, self )

	def extractRotation( self, m ):

		e = self.elements; me = m.elements
		sx = 1 / Vector3().setFromMatrixColumn( m, 0 ).length()
		sy = 1 / Vector3().setFromMatrixColumn( m, 1 ).length()
		sz = 1 / Vector3().setFromMatrixColumn( m, 2 ).length()
		e[ 0 ] = me[ 0 ] * sx; e[ 1 ] = me[ 1 ] * sx; e[ 2 ] = me[ 2 ] * sx; e[ 3 ] = 0
		e[ 4 ] = me[ 4 ] * sy; e[ 5 ] = me[ 5 ] * sy; e[ 6 ] = me[ 6 ] * sy; e[ 7 ] = 0
		e[ 8 ] = me[ 8 ] * sz; e[ 9 ] = me[ 9 ] * sz; e[ 10 ] = me[ 10 ] * sz; e[ 11 ] = 0
		e[ 12 ] = 0; e[ 13 ] = 0; e[ 14 ] = 0; e[ 15 ] = 1
		return self

	def makeRotationAxis( self, axis, angle ):

		c = math.cos( angle ); s = math.sin( angle ); t = 1 - c
		x = axis.x; y = axis.y; z = axis.z; tx = t * x; ty = t * y
		return self.set(
			tx * x + c, tx * y - s * z, tx * z + s * y, 0,
			tx * y + s * z, ty * y + c, ty * z - s * x, 0,
			tx * z - s * y, ty * z + s * x, t * z * z + c, 0,
			0, 0, 0, 1
		)

	# ------------------------------------------------------------------ additions
	# The builders ported after HullLines / GeoKit need the remaining placement helpers of
	# three.js Matrix4 (makeBasis, setPosition, makeTranslation, makeRotationX/Y/Z,
	# makeScale). They are copied line for line from src/engine/math/Matrix4.js.

	def makeBasis( self, x, y, z ):
		"""Matrix whose columns are the three basis vectors (x, y, z: Vector3)."""

		return self.set( x.x, y.x, z.x, 0, x.y, y.y, z.y, 0, x.z, y.z, z.z, 0, 0, 0, 0, 1 )

	def setPosition( self, x, y = None, z = None ):
		"""Set the translation column; `x` may be a Vector3."""

		e = self.elements

		if isinstance( x, Vector3 ):
			e[ 12 ] = x.x; e[ 13 ] = x.y; e[ 14 ] = x.z
		else:
			e[ 12 ] = x; e[ 13 ] = y; e[ 14 ] = z

		return self

	def makeTranslation( self, x, y = None, z = None ):

		if isinstance( x, Vector3 ):
			return self.set( 1, 0, 0, x.x, 0, 1, 0, x.y, 0, 0, 1, x.z, 0, 0, 0, 1 )

		return self.set( 1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1 )

	def makeRotationX( self, t ):

		c = math.cos( t ); s = math.sin( t )
		return self.set( 1, 0, 0, 0, 0, c, - s, 0, 0, s, c, 0, 0, 0, 0, 1 )

	def makeRotationY( self, t ):

		c = math.cos( t ); s = math.sin( t )
		return self.set( c, 0, s, 0, 0, 1, 0, 0, - s, 0, c, 0, 0, 0, 0, 1 )

	def makeRotationZ( self, t ):

		c = math.cos( t ); s = math.sin( t )
		return self.set( c, - s, 0, 0, s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 )

	def makeScale( self, x, y, z ):

		return self.set( x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1 )

	def makeRotationFromQuaternion( self, q ):
		return self.compose( _ZERO, q, _ONE )

	def makeRotationFromEuler( self, e ):
		return self.compose( _ZERO, Quaternion().setFromEuler( e, False ), _ONE )

	def compose( self, position, quaternion, scale ):

		e = self.elements
		x = quaternion._x; y = quaternion._y; z = quaternion._z; w = quaternion._w
		x2 = x + x; y2 = y + y; z2 = z + z
		xx = x * x2; xy = x * y2; xz = x * z2; yy = y * y2; yz = y * z2; zz = z * z2
		wx = w * x2; wy = w * y2; wz = w * z2
		sx = scale.x; sy = scale.y; sz = scale.z
		e[ 0 ] = ( 1 - ( yy + zz ) ) * sx; e[ 1 ] = ( xy + wz ) * sx; e[ 2 ] = ( xz - wy ) * sx; e[ 3 ] = 0
		e[ 4 ] = ( xy - wz ) * sy; e[ 5 ] = ( 1 - ( xx + zz ) ) * sy; e[ 6 ] = ( yz + wx ) * sy; e[ 7 ] = 0
		e[ 8 ] = ( xz + wy ) * sz; e[ 9 ] = ( yz - wx ) * sz; e[ 10 ] = ( 1 - ( xx + yy ) ) * sz; e[ 11 ] = 0
		e[ 12 ] = position.x; e[ 13 ] = position.y; e[ 14 ] = position.z; e[ 15 ] = 1
		return self

	def fromArray( self, a, o = 0 ):

		for i in range( 16 ):
			self.elements[ i ] = a[ i + o ]

		return self

	def toArray( self, a = None, o = 0 ):

		if a is None:
			a = []

		for i in range( 16 ):
			a[ o + i ] = self.elements[ i ]

		return a


_ZERO = Vector3( 0, 0, 0 )
_ONE = Vector3( 1, 1, 1 )


# ------------------------------------------------------------------ Color

NoColorSpace = ''
SRGBColorSpace = 'srgb'
LinearSRGBColorSpace = 'srgb-linear'


def SRGBToLinear( c ):
	return c * 0.0773993808 if c < 0.04045 else math.pow( c * 0.9478672986 + 0.0521327014, 2.4 )


def LinearToSRGB( c ):
	return c * 12.92 if c < 0.0031308 else 1.055 * math.pow( c, 0.41666 ) - 0.055


def _clamp01( v ):
	return max( 0.0, min( 1.0, v ) )


def _euclid( n, m ):
	return ( ( n % m ) + m ) % m


def _hue2rgb( p, q, t ):

	if t < 0:
		t += 1

	if t > 1:
		t -= 1

	if t < 1 / 6:
		return p + ( q - p ) * 6 * t

	if t < 1 / 2:
		return q

	if t < 2 / 3:
		return p + ( q - p ) * 6 * ( 2 / 3 - t )

	return p


_NAMED = {
	'black': 0x000000, 'white': 0xffffff, 'red': 0xff0000, 'green': 0x008000, 'lime': 0x00ff00,
	'blue': 0x0000ff, 'yellow': 0xffff00, 'cyan': 0x00ffff, 'magenta': 0xff00ff, 'gray': 0x808080,
	'grey': 0x808080, 'orange': 0xffa500,
}

_HEX_RE = re.compile( r'^#([A-Fa-f\d]+)$' )
_RGB_RE = re.compile( r'^rgba?\(\s*([\d.]+)(%?)\s*,\s*([\d.]+)%?\s*,\s*([\d.]+)%?\s*(?:,\s*[\d.]+\s*)?\)$' )
_HSL_RE = re.compile( r'^hsla?\(\s*([\d.]+)\s*,\s*([\d.]+)%\s*,\s*([\d.]+)%\s*(?:,\s*[\d.]+\s*)?\)$' )


class Color:
	"""RGB color held as linear-sRGB floats (three.js Color with ColorManagement on:
	hex / CSS inputs are sRGB and get linearized).
	"""

	def __init__( self, r = None, g = None, b = None ):

		self.r = 1
		self.g = 1
		self.b = 1
		self.set( r, g, b )

	def set( self, r = None, g = None, b = None ):

		if g is None and b is None:

			if r is None:
				return self

			if isinstance( r, Color ):
				self.copy( r )
			elif isinstance( r, ( int, float ) ):
				self.setHex( r )
			elif isinstance( r, str ):
				self.setStyle( r )

		else:

			self.setRGB( r, g, b )

		return self

	def setScalar( self, s ):
		self.r = s; self.g = s; self.b = s
		return self

	def multiplyScalar( self, s ):
		"""Added for the wheelhouse port (the oilskin hood is a darkened copy)."""

		self.r *= s; self.g *= s; self.b *= s
		return self

	def setHex( self, hex, colorSpace = SRGBColorSpace ):

		hex = math.floor( hex )
		return self.setRGB( ( hex >> 16 & 255 ) / 255, ( hex >> 8 & 255 ) / 255, ( hex & 255 ) / 255, colorSpace )

	def setRGB( self, r, g, b, colorSpace = LinearSRGBColorSpace ):

		if colorSpace == SRGBColorSpace:
			r = SRGBToLinear( r ); g = SRGBToLinear( g ); b = SRGBToLinear( b )

		self.r = r; self.g = g; self.b = b
		return self

	def setStyle( self, style, colorSpace = SRGBColorSpace ):

		m = _HEX_RE.match( style )

		if m:

			h = m.group( 1 )

			if len( h ) == 3:
				return self.setRGB( int( h[ 0 ], 16 ) / 15, int( h[ 1 ], 16 ) / 15, int( h[ 2 ], 16 ) / 15, colorSpace )

			if len( h ) == 6:
				return self.setHex( int( h, 16 ), colorSpace )

		else:

			m = _RGB_RE.match( style )

			if m:

				k = 100 if m.group( 2 ) == '%' else 255
				return self.setRGB( min( 1, float( m.group( 1 ) ) / k ), min( 1, float( m.group( 3 ) ) / k ), min( 1, float( m.group( 4 ) ) / k ), colorSpace )

			m = _HSL_RE.match( style )

			if m:

				return self.setHSL( float( m.group( 1 ) ) / 360, float( m.group( 2 ) ) / 100, float( m.group( 3 ) ) / 100, colorSpace )

			if _NAMED.get( style.lower() ) is not None:

				return self.setHex( _NAMED[ style.lower() ], colorSpace )

		raise ValueError( 'Color: unknown color ' + style )

	def setHSL( self, h, s, l, colorSpace = LinearSRGBColorSpace ):

		h = _euclid( h, 1 ); s = _clamp01( s ); l = _clamp01( l )

		if s == 0:
			return self.setRGB( l, l, l, colorSpace )

		p = l * ( 1 + s ) if l <= 0.5 else l + s - l * s
		q = 2 * l - p
		return self.setRGB( _hue2rgb( q, p, h + 1 / 3 ), _hue2rgb( q, p, h ), _hue2rgb( q, p, h - 1 / 3 ), colorSpace )

	def clone( self ):
		return Color( self.r, self.g, self.b )

	def copy( self, c ):
		self.r = c.r; self.g = c.g; self.b = c.b
		return self

	def getHex( self, colorSpace = SRGBColorSpace ):

		r = self.r; g = self.g; b = self.b

		if colorSpace == SRGBColorSpace:
			r = LinearToSRGB( r ); g = LinearToSRGB( g ); b = LinearToSRGB( b )

		return js_round( _clamp01( r ) * 255 ) * 65536 + js_round( _clamp01( g ) * 255 ) * 256 + js_round( _clamp01( b ) * 255 )

	def getHexString( self, colorSpace = SRGBColorSpace ):
		return ( '000000' + format( self.getHex( colorSpace ), 'x' ) )[ - 6 : ]

	def equals( self, c ):
		return c.r == self.r and c.g == self.g and c.b == self.b

	def fromArray( self, a, o = 0 ):

		self.r = a[ o ]; self.g = a[ o + 1 ]; self.b = a[ o + 2 ]
		return self

	def toArray( self, a = None, o = 0 ):

		if a is None:
			a = []

		a[ o ] = self.r; a[ o + 1 ] = self.g; a[ o + 2 ] = self.b
		return a

	def fromBufferAttribute( self, attr, i ):

		self.r = attr.getX( i ); self.g = attr.getY( i ); self.b = attr.getZ( i )
		return self




# ------------------------------------------------------------------ curves

class Curve:
	"""Curve base: parameter -> point, arc-length table (200 divisions) and Frenet frames.

	Same arc-length table size and the same u -> t mapping as three.js, because
	TubeGeometry's vertex positions come straight out of getPointAt().
	"""

	def __init__( self ):

		self.arcLengthDivisions = 200
		self.needsUpdate = False
		self.cacheArcLengths = None

	def copy( self, src ):

		self.arcLengthDivisions = src.arcLengthDivisions
		return self

	def getPoint( self, t, target = None ):
		raise NotImplementedError( 'Curve.getPoint() not implemented' )

	def getPointAt( self, u, target = None ):
		return self.getPoint( self.getUtoTmapping( u ), target )

	def getPoints( self, divisions = 5 ):

		pts = []

		for d in range( divisions + 1 ):
			pts.append( self.getPoint( d / divisions ) )

		return pts

	def getSpacedPoints( self, divisions = 5 ):

		pts = []

		for d in range( divisions + 1 ):
			pts.append( self.getPointAt( d / divisions ) )

		return pts

	def getLength( self ):

		l = self.getLengths()
		return l[ len( l ) - 1 ]

	def getLengths( self, divisions = None ):

		if divisions is None:
			divisions = self.arcLengthDivisions

		if self.cacheArcLengths is not None and len( self.cacheArcLengths ) == divisions + 1 and not self.needsUpdate:
			return self.cacheArcLengths

		self.needsUpdate = False
		cache = [ 0 ]
		last = self.getPoint( 0 )
		sum_len = 0

		for p in range( 1, divisions + 1 ):

			cur = self.getPoint( p / divisions )
			sum_len += cur.distanceTo( last )
			cache.append( sum_len )
			last = cur

		self.cacheArcLengths = cache
		return cache

	def updateArcLengths( self ):

		self.needsUpdate = True
		self.getLengths()

	# Arc-length fraction u (or absolute `distance`) -> curve parameter t.
	def getUtoTmapping( self, u, distance = None ):

		L = self.getLengths(); n = len( L )
		target = distance if distance is not None else u * L[ n - 1 ]
		lo = 0; hi = n - 1

		while lo <= hi:

			i = math.floor( lo + ( hi - lo ) / 2 )
			c = L[ i ] - target

			if c < 0:
				lo = i + 1
			elif c > 0:
				hi = i - 1
			else:
				hi = i
				break

		i = hi

		if L[ i ] == target:
			return i / ( n - 1 )

		before = L[ i ]; after = L[ i + 1 ]
		return ( i + ( target - before ) / ( after - before ) ) / ( n - 1 )

	def getTangent( self, t, target = None ):

		if target is None:
			target = Vector3()

		delta = 0.0001
		t1 = max( 0, t - delta ); t2 = min( 1, t + delta )
		a = self.getPoint( t1 ); b = self.getPoint( t2 )
		return target.copy( b ).sub( a ).normalize()

	def getTangentAt( self, u, target = None ):
		return self.getTangent( self.getUtoTmapping( u ), target )

	# Parallel-transport frames (rotation-minimizing), as used by TubeGeometry.
	def computeFrenetFrames( self, segments, closed ):

		normal = Vector3(); tangents = []; normals = []; binormals = []
		vec = Vector3(); mat = Matrix4()

		for i in range( segments + 1 ):
			tangents.append( self.getTangentAt( i / segments, Vector3() ) )

		normals.append( Vector3() )
		binormals.append( Vector3() )
		min_v = sys.float_info.max
		tx = abs( tangents[ 0 ].x ); ty = abs( tangents[ 0 ].y ); tz = abs( tangents[ 0 ].z )

		if tx <= min_v:
			min_v = tx; normal.set( 1, 0, 0 )

		if ty <= min_v:
			min_v = ty; normal.set( 0, 1, 0 )

		if tz <= min_v:
			normal.set( 0, 0, 1 )

		vec.crossVectors( tangents[ 0 ], normal ).normalize()
		normals[ 0 ].crossVectors( tangents[ 0 ], vec )
		binormals[ 0 ].crossVectors( tangents[ 0 ], normals[ 0 ] )

		for i in range( 1, segments + 1 ):

			normals.append( normals[ i - 1 ].clone() )
			binormals.append( binormals[ i - 1 ].clone() )
			vec.crossVectors( tangents[ i - 1 ], tangents[ i ] )

			if vec.length() > sys.float_info.epsilon:

				vec.normalize()
				theta = math.acos( max( - 1, min( 1, tangents[ i - 1 ].dot( tangents[ i ] ) ) ) )
				normals[ i ].applyMatrix4( mat.makeRotationAxis( vec, theta ) )

			binormals[ i ].crossVectors( tangents[ i ], normals[ i ] )

		if closed is True:

			theta = math.acos( max( - 1, min( 1, normals[ 0 ].dot( normals[ segments ] ) ) ) ) / segments

			if tangents[ 0 ].dot( vec.crossVectors( normals[ 0 ], normals[ segments ] ) ) > 0:
				theta = - theta

			for i in range( 1, segments + 1 ):

				normals[ i ].applyMatrix4( mat.makeRotationAxis( tangents[ i ], theta * i ) )
				binormals[ i ].crossVectors( tangents[ i ], normals[ i ] )

		return { 'tangents': tangents, 'normals': normals, 'binormals': binormals }


def _hermite( out, x0, x1, t0, t1 ):
	"""Cubic c0 + c1 t + c2 t^2 + c3 t^3 from Hermite endpoints / tangents."""

	out[ 0 ] = x0
	out[ 1 ] = t0
	out[ 2 ] = - 3 * x0 + 3 * x1 - 2 * t0 - t1
	out[ 3 ] = 2 * x0 - 2 * x1 + t0 + t1


def _uniformCR( out, x0, x1, x2, x3, tension ):

	_hermite( out, x1, x2, tension * ( x2 - x0 ), tension * ( x3 - x1 ) )


def _nonuniformCR( out, x0, x1, x2, x3, dt0, dt1, dt2 ):

	t1 = ( x1 - x0 ) / dt0 - ( x2 - x0 ) / ( dt0 + dt1 ) + ( x2 - x1 ) / dt1
	t2 = ( x2 - x1 ) / dt1 - ( x3 - x1 ) / ( dt1 + dt2 ) + ( x3 - x2 ) / dt2
	t1 *= dt1
	t2 *= dt1
	_hermite( out, x1, x2, t1, t2 )


def _evalPoly( c, t ):
	return c[ 0 ] + t * ( c[ 1 ] + t * ( c[ 2 ] + t * c[ 3 ] ) )


_px = [ 0, 0, 0, 0 ]; _py = [ 0, 0, 0, 0 ]; _pz = [ 0, 0, 0, 0 ]
_t0 = Vector3(); _t3 = Vector3()


class CatmullRomCurve3( Curve ):
	"""Centripetal Catmull-Rom spline (three.js CatmullRomCurve3-compatible)."""

	def __init__( self, points = None, closed = False, curveType = 'centripetal', tension = 0.5 ):

		super().__init__()
		self.type = 'CatmullRomCurve3'
		self.points = points if points is not None else []
		self.closed = closed
		self.curveType = curveType
		self.tension = tension

	def getPoint( self, t, target = None ):

		if target is None:
			target = Vector3()

		pts = self.points; l = len( pts )
		p = ( l - ( 0 if self.closed else 1 ) ) * t
		i = math.floor( p ); w = p - i

		if self.closed:
			i += 0 if i > 0 else ( math.floor( abs( i ) / l ) + 1 ) * l
		elif w == 0 and i == l - 1:
			i = l - 2; w = 1

		if self.closed or i > 0:
			p0 = pts[ ( i - 1 ) % l ]
		else:
			p0 = _t0.subVectors( pts[ 0 ], pts[ 1 ] ).add( pts[ 0 ] )

		p1 = pts[ i % l ]; p2 = pts[ ( i + 1 ) % l ]

		if self.closed or i + 2 < l:
			p3 = pts[ ( i + 2 ) % l ]
		else:
			p3 = _t3.subVectors( pts[ l - 1 ], pts[ l - 2 ] ).add( pts[ l - 1 ] )

		if self.curveType == 'centripetal' or self.curveType == 'chordal':

			pw = 0.5 if self.curveType == 'chordal' else 0.25
			dt0 = math.pow( p0.distanceToSquared( p1 ), pw )
			dt1 = math.pow( p1.distanceToSquared( p2 ), pw )
			dt2 = math.pow( p2.distanceToSquared( p3 ), pw )

			if dt1 < 1e-4:
				dt1 = 1.0

			if dt0 < 1e-4:
				dt0 = dt1

			if dt2 < 1e-4:
				dt2 = dt1

			_nonuniformCR( _px, p0.x, p1.x, p2.x, p3.x, dt0, dt1, dt2 )
			_nonuniformCR( _py, p0.y, p1.y, p2.y, p3.y, dt0, dt1, dt2 )
			_nonuniformCR( _pz, p0.z, p1.z, p2.z, p3.z, dt0, dt1, dt2 )

		else:

			_uniformCR( _px, p0.x, p1.x, p2.x, p3.x, self.tension )
			_uniformCR( _py, p0.y, p1.y, p2.y, p3.y, self.tension )
			_uniformCR( _pz, p0.z, p1.z, p2.z, p3.z, self.tension )

		return target.set( _evalPoly( _px, w ), _evalPoly( _py, w ), _evalPoly( _pz, w ) )

	def copy( self, src ):

		super().copy( src )
		self.points = [ p.clone() for p in src.points ]
		self.closed = src.closed
		self.curveType = src.curveType
		self.tension = src.tension
		return self

	def clone( self ):
		return CatmullRomCurve3().copy( self )


# ------------------------------------------------------------------ ShapeUtils

def _shapeArea( pts ):
	"""Signed area of a 2D point loop (positive counter-clockwise)."""

	n = len( pts )
	a = 0
	p = n - 1

	for q in range( n ):

		a += pts[ p ].x * pts[ q ].y - pts[ q ].x * pts[ p ].y
		p = q

	return a * 0.5


def _shapeCross( a, b, c ):
	return ( b.x - a.x ) * ( c.y - b.y ) - ( b.y - a.y ) * ( c.x - b.x )


def _shapeSame( a, b ):
	return a.x == b.x and a.y == b.y


def _inTri( a, b, c, p ):

	return ( c.x - p.x ) * ( a.y - p.y ) >= ( a.x - p.x ) * ( c.y - p.y ) and \
		( a.x - p.x ) * ( b.y - p.y ) >= ( b.x - p.x ) * ( a.y - p.y ) and \
		( b.x - p.x ) * ( c.y - p.y ) >= ( c.x - p.x ) * ( b.y - p.y )


def _polyArea( idx, P ):

	a = 0
	j = len( idx ) - 1

	for i in range( len( idx ) ):

		a += P[ idx[ j ] ].x * P[ idx[ i ] ].y - P[ idx[ i ] ].x * P[ idx[ j ] ].y
		j = i

	return a


def _inTriCCW( t, p ):

	a, b, c = t if _shapeCross( t[ 0 ], t[ 1 ], t[ 2 ] ) >= 0 else [ t[ 0 ], t[ 2 ], t[ 1 ] ]
	return _inTri( a, b, c, p )


def _bridge( poly, hole, P ):
	"""Splice `hole` (CW) into `poly` (CCW) via a bridge from the hole's rightmost
	vertex to a visible outer vertex (ray cast toward +x).
	"""

	hm = 0

	for i in range( 1, len( hole ) ):
		if P[ hole[ i ] ].x > P[ hole[ hm ] ].x:
			hm = i

	M = P[ hole[ hm ] ]

	best = math.inf
	bi = - 1
	n = len( poly )

	for i in range( n ):

		a = P[ poly[ i ] ]; b = P[ poly[ ( i + 1 ) % n ] ]

		if ( a.y <= M.y and M.y <= b.y ) or ( b.y <= M.y and M.y <= a.y ):

			if a.y == b.y:
				continue

			x = a.x + ( M.y - a.y ) * ( b.x - a.x ) / ( b.y - a.y )

			if x >= M.x and x < best:

				best = x
				bi = i if a.x > b.x else ( i + 1 ) % n

	if bi < 0:
		return poly

	# A reflex vertex inside triangle (M, I, P) may block the view; take the one
	# with the smallest angle to the ray instead.
	I = Vector2( best, M.y )
	Pv = P[ poly[ bi ] ]

	if best != Pv.x or M.y != Pv.y:

		tanMin = math.inf
		tri = [ M, Pv, I ] if Pv.y < M.y else [ M, I, Pv ]
		start = bi

		for k in range( n ):

			i = ( start + k ) % n; v = P[ poly[ i ] ]

			if v.x < M.x or i == start:
				continue

			pv = P[ poly[ ( i + n - 1 ) % n ] ]; nv = P[ poly[ ( i + 1 ) % n ] ]
			reflex = _shapeCross( pv, v, nv ) < 0

			if not reflex and not ( v.x == M.x and v.y == M.y ):
				continue

			if not _inTriCCW( tri, v ):
				continue

			tan = abs( M.y - v.y ) / ( v.x - M.x or 1e-12 )

			if tan < tanMin or ( tan == tanMin and v.x < Pv.x ):
				tanMin = tan; bi = i; Pv = v

	# A vertex may occur several times (earlier bridges); connect through the
	# occurrence whose interior wedge contains M.
	for k in range( n ):

		if not _shapeSame( P[ poly[ k ] ], Pv ):
			continue

		pv = P[ poly[ ( k + n - 1 ) % n ] ]; nv = P[ poly[ ( k + 1 ) % n ] ]
		l1 = _shapeCross( pv, Pv, M ) >= 0; l2 = _shapeCross( Pv, nv, M ) >= 0

		if ( l1 and l2 ) if _shapeCross( pv, Pv, nv ) >= 0 else ( l1 or l2 ):
			bi = k
			break

	rot = hole[ hm : ] + hole[ : hm ]
	return poly[ : bi + 1 ] + rot + [ hole[ hm ], poly[ bi ] ] + poly[ bi + 1 : ]


def _earClip( poly, P, out ):

	idx = list( poly )
	guard = 0

	while len( idx ) > 3 and guard < 100000:

		guard += 1
		n = len( idx )
		clipped = False

		for _pass in range( 2 ):

			if clipped:
				break

			for i in range( n ):

				ia = idx[ ( i + n - 1 ) % n ]; ib = idx[ i ]; ic = idx[ ( i + 1 ) % n ]
				a = P[ ia ]; b = P[ ib ]; c = P[ ic ]
				cr = _shapeCross( a, b, c )

				if cr <= 0 if _pass == 0 else cr < 0:
					continue

				ear = True

				if cr > 0:

					for k in range( n ):

						v = P[ idx[ k ] ]

						if _shapeSame( v, a ) or _shapeSame( v, b ) or _shapeSame( v, c ):
							continue

						if _inTri( a, b, c, v ):
							ear = False
							break

				if ear:

					out.append( [ ia, ib, ic ] )
					idx.pop( i )
					clipped = True
					break

		if not clipped:

			# self-intersecting or degenerate input: cut the least reflex vertex
			bi = 0; bc = - math.inf

			for i in range( n ):

				c = _shapeCross( P[ idx[ ( i + n - 1 ) % n ] ], P[ idx[ i ] ], P[ idx[ ( i + 1 ) % n ] ] )

				if c > bc:
					bc = c; bi = i

			out.append( [ idx[ ( bi + n - 1 ) % n ], idx[ bi ], idx[ ( bi + 1 ) % n ] ] )
			idx.pop( bi )

	if len( idx ) == 3:
		out.append( [ idx[ 0 ], idx[ 1 ], idx[ 2 ] ] )

	return out


class ShapeUtils:
	"""2D polygon helpers: signed area, winding test and triangulation of a contour
	with holes (hole bridging + ear clipping) -- the same output as the engine's.
	"""

	@staticmethod
	def area( pts ):
		return _shapeArea( pts )

	@staticmethod
	def isClockWise( pts ):
		return _shapeArea( pts ) < 0

	@staticmethod
	def triangulateShape( contour, holes = None ):
		"""Returns index triples into contour + holes. Like three.js, a duplicated
		closing point is removed from each input array in place.
		"""

		if holes is None:
			holes = []

		_removeDupEndPts( contour )

		for h in holes:
			_removeDupEndPts( h )

		P = list( contour )

		for h in holes:
			P += h

		poly = list( range( len( contour ) ) )
		outerCW = _polyArea( poly, P ) < 0

		if outerCW:
			poly.reverse()

		off = len( contour )
		hs = []

		for h in holes:

			idx = [ off + i for i in range( len( h ) ) ]
			off += len( h )

			if _polyArea( idx, P ) > 0:
				idx.reverse()

			if len( idx ) >= 3:
				hs.append( idx )

		hs.sort( key = lambda h: - max( P[ i ].x for i in h ) )

		for h in hs:
			poly = _bridge( poly, h, P )

		tris = _earClip( poly, P, [] )

		if outerCW:

			for t in tris:
				s = t[ 0 ]; t[ 0 ] = t[ 2 ]; t[ 2 ] = s

		return tris


def _removeDupEndPts( pts ):

	l = len( pts )

	if l > 2 and pts[ l - 1 ].x == pts[ 0 ].x and pts[ l - 1 ].y == pts[ 0 ].y:
		pts.pop()
