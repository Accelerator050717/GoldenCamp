"""GoldCamp Sample01 authoring helpers. Coordinates are Unity metres (Y up, Z front).

Source parts remain editable in .blend. Export merges rigid pieces per pivot and
uses a shared colour atlas, so hardware details do not require separate materials.
"""
import bpy, bmesh, math, os, struct, zlib
from mathutils import Vector

COLORS = {
 'skin': '#9da956', 'skin_light':'#bec887', 'belly':'#d2d88b',
 'cloth':'#34464f', 'cloth_light':'#52616a', 'cloth_dark':'#23333e',
 'hat':'#e6a72b', 'hat_light':'#ffca52', 'leather':'#985b2b',
 'leather_light':'#ba793d','leather_dark':'#533d2c', 'boot':'#493b32',
 'sole':'#705236', 'gold':'#e9af34','gold_light':'#ffe18a',
 'iron':'#42484d','iron_edge':'#939a99','wood':'#986234',
 'wood_light':'#bb8449','wood_dark':'#66452a','stone':'#77746c',
 'stone_light':'#a19b8b','stone_dark':'#524f4d', 'canvas':'#dfc897',
 'leaf':'#435d37','leaf_light':'#718449','lamp':'#fff1b5',
 'screen':'#183d36','signal':'#a3ef72','blue':'#42758a',
 'eye':'#e0e6bc','iris':'#43574b','pupil':'#17252a','white':'#fff8df',
 'seam':'#798080','dirt':'#b38c5c','rust':'#a17143','rope':'#b49559',
 'roof':'#476447','roof_light':'#628058', 'window':'#efbe60',
}
SLOTS = {name:i for i,name in enumerate(COLORS)}
ATLAS=None
def vec(p): return Vector((p[0],-p[2],p[1]))
def attach(obj,parent,pos=(0,0,0)):
    obj.parent=parent; obj.location=vec(pos); return obj
def group(name,parent=None,pos=(0,0,0)):
    obj=bpy.data.objects.new(name,None); bpy.context.collection.objects.link(obj)
    return attach(obj,parent,pos)
def paint(obj,color):
    obj.data.materials.clear(); obj.data.materials.append(ATLAS)
    # Unity consumes UV0. Primitive operators already create a UVMap; adding a
    # second layer would leave the atlas reading the original spherical UVs.
    uv=obj.data.uv_layers[0] if obj.data.uv_layers else obj.data.uv_layers.new(name='PaletteUV')
    uv.name='PaletteUV'
    for face in obj.data.polygons:
        key=color[face.index % len(color)] if isinstance(color,(tuple,list)) else color
        slot=SLOTS[key]; value=((slot%8+.5)/8,(slot//8+.5)/8)
        for i in face.loop_indices: uv.data[i].uv=value
    return obj
def mesh(name,verts,faces,color,parent=None,smooth=False):
    data=bpy.data.meshes.new(name); data.from_pydata([vec(v) for v in verts],[],faces);data.update()
    # Consistent outward normals on closed pieces; do not depend on caller winding.
    bm=bmesh.new(); bm.from_mesh(data)
    # Disconnected colour patches are open surfaces with intentional winding.
    # Recalculating those independently randomly turns rock facets inward.
    if all(edge.is_manifold for edge in bm.edges): bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bm.to_mesh(data);bm.free()
    obj=bpy.data.objects.new(name,data);bpy.context.collection.objects.link(obj);obj.parent=parent
    for face in data.polygons:face.use_smooth=smooth
    return paint(obj,color)
def _finish(obj,name,pos,parent,color):
    obj.name=name; bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
    attach(obj,parent,pos);return paint(obj,color)
def box(name,pos,size,color,parent=None,bevel=.02):
    bpy.ops.mesh.primitive_cube_add(size=1)
    obj=bpy.context.object;obj.scale=vec((size[0],size[1],-size[2]))
    obj=_finish(obj,name,pos,parent,color)
    if bevel:
        mod=obj.modifiers.new('Soft worn edges','BEVEL');mod.width=min(bevel,min(size)*.25);mod.segments=2
        bpy.context.view_layer.objects.active=obj;bpy.ops.object.modifier_apply(modifier=mod.name);paint(obj,color)
        for poly in obj.data.polygons:poly.use_smooth=False
    return obj
def ellipsoid(name,pos,size,color,parent=None,segments=20,rings=12):
    segments=min(segments,20);rings=min(rings,12)
    bpy.ops.mesh.primitive_uv_sphere_add(segments=segments,ring_count=rings,radius=.5)
    obj=bpy.context.object;obj.scale=Vector((size[0],size[2],size[1]));obj=_finish(obj,name,pos,parent,color)
    for face in obj.data.polygons:face.use_smooth=True
    return obj
def cylinder(name,pos,radius,depth,color,parent=None,axis=(0,1,0),vertices=16):
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices,radius=radius,depth=depth)
    obj=bpy.context.object;obj=_finish(obj,name,pos,parent,color)
    obj.rotation_mode='QUATERNION';obj.rotation_quaternion=Vector((0,0,1)).rotation_difference(vec(axis).normalized())
    return obj
def beam(name,a,b,width,depth,color,parent=None,bevel=.008):
    d=vec(b)-vec(a); obj=box(name,tuple((a[i]+b[i])*.5 for i in range(3)),(width,d.length,depth),color,parent,bevel)
    obj.rotation_mode='QUATERNION';obj.rotation_quaternion=Vector((0,0,1)).rotation_difference(d.normalized());return obj
def lathe(name,ys,rx,rz,color,parent=None,segments=32,start=0,end=360,smooth=True,centers=None):
    vs=[]; faces=[]; full=end-start>=359.9; count=segments if full else segments+1
    for j,y in enumerate(ys):
        for k in range(count):
            a=math.radians(start+(end-start)*k/segments)
            vs.append((math.sin(a)*rx[j],y,math.cos(a)*rz[j]+(centers[j] if centers else 0)))
    for j in range(len(ys)-1):
        for k in range(segments):
            n=(k+1)%count;faces.append((j*count+k,j*count+n,(j+1)*count+n,(j+1)*count+k))
    return mesh(name,vs,faces,color,parent,smooth)
def atlas(path,variant=0):
    colors=dict(COLORS)
    if variant==1:colors.update(hat='#b65335',hat_light='#dd7850',cloth='#704636',cloth_light='#94664c')
    if variant==2:colors.update(hat='#3c648a',hat_light='#668fb0',cloth='#3a5266',cloth_light='#647789')
    if variant==3:colors.update(hat='#57713b',hat_light='#88a553',cloth='#58634a',cloth_light='#818667')
    width=128; rows=[]
    values=list(colors.values())
    for y in range(width):
        row=bytearray([0])
        for x in range(width):
            # PNG origin top-left, UV origin bottom-left.
            i=((width-1-y)//16)*8+x//16
            h=values[i] if i<len(values) else '#777777'
            row.extend(bytes.fromhex(h[1:]));row.append(255)
        rows.append(bytes(row))
    def chunk(tag,data):return struct.pack('>I',len(data))+tag+data+struct.pack('>I',zlib.crc32(tag+data)&0xffffffff)
    os.makedirs(os.path.dirname(path),exist_ok=True)
    with open(path,'wb') as f:f.write(b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>2I5B',width,width,8,6,0,0,0))+chunk(b'IDAT',zlib.compress(b''.join(rows)))+chunk(b'IEND',b''))
def init_material(texture):
    global ATLAS
    ATLAS=bpy.data.materials.new('GoldCamp shared palette');ATLAS.use_nodes=True
    nodes=ATLAS.node_tree.nodes;p=nodes.get('Principled BSDF');p.inputs['Roughness'].default_value=.76
    image=nodes.new('ShaderNodeTexImage');image.image=bpy.data.images.load(texture);image.interpolation='Closest'
    image.image.pack()
    ATLAS.node_tree.links.new(image.outputs['Color'],p.inputs['Base Color'])
    return ATLAS
