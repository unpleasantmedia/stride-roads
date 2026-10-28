using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Stride.Roads;

/// <summary>Which side of a road, looking along it from its start.</summary>
public enum RoadSide
{
    Right,
    Left,
}

/// <summary>Whether a ramp takes traffic off a road or brings it on.</summary>
public enum RampKind
{
    Exit,
    Entrance,
}

/// <summary>
/// A ramp built into a road: over Length metres from Start metres along it, on one side, a lane of
/// its own tapers in beside the road's outer lane, runs alongside it, and peels away until its
/// centre is Separation metres out from the road's lane edge, where the ramp's own road carries
/// on, heading EndAngle degrees out from the road's way (0: alongside it again). An exit peels
/// away in the way its side's traffic goes; an entrance comes in from that way.
/// </summary>
public readonly record struct RampSpec(
    float Start,
    float Length,
    RoadSide Side,
    RampKind Kind,
    int Lanes = 1,
    float LaneWidth = 4f,
    float ShoulderWidth = 1.5f,
    float Taper = 40f,
    float Separation = 12f,
    float ShoulderHeight = 0f,
    float ShoulderAngle = RoadCrossSection.WalkableAngle,
    float Alongside = 0f,
    float EndAngle = 0f,
    float Sidewalk = 0f,
    bool InnerSidewalk = false,
    RoadBarrier Gore = RoadBarrier.Rail)
{
    /// <summary>How long it takes to swing away from the road, after its taper and its run alongside.</summary>
    public float Diverge => MathF.Max(Length - Taper - Alongside, 0.01f);

    public float End => Start + Length;

    /// <summary>The ramp's own road's cross-section: one way, its lanes, its banks.</summary>
    public RoadCrossSection Section => new(Lanes, 0, LaneWidth, ShoulderWidth, ShoulderHeight: ShoulderHeight, ShoulderAngle: ShoulderAngle);

    public float Width => Lanes * LaneWidth;
}

/// <summary>
/// The ramp's shape along its stretch of road: at each distance along, how far out its lanes
/// reach, and where its road's edges are. Offsets are right of the road's centre line, so a ramp
/// on the left has negative ones.
/// </summary>
public static class RoadRampShaper
{
    /// <summary>
    /// The middle of a ramp's lanes at its free end (where it has come away from the road, and a road can carry on from
    /// it), and the way out of the ramp there, in the road's space.
    /// </summary>
    public static (Vector3 Position, Vector3 Outward) FreeEnd(IReadOnlyList<RoadFrame> frames, RampSpec ramp, RoadCrossSection road)
    {
        var atStart = SeparatedAtStart(ramp, road);
        var frame = Turned(RoadCurve.At(frames, atStart ? ramp.Start : ramp.End), ramp, road);
        var (laneIn, laneOut, _, _) = Across(ramp, road, frame.Distance);
        return (frame.Across((laneIn + laneOut) / 2f), atStart ? -frame.Forward : frame.Forward);
    }

    /// <summary>Whether a ramp's lanes are separated from the road at the start of its stretch (else at its end).</summary>
    public static bool SeparatedAtStart(RampSpec ramp, RoadCrossSection road)
    {
        // Which way the traffic on that side goes: the right has the road's forward lanes, the
        // left its backward ones, unless the road is one way only.
        var sideFlowsForward = ramp.Side == RoadSide.Right ? road.ForwardLanes > 0 : road.BackwardLanes == 0;
        var exit = ramp.Kind == RampKind.Exit;
        // An exit separates in the way its traffic goes; an entrance joins in that way.
        return exit != sideFlowsForward;
    }

    /// <summary>How wide the road's shoulder on the ramp's side is, if it has one.</summary>
    public static float SideShoulder(RampSpec ramp, RoadCrossSection road)
    {
        var right = ramp.Side == RoadSide.Right;
        return road.Bands().Where(b => b.Kind == BandKind.Shoulder && (right ? b.Left >= 0f : b.Right <= 0f)).Sum(b => b.Width);
    }

    /// <summary>Where the road's outer lane edge is on the ramp's side: out from the centre line, signed.</summary>
    public static float LaneEdge(RampSpec ramp, RoadCrossSection road)
    {
        var lanes = road.Bands().Where(b => b.Kind == BandKind.Lane).ToList();
        return ramp.Side == RoadSide.Right ? lanes.Max(b => b.Right) : lanes.Min(b => b.Left);
    }

    /// <summary>
    /// How far along its stretch the ramp is, from where it joins the road's lane (0) to where it
    /// has fully peeled away (1): the taper first widens its lane from nothing, then it swings out.
    /// </summary>
    public static (float Width, float Gap) Profile(RampSpec ramp, RoadCrossSection road, float distance)
    {
        var s = Math.Clamp(distance - ramp.Start, 0f, ramp.Length);
        // Measured from the joined end.
        var fromJoin = SeparatedAtStart(ramp, road) ? ramp.Length - s : s;
        var width = ramp.Width * SmoothStep(fromJoin / MathF.Max(ramp.Taper, 0.01f));
        // Full width alongside the road for a while before it swings away: out from the lane edge
        // to the ramp lanes' inner edge, easing out of the road's way and into its end angle.
        var t = Math.Clamp((fromJoin - ramp.Taper - ramp.Alongside) / ramp.Diverge, 0f, 1f);
        var gap = Swing(ramp) * t * t * (3f - 2f * t) + (t * t * t - t * t) * ramp.Diverge * Bend(ramp);
        return (width, gap);
    }

    /// <summary>How far out it swings, from the road's lane edge to its lanes' inner edge.</summary>
    private static float Swing(RampSpec ramp) => MathF.Max(ramp.Separation - ramp.Width / 2f, 0f);

    /// <summary>
    /// How much it heads away from the road where it ends, as the tangent of its end angle: no more
    /// than it can bend into over its swing without first bending back in towards the road.
    /// </summary>
    public static float Bend(RampSpec ramp) =>
        MathF.Min(MathF.Tan(Math.Clamp(ramp.EndAngle, 0f, 80f) * MathF.PI / 180f), 2.9f * Swing(ramp) / ramp.Diverge);

    /// <summary>
    /// How fast its lanes move out from the road at a distance along it, in metres out per metre
    /// along the road (positive to the road's right).
    /// </summary>
    public static float Slope(RampSpec ramp, RoadCrossSection road, float distance)
    {
        // Its ends count as on it, rounding and all: Start + Length far round a long road can land
        // a hair either side of the distance asked about, and its end is where its road meets it.
        var s = distance - ramp.Start;
        const float Tolerance = 0.01f;
        if (s < -Tolerance || s > ramp.Length + Tolerance)
            return 0f;
        s = Math.Clamp(s, 0f, ramp.Length);
        var separatedAtStart = SeparatedAtStart(ramp, road);
        var fromJoin = separatedAtStart ? ramp.Length - s : s;
        var t = Math.Clamp((fromJoin - ramp.Taper - ramp.Alongside) / ramp.Diverge, 0f, 1f);
        if (fromJoin < ramp.Taper + ramp.Alongside)
            return 0f;
        var perJoin = (Swing(ramp) * (6f * t - 6f * t * t) + (3f * t * t - 2f * t) * ramp.Diverge * Bend(ramp)) / ramp.Diverge;
        return perJoin * (ramp.Side == RoadSide.Right ? 1f : -1f) * (separatedAtStart ? -1f : 1f);
    }

    /// <summary>
    /// A frame of the road turned to run along the ramp's lanes where they bend away: about the
    /// middle of its lanes, so that across it (by the same offsets from the road's centre line) is
    /// square across the ramp. Its lanes' edges then keep their widths where it bends, and its end
    /// is cut square to its own road there.
    /// </summary>
    public static RoadFrame Turned(RoadFrame frame, RampSpec ramp, RoadCrossSection road)
    {
        var slope = Slope(ramp, road, frame.Distance);
        if (MathF.Abs(slope) < 1e-5f)
            return frame;
        var (laneIn, laneOut, _, _) = Across(ramp, road, frame.Distance);
        var middle = (laneIn + laneOut) / 2f;
        var level = new Vector3(frame.Forward.X, 0f, frame.Forward.Z);
        var run = level.Length();
        var way = Vector3.Normalize(Vector3.Normalize(level) + frame.Right * slope);
        var right = Vector3.Normalize(Vector3.Cross(way, Vector3.UnitY));
        var forward = Vector3.Normalize(new Vector3(way.X * run, frame.Forward.Y, way.Z * run));
        return new RoadFrame(frame.Across(middle) - right * middle, forward, right, frame.Distance);
    }

    /// <summary>
    /// The ramp's lanes across at a distance along: their inner and outer edges, and its road's
    /// inner and outer edges (its shoulders), signed out from the road's centre line.
    /// </summary>
    public static (float LaneIn, float LaneOut, float EdgeIn, float EdgeOut) Across(RampSpec ramp, RoadCrossSection road, float distance)
    {
        var (width, gap) = Profile(ramp, road, distance);
        var sign = ramp.Side == RoadSide.Right ? 1f : -1f;
        var edge = LaneEdge(ramp, road);
        var laneIn = edge + sign * gap;
        var laneOut = laneIn + sign * width;
        // Its inner shoulder only where it has come away from the road: joined, it runs straight
        // on from the road's lane.
        var innerShoulder = MathF.Min(ramp.ShoulderWidth, gap);
        var edgeIn = laneIn - sign * innerShoulder;
        // Its outer shoulder carries on the road's where its lane starts from nothing, easing to
        // its own width as the lane widens, so the paving past the lanes never narrows.
        var grown = SmoothStep(width / MathF.Max(ramp.Width, 0.01f));
        var roadShoulder = SideShoulder(ramp, road);
        var outerShoulder = roadShoulder + (ramp.ShoulderWidth - roadShoulder) * grown;
        var edgeOut = laneOut + sign * outerShoulder;
        return (laneIn, laneOut, edgeIn, edgeOut);
    }

    /// <summary>
    /// The ramp's own road's centre line over the ramp's stretch: down the middle of its lanes,
    /// from where it leaves the road to where its own road carries on (for an exit; the reverse
    /// for an entrance, and a ramp against the road's direction).
    /// </summary>
    public static IReadOnlyList<RoadFrame> BranchFrames(IReadOnlyList<RoadFrame> roadFrames, RampSpec ramp, RoadCrossSection road)
    {
        var stretch = RoadCurve.Trim(roadFrames, ramp.Start, ramp.End);
        var centres = new List<Vector3>();
        foreach (var frame in stretch)
        {
            var (laneIn, laneOut, _, _) = Across(ramp, road, frame.Distance);
            centres.Add(frame.Across((laneIn + laneOut) / 2f));
        }

        // Its own frames, facing along its own line, in the direction the road runs.
        var frames = new List<RoadFrame>();
        var along = 0f;
        for (var i = 0; i < centres.Count; i++)
        {
            if (i > 0)
                along += Vector3.Distance(centres[i - 1], centres[i]);
            var from = centres[Math.Max(i - 1, 0)];
            var to = centres[Math.Min(i + 1, centres.Count - 1)];
            var way = to - from;
            var level = new Vector3(way.X, 0f, way.Z);
            level = level.LengthSquared() > 1e-8f ? Vector3.Normalize(level) : stretch[i].Forward;
            frames.Add(new RoadFrame(centres[i], Vector3.Normalize(way.LengthSquared() > 1e-8f ? way : level), Vector3.Normalize(Vector3.Cross(level, Vector3.UnitY)), along));
        }

        return frames;
    }

    /// <summary>
    /// Where the ramp's own road meets it: at its separated end, down the middle of its lanes,
    /// facing the way that road runs on from there (away from the ramp for an exit, in towards
    /// it for an entrance, as its traffic goes).
    /// </summary>
    public static RoadFrame BranchEnd(IReadOnlyList<RoadFrame> roadFrames, RampSpec ramp, RoadCrossSection road)
    {
        var branch = BranchFrames(roadFrames, ramp, road);
        var separatedAtStart = SeparatedAtStart(ramp, road);
        var end = separatedAtStart ? branch[0] : branch[^1];
        // Its own road meets it facing the way its lanes run there, square across them, as the
        // ramp's surface is cut, with no wedge between. The road's way, then away from the road
        // for an exit, towards it for an entrance.
        // Its lanes head away at its end angle there (along the road at none).
        var along = Turned(RoadCurve.At(roadFrames, separatedAtStart ? ramp.Start : ramp.End), ramp, road).Forward;
        var outward = separatedAtStart ? -along : along;
        var travelling = ramp.Kind == RampKind.Exit ? outward : -outward;
        var level = Vector3.Normalize(new Vector3(travelling.X, 0f, travelling.Z));
        return new RoadFrame(end.Position, travelling, Vector3.Normalize(Vector3.Cross(level, Vector3.UnitY)), 0f);
    }

    private static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
