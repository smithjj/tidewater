#!/usr/bin/env python3
# Blender assembly of the procedural lobster boat -- the only file in the port that touches
# bpy. Everything geometric comes from the pure-Python core (blender/tidewater/*), which
# reproduces src/world/boat/*.js bit for bit; this file just mirrors how src/world/BoatModel.js
# puts those pieces into a scene.
#
#   blender --background --python blender/build_boat.py
#   (or in Blender: Scripting > Open > build_boat.py > Run Script)
#
# What it mirrors from BoatModel.js:
#   - the build order (BoatModel.js:50-57): HullLines, a fresh GeoKit, parts = {}, then
#     buildHull, buildWheelhouse, buildDeckGear;
#   - one mesh per static bucket named boat-<bucket> (BoatModel.js:63-75), skipping a bucket
#     the kit never filled -- 'trap' stays empty, the pots are a separate loaded glb
#     (BoatModel.js:363-364);
#   - one mesh per animated part named boat-<part> (BoatModel.js:79-103), each placed at its
#     pivot from `parts`; the helm wheel hangs off a pivot at parts.wheelCenter whose local
#     +Z is parts.wheelAxis (BoatModel.js:93-98);
#   - the data every vertex carries: position, normal, uv, color (linear RGB) as the mesh's
#     Color attribute and aux as a second float4 attribute (roughness, metalness, pattern,
#     anim), exactly as GeoKit.prepare() lays it out;
#   - group.name = 'LobsterBoat', one Principled material per bucket with the color attribute
#     wired into Base Color, and castShadow = name !== 'glass' (Object.visible_shadow).
#
# Frame: like the game, the port builds in the boat frame (+Z forward, +Y up, +X port,
# metres, y = 0 the design waterline), and the whole boat is then rotated into Blender's
# Z-up -- a root empty with rotation_euler.x = +90 degrees, which maps the game's +Y up onto
# Blender's +Z up and the game's bow (+Z) onto Blender's -Y (front view).
#
# Blender version: written against the Blender 4.5 (4.x) Python API, checked against
# docs.blender.org/api/4.5. The 4.1+ behaviours it relies on: Mesh.from_pydata()'s
# shade_flat default (so shade_smooth() is called explicitly), custom split normals through
# Mesh.normals_split_custom_set_from_vertices(), color attributes through
# Mesh.color_attributes.new(name, type, domain), and UVLoopLayers.new()'s keyword-only name.
# It runs on earlier 4.x/3.x builds too except for the glass render method, which is set
# through whichever of Material.surface_render_method (4.2+) / blend_method (<= 4.1) exists.
#
# Self-checking: it prints every object's triangle / vertex count, the overall bounding box
# and the boat's principal dimensions, and compares them with the numbers the real JS produces
# through tools/boat/export.mjs. Any mismatch is printed and the script exits non-zero.
#
# Pure Python 3 outside the bpy block; the geometry imports are stdlib-only.

import math
import os
import sys

HERE = os.path.dirname( os.path.abspath( __file__ ) )
sys.path.insert( 0, os.path.join( HERE, 'tidewater' ) )

from mathutil import Matrix4, Quaternion, Vector3
from geokit import GeoKit, triangleCount
from hull_lines import HullLines
from hull import KEEL, buildHull
from wheelhouse import buildWheelhouse, radarArrayGeometry, throttleGeometry, wheelGeometry
from deckgear import buildDeckGear
from running import PROP, RUDDER, propellerGeometry, rudderGeometry


# BoatModel.js:11
STATIC_BUCKETS = [ 'hull', 'gelcoat', 'wood', 'fittings', 'trap', 'glow', 'glass' ]

# BoatModel.js:98-103: which bucket's material each animated part draws with
PART_MATERIAL = { 'wheel': 'wood', 'throttle': 'fittings', 'radar': 'fittings', 'propeller': 'fittings', 'rudder': 'fittings' }

COLOR_ATTR = 'Color' # the `color` attribute, linear RGB
AUX_ATTR = 'aux' # the aux float4: roughness, metalness, pattern, anim

# ------------------------------------------------------------------ the gate
#
# Measured from the real JS with tools/boat/export.mjs (and node tools/boat/stats.mjs):
# per object, triangles / vertices; then the boat's envelope and principal dimensions.
EXPECTED = {
	'hull': ( 8337, 4513 ),
	'gelcoat': ( 4466, 3469 ),
	'wood': ( 3314, 2221 ),
	'fittings': ( 25326, 29366 ),
	'trap': ( 0, 0 ), # never filled: not added as an object either
	'glow': ( 398, 616 ),
	'glass': ( 300, 189 ),
	'wheel': ( 1880, 1358 ),
	'throttle': ( 332, 293 ),
	'radar': ( 368, 1012 ),
	'propeller': ( 1276, 800 ),
	'rudder': ( 168, 216 ),
}

EXPECTED_BUCKET_TRIS = 42141 # 8337 + 4466 + 3314 + 25326 + 398 + 300
EXPECTED_ANIMATED_TRIS = 4024 # 1880 + 332 + 368 + 1276 + 168
EXPECTED_TOTAL_TRIS = 46165

# The hull's own bounding box -- the boat's envelope: LOA, beam and draft. (game frame)
# This is the box the export lists as the boat's: it is exactly the hull bucket's bbox
# (-1.449998 -0.770000 -3.900000 .. 1.449998 1.600000 4.300000), i.e. the sheer beam, the
# keel shoe and the stem head to the transom -- the same numbers BoatModel.dimensions holds.
EXPECTED_BBOX = ( ( - 1.449998, - 0.770000, - 3.900000 ), ( 1.449998, 1.600000, 4.300000 ) )
EXPECTED_DIMENSIONS = ( 8.20, 2.90, 0.77 ) # LOA, beam, draft (m)

# Every object of the whole boat, in the game frame, pivots applied: hull envelope plus the
# wheelhouse mast and the deck gear that overhangs the sheer.
EXPECTED_COLLECTION_BBOX = ( ( - 1.928495, - 0.770000, - 4.260000 ), ( 1.553660, 4.860020, 4.479175 ) )

TOL = 1e-5


# ------------------------------------------------------------------ build (no bpy)

def buildModel():
	"""Build the boat exactly as src/world/BoatModel.js:50-57 does.

	Returns ( lines, kit, parts ) with the kit accumulating the hull, the wheelhouse and the
	deck gear into the material buckets, and parts carrying the animated pivots.
	"""

	lines = HullLines()
	kit = GeoKit()
	parts = {}

	buildHull( kit, lines )
	buildWheelhouse( kit, lines, parts )
	buildDeckGear( kit, lines, parts )

	return lines, kit, parts


def boatParts( kit, parts ):
	"""The meshes BoatModel makes, in its own order (BoatModel.js:63-103).

	Each entry is { name, geo, material, position, parent, quaternion } with name/position in
	the boat frame; `geo` is None for the wheel pivot (an Empty). A bucket the kit never
	filled (trap) is left out, as BoatModel.js:66 does.
	"""

	out = []

	for name in STATIC_BUCKETS:

		geo = kit.merged( name )

		if geo is None:
			continue

		out.append( { 'name': name, 'geo': geo, 'material': name, 'position': Vector3( 0, 0, 0 ), 'parent': 'root' } )

	# helm wheel: pivot aligned with the shaft, wheel spins about its local Z (BoatModel.js:93-98)
	quat = Quaternion().setFromUnitVectors( Vector3( 0, 0, 1 ), parts[ 'wheelAxis' ] )
	out.append( { 'name': 'wheel-pivot', 'geo': None, 'material': None, 'position': parts[ 'wheelCenter' ].clone(), 'quaternion': quat, 'parent': 'root' } )
	out.append( { 'name': 'wheel', 'geo': wheelGeometry(), 'material': 'wood', 'position': Vector3( 0, 0, 0 ), 'parent': 'wheel-pivot' } )

	out.append( { 'name': 'throttle', 'geo': throttleGeometry(), 'material': 'fittings', 'position': parts[ 'throttlePivot' ].clone(), 'parent': 'root' } )
	out.append( { 'name': 'radar', 'geo': radarArrayGeometry(), 'material': 'fittings', 'position': parts[ 'radarPivot' ].clone(), 'parent': 'root' } )
	out.append( { 'name': 'propeller', 'geo': propellerGeometry(), 'material': 'fittings', 'position': PROP[ 'position' ].clone(), 'parent': 'root' } )
	out.append( { 'name': 'rudder', 'geo': rudderGeometry(), 'material': 'fittings', 'position': RUDDER[ 'pivot' ].clone(), 'parent': 'root' } )

	return out


def worldMatrices( pieces ):
	"""Game-frame world matrix per part, parents applied (the root is the identity)."""

	out = {}

	for part in pieces:

		local = Matrix4().compose( part[ 'position' ], part.get( 'quaternion', Quaternion() ), Vector3( 1, 1, 1 ) )
		parent = part[ 'parent' ]
		out[ part[ 'name' ] ] = out[ parent ].clone().multiply( local ) if parent != 'root' else local

	return out


def boxOf( geo ):
	"""( min, max ) of a geometry in its own frame."""

	geo.computeBoundingBox()
	bb = geo.boundingBox
	return ( bb.min.x, bb.min.y, bb.min.z ), ( bb.max.x, bb.max.y, bb.max.z )


def collectionBox( pieces ):
	"""Union bounding box of every part with its pivot applied -- what export.mjs walks."""

	acc = [ math.inf, math.inf, math.inf, - math.inf, - math.inf, - math.inf ]
	mats = worldMatrices( pieces )
	v = Vector3()

	for part in pieces:

		geo = part[ 'geo' ]

		if geo is None:
			continue

		m = mats[ part[ 'name' ] ]
		pos = geo.attributes[ 'position' ]

		for i in range( pos.count ):

			v.fromBufferAttribute( pos, i ).applyMatrix4( m )

			if v.x < acc[ 0 ]:
				acc[ 0 ] = v.x

			if v.x > acc[ 3 ]:
				acc[ 3 ] = v.x

			if v.y < acc[ 1 ]:
				acc[ 1 ] = v.y

			if v.y > acc[ 4 ]:
				acc[ 4 ] = v.y

			if v.z < acc[ 2 ]:
				acc[ 2 ] = v.z

			if v.z > acc[ 5 ]:
				acc[ 5 ] = v.z

	return ( acc[ 0 ], acc[ 1 ], acc[ 2 ] ), ( acc[ 3 ], acc[ 4 ], acc[ 5 ] )


# ------------------------------------------------------------------ the self-check

def close( a, b ):

	return abs( a - b ) <= TOL * max( 1.0, abs( a ), abs( b ) )


def checkData( pieces, lines ):
	"""Print and check every number the assembled geometry claims. Returns the failures."""

	failures = []
	bucketTris = 0
	animatedTris = 0

	print( 'boat meshes (game frame): triangles / vertices' )
	print( '  ' + 'object'.ljust( 22 ) + 'tris'.rjust( 8 ) + 'verts'.rjust( 8 ) + '   expected' )

	for part in pieces:

		name = part[ 'name' ]
		geo = part[ 'geo' ]

		if geo is None:
			print( '  ' + ( 'boat-' + name ).ljust( 22 ) + '   (empty: the wheel pivot)' )
			continue

		tris = int( triangleCount( geo ) )
		verts = geo.attributes[ 'position' ].count
		want = EXPECTED[ name ]

		if ( tris, verts ) != want:
			failures.append( 'boat-%s: %d/%d triangles/vertices, expected %d/%d' % ( name, tris, verts, want[ 0 ], want[ 1 ] ) )

		if name in STATIC_BUCKETS:
			bucketTris += tris
		else:
			animatedTris += tris

		print( '  ' + ( 'boat-' + name ).ljust( 22 ) + str( tris ).rjust( 8 ) + str( verts ).rjust( 8 ) + '   ' + '%d/%d' % want )

	if 'trap' in [ part[ 'name' ] for part in pieces ]:
		failures.append( 'trap bucket is not empty (it must be: the pots are a separate glb)' )

	print( '' )
	print( '  buckets   %d triangles (expected %d)' % ( bucketTris, EXPECTED_BUCKET_TRIS ) )
	print( '  animated  %d triangles (expected %d)' % ( animatedTris, EXPECTED_ANIMATED_TRIS ) )
	print( '  total     %d triangles (expected %d)' % ( bucketTris + animatedTris, EXPECTED_TOTAL_TRIS ) )

	if bucketTris != EXPECTED_BUCKET_TRIS:
		failures.append( 'bucket triangles %d != %d' % ( bucketTris, EXPECTED_BUCKET_TRIS ) )

	if animatedTris != EXPECTED_ANIMATED_TRIS:
		failures.append( 'animated triangles %d != %d' % ( animatedTris, EXPECTED_ANIMATED_TRIS ) )

	if bucketTris + animatedTris != EXPECTED_TOTAL_TRIS:
		failures.append( 'total triangles %d != %d' % ( bucketTris + animatedTris, EXPECTED_TOTAL_TRIS ) )

	# the hull's own box is the boat's envelope: LOA (z), beam (x) and draft (-y)
	hull = None

	for part in pieces:
		if part[ 'name' ] == 'hull':
			hull = part[ 'geo' ]

	bbMin, bbMax = boxOf( hull )
	print( '' )
	print( 'overall bounding box (hull envelope, game frame)' )
	print( '  x  %+.6f .. %+.6f' % ( bbMin[ 0 ], bbMax[ 0 ] ) )
	print( '  y  %+.6f .. %+.6f' % ( bbMin[ 1 ], bbMax[ 1 ] ) )
	print( '  z  %+.6f .. %+.6f' % ( bbMin[ 2 ], bbMax[ 2 ] ) )

	for i in range( 3 ):

		if not close( bbMin[ i ], EXPECTED_BBOX[ 0 ][ i ] ) or not close( bbMax[ i ], EXPECTED_BBOX[ 1 ][ i ] ):
			failures.append( 'bounding box axis %d: (%r .. %r) != (%r .. %r)' % ( i, bbMin[ i ], bbMax[ i ], EXPECTED_BBOX[ 0 ][ i ], EXPECTED_BBOX[ 1 ][ i ] ) )

	loa = bbMax[ 2 ] - bbMin[ 2 ]
	beam = bbMax[ 0 ] - bbMin[ 0 ]
	draft = - bbMin[ 1 ]
	print( '  LOA %.2f m   beam %.2f m   draft %.2f m   (expected %.2f / %.2f / %.2f)' % ( loa, beam, draft, EXPECTED_DIMENSIONS[ 0 ], EXPECTED_DIMENSIONS[ 1 ], EXPECTED_DIMENSIONS[ 2 ] ) )

	if not close( loa, EXPECTED_DIMENSIONS[ 0 ] ) or not close( beam, EXPECTED_DIMENSIONS[ 1 ] ) or not close( draft, EXPECTED_DIMENSIONS[ 2 ] ):
		failures.append( 'dimensions %.4f/%.4f/%.4f != %.4f/%.4f/%.4f' % ( loa, beam, draft, EXPECTED_DIMENSIONS[ 0 ], EXPECTED_DIMENSIONS[ 1 ], EXPECTED_DIMENSIONS[ 2 ] ) )

	# the box of everything (wheelhouse mast, davit, bow gear), pivots applied
	cMin, cMax = collectionBox( pieces )
	print( '' )
	print( 'every object, pivots applied (game frame)' )
	print( '  x  %+.6f .. %+.6f' % ( cMin[ 0 ], cMax[ 0 ] ) )
	print( '  y  %+.6f .. %+.6f' % ( cMin[ 1 ], cMax[ 1 ] ) )
	print( '  z  %+.6f .. %+.6f' % ( cMin[ 2 ], cMax[ 2 ] ) )

	for i in range( 3 ):

		if not close( cMin[ i ], EXPECTED_COLLECTION_BBOX[ 0 ][ i ] ) or not close( cMax[ i ], EXPECTED_COLLECTION_BBOX[ 1 ][ i ] ):
			failures.append( 'collection box axis %d: (%r .. %r) != (%r .. %r)' % ( i, cMin[ i ], cMax[ i ], EXPECTED_COLLECTION_BBOX[ 0 ][ i ], EXPECTED_COLLECTION_BBOX[ 1 ][ i ] ) )

	# the boat's own dimensions, straight off the hull lines (BoatModel.js:116-127)
	maxSheer = max( lines.sheerX( i / 200 ) for i in range( 201 ) )
	print( '' )
	print( 'hull lines: LOA %.2f m  beam %.2f m  draft %.2f m  deck %.4f m' % ( lines.length, maxSheer * 2, - KEEL[ 'bottom' ], lines.deckY ) )

	return failures


# ------------------------------------------------------------------ bpy

def fillMesh( mesh, geo ):
	"""One GeoKit geometry -> one Blender mesh: position, normal, uv, color, aux."""

	n = geo.attributes[ 'position' ].count
	pos = geo.attributes[ 'position' ]
	index = geo.index

	verts = [ ( pos.array[ i * 3 ], pos.array[ i * 3 + 1 ], pos.array[ i * 3 + 2 ] ) for i in range( n ) ]
	faces = [ ( index.getX( i ), index.getX( i + 1 ), index.getX( i + 2 ) ) for i in range( 0, index.count, 3 ) ]

	mesh.from_pydata( verts, [], faces )
	mesh.update()
	mesh.shade_smooth() # from_pydata shades flat by default; the geometry is smooth-shaded

	# uv: the port's per-vertex uv becomes a per-loop uv (Blender has no per-vertex uvs)
	uv = geo.attributes[ 'uv' ]
	layer = mesh.uv_layers.new( name = 'UVMap' )

	for li, loop in enumerate( mesh.loops ):

		v = loop.vertex_index
		layer.data[ li ].uv = ( uv.getX( v ), uv.getY( v ) )

	# color: the linear RGB the JS material reads, as a 4-component color attribute
	color = geo.attributes[ 'color' ]
	attr = mesh.color_attributes.new( name = COLOR_ATTR, type = 'FLOAT_COLOR', domain = 'POINT' )

	for i in range( n ):

		attr.data[ i ].color = ( color.array[ i * 3 ], color.array[ i * 3 + 1 ], color.array[ i * 3 + 2 ], 1.0 )

	# aux = ( roughness, metalness, pattern, anim ): Blender's only 4-float attribute is a color
	aux = geo.attributes[ 'aux' ]
	auxAttr = mesh.attributes.new( name = AUX_ATTR, type = 'FLOAT_COLOR', domain = 'POINT' )

	for i in range( n ):

		auxAttr.data[ i ].color = ( aux.array[ i * 4 ], aux.array[ i * 4 + 1 ], aux.array[ i * 4 + 2 ], aux.array[ i * 4 + 3 ] )

	# the normals the JS computed, kept as custom split normals
	normal = geo.attributes[ 'normal' ]
	mesh.normals_split_custom_set_from_vertices( [ ( normal.array[ i * 3 ], normal.array[ i * 3 + 1 ], normal.array[ i * 3 + 2 ] ) for i in range( n ) ] )


def makeMaterial( bucket ):
	"""One Principled material per bucket with the color attribute wired into Base Color."""

	import bpy

	mat = bpy.data.materials.new( 'boat-' + bucket )
	mat.use_nodes = True
	tree = mat.node_tree
	bsdf = tree.nodes[ 'Principled BSDF' ]
	node = tree.nodes.new( 'ShaderNodeVertexColor' )
	node.layer_name = COLOR_ATTR
	node.location = ( - 320, 120 )
	tree.links.new( node.outputs[ 'Color' ], bsdf.inputs[ 'Base Color' ] )

	if bucket == 'glass':

		# BoatMaterials.js:463: the wheelhouse glass is transparent (opacity 0.25)
		bsdf.inputs[ 'Alpha' ].default_value = 0.25

		if hasattr( mat, 'surface_render_method' ): # Blender 4.2+
			mat.surface_render_method = 'BLENDED'
		elif hasattr( mat, 'blend_method' ): # Blender <= 4.1
			mat.blend_method = 'BLEND'

	return mat


def buildInBlender( pieces ):
	"""Mirror BoatModel.js:59-105 into the scene: meshes at the root, parts at their pivots."""

	import bpy

	# ---- a clean file, so the script is idempotent
	for obj in list( bpy.data.objects ):
		bpy.data.objects.remove( obj, do_unlink = True )

	for coll in list( bpy.data.collections ):
		bpy.data.collections.remove( coll )

	for mesh in list( bpy.data.meshes ):
		bpy.data.meshes.remove( mesh )

	for mat in list( bpy.data.materials ):
		bpy.data.materials.remove( mat )

	scene = bpy.context.scene
	collection = bpy.data.collections.new( 'LobsterBoat' ) # group.name = 'LobsterBoat'
	scene.collection.children.link( collection )

	# ---- the boat frame is +Y up, so the whole boat is rotated into Blender's Z-up
	root = bpy.data.objects.new( 'LobsterBoat', None )
	root.empty_display_type = 'PLAIN_AXES'
	root.empty_display_size = 2.0
	root.rotation_mode = 'XYZ'
	root.rotation_euler = ( math.pi / 2, 0, 0 )
	collection.objects.link( root )

	materials = {}
	made = {}

	for part in pieces:

		name = part[ 'name' ]
		geo = part[ 'geo' ]
		bucket = part[ 'material' ]

		if geo is None:

			obj = bpy.data.objects.new( 'boat-' + name, None ) # the wheel pivot
			obj.empty_display_type = 'PLAIN_AXES'
			obj.empty_display_size = 0.1

		else:

			if bucket not in materials:
				materials[ bucket ] = makeMaterial( bucket )

			mesh = bpy.data.meshes.new( 'boat-' + name )
			fillMesh( mesh, geo )
			mesh.materials.append( materials[ bucket ] )
			obj = bpy.data.objects.new( 'boat-' + name, mesh )

		collection.objects.link( obj )
		obj.parent = root if part[ 'parent' ] == 'root' else made[ part[ 'parent' ] ]
		obj.location = ( part[ 'position' ].x, part[ 'position' ].y, part[ 'position' ].z )

		if 'quaternion' in part:

			q = part[ 'quaternion' ]
			obj.rotation_mode = 'QUATERNION'
			obj.rotation_quaternion = ( q._w, q._x, q._y, q._z )

		# castShadow = name !== 'glass' (BoatModel.js:69-71); receiveShadow has no per-object
		# Blender equivalent, and three's renderOrder for the glass becomes the material's
		# blended render method (see makeMaterial)
		obj.visible_shadow = name != 'glass'
		made[ name ] = obj

	bpy.context.view_layer.update()
	return collection, [ part[ 'name' ] for part in pieces ]


def checkBlender( made ):
	"""Read the counts back off the Blender meshes: the bpy path must have kept them."""

	import bpy

	failures = []

	for name in made:

		obj = bpy.data.objects[ 'boat-' + name ]

		if obj.type != 'MESH':
			continue

		tris = len( obj.data.polygons ) # every face from_pydata was given is a triangle
		verts = len( obj.data.vertices )
		want = EXPECTED[ name ]

		if ( tris, verts ) != want:
			failures.append( 'blender boat-%s: %d/%d triangles/vertices, expected %d/%d' % ( name, tris, verts, want[ 0 ], want[ 1 ] ) )

		if len( obj.data.uv_layers ) != 1:
			failures.append( 'blender boat-%s: %d uv layers, expected 1' % ( name, len( obj.data.uv_layers ) ) )

		if COLOR_ATTR not in [ a.name for a in obj.data.color_attributes ]:
			failures.append( 'blender boat-%s: no %s color attribute' % ( name, COLOR_ATTR ) )

		if AUX_ATTR not in [ a.name for a in obj.data.attributes ]:
			failures.append( 'blender boat-%s: no %s attribute' % ( name, AUX_ATTR ) )

	return failures


def main():
	failures = []

	print( '==== building the lobster boat ====' )
	lines, kit, parts = buildModel()
	pieces = boatParts( kit, parts )

	failures += checkData( pieces, lines )

	collection, names = buildInBlender( pieces )
	failures += checkBlender( names )

	print( '' )
	print( 'LobsterBoat collection: %d objects' % len( collection.objects ) )

	if failures:

		print( '' )
		print( 'SELF-CHECK FAILED (%d):' % len( failures ) )

		for f in failures:
			print( '  ' + f )

		sys.exit( 1 )

	print( '' )
	print( 'SELF-CHECK OK: every object and every dimension matches the JS reference.' )


if __name__ == '__main__':
	main()
