"""Build John Pork's Melee-resolution mesh against the exact Luigi bind skeleton."""
import json
from collections import Counter
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'character'
TEX = OUT / 'textures'
TEX.mkdir(parents=True, exist_ok=True)
RIG = json.loads((ROOT / 'importer/rig.json').read_text())
RNG = np.random.default_rng(260910)
materials = []
meshes = []


def texture(name, rgb, noise=1.5, size=256):
    values = np.broadcast_to(np.array(rgb, dtype=float), (size, size, 3)).copy()
    values += RNG.normal(0, noise, (size, size, 1))
    result = Image.fromarray(np.uint8(np.clip(values, 0, 255)))
    result.save(TEX / (name + '.png'))
    return result


def material(name, rgb=(1, 1, 1), image=None):
    materials.append({'name': name, 'diffuse': [*rgb, 1], 'texture': 'textures/' + image + '.png' if image else None})
    return len(materials) - 1


def make_plaid(size):
    yy, xx = np.indices((size, size))
    period = size / 6
    horizontal = (yy % period) / period
    vertical = (xx % period) / period
    rgb = np.zeros((size, size, 3), dtype=float) + [137, 36, 48]
    dark = ((horizontal < .24) | (vertical < .24))
    rgb[dark] *= .49
    cross = (horizontal < .24) & (vertical < .24)
    rgb[cross] *= .70
    thin = ((abs(horizontal - .58) < .025) | (abs(vertical - .58) < .025))
    rgb[thin] = [57, 28, 37]
    highlight = ((abs(horizontal - .76) < .013) | (abs(vertical - .76) < .013))
    rgb[highlight] = [177, 70, 79]
    rgb += RNG.normal(0, 2.0, (size, size, 1))
    rgb += ((xx + yy) % 2)[..., None] * 2
    return Image.fromarray(np.uint8(np.clip(rgb, 0, 255)))


skin_image = texture('skin', (210, 155, 151), 1.7, 512)
# Subtle variation survives RGB565 without turning the character into a flat pink toy.
sy, sx = np.indices((512, 512))
skin_pixels = np.array(skin_image).astype(float)
skin_pixels += (np.cos(sx / 512 * math.tau) * 2)[..., None]
Image.fromarray(np.uint8(np.clip(skin_pixels, 0, 255))).save(TEX / 'skin.png')
plaid = make_plaid(512)
plaid.save(TEX / 'plaid.png')
body = np.array(plaid).copy()
for py in range(512):
    height = 8.9 - py / 511 * 4.6
    # An open overshirt over a heather-gray T-shirt, with a visible collar opening.
    opening = .052 + .035 * max(0, (height - 7.5) / 1.4)
    for px in range(512):
        angle = abs(px / 511 - .5)
        if angle < opening:
            fleck = int(RNG.normal(0, 2))
            body[py, px] = np.clip(np.array([111, 110, 121]) + fleck, 0, 255)
        if opening <= angle < opening + .006:
            body[py, px] = [67, 24, 32]
        if height < 5.02:
            body[py, px] = [31, 49, 71]
# Front overshirt stitch lines and small buttons are baked, not extra runtime geometry.
body_image = Image.fromarray(body)
bd = ImageDraw.Draw(body_image)
for yy in [173, 260, 347, 427]:
    bd.ellipse((286, yy, 291, yy + 5), fill=(35, 26, 28))
body_image.save(TEX / 'body.png')
denim = np.array(texture('denim', (40, 64, 91), 2, 256)).astype(float)
yy, xx = np.indices((256, 256))
denim += (((xx + yy * 2) % 5) == 0)[..., None] * 6
Image.fromarray(np.uint8(np.clip(denim, 0, 255))).save(TEX / 'denim.png')
shoe = texture('shoe', (56, 52, 54), 1.2, 256)
shoe_pixels = np.array(shoe)
shoe_pixels[195:239, :] = [202, 197, 188]
shoe_pixels[239:, :] = [56, 50, 49]
shoe = Image.fromarray(shoe_pixels)
shoe_draw = ImageDraw.Draw(shoe)
for yy in range(50, 151, 20):
    shoe_draw.line((105, yy, 151, yy + 5), fill=(219, 212, 202), width=5)
shoe.save(TEX / 'shoe.png')

skin = material('Warm natural pig skin', image='skin')
hands = material('Natural hand skin', (.82, .61, .59))
shirt = material('Open red plaid overshirt and gray tee', image='body')
sleeve = material('Plaid sleeves', image='plaid')
jeans = material('Blue denim', image='denim')
shoes = material('Charcoal canvas sneakers', image='shoe')
sole = material('Rubber soles', (.69, .68, .64))
eye_white = material('Warm eye whites', (.76, .73, .65))
iris = material('Brown human irises', (.23, .12, .07))
pupil = material('Pupils and nostril recess', (.055, .038, .039))
catchlight = material('Eye catchlight', (.88, .87, .81))


def vertex(position, normal, uv=(.5, .5), joints=(23,), weights=(1,)):
    n = np.asarray(normal, dtype=float)
    n /= max(np.linalg.norm(n), 1e-12)
    return {'position': list(map(float, position)), 'normal': n.tolist(), 'uv': list(map(float, uv)), 'joints': list(joints), 'weights': list(weights)}


def triangles_from_primitives(geometry):
    result = []
    offset = 0
    for primitive in geometry['primitives']:
        count = primitive['count']
        kind = primitive['type']
        if kind == 'TriangleStrip':
            for i in range(count - 2):
                result.append([offset + i, offset + i + (2 if i % 2 else 1), offset + i + (1 if i % 2 else 2)])
        elif kind == 'Triangles':
            result.extend([list(range(offset + i, offset + i + 3)) for i in range(0, count, 3)])
        elif kind == 'TriangleFan':
            result.extend([[offset, offset + i, offset + i + 1] for i in range(1, count - 1)])
        elif kind == 'Quads':
            for i in range(0, count, 4):
                result.extend([[offset + i, offset + i + 1, offset + i + 2], [offset + i, offset + i + 2, offset + i + 3]])
        else:
            raise ValueError(f'Unsupported original primitive {kind}')
        offset += count
    # Original GX display lists use the opposite winding to our outward-normal mesh JSON.
    return [[a, c, b] for a, b, c in result]


def adopt(drawables, label, material_id, uv_function):
    vertices, triangles = [], []
    for geometry in RIG['geometry']:
        if geometry['drawable'] not in drawables:
            continue
        base = len(vertices)
        for old in geometry['vertices']:
            item = dict(old)
            item['uv'] = uv_function(*old['position'])
            vertices.append(item)
        triangles.extend([[base + i for i in tri] for tri in triangles_from_primitives(geometry)])
    meshes.append({'name': label, 'material': material_id, 'vertices': vertices, 'triangles': triangles})


adopt([0, 2], 'Jeans with original leg deformation', jeans, lambda x, y, z: [(.5 + math.atan2(x - math.copysign(1.12, x), z) / math.tau), (5.1 - y) / 4.3])
adopt([1, 5, 6], 'Plaid overshirt body', shirt, lambda x, y, z: [math.atan2(x, z) / math.tau + .5, (8.9 - y) / 4.6])
adopt([7], 'Neck', skin, lambda x, y, z: [x / 2 + .5, (9.3 - y)])
neck_mesh = meshes.pop()
adopt([8], 'Plaid sleeves', sleeve, lambda x, y, z: [abs(x) / 5, math.atan2(y - 7.93, z + .18) / math.tau + .5])
adopt([26, 47], 'Canvas sneaker uppers', shoes, lambda x, y, z: [math.atan2(x - math.copysign(1.14, x), z - .5) / math.tau + .5, 1 - max(0, min(1, (y + .16) / 1.8))])
adopt([27, 48], 'Rubber sneaker soles', sole, lambda x, y, z: [.5, .5])
# These meshes are for the editable preview. The DAT retains its native hand POBJs
# and visibility slots, including open/fist and low-detail variants.
adopt([29, 30, 50, 51], 'Native open-hand preview', hands, lambda x, y, z: [.5, .5])
preview_meshes = [meshes.pop()]
preserved_drawables = [{'index': index, 'material': hands} for index in [28, 29, 30, 44, 46, 49, 50, 51]]


def ellipsoid(name, center, radii, material_id, segments=24, rings=12, bone=23):
    vertices, triangles = [], []
    center, radii = np.array(center), np.array(radii)
    for row in range(rings + 1):
        theta = math.pi * row / rings
        for col in range(segments + 1):
            phi = math.tau * col / segments
            unit = np.array([math.sin(theta) * math.sin(phi), math.cos(theta), math.sin(theta) * math.cos(phi)])
            p = center + radii * unit
            vertices.append(vertex(p, unit / radii, (col / segments, row / rings), (bone,)))
    for row in range(rings):
        for col in range(segments):
            a = row * (segments + 1) + col
            b = a + segments + 1
            if row > 0:
                triangles.append([a, b, a + 1])
            if row < rings - 1:
                triangles.append([a + 1, b, b + 1])
    meshes.append({'name': name, 'material': material_id, 'vertices': vertices, 'triangles': triangles})


# The 4090-generated head replaces the hat, moustache and entire original face.
head = json.loads((OUT / 'john-pork-generated-head.json').read_text())
head_transform = np.array(head['sourceToMelee'])
material_offset = len(materials)
materials.extend(head['materials'])
for mesh in head['meshes']:
    mesh['material'] += material_offset
    meshes.append(mesh)


def boundary_ring(mesh, axis):
    # Weld display-list/UV duplicates only for topology; source vertices stay intact.
    by_position = {tuple(v['position']): v for v in mesh['vertices']}
    edges = Counter()
    for triangle in mesh['triangles']:
        points = [tuple(mesh['vertices'][i]['position']) for i in triangle]
        if len(set(points)) != 3:
            continue
        for a, b in zip(points, points[1:] + points[:1]):
            edges[tuple(sorted((a, b)))] += 1
    neighbors = {}
    for (a, b), count in edges.items():
        if count == 1:
            neighbors.setdefault(a, []).append(b)
            neighbors.setdefault(b, []).append(a)
    # Lowest boundary selects the inherited collar; rearmost selects the main
    # generated head shell, not its separate, overlapping inner shell.
    start = min(neighbors, key=lambda p: p[axis])
    ring, previous, current = [], None, start
    while True:
        if len(neighbors[current]) != 2 or current in ring:
            raise ValueError(f'{mesh["name"]}: neck attachment is not a simple boundary')
        ring.append(current)
        following = next(p for p in neighbors[current] if p != previous)
        if following == start:
            break
        previous, current = current, following
    # Both loops run front -> right -> back -> left, with a common front seam.
    area = sum(a[0] * b[2] - b[0] * a[2] for a, b in zip(ring, ring[1:] + ring[:1]))
    if area > 0:
        ring.reverse()
    front = max(range(len(ring)), key=lambda i: ring[i][2])
    ring = ring[front:] + ring[:front]
    return [by_position[p] for p in ring]


def connect_neck(collar, jaw):
    # Subdivide BOTH boundary polylines at their combined perimeter fractions.
    # This retains every collar/head corner rather than bridging past an edge.
    rings, distances = [], []
    for boundary in (collar, jaw):
        positions = np.array([v['position'] for v in boundary] + [boundary[0]['position']])
        lengths = np.linalg.norm(np.diff(positions, axis=0), axis=1)
        distances.append(np.concatenate(([0.], np.cumsum(lengths))) / lengths.sum())
        rings.append(positions)
    samples = sorted(set(distances[0][:-1]) | set(distances[1][:-1]))
    boundaries = []
    for positions, distance in zip(rings, distances):
        boundaries.append(np.array([[np.interp(t, distance, positions[:, axis])
                                     for axis in range(3)] for t in samples]))
    vertices, triangles = [], []
    # Joint 5 matches the collar exactly, 22 carries neck flexion, and the last
    # row is wholly joint 23 like the unchanged generated head. No arm weights:
    # winding/punch shoulder motion must not pull the neckline off the chest.
    rows = [(0., (5,), (1.,)),
            (.25, (5, 22), (.45, .55)),
            (.5, (5, 22, 23), (.1, .65, .25)),
            (.75, (22, 23), (.3, .7)),
            (1., (23,), (1.,))]
    for height, joints, weights in rows:
        for t, lower, upper in zip(samples, *boundaries):
            position = lower * (1 - height) + upper * height
            vertices.append(vertex(position, (0, 0, 0), (t, 1 - height), joints, weights))
    count = len(samples)
    for row in range(len(rows) - 1):
        for col in range(count):
            a = row * count + col
            next_col = row * count + (col + 1) % count
            triangles.extend([[a, next_col, a + count],
                              [next_col, next_col + count, a + count]])
    normals = np.zeros((len(vertices), 3))
    positions = np.array([v['position'] for v in vertices])
    for a, b, c in triangles:
        normal = np.cross(positions[b] - positions[a], positions[c] - positions[a])
        normals[[a, b, c]] += normal
    for v, normal in zip(vertices, normals):
        v['normal'] = (normal / max(np.linalg.norm(normal), 1e-12)).tolist()
    meshes.append({'name': 'Connected collar-to-jaw neck', 'material': skin,
                   'vertices': vertices, 'triangles': triangles})


# Drawable 7 stopped at y=9.266 with only 30% head weighting. The generated
# head's open nape reaches y=9.949 / z=-1.553, so retaining that short tube leaves
# the back and sides open. Replace it with a complete 360-degree skin bridge:
# original collar boundary (y=8.473..8.848) to the ACTUAL generated jaw boundary
# (y=8.951..9.949). Shared positions and endpoint weights seal both attachments
# through animation without moving the head, clothing, or any bind joint.
connect_neck(boundary_ring(neck_mesh, 1), boundary_ring(head['meshes'][0], 2))


def head_point(x, y, z):
    return (head_transform @ np.array([x, y, z, 1]))[:3]


# Small authored eye surfaces restore the open human eyes lost during 3D generation.
# Every surface shares head joint 23; no new bones or animation indices are introduced.
scale = 13.65
for side in [-1, 1]:
    ellipsoid(f'Eye white {side}', head_point(side * .045, -.027, .350),
              np.array([.023, .010, .002]) * scale, eye_white, 16, 8)
    ellipsoid(f'Brown iris {side}', head_point(side * .045, -.029, .350),
              np.array([.0095, .009, .0008]) * scale, iris, 12, 8)
    ellipsoid(f'Pupil {side}', head_point(side * .045, -.030, .350),
              np.array([.0045, .0065, .0005]) * scale, pupil, 12, 8)
    ellipsoid(f'Eye glint {side}', head_point(side * .045 - .003, -.031, .353),
              np.array([.0018, .002, .0005]) * scale, catchlight, 8, 6)
    ellipsoid(f'Nostril {side}', head_point(side * .021, -.056, .296),
              np.array([.0095, .012, .0012]) * scale, pupil, 12, 8)

result = {'materials': materials, 'meshes': meshes, 'previewMeshes': preview_meshes, 'preservedDrawables': preserved_drawables}
(OUT / 'john-pork-mesh.json').write_text(json.dumps(result, separators=(',', ':')))
(OUT / 'build-stats.json').write_text(json.dumps({'appendedTriangles': sum(len(m['triangles']) for m in meshes), 'nativeHandDrawables': [p['index'] for p in preserved_drawables], 'materials': len(materials), 'sourceRig': '../original/PlLgNr.dat', 'generatedHead': '../art/john-pork-generated.glb', 'description': '4090-generated pig head, authored eyes and clothing textures. Original body weights, native hand geometry and hand visibility animations retained.'}, indent=2))
print(json.dumps({'output': str(OUT / 'john-pork-mesh.json'), 'triangles': sum(len(m['triangles']) for m in meshes), 'meshes': len(meshes)}, indent=2))
