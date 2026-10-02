# Deck gear -- pure-Python port of src/world/boat/DeckGear.js.
#
# Everything the working boat carries above the hull: the pot hauler and its davit, the
# coiled pot warp, the buoys lying on the deck, the fenders and their lines, the cleats,
# the bow fitting with its roller and stowed plow anchor, the bait tote and barrel, and the
# stern light with the ensign. Also the layout tables the game reads (TRAP, TRAPS, HAULER,
# FENDERS, FLAG) and buoyGeometry(), which buildRoofGear() in the wheelhouse displays on
# the roof.
#
# Fidelity notes, all copied deliberately from the JS:
#   - every primitive (box, roundedBox, cylinder, rod, sphere, torus, lathe, tube, mat4,
#     alignY, auxVertices) comes from the ported GeoKit, so vertex emission order, index
#     winding and uv conventions are already the JS ones. Nothing here re-derives geometry.
#   - `new ConeGeometry( 0.12, 0.34, 4 )` and `new CylinderGeometry( 0.43, 0.37, 0.36, 4, 1 )`
#     are the engine primitives straight (not GeoKit's uv-rescaling wrappers), so the tote
#     and the plow keep their raw engine uvs.
#   - the flag is a hand-built grid: two BufferGeometry copies (one with the index triples
#     reversed) sharing one position/uv array, with the aux attribute painted per vertex.
#   - `%` in the flag's flip is integer modulo on non-negative indices; the index map reads
#     the same array it writes (a reorder), so a list comprehension is exactly equivalent.
#
# Pure Python 3, standard library only (no numpy, no bpy).

import math

from mathutil import Vector3
from geometry import BufferGeometry, ConeGeometry, CylinderGeometry, Float32BufferAttribute
from geokit import (
	alignY, auxVertices, box, cylinder, lathe, mat4, rod, roundedBox, sphere, torus, tube,
)
from hull import PALETTE, foredeckY
from hull_lines import lerp


def V( x, y, z ):

	return Vector3( x, y, z )


STAINLESS = { 'color': PALETTE[ 'stainless' ], 'rough': 0.22, 'metal': 1 }
GALV = { 'color': 0xa3a7ab, 'rough': 0.35, 'metal': 1 }
BLACK = { 'color': 0x1a1b1d, 'rough': 0.55, 'metal': 0 }


# Maine lobster buoy (foam bullet in the owner's colours) with its spindle stick.
# Returns [ geometry, options ] pairs for the fittings bucket; origin at the buoy's base.
def buoyGeometry( colors = None, stick = 0.35 ):

	if colors is None:
		colors = [ 0xff6a13, 0xf4f1ea, 0x1d4f9c ]

	bands = [
		[ [ 0, 0 ], [ 0.03, 0.004 ], [ 0.055, 0.025 ], [ 0.07, 0.07 ], [ 0.075, 0.12 ], [ 0.075, 0.17 ] ],
		[ [ 0.075, 0.17 ], [ 0.075, 0.22 ], [ 0.075, 0.27 ] ],
		[ [ 0.075, 0.27 ], [ 0.074, 0.33 ], [ 0.068, 0.39 ], [ 0.052, 0.435 ], [ 0.03, 0.455 ], [ 0, 0.46 ] ],
	]
	out = [ [ lathe( b, 12 ), { 'color': colors[ i ], 'rough': 0.55 } ] for i, b in enumerate( bands ) ]
	out.append( [ cylinder( 0.011, 0.013, 0.46 + stick + 0.06, 6 ).translate( 0, ( 0.46 + stick - 0.06 ) / 2, 0 ), { 'color': 0x9c7a4c, 'rough': 0.75 } ] )
	return out


def buildDeckGear( kit, L, parts ):

	buildHauler( kit, L )
	buildCoils( kit, L )
	buildBuoys( kit, L )
	buildFenders( kit, L )
	buildCleats( kit, L )
	buildBow( kit, L )
	buildContainers( kit, L )
	buildStern( kit, L, parts )


# ------------------------------------------------------------------ lobster traps

# A pot's footprint, and where the gear sits on the working boat's deck: [x, y (bottom level), z, yaw,
# colour for the procedural stand-in]. game/Traps.js places the modelled pots from this layout, and the
# rope coil below sits on top of the stack.
TRAP = { 'L': 0.95, 'W': 0.55, 'H': 0.37 }

TRAPS = [
	[ 0.66, 0, - 3.25, 0.02, 0xd8b21c ],
	[ 0.66, 1, - 3.23, - 0.03, 0x2f7a3c ],
	[ 0.64, 0, - 2.2, - 0.04, 0xd8b21c ],
	[ - 0.68, 0, - 3.25, 0.03, 0x1f2326 ],
]

HAULER = { 'x': - 1.0, 'z': - 0.62, 'y': 1.34 }


def buildHauler( kit, L ):

	t = L.tAtSheerZ( - 0.78 )
	xg = - ( L.sheerX( t ) - 0.035 ); yg = L.sheerY( t ) + 0.045

	# davit arm from the rail up and outboard, with a snatch block
	pts = [ V( xg, yg, - 0.8 ), V( xg - 0.02, yg + 0.45, - 0.78 ), V( xg - 0.05, 1.86, - 0.76 ), V( xg - 0.13, 2.06, - 0.74 ), V( xg - 0.3, 2.11, - 0.73 ), V( xg - 0.43, 2.07, - 0.72 ) ]
	kit.add( 'fittings', tube( pts, 0.03, 32, 8 ), STAINLESS )
	foot = roundedBox( 0.12, 0.02, 0.16, 0.008, 1 )
	foot.translate( xg, yg + 0.01, - 0.8 )
	kit.add( 'fittings', foot, STAINLESS )
	tip = pts[ len( pts ) - 1 ]
	tipCap = sphere( 0.03, 10, 6 )
	tipCap.translate( tip.x, tip.y, tip.z )
	kit.add( 'fittings', tipCap, STAINLESS )
	kit.add( 'fittings', rod( tip, tip.clone().add( V( 0, - 0.12, 0 ) ), 0.01, 6 ), STAINLESS )
	bc = tip.clone().add( V( 0, - 0.22, 0 ) )

	for dz in [ - 0.022, 0.022 ]:

		cheek = cylinder( 0.085, 0.085, 0.01, 16 )
		cheek.applyMatrix4( mat4( bc.x, bc.y, bc.z + dz, math.pi / 2, 0, 0 ) )
		kit.add( 'fittings', cheek, GALV )

	sheave = cylinder( 0.07, 0.07, 0.034, 16 )
	sheave.applyMatrix4( mat4( bc.x, bc.y, bc.z, math.pi / 2, 0, 0 ) )
	kit.add( 'fittings', sheave, { 'color': 0x2b2d30, 'rough': 0.6 } )

	# hauler: post, hydraulic motor and V-groove sheave facing aft
	x = HAULER[ 'x' ]; y = HAULER[ 'y' ]; z = HAULER[ 'z' ]
	kit.add( 'fittings', rod( V( x, L.deckY, z + 0.1 ), V( x, y - 0.1, z + 0.1 ), 0.038, 12 ), GALV )
	plate = box( 0.2, 0.012, 0.2 )
	plate.translate( x, L.deckY + 0.006, z + 0.1 )
	kit.add( 'fittings', plate, GALV )
	motor = cylinder( 0.075, 0.075, 0.17, 18 )
	motor.applyMatrix4( mat4( x, y, z + 0.13, math.pi / 2, 0, 0 ) )
	kit.add( 'fittings', motor, { 'color': 0x3a4048, 'rough': 0.45, 'metal': 0.3 } )
	head = lathe( [ [ 0, - 0.05 ], [ 0.2, - 0.05 ], [ 0.205, - 0.04 ], [ 0.1, - 0.006 ], [ 0.1, 0.006 ], [ 0.205, 0.04 ], [ 0.2, 0.05 ], [ 0, 0.05 ] ], 28 )
	head.applyMatrix4( mat4( x, y, z, math.pi / 2, 0, 0 ) )
	kit.add( 'fittings', head, GALV )
	hubCap = cylinder( 0.04, 0.05, 0.03, 12 )
	hubCap.applyMatrix4( mat4( x, y, z - 0.06, math.pi / 2, 0, 0 ) )
	kit.add( 'fittings', hubCap, GALV )
	# hydraulic hoses down to the deck
	kit.add( 'fittings', tube( [ V( x + 0.05, y, z + 0.2 ), V( x + 0.12, y - 0.2, z + 0.3 ), V( x + 0.1, 0.6, z + 0.28 ), V( x + 0.06, L.deckY + 0.02, z + 0.25 ) ], 0.012, 20, 5 ), BLACK )
	kit.add( 'fittings', tube( [ V( x - 0.05, y, z + 0.2 ), V( x - 0.02, y - 0.25, z + 0.32 ), V( x + 0.02, 0.62, z + 0.3 ), V( x + 0.02, L.deckY + 0.02, z + 0.28 ) ], 0.012, 20, 5 ), BLACK )

	# pot warp reeved from the block into the hauler and down to a loose pile
	rope = [ bc.clone().add( V( 0.07, 0, 0 ) ), V( x - 0.3, y + 0.35, z + 0.02 ), V( x - 0.12, y + 0.2, z ), V( x + 0.2, y - 0.02, z - 0.01 ), V( x + 0.16, y - 0.25, z - 0.01 ), V( x + 0.05, 0.72, z - 0.1 ), V( x + 0.2, L.deckY + 0.02, z - 0.35 ) ]
	kit.add( 'fittings', tube( rope, 0.009, 48, 4 ), { 'color': 0xe4c235, 'rough': 0.8, 'pattern': 1 } )


# ------------------------------------------------------------------ coiled pot warp

def coil( cx, cy, cz, turns, radius, ropeR, seed ):

	pts = []
	perTurn = 12
	n = turns * perTurn

	for k in range( n + 1 ):

		f = k / n
		a = k / perTurn * math.pi * 2 + seed
		r = radius * ( 1 - 0.12 * f ) + 0.012 * math.sin( k * 0.9 + seed * 3 )
		y = cy + ropeR + f * turns * ropeR * 1.35 + 0.004 * math.sin( k * 1.7 + seed )
		pts.append( V( cx + math.cos( a ) * r, y, cz + math.sin( a ) * r ) )

	# tail leading off the top of the coil
	last = pts[ len( pts ) - 1 ]
	pts.append( V( last.x * 0.7 + cx * 0.3 + 0.12, last.y - 0.01, last.z + 0.2 ) )
	pts.append( V( last.x + 0.35, cy + ropeR, last.z + 0.4 ) )
	return tube( pts, ropeR, turns * 21 + 10, 4 )


def buildCoils( kit, L ):

	kit.add( 'fittings', coil( - 0.5, L.deckY, - 2.15, 5, 0.22, 0.011, 0.3 ), { 'color': 0xe4c235, 'rough': 0.8, 'pattern': 1 } )
	topTrap = L.deckY + 0.03 + TRAP[ 'H' ] + 0.012
	kit.add( 'fittings', coil( - 0.68, topTrap, - 3.2, 4, 0.19, 0.01, 1.7 ), { 'color': 0x2f6f4f, 'rough': 0.8, 'pattern': 1 } )


# ------------------------------------------------------------------ buoys lying on deck

def buildBuoys( kit, L ):

	place = [
		[ - 0.2, L.deckY + 0.075, - 2.75, math.pi / 2, 0.4 ],
		[ 0.12, L.deckY + 0.075, - 3.0, math.pi / 2, - 0.9 ],
	]

	for x, y, z, rx, ry in place:

		m = mat4( x, y, z, rx, ry, 0, 1, 1, 1, 'YXZ' )

		for g, opts in buoyGeometry( None, 0.3 ):

			g.translate( 0, - 0.23, 0 )
			g.applyMatrix4( m )
			kit.add( 'fittings', g, opts )


# ------------------------------------------------------------------ fenders

FENDERS = [ [ - 1, - 2.4 ], [ - 1, - 1.05 ], [ - 1, 0.65 ], [ 1, - 1.7 ] ]


def buildFenders( kit, L ):

	prof = [ [ 0, 0 ], [ 0.03, 0.004 ], [ 0.055, 0.016 ], [ 0.07, 0.04 ], [ 0.075, 0.07 ], [ 0.075, 0.43 ], [ 0.07, 0.46 ], [ 0.055, 0.484 ], [ 0.03, 0.496 ], [ 0, 0.5 ] ]

	for s, z in FENDERS:

		yTop = 0.86
		hullX = L.hullXAt( z, yTop - 0.05 )
		fx = s * ( hullX + 0.08 )
		f = lathe( prof, 12 )
		f.translate( fx, yTop - 0.5, z )
		kit.add( 'fittings', f, { 'color': 0x1d3a66, 'rough': 0.45 } )
		eye = torus( 0.018, 0.006, 5, 10 )
		eye.translate( fx, yTop + 0.018, z )
		kit.add( 'fittings', eye, { 'color': 0x1d3a66, 'rough': 0.45 } )
		# fender line up and over the gunwale to a cleat inside
		t = L.tAtSheerZ( z )
		ys = L.sheerY( t ) + 0.05
		xo = s * ( L.sheerX( t ) + 0.01 ); xi = s * ( L.sheerX( t ) - L.shell - 0.05 )
		line = [ V( fx, yTop + 0.03, z ), V( fx - s * 0.01, ( yTop + ys ) / 2, z ), V( xo, ys, z ), V( ( xo + xi ) / 2, ys + 0.012, z ), V( xi, ys - 0.03, z ) ]
		kit.add( 'fittings', tube( line, 0.006, 16, 4 ), { 'color': 0xf0efe8, 'rough': 0.8, 'pattern': 1 } )


# ------------------------------------------------------------------ cleats

def cleat( pos, yaw, len = 0.2 ):

	parts = []
	horn = cylinder( 0.011, 0.011, len * 0.7, 8 )
	horn.applyMatrix4( mat4( 0, 0.045, 0, math.pi / 2, 0, 0 ) )
	parts.append( horn )

	for s in [ 1, - 1 ]:

		tipC = cylinder( 0.006, 0.011, len * 0.15, 8 )
		tipC.applyMatrix4( mat4( 0, 0.045, s * len * 0.425, s * math.pi / 2, 0, 0 ) )
		parts.append( tipC )
		foot = cylinder( 0.013, 0.018, 0.045, 8 )
		foot.translate( 0, 0.0225, s * len * 0.22 )
		parts.append( foot )

	m = mat4( pos.x, pos.y, pos.z, 0, yaw, 0 )
	return [ p.applyMatrix4( m ) for p in parts ]


def buildCleats( kit, L ):

	list_ = []

	for s in [ 1, - 1 ]:

		for z in [ - 3.6, - 1.35 ]:

			t = L.tAtSheerZ( z )
			list_.extend( cleat( V( s * ( L.sheerX( t ) - 0.04 ), L.sheerY( t ) + 0.048, z ), 0 ) )

	tb = L.tAtSheerZ( 3.35 )
	list_.extend( cleat( V( 0, foredeckY( L, tb, 0 ), 3.35 ), math.pi / 2, 0.24 ) )

	for g in list_:
		kit.add( 'fittings', g, STAINLESS )


# ------------------------------------------------------------------ bow: stem head, roller, anchor

def buildBow( kit, L ):

	yTip = L.sheerY( 1 )
	# stem head fitting wrapping the bow tip
	head = roundedBox( 0.09, 0.05, 0.3, 0.015, 2 )
	head.translate( 0, yTip + 0.03, L.zBow - 0.08 )
	kit.add( 'fittings', head, STAINLESS )

	for s in [ 1, - 1 ]:

		cheek = box( 0.008, 0.07, 0.16 )
		cheek.translate( s * 0.04, yTip + 0.08, L.zBow + 0.02 )
		kit.add( 'fittings', cheek, STAINLESS )

	roller = cylinder( 0.03, 0.03, 0.07, 12 )
	roller.applyMatrix4( mat4( 0, yTip + 0.085, L.zBow + 0.07, 0, 0, math.pi / 2 ) )
	kit.add( 'fittings', roller, BLACK )

	# plow anchor stowed on the roller: shank over the roller, plowshare hanging against the stem
	sa = V( 0, yTip + 0.1, L.zBow - 0.45 ); sb = V( 0, yTip + 0.12, L.zBow + 0.13 )
	kit.add( 'fittings', rod( sa, sb, 0.017, 8 ), GALV )
	knuckle = cylinder( 0.025, 0.025, 0.07, 10 )
	knuckle.applyMatrix4( mat4( sb.x, sb.y, sb.z, 0, 0, math.pi / 2 ) )
	kit.add( 'fittings', knuckle, GALV )
	plowDir = V( 0, - 0.85, - 0.4 ).normalize()
	plow = ConeGeometry( 0.12, 0.34, 4 )
	plow.rotateY( math.pi / 4 )
	plow.scale( 1, 1, 0.38 )
	plow.applyMatrix4( alignY( sb.clone().add( V( 0, - 0.02, 0.02 ) ).addScaledVector( plowDir, 0.17 ), plowDir ) )
	kit.add( 'fittings', plow, GALV )
	# rode running aft to the deck pipe
	t = L.tAtSheerZ( L.zBow - 0.75 )
	pipeY = foredeckY( L, t, 0 )
	dp = cylinder( 0.035, 0.045, 0.04, 14 )
	dp.translate( 0, pipeY + 0.02, L.zBow - 0.75 )
	kit.add( 'fittings', dp, STAINLESS )
	kit.add( 'fittings', tube( [ sa, V( 0, lerp( sa.y, pipeY, 0.6 ) + 0.03, L.zBow - 0.6 ), V( 0, pipeY + 0.04, L.zBow - 0.75 ) ], 0.012, 10, 5 ), GALV )


# ------------------------------------------------------------------ bait tote and barrel

def buildContainers( kit, L ):

	# bait tote beside the hauler
	tote = CylinderGeometry( 0.43, 0.37, 0.36, 4, 1 )
	tote.rotateY( math.pi / 4 )
	tote.scale( 1, 1, 0.62 )
	tote.translate( - 0.72, L.deckY + 0.18, - 1.45 )
	kit.add( 'fittings', tote, { 'color': 0x2a64b0, 'rough': 0.55 } )
	bait = box( 0.52, 0.02, 0.33 )
	bait.translate( - 0.72, L.deckY + 0.352, - 1.45 )
	kit.add( 'fittings', bait, { 'color': 0x5a2c22, 'rough': 0.35 } )

	# bait barrel against the port rail aft of the wheelhouse
	bx = 0.86; bz = - 0.8
	barrel = cylinder( 0.26, 0.26, 0.78, 20 )
	barrel.translate( bx, L.deckY + 0.39, bz )
	kit.add( 'fittings', barrel, { 'color': 0x1f5ea6, 'rough': 0.5 } )

	for y in [ 0.26, 0.52 ]:

		rib = torus( 0.262, 0.012, 5, 24 )
		rib.applyMatrix4( mat4( bx, L.deckY + y, bz, math.pi / 2, 0, 0 ) )
		kit.add( 'fittings', rib, { 'color': 0x1f5ea6, 'rough': 0.5 } )

	lid = cylinder( 0.27, 0.27, 0.03, 20 )
	lid.translate( bx, L.deckY + 0.795, bz )
	kit.add( 'fittings', lid, { 'color': 0x1b4f8c, 'rough': 0.5 } )


# ------------------------------------------------------------------ stern: light and ensign

FLAG = { 'w': 0.5, 'h': 0.33 }


def buildStern( kit, L, parts ):

	y0 = L.sheerY( 0 ) + 0.045
	base = cylinder( 0.03, 0.035, 0.03, 12 )
	base.translate( 0, y0 + 0.015, L.zAft + 0.03 )
	kit.add( 'fittings', base, BLACK )
	lens = cylinder( 0.025, 0.025, 0.05, 12 )
	lens.translate( 0, y0 + 0.055, L.zAft + 0.03 )
	kit.add( 'glow', lens, { 'color': 0xfff3dc, 'rough': 0.2, 'pattern': 0 } )
	cap = cylinder( 0.03, 0.028, 0.012, 12 )
	cap.translate( 0, y0 + 0.086, L.zAft + 0.03 )
	kit.add( 'fittings', cap, BLACK )

	# flag staff in a socket at the starboard quarter
	sx = - ( L.sheerX( 0.02 ) - 0.08 ); sz = L.zAft + 0.14
	top = y0 + 1.0
	kit.add( 'fittings', rod( V( sx, y0 - 0.02, sz ), V( sx, top, sz ), 0.014, 8, 0.01 ), { 'color': 0xf1efe8, 'rough': 0.3 } )
	finial = sphere( 0.02, 8, 6 )
	finial.translate( sx, top + 0.015, sz )
	kit.add( 'fittings', finial, { 'color': 0xc8a050, 'rough': 0.25, 'metal': 1 } )
	socket = cylinder( 0.022, 0.026, 0.06, 10 )
	socket.translate( sx, y0 + 0.01, sz )
	kit.add( 'fittings', socket, STAINLESS )

	# flag: rest pose streams aft; the fittings material animates it (pattern 2)
	w = FLAG[ 'w' ]; h = FLAG[ 'h' ]
	nu = 12; nv = 6
	pos = []; uvs = []; idx = []

	for j in range( nv + 1 ):

		for i in range( nu + 1 ):

			u = i / nu; v = j / nv
			pos.extend( ( sx, top - 0.03 - h + v * h, sz - u * w ) )
			uvs.extend( ( u, v ) )

	for j in range( nv ):

		for i in range( nu ):

			a = j * ( nu + 1 ) + i; b = a + 1; c = a + nu + 2; d = a + nu + 1
			idx.extend( ( a, b, c, a, c, d ) )

	for flip in [ False, True ]:

		g = BufferGeometry()
		g.setAttribute( 'position', Float32BufferAttribute( pos, 3 ) )
		g.setAttribute( 'uv', Float32BufferAttribute( uvs, 2 ) )
		ix = [ idx[ k - ( k % 3 ) + ( 2 - ( k % 3 ) ) ] for k in range( len( idx ) ) ] if flip else list( idx )
		g.setIndex( ix )
		g.computeVertexNormals()
		auxVertices( g, lambda p, k: [ 0.7, 0, 2, uvs[ k * 2 ] ] )
		kit.add( 'fittings', g, { 'color': 0xffffff } )

	parts[ 'flagPivot' ] = V( sx, 0, sz )
