using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Stride.Roads;

/// <summary>
/// A barrier along a road's edge or median, or along a gore (between a ramp and its road, or a fork's two groups,
/// once they have parted: one each side, with a crash cushion at its nose).
/// </summary>
public enum RoadBarrier
{
    None,
    /// <summary>A guard rail: a steel beam on posts.</summary>
    Rail,
    /// <summary>A concrete wall.</summary>
    Concrete,
}

/// <summary>The barriers along a road: on its left and right edges, and down its middle.</summary>
public readonly record struct RoadBarriers(RoadBarrier Left = RoadBarrier.None, RoadBarrier Right = RoadBarrier.None, RoadBarrier Median = RoadBarrier.None);

/// <summary>Gores: their painted chevrons, and the barriers along them.</summary>
public static partial class RoadMeshBuilder
{
    /// <summary>How far apart a gore's chevrons are along it, how far back each one's point reaches, and how wide its paint is.</summary>
    public const float ChevronSpacing = 3f, ChevronBack = 1.5f, ChevronWidth = 0.3f;

    /// <summary>How narrow a gore can be for chevrons in it.</summary>
    public const float ChevronRoom = 0.6f;

    /// <summary>How far in from a lane's edge a gore barrier stands, at least, and how far apart its two sides must be to start.</summary>
    public const float RailFromLane = 0.6f, RailClearance = 0.8f;

    /// <summary>A guard rail: its beam's top and bottom, how far it stands out from its posts, and its posts' spacing and size.</summary>
    public const float RailTop = 0.75f, RailBottom = 0.45f, RailBeam = 0.1f, PostSpacing = 2f, PostSize = 0.15f;

    /// <summary>How far in from the paving's (or sidewalk's) outer edge a barrier along a road's edge stands.</summary>
    public const float BarrierIn = 0.4f;

    /// <summary>A concrete gore wall: how high and how thick.</summary>
    public const float WallHeight = 0.85f, WallWidth = 0.4f;

    /// <summary>A crash cushion at a gore's nose: how wide, high and long.</summary>
    public const float TipWidth = 0.6f, TipHeight = 1.1f, TipLength = 1.2f;

    /// <summary>
    /// Chevrons along a gore from <paramref name="from"/> to <paramref name="to"/> along the road: each a V between
    /// its two sides there (across, from <paramref name="sides"/>, where it gives them), its point
    /// <see cref="ChevronBack"/> metres back towards where the gore starts (<paramref name="back"/>: -1 back down the
    /// road, +1 on up it).
    /// </summary>
    private static void Chevrons(MeshData mesh, IReadOnlyList<RoadFrame> frames, float from, float to, float back, Func<float, (float Left, float Right)?> sides)
    {
        if (to - from < ChevronSpacing)
            return;
        var (first, last) = (frames[0].Distance, frames[^1].Distance);
        // Counted from the nose end (where the gore starts), so each starts the same way.
        var start = back < 0f ? from : to;
        for (var k = 1; ; k++)
        {
            var d = start - back * ChevronSpacing * k;
            if (d < from || d > to)
                break;
            var behind = d + back * ChevronBack;
            if (behind < first || behind > last || sides(d) is not { } here || sides(behind) is not { } there || here.Right - here.Left < ChevronRoom)
                continue;
            var (f, b) = (RoadCurve.At(frames, d), RoadCurve.At(frames, behind));
            var tip = b.Across((there.Left + there.Right) / 2f) + b.Up * LineLift;
            Paint(mesh, new[] { f.Across(here.Left) + f.Up * LineLift, tip, f.Across(here.Right) + f.Up * LineLift }, f.Up, ChevronWidth);
        }
    }

    /// <summary>A painted line through these points, this wide, lying flat.</summary>
    private static void Paint(MeshData mesh, IReadOnlyList<Vector3> points, Vector3 up, float width)
    {
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            var along = b - a;
            if (along.LengthSquared() < 1e-6f)
                continue;
            var side = Vector3.Normalize(Vector3.Cross(along, up)) * (width / 2f);
            // A little longer each end, so the V's arms meet at its point.
            var over = Vector3.Normalize(along) * (width / 2f);
            mesh.Quad(mesh.Add(a - over - side, up, Vector2.Zero), mesh.Add(a - over + side, up, Vector2.Zero),
                mesh.Add(b + over + side, up, Vector2.Zero), mesh.Add(b + over - side, up, Vector2.Zero));
        }
    }

    /// <summary>
    /// A gore barrier along a line of places (each with its frame's right and up), facing <paramref name="facing"/>
    /// across (+1 the frame's right; 0 both ways, a beam each side): a guard rail's posts and beam, or a concrete wall.
    /// </summary>
    private static void Barrier(RoadMeshes meshes, IReadOnlyList<(Vector3 At, Vector3 Right, Vector3 Up)> line, float facing, RoadBarrier kind)
    {
        if (line.Count < 2 || kind == RoadBarrier.None)
            return;
        if (kind == RoadBarrier.Concrete)
        {
            Extrude(meshes.Walls, line, -WallWidth / 2f, WallWidth / 2f, -SkirtDepth, WallHeight);
            return;
        }
        // Its beam on the side it faces, its posts behind.
        foreach (var side in facing == 0f ? new[] { -1f, 1f } : new[] { facing })
        {
            var beamAt = side * (PostSize / 2f + RailBeam / 2f);
            Extrude(meshes.Barriers, line, beamAt - RailBeam / 2f, beamAt + RailBeam / 2f, RailBottom, RailTop);
        }
        var walked = 0f;
        var next = 0f;
        for (var i = 0; i < line.Count; i++)
        {
            if (i > 0)
                walked += Vector3.Distance(line[i - 1].At, line[i].At);
            if (walked + 1e-3f < next && i < line.Count - 1)
                continue;
            var (at, right, up) = line[i];
            var forward = Vector3.Normalize(Vector3.Cross(up, right));
            Box(meshes.Barriers, at + up * (RailTop - SkirtDepth) / 2f, right, up, forward, new Vector3(PostSize, RailTop + SkirtDepth, PostSize) / 2f);
            next = walked + PostSpacing;
        }
    }

    /// <summary>
    /// A crash cushion at a gore's nose, its face towards the traffic coming at it (<paramref name="towards"/>): a rounded
    /// yellow nose, and behind it, back into the gore, a steel body of ribbed bays narrowing a little to its rails.
    /// </summary>
    private static void Tip(RoadMeshes meshes, Vector3 at, Vector3 towards, Vector3 up)
    {
        var forward = Vector3.Normalize(towards);
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        var half = TipWidth / 2f;
        // The nose: half a round post, its round side towards the traffic.
        const int sides = 10;
        for (var i = 0; i < sides; i++)
        {
            var (a, b) = (MathF.PI * i / sides, MathF.PI * (i + 1) / sides);
            Vector3 Round(float angle) => right * (MathF.Cos(angle) * half) + forward * (MathF.Sin(angle) * half);
            var (na, nb) = (Vector3.Normalize(Round(a)), Vector3.Normalize(Round(b)));
            var (pa, pb) = (at + Round(a), at + Round(b));
            meshes.YellowLines.Quad(meshes.YellowLines.Add(pa, na, Vector2.Zero), meshes.YellowLines.Add(pb, nb, Vector2.Zero),
                meshes.YellowLines.Add(pb + up * TipHeight, nb, Vector2.Zero), meshes.YellowLines.Add(pa + up * TipHeight, na, Vector2.Zero));
            meshes.YellowLines.Triangle(meshes.YellowLines.Add(at + up * TipHeight, up, Vector2.Zero),
                meshes.YellowLines.Add(pa + up * TipHeight, up, Vector2.Zero), meshes.YellowLines.Add(pb + up * TipHeight, up, Vector2.Zero));
        }
        // The body: bays back from the nose, each a little lower and narrower, with a rib between.
        const int bays = 4;
        var length = TipLength * 2.5f / bays;
        for (var k = 0; k < bays; k++)
        {
            var shrink = 1f - 0.08f * k;
            var centre = at - forward * (length * (k + 0.5f)) + up * (TipHeight * shrink * 0.45f);
            Box(meshes.Barriers, centre, right, up, forward, new Vector3(half * shrink, TipHeight * shrink * 0.45f, length * 0.45f));
            Box(meshes.Barriers, centre - forward * (length * 0.5f), right, up, forward, new Vector3(half * shrink + 0.03f, TipHeight * shrink * 0.47f, 0.04f));
        }
    }

    /// <summary>A box at a centre, along these axes, this far out each way along each.</summary>
    private static void Box(MeshData mesh, Vector3 centre, Vector3 x, Vector3 y, Vector3 z, Vector3 half)
    {
        foreach (var (n, u, v, h) in new[] { (x, y, z, half.X), (-x, y, z, half.X), (y, x, z, half.Y), (-y, x, z, half.Y), (z, x, y, half.Z), (-z, x, y, half.Z) })
        {
            var (hu, hv) = (n == x || n == -x ? (half.Y, half.Z) : n == y || n == -y ? (half.X, half.Z) : (half.X, half.Y));
            var c = centre + n * h;
            mesh.Quad(mesh.Add(c - u * hu - v * hv, n, Vector2.Zero), mesh.Add(c + u * hu - v * hv, n, Vector2.Zero),
                mesh.Add(c + u * hu + v * hv, n, Vector2.Zero), mesh.Add(c - u * hu + v * hv, n, Vector2.Zero));
        }
    }

    /// <summary>A rectangle across (from left to right of each place, bottom to top above it) carried along a line: its four sides, and its ends.</summary>
    private static void Extrude(MeshData mesh, IReadOnlyList<(Vector3 At, Vector3 Right, Vector3 Up)> line, float left, float right, float bottom, float top)
    {
        Vector3 P((Vector3 At, Vector3 Right, Vector3 Up) p, float x, float y) => p.At + p.Right * x + p.Up * y;
        var faces = new (float X0, float Y0, float X1, float Y1, Func<(Vector3 At, Vector3 Right, Vector3 Up), Vector3> Normal)[]
        {
            (left, top, right, top, p => p.Up),
            (right, top, right, bottom, p => p.Right),
            (left, bottom, left, top, p => -p.Right),
        };
        foreach (var face in faces)
        {
            int? lastA = null, lastB = null;
            foreach (var p in line)
            {
                var n = face.Normal(p);
                var a = mesh.Add(P(p, face.X0, face.Y0), n, Vector2.Zero);
                var b = mesh.Add(P(p, face.X1, face.Y1), n, Vector2.Zero);
                if (lastA is { } pa && lastB is { } pb)
                    mesh.Quad(pa, pb, b, a);
                (lastA, lastB) = (a, b);
            }
        }
        foreach (var (p, sign) in new[] { (line[0], -1f), (line[^1], 1f) })
        {
            var n = Vector3.Normalize(Vector3.Cross(p.Up, p.Right)) * sign;
            mesh.Quad(mesh.Add(P(p, left, bottom), n, Vector2.Zero), mesh.Add(P(p, right, bottom), n, Vector2.Zero),
                mesh.Add(P(p, right, top), n, Vector2.Zero), mesh.Add(P(p, left, top), n, Vector2.Zero));
        }
    }

    /// <summary>
    /// A gore's two barriers and the cushion at its nose: along <paramref name="leftAt"/> and <paramref name="rightAt"/>
    /// (across, in each place's frame) at the distances along given, from where they are far enough apart, facing out of
    /// the gore towards their lanes. The cushion faces <paramref name="back"/> (-1: back down the road).
    /// </summary>
    private static void GoreBarriers(RoadMeshes meshes, IReadOnlyList<RoadFrame> leftFrames, IReadOnlyList<RoadFrame> rightFrames, IReadOnlyList<float> along,
        Func<float, float> leftAt, Func<float, float> rightAt, float back, RoadBarrier kind)
    {
        if (kind == RoadBarrier.None)
            return;
        var clear = along.Where(d => rightAt(d) - leftAt(d) >= RailClearance).ToList();
        if (clear.Count < 2)
            return;
        // Only the run from the nose on: the first clear place (counted from where the gore starts) to the far end.
        var nose = back < 0f ? clear.Min() : clear.Max();
        var run = along.Where(d => back < 0f ? d >= nose : d <= nose).OrderBy(d => d).ToList();
        if (run.Count < 2)
            return;
        (Vector3, Vector3, Vector3) Place(IReadOnlyList<RoadFrame> frames, float d, float x)
        {
            var f = RoadCurve.At(frames, d);
            return (f.Across(x), f.Right, f.Up);
        }
        Barrier(meshes, run.Select(d => Place(leftFrames, d, leftAt(d))).ToList(), -1f, kind);
        Barrier(meshes, run.Select(d => Place(rightFrames, d, rightAt(d))).ToList(), 1f, kind);
        var at = RoadCurve.At(leftFrames, nose);
        var (l, r) = (at.Across(leftAt(nose)), RoadCurve.At(rightFrames, nose).Across(rightAt(nose)));
        Tip(meshes, (l + r) / 2f, at.Forward * back, at.Up);
    }

    /// <summary>A barrier along each run of frames where the test holds, at an offset across (each frame's), facing as <see cref="Barrier"/> takes it.</summary>
    private static void BarrierWhere(RoadMeshes meshes, IReadOnlyList<RoadFrame> frames, Func<RoadFrame, float> offset, Func<RoadFrame, bool> where,
        float facing, RoadBarrier kind)
    {
        if (kind == RoadBarrier.None)
            return;
        foreach (var run in Runs(frames, where))
            Barrier(meshes, run.Select(f => (f.Across(offset(f)), f.Right, f.Up)).ToList(), facing, kind);
    }

    /// <summary>Distances along from one to another, about a metre apart, ends included.</summary>
    private static List<float> Along(float from, float to)
    {
        var count = Math.Max(1, (int)MathF.Ceiling(to - from));
        return Enumerable.Range(0, count + 1).Select(k => from + (to - from) * k / count).ToList();
    }
}
