import bpy, os
ROOT=r'C:\Users\smith\OneDrive\Documents\codex-projects\assets\eagle-ray'
s=bpy.context.scene
views=globals().get('VIEWS',['hero','dorsal','detail','ventral'])
configs={
    'hero':('CAMERA | three-quarter portrait',1800,1200),
    'dorsal':('CAMERA | dorsal markings',1200,1600),
    'detail':('CAMERA | facial anatomy',1600,1200),
    'ventral':('CAMERA | ventral anatomy',1600,1200),
}
floor=bpy.data.objects['Seafloor | fine pale sand']
water=bpy.data.objects['Water | optional subtle haze']
fill=bpy.data.objects['Ventral inspection | softbox']
s.cycles.samples=globals().get('SAMPLES',112);s.render.resolution_percentage=100
for view in views:
    name,rx,ry=configs[view];s.camera=bpy.data.objects[name]
    s.render.resolution_x=rx;s.render.resolution_y=ry
    floor.hide_render=view=='ventral';water.hide_render=view=='ventral';fill.hide_render=view!='ventral'
    s.render.filepath=os.path.join(ROOT,'spotted_eagle_ray_'+view+'.png')
    bpy.ops.render.render(write_still=True)
    print('Rendered '+view)
floor.hide_render=False;water.hide_render=False;fill.hide_render=True
s.camera=bpy.data.objects['CAMERA | three-quarter portrait'];s.render.resolution_x=1800;s.render.resolution_y=1200
s.render.filepath=os.path.join(ROOT,'spotted_eagle_ray_hero.png')
s.cycles.samples=112
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'spotted_eagle_ray.blend'))
