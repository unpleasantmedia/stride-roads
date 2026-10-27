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
        foreach (var viaduct in road.Entity.GetAll<RoadViaductComponent>())
            hash.Add(viaduct.Over(0f, 0f));
        foreach (var ramp in road.Entity.GetAll<RoadRampComponent>())
            hash.Add(ramp.Spec);
        hash.Add(road.Entity.Get<RoadForkComponent>()?.Spec);
        // The ramps and roads its ends are held to, as last built.
        foreach (var ramp in new[] { road.FromRamp, road.ToRamp })
        {
            if (ramp?.Entity?.Get<RoadComponent>() is not { } owner)
                continue;
            hash.Add(ramp.Spec);
            hash.Add(owner.Frames);
            hash.Add(owner.Entity.Transform.WorldMatrix);
        }
        foreach (var fork in new[] { road.FromFork, road.ToFork })
        {
            if (fork?.Entity?.Get<RoadComponent>() is not { } owner)
                continue;
            hash.Add(fork.Spec);
            hash.Add(owner.Section);
            hash.Add(owner.Frames);
            hash.Add(owner.Entity.Transform.WorldMatrix);
        }
        hash.Add(road.FromForksLeft);
        hash.Add(road.ToForksLeft);
        foreach (var other in new[] { road.FromRoad, road.ToRoad })
        {
            if (other?.Entity == null)
                continue;
            hash.Add(other.Frames);
            hash.Add(other.Entity.Transform.WorldMatrix);
        }
        hash.Add(road.FromRoadsEnd);
        hash.Add(road.ToRoadsEnd);
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
            foreach (var viaduct in point.GetAll<RoadViaductComponent>())
                hash.Add(viaduct.Over(0f, 0f));
            // Which point the viaducts are on.
            hash.Add(-1);
        }
        return hash.ToHashCode();
    }

    /// <summary>
    /// A road's viaducts, as stretches along it: one on the road's entity carries all of it; one on a point, the road
    /// from that point to the next. <paramref name="pointsAt"/> is how far along the road each point is (and, last, its
    /// whole length).
    /// </summary>
    private static List<StructureSpec> Structures(RoadComponent road, List<Entity> points, float[] pointsAt)
    {
        var structures = new List<StructureSpec>();
        var length = pointsAt[^1];
        foreach (var viaduct in road.Entity.GetAll<RoadViaductComponent>())
            structures.Add(viaduct.Over(0f, length));
        // pointsAt has an entry past the last point: on a ring, its way back round to the first; on an open road, none.
        for (var i = 0; i < points.Count && i + 1 < pointsAt.Length; i++)
        {
            foreach (var viaduct in points[i].GetAll<RoadViaductComponent>())
                structures.Add(viaduct.Over(pointsAt[i], pointsAt[i + 1]));
        }
        return structures;
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

        var curvePoints = points.Select(ToCurvePoint).ToList();
        // Its ends held where they are joined, running on the way what they join does; their points shown there too.
        foreach (var atStart in new[] { true, false })
        {
            if (points.Count < 2 && !atStart || JoinOf(road, atStart) is not { } pin)
                continue;
            var i = atStart ? 0 : points.Count - 1;
            curvePoints[i] = curvePoints[i] with { Position = ToNumerics(pin.Position), Tangent = ToNumerics(pin.Way), Turn = null, Bank = 0f };
            points[i].Transform.Position = pin.Position;
            points[i].Transform.Rotation = Quaternion.Identity;
        }
        var curve = new RoadCurve(curvePoints, road.Closed);
        // What each point's rotation is relative to, for its gizmo: the way the road would run through it on its own.
        for (var i = 0; i < points.Count; i++)
        {
            var frame = points[i].Get<RoadPointComponent>() is { FollowRotation: true } ? Quaternion.Identity : Facing(curve.NaturalTangentAt(i));
            points[i].Tags.Set(GizmoFrame.BaseRotationKey, frame);
        }
        road.Frames = curve.Frames(RoadComponent.Spacing, out var pointsAt);
        var structures = Structures(road, points, pointsAt);
        var ramps = road.Entity.GetAll<RoadRampComponent>().Select(r => r.Spec).ToList();
        var fork = road.Entity.Get<RoadForkComponent>()?.Spec;
        var meshes = RoadMeshBuilder.Build(road.Frames, road.Section, curve.Closed, road.SidewalkFalloff, structures, ramps, fork);
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

    /// <summary>
    /// A point as the road's maths takes it, in metres in its road's space. Its rotation turns the road from the way it
    /// would run through it on its own (which its gizmo shows, see <see cref="GizmoFrame"/>); set to follow its rotation,
    /// the road runs the way it faces instead, banked by how far it rolls about that way.
    /// </summary>
    /// <summary>
    /// Where a road's start (or end) is joined to a ramp's free end or another road's end, and the way the road runs
    /// there, in its own space: none if that end is joined to nothing, or to something not built.
    /// </summary>
    public static (Vector3 Position, Vector3 Way)? JoinOf(RoadComponent road, bool atStart)
    {
        var (ramp, other, othersEnd) = atStart ? (road.FromRamp, road.FromRoad, road.FromRoadsEnd) : (road.ToRamp, road.ToRoad, road.ToRoadsEnd);
        var (fork, forksLeft) = atStart ? (road.FromFork, road.FromForksLeft) : (road.ToFork, road.ToForksLeft);
        var end = fork != null && fork.Entity?.Get<RoadComponent>() != road ? ForkEnd(fork, forksLeft)
            : ramp != null ? RampEnd(ramp)
            : other != null && other != road ? RoadEnd(other, othersEnd) : null;
        if (end is not { } at)
            return null;
        var toRoad = Matrix.Invert(road.Entity.Transform.WorldMatrix);
        // Out of what it joins, at its start; into it, at its end.
        var way = Vector3.Normalize(Vector3.TransformNormal(at.Outward, toRoad));
        return (Vector3.TransformCoordinate(at.Position, toRoad), atStart ? way : -way);
    }

    /// <summary>The shape of the fork on a road, as last built: none if it has none, isn't built, or can't part it.</summary>
    public static RoadForkShape? ForkShape(RoadForkComponent fork)
    {
        if (fork.Entity?.Get<RoadComponent>() is not { } owner || owner.Frames.Count < 2 || owner.Closed)
            return null;
        var shape = new RoadForkShape(owner.Section, fork.Spec, owner.Frames[0].Distance, owner.Frames[^1].Distance);
        return shape.Valid ? shape : null;
    }

    /// <summary>Where a fork's group (left or right) ends, for its branch, and the way out of the fork there, in the world.</summary>
    public static (Vector3 Position, Vector3 Outward)? ForkEnd(RoadForkComponent fork, bool left)
    {
        if (ForkShape(fork) is not { } shape)
            return null;
        var owner = fork.Entity.Get<RoadComponent>()!;
        var (position, outward) = shape.BranchEnd(owner.Frames, left);
        var world = owner.Entity.Transform.WorldMatrix;
        return (Vector3.TransformCoordinate(ToStride(position), world), Vector3.Normalize(Vector3.TransformNormal(ToStride(outward), world)));
    }

    /// <summary>Where a road's first (or last) frame is, and the way out of the road there, in the world: none if it isn't built.</summary>
    public static (Vector3 Position, Vector3 Outward)? RoadEnd(RoadComponent road, bool last)
    {
        if (road.Entity == null || road.Frames.Count < 2)
            return null;
        var frame = last ? road.Frames[^1] : road.Frames[0];
        var outward = last ? frame.Forward : -frame.Forward;
        var world = road.Entity.Transform.WorldMatrix;
        return (Vector3.TransformCoordinate(ToStride(frame.Position), world), Vector3.Normalize(Vector3.TransformNormal(ToStride(outward), world)));
    }

    /// <summary>The roads being built, for finding what to join to.</summary>
    public IEnumerable<RoadComponent> Roads => ComponentDatas.Keys;

    /// <summary>Where a ramp ends, away from its road, and the way out of it there, in the world: none if its road isn't built.</summary>
    public static (Vector3 Position, Vector3 Outward)? RampEnd(RoadRampComponent ramp)
    {
        if (ramp.Entity?.Get<RoadComponent>() is not { } owner || owner.Frames.Count < 2)
            return null;
        var (position, outward) = RoadRampShaper.FreeEnd(owner.Frames, ramp.Spec, owner.Section);
        var world = owner.Entity.Transform.WorldMatrix;
        return (Vector3.TransformCoordinate(ToStride(position), world), Vector3.Normalize(Vector3.TransformNormal(ToStride(outward), world)));
    }

    private static Vector3 ToStride(NVector3 v) => new(v.X, v.Y, v.Z);

    public static CurvePoint ToCurvePoint(Entity point)
    {
        var at = point.Transform.Position;
        var rotation = point.Transform.Rotation;
        var settings = point.Get<RoadPointComponent>();
        NVector3? tangent = null;
        var bank = 0f;
        System.Numerics.Quaternion? turn = null;
        if (settings is not { FollowRotation: true })
        {
            if (MathF.Abs(rotation.W) < 0.99999f)
                turn = new System.Numerics.Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
        }
        else
        {
            var forward = ToNumerics(Vector3.Transform(-Vector3.UnitZ, rotation));
            var right = ToNumerics(Vector3.Transform(Vector3.UnitX, rotation));
            tangent = forward;
            var (levelRight, levelUp) = RoadCurve.Level(forward);
            bank = MathF.Atan2(NVector3.Dot(right, levelUp), NVector3.Dot(right, levelRight));
        }
        return new CurvePoint(ToNumerics(at), tangent, settings?.Handle ?? 1f / 3f, bank, turn);
    }

    /// <summary>The rotation that faces a point (its forward, -Z) the way a road runs, level across.</summary>
    public static Quaternion Facing(NVector3 tangent)
    {
        var (right, up) = RoadCurve.Level(tangent);
        var axes = Matrix.Identity;
        axes.Right = new Vector3(right.X, right.Y, right.Z);
        axes.Up = new Vector3(up.X, up.Y, up.Z);
        axes.Backward = -new Vector3(tangent.X, tangent.Y, tangent.Z);
        return Quaternion.RotationMatrix(axes);
    }

    private static NVector3 ToNumerics(Vector3 v) => new(v.X, v.Y, v.Z);

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
