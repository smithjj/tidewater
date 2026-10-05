# Converts a vendor's GLB (public/models/characters/<name>.glb, Rocketbox, MIT) into what Unity imports: <name>.fbx (the 80-bone skin and the seven clips, one take
# per clip) and its textures as PNG: <name>_<material>_color / _normal, and _mask, the HDRP mask map made from the glTF ORM texture
# (R metallic, G occlusion, B 0, A smoothness = 1 - roughness). Blender 5.2, headless:
#   blender -b --python unity/tools/characters-to-fbx.py -- public/models/characters/joe.glb unity/Assets/Tidewater/Resources/characters joe
import bpy, sys, os
import numpy as np

glb, outdir, name = sys.argv[sys.argv.index('--') + 1:][:3]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb)

# the importer's bone-shape helper
for o in list(bpy.data.objects):
	if o.type == 'MESH' and o.name.startswith('Icosphere'): bpy.data.objects.remove(o)

def save(img, path, data=False):
	img.file_format = 'PNG'
	img.filepath_raw = path
	img.save()

for img in list(bpy.data.images):
	if not img.has_data and not img.packed_file: continue
	prefix = img.name.split('_', 1)[1]  # m110_body_color -> body_color
	mat, kind = prefix.rsplit('_', 1) if prefix != 'opacity' else ('opacity', 'color')
	if kind == 'orm':
		w, h = img.size
		orm = np.array(img.pixels[:], dtype=np.float32).reshape(-1, 4)
		mask = np.zeros_like(orm)
		mask[:, 0] = orm[:, 2]          # metallic
		mask[:, 1] = orm[:, 0]          # occlusion
		mask[:, 3] = 1.0 - orm[:, 1]    # smoothness
		out = bpy.data.images.new(name + '_' + mat + '_mask', w, h, alpha=True, is_data=True)
		out.colorspace_settings.name = 'Non-Color'
		out.pixels = mask.ravel().tolist()
		save(out, os.path.join(outdir, '%s_%s_mask.png' % (name, mat)))
	else:
		save(img, os.path.join(outdir, '%s_%s_%s.png' % (name, mat, kind)))
		if kind == 'color' and mat == 'opacity': pass

for o in bpy.data.objects: o.select_set(False)
for o in bpy.data.objects:
	if o.type in ('ARMATURE', 'MESH'): o.select_set(True)
bpy.ops.export_scene.fbx(
	filepath=os.path.join(outdir, name + '.fbx'), use_selection=True, object_types={'ARMATURE', 'MESH'}, use_mesh_modifiers=False,
	add_leaf_bones=False, bake_anim=True, bake_anim_use_all_actions=True, bake_anim_use_nla_strips=False, bake_anim_force_startend_keying=True,
	bake_anim_step=1.0, bake_anim_simplify_factor=0.0, apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y',
	path_mode='STRIP', embed_textures=False, mesh_smooth_type='FACE')
print('exported', name, [a.name for a in bpy.data.actions])
