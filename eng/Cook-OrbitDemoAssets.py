"""Run in Blender to cook the two bounded Boulder 01 meshes."""

import json
import struct
import sys
from pathlib import Path

import bmesh
import bpy


def wyu_position(vertex):
    # Blender Z-up becomes WYU Y-up by a rotation. Keep the authored scale.
    return vertex.co.x, vertex.co.z, -vertex.co.y


def write_lod(rock, origin, destination, triangle_limit):
    mesh = rock.data.copy()
    lod = bpy.data.objects.new("Boulder 01 LOD", mesh)
    bpy.context.collection.objects.link(lod)
    bpy.context.view_layer.objects.active = lod
    modifier = lod.modifiers.new("Orbit demo decimation", "DECIMATE")
    modifier.decimate_type = "COLLAPSE"
    mesh.calc_loop_triangles()
    modifier.ratio = (triangle_limit - 4) / len(mesh.loop_triangles)
    bpy.ops.object.modifier_apply(modifier=modifier.name)
    lod.data.calc_loop_triangles()
    triangles = lod.data.loop_triangles
    if not 0 < len(triangles) <= triangle_limit:
        raise ValueError(f"Invalid triangle count for {destination.name}: {len(triangles)}")

    center_x, bottom, center_z = origin
    with destination.open("wb") as output:
        output.write(struct.pack("<4sIII", b"WORB", 1, len(lod.data.vertices), len(triangles) * 3))
        for vertex in lod.data.vertices:
            x, y, z = wyu_position(vertex)
            output.write(struct.pack("<fff", x - center_x, y - bottom, z - center_z))
        for triangle in triangles:
            output.write(struct.pack("<III", *triangle.vertices))
    print(f"{destination.name}: {len(lod.data.vertices)} vertices, {len(triangles)} triangles")


def cook(source, destination):
    # WP16 uses vertex/instance colors. Import geometry only so the cooker has no
    # dependency on the original glTF material maps.
    document = json.loads(source.read_text(encoding="utf-8"))
    primitive = document["meshes"][0]["primitives"][0]
    primitive["attributes"] = {"POSITION": primitive["attributes"]["POSITION"]}
    primitive.pop("material", None)
    for key in ("materials", "textures", "images", "samplers"):
        document.pop(key, None)
    geometry = source.with_name("boulder_01_geometry.gltf")
    geometry.write_text(json.dumps(document), encoding="utf-8")

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(geometry), merge_vertices=True)
    rock, = [item for item in bpy.context.scene.objects if item.type == "MESH"]
    bpy.context.view_layer.objects.active = rock
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    editable = bmesh.new()
    editable.from_mesh(rock.data)
    bmesh.ops.remove_doubles(editable, verts=list(editable.verts), dist=0.00001)
    editable.to_mesh(rock.data)
    editable.free()
    x, y, z = zip(*(wyu_position(vertex) for vertex in rock.data.vertices))
    origin = (min(x) + max(x)) / 2, min(y), (min(z) + max(z)) / 2
    write_lod(rock, origin, destination / "boulder_01_lod0.bin", 2500)
    write_lod(rock, origin, destination / "boulder_01_lod1.bin", 500)
    geometry.unlink()


if __name__ == "__main__":
    model_path, output_folder = (Path(arg).resolve() for arg in sys.argv[sys.argv.index("--") + 1 :])
    cook(model_path, output_folder)
