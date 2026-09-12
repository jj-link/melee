"""Shared mesh builders for the wheelchair, seated body, and head attachment."""
import math

import bpy
from mathutils import Vector


def material(name, color, metallic=0.0, roughness=0.65):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.diffuse_color = (*color[:3], 1)
    mat.use_nodes = True
    shader = mat.node_tree.nodes.get('Principled BSDF')
    shader.inputs['Base Color'].default_value = mat.diffuse_color
    shader.inputs['Metallic'].default_value = metallic
    shader.inputs['Roughness'].default_value = roughness
    return mat


class Parts:
    """Link named, independently editable parts beneath one assembly root."""

    def __init__(self, name, collection):
        self.collection = collection
        self.root = bpy.data.objects.new(name, None)
        collection.objects.link(self.root)

    def adopt(self, obj, name, mat=None, smooth=False):
        obj.name = name
        for collection in list(obj.users_collection):
            collection.objects.unlink(obj)
        self.collection.objects.link(obj)
        obj.parent = self.root
        if mat is not None:
            obj.data.materials.append(mat)
        if smooth:
            for polygon in obj.data.polygons:
                polygon.use_smooth = True
        return obj

    def mesh(self, name, vertices, faces, mat, smooth=False):
        mesh = bpy.data.meshes.new(name)
        mesh.from_pydata(vertices, [], faces)
        mesh.update()
        obj = bpy.data.objects.new(name, mesh)
        return self.adopt(obj, name, mat, smooth)

    def box(self, name, location, size, mat, bevel=0.0, rotation=(0, 0, 0)):
        """Box size is its full XYZ dimensions; rotation is Euler radians."""
        bpy.ops.mesh.primitive_cube_add(size=1, location=location)
        obj = self.adopt(bpy.context.object, name, mat)
        obj.scale = size
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
        if bevel:
            modifier = obj.modifiers.new('Edge bevel', 'BEVEL')
            modifier.width = bevel
            modifier.segments = 2
            bpy.ops.object.modifier_apply(modifier=modifier.name)
        obj.rotation_euler = rotation
        return obj

    def sphere(self, name, location, scale, mat, segments=16, rings=8):
        """Ellipsoid scale is its XYZ radii, not full dimensions."""
        bpy.ops.mesh.primitive_uv_sphere_add(segments=segments, ring_count=rings,
                                           radius=1, location=location)
        obj = self.adopt(bpy.context.object, name, mat, True)
        obj.scale = scale
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
        return obj

    def rod(self, name, start, end, radius, mat, vertices=10, end_radius=None):
        start, end = Vector(start), Vector(end)
        delta = end - start
        bpy.ops.mesh.primitive_cone_add(vertices=vertices, radius1=radius,
                                       radius2=radius if end_radius is None else end_radius,
                                       depth=delta.length, location=(start + end) * 0.5)
        obj = self.adopt(bpy.context.object, name, mat, True)
        obj.rotation_euler = delta.to_track_quat('Z', 'Y').to_euler()
        return obj

    def tube(self, name, points, radius, mat, sides=6, closed=False):
        points = [Vector(point) for point in points]
        verts, faces = [], []
        for i, point in enumerate(points):
            before = points[(i - 1) % len(points)] if closed or i else point
            after = points[(i + 1) % len(points)] if closed or i + 1 < len(points) else point
            tangent = (after - before).normalized()
            normal = tangent.cross(Vector((0, 0, 1)))
            if normal.length_squared < 0.001:
                normal = tangent.cross(Vector((1, 0, 0)))
            normal.normalize()
            binormal = tangent.cross(normal).normalized()
            for side in range(sides):
                angle = math.tau * side / sides
                verts.append(point + radius * (math.cos(angle) * normal + math.sin(angle) * binormal))
        for i in range(len(points) if closed else len(points) - 1):
            a, b = i * sides, ((i + 1) % len(points)) * sides
            for side in range(sides):
                nxt = (side + 1) % sides
                faces.append((a + side, a + nxt, b + nxt, b + side))
        if not closed:
            faces.extend([tuple(reversed(range(sides))), tuple(range(len(verts) - sides, len(verts)))])
        return self.mesh(name, verts, faces, mat, True)
