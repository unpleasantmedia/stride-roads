using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Stride.Roads;

/// <summary>
/// Where the ground should be for a road on the terrain's grid: at each grid point near the road,
/// the height it wants (the road's surface there, less <see cref="RoadGround.Sink"/>) and how much
/// (1 under the road, its banks and a margin past them, easing to nothing over the falloff beyond),
/// and how far out past the paved surface it is.
/// </summary>
/// <remarks>
/// Off a road stretch's end, Outside is pushed far out for ranking (so it gives way); Past is then how
/// far out past the paving it really is.
/// </remarks>
public readonly record struct GroundTarget(float Height, float Weight, float Outside = 0f, float? Past = null)
{
    /// <summary>How far out past the paved surface it really is, whatever its ranking.</summary>
    public float Beyond => Past ?? Outside;

    /// <summary>
    /// Whether this wants a grid point more than that does: the one wanting it more, or where both
    /// want it fully, the one nearer its paving, so a ramp's lane is not dug into by the road's bank
    /// beside it, nor a junction's by its roads'. Where both are as near (both under their surfaces:
    /// a parking lot's drive across a road's sidewalk), the lower, so the ground is under both.
    /// </summary>
    public bool Beats(GroundTarget other) =>
        Weight > other.Weight || (Weight == other.Weight && (Outside < other.Outside || (Outside == other.Outside && Height < other.Height)));
}

public static class RoadGround
{
    /// <summary>How far under a road's surface the ground is put, so it never shows through.</summary>
    public const float Sink = 0.2f;

    /// <summary>How far above the road the ground under and beside a sidewalk is put: under its top, a curb's height up, by a little.</summary>
    public const float WalkGround = RoadCrossSection.CurbHeight - 0.1f;

    /// <summary>What a grid point off the end of the stretch it is measured against counts as being further out, so it gives way.</summary>
    private const float OffEnd = 1000f;

    /// <summary>
    /// The ground wanted under a road, on a grid spacing metres apart, keyed by (x, z) grid index:
    /// flat under the road, down across its banks (see <see cref="RoadCrossSection.ShoulderHeight"/>) as far as they
    /// are left uncovered by bankCover (0: all the way down them; 1: level, burying them),
    /// flat for margin past them, then easing back over falloff.
    /// Where it passes a grid point more than once (a tight bend), the fullest weight wins.
    /// </summary>
    public static Dictionary<(int X, int Z), GroundTarget> Targets(
        IReadOnlyList<RoadFrame> frames, RoadCrossSection section, float spacing, float margin = 1f, float falloff = 12f,
        float bankCover = 0f)
    {
        var targets = new Dictionary<(int X, int Z), GroundTarget>();
        if (frames.Count == 0)
            return targets;

        var bands = section.Bands();
        // Past a sidewalk, flat a grid step further before its bank: the ground between grid points
        // would otherwise rise (or fall) into the sidewalk from the next one out, up a cutting.
        var left = bands[0].Left - (section.SidewalkLeft > 0f ? spacing : 0f);
        var right = bands[^1].Right + (section.SidewalkRight > 0f ? spacing : 0f);
        var bank = section.BankWidth;
        var reach = bank + margin + falloff + spacing;

        // Every grid point near each stretch between two frames, measured against that stretch.
        // Past a stretch's ends it counts too (measured from the frame there), but for the road's
        // own two ends: on the outside of a bend the stretches fan apart, and the ground in the
        // wedges between them wants the road's height as much as the rest.
        var stretches = Math.Max(frames.Count - 1, 1);
        for (var i = 0; i < stretches; i++)
        {
            var a = frames[i];
            var b = frames[Math.Min(i + 1, frames.Count - 1)];
            var (openBefore, openAfter) = (i > 0, i < stretches - 1);
            var min = Vector3.Min(Vector3.Min(a.Across(left - reach), a.Across(right + reach)), Vector3.Min(b.Across(left - reach), b.Across(right + reach)));
            var max = Vector3.Max(Vector3.Max(a.Across(left - reach), a.Across(right + reach)), Vector3.Max(b.Across(left - reach), b.Across(right + reach)));
            for (var gx = (int)MathF.Floor(min.X / spacing); gx <= (int)MathF.Ceiling(max.X / spacing); gx++)
            for (var gz = (int)MathF.Floor(min.Z / spacing); gz <= (int)MathF.Ceiling(max.Z / spacing); gz++)
            {
                var point = new Vector3(gx * spacing, 0f, gz * spacing);
                if (!Across(a, b, point, openBefore, openAfter, out var offset, out var height, out var offEnd))
                    continue;

                // How far out past the road's edge, and past its bank.
                var outside = MathF.Max(left - offset, offset - right);
                // Down its bank, but only so far: the ground covers that share of it. A side with a
                // sidewalk has none: the ground meets the sidewalk.
                var walked = offset < left ? section.SidewalkLeft > 0f : offset > right && section.SidewalkRight > 0f;
                var sideBank = walked ? 0f : bank;
                var down = Down(section.ShoulderHeight, sideBank, Climb(section.ShoulderHeight, outside, spacing), bankCover);
                var beyond = outside - sideBank - margin;
                var weight = beyond <= 0f ? 1f : 1f - SmoothStep(beyond / falloff);
                if (weight <= 0f)
                    continue;

                // Off a stretch's end, it gives way to the stretch the point lies beside, wanting it just as much.
                // Just past a sidewalk, the ground comes up to just under its top, to meet it. Not under it: a
                // grid cell there reaches the asphalt, and the ground across it would rise through that.
                var pastWalk = outside <= 0f && (offset < bands[0].Left && section.SidewalkLeft > 0f || offset > bands[^1].Right && section.SidewalkRight > 0f);
                var sink = pastWalk ? -WalkGround : Sink;
                // Off a stretch's end, the nearer that end the more it counts: inside a tight bend a point
                // lies off the ends of many stretches, all under the road, and the first measured (far round
                // the bend, at another height) won, raising the ground through the road.
                var wanted = new GroundTarget(height - sink - down, weight, MathF.Max(outside, 0f) + (offEnd ? OffEnd + MathF.Abs(offset) : 0f),
                    MathF.Max(outside, 0f));
                if (!targets.TryGetValue((gx, gz), out var had) || wanted.Beats(had))
                    targets[(gx, gz)] = wanted;
            }
        }

        return targets;
    }


    /// <summary>
    /// Whether a point lies beside the stretch of road from a to b (not before a nor past b), and
    /// if so how far right of its centre line it is and how high the road is there.
    /// </summary>
    /// <remarks>
    /// Before a (or past b), where the stretch has a neighbour that way, a point counts too: as far
    /// out from a (or b) as it is, on the side it lies, as on the outside of a bend.
    /// </remarks>
    private static bool Across(RoadFrame a, RoadFrame b, Vector3 point, bool openBefore, bool openAfter, out float offset, out float height, out bool offEnd)
    {
        var start = new Vector2(a.Position.X, a.Position.Z);
        var end = new Vector2(b.Position.X, b.Position.Z);
        var at = new Vector2(point.X, point.Z);
        var along = end - start;
        var t = along.LengthSquared() > 1e-8f ? Vector2.Dot(at - start, along) / along.LengthSquared() : 0f;
        offset = 0f;
        height = 0f;
        offEnd = false;
        if ((t < -1e-4f && !openBefore) || (t > 1f + 1e-4f && !openAfter))
            return false;

        var beyond = t < 0f || t > 1f;
        offEnd = beyond;
        t = Math.Clamp(t, 0f, 1f);
        var right = Vector3.Normalize(Vector3.Lerp(a.Right, b.Right, t));
        var centre = Vector3.Lerp(a.Position, b.Position, t);
        var fromCentre = at - new Vector2(centre.X, centre.Z);
        offset = Vector2.Dot(fromCentre, new Vector2(right.X, right.Z));
        // Off the stretch's end: as far out as it really is from that end, on its side.
        if (beyond)
            offset = MathF.CopySign(fromCentre.Length(), offset);
        height = centre.Y;
        return true;
    }

    /// <summary>
    /// How far out along a bank a point is, for how far down (or up) it the ground is: a bank rising from
    /// the road (a cutting) starts a grid step out past its edge. The ground is drawn in triangles
    /// between grid points, and one up the bank within a step of the edge tilted the triangle under the
    /// road's edge up through the asphalt.
    /// </summary>
    private static float Climb(float bankHeight, float outside, float spacing) =>
        bankHeight < 0f ? MathF.Max(outside - spacing, 0f) : outside;

    /// <summary>
    /// How far the ground is down a bank height deep (up, if negative) and width wide, this far out
    /// past the road's edge, with cover (0 to 1) of its height buried: the ground follows it down
    /// only the rest of the way.
    /// </summary>
    private static float Down(float height, float width, float outside, float cover) =>
        height * (1f - Math.Clamp(cover, 0f, 1f)) * (width > 0f ? Math.Clamp(outside / width, 0f, 1f) : 0f);

    private static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
