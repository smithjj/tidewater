"""Spotted eagle ray, built through the connected Blender MCP session.
Reference: Pacific-Spotted-Eagle-Ray-009.jpg. Units are metres.
"""
import bpy, math, random, os, json
from mathutils import Vector
from math import sin, cos, pi, exp, sqrt

ROOT = r'C:\Users\smith\OneDrive\Documents\codex-projects\assets\eagle-ray'
os.makedirs(os.path.join(ROOT, 'working'), exist_ok=True)
if bpy.data.filepath and not bpy.data.filepath.endswith('spotted_eagle_ray.blend'):
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'working','previous_session.blend'), copy=True)
scene = bpy.data.scenes.new('SPOTTED EAGLE RAY | photographic study')
bpy.context.window.scene = scene
for sc in list(bpy.data.scenes):
    if sc != scene: bpy.data.scenes.remove(sc)
for ob in list(bpy.data.objects):
    if not ob.users_scene: bpy.data.objects.remove(ob, do_unlink=True)
bpy.data.orphans_purge(do_recursive=True)

def collection(name):
    c=bpy.data.collections.new(name); scene.collection.children.link(c); return c
animal=collection('01 | EAGLE RAY • model')
detail=collection('02 | Anatomy • eyes, gills, spiracles')
stage=collection('03 | Photographic studio')
cutters=collection('04 | Hidden anatomical cutters')
reference=collection('05 | Reference • hidden in renders')
root=bpy.data.objects.new('EAGLE RAY | master transform',None); animal.objects.link(root)
root.empty_display_type='PLAIN_AXES'; root.empty_display_size=.45
root['reference']='Pacific-Spotted-Eagle-Ray-009.jpg'
root['notes']='Photographic reconstruction. +Y is forward, +Z dorsal. Body spots are procedural and travel with the UVs.'
root['wingspan_m']=4.64

def link_obj(ob,col,parent=True):
    for c in list(ob.users_collection): c.objects.unlink(ob)
    col.objects.link(ob)
    if parent: ob.parent=root
    return ob
def mesh(name,v,f,mat,col=animal,sub=1,uvmode='XY'):
    me=bpy.data.meshes.new(name); me.from_pydata(v,[],f); me.update()
    ob=bpy.data.objects.new(name,me); col.objects.link(ob); ob.parent=root
    if mat: me.materials.append(mat)
    for p in me.polygons:p.use_smooth=True
    uv=me.uv_layers.new(name='Skin coordinates')
    for lp in me.loops:
        co=me.vertices[lp.vertex_index].co
        uv.data[lp.index].uv=(co.x/5+.5,(co.y if uvmode=='XY' else co.z)/5+.5)
    if sub:
        m=ob.modifiers.new('Silken surface | subdivision','SUBSURF');m.levels=sub;m.render_levels=sub
    return ob
def material(name,color,rough=.35):
    m=bpy.data.materials.new(name);m.diffuse_color=(*color,1);m.use_nodes=True
    p=m.node_tree.nodes.get('Principled BSDF');p.inputs['Base Color'].default_value=(*color,1);p.inputs['Roughness'].default_value=rough
    return m
ivory=material('Ventral skin | warm porcelain',(0.64,.665,.59),.39)
dark=material('Spiracle and gill interior | deep umber',(0.007,.008,.006),.43)
rim=material('Spiracle lips | charcoal olive',(0.022,.031,.025),.33)
lip=material('Soft mouth folds | pale grey',(0.34,.38,.33),.4)
eye_mat=material('Eyes | glossy obsidian',(0.003,.005,.004),.13)
iris=material('Iris | smoky olive bronze',(.052,.064,.038),.22)
keratin=material('Tail barbs | dark keratin',(.039,.046,.035),.31)

skin=material('DORSAL SKIN | irregular ivory spots • procedural',(.025,.036,.029),.32)
n=skin.node_tree.nodes; n.clear(); l=skin.node_tree.links
def node(typ,name,x,y):
    a=n.new(typ);a.label=name;a.location=(x,y);return a
def mathn(op,a=None,b=None,x=0,y=0):
    q=node('ShaderNodeMath',op,x,y);q.operation=op
    for i,v in enumerate((a,b)):
        if v is None:continue
        if isinstance(v,(int,float)):q.inputs[i].default_value=v
        else:l.new(v,q.inputs[i])
    return q.outputs[0]
out=node('ShaderNodeOutputMaterial','Skin output',1200,150)
p=node('ShaderNodeBsdfPrincipled','Soft wet skin',940,150)
p.inputs['Roughness'].default_value=.33;p.inputs['IOR'].default_value=1.39
p.inputs['Specular IOR Level'].default_value=.23
p.inputs['Subsurface Weight'].default_value=.035
p.inputs['Subsurface Radius'].default_value=(.09,.045,.025)
p.inputs['Coat Weight'].default_value=.025;p.inputs['Coat Roughness'].default_value=.3
l.new(p.outputs[0],out.inputs['Surface'])
uv=node('ShaderNodeUVMap','Rest-space skin UV',-1200,400);uv.uv_map='Skin coordinates'
vm=node('ShaderNodeVectorMath','Physical scale',-1020,400);vm.operation='MULTIPLY';vm.inputs[1].default_value=(5,5,5);l.new(uv.outputs['UV'],vm.inputs[0])
noise=node('ShaderNodeTexNoise','Small organic spot distortion',-1050,140);noise.inputs['Scale'].default_value=55;noise.inputs['Detail'].default_value=2.5;l.new(vm.outputs[0],noise.inputs['Vector'])
warp=node('ShaderNodeVectorMath','Tiny distortion',-820,180);warp.operation='SCALE';warp.inputs[3].default_value=.004;l.new(noise.outputs['Color'],warp.inputs[0])
add=node('ShaderNodeVectorMath','Warped surface',-640,420);add.operation='ADD';l.new(vm.outputs[0],add.inputs[0]);l.new(warp.outputs[0],add.inputs[1])
vor=node('ShaderNodeTexVoronoi','Individual irregular spots',-460,430);vor.voronoi_dimensions='2D';vor.distance='EUCLIDEAN';vor.inputs['Scale'].default_value=8.1;vor.inputs['Randomness'].default_value=.63;l.new(add.outputs[0],vor.inputs['Vector'])
sep=node('ShaderNodeSeparateColor','Per-spot size variation',-450,110);l.new(vor.outputs['Color'],sep.inputs[0])
radius=mathn('MULTIPLY_ADD',sep.outputs[0],.08,-220,80)
# third socket is the minimum radius.
radius.node.inputs[2].default_value=.165
distance=mathn('SUBTRACT',vor.outputs['Distance'],radius,-170,360)
ramp=node('ShaderNodeValToRGB','Soft pigment boundaries',30,430)
ramp.color_ramp.elements[0].position=0;ramp.color_ramp.elements[0].color=(.64,.68,.60,1)
ramp.color_ramp.elements[1].position=.028;ramp.color_ramp.elements[1].color=(.009,.014,.011,1)
l.new(distance,ramp.inputs[0])
mottle=node('ShaderNodeTexNoise','Low contrast skin mottling',-400,-180);mottle.inputs['Scale'].default_value=14;mottle.inputs['Detail'].default_value=3;l.new(vm.outputs[0],mottle.inputs['Vector'])
mix=node('ShaderNodeMixRGB','Subtle pigment variation',280,380);mix.blend_type='MULTIPLY';mix.inputs[0].default_value=.13;l.new(ramp.outputs[0],mix.inputs[1]);l.new(mottle.outputs['Fac'],mix.inputs[2])
attr=node('ShaderNodeAttribute','Dorsal / ventral boundary',40,-20);attr.attribute_name='Dorsal'
mix2=node('ShaderNodeMixRGB','Ivory belly with natural edge',560,290);mix2.inputs[1].default_value=(.64,.665,.59,1);l.new(attr.outputs['Fac'],mix2.inputs[0]);l.new(mix.outputs[0],mix2.inputs[2]);l.new(mix2.outputs[0],p.inputs['Base Color'])
uvsep=node('ShaderNodeSeparateXYZ','Snout pigment control',-400,-600);l.new(uv.outputs['UV'],uvsep.inputs[0])
fade=node('ShaderNodeMapRange','Dark projecting bill',-150,-650);fade.interpolation_type='SMOOTHERSTEP';fade.inputs['From Min'].default_value=.726;fade.inputs['From Max'].default_value=.774;l.new(uvsep.outputs['Y'],fade.inputs['Value'])
bill=node('ShaderNodeMixRGB','Unspotted tip of snout',360,110);l.new(fade.outputs['Result'],bill.inputs[0]);l.new(mix.outputs[0],bill.inputs[1]);bill.inputs[2].default_value=(.012,.018,.014,1);l.new(bill.outputs[0],mix2.inputs[2])
micro=node('ShaderNodeTexNoise','Extremely fine smooth skin grain',-170,-360);micro.inputs['Scale'].default_value=780;micro.inputs['Detail'].default_value=2;l.new(vm.outputs[0],micro.inputs['Vector'])
bump=node('ShaderNodeBump','Microscopic texture, no thorns',550,-150);bump.inputs['Strength'].default_value=.12;bump.inputs['Distance'].default_value=.00038;l.new(micro.outputs['Fac'],bump.inputs['Height']);l.new(bump.outputs['Normal'],p.inputs['Normal'])
rough=mathn('MULTIPLY_ADD',mottle.outputs['Fac'],.12,300,-190);rough.node.inputs[2].default_value=.40;l.new(rough,p.inputs['Roughness'])

def dorsal_attr(ob,values=None):
    a=ob.data.attributes.new('Dorsal','FLOAT','POINT')
    for i,v in enumerate(a.data):v.value=values[i] if values is not None else 1.0
def smooth_table(x,points):
    for i in range(len(points)-1):
        x0,v0=points[i];x1,v1=points[i+1]
        if x<=x1:
            t=max(0,(x-x0)/(x1-x0))
            # Cubic Hermite with slopes of neighboring points.
            m0=(v1-points[max(0,i-1)][1])/(x1-points[max(0,i-1)][0])
            m1=(points[min(len(points)-1,i+2)][1]-v0)/(points[min(len(points)-1,i+2)][0]-x0)
            return (2*t**3-3*t*t+1)*v0+(t**3-2*t*t+t)*(x1-x0)*m0+(-2*t**3+3*t*t)*v1+(t**3-t*t)*(x1-x0)*m1
    return points[-1][1]
FORE=[(0,1.50),(.15,1.49),(.28,1.40),(.34,1.20),(.42,.96),(.55,.78),(.85,.62),(1.2,.40),(1.65,.055),(2,-.24),(2.24,-.46),(2.32,-.56)]
REAR=[(0,-1.10),(.23,-1.07),(.42,-.91),(.70,-.68),(1.03,-.65),(1.38,-.76),(1.72,-.88),(2,-.83),(2.24,-.66),(2.32,-.56)]
def bounds(x):return smooth_table(abs(x),REAR),smooth_table(abs(x),FORE)
def surface(x,y,upper=True):
    a=abs(x)/2.32;rear,fore=bounds(x);t=max(0,min(1,(y-rear)/max(.0001,fore-rear)))
    section=sqrt(max(0,1-(2*t-1)**2))
    wing=.048*(1-a)**1.25+.006
    core=.197*exp(-(abs(x)/.54)**2.6)*exp(-((y-.15)/1.2)**4)
    head=.06*exp(-((abs(x)-.28)/.15)**2-((y-.93)/.26)**2)
    height=(wing+core)*section
    lift=(.68 if x<0 else .08)*a**3
    ripple=.005*sin(a*pi*2.3+y*3)*a*a*section
    mid=.025+.014*y+lift+ripple-.055*exp(-((y-1.47)/.22)**2)*exp(-(x/.35)**4)
    return mid+(height+head*section if upper else -height*.61)

NX=241;NT=144;verts=[];faces=[];dorsal=[]
for ix in range(NX):
    x=-2.32+4.64*(ix+1)/(NX+1);rear,fore=bounds(x)
    for k in range(NT):
        th=2*pi*k/NT;y=rear+(fore-rear)*(.5+.5*cos(th));up=sin(th)>=0
        z=surface(x,y,up);verts.append((x,y,z))
        # Dark margins are thin and irregular; the belly stays cream.
        d=max(0,min(1,(sin(th)-.035)/.13));d=d*d*(3-2*d);dorsal.append(d)
for i in range(NX-1):
    for k in range(NT):
        a=i*NT+k;b=i*NT+(k+1)%NT;faces.append((a,b,b+NT,a+NT))
for side,ix in [(-1,0),(1,NX-1)]:
    tip=len(verts);verts.append((side*2.32,-.56,.025+.014*(-.56)+(.68 if side<0 else .08)));dorsal.append(.45)
    for k in range(NT):
        a=ix*NT+k;b=ix*NT+(k+1)%NT;faces.append((tip,b,a) if side<0 else (tip,a,b))
body=mesh('Ray | continuous sculpted disc and duckbill',verts,faces,skin,sub=1);dorsal_attr(body,dorsal)
# Recalculate once to guarantee a fully outward closed skin.
bpy.context.view_layer.objects.active=body;body.select_set(True)
bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT');bpy.ops.mesh.normals_make_consistent(inside=False);bpy.ops.object.mode_set(mode='OBJECT');body.select_set(False)
body.data.materials.append(dark)
body['construction']='Closed quad-based disc with continuous upper and lower surfaces; planar rest UVs; editable wing shape keys.'
body.shape_key_add(name='Basis')
for name,amount in [('Wings | upward stroke',.85),('Wings | downward stroke',-.95)]:
    key=body.shape_key_add(name=name,from_mix=False);key.value=0
    for v in key.data:
        x,y,z=v.co;w=max(0,(abs(x)-.52)/1.8);v.co.z+=amount*w*w*(.8+.2*cos(y*1.9));v.co.x*=1-.08*w*w

def sphere(name,loc,scale,mat,col=detail,segments=48):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=segments,ring_count=24,location=loc)
    ob=bpy.context.object;ob.name=name;ob.scale=scale;link_obj(ob,col)
    ob.data.materials.append(mat)
    for p in ob.data.polygons:p.use_smooth=True
    return ob
def tube(name,points,radii,mat,col=detail,sides=12,sub=1):
    vs=[];fs=[]
    for i,pt in enumerate(points):
        pt=Vector(pt);t=Vector(points[min(i+1,len(points)-1)])-Vector(points[max(0,i-1)])
        t.normalize();ref=Vector((0,0,1)) if abs(t.z)<.9 else Vector((1,0,0));u=t.cross(ref).normalized();v=t.cross(u).normalized()
        for j in range(sides):vs.append(pt+radii[i]*(cos(2*pi*j/sides)*u+sin(2*pi*j/sides)*v))
    for i in range(len(points)-1):
        for j in range(sides):a=i*sides+j;b=i*sides+(j+1)%sides;fs.append((a,b,b+sides,a+sides))
    fs.append(tuple(reversed(range(sides))));fs.append(tuple((len(points)-1)*sides+j for j in range(sides)))
    return mesh(name,vs,fs,mat,col,sub)

for s,label in [(-1,'L'),(1,'R')]:
    # Modest laterally-facing eyes embedded in the head, not stalked spheres.
    x=s*.366;y=1.025;z=surface(x,y,True)
    socket=sphere(label+' | raised orbital tissue',(x,y,z-.023),(.052,.067,.026),rim)
    e=sphere(label+' | eye globe',(x+s*.008,y+.008,z-.002),(.027,.032,.016),eye_mat)
    ir=sphere(label+' | subtle iris',(x+s*.010,y+.010,z+.009),(.014,.017,.006),iris)
    pupil=sphere(label+' | pupil',(x+s*.013,y+.013,z+.013),(.007,.010,.004),eye_mat)
    # True dorsal recesses, just behind the eyes.
    sx=s*.382;sy=.765;sz=surface(sx,sy,True)
    cutter=sphere(label+' | spiracle subtraction',(sx,sy,sz+.029),(.052,.125,.089),dark,cutters)
    cutter.hide_render=True;cutter.hide_set(True);cutter.display_type='WIRE'
    mod=body.modifiers.new(label+' | recessed spiracle','BOOLEAN');mod.operation='DIFFERENCE';mod.solver='EXACT';mod.object=cutter
    sphere(label+' | spiracle shadow',(sx,sy,sz-.049),(.039,.101,.019),dark)
    # A low asymmetric crescent of skin surrounds the aperture.
    pts=[]
    for k in range(49):
        th=2*pi*k/48;xx=sx+.049*cos(th);yy=sy+.117*sin(th)
        pts.append((xx,yy,surface(xx,yy,True)+.005))
    tube(label+' | spiracle rim',pts,[.0045]*49,rim,sides=10)
    # Five paired ventral gill slits with pale fleshy lower rims.
    for k in range(5):
        gy=.52-k*.151;gx=s*(.29+.018*k);pts=[];lippts=[]
        for j in range(13):
            t=j/12;xx=gx+s*(t-.5)*(.175-.012*k);yy=gy+.048*sin(pi*t)
            zz=surface(xx,yy,False)-.003
            pts.append((xx,yy,zz));lippts.append((xx,yy-.014,zz-.002))
        rr=[.0015+.011*sin(pi*j/12)**.5 for j in range(13)]
        tube(label+f' | gill slit {k+1}',pts,rr,dark,sides=10)
        tube(label+f' | gill fold {k+1}',lippts,[r*.72 for r in rr],ivory,sides=8)

# Transverse mouth underneath the duckbill, with separate fleshy lower lip.
pts=[];lower=[]
for i in range(33):
    x=-.223+.446*i/32;y=1.135-.061*(1-(x/.223)**2);z=surface(x,y,False)-.010
    pts.append((x,y,z));lower.append((x,y-.025,z-.002))
tube('Mouth | recessed transverse opening',pts,[.002+.012*sin(pi*i/32)**.6 for i in range(33)],dark)
tube('Mouth | softly rolled lower lip',lower,[.002+.014*sin(pi*i/32)**.6 for i in range(33)],lip)
for s in [-1,1]:
    x=s*.16;y=1.25;z=surface(x,y,False)-.008
    sphere('Ventral nostril',(x,y,z),(.035,.014,.007),dark)
    # Small sensory pores concentrated under the snout.
    for k in range(16):
        xx=s*(.07+.025*(k%5));yy=1.32-.039*(k//5);zz=surface(xx,yy,False)-.004
        sphere('Rostral sensory pore',(xx,yy,zz),(.0035,.0035,.0015),lip,segments=12)

# Narrow, rounded pelvic fins behind the main disc.
for s,label in [(-1,'L'),(1,'R')]:
    vv=[];ff=[];dd=[];nr=22;nc=36
    for i in range(nr):
        t=i/(nr-1);cx=s*(.15+.23*sin(t*pi*.72));yy=-.84-.50*t
        half=.13*sin(pi*(.06+.92*t))
        for j in range(nc):
            th=2*pi*j/nc;xx=cx+s*half*cos(th);z=.025+.035*sin(th)*sin(pi*t)-.035*t
            vv.append((xx,yy,z));dd.append(1 if sin(th)>.15 else 0)
    for i in range(nr-1):
        for j in range(nc):a=i*nc+j;b=i*nc+(j+1)%nc;ff.append((a,b,b+nc,a+nc))
    ff.append(tuple(reversed(range(nc))));ff.append(tuple((nr-1)*nc+j for j in range(nc)))
    ob=mesh(label+' | rounded pelvic fin',vv,ff,skin);dorsal_attr(ob,dd)

# One long tapering whip tail; no caudal fin.
points=[];radii=[]
for i in range(151):
    t=i/150;points.append((.13*sin(t*pi*1.65)*t-.40*t*t,-.97-4.80*t,.02+.08*sin(t*pi)-.10*t*t))
    radii.append(.061*(1-t)**2.6+.0018*(1-t)+.00035)
tail=tube('Tail | continuous five-metre tapering whip',points,radii,rim,animal,sides=16,sub=1)
tail['note']='Tapers continuously to a fine tip; gently curved swimming posture.'
# Short pale tail base, naturally meeting the white underside.
tube('Tail | pale basal sheath',points[:11],[r*1.015 for r in radii[:11]],ivory,animal,sides=20)

# Small falcate dorsal fin, rooted above the pelvic fins.
vv=[];ff=[];nr=30;nc=24
for i in range(nr):
    t=i/(nr-1);yy=-1.035-.35*t;zz=.048+.32*sin(pi*t)**.85
    width=.035*(1-t)+.001
    for k in range(nc):
        th=2*pi*k/nc;vv.append((width*cos(th),yy, .038+(zz-.038)*(.5+.5*sin(th))))
for i in range(nr-1):
    for j in range(nc):a=i*nc+j;b=i*nc+(j+1)%nc;ff.append((a,b,b+nc,a+nc))
ff.extend([tuple(reversed(range(nc))),tuple((nr-1)*nc+j for j in range(nc))])
fin=mesh('Dorsal fin | small swept fin at tail base',vv,ff,skin,sub=2,uvmode='YZ');dorsal_attr(fin)
# Paired flattened barbs with visible backward-facing serrations.
for off in [-.018,.018]:
    pts=[(off,-1.27-.39*t,.094+.028*sin(pi*t)) for t in [i/20 for i in range(21)]]
    barb=tube('Tail | serrated caudal barb',pts,[.009*(1-i/20)+.0004 for i in range(21)],keratin,sides=8)
    barb.scale.z=.45
    for k in range(10):
        yy=-1.32-k*.026
        for s in [-1,1]:
            mesh('Barb | lateral tooth',[(off+s*.006,yy,.047),(off+s*.014,yy+.018,.047),(off+s*.005,yy+.013,.05)],[(0,1,2)],keratin,detail,0)

# Reference image is packed into the blend but hidden in all renders.
im=bpy.data.images.load(os.path.join(ROOT,'Pacific-Spotted-Eagle-Ray-009.jpg'),check_existing=True);im.pack()
ref=bpy.data.objects.new('REFERENCE | supplied spotted eagle ray photograph',None);reference.objects.link(ref);ref.empty_display_type='IMAGE';ref.data=im;ref.empty_display_size=4;ref.location=(0,4,0);ref.hide_render=True;ref.hide_set(True)

# Quiet photographic stage, organized separately for easy hiding.
ground=material('Studio | deep ocean blue',(.034,.066,.073),.74)
bpy.ops.mesh.primitive_plane_add(size=200,location=(0,0,-.66));plane=bpy.context.object;plane.name='Backdrop | ocean slate';link_obj(plane,stage,False);plane.data.materials.append(ground)
world=bpy.data.worlds.new('Cool neutral studio environment');world.use_nodes=True;world.node_tree.nodes['Background'].inputs[0].default_value=(.18,.25,.27,1);world.node_tree.nodes['Background'].inputs[1].default_value=.35;scene.world=world
def area(name,loc,power,color,size,target):
    d=bpy.data.lights.new(name,'AREA');d.energy=power;d.color=color;d.shape='DISK';d.size=size
    o=bpy.data.objects.new(name,d);stage.objects.link(o);o.location=loc;o.rotation_euler=(Vector(target)-o.location).to_track_quat('-Z','Y').to_euler()
area('Key | large soft daylight',(1.5,4.5,7),1050,(.84,.93,1),5,(0,0,0))
area('Rim | cool long reflection',(-4,-2,5),750,(.67,.78,1),4,(0,-.2,0))
area('Fill | warm frontal detail',(4,4,2.3),450,(1,.89,.72),4,(0,.4,0))
area('Tail separation',(-1,-5,3),350,(.62,.86,1),3,(0,-3,0))
def camera(name,loc,target,lens=52):
    d=bpy.data.cameras.new(name);o=bpy.data.objects.new(name,d);stage.objects.link(o);o.location=loc;o.rotation_euler=(Vector(target)-o.location).to_track_quat('-Z','Y').to_euler();d.lens=lens;d.clip_end=300;return o
hero=camera('CAMERA | three-quarter portrait',(7.5,8,4.3),(0,-1.4,0),56)
top=camera('CAMERA | dorsal markings',(0,-2.04,11),(0,-2.04,0),48);top.data.type='ORTHO';top.data.ortho_scale=8.1
close=camera('CAMERA | facial anatomy',(3.6,4.5,2.5),(0,.78,.06),70)
under=camera('CAMERA | ventral anatomy',(3.3,5,-4.6),(0,.25,0),49)
scene.camera=hero
scene.render.engine='CYCLES';scene.cycles.samples=80;scene.cycles.use_denoising=True
prefs=bpy.context.preferences.addons['cycles'].preferences
try:
    prefs.compute_device_type='OPTIX';prefs.get_devices()
    for d in prefs.devices:d.use=d.type=='OPTIX'
    scene.cycles.device='GPU'
except Exception:pass
scene.render.resolution_x=1600;scene.render.resolution_y=1200;scene.render.resolution_percentage=100
scene.render.image_settings.file_format='PNG';scene.render.image_settings.color_mode='RGBA';scene.render.film_transparent=False
scene.view_settings.view_transform='AgX';scene.view_settings.look='AgX - Medium High Contrast'
scene.render.filepath=os.path.join(ROOT,'spotted_eagle_ray_hero.png')
scene.unit_settings.system='METRIC'
scene['asset_notes']='Ray collection is independent of studio. UV-driven procedural spots, manifold body, two wing shape keys. Reference retained packed and hidden.'
bpy.ops.object.select_all(action='DESELECT');body.select_set(True);bpy.context.view_layer.objects.active=body
for screen in bpy.data.screens:
    for a in screen.areas:
        if a.type=='VIEW_3D':
            a.spaces.active.region_3d.view_perspective='CAMERA';a.spaces.active.shading.type='MATERIAL'
text=bpy.data.texts.new('ABOUT | Spotted eagle ray')
text.write('Spotted eagle ray reconstructed from the supplied Pacific-Spotted-Eagle-Ray-009.jpg.\n\nMAIN ASSET: collections 01 and 02. Move EAGLE RAY | master transform.\nThe continuous disc has two additive wing stroke shape keys.\nAll skin shading is procedural; the original photograph is packed as a hidden reference.\nCameras: portrait, dorsal, close facial, and ventral. Studio is collection 03.\nThe spiracle Boolean cutters in collection 04 must remain present, hidden from renders.\nUnits: metres. Artistic proportional reconstruction, not a scan.\nAnatomical cross-check: https://www.floridamuseum.ufl.edu/discover-fish/species-profiles/spotted-eagle-ray/\n')
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'spotted_eagle_ray.blend'))
print(json.dumps({'saved':bpy.data.filepath,'objects':len(scene.objects),'body_vertices':len(body.data.vertices),'body_faces':len(body.data.polygons)}))
