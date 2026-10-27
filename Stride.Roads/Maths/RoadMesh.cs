using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Stride.Roads;

/// <summary>A mesh being built: its vertices, their normals and UVs, and its triangles.</summary>
public sealed class MeshData
{
    public List<Vector3> Vertices { get; } = new();
    public List<Vector3> Normals { get; } = new();
    public List<Vector2> UVs { get; } = new();
    public List<int> Indices { get; } = new();

    public int Add(Vector3 at, Vector3 normal, Vector2 uv)
    {
        Vertices.Add(at);
        Normals.Add(normal);
        UVs.Add(uv);
        return Vertices.Count - 1;
    }

    /// <summary>
    /// A quad from a to b to c to d round its edge, facing the way of its vertices' normals: its
    /// triangles wound whichever way makes them face there, so callers need not mind the winding.
    /// </summary>
    public void Quad(int a, int b, int c, int d)
    {
        Triangle(a, b, c);
        Triangle(a, c, d);
    }

    private void Triangle(int a, int b, int c)
    {
        var (pa, pb, pc) = (Vertices[a], Vertices[b], Vertices[c]);
        // Stride draws clockwise triangles as front faces: with its right-handed axes, those whose
        // edges cross away from the viewer (as its own plane primitive is wound).
        var facing = Vector3.Cross(pb - pa, pc - pa);
        if (Vector3.Dot(facing, Normals[a] + Normals[b] + Normals[c]) <= 0f)
            Indices.AddRange(new[] { a, b, c });
        else
            Indices.AddRange(new[] { a, c, b });
    }

    public bool IsEmpty => Indices.Count == 0;
}

/// <summary>A road's meshes, one per material: asphalt, white lines, yellow lines, the banks sloping away past its shoulders, sidewalks.</summary>
public sealed record RoadMeshes(MeshData Surface, MeshData WhiteLines, MeshData YellowLines, MeshData Banks, MeshData Sidewalks);

/// <summary>Builds a road's meshes from its frames and its cross-section, in metres, y up.</summary>
public static class RoadMeshBuilder
{
    /// <summary>How wide a painted line is.</summary>
    public const float LineWidth = 0.15f;

    /// <summary>How far above the asphalt the lines lie, so they never flicker into it.</summary>
    public const float LineLift = 0.01f;

    /// <summary>How long a lane line's dash is, and the gap after it.</summary>
    public const float DashLength = 3f, DashGap = 6f;

    /// <summary>How far down the road's edges, and the foot of its banks, reach into the ground.</summary>
    public const float SkirtDepth = 0.6f;

    /// <summary>How far down a sidewalk's outer face goes below the road by default, to meet the ground.</summary>
    public const float SidewalkSkirt = 4f;

    /// <summary>
    /// A road's meshes. Where structures (viaducts) carry it, it has no skirts, banks or sidewalk
    /// faces down into the ground: they build their own sides.
    /// </summary>
    public static RoadMeshes Build(IReadOnlyList<RoadFrame> frames, RoadCrossSection section, bool closed, float sidewalkSkirt = SidewalkSkirt,
        IReadOnlyList<StructureSpec>? structures = null)
    {
        var meshes = new RoadMeshes(new MeshData(), new MeshData(), new MeshData(), new MeshData(), new MeshData());
        if (frames.Count < 2)
            return meshes;
        structures ??= Array.Empty<StructureSpec>();
        // Where it lies on the ground, for its skirts and banks: all of it, or the runs between its
        // structures. A ring is cut into runs as an open road, unrolled so none misses its last stretch.
        var unrolled = RoadStructureBuilder.Unrolled(frames, closed);
        var onGround = structures.Count == 0
            ? new[] { (frames, closed) }
            : RoadStructureBuilder.OnGround(unrolled, structures).Select(run => (run, false)).ToArray();
        var carried = structures
            .Select(st => RoadCurve.Trim(unrolled, Math.Max(st.Start, unrolled[0].Distance), Math.Min(st.End, unrolled[^1].Distance)))
            .Where(run => run.Count >= 2)
            .ToList();

        var runs = PavedRuns(section).ToList();
        foreach (var (left, right) in runs)
        {
            Strip(meshes.Surface, frames, closed, left, right, 0f);
            // Down into the ground at each edge, so no gap shows under it.
            foreach (var (ground, looped) in onGround)
            {
                Skirt(meshes.Surface, ground, looped, left, -1f, 0f, SkirtDepth);
                Skirt(meshes.Surface, ground, looped, right, 1f, 0f, SkirtDepth);
            }
        }

        // Past each shoulder, its bank: none past a sidewalk, where the ground meets it.
        if (runs.Count > 0)
        {
            foreach (var (ground, looped) in onGround)
            {
                if (section.SidewalkLeft <= 0f)
                    Bank(meshes.Banks, ground, looped, runs[0].Left, -1f, section.ShoulderHeight, section.BankWidth);
                if (section.SidewalkRight <= 0f)
                    Bank(meshes.Banks, ground, looped, runs[^1].Right, 1f, section.ShoulderHeight, section.BankWidth);
            }
        }

        foreach (var marking in section.Markings())
        {
            switch (marking.Kind)
            {
            case MarkingKind.EdgeLine:
                Strip(meshes.WhiteLines, frames, closed, marking.Offset - LineWidth / 2f, marking.Offset + LineWidth / 2f, LineLift);
                break;
            case MarkingKind.LaneLine:
                Dashes(meshes.WhiteLines, frames, closed, marking.Offset);
                break;
            case MarkingKind.MedianLine:
                Strip(meshes.YellowLines, frames, closed, marking.Offset - LineWidth / 2f, marking.Offset + LineWidth / 2f, LineLift);
                break;
            case MarkingKind.CentreLine:
                // Two lines, a line's width apart.
                Strip(meshes.YellowLines, frames, closed, marking.Offset - LineWidth * 1.5f, marking.Offset - LineWidth / 2f, LineLift);
                Strip(meshes.YellowLines, frames, closed, marking.Offset + LineWidth / 2f, marking.Offset + LineWidth * 1.5f, LineLift);
                break;
            }
        }

        foreach (var band in section.Bands().Where(b => b.Kind == BandKind.Sidewalk))
        {
            // On the ground its outer face reaches down to it; on a viaduct, the deck's side is below it.
            foreach (var (ground, looped) in onGround)
                Sidewalk(meshes.Sidewalks, ground, looped, band, band.Left < 0f, sidewalkSkirt);
            foreach (var run in carried)
                Sidewalk(meshes.Sidewalks, run, false, band, band.Left < 0f, 0f);
        }

        return meshes;
    }

    /// <summary>The stretches across the road paved with asphalt, left to right: all but a grass median and sidewalks.</summary>
    public static IEnumerable<(float Left, float Right)> PavedRuns(RoadCrossSection section)
    {
        float? start = null;
        var end = 0f;
        foreach (var band in section.Bands())
        {
            var paved = band.Kind is BandKind.Lane or BandKind.Shoulder || (band.Kind == BandKind.Median && section.Median == MedianKind.Paved);
            if (paved)
            {
                start ??= band.Left;
                end = band.Right;
            }
            else if (start is { } from)
            {
                yield return (from, end);
                start = null;
            }
        }

        if (start is { } last)
            yield return (last, end);
    }

    /// <summary>A flat strip along the road from left to right of its centre line, lift above its surface.</summary>
    private static void Strip(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, float left, float right, float lift)
    {
        var up = Vector3.UnitY;
        var count = frames.Count + (closed ? 1 : 0);
        var length = closed ? frames[^1].Distance + Vector3.Distance(frames[^1].Position, frames[0].Position) : frames[^1].Distance;
        int? lastLeft = null, lastRight = null;
        for (var i = 0; i < count; i++)
        {
            var frame = frames[i % frames.Count];
            var along = i == frames.Count ? length : frame.Distance;
            var l = mesh.Add(frame.Across(left) + up * lift, up, new Vector2(left, along));
            var r = mesh.Add(frame.Across(right) + up * lift, up, new Vector2(right, along));
            if (lastLeft is { } pl && lastRight is { } pr)
                mesh.Quad(pl, pr, r, l);
            (lastLeft, lastRight) = (l, r);
        }
    }

    /// <summary>A strip straight down from offset across the road (drop below its surface) by depth, facing out (side -1 left, 1 right).</summary>
    private static void Skirt(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, float offset, float side, float drop, float depth)
    {
        var count = frames.Count + (closed ? 1 : 0);
        int? lastTop = null, lastBottom = null;
        for (var i = 0; i < count; i++)
        {
            var frame = frames[i % frames.Count];
            var outward = side * frame.Right;
            var top = frame.Across(offset) - Vector3.UnitY * drop;
            var t = mesh.Add(top, outward, new Vector2(0f, frame.Distance));
            var b = mesh.Add(top - Vector3.UnitY * depth, outward, new Vector2(depth, frame.Distance));
            if (lastTop is { } pt && lastBottom is { } pb)
                mesh.Quad(pt, pb, b, t);
            (lastTop, lastBottom) = (t, b);
        }
    }

    /// <summary>
    /// A bank from the road's edge sloping away outwards (side -1 left, 1 right), width wide and
    /// dropping height (rising, if negative), facing up and out; with a skirt down from its foot.
    /// </summary>
    private static void Bank(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, float edge, float side, float height, float width)
    {
        if (width <= 0.01f)
            return;
        var slope = MathF.Sqrt(width * width + height * height);
        var count = frames.Count + (closed ? 1 : 0);
        int? lastTop = null, lastBottom = null;
        for (var i = 0; i < count; i++)
        {
            var frame = frames[i % frames.Count];
            var outward = side * frame.Right;
            var normal = Vector3.Normalize(Vector3.UnitY * width + outward * height);
            var t = mesh.Add(frame.Across(edge), normal, new Vector2(0f, frame.Distance));
            var b = mesh.Add(frame.Across(edge + side * width) - Vector3.UnitY * height, normal, new Vector2(slope, frame.Distance));
            if (lastTop is { } pt && lastBottom is { } pb)
                mesh.Quad(pt, pb, b, t);
            (lastTop, lastBottom) = (t, b);
        }
        Skirt(mesh, frames, closed, edge + side * width, side, height, SkirtDepth);
    }

    /// <summary>A dashed line along the road, centred offset right of its centre line.</summary>
    private static void Dashes(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, float offset)
    {
        var length = frames[^1].Distance;
        var up = Vector3.UnitY;
        for (var from = DashGap / 2f; from + DashLength <= length; from += DashLength + DashGap)
        {
            var piece = RoadCurve.Trim(frames, from, from + DashLength);
            int? lastLeft = null, lastRight = null;
            foreach (var frame in piece)
            {
                var l = mesh.Add(frame.Across(offset - LineWidth / 2f) + up * LineLift, up, new Vector2(0f, frame.Distance));
                var r = mesh.Add(frame.Across(offset + LineWidth / 2f) + up * LineLift, up, new Vector2(1f, frame.Distance));
                if (lastLeft is { } pl && lastRight is { } pr)
                    mesh.Quad(pl, pr, r, l);
                (lastLeft, lastRight) = (l, r);
            }
        }
    }

    /// <summary>A sidewalk: its top, the curb face down to the road, and its outer face down into the ground.</summary>
    private static void Sidewalk(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, RoadBand band, bool onLeft, float skirt)
    {
        var up = Vector3.UnitY;
        var count = frames.Count + (closed ? 1 : 0);
        var curb = onLeft ? band.Right : band.Left;
        var outer = onLeft ? band.Left : band.Right;
        var top = RoadCrossSection.CurbHeight;
        int[]? last = null;
        for (var i = 0; i < count; i++)
        {
            var frame = frames[i % frames.Count];
            var along = i == frames.Count ? frames[^1].Distance + Vector3.Distance(frames[^1].Position, frames[0].Position) : frame.Distance;
            // Facing the road from the curb, and away from it at the outer edge.
            var toRoad = onLeft ? frame.Right : -frame.Right;
            var here = new[]
            {
                mesh.Add(frame.Across(curb) + up * top, up, new Vector2(curb, along)),
                mesh.Add(frame.Across(outer) + up * top, up, new Vector2(outer, along)),
                mesh.Add(frame.Across(curb) + up * top, toRoad, new Vector2(top, along)),
                mesh.Add(frame.Across(curb), toRoad, new Vector2(0f, along)),
                mesh.Add(frame.Across(outer) + up * top, -toRoad, new Vector2(top, along)),
                mesh.Add(frame.Across(outer) - up * skirt, -toRoad, new Vector2(-skirt, along)),
            };
            if (last is { } p)
            {
                mesh.Quad(p[0], p[1], here[1], here[0]);
                mesh.Quad(p[2], p[3], here[3], here[2]);
                mesh.Quad(p[4], p[5], here[5], here[4]);
            }
            last = here;
        }
    }
}
