using System;
using System.Runtime.CompilerServices;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Game;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Game;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Processors;
using Stride.Graphics;
using Stride.Graphics.Semantics;
using Stride.Rendering;

namespace Stride.Roads.Editor;

/// <summary>
/// Where the mouse points at in the scene editor: on the model under it (as the editor's picking finds it), else on
/// the ground plane (y = 0). The model's triangles are read back once per mesh and kept, as reading is slow.
/// </summary>
internal sealed class SurfacePicker
{
    private sealed class Triangles
    {
        public required Vector3[] Vertices;
        public required int[] Indices;
    }

    private static readonly ConditionalWeakTable<Mesh, Triangles> Cache = new();

    private readonly EntityHierarchyEditorGame game;
    private readonly IEditorGameEntitySelectionService selection;
    private readonly IEditorGameCameraService camera;

    public SurfacePicker(EntityHierarchyEditorGame game, IEditorGameEntitySelectionService selection, IEditorGameCameraService camera)
    {
        this.game = game;
        this.selection = selection;
        this.camera = camera;
    }

    /// <summary>The point in the world the mouse, at this place in the viewport (0 to 1 each way), is over.</summary>
    public Vector3 PointAt(Vector2 mouse)
    {
        var ray = RayAt(mouse);
        if (ray is { } r && selection.Pick().Entity is { } entity && entity.Get<ModelComponent>() is { Model: { } model } component
            && Hit(r, component, model) is { } hit)
            return hit;
        return game.GetPositionInScene(mouse);
    }

    /// <summary>The camera's ray through this place in the viewport, as the editor makes it.</summary>
    private Ray? RayAt(Vector2 mouse)
    {
        if (camera.Component is not { } cam)
            return null;
        var view = Matrix.Invert(camera.ViewMatrix);
        var screen = new Vector2(2f * (mouse.X - 0.5f), -2f * (mouse.Y - 0.5f));
        if (cam.Projection == CameraProjectionMode.Perspective)
        {
            var fov = MathUtil.DegreesToRadians(cam.VerticalFieldOfView);
            var direction = Vector3.Normalize(new Vector3(cam.AspectRatio * screen.X, screen.Y, -1 / MathF.Tan(fov / 2f)));
            return new Ray(view.TranslationVector, Vector3.Normalize(Vector3.TransformNormal(direction, view)));
        }
        var half = cam.OrthographicSize / 2f;
        var origin = Vector3.TransformCoordinate(new Vector3(screen.X * cam.AspectRatio * half, screen.Y * half, 0), view);
        return new Ray(origin, Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, view)));
    }

    /// <summary>The nearest point where the ray meets the model's triangles, in the world; null if it misses them all.</summary>
    private Vector3? Hit(Ray ray, ModelComponent component, Model model)
    {
        var entityWorld = component.Entity.Transform.WorldMatrix;
        float? nearest = null;
        foreach (var mesh in model.Meshes)
        {
            if (mesh.Draw?.PrimitiveType != PrimitiveType.TriangleList || mesh.Draw.IndexBuffer == null || mesh.Draw.VertexBuffers is not { Length: > 0 })
                continue;
            var triangles = Read(mesh);
            if (triangles == null)
                continue;

            // In the mesh's own space: its node's (with the entity's) transform, undone on the ray.
            var world = component.Skeleton?.NodeTransformations is { } nodes && mesh.NodeIndex < nodes.Length
                ? nodes[mesh.NodeIndex].WorldMatrix
                : entityWorld;
            var toMesh = Matrix.Invert(world);
            var start = Vector3.TransformCoordinate(ray.Position, toMesh);
            var end = Vector3.TransformCoordinate(ray.Position + ray.Direction, toMesh);
            var local = new Ray(start, end - start);

            var v = triangles.Vertices;
            var i = triangles.Indices;
            for (var t = 0; t + 2 < i.Length; t += 3)
            {
                if (!Intersect(local, v[i[t]], v[i[t + 1]], v[i[t + 2]], out var along))
                    continue;
                // 'along' is in units of the ray's direction, which the transform left the world's length: the
                // same in the world, so hits from different meshes compare.
                if (nearest == null || along < nearest)
                    nearest = along;
            }
        }
        return nearest is { } d ? ray.Position + ray.Direction * d : null;
    }

    private Triangles? Read(Mesh mesh)
    {
        if (Cache.TryGetValue(mesh, out var known))
            return known;
        try
        {
            mesh.Draw.VertexBuffers[0].AsReadable(game.Services, out var vertexHelper, out var vertexCount);
            mesh.Draw.IndexBuffer.AsReadable(game.Services, out var indexHelper, out var indexCount);
            var vertices = new Vector3[vertexCount];
            vertexHelper.Copy<PositionSemantic, Vector3>(vertices);
            var indices = new int[indexCount];
            indexHelper.CopyTo(indices.AsSpan());
            var triangles = new Triangles { Vertices = vertices, Indices = indices };
            Cache.AddOrUpdate(mesh, triangles);
            return triangles;
        }
        catch (Exception)
        {
            // A mesh that can't be read back (no position, an odd format) is just not hit.
            return null;
        }
    }

    /// <summary>Möller–Trumbore: whether the ray meets the triangle ahead of its start, and how far along its direction.</summary>
    private static bool Intersect(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float along)
    {
        along = 0f;
        var ab = b - a;
        var ac = c - a;
        var p = Vector3.Cross(ray.Direction, ac);
        var det = Vector3.Dot(ab, p);
        if (MathF.Abs(det) < 1e-9f)
            return false;
        var inv = 1f / det;
        var s = ray.Position - a;
        var u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f)
            return false;
        var q = Vector3.Cross(s, ab);
        var w = Vector3.Dot(ray.Direction, q) * inv;
        if (w < 0f || u + w > 1f)
            return false;
        along = Vector3.Dot(ac, q) * inv;
        return along > 1e-4f;
    }
}
