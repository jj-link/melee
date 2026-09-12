"""Render standalone Melee portraits from the approved, read-only Hawking prototype.

Run with Blender --background --python stephen-hawking/tools/render_interface_blender.py.
"""
from pathlib import Path

import bpy
from mathutils import Vector


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'prototype/stephen-hawking.blend'
OUT = ROOT / 'character'


def frame_meshes(scene, meshes, direction, size, margin):
    """Fit the complete silhouette, including glasses and chair peripherals."""
    scene.render.resolution_x, scene.render.resolution_y = size
    scene.render.resolution_percentage = 100
    scene.render.pixel_aspect_x = scene.render.pixel_aspect_y = 1
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    corners = []
    for obj in meshes:
        evaluated = obj.evaluated_get(depsgraph)
        corners.extend(evaluated.matrix_world @ Vector(corner) for corner in evaluated.bound_box)
    if not corners:
        raise ValueError('The approved prototype collection has no drawable meshes')
    low = Vector(tuple(min(point[axis] for point in corners) for axis in range(3)))
    high = Vector(tuple(max(point[axis] for point in corners) for axis in range(3)))
    center = (low + high) * .5
    direction = Vector(direction).normalized()
    rotation = (-direction).to_track_quat('-Z', 'Y')
    right = rotation @ Vector((1, 0, 0))
    up = rotation @ Vector((0, 1, 0))
    xs = [(point - center).dot(right) for point in corners]
    ys = [(point - center).dot(up) for point in corners]
    center += right * ((min(xs) + max(xs)) * .5) + up * ((min(ys) + max(ys)) * .5)
    camera = scene.camera
    camera.location = center + direction * 10
    camera.rotation_euler = rotation.to_euler()
    camera.data.type = 'ORTHO'
    camera.data.sensor_fit = 'VERTICAL'
    camera.data.shift_x = camera.data.shift_y = 0
    camera.data.dof.use_dof = False
    camera.data.clip_start = .01
    camera.data.clip_end = 100
    camera.data.ortho_scale = max(max(ys) - min(ys), (max(xs) - min(xs)) * size[1] / size[0]) / (1 - margin * 2)
    bpy.context.view_layer.update()


def render(scene, filename):
    scene.render.filepath = str(OUT / filename)
    bpy.ops.render.render(write_still=True)


def main():
    # Never save this scene: visibility and camera changes belong only to these renders.
    bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
    scene = bpy.context.scene
    model = bpy.data.collections['Stephen Hawking prototype']
    likeness = bpy.data.collections['Likeness and spectacles']
    model_meshes = [obj for obj in model.all_objects if obj.type == 'MESH']
    head_meshes = [obj for obj in likeness.all_objects if obj.type == 'MESH']
    if not model_meshes or not head_meshes:
        raise ValueError('The approved prototype is missing its model or likeness collection')
    if scene.camera is None:
        raise ValueError('The approved prototype is missing its review camera')
    OUT.mkdir(parents=True, exist_ok=True)

    # Keep the approved materials, world illumination, softboxes and color management.
    # Hiding only non-model geometry removes the studio floor without losing its lights.
    for obj in scene.objects:
        if obj.type == 'MESH':
            obj.hide_render = obj not in model_meshes
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.render.image_settings.color_depth = '8'
    scene.render.use_border = False
    scene.render.use_compositing = False
    scene.render.use_sequencer = False
    scene.cycles.samples = 128
    scene.cycles.use_denoising = True

    # A near-front three-quarter angle retains both the likeness and wheelchair shape.
    frame_meshes(scene, model_meshes, (1.5, -4.5, 1.15), (136, 188), .035)
    render(scene, 'hawking-portrait.png')

    # The head mesh already contains the approved hair, spectacles and unified neck.
    # Isolate that complete mesh instead of color-keying skin or clipping glasses away.
    for obj in model_meshes:
        obj.hide_render = obj not in head_meshes
    frame_meshes(scene, head_meshes, (1.0, -3.44, .44), (512, 512), .04)
    render(scene, 'hawking-head.png')
    print('Hawking CSP and head portrait ready under', OUT)


if __name__ == '__main__':
    main()
