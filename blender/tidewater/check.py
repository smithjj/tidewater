#!/usr/bin/env python3
# Proof harness for the pure-Python boat port: prints the same table of vertex / index
# counts, bounding boxes and HullLines numbers as the JS reference generator
# (ref.mjs, which drives the real src/world/boat modules in node).
#
#   python3 blender/tidewater/check.py            # the Python side's table
#   python3 blender/tidewater/check.py --cmp ref.txt   # diff against the JS table
#
# No Blender, no numpy: plain python3.
#
# Run it from this directory (it imports its siblings by module name):
#   python3 check.py

import math
import os
import struct
import sys

sys.path.insert( 0, os.path.dirname( os.path.abspath( __file__ ) ) )

from mathutil import Float32Array, Vector3, js_round
import geokit as GK
from geometry import BufferAttribute, BufferGeometry, Float32BufferAttribute
from geokit import GeoKit
from hull_lines import HullLines, RHO_SEAWATER

out = []


def fmt( v ):

	if isinstance( v, float ):

		if v != v:
			return 'NaN'

		if v == math.inf:
			return 'Infinity'

		if v == - math.inf:
			return '-Infinity'

		if v == int( v ) and abs( v ) < 1e15:
			return str( int( v ) )

		return repr( v )

	return str( v )


def num( key, value ):
	out.append( 'num\t' + key + '\t' + fmt( value ) )


def txt( key, value ):
	out.append( 'txt\t' + key + '\t' + str( value ) )


def fnv32( data ):
	"""FNV-1a 32 over a bytes-like, matching the JS side's hash."""

	h = 0x811c9dc5

	for b in data:

		h = ( ( h ^ b ) * 16777619 ) & 0xFFFFFFFF

	return h


def geoRow( name, g ):

	g.computeBoundingBox()
	bb = g.boundingBox
	row = [
		'geo', name,
		str( g.attributes[ 'position' ].count ),
		str( g.index.count if g.index else 0 ),
	]
	row += [ fmt( bb.min.x ), fmt( bb.min.y ), fmt( bb.min.z ), fmt( bb.max.x ), fmt( bb.max.y ), fmt( bb.max.z ) ]
	out.append( '\t'.join( row ) )
	out.append( '\t'.join( [ 'attrs', name, ','.join( g.attributes.keys() ), fmt( GK.triangleCount( g ) ) ] ) )

	# FNV-1a 32 over the raw float32 bytes of each attribute (and over the index as u32):
	# proves the vertex ordering and values, not just the counts.
	for attr in [ 'position', 'normal', 'uv', 'color', 'aux' ]:

		a = g.attributes.get( attr )

		if a is None:
			continue

		n = a.count * a.itemSize
		data = struct.pack( '<%df' % n, *[ a.array[ i ] for i in range( n ) ] )
		vsum = 0.0

		for i in range( n ):
			vsum += a.array[ i ]

		out.append( '\t'.join( [ 'hash', name, attr, str( fnv32( data ) ), fmt( vsum ) ] ) )

	if g.index:

		n = g.index.count
		data = struct.pack( '<%dI' % n, *[ g.index.getX( i ) for i in range( n ) ] )
		out.append( '\t'.join( [ 'hash', name, 'index', str( fnv32( data ) ), fmt( sum( g.index.getX( i ) for i in range( n ) ) ) ] ) )


# ------------------------------------------------------------------ test inputs
# (all literals: both sides feed the geometry the exact same numbers)

TUBE_PTS = [ [ 0, 0, 0 ], [ 0.5, 0.3, 0.4 ], [ 1.0, 0.2, 0.9 ], [ 1.5, 0.6, 1.2 ], [ 2.0, 0.5, 1.8 ] ]
tubePoints = [ Vector3( p[ 0 ], p[ 1 ], p[ 2 ] ) for p in TUBE_PTS ]

GRID = [
	[ [ 0.0, 0.0, 0.0 ], [ 0.0, 0.05, 0.4 ], [ 0.0, 0.1, 0.8 ], [ 0.0, 0.15, 1.2 ] ],
	[ [ 0.3, 0.14, 0.0 ], [ 0.3, 0.19, 0.4 ], [ 0.3, 0.24, 0.8 ], [ 0.3, 0.29, 1.2 ] ],
	[ [ 0.6, 0.4, 0.0 ], [ 0.6, 0.45, 0.4 ], [ 0.6, 0.5, 0.8 ], [ 0.6, 0.55, 1.2 ] ],
]
gridRows = [ [ Vector3( p[ 0 ], p[ 1 ], p[ 2 ] ) for p in r ] for r in GRID ]

HEX = [
	[ 0.5, 0, 0 ],
	[ 0.25, 0, 0.4330127018922193 ],
	[ - 0.25, 0, 0.4330127018922193 ],
	[ - 0.5, 0, 0 ],
	[ - 0.25, 0, - 0.4330127018922193 ],
	[ 0.25, 0, - 0.4330127018922193 ],
]
hexLoop = [ Vector3( p[ 0 ], p[ 1 ], p[ 2 ] ) for p in HEX ]

PROFILES = [
	[ [ 0.3, 0, 0 ], [ 0.21213203435596429, 0, 0.21213203435596429 ], [ 0, 0, 0.3 ], [ - 0.21213203435596429, 0, 0.21213203435596429 ], [ - 0.3, 0, 0 ] ],
	[ [ 0.35, 0.1, 0.5 ], [ 0.24748737341529164, 0.1, 0.74748737341529164 ], [ 0, 0.1, 0.85 ], [ - 0.24748737341529164, 0.1, 0.74748737341529164 ], [ - 0.35, 0.1, 0.5 ] ],
	[ [ 0.4, 0.2, 1.0 ], [ 0.28284271247461906, 0.2, 1.28284271247461906 ], [ 0, 0.2, 1.4 ], [ - 0.28284271247461906, 0.2, 1.28284271247461906 ], [ - 0.4, 0.2, 1.0 ] ],
]
loftProfiles = [ [ Vector3( p[ 0 ], p[ 1 ], p[ 2 ] ) for p in r ] for r in PROFILES ]

CIRCLE8 = [
	[ 1, 0 ], [ 0.7071067811865476, 0.7071067811865476 ], [ 6.123233995736766e-17, 1 ], [ - 0.7071067811865475, 0.7071067811865476 ],
	[ - 1, 1.2246467991473532e-16 ], [ - 0.7071067811865476, - 0.7071067811865475 ], [ - 1.8369701987210297e-16, - 1 ], [ 0.7071067811865475, - 0.7071067811865476 ],
]
slabOutline = [ [ 0, 0 ], [ 0.6, 0 ], [ 0.6, 0.4 ], [ 0, 0.4 ] ]
slabHoles = [ [ [ 0.3 + c[ 0 ] * 0.1, 0.2 + c[ 1 ] * 0.1 ] for c in CIRCLE8 ] ]


def slabMap( u, v, side ):
	return Vector3( u, v, side * 0.05 )


# ------------------------------------------------------------------ primitives

geoRow( 'box(1,2,3)', GK.box( 1, 2, 3 ) )
geoRow( 'roundedBox(0.4,0.3,0.2,0.02,2)', GK.roundedBox( 0.4, 0.3, 0.2, 0.02, 2 ) )
geoRow( 'cylinder(0.2,0.3,0.5,12,1)', GK.cylinder( 0.2, 0.3, 0.5, 12, 1 ) )
geoRow( 'sphere(0.5,12,8)', GK.sphere( 0.5, 12, 8 ) )
geoRow( 'torus(0.4,0.1,8,24)', GK.torus( 0.4, 0.1, 8, 24 ) )
geoRow( 'lathe(3pt,16)', GK.lathe( [ [ 0.2, 0 ], [ 0.4, 0.3 ], [ 0.1, 0.6 ] ], 16 ) )
geoRow( 'tube(5pt,0.05,32,6)', GK.tube( tubePoints, 0.05, 32, 6 ) )
geoRow( 'gridSurface(3x4)', GK.gridSurface( gridRows ) )
geoRow( 'fanCap(hex)', GK.fanCap( hexLoop, Vector3( 0, 1, 0 ) ) )
geoRow( 'loft(3x5,closed)', GK.loft( loftProfiles, { 'closed': True } ) )
geoRow( 'slab(rect+hole)', GK.slab( slabOutline, slabHoles, slabMap ) )
geoRow( 'rod([0,0,0],[1,1,0.5],0.08,8)', GK.rod( Vector3( 0, 0, 0 ), Vector3( 1, 1, 0.5 ), 0.08, 8 ) )

# option / branch coverage: open cylinders, theta ranges, partial sphere and torus,
# 4-point lathe with a negative radius, closed and tensioned tubes, flip/closeJ/uvFn
# grids, flipped loft, slab without edges / front only, opposite fanCap, tapered rod.
geoRow( 'cylinder(0.2,0.2,0.4,8,2,open)', GK.cylinder( 0.2, 0.2, 0.4, 8, 2, True ) )
geoRow( 'cylinder(0.1,0.2,0.3,6,1,false,0.5,3)', GK.cylinder( 0.1, 0.2, 0.3, 6, 1, False, 0.5, 3.0 ) )
geoRow( 'sphere(0.5,8,6,0.3,2,0.2,1.5)', GK.sphere( 0.5, 8, 6, 0.3, 2.0, 0.2, 1.5 ) )
geoRow( 'torus(0.4,0.1,6,12,3)', GK.torus( 0.4, 0.1, 6, 12, 3.0 ) )
geoRow( 'lathe(4pt,8,neg r)', GK.lathe( [ [ - 0.2, 0 ], [ 0.4, 0.2 ], [ 0.35, 0.5 ], [ 0.05, 0.8 ] ], 8 ) )
geoRow( 'tube(5pt,0.05,24,6,closed)', GK.tube( tubePoints, 0.05, 24, 6, True ) )
geoRow( 'tube(5pt,0.05,20,5,tension .3)', GK.tube( tubePoints, 0.05, 20, 5, False, 0.3 ) )
geoRow( 'gridSurface(3x4,flip,closeJ)', GK.gridSurface( gridRows, { 'flip': True, 'closeJ': True } ) )
geoRow( 'gridSurface(3x4,uvFn)', GK.gridSurface( gridRows, { 'uvFn': lambda p, i, j, u, v: [ u * 0.5, v * 2 ] } ) )
geoRow( 'loft(3x5,closed,flip)', GK.loft( loftProfiles, { 'closed': True, 'flip': True } ) )
geoRow( 'slab(rect+hole,no edges)', GK.slab( slabOutline, slabHoles, slabMap, { 'edges': False } ) )
geoRow( 'slab(rect+hole,front only)', GK.slab( slabOutline, slabHoles, slabMap, { 'back': False } ) )
geoRow( 'fanCap(hex,-y)', GK.fanCap( hexLoop, Vector3( 0, - 1, 0 ) ) )
geoRow( 'rod(...,0.05,6,rEnd 0.1)', GK.rod( Vector3( 0, 0, 0 ), Vector3( 1, 1, 0.5 ), 0.05, 6, 0.1 ) )
geoRow( 'prepare(box,matrix)', GK.prepare( GK.box( 1, 1, 1 ), { 'matrix': GK.mat4( 0.5, - 0.25, 1.0, 0.1, 0.2, 0.3 ) } ) )

# slab edge cases: clockwise outline (winding is normalised), a doubled outline point
# (zero-length edge) and a loft with no options at all.
geoRow( 'slab(cw outline+hole)', GK.slab( list( reversed( slabOutline ) ), slabHoles, slabMap ) )
geoRow( 'slab(outline w/ dup point)', GK.slab( [ [ 0, 0 ], [ 0.6, 0 ], [ 0.6, 0 ], [ 0.6, 0.4 ], [ 0, 0.4 ] ], slabHoles, slabMap ) )
geoRow( 'loft(3x5,defaults)', GK.loft( loftProfiles ) )

# orientation-flipping map (det < 0) drives the other edge winding, and a rounded box
# with segments = 0 collapses to the plain mid-resolution box early-return.
geoRow( 'slab(rect+hole,mirrored map)', GK.slab( slabOutline, slabHoles, lambda u, v, side: Vector3( - u, v, side * 0.05 ) ) )
geoRow( 'roundedBox(...,segments 0)', GK.roundedBox( 0.4, 0.3, 0.2, 0.02, 0 ) )
geoRow( 'cylinder(0,0.2,0.4,8,2)', GK.cylinder( 0, 0.2, 0.4, 8, 2 ) )
geoRow( 'cylinder(0.2,0,0.4,8,2)', GK.cylinder( 0.2, 0, 0.4, 8, 2 ) )

# a bare geometry with only a position + index: prepare() has to compute the normals,
# default the uv, drop the foreign attribute and re-pack the float64-backed one.
g = BufferGeometry()
g.setAttribute( 'position', Float32BufferAttribute( [ 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0.5 ], 3 ) )
g.setIndex( [ 0, 1, 2, 1, 3, 2 ] )
g.setAttribute( 'foo', Float32BufferAttribute( Float32Array( 4 ), 1 ) )
g.setAttribute( 'aux', BufferAttribute( [ 0.1, 0.2, 3, 1 ] * 4, 4 ) )
geoRow( 'prepare(raw geom)', GK.prepare( g ) )

# prepare() options / attribute layout
g = GK.prepare( GK.box( 1, 1, 1 ), { 'color': 0xff8040, 'rough': 0.42, 'metal': 0.3, 'pattern': 2, 'anim': 0.7 } )
geoRow( 'prepare(box,opt)', g )
num( 'prepare(box,opt).color0', g.attributes[ 'color' ].array[ 0 ] )
num( 'prepare(box,opt).color1', g.attributes[ 'color' ].array[ 1 ] )
num( 'prepare(box,opt).aux0', g.attributes[ 'aux' ].array[ 0 ] )
num( 'prepare(box,opt).aux1', g.attributes[ 'aux' ].array[ 1 ] )
num( 'prepare(box,opt).aux2', g.attributes[ 'aux' ].array[ 2 ] )
num( 'prepare(box,opt).aux3', g.attributes[ 'aux' ].array[ 3 ] )
txt( 'prepare(box,opt).groups', len( g.groups ) )

# a merged bucket of everything indexed
kit = GeoKit()
kit.add( 'mix', GK.box( 1, 2, 3 ) )
kit.add( 'mix', GK.roundedBox( 0.4, 0.3, 0.2, 0.02, 2 ) )
kit.add( 'mix', GK.cylinder( 0.2, 0.3, 0.5, 12, 1 ) )
kit.add( 'mix', GK.sphere( 0.5, 12, 8 ) )
kit.add( 'mix', GK.torus( 0.4, 0.1, 8, 24 ) )
kit.add( 'mix', GK.lathe( [ [ 0.2, 0 ], [ 0.4, 0.3 ], [ 0.1, 0.6 ] ], 16 ) )
kit.add( 'mix', GK.tube( tubePoints, 0.05, 32, 6 ) )
kit.add( 'mix', GK.gridSurface( gridRows ) )
kit.add( 'mix', GK.fanCap( hexLoop, Vector3( 0, 1, 0 ) ) )
kit.add( 'mix', GK.loft( loftProfiles, { 'closed': True } ) )
kit.add( 'mix', GK.slab( slabOutline, slabHoles, slabMap ) )
m = kit.merged( 'mix' )
geoRow( 'merged(mix,11 parts)', m )
txt( 'merged(mix).buckets', len( kit.buckets ) )
txt( 'merged(missing)', 'null' if kit.merged( 'missing' ) is None else 'GEOMETRY' )
again = GK.mergePrepared( kit.buckets[ 'mix' ] )
geoRow( 'mergePrepared(same list)', again )

# single-part bucket / single prepared geometry (the no-merge path)
solo = GeoKit()
solo.add( 'one', GK.sphere( 0.3, 8, 6 ) )
geoRow( 'merged(solo,1 part)', solo.merged( 'one' ) )
geoRow( 'mergePrepared(single)', GK.mergePrepared( [ GK.prepare( GK.sphere( 0.3, 8, 6 ) ) ] ) )

# paintVertices / auxVertices
g = GK.box( 1, 1, 1 )
GK.paintVertices( g, lambda v, i: [ abs( v.x ), abs( v.y ), abs( v.z ) ] )
GK.auxVertices( g, lambda v, i: [ 0.2 + v.x * 0.1, 0.1, 1, 0.5 ] )
num( 'paint(box).color0', g.attributes[ 'color' ].array[ 0 ] )
num( 'paint(box).color1', g.attributes[ 'color' ].array[ 1 ] )
num( 'paint(box).color2', g.attributes[ 'color' ].array[ 2 ] )
num( 'paint(box).aux0', g.attributes[ 'aux' ].array[ 0 ] )
num( 'paint(box).aux3', g.attributes[ 'aux' ].array[ 3 ] )
geoRow( 'paint(box)', g )

# mat4 / alignY
m4 = GK.mat4( 1, 2, 3, 0.1, 0.2, 0.3, 1.5 )
for i in range( 16 ):
	num( 'mat4(' + str( i ) + ')', m4.elements[ i ] )

aY = GK.alignY( Vector3( 0.2, 0.4, 0.6 ), Vector3( 0.3, 1, - 0.2 ), 0.25 )
for i in range( 16 ):
	num( 'alignY(' + str( i ) + ')', aY.elements[ i ] )

# ------------------------------------------------------------------ hull lines

H = HullLines()
num( 'hull.zAft', H.zAft )
num( 'hull.zBow', H.zBow )
num( 'hull.length', H.length )
num( 'hull.deckY', H.deckY )
num( 'hull.shell', H.shell )
num( 'hull.houseBack', H.houseBack )
num( 'hull.houseFront', H.houseFront )
num( 'hull.stem.k', H.stem[ 'k' ] )
num( 'hull.stem.R', H.stem[ 'R' ] )
num( 'hull.stem.yF', H.stem[ 'yF' ] )
num( 'hull.stem.zW', H.stem[ 'zW' ] )
num( 'hull.stem.yc', H.stem[ 'yc' ] )
num( 'hull.stem.zc', H.stem[ 'zc' ] )
num( 'hull.stem.yT', H.stem[ 'yT' ] )
num( 'hull.wlStart', H.wlStart )
num( 'hull.wlEnd', H.wlEnd )
num( 'hull.tableZ0', H._tableZ0 )
num( 'hull.tableDz', H._tableDz )
num( 'hull.beamTable[0]', H._beamTable[ 0 ] )
num( 'hull.beamTable[256]', H._beamTable[ 256 ] )
num( 'hull.beamTable[512]', H._beamTable[ 512 ] )
num( 'hull.canoeVolume', H.canoeVolume )
num( 'hull.waterplaneArea', H.waterplaneArea )
num( 'hull.centerOfFlotationZ', H.centerOfFlotationZ )
num( 'hull.centerOfBuoyancy.x', H.centerOfBuoyancy.x )
num( 'hull.centerOfBuoyancy.y', H.centerOfBuoyancy.y )
num( 'hull.centerOfBuoyancy.z', H.centerOfBuoyancy.z )
num( 'hull.suggestedMass', js_round( RHO_SEAWATER * H.canoeVolume ) )
num( 'hull.field.nx', H._field[ 'nx' ] )
num( 'hull.field.nz', H._field[ 'nz' ] )
num( 'hull.field.cell', H._field[ 'cell' ] )
num( 'hull.field.x0', H._field[ 'x0' ] )
num( 'hull.field.z0', H._field[ 'z0' ] )

num( 'hull.sheerY(0)', H.sheerY( 0 ) )
num( 'hull.sheerY(0.25)', H.sheerY( 0.25 ) )
num( 'hull.sheerY(0.5)', H.sheerY( 0.5 ) )
num( 'hull.sheerY(1)', H.sheerY( 1 ) )
num( 'hull.sheerX(0)', H.sheerX( 0 ) )
num( 'hull.sheerX(0.42)', H.sheerX( 0.42 ) )
num( 'hull.sheerX(0.8)', H.sheerX( 0.8 ) )
num( 'hull.sheerX(1)', H.sheerX( 1 ) )
num( 'hull.sheerZ(0.3)', H.sheerZ( 0.3 ) )
num( 'hull.tAtSheerZ(0)', H.tAtSheerZ( 0 ) )
num( 'hull.keelY(0)', H.keelY( 0 ) )
num( 'hull.keelY(0.45)', H.keelY( 0.45 ) )
num( 'hull.keelY(1)', H.keelY( 1 ) )
num( 'hull.chineX(0.42)', H.chineX( 0.42 ) )
num( 'hull.chineY(0.7)', H.chineY( 0.7 ) )
num( 'hull.bilgeTangent(0.5)', H.bilgeTangent( 0.5 ) )
num( 'hull.bottomConvexity(0.5)', H.bottomConvexity( 0.5 ) )
num( 'hull.flare(0.8)', H.flare( 0.8 ) )
num( 'hull.bowBlend(0.7)', H.bowBlend( 0.7 ) )
num( 'hull.stemZ(0)', H.stemZ( 0 ) )
num( 'hull.stemZ(0.5)', H.stemZ( 0.5 ) )
num( 'hull.sectionCount(1)', H.sectionCount( 1 ) )
num( 'hull.sectionPoints(0.5,1).length', len( H.sectionPoints( 0.5, 1 ) ) )
num( 'hull.setback(1).length', len( H.setback( 1 ) ) )
num( 'hull.setback(1)[0]', H.setback( 1 )[ 0 ] )
num( 'hull.setback(1)[10]', H.setback( 1 )[ 10 ] )

st = H.station( 0.62, 1 )
num( 'hull.station(0.62,1).length', len( st ) )
num( 'hull.station(0.62,1)[0].x', st[ 0 ].x )
num( 'hull.station(0.62,1)[0].y', st[ 0 ].y )
num( 'hull.station(0.62,1)[0].z', st[ 0 ].z )
num( 'hull.station(0.62,1)[last].x', st[ len( st ) - 1 ].x )
num( 'hull.station(0.62,1)[last].y', st[ len( st ) - 1 ].y )
num( 'hull.station(0.62,1)[last].z', st[ len( st ) - 1 ].z )

ts = H.stationParams( 60 )
num( 'hull.stationParams(60).length', len( ts ) )
for i in [ 0, 1, 10, 30, 50, 58, 59 ]:
	num( 'hull.stationParams(60)[' + str( i ) + ']', ts[ i ] )

for z in [ - 3.0, - 1.0, 0, 1.0, 2.5, 3.5, 4.5 ]:
	num( 'hull.halfBeamAt(' + fmt( z ) + ')', H.halfBeamAt( z ) )

num( 'hull.halfBreadth(0.5,0.3)', H.halfBreadth( 0.5, 0.3 ) )
num( 'hull.halfBreadth(0.5,0.9)', H.halfBreadth( 0.5, 0.9 ) )
num( 'hull.halfBreadth(0.5,5)', H.halfBreadth( 0.5, 5 ) )
num( 'hull.halfBreadth(0.5,-5)', H.halfBreadth( 0.5, - 5 ) )
num( 'hull.draftAt(0.5)', H.draftAt( 0.5 ) )
num( 'hull.draftAt(- 1)', H.draftAt( - 1 ) )
num( 'hull.draftAt(- 5)', H.draftAt( - 5 ) )
num( 'hull.bottomAt(0.3,0.5)', H.bottomAt( 0.3, 0.5 ) )
num( 'hull.bottomAt(0.3,- 1)', H.bottomAt( 0.3, - 1 ) )
num( 'hull.bottomAt(3,0.5)', H.bottomAt( 3, 0.5 ) )
num( 'hull.zOnStation(0.05,0.5)', H.zOnStation( 0.05, 0.5 ) )
num( 'hull.zOnStation(0.8,0.5)', H.zOnStation( 0.8, 0.5 ) )
num( 'hull.zOnStation(0.8,-5)', H.zOnStation( 0.8, - 5 ) )
num( 'hull.zOnStation(0.8,5)', H.zOnStation( 0.8, 5 ) )
num( 'hull.tAtSheerZ(2)', H.tAtSheerZ( 2 ) )
num( 'hull.sectionPoints(1,1).length', len( H.sectionPoints( 1, 1 ) ) )
num( 'hull.setback(3).length', len( H.setback( 3 ) ) )
num( 'hull.station(1,3).length', len( H.station( 1, 3 ) ) )
num( 'hull.tAt(0,- 0.4)', H.tAt( 0, - 0.4 ) )
num( 'hull.hullXAt(0,- 0.4)', H.hullXAt( 0, - 0.4 ) )

s = H.buildHullSamples( 8 )
num( 'hull.buildHullSamples(8).length', len( s ) )
sa = 0; sv = 0

for x in s:
	sa += x.area; sv += x.area * x.depth

num( 'hull.samples.areaSum', sa * 0.5 )
num( 'hull.samples.volume', sv * 0.5 )

if len( s ):

	num( 'hull.samples[0].x', s[ 0 ].position.x )
	num( 'hull.samples[0].y', s[ 0 ].position.y )
	num( 'hull.samples[0].z', s[ 0 ].position.z )
	num( 'hull.samples[0].area', s[ 0 ].area )
	num( 'hull.samples[0].bottomY', s[ 0 ].bottomY )
	num( 'hull.samples[last].z', s[ len( s ) - 1 ].position.z )

num( 'hull.controlPoints(0.5) length', len( H.controlPoints( 0.5 ) ) )
num( 'hull.controlPoints(0.5)[1][0]', H.controlPoints( 0.5 )[ 1 ][ 0 ] )
num( 'hull.controlPoints(0.5)[1][1]', H.controlPoints( 0.5 )[ 1 ][ 1 ] )

s64 = H.buildHullSamples( 64 )
num( 'hull.buildHullSamples(64).length', len( s64 ) )
sa64 = 0; sv64 = 0

for x in s64:
	sa64 += x.area; sv64 += x.area * x.depth

num( 'hull.samples64.areaSum', sa64 * 0.5 )
num( 'hull.samples64.volume', sv64 * 0.5 )
num( 'hull.samples64.first.z', s64[ 0 ].position.z )
num( 'hull.samples64.last.z', s64[ len( s64 ) - 1 ].position.z )

# churn the section cache past its 4096-entry flush and re-query: results must be identical
for i in range( 4100 ):
	H.sectionPoints( i / 4100, 1 )

num( 'hull.postchurn.sectionPoints(0.5,1)[10]', H.sectionPoints( 0.5, 1 )[ 10 ] )
num( 'hull.postchurn.setback(1)[5]', H.setback( 1 )[ 5 ] )
num( 'hull.postchurn.canoeVolume', H.canoeVolume )
num( 'rhoseawater', RHO_SEAWATER )

# ------------------------------------------------------------------ output

LINES = out


def toRows( lines ):
	"""key -> list of values, from either side's TSV lines."""

	rows = {}

	for line in lines:

		parts = line.rstrip( '\n' ).split( '\t' )

		if len( parts ) < 3:
			continue

		rows[ ( parts[ 0 ], parts[ 1 ] ) ] = parts[ 2 : ]

	return rows


def same( a, b, tol = 1e-9 ):

	if a == b:
		return True

	try:
		fa = float( a ); fb = float( b )
	except ValueError:
		return False

	if fa != fa or fb != fb:
		return fa != fa and fb != fb

	return abs( fa - fb ) <= tol * max( 1.0, abs( fa ), abs( fb ) )


def compare( path, tol = 1e-9 ):

	with open( path, 'r' ) as f:
		ref = toRows( f.read().splitlines() )

	got = toRows( LINES )
	order = list( ref.keys() )
	bad = 0
	missing = [ k for k in got if k not in ref ]

	for k in order:

		a = ref.get( k )
		b = got.get( k )

		if b is None:

			print( 'MISSING  %-10s %s' % ( k[ 0 ], k[ 1 ] ) )
			bad += 1
			continue

		if len( a ) != len( b ):
			print( 'SHAPE    %-10s %s   js=%s py=%s' % ( k[ 0 ], k[ 1 ], a, b ) )
			bad += 1
			continue

		for i, ( x, y ) in enumerate( zip( a, b ) ):

			if not same( x, y, tol ):

				print( 'MISMATCH %-10s %s [%d]   js=%s py=%s' % ( k[ 0 ], k[ 1 ], i, x, y ) )
				bad += 1
				break

	for k in missing:
		print( 'EXTRA    %-10s %s' % ( k[ 0 ], k[ 1 ] ) )

	print( '' )
	print( '%d reference rows, %d mismatches' % ( len( order ), bad ) )
	return bad


if __name__ == '__main__':

	if '--cmp' in sys.argv:

		ref = sys.argv[ sys.argv.index( '--cmp' ) + 1 ]
		sys.exit( 1 if compare( ref ) else 0 )

	for line in LINES:
		print( line )
