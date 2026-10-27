using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Physics;
using Stride.Rendering.Materials;
using Buffer = Stride.Graphics.Buffer;
using NVector3 = System.Numerics.Vector3;

namespace Stride.Roads;

/// <summary>
/// Builds each <see cref="RoadComponent"/>'s model from its points whenever they or its settings
/// change. It runs in Game Studio's scene editor as well as the game (the component asks for both
/// execution modes), so a road reshapes as its points are dragged. What it builds sits on an entity
/// of its own, made here and never saved: at the root of the scene instance (not under the road, whose
/// children the editor keeps in step with the scene asset), following the road's place and marked as
/// standing for it (<see cref="GeneratedEntity.OwnerKey"/>), so clicking it in the editor selects the road.
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
            data.Holder.Scene = null;
        Release(data.Model);
        component.Frames = Array.Empty<RoadFrame>();
    }

    public override void Update(GameTime time)
    {
        foreach (var (component, data) in ComponentDatas)
        {
            // Where the road is, every frame: what it builds isn't its child, so doesn't move with it.
            if (data.Holder != null)
            {
                data.Holder.Transform.UseTRS = false;
                data.Holder.Transform.LocalMatrix = component.Entity.Transform.WorldMatrix;
            }

            var state = State(component);
            if (state == data.State)
                continue;
            data.State = state;
            Rebuild(component, data);
        }
    }

    /// <summary>The points a road runs through, in order: its entity's children.</summary>
    public static List<Entity> Points(RoadComponent road) =>
        road.Entity.Transform.Children.Select(t => t.Entity).ToList();

    /// <summary>Everything a road is built from, hashed: its points' places and settings, and its own.</summary>
    private static int State(RoadComponent road)
    {
        var hash = new HashCode();
        hash.Add(road.Section);
        hash.Add(road.Closed);
        hash.Add(road.SidewalkFalloff);
        hash.Add(road.Collision);
        hash.Add(road.SurfaceMaterial);
        hash.Add(road.WhiteLineMaterial);
        hash.Add(road.YellowLineMaterial);
        hash.Add(road.SidewalkMaterial);
        hash.Add(road.BankMaterial);
        hash.Add(road.StructureMaterial);
        foreach (var structure in road.Structures)
            hash.Add(structure);
        // Where it is in the world, for how far its viaducts' pillars reach down.
        hash.Add(road.Entity.Transform.WorldMatrix);
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
        var root = (EntityManager as SceneInstance)?.RootScene;
        if (points.Count < 2 || road.Section.Lanes == 0 || device == null || root == null)
        {
            road.Frames = Array.Empty<RoadFrame>();
            if (data.Holder != null)
                data.Holder.EnableAll(false, true);
            return;
        }

        var curve = new RoadCurve(points.Select(ToCurvePoint).ToList(), road.Closed);
        road.Frames = curve.Frames(RoadComponent.Spacing);
        var structures = road.Structures;
        var meshes = RoadMeshBuilder.Build(road.Frames, road.Section, curve.Closed, road.SidewalkFalloff, structures);
        var roadWorld = road.Entity.Transform.WorldMatrix;
        var concrete = RoadStructureBuilder.Build(RoadStructureBuilder.Unrolled(road.Frames, curve.Closed), road.Section, structures,
            local => GroundUnder(roadWorld, local));

        var parts = new[] { meshes.Surface, meshes.WhiteLines, meshes.YellowLines, meshes.Banks, meshes.Sidewalks, concrete };
        // Where it has no material of its own, a plain colour (RoadMaterials).
        var materials = new[]
        {
            road.SurfaceMaterial ?? RoadMaterials.DefaultAsphalt(device),
            road.WhiteLineMaterial ?? RoadMaterials.DefaultWhiteLine(device),
            road.YellowLineMaterial ?? RoadMaterials.DefaultYellowLine(device),
            road.BankMaterial ?? RoadMaterials.DefaultBank(device),
            road.SidewalkMaterial ?? RoadMaterials.DefaultSidewalk(device),
            road.StructureMaterial ?? RoadMaterials.DefaultConcrete(device),
        };
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
            data.Holder.Tags.Set(GeneratedEntity.OwnerKey, road.Entity);
            data.Holder.Transform.UseTRS = false;
            data.Holder.Transform.LocalMatrix = road.Entity.Transform.WorldMatrix;
            root.Entities.Add(data.Holder);
        }
        data.Holder.Get<ModelComponent>()!.Model = model;
        UpdateCollision(road, data.Holder, meshes, concrete);
        data.Holder.EnableAll(true, true);
        Release(data.Model);
        data.Model = model;
    }

    /// <summary>
    /// Gives what a road builds a static collider of its asphalt, banks, sidewalks and viaducts (the lines lie on the
    /// asphalt), made again with its shape; none if the road has no collision. In the game only: the editor never steps physics.
    /// </summary>
    private void UpdateCollision(RoadComponent road, Entity holder, RoadMeshes meshes, MeshData concrete)
    {
        if (holder.Get<StaticColliderComponent>() is { } previous)
        {
            var shape = previous.ColliderShape;
            holder.Remove(previous);
            shape?.Dispose();
        }
        if (!road.Collision || (EntityManager.ExecutionMode & ExecutionMode.Runtime) == 0)
            return;

        var vertices = new List<Vector3>();
        var indices = new List<int>();
        foreach (var part in new[] { meshes.Surface, meshes.Banks, meshes.Sidewalks, concrete })
        {
            var start = vertices.Count;
            vertices.AddRange(part.Vertices.Select(v => new Vector3(v.X, v.Y, v.Z)));
            indices.AddRange(part.Indices.Select(i => i + start));
        }
        if (indices.Count == 0)
            return;
        holder.Add(new StaticColliderComponent { ColliderShape = new StaticMeshColliderShape(vertices, indices) });
    }

    /// <summary>
    /// How high the ground is under a point of a road's frames (in its space), for how far its viaducts' pillars reach
    /// down: the world's ground plane (y = 0) for now, as Stride has no terrain of its own to ask.
    /// </summary>
    private static float? GroundUnder(Matrix roadWorld, NVector3 local)
    {
        var world = Vector3.TransformCoordinate(new Vector3(local.X, local.Y, local.Z), roadWorld);
        world.Y = 0f;
        return Vector3.TransformCoordinate(world, Matrix.Invert(roadWorld)).Y;
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
