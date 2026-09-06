"""Run with Blender --background --python this_file -- --project M:/GoldCamp.
Creates editable source .blend, palette PNGs and actual FBX models for Unity.
No concept image generation; all review images are renders of these meshes.
"""
import bpy, os, sys, argparse, json, importlib, math
from mathutils import Matrix
from pathlib import Path
here=Path(__file__).resolve().parent
sys.path.insert(0,str(here))
import model_utils as u
from miner import build_miner
parser=argparse.ArgumentParser();parser.add_argument('--project',default=str(here.parent.parent));parser.add_argument('--only',default='')
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
project=Path(args.project).resolve()
assert (project/'GoldCamp.Unity/ProjectSettings/ProjectVersion.txt').exists()
target=project/'GoldCamp.Unity/Assets/GoldCamp/Demo/Art/Sample01'
models=target/'Models';textures=target/'Textures'
models.mkdir(parents=True,exist_ok=True);textures.mkdir(parents=True,exist_ok=True)
bpy.ops.object.select_all(action='SELECT');bpy.ops.object.delete(use_global=False)
bpy.context.scene.unit_settings.system='METRIC';bpy.context.scene.unit_settings.scale_length=1
for i in range(4):u.atlas(str(textures/f'Palette{i}.png'),i)
u.init_material(str(textures/'Palette0.png'))
assets={}
if not args.only or args.only=='miner':assets['BeanMiner']=build_miner(u)
if not args.only or args.only=='props':
    from props import build_props
    assets.update(build_props(u))
if not args.only or args.only=='corner':
    from corner import build_corner
    assets.update(build_corner(u))
def descendants(root):return [root]+[o for o in root.children_recursive]

# Editable unmerged originals, one named collection per asset. Active view is miner.
for key,root in assets.items():
    coll=bpy.data.collections.new(key+' / editable parts');bpy.context.scene.collection.children.link(coll)
    for obj in descendants(root):
        for old in list(obj.users_collection):old.objects.unlink(obj)
        coll.objects.link(obj)
    if key!='BeanMiner':coll.hide_viewport=True;coll.hide_render=True
source=here/('GoldCampSample01.blend' if not args.only else f'GoldCampSample01-{args.only}.blend')
bpy.ops.wm.save_as_mainfile(filepath=str(source))

records=[]
for name,root in assets.items():
    for coll in bpy.data.collections:coll.hide_viewport=False;coll.hide_render=False
    originals=descendants(root);clones={};names={o:o.name for o in originals}
    for obj in originals:obj.name='SOURCE_'+names[obj]
    for obj in originals:
        clone=obj.copy()
        if obj.data:clone.data=obj.data.copy()
        bpy.context.scene.collection.objects.link(clone);clones[obj]=clone
    for obj,clone in clones.items():
        clone.parent=clones.get(obj.parent)
        clone.matrix_local=obj.matrix_local.copy()
        clone.name=names[obj];clone.hide_set(False);clone.hide_render=False
    # Merge meshes attached to the same rigid pivot. Joint empties stay intact.
    buckets={}
    for clone in clones.values():
        if clone.type=='MESH':buckets.setdefault(clone.parent,[]).append(clone)
    for parent,parts in buckets.items():
        if len(parts)<2:continue
        bpy.ops.object.select_all(action='DESELECT')
        for p in parts:p.select_set(True)
        bpy.context.view_layer.objects.active=parts[0];bpy.ops.object.join()
        parts[0].name=(parent.name if parent else name)+'_Mesh'
    export_root=clones[root]
    chosen=descendants(export_root)
    # Explicit change of basis at EVERY rigid pivot. Blender's automatic bake
    # leaves alternating basis corrections in nested EMPTY hierarchies.
    # Source (x,-z,y) -> FBX (x,y,-z); Unity's handedness conversion restores Z.
    basis=Matrix(((1,0,0,0),(0,0,1,0),(0,1,0,0),(0,0,0,1)))
    locals_before={o:o.matrix_local.copy() for o in chosen}
    for obj in chosen:
        obj.matrix_local=basis @ locals_before[obj] @ basis
        if obj.type=='MESH':
            obj.data.transform(basis);obj.data.flip_normals();obj.data.update()
    bpy.ops.object.select_all(action='DESELECT')
    for o in chosen:o.select_set(True)
    bpy.context.view_layer.objects.active=export_root
    bpy.ops.export_scene.fbx(filepath=str(models/f'{name}.fbx'),use_selection=True,
        object_types={'MESH','EMPTY'},global_scale=1,apply_unit_scale=True,apply_scale_options='FBX_SCALE_UNITS',
        axis_forward='-Z',axis_up='Y',use_space_transform=False,bake_space_transform=False,bake_anim=False,
        mesh_smooth_type='FACE',use_mesh_modifiers=True,add_leaf_bones=False,path_mode='STRIP',embed_textures=False)
    meshes=[o for o in chosen if o.type=='MESH']
    for o in meshes:o.data.calc_loop_triangles()
    records.append(dict(asset=name,renderers=len(meshes),triangles=sum(len(o.data.loop_triangles) for o in meshes),
        vertices=sum(len(o.data.vertices) for o in meshes),file=f'{name}.fbx',editable_source=source.name))
    bpy.ops.object.select_all(action='DESELECT')
    for o in chosen:o.select_set(True)
    bpy.ops.object.delete(use_global=False)
    for obj in originals:obj.name=names[obj]
with open(here/('asset-manifest.json' if not args.only else f'asset-manifest-{args.only}.json'),'w',encoding='utf8') as f:
    json.dump(dict(blender=bpy.app.version_string,coordinate_contract='Unity metres, +Y up, +Z forward; Blender source converts Y/Z axes',
        reference='User supplied 01 character turnaround; 02/04 camp props; 05 auxiliary accessories',assets=records),f,indent=2)
print('GOLDCAMP_ART_EXPORT_OK '+str(source),flush=True)
