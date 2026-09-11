"""Create an editable Blender project and CPU-render the imported Melee mesh JSON."""
import json
import math
from pathlib import Path

import bpy
from mathutils import Matrix, Vector

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'character/john-pork-mesh.json'
MODEL = json.loads(SOURCE.read_text())
RIG = json.loads((ROOT / 'importer/rig.json').read_text())
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)

C = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))

def position(value):
    return (value[0], -value[2], value[1])


mats = []
for material in MODEL['materials']:
    mat = bpy.data.materials.new(material['name'])
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get('Principled BSDF')
    bsdf.inputs['Base Color'].default_value = material['diffuse']
    bsdf.inputs['Roughness'].default_value = .72
    if material.get('texture'):
        tex = mat.node_tree.nodes.new('ShaderNodeTexImage')
        tex.image = bpy.data.images.load(str(SOURCE.parent / material['texture']))
        tex.image.pack()
        mat.node_tree.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
    mats.append(mat)

arm = bpy.data.armatures.new('Luigi original HSD skeleton')
rig_object = bpy.data.objects.new('Luigi-compatible rig', arm)
bpy.context.collection.objects.link(rig_object)
bpy.context.view_layer.objects.active = rig_object
rig_object.select_set(True)
bpy.ops.object.mode_set(mode='EDIT')
for joint in RIG['joints']:
    bone = arm.edit_bones.new('JOBJ_' + str(joint['index']))
    values = joint['worldMatrix']
    world = Matrix([values[i:i + 4] for i in range(0, 16, 4)]).transposed()
    bone.matrix = C @ world @ C.inverted()
    bone.length = .4
    if joint['parent'] >= 0:
        bone.parent = arm.edit_bones['JOBJ_' + str(joint['parent'])]
bpy.ops.object.mode_set(mode='OBJECT')
rig_object.show_in_front = True
rig_object.select_set(False)

for index, mesh in enumerate(MODEL['meshes'] + MODEL.get('previewMeshes', [])):
    data = bpy.data.meshes.new(mesh['name'])
    data.from_pydata([position(v['position']) for v in mesh['vertices']], [], mesh['triangles'])
    data.update()
    data.normals_split_custom_set_from_vertices([position(v['normal']) for v in mesh['vertices']])
    obj = bpy.data.objects.new(f'{index:02d} {mesh["name"]}', data)
    bpy.context.collection.objects.link(obj)
    data.materials.append(mats[mesh['material']])
    uv = data.uv_layers.new(name='Melee UV')
    for polygon in data.polygons:
        polygon.use_smooth = True
        for loop_index in polygon.loop_indices:
            vertex_index = data.loops[loop_index].vertex_index
            coords = mesh['vertices'][vertex_index]['uv']
            uv.data[loop_index].uv = (coords[0], 1 - coords[1])
    groups = {}
    for vertex_index, v in enumerate(mesh['vertices']):
        for joint, weight in zip(v['joints'], v['weights']):
            if weight <= 0:
                continue
            if joint not in groups:
                groups[joint] = obj.vertex_groups.new(name='JOBJ_' + str(joint))
            groups[joint].add([vertex_index], float(weight), 'REPLACE')
    modifier = obj.modifiers.new('Original Luigi skinning', 'ARMATURE')
    modifier.object = rig_object
    obj.parent = rig_object

# An uncluttered studio view, without borrowing the image generator's GPU.
bpy.ops.mesh.primitive_plane_add(size=200, location=(0, 0, -.19))
floor = bpy.context.object
floor.name = 'Preview floor'
mat = bpy.data.materials.new('Studio floor')
mat.diffuse_color = (.055, .07, .09, 1)
floor.data.materials.append(mat)

def aim(obj, target):
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat('-Z', 'Y').to_euler()

bpy.ops.object.camera_add(location=(16.5, -36, 17))
camera = bpy.context.object
aim(camera, (0, 0, 6.5))
camera.data.type = 'ORTHO'
camera.data.ortho_scale = 18.8
bpy.context.scene.camera = camera
for location, energy, size in [((-10, -12, 22), 2100, 10), ((8, -7, 14), 1000, 8), ((2, 10, 19), 1700, 7)]:
    bpy.ops.object.light_add(type='AREA', location=location)
    light = bpy.context.object
    light.data.energy = energy
    light.data.shape = 'DISK'
    light.data.size = size
    aim(light, (0, 0, 7))
scene = bpy.context.scene
scene.render.engine = 'CYCLES'
scene.cycles.device = 'CPU'
scene.cycles.samples = 32
scene.cycles.use_denoising = True
scene.world.color = (.15, .15, .15)
scene.view_settings.view_transform = 'Standard'
scene.render.resolution_x = 900
scene.render.resolution_y = 900
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = 'PNG'
scene.render.filepath = str(ROOT / 'character/john-pork-preview.png')
bpy.ops.wm.save_as_mainfile(filepath=str(ROOT / 'character/john-pork.blend'))
bpy.ops.render.render(write_still=True)
# A straight-on reference makes facial asymmetry and clothing placement easy to inspect.
camera.location = (0, -38, 10.4)
aim(camera, (0, 0, 6.4))
scene.render.filepath = str(ROOT / 'character/john-pork-front.png')
bpy.ops.render.render(write_still=True)
