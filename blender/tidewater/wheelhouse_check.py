#!/usr/bin/env python3
# Wheelhouse gate for the pure-Python boat port -- the Python side of tools/boat/stats.mjs.
#
# Builds the wheelhouse into a fresh GeoKit (the JS side measures the same thing as the
# difference between its buildHull and buildWheelhouse snapshots, which is the same set of
# geometries: GeoKit is only an accumulator, so the wheelhouse's contribution does not
# depend on what was added before it) and prints, per bucket:
#
#   - the wheelhouse's triangle / vertex contribution, and
#   - the bounding box and FNV-1a 32 hash of every attribute and of the index, so a
#     reordered vertex stream or a flipped triangle is caught, not just a count change.
#
#   python3 blender/tidewater/wheelhouse_check.py                     # the table
#   python3 blender/tidewater/wheelhouse_check.py --json out.json     # table + JSON
#   python3 blender/tidewater/wheelhouse_check.py --cmp js.json       # compare, exit != 0 on a mismatch
#
# Run it from this directory (it imports its siblings by module name):
#   python3 wheelhouse_check.py
#
# Pure Python 3, standard library only (no numpy, no bpy).

import json
import os
import struct
import sys

sys.path.insert( 0, os.path.dirname( os.path.abspath( __file__ ) ) )

from geokit import GeoKit, triangleCount
from hull_lines import HullLines
from wheelhouse import buildWheelhouse, wheelGeometry, throttleGeometry, radarArrayGeometry

# BoatModel.js:11
BUCKETS = [ 'hull', 'gelcoat', 'wood', 'fittings', 'trap', 'glow', 'glass' ]

ATTRS = [ 'position', 'normal', 'uv', 'color', 'aux' ]


def fnv32( data ):
	"""FNV-1a 32 over a bytes-like, matching the JS side's hash."""

	h = 0x811c9dc5

	for b in data:

		h = ( ( h ^ b ) * 16777619 ) & 0xFFFFFFFF

	return h


def hashBucket( g ):

	out = {}

	for attr in ATTRS:

		a = g.attributes.get( attr )

		if a is None:
			continue

		n = a.count * a.itemSize
		out[ attr ] = fnv32( struct.pack( '<%df' % n, *[ a.array[ i ] for i in range( n ) ] ) )

	if g.index:

		n = g.index.count
		out[ 'index' ] = fnv32( struct.pack( '<%dI' % n, *[ g.index.getX( i ) for i in range( n ) ] ) )

	return out


def boxOf( g ):

	g.computeBoundingBox()
	bb = g.boundingBox
	return { 'min': [ bb.min.x, bb.min.y, bb.min.z ], 'max': [ bb.max.x, bb.max.y, bb.max.z ] }


def snapshot( kit ):

	out = {}

	for name in BUCKETS:

		g = kit.merged( name )
		out[ name ] = { 'tris': int( triangleCount( g ) ), 'verts': g.attributes[ 'position' ].count } if g else { 'tris': 0, 'verts': 0 }

	return out


# ------------------------------------------------------------------ build

lines = HullLines()
kit = GeoKit()
parts = {}

before = snapshot( kit )
buildWheelhouse( kit, lines, parts )
after = snapshot( kit )

wheelhouse = {}

for name in BUCKETS:
	wheelhouse[ name ] = { 'tris': after[ name ][ 'tris' ] - before[ name ][ 'tris' ], 'verts': after[ name ][ 'verts' ] - before[ name ][ 'verts' ] }

wheelhouseOnly = {}

for name in BUCKETS:

	g = kit.merged( name )

	if g is None:
		continue

	wheelhouseOnly[ name ] = { 'tris': int( triangleCount( g ) ), 'verts': g.attributes[ 'position' ].count, 'box': boxOf( g ), 'hash': hashBucket( g ) }

# the three animated geometries (BoatModel attaches them to their own meshes)
ANIMATED_MAKERS = [ ( 'wheel', wheelGeometry ), ( 'throttle', throttleGeometry ), ( 'radar', radarArrayGeometry ) ]
animated = {}

for name, make in ANIMATED_MAKERS:

	g = make()
	animated[ name ] = { 'tris': int( triangleCount( g ) ), 'verts': g.attributes[ 'position' ].count, 'box': boxOf( g ), 'hash': hashBucket( g ) }

DATA = { 'tool': 'python', 'buckets': BUCKETS, 'wheelhouse': wheelhouse, 'wheelhouseOnly': wheelhouseOnly, 'animated': animated, 'parts': sorted( parts.keys() ) }


# ------------------------------------------------------------------ report

def col( s ):
	return str( s ).rjust( 11 )


def hashLine( label, w ):

	bb = w[ 'box' ]
	nums = ' '.join( '%.5f' % v for v in bb[ 'min' ] + bb[ 'max' ] )
	hashes = ' '.join( k + '=' + str( v ) for k, v in w[ 'hash' ].items() )
	return '  ' + label.ljust( 11 ) + ( str( w[ 'tris' ] ) + '/' + str( w[ 'verts' ] ) ).ljust( 12 ) + nums + '   ' + hashes


def report():

	print( 'wheelhouse delta  (buildWheelhouse into a fresh kit)' )
	print( 'bucket'.ljust( 12 ) + col( 'tris' ) + col( 'verts' ) )

	for name in BUCKETS:
		print( name.ljust( 12 ) + col( wheelhouse[ name ][ 'tris' ] ) + col( wheelhouse[ name ][ 'verts' ] ) )

	total = sum( wheelhouse[ name ][ 'tris' ] for name in BUCKETS )
	totalV = sum( wheelhouse[ name ][ 'verts' ] for name in BUCKETS )
	print( 'TOTAL'.ljust( 12 ) + col( total ) + col( totalV ) )
	print( '' )
	print( 'wheelhouse buckets: counts, bounding box, FNV-1a 32 hashes' )

	for name in BUCKETS:

		w = wheelhouseOnly.get( name )

		if w is None:
			continue

		print( hashLine( name, w ) )

	print( '' )
	print( 'animated geometries: counts, bounding box, FNV-1a 32 hashes' )

	for name, _make in ANIMATED_MAKERS:
		print( hashLine( name, animated[ name ] ) )

	print( '' )
	print( 'parts keys: ' + ', '.join( sorted( parts.keys() ) ) )


def hashVerdict( a, b, tol = 1e-6 ):
	"""Compare two { tris, verts, box, hash } records; returns a note or None."""

	notes = []

	if a is None or b is None:
		return 'missing'

	if a[ 'tris' ] != b[ 'tris' ] or a[ 'verts' ] != b[ 'verts' ]:
		notes.append( 'counts' )

	for k in range( 3 ):

		if abs( a[ 'box' ][ 'min' ][ k ] - b[ 'box' ][ 'min' ][ k ] ) > tol or abs( a[ 'box' ][ 'max' ][ k ] - b[ 'box' ][ 'max' ][ k ] ) > tol:
			notes.append( 'bbox' )
			break

	bad_hash = []

	for attr in ATTRS + [ 'index' ]:

		if attr not in a and attr not in b:
			continue

		if a.get( attr ) != b.get( attr ):
			bad_hash.append( attr )

	if bad_hash:
		notes.append( 'hash:' + ','.join( bad_hash ) )

	return '; '.join( notes ) if notes else None


def compare( path, tol = 1e-6 ):
	"""Diff this run against tools/boat/stats.mjs --json output."""

	ref = json.load( open( path ) )
	bad = 0

	print( '' )
	print( 'against ' + path )
	print( 'bucket'.ljust( 12 ) + col( 'tris' ) + col( 'verts' ) + '   verdict' )

	for name in BUCKETS:

		a = ref[ 'wheelhouse' ].get( name, { 'tris': 0, 'verts': 0 } )
		b = wheelhouse[ name ]
		ok = a[ 'tris' ] == b[ 'tris' ] and a[ 'verts' ] == b[ 'verts' ]
		bad += 0 if ok else 1
		print( name.ljust( 12 ) + col( '%d/%d' % ( a[ 'tris' ], a[ 'verts' ] ) ) + col( '%d/%d' % ( b[ 'tris' ], b[ 'verts' ] ) ) + '   ' + ( 'match' if ok else 'MISMATCH' ) )

	# bounding boxes and hashes of the wheelhouse-only merge
	print( '' )
	print( 'wheelhouse-only buckets: bbox + hash' )

	for name in BUCKETS:

		a = ref.get( 'wheelhouseOnly', {} ).get( name )
		b = wheelhouseOnly.get( name )

		if a is None and b is None:
			continue

		note = hashVerdict( a, b )
		bad += 0 if note is None else 1
		print( '  ' + name.ljust( 11 ) + ( 'match' if note is None else 'MISMATCH (' + note + ')' ) )

	print( '' )
	print( 'animated geometries: bbox + hash' )

	for name, _make in ANIMATED_MAKERS:

		note = hashVerdict( ref.get( 'animated', {} ).get( name ), animated.get( name ) )
		bad += 0 if note is None else 1
		print( '  ' + name.ljust( 11 ) + ( 'match' if note is None else 'MISMATCH (' + note + ')' ) )

	print( '' )
	print( '%d mismatched bucket(s)' % bad )
	return bad


if __name__ == '__main__':

	report()
	print( '' )
	print( 'JSON' )
	print( json.dumps( DATA ) )

	if '--json' in sys.argv:
		open( sys.argv[ sys.argv.index( '--json' ) + 1 ], 'w' ).write( json.dumps( DATA ) + '\n' )

	if '--cmp' in sys.argv:
		sys.exit( 1 if compare( sys.argv[ sys.argv.index( '--cmp' ) + 1 ] ) else 0 )
