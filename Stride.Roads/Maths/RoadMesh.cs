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

    /// <summary>The broken line between a road's lane and a ramp's alongside it: wider, and closer set.</summary>
    public const float RampLineWidth = 0.3f, RampDash = 3f, RampDashGap = 3f;

    /// <summary>How far in from where a ramp's lane joins the road its broken line starts.</summary>
    public const float RampLineDelay = 12f;

    /// <summary>How far a ramp has to have come away from its road before each has its own edge line.</summary>
    public const float Apart = 0.3f;

    /// <summary>How far before a ramp's start (or past its end) the road has a frame of its own, so its edge steps in cleanly there.</summary>
    private const float BreakGap = 0.001f;

    /// <summary>
    /// A road's meshes. Where structures (viaducts) carry it, it has no skirts, banks or sidewalk
    /// faces down into the ground: they build their own sides. Ramps built into it give up the
    /// shoulder (and sidewalk) on their side where they run alongside, with a broken line between,
    /// and have their own surface, banks and lines where they peel away.
    /// </summary>
    public static RoadMeshes Build(IReadOnlyList<RoadFrame> frames, RoadCrossSection section, bool closed, float sidewalkSkirt = SidewalkSkirt,
        IReadOnlyList<StructureSpec>? structures = null, IReadOnlyList<RampSpec>? ramps = null, ForkSpec? fork = null)
    {
        var meshes = new RoadMeshes(new MeshData(), new MeshData(), new MeshData(), new MeshData(), new MeshData());
        if (frames.Count < 2)
            return meshes;
        // A fork at one end of an open road: built over its stretch, and the road as usual over the rest.
        if (fork is { } forkSpec && !closed && new RoadForkShape(section, forkSpec, frames[0].Distance, frames[^1].Distance) is { Valid: true } forkShape)
        {
            Fork(meshes, RoadCurve.Trim(frames, forkShape.From, forkShape.To), forkShape, sidewalkSkirt);
            frames = forkSpec.AtEnd ? RoadCurve.Trim(frames, frames[0].Distance, forkShape.From) : RoadCurve.Trim(frames, forkShape.To, frames[^1].Distance);
            if (frames.Count < 2)
                return meshes;
        }
        structures ??= Array.Empty<StructureSpec>();
        ramps ??= Array.Empty<RampSpec>();
        if (ramps.Count > 0)
        {
            // A ring with ramps is built as an open road round to its start again, so a ramp may
            // cross where it closes; with frames where each ramp starts and ends, so its edge steps in square.
            frames = WithBreaks(RoadStructureBuilder.Unrolled(frames, closed), ramps, section);
            closed = false;
        }

        // A ramp at this distance on this side, if any.
        RampSpec? RampAt(float distance, RoadSide side)
        {
            foreach (var ramp in ramps)
            {
                if (ramp.Side == side && distance >= ramp.Start && distance <= ramp.End)
                    return ramp;
            }
            return null;
        }

        // Whether a ramp's lane is joined to the road here, on a side: not yet come away from it.
        bool Joined(RoadFrame f, RoadSide side) => RampAt(f.Distance, side) is { } ramp && RoadRampShaper.Profile(ramp, section, f.Distance).Gap < Apart;

        // The road's outer edge on a side: its shoulder's edge, or, where a ramp runs alongside, no
        // further than the ramp's own inner edge.
        float Outer(RoadFrame frame, RoadSide side, float normal)
        {
            if (RampAt(frame.Distance, side) is not { } ramp)
                return normal;
            var (_, _, edgeIn, _) = RoadRampShaper.Across(ramp, section, frame.Distance);
            return side == RoadSide.Right ? MathF.Min(normal, edgeIn) : MathF.Max(normal, edgeIn);
        }

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
        for (var i = 0; i < runs.Count; i++)
        {
            var (left, right) = runs[i];
            Func<RoadFrame, float> leftEdge = i == 0 ? f => Outer(f, RoadSide.Left, left) : _ => left;
            Func<RoadFrame, float> rightEdge = i == runs.Count - 1 ? f => Outer(f, RoadSide.Right, right) : _ => right;
            Strip(meshes.Surface, frames, closed, leftEdge, rightEdge, 0f);
            // Down into the ground at each edge, so no gap shows under it.
            foreach (var (ground, looped) in onGround)
            {
                Skirt(meshes.Surface, ground, looped, leftEdge, -1f, 0f, SkirtDepth);
                Skirt(meshes.Surface, ground, looped, rightEdge, 1f, 0f, SkirtDepth);
                // Past each shoulder, its bank: none past a sidewalk, where the ground meets it.
                if (i == 0 && section.SidewalkLeft <= 0f)
                    Bank(meshes.Banks, ground, looped, leftEdge, -1f, section.ShoulderHeight, section.BankWidth);
                if (i == runs.Count - 1 && section.SidewalkRight <= 0f)
                    Bank(meshes.Banks, ground, looped, rightEdge, 1f, section.ShoulderHeight, section.BankWidth);
            }
        }

        var markings = section.Markings();
        for (var m = 0; m < markings.Count; m++)
        {
            var marking = markings[m];
            switch (marking.Kind)
            {
            case MarkingKind.EdgeLine:
                // The outer edge lines give way, where a ramp runs alongside, to the ramp's broken line.
                var side = m == 0 ? RoadSide.Left : m == markings.Count - 1 ? RoadSide.Right : (RoadSide?)null;
                if (side is { } s && ramps.Any(r => r.Side == s))
                {
                    // Where a ramp is joined to the road, the road's edge line stops: the ramp's outer line carries on from it.
                    var offset = marking.Offset;
                    LineWhere(meshes.WhiteLines, frames, closed, _ => offset, f => !Joined(f, s), LineWidth, false);
                }
                else
                {
                    Strip(meshes.WhiteLines, frames, closed, marking.Offset - LineWidth / 2f, marking.Offset + LineWidth / 2f, LineLift);
                }
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

        // The broken line between the road's lane and a ramp's alongside it, on the road's lane edge (with or without a
        // shoulder there): starting a little way in from where the ramp's lane joins, once there is a lane to divide.
        foreach (var rampSide in ramps.Select(r => r.Side).Distinct())
        {
            var laneEdge = RoadRampShaper.LaneEdge(ramps.First(r => r.Side == rampSide), section);
            bool Alongside(RoadFrame f) => Joined(f, rampSide) && RampAt(f.Distance, rampSide) is { } ramp
                && RoadRampShaper.Profile(ramp, section, f.Distance).Width > 0.05f && FromJoin(ramp, section, f.Distance) >= RampLineDelay;
            // Its dashes counted from where it starts, so it starts with a dash there.
            float DashAlong(RoadFrame f) => RampAt(f.Distance, rampSide) is { } ramp ? FromJoin(ramp, section, f.Distance) - RampLineDelay : f.Distance;
            LineWhere(meshes.WhiteLines, frames, closed, _ => laneEdge, Alongside, RampLineWidth, true, RampDash, RampDashGap, DashAlong);
        }

        foreach (var ramp in ramps)
            Ramp(meshes, frames, section, ramp, sidewalkSkirt);

        foreach (var band in section.Bands().Where(b => b.Kind == BandKind.Sidewalk))
        {
            // None where a ramp runs alongside. On the ground its outer face reaches down to it; on a viaduct,
            // the deck's side is below it.
            var bandSide = band.Left < 0f ? RoadSide.Left : RoadSide.Right;
            // Back again where a ramp has come far enough away to leave it room.
            bool Free(RoadFrame f) => RampAt(f.Distance, bandSide) is not { } ramp || BothWalksFit(ramp, section, f.Distance);
            foreach (var (ground, looped) in onGround)
            {
                foreach (var run in Runs(ground, Free))
                    Sidewalk(meshes.Sidewalks, run, looped && run.Count == ground.Count, band, band.Left < 0f, sidewalkSkirt);
            }
            foreach (var carriedRun in carried)
            {
                foreach (var run in Runs(carriedRun, Free))
                    Sidewalk(meshes.Sidewalks, run, false, band, band.Left < 0f, 0f);
            }
        }

        return meshes;
    }

    /// <summary>
    /// A fork's two groups of lanes over its stretch of its trunk, easing apart: paved as one while their inner shoulders
    /// meet, then each on its own, with skirts and banks (or the trunk's sidewalks) along their outer edges and skirts
    /// along their inner ones. Between them, the trunk's line (lanes going the same way, a broken one; both ways, its
    /// yellow ones) parting into each group's inner edge line.
    /// </summary>
    private static void Fork(RoadMeshes meshes, IReadOnlyList<RoadFrame> stretch, RoadForkShape fork, float sidewalkSkirt)
    {
        if (stretch.Count < 2)
            return;
        var section = fork.Section;
        // A frame where they part, so the paving steps from one to two straight across.
        if (fork.Parting() is { } parting && stretch.All(f => MathF.Abs(f.Distance - parting) > BreakGap))
            stretch = stretch.Append(RoadCurve.At(stretch, parting)).OrderBy(f => f.Distance).ToList();
        var at = stretch.ToDictionary(f => f, f => fork.Across(f.Distance));
        Func<RoadFrame, float> outL = f => at[f].OutL, inL = f => at[f].InL, inR = f => at[f].InR, outR = f => at[f].OutR;

        foreach (var run in Runs(stretch, f => fork.Together(f.Distance) || MathF.Abs(f.Distance - (fork.Parting() ?? float.NaN)) <= BreakGap))
            Strip(meshes.Surface, run, false, outL, outR, 0f);
        foreach (var run in Runs(stretch, f => !fork.Together(f.Distance)))
        {
            Strip(meshes.Surface, run, false, outL, inL, 0f);
            Strip(meshes.Surface, run, false, inR, outR, 0f);
            Skirt(meshes.Surface, run, false, inL, 1f, 0f, SkirtDepth);
            Skirt(meshes.Surface, run, false, inR, -1f, 0f, SkirtDepth);
        }
        Skirt(meshes.Surface, stretch, false, outL, -1f, 0f, SkirtDepth);
        Skirt(meshes.Surface, stretch, false, outR, 1f, 0f, SkirtDepth);

        // Its outer edges as the trunk's: a sidewalk where it has one, else a bank.
        if (section.SidewalkLeft > 0f)
            Sidewalk(meshes.Sidewalks, stretch, false, outL, f => outL(f) - section.SidewalkLeft, true, sidewalkSkirt);
        else
            Bank(meshes.Banks, stretch, false, outL, -1f, section.ShoulderHeight, section.BankWidth);
        if (section.SidewalkRight > 0f)
            Sidewalk(meshes.Sidewalks, stretch, false, outR, f => outR(f) + section.SidewalkRight, false, sidewalkSkirt);
        else
            Bank(meshes.Banks, stretch, false, outR, 1f, section.ShoulderHeight, section.BankWidth);

        // Lines: the outer edges', and between each group's own lanes.
        LineWhere(meshes.WhiteLines, stretch, false, f => at[f].LaneOutL, _ => true, LineWidth, false);
        LineWhere(meshes.WhiteLines, stretch, false, f => at[f].LaneOutR, _ => true, LineWidth, false);
        foreach (var (group, left) in new[] { (fork.Left, true), (fork.Right, false) })
        {
            for (var i = 1; i < group.Count; i++)
            {
                var edge = group[i].Left;
                LineWhere(meshes.WhiteLines, stretch, false, f => edge + fork.Offset(left, f.Distance), _ => true, LineWidth, true);
            }
        }
        if (fork.TwoWay)
        {
            // Its yellow lines (two together, or one along each side of the median) each carrying on along a group's inner edge.
            var apart = fork.HasMedian ? 0f : LineWidth;
            LineWhere(meshes.YellowLines, stretch, false, f => at[f].LaneInL - apart, _ => true, LineWidth, false);
            LineWhere(meshes.YellowLines, stretch, false, f => at[f].LaneInR + apart, _ => true, LineWidth, false);
        }
        else
        {
            // The broken line between them while they are side by side, then an edge line along each.
            bool Side(RoadFrame f) => at[f].LaneInR - at[f].LaneInL < Apart;
            LineWhere(meshes.WhiteLines, stretch, false, f => (at[f].LaneInL + at[f].LaneInR) / 2f, Side, LineWidth, true);
            LineWhere(meshes.WhiteLines, stretch, false, f => at[f].LaneInL, f => !Side(f), LineWidth, false);
            LineWhere(meshes.WhiteLines, stretch, false, f => at[f].LaneInR, f => !Side(f), LineWidth, false);
        }
    }

    /// <summary>A ramp's own surface over its stretch, with its skirts, banks and lines.</summary>
    private static void Ramp(RoadMeshes meshes, IReadOnlyList<RoadFrame> frames, RoadCrossSection section, RampSpec ramp, float sidewalkSkirt)
    {
        // Its frames turned to run along its lanes where they bend away, so it keeps its width there.
        var stretch = RoadCurve.Trim(frames, ramp.Start, ramp.End).Select(f => RoadRampShaper.Turned(f, ramp, section)).ToList();
        if (stretch.Count < 2)
            return;
        var right = ramp.Side == RoadSide.Right;
        (float LaneIn, float LaneOut, float EdgeIn, float EdgeOut) At(RoadFrame f) => RoadRampShaper.Across(ramp, section, f.Distance);

        // Left to right across it, whichever side it is on.
        Func<RoadFrame, float> leftEdge = right ? f => At(f).EdgeIn : f => At(f).EdgeOut;
        Func<RoadFrame, float> rightEdge = right ? f => At(f).EdgeOut : f => At(f).EdgeIn;
        Strip(meshes.Surface, stretch, false, leftEdge, rightEdge, 0f);
        Skirt(meshes.Surface, stretch, false, leftEdge, -1f, 0f, SkirtDepth);
        Skirt(meshes.Surface, stretch, false, rightEdge, 1f, 0f, SkirtDepth);
        var bankWidth = ramp.Section.BankWidth;
        var sign = right ? 1f : -1f;
        var outerWalk = ramp.Sidewalk > 0f;
        var innerWalk = ramp.Sidewalk > 0f && ramp.InnerSidewalk;
        // Its banks give way to its sidewalks, where it has them: the ground meets those.
        if (!(right ? innerWalk : outerWalk))
            Bank(meshes.Banks, stretch, false, leftEdge, -1f, ramp.ShoulderHeight, bankWidth);
        if (!(right ? outerWalk : innerWalk))
            Bank(meshes.Banks, stretch, false, rightEdge, 1f, ramp.ShoulderHeight, bankWidth);

        // The road's sidewalk on its side, if it has one: the ramp's carries on from it.
        var roadWalk = RoadSidewalk(ramp, section);
        if (outerWalk)
        {
            // As wide as the road's where its lane starts from nothing, easing to its own as the lane widens, so they meet flush.
            var from = roadWalk?.Width ?? ramp.Sidewalk;
            float Width(RoadFrame f)
            {
                var grown = Math.Clamp(RoadRampShaper.Profile(ramp, section, f.Distance).Width / MathF.Max(ramp.Width, 0.01f), 0f, 1f);
                grown = grown * grown * (3f - 2f * grown);
                return from + (ramp.Sidewalk - from) * grown;
            }
            Sidewalk(meshes.Sidewalks, stretch, false, f => At(f).EdgeOut, f => At(f).EdgeOut + sign * Width(f), !right, sidewalkSkirt);
        }
        if (innerWalk)
        {
            // Between it and the road. Until the road's own sidewalk is back (where it has one), all the way across to
            // the road's paved edge, standing in for it; then its own width or what room is left, out to the road's.
            var runs = PavedRuns(section).ToList();
            var paved = right ? runs[^1].Right : runs[0].Left;
            // From the nose, where the ramp's inner edge first comes clear of the road's paving, so no wedge is left bare.
            // The road's paved edge across its turned frame: where that crosses the road's, not the same offset out.
            float PavedIn(RoadFrame f)
            {
                var road = RoadCurve.At(frames, f.Distance);
                return (paved - Vector3.Dot(f.Position - road.Position, road.Right)) / Vector3.Dot(f.Right, road.Right);
            }
            float Width(RoadFrame f)
            {
                var room = sign * (At(f).EdgeIn - PavedIn(f));
                if (roadWalk is not null)
                    return BothWalksFit(ramp, section, f.Distance) ? ramp.Sidewalk : room;
                return MathF.Min(ramp.Sidewalk, room);
            }
            foreach (var run in Runs(stretch, f => sign * (At(f).EdgeIn - PavedIn(f)) > -0.05f))
                Sidewalk(meshes.Sidewalks, run, false, f => At(f).EdgeIn, f => At(f).EdgeIn - sign * MathF.Max(Width(f), 0f), right, sidewalkSkirt);
        }

        bool Wide(RoadFrame f) => RoadRampShaper.Profile(ramp, section, f.Distance).Width > ramp.Width * 0.5f;
        bool Away(RoadFrame f) => RoadRampShaper.Profile(ramp, section, f.Distance).Gap >= Apart;
        // Its outer edge line all along, centred on its lane's edge as the road's edge line is: where
        // its lane starts from nothing, it carries straight on from the road's.
        LineWhere(meshes.WhiteLines, stretch, false, f => At(f).LaneOut, _ => true, LineWidth, false);
        // Its inner edge line, where it has come away from the road.
        LineWhere(meshes.WhiteLines, stretch, false, f => At(f).LaneIn, Away, LineWidth, false);
        // Dashed lines between its own lanes, once they are wide enough to tell apart.
        for (var lane = 1; lane < ramp.Lanes; lane++)
        {
            var share = lane / (float)ramp.Lanes;
            LineWhere(meshes.WhiteLines, stretch, false, f => At(f).LaneIn + (At(f).LaneOut - At(f).LaneIn) * share, Wide, LineWidth, true);
        }
    }

    /// <summary>How far along a ramp a distance along the road is, from the end where its lane joins the road.</summary>
    private static float FromJoin(RampSpec ramp, RoadCrossSection section, float distance)
    {
        var along = Math.Clamp(distance - ramp.Start, 0f, ramp.Length);
        return RoadRampShaper.SeparatedAtStart(ramp, section) ? ramp.Length - along : along;
    }

    /// <summary>
    /// The road's frames with more where each ramp starts and ends: one there and one just outside,
    /// so the road's edge steps in to the ramp's straight across, where the ramp's surface begins,
    /// instead of slanting in over the frame spacing and leaving a notch.
    /// </summary>
    private static IReadOnlyList<RoadFrame> WithBreaks(IReadOnlyList<RoadFrame> frames, IReadOnlyList<RampSpec> ramps, RoadCrossSection section)
    {
        var first = frames[0].Distance;
        var last = frames[^1].Distance;
        var breaks = ramps.SelectMany(r => new[] { r.Start - BreakGap, r.Start, r.End, r.End + BreakGap, r.Start + RampLineDelay, r.End - RampLineDelay })
            .Concat(ramps.SelectMany(r => RoomForSidewalk(r, section) is { } d ? new[] { d - BreakGap, d } : Array.Empty<float>()))
            .Concat(ramps.Where(r => r.InnerSidewalk && r.Sidewalk > 0f).SelectMany(r => Nose(r, section) is { } d ? new[] { d } : Array.Empty<float>()))
            .Where(d => d > first && d < last && frames.All(f => MathF.Abs(f.Distance - d) > BreakGap / 2f))
            .Distinct()
            .Select(d => RoadCurve.At(frames, d));
        return frames.Concat(breaks).OrderBy(f => f.Distance).ToList();
    }

    /// <summary>
    /// Where a ramp has come far enough away from the road for the road's sidewalk on its side to come back (its inner
    /// edge reaching the sidewalk's far side), if the road has one there and it does.
    /// </summary>
    private static float? RoomForSidewalk(RampSpec ramp, RoadCrossSection section)
    {
        if (RoadSidewalk(ramp, section) is null)
            return null;
        bool Room(float d) => BothWalksFit(ramp, section, d);
        // Its separated end is the far one from where it joins: search from the joined end towards it.
        var separatedAtStart = RoadRampShaper.SeparatedAtStart(ramp, section);
        var (joined, apart) = separatedAtStart ? (ramp.End, ramp.Start) : (ramp.Start, ramp.End);
        if (!Room(apart))
            return null;
        for (var i = 0; i < 40; i++)
        {
            var middle = (joined + apart) / 2f;
            if (Room(middle))
                apart = middle;
            else
                joined = middle;
        }
        return apart;
    }

    /// <summary>
    /// Whether a ramp is far enough out from the road here for the road's sidewalk on its side to be back, with the
    /// ramp's inner sidewalk (if it has one) whole beside it. Until then the ramp's inner sidewalk fills the gap alone.
    /// </summary>
    private static bool BothWalksFit(RampSpec ramp, RoadCrossSection section, float distance)
    {
        if (RoadSidewalk(ramp, section) is not { } walk)
            return true;
        var sign = ramp.Side == RoadSide.Right ? 1f : -1f;
        var far = ramp.Side == RoadSide.Right ? walk.Right : walk.Left;
        var inner = ramp.InnerSidewalk && ramp.Sidewalk > 0f ? ramp.Sidewalk : 0f;
        return sign * (RoadRampShaper.Across(ramp, section, distance).EdgeIn - far) >= inner;
    }

    /// <summary>Where a ramp's inner edge comes clear of the road's paving, so its inner sidewalk starts from a point there.</summary>
    private static float? Nose(RampSpec ramp, RoadCrossSection section)
    {
        var runs = PavedRuns(section).ToList();
        var right = ramp.Side == RoadSide.Right;
        var paved = right ? runs[^1].Right : runs[0].Left;
        bool Clear(float d) => right ? RoadRampShaper.Across(ramp, section, d).EdgeIn > paved : RoadRampShaper.Across(ramp, section, d).EdgeIn < paved;
        var (joined, apart) = RoadRampShaper.SeparatedAtStart(ramp, section) ? (ramp.End, ramp.Start) : (ramp.Start, ramp.End);
        if (!Clear(apart) || Clear(joined))
            return null;
        for (var i = 0; i < 40; i++)
        {
            var middle = (joined + apart) / 2f;
            if (Clear(middle))
                apart = middle;
            else
                joined = middle;
        }
        return joined;
    }

    /// <summary>The road's sidewalk on a ramp's side, if it has one.</summary>
    private static RoadBand? RoadSidewalk(RampSpec ramp, RoadCrossSection section)
    {
        var bands = section.Bands();
        var outer = ramp.Side == RoadSide.Right ? bands[^1] : bands[0];
        return outer.Kind == BandKind.Sidewalk ? outer : null;
    }

    /// <summary>The runs of two or more frames in a row where the test holds.</summary>
    private static IEnumerable<IReadOnlyList<RoadFrame>> Runs(IReadOnlyList<RoadFrame> frames, Func<RoadFrame, bool> where)
    {
        var run = new List<RoadFrame>();
        foreach (var frame in frames)
        {
            if (where(frame))
            {
                run.Add(frame);
                continue;
            }
            if (run.Count > 1)
                yield return run;
            run = new List<RoadFrame>();
        }
        if (run.Count > 1)
            yield return run;
    }

    /// <summary>
    /// A line along the road, centred where offset says, width wide, only where the test holds:
    /// solid there, or in dashes of dash metres with gap between (counted along by dashAlong).
    /// </summary>
    private static void LineWhere(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, Func<RoadFrame, float> offset,
        Func<RoadFrame, bool> where, float width, bool dashed, float dash = DashLength, float gap = DashGap, Func<RoadFrame, float>? dashAlong = null)
    {
        dashAlong ??= f => f.Distance;
        Func<RoadFrame, float> left = f => offset(f) - width / 2f;
        Func<RoadFrame, float> right = f => offset(f) + width / 2f;
        if (!dashed && frames.All(where))
        {
            Strip(mesh, frames, closed, left, right, LineLift);
            return;
        }
        foreach (var run in Runs(frames, f => where(f) && (!dashed || MathF.Abs(dashAlong(f)) % (dash + gap) < dash)))
            Strip(mesh, run, false, left, right, LineLift);
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

    private static void Strip(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, float left, float right, float lift) =>
        Strip(mesh, frames, closed, _ => left, _ => right, lift);

    /// <summary>A flat strip along the road from left to right of its centre line (at each frame), lift above its surface.</summary>
    private static void Strip(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, Func<RoadFrame, float> leftAt, Func<RoadFrame, float> rightAt, float lift)
    {
        var count = frames.Count + (closed ? 1 : 0);
        var length = closed ? frames[^1].Distance + Vector3.Distance(frames[^1].Position, frames[0].Position) : frames[^1].Distance;
        int? lastLeft = null, lastRight = null;
        for (var i = 0; i < count; i++)
        {
            var frame = frames[i % frames.Count];
            var along = i == frames.Count ? length : frame.Distance;
            var up = frame.Up;
            var (left, right) = (leftAt(frame), rightAt(frame));
            var l = mesh.Add(frame.Across(left) + up * lift, up, new Vector2(left, along));
            var r = mesh.Add(frame.Across(right) + up * lift, up, new Vector2(right, along));
            if (lastLeft is { } pl && lastRight is { } pr)
                mesh.Quad(pl, pr, r, l);
            (lastLeft, lastRight) = (l, r);
        }
    }

    /// <summary>A strip straight down from offset across the road (drop below its surface) by depth, facing out (side -1 left, 1 right).</summary>
    private static void Skirt(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, Func<RoadFrame, float> offset, float side, float drop, float depth)
    {
        var count = frames.Count + (closed ? 1 : 0);
        int? lastTop = null, lastBottom = null;
        for (var i = 0; i < count; i++)
        {
            var frame = frames[i % frames.Count];
            var outward = side * frame.Right;
            var top = frame.Across(offset(frame)) - Vector3.UnitY * drop;
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
    private static void Bank(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, Func<RoadFrame, float> edge, float side, float height, float width)
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
            var t = mesh.Add(frame.Across(edge(frame)), normal, new Vector2(0f, frame.Distance));
            var b = mesh.Add(frame.Across(edge(frame) + side * width) - Vector3.UnitY * height, normal, new Vector2(slope, frame.Distance));
            if (lastTop is { } pt && lastBottom is { } pb)
                mesh.Quad(pt, pb, b, t);
            (lastTop, lastBottom) = (t, b);
        }
        Skirt(mesh, frames, closed, f => edge(f) + side * width, side, height, SkirtDepth);
    }

    /// <summary>A dashed line along the road, centred offset right of its centre line.</summary>
    private static void Dashes(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, float offset)
    {
        var length = frames[^1].Distance;
        for (var from = DashGap / 2f; from + DashLength <= length; from += DashLength + DashGap)
        {
            var piece = RoadCurve.Trim(frames, from, from + DashLength);
            int? lastLeft = null, lastRight = null;
            foreach (var frame in piece)
            {
                var up = frame.Up;
                var l = mesh.Add(frame.Across(offset - LineWidth / 2f) + up * LineLift, up, new Vector2(0f, frame.Distance));
                var r = mesh.Add(frame.Across(offset + LineWidth / 2f) + up * LineLift, up, new Vector2(1f, frame.Distance));
                if (lastLeft is { } pl && lastRight is { } pr)
                    mesh.Quad(pl, pr, r, l);
                (lastLeft, lastRight) = (l, r);
            }
        }
    }

    /// <summary>A sidewalk: its top, the curb face down to the road, and its outer face down into the ground.</summary>
    private static void Sidewalk(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, RoadBand band, bool onLeft, float skirt) =>
        Sidewalk(mesh, frames, closed, _ => onLeft ? band.Right : band.Left, _ => onLeft ? band.Left : band.Right, onLeft, skirt);

    /// <summary>A sidewalk from its curb out to its outer edge (each at every frame), as above.</summary>
    private static void Sidewalk(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, Func<RoadFrame, float> curbAt, Func<RoadFrame, float> outerAt,
        bool onLeft, float skirt)
    {
        var count = frames.Count + (closed ? 1 : 0);
        var top = RoadCrossSection.CurbHeight;
        int[]? last = null;
        for (var i = 0; i < count; i++)
        {
            var frame = frames[i % frames.Count];
            var along = i == frames.Count ? frames[^1].Distance + Vector3.Distance(frames[^1].Position, frames[0].Position) : frame.Distance;
            var up = frame.Up;
            var (curb, outer) = (curbAt(frame), outerAt(frame));
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
