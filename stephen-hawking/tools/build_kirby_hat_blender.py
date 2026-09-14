"""Build only Kirby's Hawking-copy glasses and gray hair, in native game space.

Run with Blender --background --python tools/build_kirby_hat_blender.py.
Append -- --preview to also render character/kirby-hawking-hat-preview.png.
The generated blend includes a non-exporting, untextured native body reference;
preview colors are fitting aids, not replacements for Kirby's native materials.
"""
import argparse
import json
import math
from pathlib import Path
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree


ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'character'
TAU = math.tau
MATERIALS = [
    {'name': 'KH charcoal glasses', 'diffuse': [.075, .070, .065, 1.0]},
    {'name': 'KH silver gray hair', 'diffuse': [.58, .60, .63, 1.0]},
    {'name': 'KH pale silver locks', 'diffuse': [.77, .79, .81, 1.0]},
    {'name': 'KH graphite hair roots', 'diffuse': [.39, .42, .46, 1.0]},
]


def native_triangles(piece):
    """The rig exporter stores consecutive GX primitive vertices, not indices."""
    offset = 0
    for primitive in piece['primitives']:
        count = primitive['count']
        kind = primitive['type']
        if kind == 'Triangles':
            triangles = [(i, i + 1, i + 2) for i in range(0, count, 3)]
        elif kind == 'TriangleStrip':
            triangles = [(i + (i % 2), i + 1 - (i % 2), i + 2)
                         for i in range(count - 2)]
        elif kind == 'TriangleFan':
            triangles = [(0, i, i + 1) for i in range(1, count - 1)]
        elif kind == 'Quads':
            triangles = [triangle for i in range(0, count, 4)
                         for triangle in ((i, i + 1, i + 2), (i, i + 2, i + 3))]
        else:
            raise RuntimeError(f'Unsupported native body primitive: {kind}')
        for triangle in triangles:
            yield tuple(offset + index for index in triangle)
        offset += count
    if offset != len(piece['vertices']):
        raise RuntimeError('Native primitive counts do not cover the body vertices')


class NativeHead:
    def __init__(self, rig):
        # Drawables 0 and 2 are the right/left normal body. Other drawables are
        # expressions, feet and arms: their overlapping surfaces must not fit hair.
        self.vertices = []
        self.triangles = []
        for piece in rig['geometry']:
            if piece['drawable'] not in (0, 2):
                continue
            offset = len(self.vertices)
            self.vertices.extend(Vector(v['position']) for v in piece['vertices'])
            for triangle in native_triangles(piece):
                indices = tuple(offset + index for index in triangle)
                a, b, c = (self.vertices[index] for index in indices)
                if (b - a).cross(c - a).length_squared > 1e-12:
                    self.triangles.append(indices)
        if not self.triangles:
            raise RuntimeError('Native Kirby normal body reference is missing')
        self.minimum = Vector([min(v[a] for v in self.vertices) for a in range(3)])
        self.maximum = Vector([max(v[a] for v in self.vertices) for a in range(3)])
        self.center = (self.minimum + self.maximum) * .5
        self.reach = (self.maximum - self.minimum).length * 2
        self.bvh = BVHTree.FromPolygons(self.vertices, self.triangles, all_triangles=True)

    def radial(self, direction, clearance):
        direction = Vector(direction).normalized()
        point, _, _, _ = self.bvh.ray_cast(
            self.center + direction * self.reach, -direction, self.reach * 2)
        if point is None:
            raise RuntimeError('Headpiece fitting ray missed the native body')
        return point + direction * clearance

    def polar(self, theta, phi, clearance):
        return self.radial((math.sin(theta) * math.sin(phi), math.cos(theta),
                            math.sin(theta) * math.cos(phi)), clearance)

    def front(self, x, y, clearance=.50):
        point, _, _, _ = self.bvh.ray_cast(
            Vector((x, y, self.maximum.z + self.reach)), Vector((0, 0, -1)),
            self.reach * 2)
        if point is None:
            raise RuntimeError('Glasses fitting ray missed the native face')
        return point + Vector((0, 0, clearance))


def linear_channel(value):
    return value / 12.92 if value <= .04045 else ((value + .055) / 1.055) ** 2.4


def material(name, color):
    result = bpy.data.materials.new(name)
    result.diffuse_color = tuple(linear_channel(v) for v in color[:3]) + (1.0,)
    result.use_nodes = True
    shader = result.node_tree.nodes.get('Principled BSDF')
    shader.inputs['Base Color'].default_value = result.diffuse_color
    shader.inputs['Roughness'].default_value = .72
    return result


class Headpiece:
    def __init__(self, head, collection, materials):
        self.head = head
        self.collection = collection
        self.materials = materials
        self.objects = []

    def mesh(self, name, vertices, faces, family, face_materials=None):
        mesh = bpy.data.meshes.new(name)
        mesh.from_pydata(vertices, [], faces)
        for mat in self.materials:
            mesh.materials.append(mat)
        for index, polygon in enumerate(mesh.polygons):
            polygon.material_index = family if face_materials is None else face_materials[index]
            polygon.use_smooth = True
        # Bake triangulation and consistent outward normals, with no modifiers or
        # object transforms left for the native importer to interpret differently.
        bm = bmesh.new()
        bm.from_mesh(mesh)
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        bmesh.ops.triangulate(bm, faces=list(bm.faces), quad_method='SHORT_EDGE',
                              ngon_method='EAR_CLIP')
        bm.to_mesh(mesh)
        bm.free()
        mesh.update()
        obj = bpy.data.objects.new(name, mesh)
        self.collection.objects.link(obj)
        obj.matrix_world = Matrix.Identity(4)
        group = obj.vertex_groups.new(name='KirbyHawking_joint')
        group.add(list(range(len(mesh.vertices))), 1.0, 'REPLACE')
        obj['native_joint_index'] = 0
        self.objects.append(obj)
        return obj

    def tube(self, name, points, radius, closed=False):
        """Six-sided smooth tube, with rounded path but genuinely empty lenses."""
        points = [Vector(point) for point in points]
        vertices, faces = [], []
        sides = 6
        for index, point in enumerate(points):
            before = points[(index - 1) % len(points)] if closed else points[max(index - 1, 0)]
            after = points[(index + 1) % len(points)] if closed else points[min(index + 1, len(points) - 1)]
            tangent = (after - before).normalized()
            outward = (point - self.head.center).normalized()
            lateral = tangent.cross(outward).normalized()
            normal = lateral.cross(tangent).normalized()
            for side in range(sides):
                angle = TAU * side / sides
                vertices.append(point + radius * (lateral * math.cos(angle)
                                                   + normal * math.sin(angle)))
        for ring in range(len(points) if closed else len(points) - 1):
            following = (ring + 1) % len(points)
            for side in range(sides):
                a, b = ring * sides, following * sides
                other = (side + 1) % sides
                faces.append((a + side, a + other, b + other, b + side))
        if not closed:
            faces.extend((tuple(reversed(range(sides))),
                          tuple(range((len(points) - 1) * sides, len(points) * sides))))
        return self.mesh(name, vertices, faces, 0)

    def glasses(self):
        # Broad vertical ovals frame the native textured eyes without replacing
        # them. Curved rim depth is sampled from the actual pink skin at each point.
        for sign, side in ((-1, 'left'), (1, 'right')):
            points = [self.head.front(sign * 1.25 + 1.05 * math.cos(TAU * i / 24),
                                      8.10 + 1.30 * math.sin(TAU * i / 24))
                      for i in range(24)]
            self.tube(f'KH {side} open oval frame', points, .16, closed=True)
            start = self.head.front(sign * 2.30, 8.10)
            direction = (start - self.head.center).normalized()
            longitude = math.atan2(abs(direction.x), direction.z)
            latitude = math.asin(direction.y)
            arm = [start]
            for i in range(1, 9):
                t = i / 8
                phi = longitude + (1.76 - longitude) * t
                lat = latitude + .02 * math.sin(math.pi * t) - .18 * t
                arm.append(self.head.radial((sign * math.sin(phi) * math.cos(lat),
                                             math.sin(lat), math.cos(phi) * math.cos(lat)), .42))
            self.tube(f'KH {side} curved temple arm', arm, .125)
        bridge = [self.head.front(-.20 + .40 * i / 4,
                                  8.10 + .16 * math.sin(math.pi * i / 4)) for i in range(5)]
        self.tube('KH raised nose bridge', bridge, .135)

    @staticmethod
    def hairline(phi):
        # Receding high forehead, shorter temples, slightly longer nape. Small
        # scallops break the edge without turning the thin shell into a helmet.
        front = max(0.0, math.cos(phi))
        back = max(0.0, -math.cos(phi))
        return 1.09 - .39 * front ** 2 + .14 * back + .027 * math.sin(7 * phi + .4)

    def hair_cap(self):
        sectors, rings = 24, 6
        vertices, faces, families = [], [], []
        for clearance in (.15, .055):
            vertices.append(self.head.polar(0, 0, clearance))
            for ring in range(1, rings + 1):
                for sector in range(sectors):
                    phi = TAU * sector / sectors
                    vertices.append(self.head.polar(self.hairline(phi) * ring / rings,
                                                    phi, clearance))
        layer_size = 1 + sectors * rings
        for layer in range(2):
            offset = layer * layer_size
            for sector in range(sectors):
                following = (sector + 1) % sectors
                faces.append((offset, offset + 1 + sector, offset + 1 + following))
                families.append(1 if layer == 0 else 3)
            for ring in range(rings - 1):
                for sector in range(sectors):
                    following = (sector + 1) % sectors
                    a, b = offset + 1 + ring * sectors, offset + 1 + (ring + 1) * sectors
                    faces.append((a + sector, b + sector, b + following, a + following))
                    families.append(1 if layer == 0 else 3)
        edge = 1 + (rings - 1) * sectors
        for sector in range(sectors):
            a, b = edge + sector, edge + (sector + 1) % sectors
            faces.append((a, b, b + layer_size, a + layer_size))
            families.append(3)
        self.mesh('KH thin scalloped silver hair cap', vertices, faces, 1, families)

    def lock(self, index, start, end, width, lift):
        """Flattened, tapered six-section lock with a pale crest, not a hair spike."""
        centers = []
        for section in range(6):
            t = section / 5
            theta = start[0] + (end[0] - start[0]) * t
            phi = start[1] + (end[1] - start[1]) * t - .12 * math.sin(math.pi * t)
            centers.append(self.head.polar(theta, phi, .18 + lift * math.sin(math.pi * t)))
        vertices, faces, families = [], [], []
        for section, center in enumerate(centers):
            t = section / 5
            outward = (center - self.head.center).normalized()
            tangent = (centers[min(section + 1, 5)] - centers[max(section - 1, 0)]).normalized()
            lateral = tangent.cross(outward).normalized()
            # Almost-flat tips sink into the cap while raised crests carry a
            # restrained silver highlight that remains readable at game distance.
            taper = .12 + .88 * math.sin(math.pi * (.08 + .90 * t))
            half_width = width * taper
            height = .025 + .085 * math.sin(math.pi * t)
            for side in range(6):
                angle = TAU * side / 6
                p = center + lateral * (half_width * math.cos(angle))
                p += outward * (height * math.sin(angle))
                # Lateral lock edges also fit the source facets, not an idealized sphere.
                fitted = self.head.radial(p - self.head.center, .17)
                if (p - self.head.center).length < (fitted - self.head.center).length:
                    p = fitted
                vertices.append(p)
        for section in range(5):
            for side in range(6):
                a, b = section * 6, (section + 1) * 6
                faces.append((a + side, a + (side + 1) % 6,
                              b + (side + 1) % 6, b + side))
                families.append(2 if side == 1 and index % 3 != 1 else 1)
        faces.extend((tuple(reversed(range(6))), tuple(range(30, 36))))
        families.extend((1, 1))
        self.mesh(f'KH swept silver lock {index + 1:02d}', vertices, faces, 1, families)

    def hair(self):
        self.hair_cap()
        # Front locks sweep from Kirby's right toward the left, leaving the center
        # forehead pink. Back/temple locks follow the skull rather than puffing out.
        locks = [
            ((.18, 1.00), (.73, -.62), .39, .12),
            ((.25, 1.25), (.70, -.26), .40, .14),
            ((.35, 1.35), (.66, .12), .37, .12),
            ((.43, 1.50), (.78, .52), .35, .10),
            ((.27, -.45), (.88, -1.00), .37, .09),
            ((.42, -1.10), (1.10, -1.43), .33, .08),
            ((.63, 1.47), (1.10, 1.47), .31, .07),
            ((.19, 2.20), (.93, 1.93), .40, .10),
            ((.29, 2.66), (1.12, 2.38), .38, .09),
            ((.22, 3.08), (1.22, 2.88), .39, .11),
            ((.21, 3.63), (1.23, 3.40), .39, .10),
            ((.31, 4.06), (1.12, 3.95), .37, .09),
            ((.40, 4.60), (1.02, 4.35), .34, .08),
            ((.11, 5.15), (.72, 4.92), .36, .10),
        ]
        for index, (start, end, width, lift) in enumerate(locks):
            self.lock(index, start, end, width, lift)

    def export(self):
        # Match rig_game_model_blender.py: material families, corner normals,
        # deterministic deduplication and root-only influences. No axis conversion:
        # these authored vertices already use game Y-up, +Z-forward, native units.
        meshes = [{'name': mat['name'], 'material': index, 'vertices': [], 'triangles': []}
                  for index, mat in enumerate(MATERIALS)]
        unique = [{} for _ in MATERIALS]
        for obj in sorted(self.objects, key=lambda item: item.name):
            mesh = obj.data
            mesh.calc_loop_triangles()
            for triangle in mesh.loop_triangles:
                family = triangle.material_index
                entry, indices = meshes[family], []
                for loop_index in triangle.loops:
                    vertex_index = mesh.loops[loop_index].vertex_index
                    position = mesh.vertices[vertex_index].co
                    normal = mesh.corner_normals[loop_index].vector.normalized()
                    if not all(math.isfinite(value) for value in (*position, *normal)):
                        raise RuntimeError(f'{obj.name}: non-finite geometry')
                    if normal.length_squared < .99:
                        raise RuntimeError(f'{obj.name}: zero-length surface normal')
                    key = (obj.name, vertex_index, *(round(value, 7) for value in normal))
                    if key not in unique[family]:
                        unique[family][key] = len(entry['vertices'])
                        entry['vertices'].append({'position': list(position), 'normal': list(normal),
                                                  'uv': [.5, .5], 'joints': [0], 'weights': [1.0]})
                    indices.append(unique[family][key])
                entry['triangles'].append(indices)
        count = sum(len(mesh['triangles']) for mesh in meshes)
        if count > 3000 or any(not mesh['triangles'] for mesh in meshes):
            raise RuntimeError(f'Headpiece must have four nonempty materials and <=3000 triangles: {count}')
        document = {'materials': MATERIALS, 'meshes': meshes, 'preservedDrawables': []}
        (OUT / 'kirby-hawking-mesh.json').write_text(
            json.dumps(document, separators=(',', ':'), allow_nan=False), encoding='utf-8')
        return count


def setup_reference(head, cap_rig):
    collection = bpy.data.collections.new('REFERENCE ONLY - native Kirby body, not exported')
    bpy.context.scene.collection.children.link(collection)
    mesh = bpy.data.meshes.new('Native Kirby normal body fitting reference')
    mesh.from_pydata(head.vertices, [], head.triangles)
    mesh.materials.append(material('REFERENCE untextured pink skin', [.98, .55, .68, 1]))
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    obj = bpy.data.objects.new('REFERENCE native body - original eye textures not previewed', mesh)
    collection.objects.link(obj)
    obj.hide_select = True
    obj.display_type = 'WIRE'
    obj['export'] = False
    root = bpy.data.objects.new('REFERENCE KirbyHawking_joint rest attachment', None)
    collection.objects.link(root)
    root.empty_display_type = 'ARROWS'
    root.empty_display_size = .65
    # The root is only a fitting marker. MeshImporter alone applies its inverse;
    # do not parent the already-world-space headpiece to this translated marker.
    values = cap_rig['joints'][0]['worldMatrix']
    root.matrix_world = Matrix([values[i:i + 4] for i in range(0, 16, 4)]).transposed()
    root['native_joint_index'] = 0
    return obj


def setup_studio(head):
    collection = bpy.data.collections.new('PREVIEW ONLY - camera and lighting')
    scene = bpy.context.scene
    scene.collection.children.link(collection)
    target = Vector((0, 6.0, 0))
    camera_data = bpy.data.cameras.new('Headpiece preview camera')
    camera = bpy.data.objects.new(camera_data.name, camera_data)
    collection.objects.link(camera)
    camera.location = (13, 11, 24)
    # Native assets are Y-up; to_track_quat's world-Z roll would tip them sideways.
    forward = (target - camera.location).normalized()
    right = forward.cross(Vector((0, 1, 0))).normalized()
    up = right.cross(forward)
    camera.rotation_euler = Matrix((right, up, -forward)).transposed().to_euler()
    camera_data.type = 'ORTHO'
    camera_data.ortho_scale = 14
    scene.camera = camera
    for name, position, energy, size in (
            ('Preview key', (8, 17, 15), 1900, 8),
            ('Preview fill', (-10, 9, 10), 1200, 9),
            ('Preview silver rim', (2, 14, -10), 2100, 7)):
        data = bpy.data.lights.new(name, 'AREA')
        data.energy, data.shape, data.size = energy, 'DISK', size
        lamp = bpy.data.objects.new(name, data)
        collection.objects.link(lamp)
        lamp.location = position
        lamp.rotation_euler = (head.center - lamp.location).to_track_quat('-Z', 'Y').to_euler()
    scene.world = bpy.data.worlds.new('Headpiece preview environment')
    scene.world.use_nodes = True
    background = scene.world.node_tree.nodes.get('Background')
    background.inputs['Color'].default_value = (.12, .14, .18, 1)
    background.inputs['Strength'].default_value = .45
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 48
    scene.cycles.seed = 0
    scene.render.resolution_x = 960
    scene.render.resolution_y = 960
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = 'PNG'
    scene.view_settings.view_transform = 'Standard'
    scene.render.filepath = str(OUT / 'kirby-hawking-hat-preview.png')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--preview', action='store_true', help='Render the untextured native-body fitting preview')
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else [])
    cap_rig = json.loads((OUT / 'kirby-hawking-source-rig.json').read_text(encoding='utf-8'))
    body_rig = json.loads((OUT / 'kirby-source-rig.json').read_text(encoding='utf-8'))
    if cap_rig['rootName'] != 'KirbyHawking_joint' or len(cap_rig['joints']) != 1:
        raise RuntimeError('Expected the independent one-joint Kirby Hawking cap reference')
    head = NativeHead(body_rig)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    collection = bpy.data.collections.new('EXPORT - Kirby Hawking glasses and silver hair only')
    bpy.context.scene.collection.children.link(collection)
    materials = [material(item['name'], item['diffuse']) for item in MATERIALS]
    piece = Headpiece(head, collection, materials)
    piece.glasses()
    piece.hair()
    count = piece.export()
    reference = setup_reference(head, cap_rig)
    setup_studio(head)
    bpy.context.scene['native_coordinates'] = 'Y-up, +Z-forward; root inverse applied by MeshImporter only'
    bpy.context.scene['export_collection'] = collection.name
    bpy.context.scene['native_triangle_count'] = count
    for obj in piece.objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = piece.objects[0]
    # Disable .blend1 backups: regeneration owns only the named generated assets.
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=str(OUT / 'kirby-hawking-hat.blend'))
    if args.preview:
        reference.display_type = 'TEXTURED'
        bpy.ops.render.render(write_still=True)
    points = [vertex.co for obj in piece.objects for vertex in obj.data.vertices]
    print('KIRBY_HAWKING_HAT', json.dumps({
        'triangles': count, 'materials': len(MATERIALS), 'root': 0,
        'bounds': [[min(p[axis] for p in points), max(p[axis] for p in points)] for axis in range(3)],
        'mesh': 'character/kirby-hawking-mesh.json',
        'blend': 'character/kirby-hawking-hat.blend',
    }), flush=True)


if __name__ == '__main__':
    main()
