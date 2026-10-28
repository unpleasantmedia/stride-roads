using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Stride.Roads;

/// <summary>
/// A viaduct over Length metres from Start along a road: a deck DeckDepth thick under the road,
/// with barriers BarrierHeight high along its edges and pillars every PillarSpacing metres down to
/// the ground.
/// </summary>
public readonly record struct StructureSpec(
    float Start,
    float Length,
    float DeckDepth = 1.2f,
    float BarrierHeight = 1f,
    float BarrierWidth = 0.4f,
    float PillarSpacing = 30f,
    float PillarWidth = 1.5f)
{
    public float End => Start + Length;

    public bool Covers(float distance) => distance >= Start && distance <= End;
}

/// <summary>
/// Builds viaducts along a road's frames, as one mesh of concrete. Offsets across are right of the
/// road's centre line; heights are from its surface. In metres, y up.
/// </summary>
public static class RoadStructureBuilder
{
    /// <summary>How far pillars reach into the ground, so they never stand on air where it dips.</summary>
    public const float PillarSink = 1f;

    /// <summary>The widest a road is carried on one pillar; wider ones stand on a row of them.</summary>
    public const float PillarReach = 12f;

    /// <summary>How far at a time a pillar slides along its span, off a road below, to find clear ground.</summary>
    public const float PillarSlide = 1f;

    /// <summary>The concrete's texture repeats every this many metres.</summary>
    public const float TextureSize = 4f;

    /// <summary>
    /// The viaducts along a road. The ground says how high the ground is under a point (none where
    /// it does not know), for how far their pillars reach down.
    /// </summary>
    public static MeshData Build(IReadOnlyList<RoadFrame> frames, RoadCrossSection section, IReadOnlyList<StructureSpec> structures, Func<Vector3, float?> ground)
    {
        var mesh = new MeshData();
        if (frames.Count < 2)
            return mesh;
        var bands = section.Bands();
        var (left, right) = (bands[0].Left, bands[^1].Right);
        foreach (var structure in structures)
        {
            var stretch = RoadCurve.Trim(frames, Math.Max(structure.Start, frames[0].Distance), Math.Min(structure.End, frames[^1].Distance));
            if (stretch.Count >= 2)
                Viaduct(mesh, stretch, _ => left, _ => right, structure, ground);
        }

        return mesh;
    }

    /// <summary>
    /// The viaducts along a fork's stretch of its trunk: a deck under each of its groups, easing apart with it, with a
    /// barrier along each outer edge, and along each inner edge once they have parted (until then the two decks meet).
    /// </summary>
    public static MeshData BuildFork(IReadOnlyList<RoadFrame> frames, RoadForkShape fork, IReadOnlyList<StructureSpec> structures, Func<Vector3, float?> ground)
    {
        var mesh = new MeshData();
        foreach (var structure in structures)
        {
            var (from, to) = (Math.Max(structure.Start, fork.From), Math.Min(structure.End, fork.To));
            if (to - from < 0.5f)
                continue;
            var stretch = RoadCurve.Trim(frames, from, to);
            if (stretch.Count < 2)
                continue;
            var inner = structure with { BarrierHeight = 0f };
            Viaduct(mesh, stretch, f => fork.Across(f.Distance).OutL, f => fork.Across(f.Distance).InL, inner, ground);
            Viaduct(mesh, stretch, f => fork.Across(f.Distance).InR, f => fork.Across(f.Distance).OutR, inner, ground);
            if (structure.BarrierHeight <= 0f)
                continue;
            DeckBarrier(mesh, stretch, f => fork.Across(f.Distance).OutL, 1f, structure);
            DeckBarrier(mesh, stretch, f => fork.Across(f.Distance).OutR, -1f, structure);
            // Inner ones only where the decks have parted, each facing its own lanes.
            var run = new List<RoadFrame>();
            foreach (var frame in stretch.Append(default))
            {
                if (frame.Forward != default && !fork.Together(frame.Distance))
                {
                    run.Add(frame);
                    continue;
                }
                if (run.Count >= 2)
                {
                    DeckBarrier(mesh, run, f => fork.Across(f.Distance).InL, -1f, structure);
                    DeckBarrier(mesh, run, f => fork.Across(f.Distance).InR, 1f, structure);
                }
                run = new List<RoadFrame>();
            }
        }

        return mesh;
    }

    /// <summary>Structures with a stretch along the road taken out of them: each cut in two around it, or shortened, or gone.</summary>
    public static IReadOnlyList<StructureSpec> Without(IReadOnlyList<StructureSpec> structures, float from, float to) =>
        structures.SelectMany(s => s.End <= from || s.Start >= to ? new[] { s }
            : new[] { s with { Length = from - s.Start }, s with { Start = to, Length = s.End - to } }.Where(p => p.Length > 0.01f)).ToList();

    /// <summary>
    /// A ring's frames as an open road's: its first frame again at its end, at its whole length, so
    /// cutting it into runs keeps the stretch from its last point back round to its first.
    /// </summary>
    public static IReadOnlyList<RoadFrame> Unrolled(IReadOnlyList<RoadFrame> frames, bool closed)
    {
        if (!closed || frames.Count < 2)
            return frames;
        var length = frames[^1].Distance + Vector3.Distance(frames[^1].Position, frames[0].Position);
        return frames.Append(frames[0] with { Distance = length }).ToList();
    }

    /// <summary>The runs of a road's frames not carried by any of these structures, where it lies on the ground.</summary>
    public static IReadOnlyList<IReadOnlyList<RoadFrame>> OnGround(IReadOnlyList<RoadFrame> frames, IReadOnlyList<StructureSpec> structures)
    {
        if (structures.Count == 0 || frames.Count < 2)
            return new[] { frames };
        var runs = new List<IReadOnlyList<RoadFrame>>();
        var from = frames[0].Distance;
        foreach (var structure in structures.OrderBy(s => s.Start))
        {
            if (structure.Start > from)
                runs.Add(RoadCurve.Trim(frames, from, Math.Min(structure.Start, frames[^1].Distance)));
            from = Math.Max(from, structure.End);
        }

        if (from < frames[^1].Distance)
            runs.Add(RoadCurve.Trim(frames, from, frames[^1].Distance));
        return runs.Where(r => r.Count >= 2).ToList();
    }

    private static void Viaduct(MeshData mesh, IReadOnlyList<RoadFrame> stretch, Func<RoadFrame, float> leftAt, Func<RoadFrame, float> rightAt,
        StructureSpec viaduct, Func<Vector3, float?> ground)
    {
        var depth = viaduct.DeckDepth;
        var up = Vector3.UnitY;
        // The deck: its sides from the barriers' feet down, and its underside.
        Sheet(mesh, stretch, f => (f.Across(leftAt(f)) - up * depth, f.Across(leftAt(f))), f => -f.Right);
        Sheet(mesh, stretch, f => (f.Across(rightAt(f)), f.Across(rightAt(f)) - up * depth), f => f.Right);
        Sheet(mesh, stretch, f => (f.Across(rightAt(f)) - up * depth, f.Across(leftAt(f)) - up * depth), _ => -up);

        // A barrier along each edge (none, with no height).
        if (viaduct.BarrierHeight > 0f)
        {
            DeckBarrier(mesh, stretch, leftAt, 1f, viaduct);
            DeckBarrier(mesh, stretch, rightAt, -1f, viaduct);
        }

        // Its ends, where it meets the road on the ground: the deck's face across.
        foreach (var (frame, outward) in new[] { (stretch[0], -stretch[0].Forward), (stretch[^1], stretch[^1].Forward) })
            Face(mesh, frame.Across(leftAt(frame)) - up * depth, frame.Across(rightAt(frame)) - up * depth, frame.Across(rightAt(frame)), frame.Across(leftAt(frame)), Level(outward));

        // Pillars in a row across under it every so often, from the deck down into the ground.
        var length = stretch[^1].Distance - stretch[0].Distance;
        var spans = Math.Max(1, (int)MathF.Round(length / Math.Max(viaduct.PillarSpacing, 1f)));
        var spanLength = length / spans;
        for (var p = 0; p < spans; p++)
        {
            // Mid-span, or as near it along the span as the whole row stands clear (the ground there known: not on a road below).
            var middle = stretch[0].Distance + spanLength * (p + 0.5f);
            for (var step = 0; step <= (int)(spanLength * 0.45f / PillarSlide) * 2; step++)
            {
                var shift = (step + 1) / 2 * PillarSlide * (step % 2 == 0 ? 1f : -1f);
                var frame = RoadCurve.At(stretch, Math.Clamp(middle + shift, stretch[0].Distance, stretch[^1].Distance));
                var (left, right) = (leftAt(frame), rightAt(frame));
                var width = right - left;
                var count = Math.Max(1, (int)MathF.Ceiling(width / PillarReach));
                var row = Enumerable.Range(0, count).Select(k => frame.Across(left + width * (k + 0.5f) / count)).Select(c => (Centre: c, Floor: ground(c))).ToList();
                if (row.Any(r => r.Floor == null))
                    continue;
                foreach (var (centre, floor) in row)
                {
                    var top = centre.Y - depth;
                    // None up at the deck.
                    if (floor!.Value < top - 0.1f)
                        Pillar(mesh, centre with { Y = top }, floor.Value - PillarSink, viaduct.PillarWidth / 2f, Level(frame.Forward), Level(frame.Right));
                }
                break;
            }
        }
    }

    /// <summary>A barrier along a deck's edge, facing inward (+1: to the right): its outer face on up from the deck's side, its top, its inner face, and its ends.</summary>
    private static void DeckBarrier(MeshData mesh, IReadOnlyList<RoadFrame> stretch, Func<RoadFrame, float> edgeAt, float inward, StructureSpec viaduct)
    {
        var up = Vector3.UnitY;
        var height = up * viaduct.BarrierHeight;
        float Inner(RoadFrame f) => edgeAt(f) + inward * viaduct.BarrierWidth;
        Sheet(mesh, stretch, f => (f.Across(edgeAt(f)), f.Across(edgeAt(f)) + height), f => -inward * f.Right);
        Sheet(mesh, stretch, f => (f.Across(edgeAt(f)) + height, f.Across(Inner(f)) + height), _ => up);
        Sheet(mesh, stretch, f => (f.Across(Inner(f)) + height, f.Across(Inner(f))), f => inward * f.Right);
        foreach (var (frame, outward) in new[] { (stretch[0], -stretch[0].Forward), (stretch[^1], stretch[^1].Forward) })
            Face(mesh, frame.Across(edgeAt(frame)), frame.Across(Inner(frame)), frame.Across(Inner(frame)) + height, frame.Across(edgeAt(frame)) + height, Level(outward));
    }

    /// <summary>A sheet along the stretch, from one edge to the other at each frame, facing the way normal says.</summary>
    private static void Sheet(MeshData mesh, IReadOnlyList<RoadFrame> frames, Func<RoadFrame, (Vector3 A, Vector3 B)> edges, Func<RoadFrame, Vector3> normal)
    {
        int? lastA = null, lastB = null;
        foreach (var frame in frames)
        {
            var (a, b) = edges(frame);
            var n = Vector3.Normalize(normal(frame));
            var v = frame.Distance / TextureSize;
            var ia = mesh.Add(a, n, new Vector2(0f, v));
            var ib = mesh.Add(b, n, new Vector2(Vector3.Distance(a, b) / TextureSize, v));
            if (lastA is { } pa && lastB is { } pb)
                mesh.Quad(pa, pb, ib, ia);
            (lastA, lastB) = (ia, ib);
        }
    }

    /// <summary>A flat four-cornered face, facing the way it is told, whichever way round its corners come.</summary>
    private static void Face(MeshData mesh, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
    {
        var sideways = Vector3.Cross(normal, Vector3.UnitY);
        var right = sideways.LengthSquared() > 1e-6f ? Vector3.Normalize(sideways) : Vector3.UnitX;
        Vector2 Uv(Vector3 p) => new Vector2(Vector3.Dot(p, right), p.Y) / TextureSize;
        var first = mesh.Vertices.Count;
        foreach (var p in new[] { a, b, c, d })
            mesh.Add(p, normal, Uv(p));
        mesh.Quad(first, first + 1, first + 2, first + 3);
    }

    /// <summary>A square pillar from top down to bottom, half a side wide each way, its sides along and across the road.</summary>
    private static void Pillar(MeshData mesh, Vector3 top, float bottom, float half, Vector3 along, Vector3 across)
    {
        var low = top with { Y = bottom };
        foreach (var side in new[] { along, across, -along, -across })
        {
            var sideways = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, side)) * half;
            var face = side * half;
            Face(mesh, low + face - sideways, low + face + sideways, top + face + sideways, top + face - sideways, side);
        }
    }

    private static Vector3 Level(Vector3 v)
    {
        var flat = new Vector3(v.X, 0f, v.Z);
        return flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : Vector3.UnitZ;
    }
}
