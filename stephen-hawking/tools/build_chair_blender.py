"""Rigid, editable Hawking wheelchair study; dimensions are meters, front is -Y."""
import math

import bpy
from mathutils import Vector

from model_parts import Parts, material


MUSEUM_REFERENCE = (
    'https://coimages.sciencemuseumgroup.org.uk/512/74/'
    'large_e2021_0174_1__0001_.jpg'
)


def _box(parts, name, location, size, mat, bevel=0, rotation=(0, 0, 0)):
    """One-segment machined edges keep the chair inside its mesh budget."""
    obj = parts.box(name, location, size, mat, rotation=rotation)
    if bevel:
        modifier = obj.modifiers.new('Machined edge', 'BEVEL')
        modifier.width = min(bevel, min(size) * 0.45)
        modifier.segments = 1
        bpy.ops.object.modifier_apply(modifier=modifier.name)
    return obj


def _axial_profile(parts, name, center, profile, mat, segments=32):
    """Surface of revolution about local X, with a closed radial/axial profile."""
    vertices = []
    for x, radius in profile:
        for step in range(segments):
            angle = math.tau * step / segments
            vertices.append((x, math.sin(angle) * radius, math.cos(angle) * radius))
    faces = []
    for ring in range(len(profile)):
        nxt = (ring + 1) % len(profile)
        for step in range(segments):
            following = (step + 1) % segments
            faces.append((ring * segments + step, nxt * segments + step,
                          nxt * segments + following, ring * segments + following))
    obj = parts.mesh(name, vertices, faces, mat, smooth=True)
    obj.location = center
    return obj


def _parent_at_rest(obj, parent):
    """Keep primitive transforms intact while giving wheels a usable local axle."""
    # These meshes only have translation at the parent level. Avoid relying on a
    # dependency-graph update while the assembly is still being constructed.
    obj.location -= parent.location
    obj.parent = parent


def _cushion(parts, name, center, width, depth, height, mat, rotation=(0, 0, 0)):
    """Soft rectangular pad, with three tailored rounded-rectangle sections."""
    vertices = []
    for z, inset in ((-height / 2, 0.008), (0, 0), (height / 2, 0.012)):
        half_x, half_y = width / 2 - inset, depth / 2 - inset
        radius = min(0.024, half_x * 0.35, half_y * 0.35)
        for corner, (sx, sy) in enumerate(((1, 1), (-1, 1), (-1, -1), (1, -1))):
            for step in range(3):
                angle = corner * math.pi / 2 + step * math.pi / 4
                vertices.append((sx * (half_x - radius) + radius * math.cos(angle),
                                 sy * (half_y - radius) + radius * math.sin(angle), z))
    faces = [tuple(reversed(range(12))), tuple(range(24, 36))]
    for ring in range(2):
        for step in range(12):
            nxt = (step + 1) % 12
            faces.append((ring * 12 + step, ring * 12 + nxt,
                          (ring + 1) * 12 + nxt, (ring + 1) * 12 + step))
    obj = parts.mesh(name, vertices, faces, mat, smooth=True)
    # Keep broad pad faces flat rather than shading them like balloons.
    obj.data.polygons[0].use_smooth = False
    obj.data.polygons[1].use_smooth = False
    obj.location = center
    obj.rotation_euler = rotation
    return obj


def _spoke(parts, name, center, angle, sign, mat):
    """Broad tapered casting, not a bicycle spoke or a pile of cylinders."""
    vertices = []
    radial = Vector((0, math.sin(angle), math.cos(angle)))
    tangent = Vector((0, math.cos(angle), -math.sin(angle)))
    for x in (sign * 0.012, sign * 0.026):
        for radius, half_width in ((0.025, 0.021), (0.145, 0.014)):
            for side in (-1, 1):
                vertices.append(Vector(center) + Vector((x, 0, 0))
                                + radial * radius + tangent * half_width * side)
    faces = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1),
             (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    if sign > 0:
        faces = [tuple(reversed(face)) for face in faces]
    return parts.mesh(name, vertices, faces, mat)


def build_chair(collection) -> bpy.types.Object:
    parts = Parts('Stephen Hawking | Motorized wheelchair', collection)
    root = parts.root
    root['supplementary_museum_reference'] = MUSEUM_REFERENCE
    root['reference_note'] = ('Gray/black upholstery and monitor arrangement follow user photo; '
                              'unseen lower chassis is approximated from the museum 2016 F3.')
    root['front_axis'] = '-Y'
    root['seat_top_m'] = 0.57
    root['drive_axle_local_axis'] = 'X'
    root['approximate_triangle_budget'] = '5000-6500; assess evaluated meshes during scene integration'
    upholstery = material('Chair charcoal woven upholstery', (0.035, 0.040, 0.047), roughness=0.88)
    piping = material('Chair upholstery piping', (0.064, 0.070, 0.078), roughness=0.83)
    rubber = material('Chair graphite rubber', (0.022, 0.027, 0.032), roughness=0.86)
    shell = material('Chair satin charcoal shell', (0.055, 0.064, 0.075), metallic=0.22, roughness=0.43)
    metal = material('Chair satin steel', (0.30, 0.33, 0.36), metallic=0.75, roughness=0.33)
    dark_metal = material('Chair dark steel brackets', (0.08, 0.092, 0.105), metallic=0.65, roughness=0.4)
    hub_mat = material('Chair brushed wheel castings', (0.21, 0.235, 0.255), metallic=0.7, roughness=0.4)
    screen_mat = material('Communication screen', (0.075, 0.105, 0.125), roughness=0.32)

    # Broad enclosure, rectangular load-bearing rails, and seat lift hardware.
    _box(parts, 'Battery and controller enclosure', (0, 0.105, 0.285), (0.405, 0.46, 0.205), shell, bevel=0.023)
    _box(parts, 'Battery lower protective tray', (0, 0.105, 0.185), (0.43, 0.465, 0.026), dark_metal, bevel=0.006)
    _box(parts, 'Seat lift pedestal', (0, 0.095, 0.416), (0.155, 0.18, 0.095), dark_metal, bevel=0.008)
    _box(parts, 'Seat pan steel plate', (0, 0.015, 0.488), (0.455, 0.465, 0.026), metal, bevel=0.007)
    _box(parts, 'Rear battery access lid', (0, 0.341, 0.296), (0.30, 0.009, 0.135), dark_metal, bevel=0.005)
    _box(parts, 'Rear battery lid grip', (0, 0.349, 0.333), (0.092, 0.015, 0.015), rubber)
    for side in (-1, 1):
        label = 'Left' if side < 0 else 'Right'
        x = side * 0.224
        _box(parts, label + ' longitudinal chassis rail', (x, 0.06, 0.30), (0.034, 0.57, 0.052), dark_metal)
        parts.rod(label + ' drive gearbox', (side * 0.17, -0.19, 0.205),
                  (side * 0.254, -0.19, 0.205), 0.061, dark_metal, vertices=16)
        parts.rod(label + ' horizontal drive motor', (side * 0.155, -0.17, 0.225),
                  (side * 0.155, 0.05, 0.225), 0.048, shell, vertices=12)
        for index in range(5):
            _box(parts, label + ' enclosure vent %02d' % index,
                      (side * 0.2035, 0.035 + index * 0.031, 0.316),
                      (0.004, 0.018, 0.055), rubber)
        parts.rod(label + ' seat tilt link', (x, -0.12, 0.35),
                  (x, 0.19, 0.47), 0.021, dark_metal, vertices=8)
        parts.rod(label + ' tilt pivot cap', (side * 0.235, 0.18, 0.46),
                  (side * 0.246, 0.18, 0.46), 0.027, metal, vertices=12)

        # Tire and rim profiles have constant radius around a true X axle.
        center = (side * 0.288, -0.19, 0.205)
        wheel = _axial_profile(parts, label + ' drive tire | rotate local X', center,
                               [(-0.040, 0.153), (-0.042, 0.183), (-0.028, 0.201),
                                (0.028, 0.201), (0.042, 0.183), (0.040, 0.153)], rubber)
        wheel['rotation_axis'] = 'LOCAL_X'
        rim = _axial_profile(parts, label + ' drive alloy rim', center,
                             [(-0.030, 0.136), (-0.030, 0.156),
                              (0.030, 0.156), (0.030, 0.136)], hub_mat, segments=24)
        _parent_at_rest(rim, wheel)
        for spoke in range(5):
            obj = _spoke(parts, label + ' cast spoke %02d' % spoke,
                         center, math.tau * spoke / 5 + 0.25, side, hub_mat)
            _parent_at_rest(obj, wheel)
        hub = parts.rod(label + ' drive hub', (side * 0.26, -0.19, 0.205),
                        (side * 0.335, -0.19, 0.205), 0.041, dark_metal, vertices=16)
        _parent_at_rest(hub, wheel)
        cap = parts.rod(label + ' axle dust cap', (side * 0.335, -0.19, 0.205),
                        (side * 0.34, -0.19, 0.205), 0.024, hub_mat, vertices=12)
        _parent_at_rest(cap, wheel)

        # Rear caster forks visibly connect to the rear of the chassis.
        parts.rod(label + ' caster swing arm', (x, 0.16, 0.30),
                  (side * 0.285, 0.34, 0.265), 0.027, dark_metal, vertices=8)
        parts.rod(label + ' caster swivel', (side * 0.285, 0.34, 0.22),
                  (side * 0.285, 0.34, 0.285), 0.022, metal, vertices=12)
        caster_center = (side * 0.285, 0.365, 0.11)
        caster = _axial_profile(parts, label + ' rear caster tire | rotate local X', caster_center,
                                [(-0.026, 0.052), (-0.028, 0.09), (-0.016, 0.107),
                                 (0.016, 0.107), (0.028, 0.09), (0.026, 0.052)], rubber, segments=20)
        caster['rotation_axis'] = 'LOCAL_X'
        disc = parts.rod(label + ' caster wheel hub',
                         (side * 0.285 - 0.027, 0.365, 0.11),
                         (side * 0.285 + 0.027, 0.365, 0.11), 0.059, hub_mat, vertices=20)
        _parent_at_rest(disc, caster)
        for fork_side in (-1, 1):
            fork_x = side * 0.285 + fork_side * 0.035
            parts.tube(label + ' caster fork ' + str(fork_side),
                       [(side * 0.285, 0.34, 0.235), (fork_x, 0.34, 0.22),
                        (fork_x, 0.365, 0.11)], 0.013, dark_metal, sides=6)
        parts.rod(label + ' caster axle pin', (side * 0.285 - 0.049, 0.365, 0.11),
                  (side * 0.285 + 0.049, 0.365, 0.11), 0.014, metal, vertices=10)

    _cushion(parts, 'Seat cushion', (0, 0, 0.535), 0.442, 0.45, 0.07, upholstery)
    parts.tube('Seat tailored front seam',
               [(-0.203, -0.21, 0.535), (-0.185, -0.225, 0.535),
                (0.185, -0.225, 0.535), (0.203, -0.21, 0.535)], 0.0026, piping, sides=5)
    recline = math.radians(-13)
    _box(parts, 'Reclined rigid back shell', (0, 0.340, 0.955),
              (0.435, 0.04, 0.615), shell, bevel=0.015, rotation=(recline, 0, 0))
    _cushion(parts, 'Contoured back upholstery', (0, 0.308, 0.955),
             0.413, 0.615, 0.065, upholstery,
             rotation=(math.pi / 2 + recline, 0, 0))
    # Sewn edges trace the front of the tilted pad without competing with clothes.
    for side in (-1, 1):
        x = side * 0.192
        parts.tube(('Left' if side < 0 else 'Right') + ' back bolster seam',
                   [(x, 0.217, 0.685), (x, 0.277, 0.94), (x, 0.333, 1.20)],
                   0.0025, piping, sides=5)
        parts.tube(('Left' if side < 0 else 'Right') + ' back steel rail',
                   [(side * 0.215, 0.14, 0.475), (side * 0.215, 0.30, 0.70),
                    (side * 0.215, 0.425, 1.205)], 0.017, metal, sides=8)
    parts.rod('Back recline actuator housing', (0, 0.265, 0.34),
              (0, 0.378, 0.74), 0.028, dark_metal, vertices=12)
    parts.rod('Back recline actuator piston', (0, 0.378, 0.74),
              (0, 0.427, 0.96), 0.012, metal, vertices=10)

    # A shared backrest bracket carries both the actuator pivot and headrest
    # clamps. Its crossbrace terminates on the two existing rear steel rails.
    brace_z = 1.025
    rail_y = 0.30 + (brace_z - 0.70) * (0.425 - 0.30) / (1.205 - 0.70)
    parts.tube('Rear backrest mounting crossbrace',
               [(-0.215, rail_y, brace_z), (-0.165, 0.4165, brace_z),
                (0.165, 0.4165, brace_z), (0.215, rail_y, brace_z)],
               0.013, dark_metal, sides=8)
    spine_angle = -math.atan2(0.025, 0.25)
    _box(parts, 'Headrest to backrest mounting spine', (0, 0.4225, 1.075),
         (0.064, 0.020, math.hypot(0.025, 0.25)), dark_metal,
         bevel=0.003, rotation=(spine_angle, 0, 0))
    for side in (-1, 1):
        _box(parts, ('Left' if side < 0 else 'Right') + ' actuator mounting clevis',
             (side * 0.022, 0.427, 0.965), (0.012, 0.037, 0.034), dark_metal, bevel=0.003)
    parts.rod('Backrest actuator pivot pin', (-0.033, 0.427, 0.960),
              (0.033, 0.427, 0.960), 0.007, metal, vertices=10)
    _box(parts, 'Lower headrest post clamp', (0, 0.433, 1.080),
         (0.064, 0.044, 0.040), dark_metal, bevel=0.004)
    for z in (1.010, 1.164):
        y = 0.410 + (z - 0.950) * 0.10 + 0.014
        for side in (-1, 1):
            parts.rod('Backrest mounting bolt %.3f %d' % (z, side),
                      (side * 0.022, y - 0.006, z), (side * 0.022, y + 0.005, z),
                      0.0045, metal, vertices=6)

    # Thin, rear-mounted headrest: no cheek wings obscuring the generated face.
    parts.tube('Headrest adjustable steel stem',
               [(0, 0.43, 1.07), (0, 0.454, 1.25), (-0.025, 0.443, 1.32)],
               0.014, metal, sides=8)
    _box(parts, 'Headrest adjustment clamp', (0, 0.443, 1.19), (0.07, 0.05, 0.052), dark_metal, bevel=0.005)
    _box(parts, 'Headrest backing plate', (-0.025, 0.439, 1.324),
              (0.249, 0.024, 0.25), shell, bevel=0.014, rotation=(recline, 0, 0))
    _cushion(parts, 'Broad black headrest', (-0.025, 0.414, 1.324),
             0.277, 0.275, 0.055, upholstery,
             rotation=(math.pi / 2 + recline, 0, 0))

    for side in (-1, 1):
        label = 'Left' if side < 0 else 'Right'
        x = side * 0.28
        parts.tube(label + ' armrest support frame',
                   [(side * 0.228, 0.20, 0.50), (x, 0.20, 0.75),
                    (x, -0.225, 0.75)], 0.016, dark_metal, sides=8)
        parts.rod(label + ' front armrest strut', (side * 0.225, -0.15, 0.49),
                  (x, -0.17, 0.75), 0.013, metal, vertices=8)
        _box(parts, label + ' armrest underside', (x, -0.004, 0.777),
                  (0.085, 0.44, 0.028), dark_metal, bevel=0.005)
        _cushion(parts, label + ' armrest pad', (x, -0.004, 0.804),
                 0.096, 0.444, 0.032, upholstery)
        _box(parts, label + ' side skirt', (side * 0.244, 0.055, 0.667),
                  (0.015, 0.23, 0.16), shell, bevel=0.012)
        _box(parts, label + ' pale side pouch flap', (side * 0.254, 0.065, 0.689),
                  (0.008, 0.18, 0.10), piping, bevel=0.006)
        # Calf support stays behind the contracted ankle-to-knee centerline.
        parts.tube(label + ' drop leg support',
                   [(side * 0.18, -0.185, 0.485), (side * 0.17, -0.31, 0.43),
                    (side * 0.17, -0.43, 0.145), (side * 0.10, -0.53, 0.125)],
                   0.016, metal, sides=8)
        _box(parts, label + ' calf backing', (side * 0.105, -0.373, 0.33),
                  (0.15, 0.027, 0.17), dark_metal, rotation=(math.radians(12), 0, 0))
        _cushion(parts, label + ' calf support pad', (side * 0.105, -0.397, 0.33),
                 0.145, 0.17, 0.04, upholstery,
                 rotation=(math.pi / 2 + math.radians(12), 0, 0))
        _box(parts, label + ' footplate', (side * 0.105, -0.535, 0.127),
                  (0.187, 0.245, 0.017), metal, bevel=0.005)
        _box(parts, label + ' footplate nonslip surface', (side * 0.105, -0.535, 0.138),
                  (0.17, 0.225, 0.006), rubber)
        parts.tube(label + ' rear heel stop',
                   [(side * 0.105 - 0.083, -0.44, 0.155),
                    (side * 0.105 - 0.073, -0.427, 0.176),
                    (side * 0.105 + 0.073, -0.427, 0.176),
                    (side * 0.105 + 0.083, -0.44, 0.155)], 0.008, rubber, sides=6)

    _box(parts, 'Joystick controller pod', (-0.28, -0.275, 0.818),
              (0.096, 0.122, 0.052), shell, bevel=0.015, rotation=(math.radians(-10), 0, 0))
    parts.rod('Joystick flexible boot', (-0.28, -0.265, 0.84),
              (-0.28, -0.265, 0.863), 0.021, rubber, vertices=12, end_radius=0.012)
    parts.rod('Joystick stem', (-0.28, -0.265, 0.86),
              (-0.28, -0.267, 0.898), 0.006, dark_metal, vertices=8)
    parts.sphere('Joystick grip', (-0.28, -0.268, 0.900), (0.016, 0.018, 0.012),
                 rubber, segments=12, rings=6)
    for x in (-0.302, -0.28, -0.258):
        _box(parts, 'Recessed controller key', (x, -0.307, 0.846), (0.012, 0.013, 0.003), rubber)

    # Right-side articulated monitor boom is deliberately outside the knees.
    _box(parts, 'Monitor boom seat clamp', (0.255, -0.165, 0.507),
              (0.07, 0.067, 0.085), dark_metal, bevel=0.005)
    parts.rod('Monitor lower hinge axle', (0.235, -0.165, 0.525),
              (0.315, -0.165, 0.525), 0.031, hub_mat, vertices=16)
    parts.tube('Monitor curved upright',
               [(0.30, -0.165, 0.53), (0.34, -0.24, 0.73),
                (0.36, -0.32, 0.85), (0.36, -0.39, 0.97)],
               0.016, metal, sides=10)
    parts.rod('Monitor upper articulation hinge', (0.345, -0.39, 0.97),
              (0.385, -0.39, 0.97), 0.03, hub_mat, vertices=16)
    parts.rod('Monitor horizontal articulated arm', (0.36, -0.435, 0.97),
              (0.25, -0.46, 0.97), 0.018, dark_metal, vertices=10)
    parts.rod('Monitor upper pivot fastener', (0.385, -0.39, 0.97),
              (0.39, -0.39, 0.97), 0.012, dark_metal, vertices=10)
    _box(parts, 'Monitor rear mounting bracket', (0.26, -0.455, 0.996),
              (0.075, 0.043, 0.09), dark_metal, bevel=0.005)
    _box(parts, 'Communication monitor housing', (0.26, -0.427, 1.035),
              (0.35, 0.046, 0.304), shell, bevel=0.012)
    _box(parts, 'Monitor occupant-facing bezel', (0.26, -0.400, 1.039),
              (0.328, 0.008, 0.277), rubber, bevel=0.006)
    # A single correctly oriented +Y face, with explicit full-frame texture UVs.
    screen = parts.mesh('Communication display surface',
                        [(0.111, -0.395, 0.929), (0.111, -0.395, 1.153),
                         (0.409, -0.395, 1.153), (0.409, -0.395, 0.929)],
                        [(0, 1, 2, 3)], screen_mat)
    uv = screen.data.uv_layers.new(name='Screen UV')
    for loop, coord in zip(uv.data, ((1, 0), (1, 1), (0, 1), (0, 0))):
        loop.uv = coord
    screen['faces_occupant'] = '+Y'
    _box(parts, 'Monitor rear electronics enclosure', (0.26, -0.457, 1.055),
              (0.218, 0.024, 0.108), dark_metal, bevel=0.007)
    for index in range(6):
        _box(parts, 'Monitor rear ventilation slot %02d' % index,
                  (0.19 + index * 0.026, -0.470, 1.055), (0.012, 0.003, 0.049), rubber)
    _box(parts, 'Monitor lower status indicator', (0.39, -0.395, 0.916),
              (0.012, 0.002, 0.003), metal)

    parts.tube('Monitor signal and power loom',
               [(0.345, -0.464, 1.03), (0.392, -0.466, 1.015),
                (0.396, -0.437, 0.94), (0.369, -0.373, 0.90),
                (0.345, -0.305, 0.82), (0.33, -0.232, 0.66),
                (0.294, -0.174, 0.50), (0.238, -0.05, 0.36)],
               0.005, rubber, sides=6)
    parts.tube('Joystick controller cable',
               [(-0.29, -0.296, 0.796), (-0.304, -0.258, 0.725),
                (-0.282, -0.12, 0.717), (-0.245, 0.08, 0.60),
                (-0.231, 0.15, 0.345)], 0.0045, rubber, sides=6)
    parts.tube('Rear actuator wiring loom',
               [(-0.07, 0.352, 0.31), (-0.09, 0.381, 0.51),
                (-0.06, 0.428, 0.73), (-0.06, 0.45, 0.94),
                (0, 0.432, 1.07)], 0.005, rubber, sides=6)
    return root
