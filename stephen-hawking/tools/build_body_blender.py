"""Editable, headless seated body; coordinates are shared with the chair assembly."""
import math

import bmesh
import bpy
from mathutils import Vector

from model_parts import Parts, material


def _loft(parts, name, sections, mat, sides=12):
    """Closed section mesh: (center, lateral radius, depth radius), no modifiers."""
    centers = [Vector(section[0]) for section in sections]
    vertices, faces = [], []
    for index, (center, width, depth) in enumerate(sections):
        tangent = (centers[min(index + 1, len(centers) - 1)]
                   - centers[max(index - 1, 0)]).normalized()
        lateral = Vector((1, 0, 0))
        lateral = (lateral - tangent * lateral.dot(tangent)).normalized()
        normal = tangent.cross(lateral).normalized()
        for side in range(sides):
            angle = math.tau * side / sides
            vertices.append(centers[index] + lateral * (width * math.cos(angle))
                            + normal * (depth * math.sin(angle)))
    for ring in range(len(sections) - 1):
        for side in range(sides):
            following = (side + 1) % sides
            a, b = ring * sides, (ring + 1) * sides
            faces.append((a + side, a + following, b + following, b + side))
    faces.append(tuple(reversed(range(sides))))
    faces.append(tuple(range((len(sections) - 1) * sides, len(sections) * sides)))
    return parts.mesh(name, vertices, faces, mat, smooth=True)


def _panel(parts, name, outline, mat, thickness=0.002):
    """Thin tailored panel, with its visible surface facing the chair's front."""
    count = len(outline)
    vertices = list(outline) + [(x, y + thickness, z) for x, y, z in outline]
    front = list(range(count))
    signed_area = sum(outline[i][0] * outline[(i + 1) % count][2]
                      - outline[(i + 1) % count][0] * outline[i][2]
                      for i in range(count))
    if signed_area < 0:
        front.reverse()
    faces = [tuple(front), tuple(i + count for i in reversed(front))]
    faces.extend((a, a + count, b + count, b)
                 for a, b in zip(front, front[1:] + front[:1]))
    return parts.mesh(name, vertices, faces, mat)


def _clip_to_triangle(polygon, triangle):
    """Clip a counter-clockwise X/Z polygon to one torso triangle."""
    for a, b in zip(triangle, triangle[1:] + triangle[:1]):
        if not polygon:
            break
        dx, dy = b.x - a.x, b.y - a.y
        previous = polygon[-1]
        previous_side = dx * (previous.y - a.y) - dy * (previous.x - a.x)
        clipped = []
        for current in polygon:
            current_side = dx * (current.y - a.y) - dy * (current.x - a.x)
            if (current_side >= 0) != (previous_side >= 0):
                clipped.append(previous.lerp(current, previous_side / (previous_side - current_side)))
            if current_side >= 0:
                clipped.append(current)
            previous, previous_side = current, current_side
        polygon = clipped
    return polygon


def _fitted_panel(parts, name, outline, mat, torso, offset=0.003, allow_partial=False):
    """Clip cloth to the torso's actual facets, keeping every face in front."""
    pattern = bmesh.new()
    face = pattern.faces.new([pattern.verts.new((x, 0, z)) for x, y, z in outline])
    pattern.normal_update()
    if face.normal.y > 0:
        face.normal_flip()
    bmesh.ops.triangulate(pattern, faces=list(pattern.faces))
    pieces = []
    expected_area = 0.0
    for face in pattern.faces:
        points = [Vector((vertex.co.x, vertex.co.z)) for vertex in face.verts]
        expected_area += face.calc_area()
        pieces.append((points, min(p.x for p in points), max(p.x for p in points),
                       min(p.y for p in points), max(p.y for p in points)))
    pattern.free()

    mesh = bmesh.new()
    covered_area = 0.0
    torso.data.calc_loop_triangles()
    for triangle in torso.data.loop_triangles:
        a, b, c = [torso.data.vertices[index].co for index in triangle.vertices]
        normal = (b - a).cross(c - a)
        if normal.y >= -1e-10:
            continue
        clip = [Vector((point.x, point.z)) for point in (a, b, c)]
        xmin, xmax = min(p.x for p in clip), max(p.x for p in clip)
        zmin, zmax = min(p.y for p in clip), max(p.y for p in clip)
        for points, pxmin, pxmax, pzmin, pzmax in pieces:
            if pxmax < xmin or pxmin > xmax or pzmax < zmin or pzmin > zmax:
                continue
            clipped = _clip_to_triangle(points, clip)
            if len(clipped) < 3:
                continue
            area = 0.5 * sum(p.x * q.y - q.x * p.y
                             for p, q in zip(clipped, clipped[1:] + clipped[:1]))
            if area <= 1e-12:
                continue
            covered_area += area
            vertices = []
            for point in clipped:
                y = a.y - (normal.x * (point.x - a.x) + normal.z * (point.y - a.z)) / normal.y
                vertices.append(mesh.verts.new((point.x, y - offset, point.y)))
            mesh.faces.new(vertices)
    if covered_area <= 1e-12 or (not allow_partial and abs(covered_area - expected_area) > max(1e-8, expected_area * 1e-4)):
        mesh.free()
        raise ValueError(f'{name} does not fit its torso surface: {covered_area} / {expected_area}')
    bmesh.ops.remove_doubles(mesh, verts=list(mesh.verts), dist=0.0000001)
    mesh.normal_update()
    mesh.verts.ensure_lookup_table()
    mesh.verts.index_update()
    coordinates = [tuple(vertex.co) for vertex in mesh.verts]
    faces = [tuple(vertex.index for vertex in face.verts) for face in mesh.faces]
    mesh.free()
    return parts.mesh(name, coordinates, faces, mat, smooth=True)


def _collar(parts, torso, mat):
    """Open neck band with thin, rolled collar leaves fitted to the shirt."""
    cx, cy = 0.015, 0.223
    segments, gap = 16, 0.42
    step = (math.tau - 2 * gap) / segments
    angles = [-math.pi / 2 + gap + index * step for index in range(segments + 1)]
    band_width, band_depth = 0.040, 0.041
    profile = [(band_width, band_depth, 1.190), (band_width, band_depth, 1.202),
               (band_width - 0.001, band_depth - 0.001, 1.202),
               (band_width - 0.001, band_depth - 0.001, 1.190)]
    vertices = [(cx + width * math.cos(angle), cy + depth * math.sin(angle), z)
                for width, depth, z in profile for angle in angles]
    stride = segments + 1
    faces = []
    for ring in range(4):
        a, b = ring * stride, ((ring + 1) % 4) * stride
        faces.extend((a + index, a + index + 1, b + index + 1, b + index)
                     for index in range(segments))
    faces.append(tuple(ring * stride for ring in range(4)))
    faces.append(tuple(ring * stride + segments for ring in reversed(range(4))))
    parts.mesh('Open shirt collar band', vertices, faces, mat, smooth=True)

    for side in (-1, 1):
        label = 'Left' if side < 0 else 'Right'
        outline = [(cx + side * 0.014, 0.180, 1.187),
                   (cx + side * 0.037, 0.160, 1.174),
                   (cx + side * 0.029, 0.130, 1.141),
                   (cx + side * 0.004, 0.150, 1.166)]
        leaf = _fitted_panel(parts, label + ' folded shirt collar', outline, mat, torso, offset=0.006)
        mesh = bmesh.new()
        mesh.from_mesh(leaf.data)
        band_edge = [Vector((cx + side * band_width * math.sin(angle),
                             cy - band_depth * math.cos(angle), 1.202))
                     for angle in (gap, gap + step)]
        # Follow the fitted leaf's real top-edge breakpoints so the rolled
        # portion joins it without gaps across torso facet boundaries.
        start = Vector((outline[0][0], outline[0][2]))
        direction = Vector((outline[1][0], outline[1][2])) - start
        boundary = []
        for vertex in mesh.verts:
            delta = Vector((vertex.co.x, vertex.co.z)) - start
            u = delta.dot(direction) / direction.length_squared
            if (-1e-6 <= u <= 1 + 1e-6
                    and abs(delta.x * direction.y - delta.y * direction.x) < 1e-8):
                boundary.append((max(0.0, min(1.0, u)), vertex.co.copy()))
        boundary.sort(key=lambda entry: entry[0])
        if len(boundary) < 2:
            raise ValueError(f'{label} collar fold has no fitted attachment edge')
        edges = [(band_edge[0].lerp(band_edge[1], u), point) for u, point in boundary]
        rows = []
        for row in range(5):
            t = row / 4
            strip = []
            for upper, lower in edges:
                point = upper.lerp(lower, t)
                point.y -= 0.0025 * math.sin(math.pi * t)
                hit, surface, normal, index = torso.ray_cast(
                    Vector((point.x, -1, point.z)), Vector((0, 1, 0)))
                if hit and row not in (0, 4):
                    point.y = min(point.y, surface.y - 0.006)
                strip.append(mesh.verts.new(point))
            rows.append(strip)
        for row in range(4):
            for column in range(len(edges) - 1):
                face = mesh.faces.new((rows[row][column], rows[row][column + 1],
                                       rows[row + 1][column + 1], rows[row + 1][column]))
                face.normal_update()
                if face.normal.y > 0:
                    face.normal_flip()
                face.smooth = True
        bmesh.ops.remove_doubles(mesh, verts=list(mesh.verts), dist=0.000001)
        mesh.normal_update()
        mesh.to_mesh(leaf.data)
        mesh.free()
        leaf.data.update()
        bpy.context.view_layer.objects.active = leaf
        leaf.select_set(True)
        thickness = leaf.modifiers.new('Folded shirt cloth thickness', 'SOLIDIFY')
        thickness.thickness = 0.0009
        thickness.offset = -1
        bpy.ops.object.modifier_apply(modifier=thickness.name)


def _hand(parts, name, wrist, inward, skin, nail):
    """Dorsal hand faces upward; distinct curved digits settle across the thigh."""
    x, y, z = wrist
    _loft(parts, name + ' skin palm', [
        ((x, y, z), 0.021, 0.015),
        ((x, y - 0.022, z - 0.007), 0.030, 0.016),
        ((x, y - 0.045, z - 0.014), 0.033, 0.015),
        ((x, y - 0.061, z - 0.021), 0.031, 0.012),
    ], skin, sides=10)
    # From little finger to index finger; the thumb is on the inward edge.
    for index, (offset, length) in enumerate(((-0.024, 0.043), (-0.008, 0.057),
                                             (0.008, 0.062), (0.024, 0.055))):
        base = Vector((x + inward * offset, y - 0.054, z - 0.019))
        radius = 0.0064 if index == 0 else 0.0070
        tip_y = base.y - length
        _loft(parts, name + ' skin finger ' + str(index + 1), [
            (base, radius * 1.05, radius),
            ((base.x, base.y - length * 0.32, base.z - 0.003), radius, radius),
            ((base.x + inward * 0.001, base.y - length * 0.66, base.z - 0.013),
             radius * 0.90, radius * 0.90),
            ((base.x + inward * 0.002, tip_y + 0.006, base.z - 0.030),
             radius * 0.78, radius * 0.80),
            ((base.x + inward * 0.002, tip_y, base.z - 0.036), 0.0032, 0.0035),
        ], skin, sides=6)
        # Tiny dorsal nail plates remain separate editable meshes, not painted fingers.
        nx = base.x + inward * 0.002
        parts.mesh(name + ' fingernail ' + str(index + 1), [
            (nx - 0.0040, tip_y + 0.014, base.z - 0.017),
            (nx + 0.0040, tip_y + 0.014, base.z - 0.017),
            (nx + 0.0034, tip_y + 0.004, base.z - 0.026),
            (nx - 0.0034, tip_y + 0.004, base.z - 0.026),
        ], [(0, 1, 2, 3)], nail)
    _loft(parts, name + ' skin thumb', [
        ((x + inward * 0.020, y - 0.015, z - 0.008), 0.012, 0.010),
        ((x + inward * 0.041, y - 0.025, z - 0.017), 0.010, 0.009),
        ((x + inward * 0.046, y - 0.040, z - 0.024), 0.008, 0.008),
        ((x + inward * 0.042, y - 0.054, z - 0.030), 0.006, 0.006),
        ((x + inward * 0.041, y - 0.058, z - 0.032), 0.003, 0.003),
    ], skin, sides=8)


def build_body(collection):
    """Return the unrigged body, head mount, and world-space neck base section."""
    parts = Parts('Hawking seated body', collection)
    suit = material('Hawking warm grey wool', (0.125, 0.110, 0.100), roughness=0.91)
    lapel = material('Hawking lapel wool', (0.135, 0.119, 0.109), roughness=0.84)
    fold = material('Hawking wool fold shadow', (0.060, 0.051, 0.045), roughness=0.94)
    shirt = material('Hawking off-white shirt', (0.400, 0.390, 0.370), roughness=0.82)
    collar = material('Hawking shirt collar', (0.430, 0.420, 0.400), roughness=0.80)
    skin = material('Hawking skin', (0.56, 0.34, 0.245), roughness=0.70)
    nail = material('Hawking natural nails', (0.64, 0.47, 0.38), roughness=0.48)
    buttons = material('Hawking dark horn buttons', (0.023, 0.026, 0.029), roughness=0.40)
    leather = material('Hawking dark shoe leather', (0.020, 0.024, 0.029), roughness=0.38)
    sole = material('Hawking shoe soles', (0.009, 0.012, 0.015), roughness=0.80)

    _loft(parts, 'Trouser seated pelvis', [
        ((0, 0.045, 0.582), 0.135, 0.123),
        ((0, 0.040, 0.610), 0.178, 0.141),
        ((0, 0.042, 0.661), 0.176, 0.136),
        ((0, 0.060, 0.715), 0.155, 0.103),
    ], suit, sides=16)

    # The chest/back forms a single section mesh, with a narrow older man's shoulders.
    torso = _loft(parts, 'Jacket shaped torso and skirt', [
        ((0, 0.073, 0.623), 0.183, 0.122),
        ((0, 0.079, 0.683), 0.177, 0.122),
        ((0, 0.088, 0.769), 0.148, 0.104),
        ((0, 0.127, 0.873), 0.146, 0.104),
        ((0, 0.166, 0.987), 0.165, 0.100),
        ((0, 0.205, 1.085), 0.190, 0.096),
        ((0, 0.222, 1.133), 0.168, 0.084),
        ((0.015, 0.231, 1.176), 0.079, 0.060),
        ((0.015, 0.232, 1.190), 0.046, 0.043),
    ], suit, sides=16)
    # The surface inside the open neckline is skin, not a wool cap across the neck.
    torso.data.materials.append(skin)
    torso.data.polygons[-1].material_index = 1

    _fitted_panel(parts, 'Visible neck at open shirt', [
        (-0.006, 0.190, 1.225), (-0.006, 0.190, 1.190), (0.015, 0.150, 1.162),
        (0.036, 0.190, 1.190), (0.036, 0.190, 1.225),
    ], skin, torso, offset=0.0025, allow_partial=True)
    _fitted_panel(parts, 'Open shirt front', [
        (-0.025, 0.180, 1.182), (-0.058, 0.107, 1.108),
        (-0.070, 0.062, 0.987), (-0.053, 0.018, 0.876),
        (-0.045, -0.027, 0.773), (0.059, -0.026, 0.768),
        (0.068, 0.020, 0.879), (0.079, 0.065, 0.987),
        (0.068, 0.110, 1.108), (0.055, 0.182, 1.182),
        (0.015, 0.152, 1.163),
    ], shirt, torso)
    _fitted_panel(parts, 'Shirt central placket', [
        (0.009, 0.143, 1.141), (0.005, 0.058, 0.986),
        (0.006, -0.030, 0.780), (0.018, -0.031, 0.780),
        (0.017, 0.057, 0.986), (0.022, 0.142, 1.141),
    ], collar, torso, offset=0.0045)
    for side in (-1, 1):
        _fitted_panel(parts, ('Left' if side < 0 else 'Right') + ' notched jacket lapel', [
            (side * 0.054, 0.178, 1.174), (side * 0.097, 0.155, 1.150),
            (side * 0.088, 0.126, 1.123), (side * 0.112, 0.110, 1.113),
            (side * 0.083, 0.048, 0.963), (side * 0.037, 0.016, 0.855),
            (side * 0.051, 0.064, 0.990), (side * 0.037, 0.116, 1.112),
        ], lapel, torso, offset=0.0055)
    _collar(parts, torso, collar)

    _panel(parts, 'Jacket breast welt pocket', [
        (-0.151, 0.089, 1.012), (-0.090, 0.064, 1.018),
        (-0.090, 0.062, 1.010), (-0.151, 0.087, 1.004),
    ], fold, thickness=0.0015)
    for side in (-1, 1):
        _panel(parts, ('Left' if side < 0 else 'Right') + ' jacket hip pocket flap', [
            (side * 0.073, -0.005, 0.789), (side * 0.138, 0.026, 0.795),
            (side * 0.140, 0.020, 0.769), (side * 0.077, -0.012, 0.765),
        ], suit)
    for z, y in ((0.859, 0.009), (0.787, -0.028)):
        parts.sphere('Jacket front horn button', (-0.034, y, z), (0.007, 0.003, 0.007),
                     buttons, segments=8, rings=4)
    for z, y in ((1.068, 0.098), (0.984, 0.054), (0.907, 0.023), (0.826, -0.014)):
        parts.sphere('Shirt small pearl button', (0.013, y, z), (0.0028, 0.0018, 0.0028),
                     collar, segments=6, rings=4)

    for side in (-1, 1):
        label = 'Left' if side < 0 else 'Right'
        x = side * 0.102
        _loft(parts, label + ' continuous bent trouser leg', [
            ((side * 0.091, 0.040, 0.652), 0.090, 0.087),
            ((x, -0.078, 0.653), 0.092, 0.085),
            ((x, -0.231, 0.638), 0.085, 0.081),
            ((x, -0.350, 0.610), 0.080, 0.077),
            ((x, -0.403, 0.570), 0.077, 0.074),
            ((x, -0.429, 0.500), 0.070, 0.070),
            ((x, -0.458, 0.385), 0.059, 0.061),
            ((x, -0.472, 0.270), 0.052, 0.054),
            ((x, -0.476, 0.221), 0.050, 0.051),
        ], suit, sides=12)
        # Low-profile cloth creases follow the existing knee silhouette.
        parts.tube(label + ' knee upper cloth fold', [
            (x - 0.057, -0.359, 0.667), (x, -0.366, 0.678),
            (x + 0.057, -0.378, 0.656),
        ], 0.0027, fold, sides=5)
        parts.tube(label + ' knee diagonal cloth fold', [
            (x - 0.051, -0.458, 0.554), (x + 0.006, -0.477, 0.537),
            (x + 0.049, -0.463, 0.515),
        ], 0.0023, fold, sides=5)
        parts.tube(label + ' trouser shin pressed crease', [
            (x, -0.500, 0.482), (x, -0.519, 0.383), (x, -0.527, 0.272),
        ], 0.0015, lapel, sides=4)
        _loft(parts, label + ' formal shoe upper', [
            ((x, -0.433, 0.183), 0.030, 0.029),
            ((x, -0.456, 0.189), 0.044, 0.044),
            ((x, -0.487, 0.191), 0.049, 0.047),
            ((x, -0.533, 0.186), 0.052, 0.040),
            ((x, -0.586, 0.176), 0.055, 0.027),
            ((x, -0.624, 0.173), 0.048, 0.024),
            ((x, -0.641, 0.172), 0.024, 0.015),
        ], leather, sides=12)
        _loft(parts, label + ' shoe sole', [
            ((x, -0.537, 0.140), 0.049, 0.104),
            ((x, -0.537, 0.145), 0.055, 0.111),
            ((x, -0.537, 0.154), 0.054, 0.110),
        ], sole, sides=12)
        parts.tube(label + ' shoe toe cap seam', [
            (x - 0.045, -0.584, 0.188), (x - 0.025, -0.590, 0.199),
            (x, -0.592, 0.203), (x + 0.025, -0.590, 0.199),
            (x + 0.045, -0.584, 0.188),
        ], 0.0010, sole, sides=4)
        for lace in range(3):
            y = -0.515 - lace * 0.012
            z = 0.229 - lace * 0.006
            parts.tube(label + ' shoe lace ' + str(lace + 1), [
                (x - 0.018, y + 0.003, z), (x + 0.018, y - 0.003, z),
            ], 0.0012, sole, sides=4)

    # Elbows stay at armrest height; unequal inward reaches avoid a mirrored mannequin pose.
    for side, wrist in ((-1, (-0.126, -0.229, 0.754)),
                        (1, (0.091, -0.219, 0.746))):
        label = 'Left' if side < 0 else 'Right'
        wx, wy, wz = wrist
        _loft(parts, label + ' continuous bent jacket sleeve', [
            ((side * 0.150, 0.210, 1.110), 0.061, 0.066),
            ((side * 0.187, 0.181, 1.098), 0.065, 0.066),
            ((side * 0.215, 0.125, 1.022), 0.061, 0.060),
            ((side * 0.231, 0.069, 0.936), 0.056, 0.054),
            ((side * 0.235, 0.012, 0.873), 0.052, 0.048),
            ((side * 0.225, -0.050, 0.850), 0.048, 0.043),
            ((side * 0.190, -0.125, 0.825), 0.044, 0.039),
            ((wx + side * 0.020, wy + 0.038, wz + 0.011), 0.036, 0.031),
            ((wx, wy + 0.012, wz + 0.003), 0.031, 0.027),
        ], suit, sides=12)
        _loft(parts, label + ' shirt cuff', [
            ((wx + side * 0.006, wy + 0.020, wz + 0.006), 0.027, 0.022),
            ((wx, wy, wz), 0.025, 0.020),
            ((wx - side * 0.004, wy - 0.010, wz - 0.003), 0.023, 0.017),
        ], collar, sides=10)
        _hand(parts, label + ' hand', (wx - side * 0.004, wy - 0.008, wz - 0.003),
              -side, skin, nail)
        parts.tube(label + ' elbow fabric compression fold', [
            (side * 0.258, -0.036, 0.902), (side * 0.233, -0.048, 0.910),
            (side * 0.204, -0.034, 0.908),
        ], 0.0028, fold, sides=5)
        parts.tube(label + ' forearm fabric fold', [
            (side * 0.221, -0.116, 0.855), (side * 0.192, -0.135, 0.863),
            (side * 0.168, -0.118, 0.858),
        ], 0.0024, fold, sides=5)
        for button in range(2):
            parts.sphere(label + ' sleeve button ' + str(button + 1),
                         (wx + side * 0.028, wy + 0.034 + button * 0.012, wz + 0.021),
                         (0.004, 0.004, 0.0025), buttons, segments=6, rings=4)

    mount = bpy.data.objects.new('Hawking head mount', None)
    collection.objects.link(mount)
    mount.parent = parts.root
    mount.location = (0.02, 0.22, 1.245)
    mount.rotation_euler = (0.0, 0.0, 0.0)
    mount.empty_display_type = 'ARROWS'
    mount.empty_display_size = 0.055
    return {'root': parts.root, 'head_mount': mount,
            'neck_base': ((0.014, 0.224, 1.168), 0.036, 0.032)}
