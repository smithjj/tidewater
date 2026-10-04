import bpy, math, os, json
from mathutils import Vector
ROOT=r'C:\Users\smith\OneDrive\Documents\codex-projects\assets\eagle-ray'
s=bpy.context.scene
animal=bpy.data.collections['01 | EAGLE RAY • model']
detail=bpy.data.collections['02 | Anatomy • eyes, gills, spiracles']
stage=bpy.data.collections['03 | Photographic studio']
root=bpy.data.objects['EAGLE RAY | master transform']
body=bpy.data.objects['Ray | continuous sculpted disc and duckbill']
for k in body.data.shape_keys.key_blocks: k.value=0

# Distinct fleshy subrostral lobe, softly merging into the white lower head.
bpy.ops.mesh.primitive_uv_sphere_add(segments=64,ring_count=32,location=(0,1.287,-.065))
o=bpy.context.object;o.name='Snout | fleshy subrostral lobe';o.scale=(.271,.285,.068)
for c in list(o.users_collection):c.objects.unlink(o)
detail.objects.link(o);o.parent=root;o.data.materials.append(bpy.data.materials['Ventral skin | warm porcelain'])
for p in o.data.polygons:p.use_smooth=True

# Raised white cheek line visible beneath the lateral eyes.
for i,v in enumerate(body.data.vertices):
    x,y,z=v.co
    if .83<y<1.20 and abs(x)>.28:
        w=math.exp(-((y-1.025)/.15)**4)*max(0,min(1,(abs(x)-.28)/.09))
        side=max(0,min(1,(z-.028)/.058));side=side*side*(3-2*side)
        a=body.data.attributes['Dorsal'].data[i];a.value*=1-w+w*side

# A natural sandy bottom with restrained relief and pigment variations.
plane=bpy.data.objects['Backdrop | ocean slate'];plane.name='Seafloor | fine pale sand'
m=bpy.data.materials.new('Seafloor | natural mineral sand');m.use_nodes=True
n=m.node_tree.nodes;l=m.node_tree.links;n.clear()
out=n.new('ShaderNodeOutputMaterial');out.location=(600,100)
p=n.new('ShaderNodeBsdfPrincipled');p.location=(360,100);p.inputs['Roughness'].default_value=.87;l.new(p.outputs[0],out.inputs[0])
tex=n.new('ShaderNodeTexCoord');tex.location=(-1000,0)
coarse=n.new('ShaderNodeTexNoise');coarse.location=(-750,200);coarse.inputs['Scale'].default_value=2.8;coarse.inputs['Detail'].default_value=5;l.new(tex.outputs['Object'],coarse.inputs['Vector'])
ramp=n.new('ShaderNodeValToRGB');ramp.location=(-420,200);ramp.color_ramp.elements[0].position=.15;ramp.color_ramp.elements[0].color=(.17,.225,.185,1);ramp.color_ramp.elements[1].position=.83;ramp.color_ramp.elements[1].color=(.43,.46,.32,1);l.new(coarse.outputs['Fac'],ramp.inputs[0]);l.new(ramp.outputs[0],p.inputs['Base Color'])
fine=n.new('ShaderNodeTexNoise');fine.location=(-740,-150);fine.inputs['Scale'].default_value=420;fine.inputs['Detail'].default_value=3;l.new(tex.outputs['Object'],fine.inputs['Vector'])
b=n.new('ShaderNodeBump');b.location=(70,-160);b.inputs['Strength'].default_value=.45;b.inputs['Distance'].default_value=.012;l.new(fine.outputs['Fac'],b.inputs['Height']);l.new(b.outputs[0],p.inputs['Normal'])
plane.data.materials.clear();plane.data.materials.append(m)
plane.location.z=-.71

# A very faint water medium; surface details remain readable.
water=bpy.data.materials.new('Water | subtle blue-green scattering');water.use_nodes=True
wn=water.node_tree.nodes;wl=water.node_tree.links;wn.clear();wo=wn.new('ShaderNodeOutputMaterial');vol=wn.new('ShaderNodeVolumePrincipled')
vol.inputs['Density'].default_value=.0035;vol.inputs['Color'].default_value=(.27,.62,.67,1);vol.inputs['Anisotropy'].default_value=.3;wl.new(vol.outputs['Volume'],wo.inputs['Volume'])
bpy.ops.mesh.primitive_cube_add(size=1,location=(0,0,3))
water_ob=bpy.context.object;water_ob.name='Water | optional subtle haze';water_ob.scale=(70,70,40)
for c in list(water_ob.users_collection):c.objects.unlink(water_ob)
stage.objects.link(water_ob);water_ob.data.materials.append(water);water_ob.display_type='WIRE';water_ob.hide_set(True)

mat=next(m for m in bpy.data.materials if m.name.startswith('DORSAL SKIN'))
shader=next(n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED');shader.inputs['Specular IOR Level'].default_value=.18;shader.inputs['Coat Weight'].default_value=.015
pigment=next(n for n in mat.node_tree.nodes if n.label=='Soft pigment boundaries');pigment.color_ramp.elements[1].color=(.005,.009,.006,1)
ld=bpy.data.lights.new('Ventral inspection | softbox','AREA');ld.energy=500;ld.size=4
lo=bpy.data.objects.new('Ventral inspection | softbox',ld);stage.objects.link(lo);lo.location=(1,2,-4);lo.rotation_euler=(Vector((0,.2,0))-lo.location).to_track_quat('-Z','Y').to_euler();lo.hide_render=True

# Camera framing favors the silhouette while retaining the entire tail.
hero=bpy.data.objects['CAMERA | three-quarter portrait'];hero.location=(7.7,6.5,4.5)
hero.rotation_euler=(Vector((0,-1.6,.04))-hero.location).to_track_quat('-Z','Y').to_euler();hero.data.lens=61
top=bpy.data.objects['CAMERA | dorsal markings'];top.location=(0,-2.02,11);top.rotation_euler=(0,0,0);top.data.ortho_scale=8.3
face=bpy.data.objects['CAMERA | facial anatomy'];face.location=(2.4,3.5,1.50);face.rotation_euler=(Vector((0,.78,.05))-face.location).to_track_quat('-Z','Y').to_euler();face.data.lens=76
s.camera=hero;s.cycles.samples=96;s.cycles.use_denoising=True
s.render.resolution_x=1800;s.render.resolution_y=1200;s.render.resolution_percentage=100
s.render.filepath=os.path.join(ROOT,'spotted_eagle_ray_hero.png')
for a in bpy.context.screen.areas:
    if a.type=='VIEW_3D':
        a.spaces.active.region_3d.view_perspective='CAMERA';a.spaces.active.shading.type='MATERIAL'
bpy.ops.object.select_all(action='DESELECT');body.select_set(True);bpy.context.view_layer.objects.active=body
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'spotted_eagle_ray.blend'))
print('Finished anatomy, seafloor, water, and camera framing.')
