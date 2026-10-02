# Running gear -- pure-Python port of src/world/boat/Running.js.
#
# The two geometries the boat swims on: the four-blade propeller (its hub, and the blades
# lofted from a pitch/chord/skew law with a thickness that vanishes at the edges, merged for
# one draw call) and the balanced spade rudder (a NACA-ish foil swept between two chord
# scales, capped top and bottom, on a stock with a heel bearing). PROP and RUDDER are the
# boat-frame anchor points BoatModel exposes, and RUDDER.maxAngle the steering limit.
#
# Fidelity notes, all copied deliberately from the JS:
#   - `lathe`, `gridSurface`, `fanCap`, `orientTowards`, `prepare`, `mergePrepared` and
#     `cylinder` come from the ported GeoKit, so the vertex emission order, index winding
#     and uv conventions are already the JS ones.
#   - the propeller's blade frame uses Math.atan2 and Math.pow: atan2 is math.atan2 and pow
#     is math.pow on the same doubles, so the mid-surface rows land on the same floats.
#   - `prepare()` is called on the blade faces *after* computeVertexNormals, so it keeps the
#     normals it was given (only the color/aux/uv defaults are filled in).
#
# Pure Python 3, standard library only (no numpy, no bpy).

import math

from mathutil import Matrix4, Vector3
from geokit import (
	cylinder, fanCap, gridSurface, lathe, mergePrepared, orientTowards, prepare,
)
from hull import PALETTE


def V( x, y, z ):

	return Vector3( x, y, z )


# Propeller hub centre (boat frame) and rudder stock pivot.
PROP = { 'position': V( 0, - 0.53, - 3.3 ), 'radius': 0.21, 'blades': 4, 'pitch': 0.42 }
RUDDER = { 'pivot': V( 0, - 0.52, - 3.58 ), 'top': - 0.33, 'bottom': - 0.72, 'lead': 0.08, 'trail': - 0.27, 'maxAngle': 0.61 }

BRONZE = { 'color': 0xc8905a, 'rough': 0.3, 'metal': 1 }


# Four-blade right-handed propeller. Local frame: shaft axis +Z (forward); a positive
# rotation about +Z (clockwise seen from astern) drives the boat ahead.
def propellerGeometry():

	R = PROP[ 'radius' ]; blades = PROP[ 'blades' ]; P = PROP[ 'pitch' ]
	list_ = []

	hub = lathe( [ [ 0, - 0.09 ], [ 0.015, - 0.085 ], [ 0.03, - 0.068 ], [ 0.043, - 0.04 ], [ 0.048, 0.0 ], [ 0.047, 0.04 ], [ 0.042, 0.06 ], [ 0, 0.06 ] ], 18 )
	hub.applyMatrix4( Matrix4().makeRotationX( math.pi / 2 ) )
	list_.append( prepare( hub, BRONZE ) )

	rh = 0.042
	NR = 9; NC = 9

	for b in range( blades ):

		theta0 = b * math.pi * 2 / blades
		mid = []

		for i in range( NR ):

			rho = i / ( NR - 1 )
			r = rh + ( R - rh ) * rho
			chord = 0.15 * math.sqrt( max( 0, 1 - math.pow( rho, 2.4 ) ) ) * ( 0.62 + 0.38 * math.sin( math.pi * min( 1, rho * 1.4 ) ) ) + 0.002
			phi = math.atan2( P, 2 * math.pi * r )
			skew = 0.32 * rho * rho
			row = []

			for k in range( NC ):

				c = - 1 + 2 * k / ( NC - 1 )
				s = c * chord * 0.5
				th = theta0 - skew + s * math.cos( phi ) / r
				row.append( V( math.cos( th ) * r, math.sin( th ) * r, s * math.sin( phi ) ) )

			mid.append( row )

		# thickness along the local surface normal, vanishing at the edges and tip
		face = []; back = []

		for i in range( NR ):

			rho = i / ( NR - 1 )
			fr = []; br = []

			for k in range( NC ):

				c = - 1 + 2 * k / ( NC - 1 )
				du = mid[ min( NR - 1, i + 1 ) ][ k ].clone().sub( mid[ max( 0, i - 1 ) ][ k ] )
				dv = mid[ i ][ min( NC - 1, k + 1 ) ].clone().sub( mid[ i ][ max( 0, k - 1 ) ] )
				n = du.cross( dv ).normalize()
				t = 0.013 * ( 1 - 0.8 * rho ) * math.sqrt( max( 0, 1 - c * c ) ) * ( 1 - math.pow( rho, 8 ) )
				fr.append( mid[ i ][ k ].clone().addScaledVector( n, t * 0.5 ) )
				br.append( mid[ i ][ k ].clone().addScaledVector( n, - t * 0.5 ) )

			face.append( fr ); back.append( br )

		gf = gridSurface( face )
		gb = gridSurface( back, { 'flip': True } )
		# make sure each side faces away from the other
		probe = face[ 4 ][ 4 ].clone().sub( back[ 4 ][ 4 ] )
		orientTowards( gf, probe )
		orientTowards( gb, probe.clone().negate() )
		gf.computeVertexNormals()
		gb.computeVertexNormals()
		list_.append( prepare( gf, BRONZE ) ); list_.append( prepare( gb, BRONZE ) )

	return mergePrepared( list_ )


# Balanced spade rudder on a stock; local origin on the stock axis at mid blade.
def rudderGeometry():

	pivot = RUDDER[ 'pivot' ]; top = RUDDER[ 'top' ]; bottom = RUDDER[ 'bottom' ]; lead = RUDDER[ 'lead' ]; trail = RUDDER[ 'trail' ]
	yTop = top - pivot.y; yBot = bottom - pivot.y

	def foil( chordScale, y ):

		c = ( lead - trail ) * chordScale
		z0 = lead * chordScale
		pts = []
		N = 12

		for i in range( N + 1 ):

			x = 1 - math.cos( math.pi * i / N ) * 0.5 - 0.5 # cosine spacing 0..1
			yt = 5 * 0.14 * ( 0.2969 * math.sqrt( x ) - 0.126 * x - 0.3516 * x * x + 0.2843 * x ** 3 - 0.1036 * x ** 4 )
			pts.append( [ z0 - x * c, yt * c ] )

		loop = []

		for i in range( N + 1 ):
			loop.append( V( pts[ i ][ 1 ], y, pts[ i ][ 0 ] ) )

		for i in range( N - 1, 0, - 1 ):
			loop.append( V( - pts[ i ][ 1 ], y, pts[ i ][ 0 ] ) )

		return loop

	bottomLoop = foil( 0.92, yBot ); topLoop = foil( 1.0, yTop )
	rows = [ bottomLoop + [ bottomLoop[ 0 ].clone() ], topLoop + [ topLoop[ 0 ].clone() ] ]
	side = gridSurface( rows )
	# normals must point away from the stock axis
	p = side.attributes[ 'position' ]; n = side.attributes[ 'normal' ]
	dot = 0
	a = V( 0, 0, 0 ); b = V( 0, 0, 0 )

	for i in range( p.count ):

		a.fromBufferAttribute( p, i ); b.fromBufferAttribute( n, i )
		dot += b.x * a.x

	if dot < 0:

		idx = list( side.index.array )

		for i in range( 0, len( idx ), 3 ):

			t = idx[ i + 1 ]; idx[ i + 1 ] = idx[ i + 2 ]; idx[ i + 2 ] = t

		side.setIndex( idx )
		side.computeVertexNormals()

	opts = { 'color': PALETTE[ 'antifouling' ], 'rough': 0.75 }
	list_ = [ prepare( side, opts ), prepare( fanCap( bottomLoop, V( 0, - 1, 0 ) ), opts ), prepare( fanCap( topLoop, V( 0, 1, 0 ) ), opts ) ]
	stock = cylinder( 0.022, 0.022, 0.3, 10 )
	stock.translate( 0, yTop + 0.15, 0 )
	list_.append( prepare( stock, BRONZE ) )
	heel = cylinder( 0.012, 0.012, 0.05, 8 )
	heel.translate( 0, yBot - 0.015, 0 )
	list_.append( prepare( heel, BRONZE ) )
	return mergePrepared( list_ )
