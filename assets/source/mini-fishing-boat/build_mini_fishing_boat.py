"""Reference-built mini fishing boat. Run inside Blender through Blender MCP.
Units are metres; proportions are estimated from the single supplied photograph.
All materials and the reed camouflage texture are packed into the final blend.
"""
import bpy, math, random, json, os
from pathlib import Path
from mathutils import Vector, Matrix
import numpy as np

OUT = Path(__file__).resolve().parent
random.seed(418)
S = bpy.data.scenes.new('MINI FISHING BOAT | Marsh camouflage')
bpy.context.window.scene = S
for sc in list(bpy.data.scenes):
    if sc != S: bpy.data.scenes.remove(sc)
for obj in list(bpy.data.objects): bpy.data.objects.remove(obj, do_unlink=True)
for col in list(bpy.data.collections): bpy.data.collections.remove(col)
bpy.data.orphans_purge(do_recursive=True)
S.unit_settings.system = 'METRIC'
S['Reference'] = 'hidden-marsh-transp-background.webp — supplied by user'
S['Construction'] = 'Individually modeled hull, upholstery, seat frames, deck fittings and controls. Dimensions estimated from photograph.'
MODEL = bpy.data.collections.new('BOAT | Complete editable model')
S.collection.children.link(MODEL)
COLS = {}
for key,label in [('hull','01 | Molded hull & cockpit'),('seats','02 | Upholstered fishing chairs'),('frames','03 | Seat frames & sliding rails'),('controls','04 | Drive controls & wiring'),('fittings','05 | Rod holders, handles & fittings'),('labels','06 | Hull graphics')]:
    c=bpy.data.collections.new(label); MODEL.children.link(c); COLS[key]=c
ENV=bpy.data.collections.new('STUDIO | Cameras & lighting'); S.collection.children.link(ENV)
REF=bpy.data.collections.new('REFERENCE | Hidden in renders'); S.collection.children.link(REF)
ROOT=bpy.data.objects.new('MINI BOAT | Move whole assembly',None); MODEL.objects.link(ROOT)
ROOT.empty_display_size=.35
ROOT['Length overall (estimated)']=3.30
ROOT['Beam (estimated)']=1.38
ACTIVE=COLS['hull']

def link(obj):
    for c in list(obj.users_collection): c.objects.unlink(obj)
    ACTIVE.objects.link(obj)
    if ACTIVE not in (ENV,REF): obj.parent=ROOT
    return obj

def mesh(name,verts,faces,material,smooth=True):
    d=bpy.data.meshes.new(name); d.from_pydata(verts,[],faces); d.update()
    o=bpy.data.objects.new(name,d); ACTIVE.objects.link(o)
    if ACTIVE not in (ENV,REF): o.parent=ROOT
    if material: d.materials.append(material)
    for f in d.polygons: f.use_smooth=smooth
    return o

def bevel(o,width=.015,segments=3):
    m=o.modifiers.new('Soft manufactured edges','BEVEL'); m.width=width; m.segments=segments
    return o

def cube(name,loc,scale,material,rounding=.01,rotation=None):
    bpy.ops.mesh.primitive_cube_add(size=1,location=loc)
    o=link(bpy.context.object); o.name=name; o.dimensions=scale
    bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
    if rotation: o.rotation_euler=rotation
    if material: o.data.materials.append(material)
    if rounding: bevel(o,rounding,4)
    return o

def cyl(name,a,b,r,material,vertices=32):
    a,b=Vector(a),Vector(b)
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices,radius=r,depth=(b-a).length,location=(a+b)/2)
    o=link(bpy.context.object); o.name=name; o.rotation_euler=(b-a).to_track_quat('Z','Y').to_euler()
    o.data.materials.append(material); bevel(o,min(.003,r*.2),2)
    for p in o.data.polygons: p.use_smooth=(len(p.vertices)==4)
    return o

def tube(name,pts,r,material,cyclic=False):
    d=bpy.data.curves.new(name,'CURVE'); d.dimensions='3D'; d.resolution_u=18
    sp=d.splines.new('POLY'); sp.points.add(len(pts)-1)
    for p,co in zip(sp.points,pts): p.co=(*co,1)
    sp.use_cyclic_u=cyclic; d.bevel_depth=r; d.bevel_resolution=3
    o=bpy.data.objects.new(name,d); ACTIVE.objects.link(o); o.parent=ROOT
    d.materials.append(material); return o

def torus(name,loc,major,minor,material,normal=(0,0,1)):
    bpy.ops.mesh.primitive_torus_add(major_radius=major,minor_radius=minor,major_segments=48,minor_segments=12,location=loc)
    o=link(bpy.context.object); o.name=name; o.rotation_euler=Vector(normal).to_track_quat('Z','Y').to_euler(); o.data.materials.append(material)
    for p in o.data.polygons: p.use_smooth=True
    return o

def material(name,color,rough=.45,metal=0):
    m=bpy.data.materials.new(name); m.diffuse_color=(*color,1); m.use_nodes=True
    p=m.node_tree.nodes.get('Principled BSDF'); p.inputs['Base Color'].default_value=(*color,1)
    p.inputs['Roughness'].default_value=rough; p.inputs['Metallic'].default_value=metal
    return m

def ramp(nd,stops):
    n=nd.new('ShaderNodeValToRGB'); cr=n.color_ramp
    cr.elements[0].position=stops[0][0]; cr.elements[0].color=(*stops[0][1],1)
    cr.elements[1].position=stops[-1][0]; cr.elements[1].color=(*stops[-1][1],1)
    for t,c in stops[1:-1]: cr.elements.new(t).color=(*c,1)
    return n

black=material('Powder coated charcoal aluminum',(.025,.032,.031),.32,.45)
rubber=material('Rubber | satin graphite',(.018,.023,.022),.69)
dark=material('Deep recess shadow',(.009,.014,.012),.82)
silver=material('Stainless steel | brushed',(.48,.53,.51),.26,.82)
thread=material('Upholstery seam | dull straw',(.27,.245,.12),.85)
olive=material('Molded fittings | olive green',(.105,.151,.119),.54)
ivory=material('Printed pale gray',(.62,.7,.66),.48)
blue=material('Decal | midnight blue',(.035,.075,.18),.4)
lime=material('Decal | yellow green',(.38,.54,.13),.48)

hullmat=material('HULL | Marsh green, fog gray and sand camouflage',(.21,.29,.24),.47)
nd=hullmat.node_tree.nodes; lk=hullmat.node_tree.links; p=nd.get('Principled BSDF')
tex=nd.new('ShaderNodeTexCoord'); tex.object=ROOT
n=nd.new('ShaderNodeTexNoise'); n.inputs['Scale'].default_value=.67; n.inputs['Detail'].default_value=5.4; n.inputs['Roughness'].default_value=.79
lk.new(tex.outputs['Object'],n.inputs['Vector'])
cr=ramp(nd,[(.22,(.055,.092,.07)),(.40,(.077,.135,.10)),(.44,(.115,.19,.15)),(.48,(.32,.39,.35)),(.515,(.37,.43,.37)),(.54,(.36,.31,.225)),(.64,(.36,.31,.225)),(.70,(.13,.19,.14)),(.80,(.08,.12,.095))])
cr.color_ramp.interpolation='EASE'; lk.new(n.outputs['Fac'],cr.inputs[0]); lk.new(cr.outputs[0],p.inputs['Base Color'])
fine=nd.new('ShaderNodeTexNoise'); fine.inputs['Scale'].default_value=460; fine.inputs['Detail'].default_value=2
lk.new(tex.outputs['Object'],fine.inputs['Vector'])
bu=nd.new('ShaderNodeBump'); bu.inputs['Strength'].default_value=.24; bu.inputs['Distance'].default_value=.0010
lk.new(fine.outputs['Fac'],bu.inputs['Height']); lk.new(bu.outputs[0],p.inputs['Normal'])
p.inputs['Coat Weight'].default_value=.12; p.inputs['Coat Roughness'].default_value=.47
# Fine molded color flecks break up the sprayed, feathered camouflage boundaries.
speckle=nd.new('ShaderNodeTexNoise'); speckle.inputs['Scale'].default_value=94; speckle.inputs['Detail'].default_value=2.8; speckle.inputs['Roughness'].default_value=.8
lk.new(tex.outputs['Object'],speckle.inputs['Vector'])
sp=ramp(nd,[(.2,(.52,.56,.51)),(.8,(1,1,1))]); lk.new(speckle.outputs['Fac'],sp.inputs[0])
mix=nd.new('ShaderNodeMixRGB'); mix.blend_type='MULTIPLY'; mix.inputs[0].default_value=.30
lk.new(cr.outputs[0],mix.inputs[1]); lk.new(sp.outputs[0],mix.inputs[2]); lk.new(mix.outputs[0],p.inputs['Base Color'])

def catmull(points,steps=8):
    result=[]
    for i in range(len(points)):
        a,b,c,d=[Vector(points[j%len(points)]) for j in (i-1,i,i+1,i+2)]
        for k in range(steps):
            t=k/steps
            result.append(.5*((2*b)+(-a+c)*t+(2*a-5*b+4*c-d)*t*t+(-a+3*b-3*c+d)*t*t*t))
    return result

# Broad, rounded bow at negative X; flatter transom at positive X.
outline=catmull([(-1.66,0),(-1.63,-.25),(-1.48,-.49),(-1.21,-.625),(-.72,-.683),(.30,-.690),(1.16,-.65),(1.48,-.575),(1.60,-.45),(1.62,0),(1.60,.45),(1.48,.575),(1.16,.65),(.30,.690),(-.72,.683),(-1.21,.625),(-1.48,.49),(-1.63,.25)],8)
def sheer(x): return .606+.077*(abs(x)/1.66)**2

# Continuous closed rotomolded shell, rolled edge, inset cockpit, curved bilges.
profiles=[(.86,.70,'abs',.10),(.882,.73,'abs',.072),(.93,.79,'abs',.115),(.98,.935,'top',-.12),(1.0,1.0,'top',-.032),(1.001,1.003,'top',-.008),(.995,.99,'top',.013),(.97,.943,'top',.025),(.94,.87,'top',.017),(.932,.854,'top',-.005),(.923,.83,'top',-.065),(.864,.749,'abs',.310),(.832,.704,'abs',.267),(.806,.665,'abs',.253)]
verts=[]
for profile_index,(sx,sy,mode,h) in enumerate(profiles):
    for q in outline:
        bow_taper=(.13,.12,.085,.012)[profile_index] if profile_index<4 and q.x<0 else 0
        verts.append((q.x*(sx-bow_taper),q.y*sy,h if mode=='abs' else sheer(q.x)+h))
N=len(outline); faces=[]
for k in range(len(profiles)-1):
    for i in range(N): faces.append((k*N+i,k*N+(i+1)%N,(k+1)*N+(i+1)%N,(k+1)*N+i))
faces.append(tuple(reversed(range(N))))
faces.append(tuple((len(profiles)-1)*N+i for i in range(N)))
hull=mesh('HULL | Seamless camouflaged molded shell',verts,faces,hullmat)
hull.data.polygons[-1].use_smooth=False; hull.data.polygons[-2].use_smooth=False
hull['Construction']='Continuous watertight mesh including hull bottom, topsides, rolled gunwales, inside walls and cockpit sole.'

# Lower twin runners and raised central tunnel communicate the small pontoon hull.
for side in [-1,1]:
    runner=cube('Port keel runner' if side==1 else 'Starboard keel runner',(.12,side*.355,.093),(2.22,.16,.12),hullmat,.055)
    # Long mounting tracks follow the inside edge of each gunwale.
    ACTIVE=COLS['frames']
    cube('Seat slide | port' if side==1 else 'Seat slide | starboard',(.07,side*.514,.602),(2.39,.053,.044),black,.009)
    cube('Rail polished top channel',(.07,side*.510,.627),(2.36,.016,.005),silver,.002)
    for x in [-1.08,-.68,-.2,.32,.82,1.2]:
        cyl('Recessed rail fastening screw',(x,side*.511,.625),(x,side*.511,.632),.009,silver,16)
        cube('Fastener drive slot',(x,side*.511,.633),(.010,.002,.0015),dark,.0004)
    ACTIVE=COLS['hull']

# Subtle raised non-slip sole, drain channels and ribs inside the open cockpit.
floormat=material('Floor tread | deep marsh green',(.098,.133,.104),.77)
cube('Cockpit sole | central non-slip inset',(-.09,0,.258),(2.40,.78,.014),floormat,.10)
for x in np.linspace(-1.13,1.03,29):
    cube('Molded anti-slip transverse rib',(float(x),0,.270),(.012,.705,.008),olive,.004)
for y in [-.43,.43]:
    tube('Cockpit drainage groove',[(-1.13,y,.269),(1.12,y,.269)],.009,hullmat)

# Make a seamless, packed reed-camouflage textile bitmap from painted grasses.
SIZE=1024
rng=np.random.default_rng(718)
xx,yy=np.meshgrid(np.arange(SIZE)/SIZE,np.arange(SIZE)/SIZE)
cloud=np.zeros((SIZE,SIZE),dtype=np.float32)
for k in range(20):
    fx,fy=rng.integers(1,16,2); phase=rng.random()*math.tau
    cloud+=np.sin(math.tau*(fx*xx+fy*yy)+phase)/(1+math.sqrt(fx*fx+fy*fy))
cloud=(cloud-cloud.min())/(cloud.max()-cloud.min())
base=np.zeros((SIZE,SIZE,4),dtype=np.float32); base[:,:,3]=1
lo=np.array([.058,.079,.045]); hi=np.array([.30,.29,.145])
base[:,:,:3]=lo+(hi-lo)*cloud[:,:,None]

def paint_segment(a,b,width,color,opacity=1):
    ax,ay=a; bx,by=b
    for dx in [-SIZE,0,SIZE]:
        for dy in [-SIZE,0,SIZE]:
            x0=max(0,int(min(ax,bx)+dx-width-1)); x1=min(SIZE,int(max(ax,bx)+dx+width+2))
            y0=max(0,int(min(ay,by)+dy-width-1)); y1=min(SIZE,int(max(ay,by)+dy+width+2))
            if x1<=x0 or y1<=y0: continue
            gx,gy=np.meshgrid(np.arange(x0,x1),np.arange(y0,y1))
            vx=bx-ax; vy=by-ay
            t=np.clip(((gx-ax-dx)*vx+(gy-ay-dy)*vy)/(vx*vx+vy*vy+1e-5),0,1)
            dist=np.sqrt((gx-ax-dx-t*vx)**2+(gy-ay-dy-t*vy)**2)
            alpha=np.clip(width+.65-dist,0,1)*opacity
            patch=base[y0:y1,x0:x1,:3]
            patch[:]=patch*(1-alpha[:,:,None])+np.array(color)*alpha[:,:,None]

for i in range(370):
    origin=rng.uniform(0,SIZE,2); angle=rng.uniform(0,math.tau)
    length=rng.uniform(55,330); bend=rng.uniform(-.8,.8); w=rng.uniform(.55,2.15)
    points=[]
    for j in range(9):
        t=j/8; theta=angle+bend*t
        points.append(origin+length*t*np.array([math.cos(theta),math.sin(theta)]))
    c=random.choice([(.47,.41,.20),(.60,.52,.28),(.34,.33,.16),(.20,.27,.11),(.69,.60,.34)])
    for j in range(8):
        paint_segment(points[j],points[j+1],w*1.8,(.035,.051,.021),.76)
        paint_segment(points[j],points[j+1],w,c,.9)
    if i%3==0:
        for j in [2,4,6]:
            p0=points[j]; theta=angle+bend*j/8+random.choice([-1,1])*.64
            endpoint=p0+rng.uniform(15,55)*np.array([math.cos(theta),math.sin(theta)])
            paint_segment(p0,endpoint,w*.65,c,.88)
grain=rng.normal(0,.013,(SIZE,SIZE,1))
base[:,:,:3]=np.clip(base[:,:,:3]+grain,0,1)
img=bpy.data.images.new('Marsh reed upholstery | packed seamless textile',width=SIZE,height=SIZE,alpha=True)
img.pixels.foreach_set(base.ravel()); img.pack()
fabric=material('CHAIRS | Woven marsh reeds camouflage',(.22,.24,.10),.88)
nd=fabric.node_tree.nodes; lk=fabric.node_tree.links; p=nd.get('Principled BSDF')
coord=nd.new('ShaderNodeTexCoord'); mapping=nd.new('ShaderNodeVectorMath'); mapping.operation='SCALE'; mapping.inputs[3].default_value=1.55
lk.new(coord.outputs['Object'],mapping.inputs[0])
it=nd.new('ShaderNodeTexImage'); it.image=img; it.projection='BOX'; it.projection_blend=.23; lk.new(mapping.outputs[0],it.inputs['Vector']); lk.new(it.outputs['Color'],p.inputs['Base Color'])
noise=nd.new('ShaderNodeTexNoise'); noise.inputs['Scale'].default_value=580; noise.inputs['Detail'].default_value=2
lk.new(coord.outputs['Object'],noise.inputs['Vector'])
bb=nd.new('ShaderNodeBump'); bb.inputs['Strength'].default_value=.25; bb.inputs['Distance'].default_value=.0006
lk.new(noise.outputs['Fac'],bb.inputs['Height']); lk.new(bb.outputs[0],p.inputs['Normal'])
p.inputs['Sheen Weight'].default_value=.018; p.inputs['Sheen Roughness'].default_value=.8
p.inputs['Specular IOR Level'].default_value=.16

def rrloop(hx,hy,r,z=0,steps=12):
    pts=[]
    for cx,cy,start in [(hx-r,hy-r,0),(-hx+r,hy-r,90),(-hx+r,-hy+r,180),(hx-r,-hy+r,270)]:
        for i in range(steps):
            a=math.radians(start+i*90/steps); pts.append((cx+r*math.cos(a),cy+r*math.sin(a),z))
    return pts

def cushion(name,loc,size,rotation=(0,0,0)):
    hx,hy,thick=size[0]/2,size[1]/2,size[2]
    vs=[]; fs=[]
    ringdata=[(.82,-thick*.47),(.96,-thick*.36),(1,0),(.965,thick*.30),(.86,thick*.47),(.52,thick*.535),(.12,thick*.54)]
    for scale,z in ringdata:
        vs.extend(rrloop(hx*scale,hy*scale,min(hx,hy)*.36*scale,z))
    n=48
    for k in range(len(ringdata)-1):
        for i in range(n): fs.append((k*n+i,k*n+(i+1)%n,(k+1)*n+(i+1)%n,(k+1)*n+i))
    fs.append(tuple(reversed(range(n)))); fs.append(tuple((len(ringdata)-1)*n+i for i in range(n)))
    o=mesh(name,vs,fs,fabric); o.location=loc; o.rotation_euler=rotation
    sub=o.modifiers.new('Padded upholstery smoothing','SUBSURF'); sub.levels=2; sub.render_levels=2
    bpy.context.view_layer.update()
    mat=o.matrix_world.copy()
    pts=[tuple(mat@Vector(v)) for v in rrloop(hx*.983,hy*.983,min(hx,hy)*.355,thick*.08,18)]
    tube(name+' | stitched perimeter piping',pts,.0026,thread,True)
    # Short, individually visible thread stitches along the outer seam.
    for j in range(0,len(pts),2):
        a=Vector(pts[j]); b=Vector(pts[(j+1)%len(pts)])
        tube(name+' | seam stitches',[a.lerp(b,.10),a.lerp(b,.68)],.00065,thread)
    return o

for seat_index,x in enumerate([-.66,.77],1):
    ACTIVE=COLS['frames']
    for xo in [-.17,.17]:
        path=[(-.508,.649),(-.478,.687),(-.325,.820),(-.260,.838),(.260,.838),(.325,.820),(.478,.687),(.508,.649)]
        vs=[]
        for j,(py,pz) in enumerate(path):
            before=Vector(path[max(0,j-1)]); after=Vector(path[min(len(path)-1,j+1)])
            tangent=(after-before).normalized(); normal=Vector((-tangent.y,tangent.x))*.007
            for dx,sign in [(-.024,-1),(.024,-1),(.024,1),(-.024,1)]:
                vs.append((x+xo+dx,py+normal.x*sign,pz+normal.y*sign))
        fs=[]
        for j in range(len(path)-1):
            for q in range(4): fs.append((j*4+q,j*4+(q+1)%4,(j+1)*4+(q+1)%4,(j+1)*4+q))
        fs.extend([(3,2,1,0),tuple((len(path)-1)*4+q for q in range(4))])
        bevel(mesh(f'Chair {seat_index} | bent flat steel arch',vs,fs,black,False),.006,3)
    for side in [-1,1]:
        y=side*.197
        for offset in [-.17,.17]:
            cube('Sliding seat carriage',(x+offset,side*.501,.641),(.10,.078,.024),black,.008)
            cyl('Carriage lock screw',(x+offset,side*.51,.646),(x+offset,side*.51,.660),.012,silver,20)
        # Backrest straps rise behind the cushion and recline gently aft.
        tube(f'Chair {seat_index} | backrest support {side}',[(x-.07,y,.804),(x+.205,y,.805),(x+.29,y,.866),(x+.335,y,1.055),(x+.40,y,1.33)],.022,black)
        cyl('Backrest hinge washer',(x+.267,y-.025,.883),(x+.267,y+.025,.883),.036,black)
        cyl('Backrest hinge pin',(x+.267,y-.03,.883),(x+.267,y+.03,.883),.012,silver)
    cube(f'Chair {seat_index} | under-seat pan',(x,0,.835),(.48,.50,.042),rubber,.08)
    cyl('Seat swivel bearing',(x,0,.798),(x,0,.830),.117,black)
    ACTIVE=COLS['seats']
    cushion(f'Chair {seat_index} | padded seat',(x-.025,0,.887),(.60,.585,.140))
    # Local top (+Z) is the upholstered front; rotate it to face the bow (-X).
    cushion(f'Chair {seat_index} | padded backrest',(x+.336,0,1.242),(.465,.548,.135),(0,math.radians(-78),0))
    # Rear cover and upholstery tabs are visible from the stern.
    for side in [-1,1]:
        cube('Backrest attachment webbing',(x+.388,side*.182,1.19),(.029,.047,.232),rubber,.008,rotation=(0,.20,0))
    ACTIVE=COLS['frames']
    tube('Seat adjustment pull handle',[(x-.23,-.11,.79),(x-.35,-.11,.79),(x-.35,.11,.79),(x-.23,.11,.79)],.011,black)

print('Hull, upholstery and seat structures complete.')

ACTIVE=COLS['fittings']
for x,y in [(-1.04,.588),(-1.04,-.588),(1.09,.579),(1.09,-.579)]:
    z=sheer(x)+.021
    bpy.ops.mesh.primitive_cylinder_add(vertices=48,radius=.037,depth=.094,location=(x,y,z-.012))
    cutter=bpy.context.object; cutter.name='Temporary cup holder cavity'
    m=hull.modifiers.new('Molded cup well','BOOLEAN'); m.operation='DIFFERENCE'; m.solver='EXACT'; m.object=cutter
    bpy.context.view_layer.objects.active=hull
    bpy.ops.object.modifier_apply(modifier=m.name)
    bpy.data.objects.remove(cutter,do_unlink=True)
    # Hollow tapered cup insert, visible down to its recessed floor.
    n=48; vs=[]
    for r,h in [(.036,z),(.034,z-.010),(.029,z-.055),(.008,z-.057)]:
        for i in range(n):
            a=math.tau*i/n; vs.append((x+r*math.cos(a),y+r*math.sin(a),h))
    fs=[]
    for k in range(3):
        for i in range(n): fs.append((k*n+i,k*n+(i+1)%n,(k+1)*n+(i+1)%n,(k+1)*n+i))
    fs.append(tuple(3*n+i for i in range(n)))
    mesh('Recessed molded cup holder',vs,fs,olive)
    torus('Cup holder rolled rim',(x,y,z),.038,.004,hullmat)
    cyl('Cup well drain',(x,y,z-.059),(x,y,z-.056),.004,dark,16)

# Bow and stern carry grips mounted through molded sockets.
for x,z in [(-1.53,.689),(1.54,.693)]:
    for y in [-.175,.175]:
        cube('Carry handle anchor',(x,y,z),(.086,.061,.025),olive,.012)
        cyl('Carry handle countersunk bolt',(x,y,z+.01),(x,y,z+.019),.010,silver,20)
    tube('Recessed end carry handle',[(x,-.17,z+.019),(x,-.15,z+.043),(x,.15,z+.043),(x,.17,z+.019)],.020,rubber)
    for y in np.linspace(-.104,.104,12):
        torus('Handle grip rib',(x,float(y),z+.043),.020,.0015,black,(0,1,0))

# Two adjustable, open U-cradle rod holders reproduce the distinctive silhouettes.
def rod_holder(name,x,y,z,ang):
    def tr(v):
        a=ang
        return (x+math.cos(a)*v[0]-math.sin(a)*v[1],y+math.sin(a)*v[0]+math.cos(a)*v[1],z+v[2])
    cube(name+' | deck mounting plate',(x,y,z),(.092,.072,.016),black,.007)
    for ox in [-.03,.03]:
        for oy in [-.022,.022]: cyl('Rod mount screw',(x+ox,y+oy,z+.008),(x+ox,y+oy,z+.014),.005,silver,16)
    cyl(name+' | swiveling stem',tr((0,0,.01)),tr((0,0,.135)),.018,black)
    cyl(name+' | pitch pivot',tr((-.003,-.033,.123)),tr((-.003,.033,.123)),.027,black)
    cyl(name+' | pivot cap',tr((-.003,-.039,.123)),tr((-.003,-.035,.123)),.013,silver)
    # Rising cradle has a central saddle and two horns with rubber caps.
    tube(name+' | cradle saddle',[tr((-.08,-.045,.224)),tr((-.027,-.035,.170)),tr((.045,-.030,.155)),tr((.084,-.044,.191))],.013,black)
    tube(name+' | other cradle cheek',[tr((-.08,.045,.224)),tr((-.027,.035,.170)),tr((.045,.030,.155)),tr((.084,.044,.191))],.013,black)
    cyl(name+' | saddle crossbar',tr((.026,-.035,.160)),tr((.026,.035,.160)),.014,rubber)
    for sign in [-1,1]:
        cyl(name+' | padded horn tip',tr((-.080,sign*.045,.209)),tr((-.083,sign*.045,.231)),.017,rubber)
    cyl(name+' | adjustment star knob',tr((-.003,.038,.123)),tr((-.003,.060,.123)),.024,rubber,8)
rod_holder('Bow rod holder',-1.16,.55,.667,math.radians(12))
rod_holder('Aft rod holder',.68,-.608,.637,math.radians(-22))

# Drain fitting and small tiedown eyes inset into the molded side.
for ysign in [-1,1]:
    cyl('Hull drain plug',(.62,ysign*.655,.450),(.62,ysign*.672,.450),.021,black)
    cyl('Drain plug center',(.62,ysign*.670,.450),(.62,ysign*.676,.450),.010,rubber,8)
    for x in [-1.24,1.35]:
        y=ysign*(.600 if x<0 else .573); z=sheer(x)+.04
        cube('Tie down eye base',(x,y,z-.019),(.064,.040,.012),black,.008)
        torus('Stainless tie down eye',(x,y,z+.002),.021,.004,silver,(0,1,0))

ACTIVE=COLS['controls']
# Compact ribbed control/power enclosure beneath the aft chair.
housing=cube('Aft drive control | beveled power enclosure',(1.035,0,.504),(.375,.410,.404),black,.064)
cube('Drive enclosure | top lid',(1.040,0,.711),(.327,.358,.036),rubber,.025)
for yy in [-.153,-.085,0,.085,.153]:
    cube('Power enclosure reinforcement rib',(1.035,yy,.702),(.40,.020,.040),black,.009)
for xx in [.88,1.19]:
    for yy in [-.144,.144]: cyl('Enclosure captive fastener',(xx,yy,.725),(xx,yy,.731),.007,silver,16)
cube('Power enclosure release tab',(.828,0,.537),(.029,.104,.063),rubber,.008)
for side in [-1,1]:
    # Heel plates and angled toe paddles for hands-free control.
    pedal=cube('Foot steering paddle',(.57,side*.265,.354),(.265,.146,.037),rubber,.025,(0,math.radians(-18),0))
    cube('Foot pedal hinge',(.696,side*.265,.319),(.05,.17,.038),black,.007)
    for j in range(6):
        x=.48+j*.027
        cube('Pedal traction rib',(x,side*.265,.36+(x-.57)*.32),(.012,.125,.013),black,.004,(0,math.radians(-18),0))
    tube('Pedal control cable',[(.70,side*.29,.310),(.77,side*.33,.282),(1.16,side*.33,.282),(1.22,side*.245,.403)],.006,rubber)

# Small bow-mounted motor/control head and its coiled supply cable.
cube('Bow control | molded bracket',(-1.32,-.17,.497),(.11,.22,.045),black,.010,(0,.15,0))
cyl('Bow control | tilt pivot',(-1.32,-.265,.536),(-1.32,-.075,.536),.022,silver)
head=cube('Bow control | compact black head',(-1.355,-.17,.605),(.125,.186,.090),black,.022,(0,math.radians(-28),0))
cube('Bow control | inset top face',(-1.367,-.17,.650),(.078,.135,.009),rubber,.007,(0,math.radians(-28),0))
ledmat=material('Control indicator | green',(.25,.51,.10),.25)
cyl('Control status lamp',(-1.38,-.208,.649),(-1.38,-.208,.655),.004,ledmat,16)
tube('Bow power lead',[(-1.32,-.096,.580),(-1.28,.14,.40),(-1.12,.25,.299),(-.81,.37,.295),(-.38,.416,.335),(-.24,.471,.557)],.007,rubber)
for i in range(5):
    x=-1.27+i*.024
    torus('Bow lead strain relief',(x,.130+i*.009,.414-i*.014),.011,.003,rubber,(1,.3,-.4))
# Wiring runs tucked under the aluminum track.
tube('Starboard protected wiring run',[(-.20,-.494,.574),(.17,-.50,.554),(.59,-.502,.556),(1.21,-.459,.597),(1.30,-.25,.573),(1.223,-.10,.489)],.008,rubber)

# Decals are actual editable text, arranged in the plane of the hull side.
ACTIVE=COLS['labels']
def lettering(name,text,loc,size,mat,side=-1):
    d=bpy.data.curves.new(name,'FONT'); d.body=text; d.align_x='CENTER'; d.align_y='CENTER'; d.size=size; d.extrude=.0002; d.bevel_depth=.00005
    o=bpy.data.objects.new(name,d); ACTIVE.objects.link(o); o.parent=ROOT; o.location=loc
    # Text local Y is vertical, its front points away from the boat.
    o.rotation_euler=(math.pi/2,0,0) if side==-1 else (math.pi/2,0,math.pi)
    d.materials.append(mat); return o
for side in [-1,1]:
    # Badges sit high on the aft shoulder, as in the supplied image.
    yy=side*.673
    lettering('Hull model decal','TWIN TROLLER',(.925,yy,.546),.036,blue,side)
    lettering('Model suffix','X10',(1.193,side*.665,.550),.032,lime,side)
    # Round emblem: pale field with a navy center and twin green bars.
    cyl('Round maker emblem',(.636,side*.670,.540),(.636,side*.674,.540),.042,ivory,48)
    cyl('Navy emblem field',(.636,side*.674,.540),(.636,side*.675,.540),.032,blue,48)
    for x in [.625,.644]: cube('Twin stripe emblem',(x,side*.677,.540),(.006,.0015,.046),lime,.001)
    lettering('Bow specification decal','10-ft. MINI FISHING BOAT',(-.58,side*.681,.553),.019,ivory,side)

# Store the source image as a hidden, packed reference plane.
refimg=bpy.data.images.load(str(OUT/'hidden-marsh-transp-background.webp'),check_existing=True); refimg.pack()
refobj=bpy.data.objects.new('REFERENCE | Original supplied photograph',None); REF.objects.link(refobj)
refobj.empty_display_type='IMAGE'; refobj.data=refimg; refobj.empty_display_size=3.3
refobj.location=(0,3,1); refobj.rotation_euler=(math.pi/2,0,0); refobj.hide_render=True; refobj.hide_viewport=True

ACTIVE=ENV
studio=material('Studio backdrop | warm mist',(.19,.22,.205),.83)
cube('Ground | soft studio shadow',(0,0,-.022),(200,200,.10),studio,0)
world=bpy.data.worlds.new('Studio ambient'); world.use_nodes=True
world.node_tree.nodes.get('Background').inputs[0].default_value=(.30,.36,.34,1)
world.node_tree.nodes.get('Background').inputs[1].default_value=.42
S.world=world
def area(name,loc,power,size,color,target=(0,0,.6)):
    d=bpy.data.lights.new(name,'AREA'); d.energy=power; d.shape='DISK'; d.size=size; d.color=color
    o=bpy.data.objects.new(name,d); ENV.objects.link(o); o.location=loc; o.rotation_euler=(Vector(target)-o.location).to_track_quat('-Z','Y').to_euler()
area('Key | broad warm softbox',(-3.4,-4.2,6.3),850,4.6,(1,.91,.78))
area('Fill | cool overhead',(-.1,3.5,4.4),700,3.8,(.76,.88,1))
area('Rim | long aft reflection',(4,1.2,4.7),1000,3.1,(1,.95,.84))
area('Front lift',(-4,-1,2),110,2.0,(.86,.95,1))

def camera(name,loc,target,lens=55,ortho=None):
    d=bpy.data.cameras.new(name); o=bpy.data.objects.new(name,d); ENV.objects.link(o)
    o.location=loc; o.rotation_euler=(Vector(target)-o.location).to_track_quat('-Z','Y').to_euler(); d.lens=lens
    if ortho: d.type='ORTHO'; d.ortho_scale=ortho
    return o
hero=camera('CAMERA | Reference three-quarter',(-4.6,-6.8,3.50),(0,0,.72),ortho=4.30)
camera('CAMERA | Stern & cockpit',(4.3,-5.1,3.8),(0,0,.68),ortho=4.35)
camera('CAMERA | Elevated bow',(-4.7,-4.3,5.6),(0,0,.68),ortho=4.45)
camera('CAMERA | Port profile',(0,6,2.18),(0,0,.74),ortho=3.9)
S.camera=hero
S.render.engine='CYCLES'
S.cycles.samples=48; S.cycles.use_denoising=True
S.render.resolution_x=1600; S.render.resolution_y=1100; S.render.resolution_percentage=65
S.render.image_settings.file_format='PNG'; S.render.image_settings.color_mode='RGBA'
S.view_settings.view_transform='AgX'
S.render.film_transparent=False
S.render.filepath=str(OUT/'working'/'first_preview.png')
S.render.image_settings.color_depth='8'
for screen in bpy.data.screens:
    for ar in screen.areas:
        if ar.type=='VIEW_3D':
            ar.spaces.active.clip_end=200
            ar.spaces.active.region_3d.view_distance=4.6
            ar.spaces.active.region_3d.view_location=Vector((0,0,.7))
            ar.spaces.active.region_3d.view_rotation=hero.rotation_euler.to_quaternion()
            ar.spaces.active.shading.type='MATERIAL'
            ar.spaces.active.overlay.show_extras=False
bpy.ops.object.select_all(action='DESELECT'); ROOT.select_set(True); bpy.context.view_layer.objects.active=ROOT
S['Dimensions note']='Approximate 3.3 m long x 1.38 m beam. Photo reconstruction, not a measured manufacturing model.'
S['Materials note']='Procedural three-color molded hull camouflage; packed seamless reed-pattern upholstery; all editable.'
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'mini_fishing_boat.blend'))
print(json.dumps({'status':'built','objects':len(S.objects),'meshes':len([o for o in S.objects if o.type=='MESH']),'file':bpy.data.filepath}))
