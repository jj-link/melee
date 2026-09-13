"""Convert the approved prototype, never rebuild or modify it.
Sequence: importer hawking-export <root>; blender --background --python this_file;
          importer hawking-import <root>.
Only character/ is written. Native Zelda joint indices, source inverse binds and
all 206 authored parts are retained; draw calls consolidate to texture families.
"""
import json
import math
from pathlib import Path
import struct
import zlib

import bpy
import numpy as np
from mathutils import Euler, Matrix, Vector

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'character'
OUT.mkdir(exist_ok=True)
rig = json.loads((OUT / 'hawking-source-rig.json').read_text())
animation = json.loads((OUT / 'hawking-animation-input.json').read_text())
source = rig['joints']
if len(source) != 118:
    raise RuntimeError('Expected the authoritative Zelda v1.02 118-joint rig')

# Blender Z-up / negative-Y forward -> Melee Y-up / positive-Z forward.
# 10 game units per authored metre keeps the seated silhouette near Zelda height.
SCALE = 10.0
TO_GAME = Matrix(((SCALE, 0, 0, 0), (0, 0, SCALE, 0),
                  (0, -SCALE, 0, 0), (0, 0, 0, 1)))

def matrix(values):
    return Matrix([values[i:i + 4] for i in range(0, 16, 4)]).transposed()


def array(value):
    return [float(component) for row in value.transposed() for component in row]


def srgb(color):
    return [float(12.92 * v if v <= .0031308 else 1.055 * max(0, v) ** (1 / 2.4) - .055)
            for v in color[:3]] + [1.0]


# Explicit anatomical mapping in approved prototype coordinates. Duplicate pivots
# are native roll/helper joints, NOT deleted bones. Hands retain every finger ID.
pivots = {
    4: (0, .045, .652), 5: (.102, .04, .652), 6: (.102, .04, .652),
    7: (.102, -.403, .570), 8: (.102, -.476, .221), 9: (.102, -.476, .221),
    10: (.102, -.60, .173), 11: (-.102, .04, .652), 12: (-.102, .04, .652),
    13: (-.102, -.403, .570), 14: (-.102, -.476, .221), 15: (-.102, -.476, .221),
    16: (-.102, -.60, .173), 66: (0, .127, .873), 67: (0, .205, 1.085),
    68: (.08, .205, 1.10), 69: (.15, .20, 1.10), 70: (.18, .18, 1.10),
    71: (.187, .181, 1.098), 72: (.187, .181, 1.098),
    73: (.225, -.050, .850), 74: (.190, -.125, .825), 75: (.215, -.08, .84),
    76: (.091, -.219, .746), 88: (.014, .224, 1.168), 89: (.020, .220, 1.225),
    96: (-.08, .205, 1.10), 97: (-.15, .20, 1.10), 98: (-.18, .18, 1.10),
    99: (-.187, .181, 1.098), 100: (-.187, .181, 1.098),
    101: (-.225, -.050, .850), 102: (-.190, -.125, .825), 103: (-.215, -.08, .84),
    104: (-.126, -.229, .754),
}
names = {0: 'SkeletonRoot', 1: 'FighterRoot', 2: 'MotionRoot', 3: 'RotationRoot',
         4: 'SeatedPelvis_Chair', 6: 'RightHip', 7: 'RightKnee', 9: 'RightAnkle',
         12: 'LeftHip', 13: 'LeftKnee', 15: 'LeftAnkle', 66: 'Spine', 67: 'Chest',
         72: 'RightUpperArm', 73: 'RightForearm', 76: 'RightHand', 88: 'Neck',
         89: 'Head', 100: 'LeftUpperArm', 101: 'LeftForearm', 104: 'LeftHand'}
bone_names = [f'Hw_{i:03d}_{names.get(i, "NativeHelper")}' for i in range(118)]
old_world = [matrix(j['worldMatrix']) for j in source]
world = []
for i, j in enumerate(source):
    m = old_world[i].copy()
    if i in pivots:
        m.translation = TO_GAME @ Vector(pivots[i])
    elif i not in (0, 1, 2, 3, 116, 117):
        parent = j['parent']
        # Unused dress/hair/finger pivots remain in their native order and follow
        # their anatomical ancestor. Fine finger offsets shrink to human hands.
        ratio = .38 if 77 <= i <= 87 or 105 <= i <= 115 else .7
        m.translation = world[parent].translation + (old_world[i].translation - old_world[parent].translation) * ratio
    world.append(m)

bind = []
local = []
for i, j in enumerate(source):
    m = world[i] if j['parent'] < 0 else world[j['parent']].inverted() @ world[i]
    loc, rotation, scale = m.decompose()
    euler = rotation.to_euler('XYZ', Euler(j['rotation'], 'XYZ'))
    # Preserve the exact native root channels and attachment helper transforms.
    if i in (0, 1, 2, 3, 116, 117):
        loc, euler, scale = Vector(j['translation']), Euler(j['rotation'], 'XYZ'), Vector(j['scale'])
    local.append(Matrix.LocRotScale(loc, euler.to_quaternion(), scale))
    bind.append({'index': i, 'parent': j['parent'], 'rotation': list(euler),
                 'translation': list(loc), 'scale': list(scale), 'worldMatrix': array(world[i]),
                 'inverseBindMatrix': array(world[i].inverted()), 'anatomy': names.get(i, 'native helper')})
(OUT / 'hawking-bind.json').write_text(json.dumps({'joints': bind, 'sourceToMelee': array(TO_GAME),
    'chairJoint': 4, 'sourceInverseBinds': [j['inverseBindMatrix'] for j in source]}, separators=(',', ':')))

bpy.ops.wm.open_mainfile(filepath=str(ROOT / 'prototype/stephen-hawking.blend'))
model = bpy.data.collections.get('Stephen Hawking prototype')
if model is None:
    raise RuntimeError('Approved prototype collection is missing')
model_objects = set(model.all_objects)
objects = sorted((o for o in model.all_objects if o.type == 'MESH'), key=lambda o: o.name)
# Source studio is unnecessary in an editable game rig, and must not enter export.
for obj in list(bpy.data.objects):
    if obj not in model_objects:
        bpy.data.objects.remove(obj, do_unlink=True)


# Bake solid swatches and the approved CORNER-domain neck gradient into a texture.
# Native Melee cannot combine vertex-color mode with diffuse lighting, so CLR0
# would make the neck unlit beside a lit head. Every family here uses diffuse+TEX0.
materials = [{'name': 'Hw authored solid and neck colors', 'diffuse': [1, 1, 1, 1],
              'texture': 'textures/Hw-colors.png'}]
material_modes = {}
textures = {}
(OUT / 'textures').mkdir(exist_ok=True)
atlas = np.ones((512, 512, 4), dtype=np.float32)
tiles = []
solid_tiles = {}


def bake_tile(colors):
    index = len(tiles)
    if index >= 1024:
        raise RuntimeError('Authored neck/color atlas exceeds 512x512 budget')
    x, y = (index % 32) * 16, (index // 32) * 16
    rgb = np.asarray(colors, dtype=np.float32)[:, :3]
    linear = np.where(rgb <= .04045, rgb / 12.92, ((rgb + .055) / 1.055) ** 2.4)
    for py in range(16):
        for px in range(16):
            # Four texels of padding prevent cross-tile bilinear contamination.
            b, c = (px - 4) / 8, (py - 4) / 8
            barycentric = np.maximum(0, [1 - b - c, b, c])
            barycentric /= barycentric.sum()
            atlas[y + py, x + px] = srgb(barycentric @ linear)
    tiles.append(index)
    return [Vector(((x + u + .5) / 512, (y + v + .5) / 512))
            for u, v in ((4, 4), (12, 4), (4, 12))]


def save_atlas():
    # Write display/sRGB bytes directly, avoiding Blender save/render color transforms.
    # Remove unused atlas rows without resampling any tile or changing its texel coordinates.
    rows = max(1, (len(tiles) + 31) // 32)
    height = 16 * (1 << (rows - 1).bit_length())
    for mesh in meshes:
        if mesh['material'] == 0:
            for vertex in mesh['vertices']:
                vertex['uv'][1] = 1 - (1 - vertex['uv'][1]) * 512 / height
    pixels = np.rint(np.clip(atlas[:height][::-1], 0, 1) * 255).astype(np.uint8)
    scanlines = b''.join(b'\0' + row.tobytes() for row in pixels)
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))
    (OUT / 'textures/Hw-colors.png').write_bytes(b'\x89PNG\r\n\x1a\n'
        + chunk(b'IHDR', struct.pack('>2I5B', 512, height, 8, 6, 0, 0, 0))
        + chunk(b'sRGB', b'\0') + chunk(b'IDAT', zlib.compress(scanlines, 9)) + chunk(b'IEND', b''))


for obj in objects:
    for mat in obj.data.materials:
        if mat.name in material_modes:
            continue
        shader = mat.node_tree.nodes.get('Principled BSDF') if mat.use_nodes else None
        socket = shader.inputs['Base Color'] if shader else None
        if socket is not None and socket.is_linked:
            node = socket.links[0].from_node
            if node.type == 'TEX_IMAGE':
                image = node.image
                if image is None:
                    raise RuntimeError(f'{mat.name}: missing authored image')
                if image.name not in textures:
                    index = len(materials)
                    copy = image.copy()
                    width, height = copy.size
                    def dimension(n):
                        return max(4, min(512, 2 ** int(math.ceil(math.log2(n)))))
                    copy.scale(dimension(width), dimension(height))
                    pixels = np.asarray(copy.pixels[:], dtype=np.float32).reshape(-1, 4)
                    pixels[:, 3] = 1
                    copy.pixels.foreach_set(pixels.ravel())
                    filename = f'textures/Hw-texture-{index}.png'
                    copy.filepath_raw = str(OUT / filename)
                    copy.file_format = 'PNG'
                    copy.save()
                    textures[image.name] = index
                    materials.append({'name': 'Hw ' + mat.name, 'diffuse': [1, 1, 1, 1], 'texture': filename})
                material_modes[mat.name] = (textures[image.name], 'texture', None)
            elif node.type == 'VERTEX_COLOR':
                material_modes[mat.name] = (0, 'color', node.layer_name)
            else:
                raise RuntimeError(f'Unsupported authored base-color shader {mat.name}: {node.type}; refusing to drop likeness')
        else:
            material_modes[mat.name] = (0, 'solid', srgb(socket.default_value if socket else mat.diffuse_color))

meshes = [{'name': m['name'], 'material': i, 'vertices': [], 'triangles': []} for i, m in enumerate(materials)]
unique = [{} for _ in materials]
part_manifest = []
for obj in objects:
    transform = obj.matrix_world.copy()
    obj.data = obj.data.copy()
    obj.data.transform(transform)
    obj.parent = None
    obj.matrix_world = Matrix.Identity(4)
    mesh = obj.data
    mesh.calc_loop_triangles()
    # The visible occupant and chair are rigid. Keep the animated native bones
    # for hitboxes, held items and projectiles, not for skin deformation.
    group = obj.vertex_groups.new(name=bone_names[4])
    group.add(range(len(mesh.vertices)), 1.0, 'REPLACE')
    for triangle in mesh.loop_triangles:
        mat = mesh.materials[triangle.material_index]
        family, mode, parameter = material_modes[mat.name]
        entry = meshes[family]
        indices = []
        if mode == 'color':
            attribute = mesh.color_attributes.get(parameter)
            if attribute is None:
                raise RuntimeError(f'{obj.name}: missing approved vertex-color layer {parameter}')
            colors = [srgb(attribute.data[li if attribute.domain == 'CORNER' else mesh.loops[li].vertex_index].color)
                      for li in triangle.loops]
            baked_uv = bake_tile(colors)
        elif mode == 'solid':
            key = tuple(parameter)
            if key not in solid_tiles:
                solid_tiles[key] = bake_tile([parameter] * 3)
            baked_uv = [solid_tiles[key][0]] * 3
        for corner, loop_index in enumerate(triangle.loops):
            loop = mesh.loops[loop_index]
            vi = loop.vertex_index
            position = TO_GAME @ mesh.vertices[vi].co
            normal = TO_GAME.to_3x3() @ mesh.corner_normals[loop_index].vector
            normal.normalize()
            uv = mesh.uv_layers.active.data[loop_index].uv if mode == 'texture' else baked_uv[corner]
            key = (obj.name, vi, *[round(c, 7) for c in normal], *[round(c, 7) for c in uv])
            if key not in unique[family]:
                unique[family][key] = len(entry['vertices'])
                entry['vertices'].append({'position': list(position), 'normal': list(normal),
                    'uv': [float(uv.x), float(1 - uv.y)], 'joints': [4], 'weights': [1.0]})
            indices.append(unique[family][key])
        entry['triangles'].append(indices)
    part_manifest.append({'name': obj.name, 'triangles': len(mesh.loop_triangles),
                          'joints': [4]})
    # Editable blend uses the same game-space geometry and inverse-bind skeleton.
    mesh.transform(TO_GAME)
save_atlas()

triangle_count = sum(len(m['triangles']) for m in meshes)
if triangle_count > 18000:
    raise RuntimeError(f'Approved model exceeds 18k triangle ceiling: {triangle_count}; no silent destructive decimation')
if any(not m['triangles'] for m in meshes):
    raise RuntimeError('Empty material family in game export')
(OUT / 'hawking-mesh.json').write_text(json.dumps({'materials': materials, 'meshes': meshes,
    'preservedDrawables': [], 'sourceToMelee': array(TO_GAME)}, separators=(',', ':')))

armature = bpy.data.armatures.new('Hw native Zelda-indexed seated skeleton')
arm = bpy.data.objects.new('Stephen Hawking game rig', armature)
bpy.context.scene.collection.objects.link(arm)
bpy.context.view_layer.objects.active = arm
arm.select_set(True)
bpy.ops.object.mode_set(mode='EDIT')
# Blender bones are Y-directed; use native world axes and a short display length.
# Pose channels are converted with convert_local_to_pose, not assigned as native Euler values.
for i, j in enumerate(bind):
    bone = armature.edit_bones.new(bone_names[i])
    bone.head = world[i].translation
    bone.tail = bone.head + world[i].to_3x3() @ Vector((0, .35, 0))
    bone.align_roll(world[i].to_3x3() @ Vector((0, 0, 1)))
    if j['parent'] >= 0:
        bone.parent = armature.edit_bones[bone_names[j['parent']]]
    bone.use_connect = False
bpy.ops.object.mode_set(mode='OBJECT')
arm.show_in_front = True
for i, bone in enumerate(armature.bones):
    bone['native_joint_index'] = i
    bone['native_parent_index'] = bind[i]['parent']
    bone['authoritative_source_inverse_bind'] = source[i]['inverseBindMatrix'] or []
for obj in objects:
    modifier = obj.modifiers.new('Native Melee seated skinning', 'ARMATURE')
    modifier.object = arm
    obj.parent = arm


def converted(values, joint, lock_seated_pose, lock_root_pose):
    if lock_root_pose:
        target = bind[joint]
        return target['rotation'] + target['translation'] + target['scale']
    if joint <= 3 or joint in (116, 117):
        return values
    target = bind[joint]
    if joint == 4 or lock_seated_pose:
        return target['rotation'] + target['translation'] + target['scale']
    baseline = animation['reference'][joint]
    native_translation = Vector(source[joint]['translation']).length
    ratio = 1 if joint <= 4 or native_translation < .01 else min(3, max(.1, Vector(target['translation']).length / native_translation))
    return ([target['rotation'][i] + values[i] - baseline[i] for i in range(3)]
            + [target['translation'][i] + (values[i + 3] - baseline[i + 3]) * ratio for i in range(3)]
            + [values[i + 6] * target['scale'][i] / source[joint]['scale'][i] for i in range(3)])


# Save representative gameplay plus every demo clip as editable Blender actions.
# Native AJ conversion preserves source splines except bombs/oversized demo clips.
for clip in animation['clips']:
    action = bpy.data.actions.new(clip['name'])
    action.use_fake_user = True
    arm.animation_data_create()
    arm.animation_data.action = action
    for frame, samples in enumerate(clip['samples']):
        poses = []
        for i, values in enumerate(samples):
            value = converted(values, i, clip['lockSeatedPose'], clip['lockRootPose'])
            pose = Matrix.LocRotScale(Vector(value[3:6]), Euler(value[:3], 'XYZ').to_quaternion(), Vector(value[6:9]))
            parent = bind[i]['parent']
            pose = pose if parent < 0 else poses[parent] @ pose
            poses.append(pose)
            pb = arm.pose.bones[bone_names[i]]
            # Skin transform must equal native posedWorld * inverseBindWorld.
            desired = pose @ world[i].inverted() @ pb.bone.matrix_local
            if parent < 0:
                pb.matrix_basis = pb.bone.convert_local_to_pose(desired, pb.bone.matrix_local, invert=True)
            else:
                parent_bone = arm.pose.bones[bone_names[parent]]
                parent_pose = poses[parent] @ world[parent].inverted() @ parent_bone.bone.matrix_local
                pb.matrix_basis = pb.bone.convert_local_to_pose(desired, pb.bone.matrix_local,
                    parent_matrix=parent_pose, parent_matrix_local=parent_bone.bone.matrix_local, invert=True)
            pb.rotation_mode = 'QUATERNION'
            pb.keyframe_insert(data_path='location', frame=frame, group=pb.name)
            pb.keyframe_insert(data_path='rotation_quaternion', frame=frame, group=pb.name)
            pb.keyframe_insert(data_path='scale', frame=frame, group=pb.name)
    action['native_frame_count'] = clip['frames']
    if clip['lockRootPose']:
        action.use_frame_range = True
        action.frame_start = 0
        action.frame_end = clip['frames']
arm.animation_data.action = next(a for a in bpy.data.actions if a.name.endswith('_ACTION_Wait1_figatree'))
bpy.context.scene.frame_set(0)
bpy.context.scene.render.fps = 60
arm['chair_attachment_joint'] = 4
arm['retarget_method'] = 'Visible body rigidly bound to the chair; animated native combat bones and root motion retained; results use the seated bind pose; no joint reindexing'
# Pack source images into the generated edit asset; original .blend remains untouched.
bpy.ops.file.pack_all()
bpy.ops.wm.save_as_mainfile(filepath=str(OUT / 'stephen-hawking-game-rig.blend'))
(OUT / 'hawking-rig-manifest.json').write_text(json.dumps({'source': 'prototype/stephen-hawking.blend',
    'joints': 118, 'chairJoint': 4, 'triangles': triangle_count, 'drawables': len(meshes),
    'materials': materials, 'parts': part_manifest, 'editableActions': len(animation['clips']),
    'bounds': {'unitsPerMetre': SCALE, 'maxTriangles': 18000, 'maxTextureDimension': 512},
    'outputs': ['hawking-mesh.json', 'hawking-bind.json', 'stephen-hawking-game-rig.blend']}, indent=2))
print('HAWKING_GAME_RIG', json.dumps({'triangles': triangle_count, 'drawables': len(meshes),
    'joints': 118, 'parts': len(objects), 'editableActions': len(animation['clips'])}), flush=True)
