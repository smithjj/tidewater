"""Render a compact, seamless full swim cycle from the saved animated blend."""
import bpy, os, time, json, sys, traceback

ROOT=r'C:\Users\smith\OneDrive\Documents\codex-projects\assets\eagle-ray'
OUT=os.path.join(ROOT,'working','gif_frames')
os.makedirs(OUT,exist_ok=True)
s=bpy.context.scene
s.render.engine='BLENDER_EEVEE'
s.eevee.taa_render_samples=24
s.eevee.volumetric_samples=16
s.eevee.use_volumetric_shadows=False
s.eevee.use_shadows=True
s.render.resolution_x=720;s.render.resolution_y=480;s.render.resolution_percentage=100
s.render.image_settings.file_format='PNG';s.render.image_settings.color_mode='RGB'
s.render.film_transparent=False
s.camera=bpy.data.objects['CAMERA | three-quarter portrait']
s.frame_start=1;s.frame_end=72
s.use_preview_range=False
count=36
started=time.time()
for i in range(count):
    frame=1+2*i
    path=os.path.join(OUT,'ray_%04d.png'%(i+1))
    s.frame_set(frame);bpy.context.view_layer.update()
    s.render.filepath=path
    bpy.ops.render.render(write_still=True)
    print(json.dumps({'rendered':i+1,'total':count,'blender_frame':frame,'seconds':round(time.time()-started,1)}),flush=True)
