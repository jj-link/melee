"""Blender 5.0: --background --python stephen-hawking/tools/build_prototype_blender.py.

Builds a complete editable visual prototype and GLB from the checked-in head asset
and deterministic mechanical/body meshes. This does not alter any playable ISO.
"""
import json
import math
import sys
from pathlib import Path

import bmesh
import bpy
import numpy as np
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from build_body_blender import build_body
from build_chair_blender import build_chair
from model_parts import Parts, material

OUTPUT = ROOT / 'prototype'
OUTPUT.mkdir(exist_ok=True)
HEAD_HEIGHT = 0.28
HEAD_TRIANGLES = 5000
HEAD_TILT = (-8, -15, 0)


def new_collection(name, parent):
    collection = bpy.data.collections.new(name)
    parent.children.link(collection)
    return collection


def import_head(collection, mount):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=str(ROOT / 'art/hawking-head-generated.glb'))
    imported = set(bpy.data.objects) - before
    heads = [obj for obj in imported if obj.type == 'MESH']
    if not heads:
        raise RuntimeError('The generated head asset contains no mesh')
    bpy.ops.object.select_all(action='DESELECT')
    for obj in heads:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = heads[0]
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    coordinates = np.array([tuple(vertex.co) for obj in heads for vertex in obj.data.vertices])
    low, high = coordinates.min(axis=0), coordinates.max(axis=0)
    base = coordinates[coordinates[:, 2] < low[2] + (high[2] - low[2]) * 0.07]
    center = np.median(base[:, :2], axis=0)
    scale = HEAD_HEIGHT / (high[2] - low[2])
    count = sum(len(poly.vertices) - 2 for obj in heads for poly in obj.data.polygons)
    for index, obj in enumerate(heads):
        obj.name = f'Hawking likeness head {index + 1}'
        for current in list(obj.users_collection):
            current.objects.unlink(obj)
        collection.objects.link(obj)
        for vertex in obj.data.vertices:
            vertex.co = ((vertex.co.x - center[0]) * scale,
                         (vertex.co.y - center[1]) * scale,
                         (vertex.co.z - low[2]) * scale - .025)
        obj.parent = mount
        mesh = bmesh.new()
        mesh.from_mesh(obj.data)
        bmesh.ops.remove_doubles(mesh, verts=list(mesh.verts), dist=0.000002)
        # The body already supplies the lower neck. Discard the reconstructed
        # stump rather than leaving its wide, uneven rim around that neck.
        bmesh.ops.bisect_plane(mesh, geom=list(mesh.verts) + list(mesh.edges) + list(mesh.faces),
                               plane_co=(0, 0, .015), plane_no=(0, 0, 1),
                               clear_inner=True, clear_outer=False, dist=0.000001)
        # Preserve the facial fit, but give the nape a wider attachment and a
        # longer blend into the occiput instead of pinching it into a neck peg.
        for vertex in mesh.verts:
            if vertex.co.z >= .08:
                continue
            rear = min(1.0, max(0.0, vertex.co.y / .04))
            rear = rear * rear * (3 - 2 * rear)
            blend_top = .055 + .025 * rear
            neck_depth = .034 + .006 * rear
            if vertex.co.z < blend_top:
                radius = math.hypot(vertex.co.x / .038, vertex.co.y / neck_depth)
                if radius > 1:
                    weight = min(1.0, (blend_top - vertex.co.z) / (blend_top - .015))
                    weight = weight * weight * (3 - 2 * weight)
                    shrink = 1 - weight * (1 - 1 / radius)
                    vertex.co.x *= shrink
                    vertex.co.y *= shrink
        mesh.to_mesh(obj.data)
        mesh.free()
        bpy.context.view_layer.objects.active = obj
        modifier = obj.modifiers.new('Head detail budget', 'DECIMATE')
        modifier.ratio = min(1.0, HEAD_TRIANGLES / count)
        modifier.use_collapse_triangulate = True
        bpy.ops.object.modifier_apply(modifier=modifier.name)
        # Collapse can leave duplicate faces; imported split normals also no
        # longer describe the simplified surface.
        obj.data.validate()
        normals = obj.data.attributes.get('custom_normal')
        if normals is not None:
            obj.data.attributes.remove(normals)
        obj.data.update()
        for polygon in obj.data.polygons:
            polygon.use_smooth = True
        for mat in obj.data.materials:
            mat.name = 'Hawking painted likeness'
            shader = mat.node_tree.nodes.get('Principled BSDF')
            if shader:
                for name, value in [('Metallic', 0.0), ('Roughness', 0.72)]:
                    for link in list(shader.inputs[name].links):
                        mat.node_tree.links.remove(link)
                    shader.inputs[name].default_value = value
    for obj in imported - set(heads):
        bpy.data.objects.remove(obj, do_unlink=True)
    # Match the hands/neck to the reconstructed diffuse skin, excluding hair and background.
    shader = heads[0].data.materials[0].node_tree.nodes.get('Principled BSDF')
    if shader and shader.inputs['Base Color'].is_linked:
        image = shader.inputs['Base Color'].links[0].from_node.image
        pixels = np.asarray(image.pixels[:], dtype=np.float32).reshape(-1, 4)
        skin = pixels[(pixels[:, 0] > 0.36) & (pixels[:, 0] < 0.94)
                      & (pixels[:, 0] > pixels[:, 1] * 1.09)
                      & (pixels[:, 1] > pixels[:, 2] * 1.07)]
        if len(skin):
            srgb = np.median(skin[:, :3], axis=0)
            linear = np.where(srgb <= 0.04045, srgb / 12.92, ((srgb + 0.055) / 1.055) ** 2.4)
            material('Hawking skin', tuple(float(v) for v in linear), roughness=0.72)
        # The authored diffuse changes only the masked hair texels. Sample the
        # original skin above so the face, neck and hands retain their colors.
        image = bpy.data.images.load(str(ROOT / 'art/head-diffuse-warm-brown.png'), check_existing=True)
        shader.inputs['Base Color'].links[0].from_node.image = image
    return heads


def _join_neck(head, base):
    """Extend the open head rim to the collar, preserving surface and color continuity."""
    mesh = bmesh.new()
    mesh.from_mesh(head.data)
    rim_edges = [edge for edge in mesh.edges if edge.is_boundary
                 and all(abs(vertex.co.z - .015) < .002 for vertex in edge.verts)]
    bmesh.ops.subdivide_edges(mesh, edges=rim_edges, cuts=1, use_grid_fill=False)
    mesh.normal_update()
    mesh.edges.index_update()
    pending = {edge for edge in mesh.edges if edge.is_boundary
               and all(abs(vertex.co.z - .015) < .002 for vertex in edge.verts)}
    rings = []
    while pending:
        first = min(pending, key=lambda edge: edge.index)
        pending.remove(first)
        loop = first.link_loops[0]
        ring = [loop.vert]
        vertex = loop.link_loop_next.vert
        while vertex != ring[0]:
            ring.append(vertex)
            following = [edge for edge in vertex.link_edges if edge in pending]
            if len(following) != 1:
                raise ValueError('The reconstructed neck rim is not a closed loop')
            edge = following[0]
            pending.remove(edge)
            vertex = edge.other_vert(vertex)
        rings.append(ring)

    def area(ring):
        return sum(a.co.x * b.co.y - b.co.x * a.co.y
                   for a, b in zip(ring, ring[1:] + ring[:1]))

    # The reconstruction has an inner wall as well as its outward-facing rim.
    outer = max(rings, key=area)
    if area(outer) <= 0:
        raise ValueError('The reconstructed head has no outward-facing neck rim')
    uv_layer = mesh.loops.layers.uv.active
    colors = mesh.loops.layers.float_color.new('Neck tint')
    for face in mesh.faces:
        for loop in face.loops:
            loop[colors] = (1, 1, 1, 1)
    skin = np.array(bpy.data.materials['Hawking skin'].diffuse_color)
    neck_mat = material('Hawking blended neck', (1, 1, 1), roughness=.72)
    color_node = neck_mat.node_tree.nodes.new('ShaderNodeVertexColor')
    color_node.layer_name = 'Neck tint'
    neck_mat.node_tree.links.new(color_node.outputs['Color'],
                                neck_mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
    neck_index = len(head.data.materials)
    head.data.materials.append(neck_mat)
    for ring in rings:
        if ring is outer:
            continue
        edges = [mesh.edges.get((a, b)) for a, b in zip(ring, ring[1:] + ring[:1])]
        # Fitting compresses the cavity wall against the visible skin. Remove
        # that hidden wall within the blend region instead of capping it there.
        frontier = {face for edge in edges for face in edge.link_faces}
        interior = set()
        while frontier:
            face = frontier.pop()
            center = face.calc_center_median()
            blend_top = .05 + .03 * min(1.0, max(0.0, center.y / .04))
            if face in interior or max(vertex.co.z for vertex in face.verts) >= blend_top:
                continue
            if face.normal.x * center.x + face.normal.y * center.y > 0:
                continue
            interior.add(face)
            frontier.update(other for edge in face.edges for other in edge.link_faces)
        bmesh.ops.delete(mesh, geom=list(interior), context='FACES')

    shader = head.data.materials[0].node_tree.nodes['Principled BSDF']
    image = shader.inputs['Base Color'].links[0].from_node.image
    width, height = image.size
    pixels = np.asarray(image.pixels[:], dtype=np.float32).reshape(height, width, 4)

    def sample(uv):
        x, y = uv.x * width - .5, uv.y * height - .5
        ix, iy = math.floor(x), math.floor(y)
        fx, fy = x - ix, y - iy
        rgb = ((pixels[iy % height, ix % width, :3] * (1 - fx)
                + pixels[iy % height, (ix + 1) % width, :3] * fx) * (1 - fy)
               + (pixels[(iy + 1) % height, ix % width, :3] * (1 - fx)
                  + pixels[(iy + 1) % height, (ix + 1) % width, :3] * fx) * fy)
        linear = np.where(rgb <= .04045, rgb / 12.92, ((rgb + .055) / 1.055) ** 2.4)
        return np.append(linear, 1)

    edge_colors = []
    for a, b in zip(outer, outer[1:] + outer[:1]):
        face = mesh.edges.get((a, b)).link_faces[0]
        edge_colors.append(tuple(sample(next(loop[uv_layer].uv for loop in face.loops
                                             if loop.vert == vertex)) for vertex in (a, b)))
    center, lateral, depth = base
    center = Vector(center)
    inverse = head.matrix_world.inverted()
    axis = (head.matrix_world.to_3x3() @ Vector((0, 0, 1))).normalized()
    controls = []
    for vertex in outer:
        top = head.matrix_world @ vertex.co
        angle = math.atan2(vertex.co.y / (.040 if vertex.co.y >= 0 else .034),
                           vertex.co.x / .038)
        bottom = center + Vector((lateral * math.cos(angle), depth * math.sin(angle), 0))
        span = (top.z - bottom.z) / 3
        controls.append((top, top - axis * span, bottom + Vector((0, 0, span)), bottom))
    previous = outer
    previous_t = 0.0
    for step in range(1, 5):
        t = step / 4
        blend = t * t * (3 - 2 * t)
        ring = [mesh.verts.new(inverse @ (a * (1 - t) ** 3 + b * (3 * (1 - t) ** 2 * t)
                                        + c * (3 * (1 - t) * t * t) + d * t ** 3))
                for a, b, c, d in controls]
        for index, (ca, cb) in enumerate(edge_colors):
            following = (index + 1) % len(ring)
            face = mesh.faces.new((previous[following], previous[index], ring[index], ring[following]))
            face.material_index = neck_index
            corner_colors = (cb * (1 - previous_t) + skin * previous_t,
                             ca * (1 - previous_t) + skin * previous_t,
                             ca * (1 - blend) + skin * blend,
                             cb * (1 - blend) + skin * blend)
            for loop, color in zip(face.loops, corner_colors):
                loop[colors] = color
        previous, previous_t = ring, blend
    cap = mesh.faces.new(tuple(reversed(previous)))
    cap.material_index = neck_index
    for loop in cap.loops:
        loop[colors] = skin
    mesh.normal_update()
    mesh.to_mesh(head.data)
    mesh.free()
    for polygon in head.data.polygons:
        polygon.use_smooth = True
    head.data.update()
    head.data.color_attributes.active_color_index = head.data.color_attributes.find('Neck tint')
    head.data.color_attributes.render_color_index = head.data.color_attributes.active_color_index




def apply_screen_texture():
    mat = bpy.data.materials['Communication screen']
    shader = mat.node_tree.nodes.get('Principled BSDF')
    texture = mat.node_tree.nodes.new('ShaderNodeTexImage')
    texture.image = bpy.data.images.load(str(ROOT / 'art/communication-screen.png'))
    mat.node_tree.links.new(texture.outputs['Color'], shader.inputs['Base Color'])
    mat.node_tree.links.new(texture.outputs['Color'], shader.inputs['Emission Color'])
    shader.inputs['Emission Strength'].default_value = .22
    shader.inputs['Roughness'].default_value = .38
    for obj in bpy.data.objects:
        if obj.type != 'MESH' or mat not in obj.data.materials[:]:
            continue
        coordinates = [vertex.co for vertex in obj.data.vertices]
        x0, x1 = min(v.x for v in coordinates), max(v.x for v in coordinates)
        z0, z1 = min(v.z for v in coordinates), max(v.z for v in coordinates)
        uv = obj.data.uv_layers.get('ScreenUV') or obj.data.uv_layers.new(name='ScreenUV')
        obj.data.uv_layers.active = uv
        for polygon in obj.data.polygons:
            for index in polygon.loop_indices:
                vertex = obj.data.vertices[obj.data.loops[index].vertex_index].co
                uv.data[index].uv = ((x1 - vertex.x) / (x1 - x0), (vertex.z - z0) / (z1 - z0))


def aim(obj, target):
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat('-Z', 'Y').to_euler()


bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
model = new_collection('Stephen Hawking prototype', scene.collection)
chair_collection = new_collection('Motorized wheelchair', model)
body_collection = new_collection('Seated character', model)
head_collection = new_collection('Likeness and spectacles', model)
root = bpy.data.objects.new('Stephen Hawking - visual prototype', None)
model.objects.link(root)
root['scope'] = 'Visual model only. Not yet a Melee fighter or gameplay rig.'
chair = build_chair(chair_collection)
chair.parent = root
body = build_body(body_collection)
body['root'].parent = root
heads = import_head(head_collection, body['head_mount'])
body['head_mount'].rotation_euler = tuple(math.radians(angle) for angle in HEAD_TILT)
body['head_mount'].location.z -= .05
apply_screen_texture()
bpy.context.view_layer.update()
_join_neck(heads[0], body['neck_base'])

counts = {}
for collection in (chair_collection, body_collection, head_collection):
    counts[collection.name] = sum(len(poly.vertices) - 2 for obj in collection.all_objects
                                  if obj.type == 'MESH' for poly in obj.data.polygons)
model_meshes = [obj for obj in model.all_objects if obj.type == 'MESH']
corners = [obj.matrix_world @ Vector(corner) for obj in model_meshes for corner in obj.bound_box]
low = Vector(tuple(min(point[i] for point in corners) for i in range(3)))
high = Vector(tuple(max(point[i] for point in corners) for i in range(3)))
manifest = {'scope': root['scope'], 'triangles': counts, 'total_triangles': sum(counts.values()),
            'mesh_objects': len(model_meshes), 'bounds_meters': {'min': list(low), 'max': list(high)},
            'head_height_meters': HEAD_HEIGHT, 'head_tilt_degrees': HEAD_TILT,
            'source_head': 'art/hawking-head-generated.glb',
            'head_diffuse': 'art/head-diffuse-warm-brown.png'}
(OUTPUT / 'model-stats.json').write_text(json.dumps(manifest, indent=2))
print('PROTOTYPE_GEOMETRY', json.dumps(manifest), flush=True)

bpy.ops.object.select_all(action='DESELECT')
for obj in model.all_objects:
    obj.select_set(True)
bpy.ops.export_scene.gltf(filepath=str(OUTPUT / 'stephen-hawking.glb'), export_format='GLB',
                          use_selection=True, export_apply=True, export_animations=False,
                          export_cameras=False, export_lights=False)

studio = new_collection('Preview studio - excluded from GLB', scene.collection)
parts = Parts('Preview studio', studio)
floor = material('Preview floor', (.085, .105, .135), roughness=.9)
parts.box('Studio floor', (0, 0, low.z - .025), (200, 200, .05), floor)
scene.world = bpy.data.worlds.new('Preview environment')
scene.world.use_nodes = True
background = scene.world.node_tree.nodes.get('Background')
background.inputs['Color'].default_value = (.27, .32, .40, 1)
background.inputs['Strength'].default_value = .45
for name, location, energy, size in [
        ('Key softbox', (-3, -4, 5), 480, 4),
        ('Face fill', (3, -2, 3), 230, 3),
        ('Chair rim', (1, 3, 4), 520, 3)]:
    bpy.ops.object.light_add(type='AREA', location=location)
    light = bpy.context.object
    light.name = name
    light.data.energy = energy
    light.data.shape = 'DISK'
    light.data.size = size
    aim(light, (0, 0, .8))
    for collection in list(light.users_collection):
        collection.objects.unlink(light)
    studio.objects.link(light)
bpy.ops.object.camera_add()
camera = bpy.context.object
camera.name = 'Prototype review camera'
camera.data.type = 'ORTHO'
scene.camera = camera
scene.render.engine = 'CYCLES'
scene.cycles.device = 'CPU'
scene.cycles.samples = 32
scene.cycles.use_denoising = True
scene.render.threads_mode = 'FIXED'
scene.render.threads = 16
scene.view_settings.view_transform = 'Standard'
scene.render.resolution_x = 1000
scene.render.resolution_y = 1100
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = 'PNG'
views = [
    ('three-quarter', (2.5, -4.2, 2.25), (0, -.04, .79), 1.92),
    ('front', (0, -4.5, 1.65), (0, -.04, .78), 1.92),
    ('side', (4.5, .05, 1.65), (0, -.04, .78), 1.92),
    ('back', (-2.7, 4.0, 2.15), (0, -.04, .78), 1.92),
    ('portrait', (1.0, -3.3, 1.68), (0, .14, 1.24), .78),
    ('collar-detail', (.18, -2.8, 1.42), (.012, .12, 1.176), .28),
    ('rear-mount-detail', (1.0, 3.0, 1.48), (0, .42, 1.075), .57),
    ('nape-right', (3.2, .18, 1.32), (0, .20, 1.29), .42),
    ('nape-left', (-3.2, .18, 1.32), (0, .20, 1.29), .42),
]
for name, position, target, scale in views:
    camera.location = position
    aim(camera, target)
    camera.data.ortho_scale = scale
    scene.render.filepath = str(OUTPUT / f'hawking-{name}.png')
    if name == 'three-quarter':
        for image in bpy.data.images:
            # Embedded GLB images have no backing path. Repacking them from
            # disk discards their existing bytes in Blender 5.
            if image.source == 'FILE' and image.has_data and image.packed_file is None:
                image.pack()
        bpy.ops.wm.save_as_mainfile(filepath=str(OUTPUT / 'stephen-hawking.blend'))
    bpy.ops.render.render(write_still=True)
print('PROTOTYPE_RENDER_COMPLETE', OUTPUT, flush=True)
