# Lines plan of the procedural boat -- pure-Python port of
# src/world/boat/HullLines.js.
#
# Lines plan of an ~8.2 m Downeast lobster boat, in the boat's local frame:
# +Z forward (bow), +Y up, +X port. y = 0 is the design waterline, x = 0 the
# centerline and z = 0 the middle of the waterline.
#
# The hull surface is parameterized by t (0 = transom .. 1 = stem head) and a
# section index j running from the keel (j = 0) up to the sheer. Each section
# is a centripetal Catmull-Rom spline through seven control points (keel,
# garboard, bilge start, bilge, bilge end, topsides, sheer). Aft sections are
# planar (constant z); forward of t = 0.55 they tilt progressively so that the
# last one (t = 1) traces the raked stem with a rounded forefoot.
#
# NOTE the JS types: sections and the setback table are Float64Array, but the
# seabed-of-the-hull height field `bottom` is a Float32Array -- so its heights are
# rounded to float32 and the volumes / centers are computed from those rounded
# values. That rounding is reproduced here (Float32Array from mathutil).
#
# Pure Python 3, standard library only (no numpy, no bpy).

import math

from mathutil import Float32Array, Float64Array, Vector3, js_hypot


def clamp01( x ):
	return min( 1, max( 0, x ) )


def lerp( a, b, t ):
	return a + ( b - a ) * t


def sstep( a, b, x ):

	t = clamp01( ( x - a ) / ( b - a ) )
	return t * t * ( 3 - 2 * t )


# Intervals per spline span (K-G, G-B1, B1-C, C-B2, B2-M, M-S) at density 1.
SPANS = [ 4, 4, 3, 3, 8, 10 ]

RHO_SEAWATER = 1025


class HullSample:
	"""One buoyancy sample: a patch of the immersed canoe body.

	position is the sample point (see buildHullSamples), area the patch's waterplane
	area, depth the mean hull depth below y = 0 and bottomY the hull bottom there.
	"""

	def __init__( self, position, area, depth, bottomY ):

		self.position = position
		self.area = area
		self.depth = depth
		self.bottomY = bottomY


class HullLines:

	def __init__( self ):

		self.zAft = - 3.9
		self.zBow = 4.3
		self.length = self.zBow - self.zAft

		self.deckY = 0.35 # cockpit sole
		self.shell = 0.07 # outer skin to inner lining at the sheer
		self.houseBack = - 0.35 # aft end of the wheelhouse side walls
		self.houseFront = 1.45 # wheelhouse front (base of the windshield)

		# Stem: straight rake above the forefoot, circular forefoot below.
		k = 0.2; R = 0.45
		yF = self.keelY( 1 ); yTip = self.sheerY( 1 )
		zW = self.zBow - k * yTip
		q = math.sqrt( 1 + k * k )
		yc = yF + R
		self.stem = { 'k': k, 'R': R, 'yF': yF, 'zW': zW, 'yc': yc, 'zc': zW + k * yc - R * q, 'yT': yc - R * k / q }

		self._setback = {}
		self._sectionCache = {}

		self.analyze()

	# ---------------------------------------------------------------- design curves

	def sheerZ( self, t ):

		return self.zAft + self.length * t

	def tAtSheerZ( self, z ):

		return clamp01( ( z - self.zAft ) / self.length )

	def sheerY( self, t ):

		a = max( 0, 1 - t / 0.2 )
		return 0.98 + 0.62 * math.pow( t, 2.2 ) + 0.04 * a * a

	def sheerX( self, t ):

		if t <= 0.42:

			a = 1 - t / 0.42
			return 1.45 - 0.17 * a * a

		s = ( t - 0.42 ) / 0.58
		return 1.45 * ( 1 - math.pow( s, 2.6 ) )

	def keelY( self, t ):

		if t <= 0.45:
			return - 0.23 - 0.22 * math.sin( math.pi * 0.5 * t / 0.45 )

		return - 0.45 + 0.33 * math.pow( sstep( 0.45, 1, t ), 1.6 )

	def chineX( self, t ):

		if t <= 0.42:

			a = 1 - t / 0.42
			return 1.26 - 0.1 * a * a

		s = ( t - 0.42 ) / 0.58
		return 1.26 * math.pow( max( 0, 1 - math.pow( s, 1.7 ) ), 1.15 )

	def chineY( self, t ):

		return lerp( 0.01, - 0.045, sstep( 0, 0.45, t ) ) + 0.33 * math.pow( sstep( 0.5, 1, t ), 1.3 )

	def bilgeTangent( self, t ):

		return lerp( 0.2, 0.24, sstep( 0, 0.4, t ) ) * ( 1 - 0.55 * sstep( 0.6, 1, t ) )

	def bottomConvexity( self, t ):

		return lerp( 0.025, 0.012, sstep( 0.55, 0.95, t ) )

	def flare( self, t ):

		return lerp( - 0.012, 0.075, sstep( 0.3, 0.85, t ) ) * ( 1 - 0.3 * sstep( 0.9, 1, t ) )

	def bowBlend( self, t ):

		return ( ( t - 0.55 ) / 0.45 ) ** 2 if t > 0.55 else 0

	def stemZ( self, y ):

		s = self.stem

		if y >= s[ 'yT' ]:
			return s[ 'zW' ] + s[ 'k' ] * y

		dy = y - s[ 'yc' ]
		return s[ 'zc' ] + math.sqrt( max( 0, s[ 'R' ] * s[ 'R' ] - dy * dy ) )

	# ---------------------------------------------------------------- sections

	def controlPoints( self, t ):

		K = [ 0, self.keelY( t ) ]
		C0 = [ self.chineX( t ), self.chineY( t ) ]
		S = [ self.sheerX( t ), self.sheerY( t ) ]

		bx = C0[ 0 ] - K[ 0 ]; by = C0[ 1 ] - K[ 1 ]
		lb = math.hypot( bx, by )
		dbx = bx / lb; dby = by / lb
		tx = S[ 0 ] - C0[ 0 ]; ty = S[ 1 ] - C0[ 1 ]
		lt = math.hypot( tx, ty )
		dtx = tx / lt; dty = ty / lt

		# shape offsets fade out where the section collapses into the stem
		w = sstep( 0, 0.3, C0[ 0 ] )
		r = min( self.bilgeTangent( t ), 0.3 * lb, 0.3 * lt )

		convex = self.bottomConvexity( t ) * lb * w
		G = [ K[ 0 ] + dbx * lb * 0.5 + dby * convex, K[ 1 ] + dby * lb * 0.5 - dbx * convex ]
		B1 = [ C0[ 0 ] - dbx * r, C0[ 1 ] - dby * r ]
		B2 = [ C0[ 0 ] + dtx * r, C0[ 1 ] + dty * r ]

		# midpoint of a circular fillet between bottom and topsides lines
		psi = math.acos( min( 1, max( - 1, dbx * dtx + dby * dty ) ) )
		mx = dtx - dbx; my = dty - dby
		ml = math.hypot( mx, my )
		e = r * math.tan( psi / 4 ) / ml if ml > 1e-6 else 0
		Cm = [ C0[ 0 ] + mx * e, C0[ 1 ] + my * e ]

		fl = self.flare( t ) * lt * w # > 0: concave flare
		M = [ C0[ 0 ] + dtx * lt * 0.5 - dty * fl, C0[ 1 ] + dty * lt * 0.5 + dtx * fl ]

		return [ K, G, B1, Cm, B2, M, S ]

	def sectionPoints( self, t, density = 1 ):
		"""Section points (x >= 0, y) from keel to sheer as a flat [x0, y0, x1, y1, ...] array."""

		key = str( t ) + ':' + str( density )
		cached = self._sectionCache.get( key )

		if cached is not None:
			return cached

		cp = self.controlPoints( t )
		n = len( cp )
		ext = [
			[ 2 * cp[ 0 ][ 0 ] - cp[ 1 ][ 0 ], 2 * cp[ 0 ][ 1 ] - cp[ 1 ][ 1 ] ],
		] + cp + [
			[ 2 * cp[ n - 1 ][ 0 ] - cp[ n - 2 ][ 0 ], 2 * cp[ n - 1 ][ 1 ] - cp[ n - 2 ][ 1 ] ],
		]

		# `density` is a whole number of intervals per span: JS throws (RangeError) when the
		# element count is not integral, so fail the same way rather than silently rounding.
		count = sum( SPANS ) * density + 1

		if count != int( count ):
			raise ValueError( 'HullLines.sectionPoints: density must give a whole number of points' )

		count = int( count )
		out = Float64Array( count * 2 )
		k = 0
		p = [ 0, 0 ]

		for s in range( len( SPANS ) ):

			# JS loops `i < steps` with a possibly fractional steps: ceil is that loop bound
			steps = math.ceil( SPANS[ s ] * density )

			for i in range( steps ):

				catmullRom( ext[ s ], ext[ s + 1 ], ext[ s + 2 ], ext[ s + 3 ], i / steps, p )
				out[ k ] = max( 0, p[ 0 ] ); k += 1
				out[ k ] = p[ 1 ]; k += 1

		out[ k ] = cp[ n - 1 ][ 0 ]; k += 1
		out[ k ] = cp[ n - 1 ][ 1 ]; k += 1

		if len( self._sectionCache ) > 4096:
			self._sectionCache.clear()

		self._sectionCache[ key ] = out
		return out

	def setback( self, density = 1 ):
		"""Longitudinal setback of each section point, so the t = 1 section traces the stem."""

		H = self._setback.get( density )

		if H is not None:
			return H

		sec = self.sectionPoints( 1, density )
		n = len( sec ) // 2
		H = Float64Array( n )

		for j in range( n ):
			H[ j ] = self.zBow - self.stemZ( sec[ 2 * j + 1 ] )

		H[ n - 1 ] = 0
		self._setback[ density ] = H
		return H

	def sectionCount( self, density = 1 ):

		return sum( SPANS ) * density + 1

	def station( self, t, density = 1 ):
		"""Surface points of station t as Vector3s (port side)."""

		sec = self.sectionPoints( t, density )
		H = self.setback( density )
		zs = self.sheerZ( t ); D = self.bowBlend( t )
		pts = []

		for j in range( len( H ) ):
			pts.append( Vector3( sec[ 2 * j ], sec[ 2 * j + 1 ], zs - D * H[ j ] ) )

		return pts

	def stationParams( self, count ):
		"""Station parameters, denser toward the bow where the sections change quickly.

		`count` is the number of stations (a whole number; JS walks an accumulator with it).
		"""

		count = int( count )

		def density( t ):
			return 1 + 2.2 * sstep( 0.62, 1, t ) + 0.5 * ( 1 - sstep( 0, 0.08, t ) )

		N = 2000
		cum = Float64Array( N + 1 )

		for i in range( 1, N + 1 ):
			cum[ i ] = cum[ i - 1 ] + density( ( i - 0.5 ) / N ) / N

		total = cum[ N ]
		ts = []
		k = 0

		for i in range( count ):

			target = total * i / ( count - 1 )

			while k < N and cum[ k + 1 ] < target:
				k += 1

			f = ( target - cum[ k ] ) / max( 1e-12, cum[ k + 1 ] - cum[ k ] )
			ts.append( 1 if i == count - 1 else ( k + clamp01( f ) ) / N )

		return ts

	def halfBreadth( self, t, y ):
		"""Outer half-breadth of the station t at height y (topsides, above the bilge)."""

		sec = self.sectionPoints( t, 2 )
		n = len( sec ) // 2

		if y >= sec[ 2 * n - 1 ]:
			return sec[ 2 * n - 2 ]

		for j in range( n - 2, - 1, - 1 ):

			y0 = sec[ 2 * j + 1 ]; y1 = sec[ 2 * j + 3 ]

			if y >= y0 and y <= y1:

				f = ( y - y0 ) / max( 1e-9, y1 - y0 )
				return lerp( sec[ 2 * j ], sec[ 2 * j + 2 ], f )

		return 0

	def tAt( self, z, y ):
		"""Station parameter whose surface passes through longitudinal position z at height y."""

		lo = 0; hi = 1

		for i in range( 40 ):

			mid = ( lo + hi ) * 0.5

			if self.zOnStation( mid, y ) < z:
				lo = mid
			else:
				hi = mid

		return ( lo + hi ) * 0.5

	def zOnStation( self, t, y ):
		"""Longitudinal position of station t at height y (accounts for the tilted bow sections)."""

		D = self.bowBlend( t )

		if D == 0:
			return self.sheerZ( t )

		sec = self.sectionPoints( t, 2 )
		H = self.setback( 2 )
		n = len( sec ) // 2
		h = 0

		if y <= sec[ 1 ]:
			h = H[ 0 ]
		elif y >= sec[ 2 * n - 1 ]:
			h = 0
		else:

			for j in range( n - 1 ):

				y0 = sec[ 2 * j + 1 ]; y1 = sec[ 2 * j + 3 ]

				if y >= y0 and y <= y1:

					h = lerp( H[ j ], H[ j + 1 ], ( y - y0 ) / max( 1e-9, y1 - y0 ) )
					break

		return self.sheerZ( t ) - D * h

	def hullXAt( self, z, y ):
		"""Outer hull half-breadth at an arbitrary (z, y) on the topsides."""

		return self.halfBreadth( self.tAt( z, y ), y )

	# ---------------------------------------------------------------- hydrostatics

	def analyze( self ):

		density = 3
		NT = 480
		H = self.setback( density )
		nj = len( H )

		# Waterline (y = 0 crossing of every station).
		wl = []

		for i in range( NT + 1 ):

			t = i / NT
			sec = self.sectionPoints( t, density )
			zs = self.sheerZ( t ); D = self.bowBlend( t )

			for j in range( nj - 1 ):

				y0 = sec[ 2 * j + 1 ]; y1 = sec[ 2 * j + 3 ]

				if y0 <= 0 and y1 > 0:

					f = - y0 / ( y1 - y0 )
					wl.append( [ zs - D * lerp( H[ j ], H[ j + 1 ], f ), lerp( sec[ 2 * j ], sec[ 2 * j + 2 ], f ) ] )
					break

		wl.sort( key = lambda ab: ab[ 0 ] )
		self.wlStart = wl[ 0 ][ 0 ]
		self.wlEnd = wl[ len( wl ) - 1 ][ 0 ]

		TN = 512
		self._beamTable = Float64Array( TN + 1 )
		self._tableZ0 = self.wlStart
		self._tableDz = ( self.wlEnd - self.wlStart ) / TN
		k = 0

		for i in range( TN + 1 ):

			z = self.wlStart + i * self._tableDz

			while k < len( wl ) - 2 and wl[ k + 1 ][ 0 ] < z:
				k += 1

			a = wl[ k ]; b = wl[ k + 1 ]
			f = clamp01( ( z - a[ 0 ] ) / max( 1e-9, b[ 0 ] - a[ 0 ] ) )
			self._beamTable[ i ] = lerp( a[ 1 ], b[ 1 ], f )

		# Rasterize the immersed canoe body (port half) into a bottom height field.
		cell = 0.02
		x0 = 0; z0 = self.zAft - 0.02
		nx = math.ceil( 1.5 / cell ); nz = math.ceil( ( self.wlEnd + 0.05 - z0 ) / cell )
		bottom = Float32Array( nx * nz ).fill( math.inf )
		grid = []

		for i in range( NT + 1 ):

			t = i / NT
			sec = self.sectionPoints( t, density )
			zs = self.sheerZ( t ); D = self.bowBlend( t )
			row = Float64Array( nj * 3 )

			for j in range( nj ):

				row[ 3 * j ] = sec[ 2 * j ]
				row[ 3 * j + 1 ] = sec[ 2 * j + 1 ]
				row[ 3 * j + 2 ] = zs - D * H[ j ]

			grid.append( row )

		def tri( a, b, c ):

			# a, b, c: [x, y, z]
			if min( a[ 1 ], b[ 1 ], c[ 1 ] ) > 0.01:
				return

			minX = min( a[ 0 ], b[ 0 ], c[ 0 ] ); maxX = max( a[ 0 ], b[ 0 ], c[ 0 ] )
			minZ = min( a[ 2 ], b[ 2 ], c[ 2 ] ); maxZ = max( a[ 2 ], b[ 2 ], c[ 2 ] )
			i0 = max( 0, math.ceil( ( minX - x0 ) / cell - 0.5 ) ); i1 = min( nx - 1, math.floor( ( maxX - x0 ) / cell - 0.5 ) )
			k0 = max( 0, math.ceil( ( minZ - z0 ) / cell - 0.5 ) ); k1 = min( nz - 1, math.floor( ( maxZ - z0 ) / cell - 0.5 ) )

			if i0 > i1 or k0 > k1:
				return

			det = ( b[ 2 ] - c[ 2 ] ) * ( a[ 0 ] - c[ 0 ] ) + ( c[ 0 ] - b[ 0 ] ) * ( a[ 2 ] - c[ 2 ] )

			if abs( det ) < 1e-12:
				return

			for kk in range( k0, k1 + 1 ):

				pz = z0 + ( kk + 0.5 ) * cell

				for ii in range( i0, i1 + 1 ):

					px = x0 + ( ii + 0.5 ) * cell
					l1 = ( ( b[ 2 ] - c[ 2 ] ) * ( px - c[ 0 ] ) + ( c[ 0 ] - b[ 0 ] ) * ( pz - c[ 2 ] ) ) / det
					l2 = ( ( c[ 2 ] - a[ 2 ] ) * ( px - c[ 0 ] ) + ( a[ 0 ] - c[ 0 ] ) * ( pz - c[ 2 ] ) ) / det
					l3 = 1 - l1 - l2

					if l1 < - 1e-9 or l2 < - 1e-9 or l3 < - 1e-9:
						continue

					y = l1 * a[ 1 ] + l2 * b[ 1 ] + l3 * c[ 1 ]
					idx = kk * nx + ii

					if y < bottom[ idx ]:
						bottom[ idx ] = y

		A = [ 0, 0, 0 ]; B = [ 0, 0, 0 ]; C = [ 0, 0, 0 ]; Dp = [ 0, 0, 0 ]

		def get( row, j, out ):

			out[ 0 ] = row[ 3 * j ]; out[ 1 ] = row[ 3 * j + 1 ]; out[ 2 ] = row[ 3 * j + 2 ]
			return out

		for i in range( NT ):

			for j in range( nj - 1 ):

				get( grid[ i ], j, A ); get( grid[ i + 1 ], j, B ); get( grid[ i + 1 ], j + 1, C ); get( grid[ i ], j + 1, Dp )
				tri( A, B, C ); tri( A, C, Dp )

		self._field = { 'bottom': bottom, 'nx': nx, 'nz': nz, 'cell': cell, 'x0': x0, 'z0': z0 }

		# Waterplane area, displaced volume, centers.
		area = 0; volume = 0; mz = 0; vz = 0; vy = 0

		for kk in range( nz ):

			pz = z0 + ( kk + 0.5 ) * cell

			for ii in range( nx ):

				y = bottom[ kk * nx + ii ]

				if not ( y < 0 ):
					continue

				dA = cell * cell * 2
				area += dA
				mz += dA * pz
				volume += dA * - y
				vz += dA * - y * pz
				vy += dA * - y * ( y * 0.5 )

		self.waterplaneArea = area
		self.canoeVolume = volume
		self.centerOfFlotationZ = mz / area
		self.centerOfBuoyancy = Vector3( 0, vy / volume, vz / volume )

	# Waterplane half-beam at longitudinal position z (0 outside the waterline).
	def halfBeamAt( self, z ):

		if z < self.wlStart or z > self.wlEnd:
			return 0

		f = ( z - self._tableZ0 ) / self._tableDz
		i = min( len( self._beamTable ) - 2, max( 0, math.floor( f ) ) )
		return lerp( self._beamTable[ i ], self._beamTable[ i + 1 ], clamp01( f - i ) )

	# Canoe-body depth below the waterline (at the centerline) at z, 0 outside.
	def draftAt( self, z ):

		field = self._field
		bottom = field[ 'bottom' ]; nx = field[ 'nx' ]; nz = field[ 'nz' ]; cell = field[ 'cell' ]; z0 = field[ 'z0' ]
		f = ( z - z0 ) / cell - 0.5

		if f < - 0.5 or f > nz - 0.5:
			return 0

		k = min( nz - 2, max( 0, math.floor( f ) ) )
		a = bottom[ k * nx ]; b = bottom[ ( k + 1 ) * nx ]
		da = - a if a < 0 else 0; db = - b if b < 0 else 0
		return lerp( da, db, clamp01( f - k ) )

	# Hull bottom height (canoe body) at (x, z); +Infinity outside the immersed footprint.
	def bottomAt( self, x, z ):

		field = self._field
		bottom = field[ 'bottom' ]; nx = field[ 'nx' ]; nz = field[ 'nz' ]; cell = field[ 'cell' ]; x0 = field[ 'x0' ]; z0 = field[ 'z0' ]
		i = math.floor( ( abs( x ) - x0 ) / cell ); k = math.floor( ( z - z0 ) / cell )

		if i < 0 or i >= nx or k < 0 or k >= nz:
			return math.inf

		return bottom[ k * nx + i ]

	def buildHullSamples( self, slices = 8 ):
		"""Buoyancy samples: `slices` longitudinal slices x 4 lateral strips (two per side).

		Each sample carries the waterplane area of its patch; its y is the mean hull
		depth of the patch, so sum(area * -y) equals the displaced canoe-body volume,
		and x sits at the patch's radius of gyration so roll stiffness is preserved.

		`slices` is a whole number (JS indexes its accumulator with it directly).
		"""

		slices = int( slices )
		field = self._field
		bottom = field[ 'bottom' ]; nx = field[ 'nx' ]; nz = field[ 'nz' ]; cell = field[ 'cell' ]; x0 = field[ 'x0' ]; z0 = field[ 'z0' ]
		L = self.wlEnd - self.wlStart
		acc = []

		for s in range( slices ):
			acc.append( [ { 'a': 0, 'x2': 0, 'z': 0, 'v': 0 }, { 'a': 0, 'x2': 0, 'z': 0, 'v': 0 } ] )

		for kk in range( nz ):

			pz = z0 + ( kk + 0.5 ) * cell
			s = min( slices - 1, max( 0, math.floor( ( pz - self.wlStart ) / L * slices ) ) )
			hb = self.halfBeamAt( pz )

			for ii in range( nx ):

				y = bottom[ kk * nx + ii ]

				if not ( y < 0 ):
					continue

				px = x0 + ( ii + 0.5 ) * cell
				strip = 1 if hb > 0 and px > hb * 0.5 else 0
				dA = cell * cell
				c = acc[ s ][ strip ]
				c[ 'a' ] += dA; c[ 'x2' ] += dA * px * px; c[ 'z' ] += dA * pz; c[ 'v' ] += dA * - y

		samples = []

		for s in range( slices ):

			for strip in range( 2 ):

				c = acc[ s ][ strip ]

				if c[ 'a' ] <= 0:
					continue

				x = math.sqrt( c[ 'x2' ] / c[ 'a' ] ); z = c[ 'z' ] / c[ 'a' ]; y = - c[ 'v' ] / c[ 'a' ]
				bottomY = min( 0, self.bottomAt( x, z ) )
				samples.append( HullSample( Vector3( x, y, z ), c[ 'a' ], - y, bottomY ) )
				samples.append( HullSample( Vector3( - x, y, z ), c[ 'a' ], - y, bottomY ) )

		return samples


def catmullRom( p0, p1, p2, p3, u, out ):
	"""Centripetal Catmull-Rom between p1 and p2."""

	d01 = max( 1e-5, math.sqrt( js_hypot( p1[ 0 ] - p0[ 0 ], p1[ 1 ] - p0[ 1 ] ) ) )
	d12 = max( 1e-5, math.sqrt( js_hypot( p2[ 0 ] - p1[ 0 ], p2[ 1 ] - p1[ 1 ] ) ) )
	d23 = max( 1e-5, math.sqrt( js_hypot( p3[ 0 ] - p2[ 0 ], p3[ 1 ] - p2[ 1 ] ) ) )
	t0 = 0; t1 = d01; t2 = t1 + d12; t3 = t2 + d23
	t = t1 + ( t2 - t1 ) * u

	for c in range( 2 ):

		a1 = ( ( t1 - t ) * p0[ c ] + ( t - t0 ) * p1[ c ] ) / ( t1 - t0 )
		a2 = ( ( t2 - t ) * p1[ c ] + ( t - t1 ) * p2[ c ] ) / ( t2 - t1 )
		a3 = ( ( t3 - t ) * p2[ c ] + ( t - t2 ) * p3[ c ] ) / ( t3 - t2 )
		b1 = ( ( t2 - t ) * a1 + ( t - t0 ) * a2 ) / ( t2 - t0 )
		b2 = ( ( t3 - t ) * a2 + ( t - t1 ) * a3 ) / ( t3 - t1 )
		out[ c ] = ( ( t2 - t ) * b1 + ( t - t1 ) * b2 ) / ( t2 - t1 )

	return out
