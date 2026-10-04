import bpy, math, os, json, bmesh
from math import sin,cos,pi,exp,sqrt
from mathutils import Vector
ROOT=r'C:\Users\smith\OneDrive\Documents\codex-projects\assets\eagle-ray'
s=bpy.context.scene
source=open(os.path.join(ROOT,'build_eagle_ray.py'),encoding='utf-8').read()
exec(source[source.index('def smooth_table'):source.index('NX=241')])
body=bpy.data.objects['Ray | continuous sculpted disc and duckbill']
lobe=bpy.data.objects.get('Snout | fleshy subrostral lobe')
if lobe:bpy.data.objects.remove(lobe,do_unlink=True)

def lobe_shift(x,y):
    if y<.93:return 0,0
    rear,fore=bounds(x);t=max(0,min(1,(y-rear)/(fore-rear)));sec=sqrt(max(0,1-(2*t-1)**2))
    w=exp(-(x/.285)**4)
    dz=-.075*exp(-((y-1.28)/.31)**4)*w*sec**.45
    dy=.070*exp(-((y-1.40)/.17)**2)*w*sec**.7
    return dy,dz

if not body.get('integrated_subrostral_lobe'):
    for key in body.data.shape_keys.key_blocks:
        for i,v in enumerate(key.data):
            if i<241*144 and i%144>72:
                x,y,z=v.co;dy,dz=lobe_shift(x,y);v.co.y+=dy;v.co.z+=dz
    for o in bpy.data.collections['02 | Anatomy • eyes, gills, spiracles'].objects:
        if o.name.startswith('Mouth |'):
            for v in o.data.vertices:
                dy,dz=lobe_shift(v.co.x,v.co.y);v.co.y+=dy;v.co.z+=dz
        elif o.name.startswith(('Ventral nostril','Rostral sensory pore')):
            dy,dz=lobe_shift(o.location.x,o.location.y);o.location.y+=dy;o.location.z+=dz
    body['integrated_subrostral_lobe']=True

# Store final render settings; all texture dependencies are embedded.
s.camera=bpy.data.objects['CAMERA | three-quarter portrait']
s.render.resolution_x=1800;s.render.resolution_y=1200;s.render.resolution_percentage=100
s.cycles.samples=112;s.render.filepath=os.path.join(ROOT,'spotted_eagle_ray_hero.png')
s.render.film_transparent=False
for o in [bpy.data.objects['Seafloor | fine pale sand'],bpy.data.objects['Water | optional subtle haze']]:o.hide_render=False
bpy.data.objects['Ventral inspection | softbox'].hide_render=True
bpy.data.collections['03 | Photographic studio'].name='03 | Presentation • seafloor, lights, cameras'
for image in bpy.data.images:
    if image.source=='FILE' and not image.packed_file:image.pack()
bpy.context.view_layer.objects.active=body
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'spotted_eagle_ray.blend'))

bm=bmesh.new();bm.from_mesh(body.data)
report={'body_vertices':len(body.data.vertices),'body_faces':len(body.data.polygons),'body_boundary_edges':sum(e.is_boundary for e in bm.edges),'body_nonmanifold_edges':sum(not e.is_manifold for e in bm.edges),'wing_shape_keys':[(k.name,k.value) for k in body.data.shape_keys.key_blocks],'scene_objects':len(s.objects),'materials':len(bpy.data.materials),'external_images':[im.filepath for im in bpy.data.images if im.source=='FILE' and not im.packed_file]}
bm.free()
with open(os.path.join(ROOT,'working','asset_validation.json'),'w') as f:json.dump(report,f,indent=2)
print(json.dumps(report))
