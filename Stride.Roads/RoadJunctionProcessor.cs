using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Games;
using Stride.Graphics;
using Stride.Physics;
using Stride.Rendering;
using NVector3 = System.Numerics.Vector3;

namespace Stride.Roads;

/// <summary>
/// Builds each <see cref="RoadJunctionComponent"/> from the roads meeting it (<see cref="RoadJunctionShaper"/>), in the
/// editor as in the game, whenever they or it change: its surface, lines, banks and sidewalks, on an entity of its own at
/// the scene's root (never saved), in the world's space. It keeps how far it cuts each road back, which the road then
/// builds to.
/// </summary>
public sealed class RoadJunctionProcessor : EntityProcessor<RoadJunctionComponent, RoadJunctionProcessor.Built>
{
    public const string BuiltName = "Road junction (built)";

    public sealed class Built
    {
        public Entity? Holder;
        public Model? Model;
        public int? State;
    }

    private IGraphicsDeviceService? _graphics;

    protected override void OnSystemAdd() => _graphics = Services.GetService<IGraphicsDeviceService>();

    protected override Built GenerateComponentData(Entity entity, RoadJunctionComponent component) => new();

    protected override bool IsAssociatedDataValid(Entity entity, RoadJunctionComponent component, Built associatedData) => true;

    protected override void OnEntityComponentRemoved(Entity entity, RoadJunctionComponent component, Built data)
    {
        if (data.Holder != null)
            data.Holder.Scene = null;
        RoadProcessor.Release(data.Model);
        component.Setbacks.Clear();
    }

    /// <summary>The junctions being built, for finding what to join to.</summary>
    public IEnumerable<RoadJunctionComponent> Junctions => ComponentDatas.Keys;

    public override void Update(GameTime time)
    {
        var roads = EntityManager.Processors.OfType<RoadProcessor>().FirstOrDefault()?.Roads.ToList() ?? new List<RoadComponent>();
        foreach (var (junction, data) in ComponentDatas)
        {
            var arms = Arms(junction, roads);
            var hash = new HashCode();
            hash.Add(junction.Entity.Transform.WorldMatrix);
            hash.Add(junction.CornerRadius);
            hash.Add(junction.TrafficControl);
            hash.Add(junction.SurfaceMaterial);
            hash.Add(junction.WhiteLineMaterial);
            hash.Add(junction.SidewalkMaterial);
            hash.Add(junction.BankMaterial);
            hash.Add(junction.StructureMaterial);
            hash.Add(junction.Entity.Get<RoadViaductComponent>()?.Over(0f, 0f));
            foreach (var (road, start) in arms)
            {
                hash.Add(road);
                hash.Add(start);
                hash.Add(road.Frames);
                hash.Add(road.Entity.Transform.WorldMatrix);
                hash.Add(road.Section);
            }
            var state = hash.ToHashCode();
            if (state == data.State)
                continue;
            data.State = state;
            Rebuild(junction, data, arms);
        }
    }

    /// <summary>The roads meeting a junction, each with whether it starts there: those built enough to have a way out of it.</summary>
    private static List<(RoadComponent Road, bool Start)> Arms(RoadJunctionComponent junction, List<RoadComponent> roads)
    {
        var arms = new List<(RoadComponent, bool)>();
        foreach (var road in roads)
        {
            if (road.Frames.Count < 2 || road.Closed)
                continue;
            // By id: what a road names may be a copy of it (see RoadProcessor.Relink).
            if (road.FromJunction?.Id == junction.Id)
                arms.Add((road, true));
            if (road.ToJunction?.Id == junction.Id)
                arms.Add((road, false));
        }
        return arms;
    }

    /// <summary>A road as an arm of a junction at its start or end, in the world: which way it leaves it, and where it really is out along it.</summary>
    private static JunctionArm? ArmOf(RoadComponent road, bool start, RoadJunctionComponent junction)
    {
        var frames = road.Frames;
        var world = road.Entity.Transform.WorldMatrix;
        RoadFrame ToWorld(RoadFrame f) => new(
            ToNumerics(Vector3.TransformCoordinate(ToStride(f.Position), world)),
            NVector3.Normalize(ToNumerics(Vector3.TransformNormal(ToStride(f.Forward), world))),
            NVector3.Normalize(ToNumerics(Vector3.TransformNormal(ToStride(f.Right), world))),
            f.Distance);
        var (first, last) = (frames[0].Distance, frames[^1].Distance);
        var length = last - first;
        RoadFrame Along(float distance)
        {
            if (start)
                return ToWorld(RoadCurve.At(frames, first + distance)) with { Distance = distance };
            var frame = ToWorld(RoadCurve.At(frames, last - distance));
            return frame with { Forward = -frame.Forward, Right = -frame.Right, Distance = distance };
        }
        var forward = Along(0f).Forward;
        var level = new NVector3(forward.X, 0f, forward.Z);
        if (level.LengthSquared() < 1e-8f)
            return null;
        // Cut back no further than halfway along it if a junction is at its other end too, so some of it is left between them; nor right up to a loose end.
        var other = start ? road.ToJunction : road.FromJunction;
        var most = other != null && other.Id != junction.Id ? length / 2f - 1f : length - 2f;
        return new JunctionArm(NVector3.Normalize(level), road.Section, start, Along, most);
    }

    private void Rebuild(RoadJunctionComponent junction, Built data, List<(RoadComponent Road, bool Start)> arms)
    {
        var device = _graphics?.GraphicsDevice;
        var root = (EntityManager as SceneInstance)?.RootScene;
        var shaped = arms.Select(a => (a.Road, a.Start, Arm: ArmOf(a.Road, a.Start, junction))).Where(a => a.Arm != null).ToList();
        junction.Setbacks.Clear();
        if (device == null || root == null || shaped.Count == 0)
        {
            data.Holder?.EnableAll(false, true);
            return;
        }

        var centre = ToNumerics(junction.Entity.Transform.WorldMatrix.TranslationVector);
        // Its banks as its roads' are, on average; none, carried on a viaduct.
        var viaduct = junction.Entity.Get<RoadViaductComponent>();
        var bankHeight = viaduct != null ? 0f : shaped.Average(a => a.Road.Section.ShoulderHeight);
        var bankAngle = shaped.Average(a => a.Road.Section.ShoulderAngle);
        var armShapes = shaped.Select(a => a.Arm!.Value).ToList();
        var shape = RoadJunctionShaper.Build(centre, armShapes, junction.CornerRadius, bankHeight, bankAngle, carried: viaduct != null);
        var signals = RoadSignals.Build(shape, armShapes, junction.TrafficControl);
        for (var i = 0; i < shaped.Count; i++)
            junction.Setbacks[(shaped[i].Road, shaped[i].Start)] = shape.Setbacks[i];

        // Drawn with its own materials, else as most of its roads are, else the plain ones.
        Material? Most(Func<RoadComponent, Material?> of) =>
            shaped.Select(a => of(a.Road)).Where(m => m != null).GroupBy(m => m).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        var surface = junction.SurfaceMaterial ?? Most(r => r.SurfaceMaterial) ?? RoadMaterials.DefaultAsphalt(device);
        var deck = viaduct != null ? Deck(shape, centre, viaduct) : new MeshData();
        var parts = new[]
        {
            (shape.Surface, surface),
            (shape.WhiteLines, junction.WhiteLineMaterial ?? Most(r => r.WhiteLineMaterial) ?? RoadMaterials.DefaultWhiteLine(device)),
            (shape.Banks, junction.BankMaterial ?? Most(r => r.BankMaterial) ?? surface),
            (shape.Sidewalks ?? new MeshData(), junction.SidewalkMaterial ?? Most(r => r.SidewalkMaterial) ?? RoadMaterials.DefaultSidewalk(device)),
            (deck, junction.StructureMaterial ?? Most(r => r.StructureMaterial) ?? RoadMaterials.DefaultConcrete(device)),
            (signals.Steel, RoadMaterials.DefaultSteel(device)),
            (signals.Black, RoadMaterials.DefaultSignalBlack(device)),
            (signals.Red, RoadMaterials.DefaultSignRed(device)),
            (signals.White, RoadMaterials.DefaultWhiteLine(device)),
            (signals.Amber, RoadMaterials.DefaultLampAmber(device)),
            (signals.Green, RoadMaterials.DefaultLampGreen(device)),
        };
        var model = new Model();
        var bounds = BoundingBox.Empty;
        foreach (var (part, material) in parts.Where(p => !p.Item1.IsEmpty))
        {
            var mesh = RoadProcessor.ToMesh(device, part);
            mesh.MaterialIndex = model.Materials.Count;
            model.Materials.Add(new MaterialInstance(material));
            model.Meshes.Add(mesh);
            BoundingBox.Merge(ref bounds, ref mesh.BoundingBox, out bounds);
        }
        model.BoundingBox = bounds;
        model.BoundingSphere = BoundingSphere.FromBox(bounds);

        if (data.Holder == null)
        {
            data.Holder = new Entity(BuiltName) { new ModelComponent() };
            data.Holder.Tags.Set(GeneratedEntity.OwnerKey, junction.Entity);
            root.Entities.Add(data.Holder);
        }
        data.Holder.Get<ModelComponent>()!.Model = model;
        // Solid: its paving, banks, sidewalks and deck (not its lines, nor its signs).
        UpdateCollision(data.Holder, new[] { shape.Surface, shape.Banks, shape.Sidewalks ?? new MeshData(), deck });
        data.Holder.EnableAll(true, true);
        RoadProcessor.Release(data.Model);
        data.Model = model;
    }

    /// <summary>
    /// A junction's viaduct, in the world: a deck under all it covers, as deep as the viaduct's, with a barrier along each
    /// of its corners (its edges between its roads, whose own viaducts' barriers they carry on from), and pillars under
    /// its middle down to the ground.
    /// </summary>
    private static MeshData Deck(JunctionShape shape, NVector3 centre, RoadViaductComponent viaduct)
    {
        var mesh = new MeshData();
        var outline = shape.Reach;
        if (outline.Count < 3)
            return mesh;
        var down = NVector3.UnitY * viaduct.DeckDepth;
        // Its sides, all round, and its underside.
        for (var i = 0; i < outline.Count; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
            var along = b - a;
            if (along.LengthSquared() < 1e-6f)
                continue;
            var outward = NVector3.Normalize(new NVector3(along.Z, 0f, -along.X));
            if (NVector3.Dot(outward, a - centre) < 0f)
                outward = -outward;
            mesh.Quad(mesh.Add(a, outward, default), mesh.Add(b, outward, default), mesh.Add(b - down, outward, default), mesh.Add(a - down, outward, default));
        }
        var under = mesh.Add(centre - down - NVector3.UnitY * 0.01f, -NVector3.UnitY, default);
        var first = mesh.Vertices.Count;
        foreach (var p in outline)
            mesh.Add(p - down, -NVector3.UnitY, default);
        for (var i = 0; i < outline.Count; i++)
            mesh.Triangle(under, first + i, first + (i + 1) % outline.Count);

        // Barriers round its corners: each corner runs clockwise round it, the outside on its left, so in to the right.
        var height = NVector3.UnitY * viaduct.BarrierHeight;
        foreach (var corner in viaduct.BarrierHeight > 0f ? shape.Corners : Array.Empty<IReadOnlyList<NVector3>>())
        {
            for (var i = 0; i + 1 < corner.Count; i++)
            {
                var (a, b) = (corner[i], corner[i + 1]);
                var along = b - a;
                if (along.LengthSquared() < 1e-6f)
                    continue;
                var inward = NVector3.Normalize(NVector3.Cross(along with { Y = 0f }, NVector3.UnitY)) * viaduct.BarrierWidth;
                var (ai, bi) = (a + inward, b + inward);
                var outward = -NVector3.Normalize(inward);
                mesh.Quad(mesh.Add(a, outward, default), mesh.Add(b, outward, default), mesh.Add(b + height, outward, default), mesh.Add(a + height, outward, default));
                mesh.Quad(mesh.Add(a + height, NVector3.UnitY, default), mesh.Add(b + height, NVector3.UnitY, default), mesh.Add(bi + height, NVector3.UnitY, default),
                    mesh.Add(ai + height, NVector3.UnitY, default));
                mesh.Quad(mesh.Add(ai, -outward, default), mesh.Add(bi, -outward, default), mesh.Add(bi + height, -outward, default), mesh.Add(ai + height, -outward, default));
            }
        }

        // A pillar under its middle, down to the ground (y = 0, as the roads' viaducts take it).
        var top = centre - down;
        if (top.Y > 0.1f)
        {
            var half = viaduct.PillarWidth / 2f;
            var bottom = -RoadStructureBuilder.PillarSink;
            foreach (var (n, u) in new[] { (NVector3.UnitX, NVector3.UnitZ), (-NVector3.UnitX, NVector3.UnitZ), (NVector3.UnitZ, NVector3.UnitX), (-NVector3.UnitZ, NVector3.UnitX) })
            {
                var c = top with { Y = 0f } + n * half;
                mesh.Quad(mesh.Add(c - u * half + NVector3.UnitY * bottom, n, default), mesh.Add(c + u * half + NVector3.UnitY * bottom, n, default),
                    mesh.Add(c + u * half + NVector3.UnitY * top.Y, n, default), mesh.Add(c - u * half + NVector3.UnitY * top.Y, n, default));
            }
        }
        return mesh;
    }

    /// <summary>A static collider of its surface, banks and sidewalks, in the game only.</summary>
    private void UpdateCollision(Entity holder, IEnumerable<MeshData> parts)
    {
        if (holder.Get<StaticColliderComponent>() is { } previous)
        {
            var shape = previous.ColliderShape;
            holder.Remove(previous);
            shape?.Dispose();
        }
        if ((EntityManager.ExecutionMode & ExecutionMode.Runtime) == 0)
            return;
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        foreach (var part in parts)
        {
            var start = vertices.Count;
            vertices.AddRange(part.Vertices.Select(ToStride));
            indices.AddRange(part.Indices.Select(i => i + start));
        }
        if (indices.Count > 0)
            holder.Add(new StaticColliderComponent { ColliderShape = new StaticMeshColliderShape(vertices, indices) });
    }

    private static Vector3 ToStride(NVector3 v) => new(v.X, v.Y, v.Z);
    private static NVector3 ToNumerics(Vector3 v) => new(v.X, v.Y, v.Z);
}
