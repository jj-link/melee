"""Extract and bind the RTX4090-generated pig head; retain Luigi's proven body skinning."""
import json
from pathlib import Path

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'character'
OUT.mkdir(exist_ok=True)
rig = json.loads((ROOT / 'importer/rig.json').read_text())
anchor = rig['joints'][23]['worldMatrix'][12:15]
scale = 13.65
transform = Matrix(((scale, 0, 0, anchor[0]),
                    (0, 0, scale, anchor[1] - .233 * scale),
                    (0, -scale, 0, anchor[2] + .081 * scale),
                    (0, 0, 0, 1)))
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.gltf(filepath=str(ROOT / 'art/john-pork-generated.glb'))
obj = next(o for o in bpy.context.scene.objects if o.type == 'MESH')
bpy.context.view_layer.objects.active = obj
obj.select_set(True)
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
mesh = bmesh.new()
mesh.from_mesh(obj.data)
bmesh.ops.remove_doubles(mesh, verts=list(mesh.verts), dist=.00002)
bmesh.ops.bisect_plane(mesh, geom=list(mesh.verts) + list(mesh.edges) + list(mesh.faces),
                      plane_co=(0, 0, .207), plane_no=(0, 0, 1), clear_inner=True)
# Remove the generated overshirt's raised back collar, not part of the pig head.
bmesh.ops.bisect_plane(mesh, geom=list(mesh.verts) + list(mesh.edges) + list(mesh.faces),
                      plane_co=(0, .194, 0), plane_no=(0, -1, 0), clear_inner=True)
# The remaining dark plaid collar is connected to the head in the generated mesh.
# Separate garment-colored faces below the jaw while preserving the pink skin.
bsdf = obj.data.materials[0].node_tree.nodes.get('Principled BSDF')
source_image = bsdf.inputs['Base Color'].links[0].from_node.image
width, height = source_image.size
pixels = np.empty(width * height * 4, dtype=np.float32)
source_image.pixels.foreach_get(pixels)
pixels = pixels.reshape(height, width, 4)
uv_layer = mesh.loops.layers.uv.active
garment_faces = []
for face in mesh.faces:
    center = face.calc_center_median()
    if center.z >= .325 or center.y <= .04:
        continue
    # The raised collar reaches z=.30. Include its dark edge triangles, not
    # just faces whose center is dark, or a black fringe survives the neck join.
    samples = [loop[uv_layer].uv for loop in face.loops]
    samples.append(sum(samples, Vector((0, 0))) / len(samples))
    if any(pixels[min(height - 1, max(0, int(uv.y * height))),
                  min(width - 1, max(0, int(uv.x * width))), 0] < .42
           for uv in samples):
        garment_faces.append(face)
bmesh.ops.delete(mesh, geom=garment_faces, context='FACES')
print(f'Removed {len(garment_faces)} generated collar faces.')
mesh.to_mesh(obj.data)
mesh.free()
original_triangles = sum(len(p.vertices) - 2 for p in obj.data.polygons)
decimate = obj.modifiers.new('Melee head polygon budget', 'DECIMATE')
decimate.ratio = min(1, 2800 / original_triangles)
decimate.use_collapse_triangulate = True
bpy.ops.object.modifier_apply(modifier=decimate.name)
# The color cut can leave skin fans touching only at a boundary vertex.
# Open those pinches so the collar-to-jaw bridge has a continuous perimeter.
mesh = bmesh.new()
mesh.from_mesh(obj.data)
bmesh.ops.remove_doubles(mesh, verts=list(mesh.verts), dist=.00002)
while True:
    pinches = [v for v in mesh.verts if sum(edge.is_boundary for edge in v.link_edges) > 2]
    if not pinches:
        break
    faces = {face for v in pinches for face in v.link_faces}
    bmesh.ops.delete(mesh, geom=list(faces), context='FACES')
mesh.to_mesh(obj.data)
mesh.free()
for polygon in obj.data.polygons:
    polygon.use_smooth = True
obj.data.update()
obj.data.calc_loop_triangles()
image = source_image.copy()
image.scale(512, 512)
texture_path = OUT / 'textures/john-pork-generated-atlas.png'
texture_path.parent.mkdir(exist_ok=True)
image.filepath_raw = str(texture_path)
image.file_format = 'PNG'
image.save()
vertices, triangles, unique = [], [], {}
uv = obj.data.uv_layers.active.data
for triangle in obj.data.loop_triangles:
    indices = []
    for loop_index in triangle.loops:
        vertex_index = obj.data.loops[loop_index].vertex_index
        coords = uv[loop_index].uv
        normal = obj.data.corner_normals[loop_index].vector
        key = (vertex_index, round(coords.x, 7), round(coords.y, 7), *[round(v, 6) for v in normal])
        if key not in unique:
            position = transform @ obj.data.vertices[vertex_index].co
            unique[key] = len(vertices)
            vertices.append({'position': list(position), 'normal': [normal.x, normal.z, -normal.y],
                             'uv': [coords.x, 1 - coords.y], 'joints': [23], 'weights': [1]})
        indices.append(unique[key])
    triangles.append(indices)
result = {'sourceToMelee': [list(row) for row in transform],
          'materials': [{'name': 'RTX4090 generated John Pork head', 'diffuse': [1, 1, 1, 1],
                         'texture': 'textures/john-pork-generated-atlas.png'}],
          'meshes': [{'name': 'Generated John Pork pig head', 'material': 0,
                      'vertices': vertices, 'triangles': triangles}]}
(OUT / 'john-pork-generated-head.json').write_text(json.dumps(result, separators=(',', ':')))
print(json.dumps({'head_triangles': len(triangles), 'head_vertices': len(vertices),
                  'rig_joint': 23, 'source': 'art/john-pork-generated.glb'}, indent=2))
