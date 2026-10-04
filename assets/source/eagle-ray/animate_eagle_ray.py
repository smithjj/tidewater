"""Add a repeating, adjustable swim to the spotted eagle ray blend."""
import bpy, math, os, json

ROOT=r'C:\Users\smith\OneDrive\Documents\codex-projects\assets\eagle-ray'
OUT=os.path.join(ROOT,'spotted_eagle_ray.blend')
scene=bpy.context.scene
rig=bpy.data.objects['EAGLE RAY | master transform']
body=bpy.data.objects['Ray | continuous sculpted disc and duckbill']
tail=bpy.data.objects['Tail | continuous five-metre tapering whip']
keys=body.data.shape_keys

# Preserve an easy restore point before adding animation.
if os.path.exists(OUT):
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'working','spotted_eagle_ray_static.blend'),copy=True)

# The dorsal shoulder on the far (left) side has a slight natural lift in the
# reference pose. Reduce that static asymmetry so the wings stroke from a
# relaxed glide pose. Move every shape key together to preserve the key deltas.
for block in keys.key_blocks:
    for v in block.data:
        x=v.co.x
        if x < -.52:
            a=min(1.0,max(0.0,(abs(x)-.52)/1.8))
            v.co.z-=.56*a**3
body.data.update()

# Create smooth left/right lateral tail-sweep poses around a neutral tail.
if not tail.data.shape_keys:
    base=tail.shape_key_add(name='Basis',from_mix=False)
    left=tail.shape_key_add(name='Tail | sweep left',from_mix=False)
    right=tail.shape_key_add(name='Tail | sweep right',from_mix=False)
    for i,v in enumerate(base.data):
        y=v.co.y;t=min(1.0,max(0.0,(-y-.97)/4.80))
        displacement=.30*(.24*t+.76*t*t)*math.sin(t*math.pi/2)
        left.data[i].co.x=v.co.x+displacement
        right.data[i].co.x=v.co.x-displacement
else:
    left=tail.data.shape_keys.key_blocks['Tail | sweep left']
    right=tail.data.shape_keys.key_blocks['Tail | sweep right']

for k in keys.key_blocks:
    k.value=0.0
for k in tail.data.shape_keys.key_blocks:
    k.value=0.0

rig['Swim | strength']=.78
rig['Swim | tail sway']=.82
rig['Swim | period (frames)']=72
rig.id_properties_ui('Swim | strength').update(min=0.0,max=1.0,soft_min=0.0,soft_max=1.0,description='Overall pectoral fin stroke amount; 0 pauses in a glide.')
rig.id_properties_ui('Swim | tail sway').update(min=0.0,max=1.0,soft_min=0.0,soft_max=1.0,description='Side-to-side sweep of the flexible tail.')
rig.id_properties_ui('Swim | period (frames)').update(min=36,max=120,soft_min=48,soft_max=96,description='Frames per wing-stroke cycle at the scene frame rate.')

def add_driver(idblock,path,expr,controls):
    fc=idblock.driver_add(path)
    d=fc.driver;d.type='SCRIPTED'
    for name,data_path in controls:
        var=d.variables.new();var.name=name;var.type='SINGLE_PROP'
        target=var.targets[0];target.id=rig;target.data_path='["'+data_path+'"]'
    d.expression=expr
    return fc

phase='2*pi*(frame-1)/period'
controls=[('strength','Swim | strength'),('period','Swim | period (frames)')]
add_driver(keys,'key_blocks["Wings | upward stroke"].value',f'strength * max(0.0,sin({phase}))**1.25',controls)
add_driver(keys,'key_blocks["Wings | downward stroke"].value',f'strength * max(0.0,-sin({phase}))**1.25',controls)
tail_controls=[('strength','Swim | tail sway'),('period','Swim | period (frames)')]
add_driver(tail.data.shape_keys,'key_blocks["Tail | sweep left"].value',f'strength * max(0.0,sin({phase}))**1.15',tail_controls)
add_driver(tail.data.shape_keys,'key_blocks["Tail | sweep right"].value',f'strength * max(0.0,-sin({phase}))**1.15',tail_controls)

# Small body drift and pitch follow the fin beat with a soft lag.
def root_driver(data_path,index,expr):
    fc=rig.driver_add(data_path,index);d=fc.driver;d.type='SCRIPTED'
    var=d.variables.new();var.name='period';var.type='SINGLE_PROP';var.targets[0].id=rig;var.targets[0].data_path='["Swim | period (frames)"]'
    d.expression=expr
root_driver('location',2,'0.026*sin(2*pi*(frame-1)/period)')
root_driver('rotation_euler',0,'0.012*sin(2*pi*(frame-1)/period-0.35)')
root_driver('rotation_euler',2,'0.016*sin(2*pi*(frame-1)/period-0.5)')

scene.render.fps=30
scene.render.fps_base=1.0
scene.frame_start=1;scene.frame_end=72
scene.use_preview_range=True;scene.frame_preview_start=1;scene.frame_preview_end=72
for marker in list(scene.timeline_markers):scene.timeline_markers.remove(marker)
for frame,name in [(1,'Glide • cycle start'),(19,'Upstroke peak'),(37,'Wing reversal'),(55,'Downstroke peak')]:scene.timeline_markers.new(name,frame=frame)
scene.frame_set(1)
scene.render.filepath=os.path.join(ROOT,'spotted_eagle_ray_hero.png')
bpy.context.view_layer.objects.active=body
bpy.ops.object.select_all(action='DESELECT');body.select_set(True)
bpy.ops.wm.save_as_mainfile(filepath=OUT)

# Sample both stroke peaks after saving to leave the file at its relaxed start.
samples=[]
for f in (1,19,37,55,73):
    scene.frame_set(f);bpy.context.view_layer.update()
    samples.append({'frame':f,'upstroke':round(keys.key_blocks['Wings | upward stroke'].value,4),'downstroke':round(keys.key_blocks['Wings | downward stroke'].value,4),'tail_left':round(left.value,4),'tail_right':round(right.value,4),'root_z':round(rig.location.z,4)})
scene.frame_set(1);bpy.context.view_layer.update()
print(json.dumps({'saved':bpy.data.filepath,'cycle_seconds':72/30,'sampled_motion':samples},indent=2))
