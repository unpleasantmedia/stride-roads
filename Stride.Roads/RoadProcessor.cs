using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Buffer = Stride.Graphics.Buffer;
using NVector3 = System.Numerics.Vector3;

namespace Stride.Roads;

/// <summary>
/// Builds each <see cref="RoadComponent"/>'s model from its points whenever they or its settings
/// change. It runs in Game Studio's scene editor as well as the game (the component asks for both
/// execution modes), so a road reshapes as its points are dragged. What it builds sits on a child
/// entity of its own, made here and never saved.
/// </summary>
public sealed class RoadProcessor : EntityProcessor<RoadComponent, RoadProcessor.Built>
{
    /// <summary>The name of the entity holding what it builds.</summary>
    public const string BuiltName = "Road (built)";

    public sealed class Built
    {
        public Entity? Holder;
        public Model? Model;
        public int? State;
    }

    private IGraphicsDeviceService? _graphics;

    protected override void OnSystemAdd() => _graphics = Services.GetService<IGraphicsDeviceService>();

    protected override Built GenerateComponentData(Entity entity, RoadComponent component) => new();

    protected override bool IsAssociatedDataValid(Entity entity, RoadComponent component, Built associatedData) => true;

    protected override void OnEntityComponentRemoved(Entity entity, RoadComponent component, Built data)
    {
        if (data.Holder != null)
            entity.RemoveChild(data.Holder);
        Release(data.Model);
        component.Frames = Array.Empty<RoadFrame>();
    }

    public override void Update(GameTime time)
    {
        foreach (var (component, data) in ComponentDatas)
        {
            var state = State(component);
            if (state == data.State)
                continue;
            data.State = state;
            Rebuild(component, data);
        }
    }

    /// <summary>The points a road runs through, in order: its entity's children but what it builds.</summary>
    public static List<Entity> Points(RoadComponent road) =>
        road.Entity.Transform.Children.Select(t => t.Entity).Where(e => e.Name != BuiltName).ToList();

    /// <summary>Everything a road is built from, hashed: its points' places and settings, and its own.</summary>
    private static int State(RoadComponent road)
    {
        var hash = new HashCode();
        hash.Add(road.Section);
        hash.Add(road.Closed);
        hash.Add(road.SidewalkFalloff);
        hash.Add(road.SurfaceMaterial);
        hash.Add(road.WhiteLineMaterial);
        hash.Add(road.YellowLineMaterial);
        hash.Add(road.SidewalkMaterial);
        hash.Add(road.BankMaterial);
        foreach (var point in Points(road))
        {
            hash.Add(point.Transform.Position);
            hash.Add(point.Transform.Rotation);
            if (point.Get<RoadPointComponent>() is { } settings)
            {
                hash.Add(settings.FollowRotation);
                hash.Add(settings.Handle);
            }
        }
        return hash.ToHashCode();
    }

    private void Rebuild(RoadComponent road, Built data)
    {
        var points = Points(road);
        var device = _graphics?.GraphicsDevice;
        if (points.Count < 2 || road.Section.Lanes == 0 || device == null)
        {
            road.Frames = Array.Empty<RoadFrame>();
            if (data.Holder != null)
                data.Holder.EnableAll(false, true);
            return;
        }

        var curve = new RoadCurve(points.Select(ToCurvePoint).ToList(), road.Closed);
        road.Frames = curve.Frames(RoadComponent.Spacing);
        var meshes = RoadMeshBuilder.Build(road.Frames, road.Section, curve.Closed, road.SidewalkFalloff);

        var parts = new[] { meshes.Surface, meshes.WhiteLines, meshes.YellowLines, meshes.Banks, meshes.Sidewalks };
        var materials = new[] { road.SurfaceMaterial, road.WhiteLineMaterial, road.YellowLineMaterial, road.BankMaterial, road.SidewalkMaterial };
        var model = new Model();
        var bounds = BoundingBox.Empty;
        foreach (var index in Enumerable.Range(0, parts.Length).Where(i => !parts[i].IsEmpty))
        {
            var mesh = ToMesh(device, parts[index]);
            mesh.MaterialIndex = model.Materials.Count;
            model.Materials.Add(new MaterialInstance(materials[index]));
            model.Meshes.Add(mesh);
            BoundingBox.Merge(ref bounds, ref mesh.BoundingBox, out bounds);
        }
        model.BoundingBox = bounds;
        model.BoundingSphere = BoundingSphere.FromBox(bounds);

        if (data.Holder == null)
        {
            data.Holder = new Entity(BuiltName) { new ModelComponent() };
            road.Entity.AddChild(data.Holder);
        }
        data.Holder.Get<ModelComponent>()!.Model = model;
        data.Holder.EnableAll(true, true);
        Release(data.Model);
        data.Model = model;
    }

    /// <summary>A point as the road's maths takes it, in metres in its road's space.</summary>
    private static CurvePoint ToCurvePoint(Entity point)
    {
        var at = point.Transform.Position;
        var settings = point.Get<RoadPointComponent>();
        NVector3? tangent = null;
        if (settings is { FollowRotation: true })
        {
            var forward = Vector3.Transform(Vector3.UnitZ * -1f, point.Transform.Rotation);
            tangent = new NVector3(forward.X, forward.Y, forward.Z);
        }
        return new CurvePoint(new NVector3(at.X, at.Y, at.Z), tangent, settings?.Handle ?? 1f / 3f);
    }

    private static Mesh ToMesh(GraphicsDevice device, MeshData part)
    {
        var vertices = new VertexPositionNormalTexture[part.Vertices.Count];
        for (var i = 0; i < vertices.Length; i++)
        {
            var (v, n, uv) = (part.Vertices[i], part.Normals[i], part.UVs[i]);
            vertices[i] = new VertexPositionNormalTexture(new Vector3(v.X, v.Y, v.Z), new Vector3(n.X, n.Y, n.Z), new Vector2(uv.X, uv.Y));
        }
        var indices = part.Indices.ToArray();
        var bounds = BoundingBox.FromPoints(vertices.Select(v => v.Position).ToArray());
        var draw = new MeshDraw
        {
            PrimitiveType = PrimitiveType.TriangleList,
            DrawCount = indices.Length,
            IndexBuffer = new IndexBufferBinding(Buffer.Index.New(device, indices), true, indices.Length),
            VertexBuffers = [new VertexBufferBinding(Buffer.Vertex.New(device, vertices), VertexPositionNormalTexture.Layout, vertices.Length)],
        };
        return new Mesh { Draw = draw, BoundingBox = bounds, BoundingSphere = BoundingSphere.FromBox(bounds) };
    }

    /// <summary>Frees a model it built: its buffers are its own.</summary>
    private static void Release(Model? model)
    {
        if (model == null)
            return;
        foreach (var mesh in model.Meshes)
        {
            mesh.Draw.IndexBuffer?.Buffer.Dispose();
            foreach (var binding in mesh.Draw.VertexBuffers)
                binding.Buffer.Dispose();
        }
    }
}
