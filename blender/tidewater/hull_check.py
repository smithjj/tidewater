#!/usr/bin/env python3
# Hull gate for the pure-Python boat port -- the Python side of hull_ref.mjs, which drives
# the real src/world/boat/HullBuilder.js in node.
#
# Builds the hull into a fresh GeoKit (BoatModel.js:55 adds it first, so the kit's whole
# content is the hull's contribution) and prints, per bucket:
#
#   - the triangle / vertex counts, and
#   - the bounding box and FNV-1a 32 hash of every attribute and of the index, so a
#     reordered vertex stream or a flipped triangle is caught, not just a count change,
#
# plus buildHullVolume() (a closed geometry, not a bucket) and keelVolume() (a number).
#
#   python3 blender/tidewater/hull_check.py                     # the table
#   python3 blender/tidewater/hull_check.py --json out.json     # table + JSON
#   python3 blender/tidewater/hull_check.py --cmp js.json       # compare, exit != 0 on a mismatch
#
# Run it from this directory (it imports its siblings by module name):
#   python3 hull_check.py
#
# Pure Python 3, standard library only (no numpy, no bpy).

import json
import os
import struct
import sys

sys.path.insert( 0, os.path.dirname( os.path.abspath( __file__ ) ) )

from geokit import GeoKit, triangleCount
from hull_lines import HullLines
from hull import buildHull, buildHullVolume, keelVolume

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


def record( g ):

	return { 'tris': int( triangleCount( g ) ), 'verts': g.attributes[ 'position' ].count, 'box': boxOf( g ), 'hash': hashBucket( g ) }


# ------------------------------------------------------------------ build

lines = HullLines()
kit = GeoKit()
buildHull( kit, lines )

hull = {}

for name in BUCKETS:

	g = kit.merged( name )

	if g is None:
		continue

	hull[ name ] = record( g )

hullVolume = record( buildHullVolume( lines ) )
keelV = keelVolume( lines )

DATA = { 'tool': 'python', 'buckets': BUCKETS, 'hull': hull, 'hullVolume': hullVolume, 'keelVolume': keelV }


# ------------------------------------------------------------------ report

def hashLine( label, w ):

	bb = w[ 'box' ]
	nums = ' '.join( '%.5f' % v for v in bb[ 'min' ] + bb[ 'max' ] )
	hashes = ' '.join( k + '=' + str( v ) for k, v in w[ 'hash' ].items() )
	return '  ' + label.ljust( 11 ) + ( str( w[ 'tris' ] ) + '/' + str( w[ 'verts' ] ) ).ljust( 12 ) + nums + '   ' + hashes


def report():

	print( 'buildHull into a fresh kit: counts, bounding box, FNV-1a 32 hashes' )

	for name in BUCKETS:

		w = hull.get( name )

		if w is None:
			continue

		print( hashLine( name, w ) )

	print( '' )
	print( 'buildHullVolume: ' + hashLine( 'volume', hullVolume ).strip() )
	print( 'keelVolume: ' + repr( keelV ) )


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
	"""Diff this run against hull_ref.mjs --json output."""

	ref = json.load( open( path ) )
	bad = 0

	print( '' )
	print( 'against ' + path )
	print( 'bucket'.ljust( 12 ) + 'tris/verts (JS)'.rjust( 20 ) + 'tris/verts (PY)'.rjust( 20 ) + '   verdict' )

	for name in BUCKETS:

		a = ref[ 'hull' ].get( name )
		b = hull.get( name )

		if a is None and b is None:
			continue

		note = hashVerdict( a, b, tol )
		bad += 0 if note is None else 1
		as_ = '%d/%d' % ( a[ 'tris' ], a[ 'verts' ] ) if a else '-'
		bs = '%d/%d' % ( b[ 'tris' ], b[ 'verts' ] ) if b else '-'
		print( name.ljust( 12 ) + as_.rjust( 20 ) + bs.rjust( 20 ) + '   ' + ( 'match' if note is None else 'MISMATCH (' + note + ')' ) )

	# buildHullVolume: counts, bbox and hashes
	note = hashVerdict( ref.get( 'hullVolume' ), hullVolume, tol )
	bad += 0 if note is None else 1
	print( '' )
	a = ref.get( 'hullVolume' )
	print( 'hullVolume   ' + ( '%d/%d' % ( a[ 'tris' ], a[ 'verts' ] ) if a else '-' ).rjust( 20 ) + ( '%d/%d' % ( hullVolume[ 'tris' ], hullVolume[ 'verts' ] ) ).rjust( 20 ) + '   ' + ( 'match' if note is None else 'MISMATCH (' + note + ')' ) )

	# keelVolume: a number
	dk = abs( ref[ 'keelVolume' ] - keelV )
	ok = dk <= max( 1e-9, tol * abs( ref[ 'keelVolume' ] ) )
	bad += 0 if ok else 1
	print( 'keelVolume   ' + repr( ref[ 'keelVolume' ] ).rjust( 20 ) + repr( keelV ).rjust( 20 ) + '   ' + ( 'match' if ok else 'MISMATCH (delta %.3g)' % dk ) )

	print( '' )
	print( '%d mismatched row(s)' % bad )
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
