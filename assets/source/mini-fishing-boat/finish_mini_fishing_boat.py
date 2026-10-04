"""Validate and produce the final delivery through the connected Blender MCP."""
import bpy, bmesh, json, math
from pathlib import Path
from mathutils import Vector
OUT=Path(__file__).resolve().parent
S=bpy.context.scene
S.cycles.device='GPU'; S.cycles.samples=96; S.cycles.use_denoising=True
S.render.resolution_percentage=100
S.render.resolution_x=1800; S.render.resolution_y=1200
S.camera=bpy.data.objects['CAMERA | Reference three-quarter']
S.render.film_transparent=False
S.render.filepath=str(OUT/'mini_fishing_boat_hero.png')
bpy.ops.render.render(write_still=True)
S.camera=bpy.data.objects['CAMERA | Elevated bow']
S.render.filepath=str(OUT/'mini_fishing_boat_cockpit.png')
bpy.ops.render.render(write_still=True)
S.camera=bpy.data.objects['CAMERA | Stern & cockpit']
S.render.filepath=str(OUT/'working'/'stern_check.png')
S.render.resolution_percentage=65
bpy.ops.render.render(write_still=True)
S.camera=bpy.data.objects['CAMERA | Reference three-quarter']
S.render.resolution_percentage=100
S.render.film_transparent=True
ground=bpy.data.objects['Ground | soft studio shadow']; ground.hide_render=True
S.render.filepath=str(OUT/'mini_fishing_boat_transparent.png')
bpy.ops.render.render(write_still=True)
ground.hide_render=False; S.render.film_transparent=False
S.render.filepath=str(OUT/'mini_fishing_boat_hero.png')
for screen in bpy.data.screens:
    for a in screen.areas:
        if a.type=='VIEW_3D':
            v=a.spaces.active
            v.region_3d.view_perspective='ORTHO'
            v.region_3d.view_location=Vector((0,0,.73))
            v.region_3d.view_distance=4.3
            v.region_3d.view_rotation=S.camera.rotation_euler.to_quaternion()
            v.shading.type='MATERIAL'; v.overlay.show_extras=False
S['Delivery']='Editable .blend + studio hero, elevated cockpit and transparent PNGs. All source textures packed.'
# Verify hull shell closure, packed images, valid object coordinates, and scene scale.
hull=bpy.data.objects['HULL | Seamless camouflaged molded shell']
bm=bmesh.new(); bm.from_mesh(hull.data)
report={
    'blend':str(OUT/'mini_fishing_boat.blend'),
    'reference':'hidden-marsh-transp-background.webp',
    'scene_objects':len(S.objects),
    'hull_vertices':len(hull.data.vertices),
    'hull_faces':len(hull.data.polygons),
    'hull_boundary_edges':sum(e.is_boundary for e in bm.edges),
    'hull_nonmanifold_edges':sum(not e.is_manifold for e in bm.edges),
    'non_finite_vertices':sum(not all(math.isfinite(v) for v in p.co) for o in S.objects if o.type=='MESH' for p in o.data.vertices),
    'packed_images':[i.name for i in bpy.data.images if i.packed_file],
    'dimensions_metres':list(hull.dimensions),
    'note':'Proportions and details reconstructed from one image; unshown details are interpreted.'
}
bm.free()
assert report['hull_boundary_edges']==0, report
assert report['hull_nonmanifold_edges']==0, report
assert report['non_finite_vertices']==0, report
doc=bpy.data.texts.get('ABOUT | Mini fishing boat') or bpy.data.texts.new('ABOUT | Mini fishing boat')
doc.clear(); doc.write('MINI FISHING BOAT\n\nBuilt through Blender MCP from the supplied photograph.\n\nThe model uses metric units. Approximate hull length is 3.3 m; dimensions are visually estimated.\n\nSelect MINI BOAT | Move whole assembly to transform the boat. Six numbered collections separate the molded shell, upholstered chairs, seat frames, controls, fittings and lettering. Cameras, lights, and the ground are in STUDIO.\n\nThe camouflage hull is procedural. Reed upholstery and the original photograph are packed. There are no external texture dependencies.\n\nThe shape of the hidden stern, underside and controls is interpreted from the visible reference. This is a visual model rather than a manufacturing or engineering design.\n')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'mini_fishing_boat.blend'))
(OUT/'working'/'validation.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report,indent=2))
