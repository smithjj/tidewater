import bpy,os
ROOT=r"C:\Users\smith\OneDrive\Documents\codex-projects\assets\eagle-ray"
body=bpy.data.objects['Ray | continuous sculpted disc and duckbill']
for o in list(bpy.data.objects):
    if any(t in o.name for t in ['raised orbital tissue','spiracle rim','soft spiracle collar']):bpy.data.objects.remove(o,do_unlink=True)
for f in body.data.polygons:f.material_index=0
m=next(m for m in bpy.data.materials if m.name.startswith('DORSAL SKIN'));n=m.node_tree.nodes;l=m.node_tree.links
p=next(q for q in n if q.type=='BSDF_PRINCIPLED');uv=next(q for q in n if q.type=='UVMAP')
if not n.get('Natural spiracle pigment'):
    original=p.inputs['Base Color'].links[0].from_socket;distances=[]
    for sign in [-1,1]:
        sub=n.new('ShaderNodeVectorMath');sub.operation='SUBTRACT';l.new(uv.outputs[0],sub.inputs[0]);sub.inputs[1].default_value=(.5+sign*.382/5,.5+.765/5,0)
        scale=n.new('ShaderNodeVectorMath');scale.operation='MULTIPLY';l.new(sub.outputs[0],scale.inputs[0]);scale.inputs[1].default_value=(5/.076,5/.150,0)
        length=n.new('ShaderNodeVectorMath');length.operation='LENGTH';l.new(scale.outputs[0],length.inputs[0]);distances.append(length.outputs['Value'])
    minimum=n.new('ShaderNodeMath');minimum.operation='MINIMUM';l.new(distances[0],minimum.inputs[0]);l.new(distances[1],minimum.inputs[1])
    fade=n.new('ShaderNodeMapRange');fade.interpolation_type='SMOOTHERSTEP';fade.inputs['From Min'].default_value=.84;fade.inputs['From Max'].default_value=1.3;l.new(minimum.outputs[0],fade.inputs['Value'])
    mix=n.new('ShaderNodeMixRGB');mix.name='Natural spiracle pigment';mix.label='Soft pigment transition around breathing apertures';mix.inputs[1].default_value=(.005,.009,.006,1);l.new(original,mix.inputs[2]);l.new(fade.outputs['Result'],mix.inputs[0]);l.new(mix.outputs[0],p.inputs['Base Color'])
em=bpy.data.materials['Eyes | glossy obsidian'].node_tree.nodes.get('Principled BSDF');em.inputs['Roughness'].default_value=.22;em.inputs['Specular IOR Level'].default_value=.28
cavity=bpy.data.materials['Spiracle and gill interior | deep umber'].node_tree.nodes.get('Principled BSDF');cavity.inputs['Specular IOR Level'].default_value=.16;cavity.inputs['Roughness'].default_value=.49
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'spotted_eagle_ray.blend'))
print('Natural spiracle pigment and eyes polished')
