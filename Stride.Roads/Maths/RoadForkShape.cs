using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Stride.Roads;

/// <summary>
/// A fork built into one end of a road (its trunk): over its last (or first) <see cref="Length"/> metres, the trunk's
/// lanes part into two groups that ease apart until <see cref="Separation"/> metres of gore lie between their
/// shoulders, where a branch road carries on from each. On a road one way only, the traffic's left
/// <see cref="LeftLanes"/> lanes go one way and the rest the other; on one both ways, its two directions part.
/// Whether it splits or merges is only which way the traffic goes.
/// </summary>
public readonly record struct ForkSpec(bool AtEnd = true, float Length = 40f, int LeftLanes = 1, float Separation = 2f, RoadBarrier Gore = RoadBarrier.Rail);

/// <summary>A fork's shape along its trunk, from where it starts parting to where its branches carry on.</summary>
public sealed class RoadForkShape
{
    private readonly float aside;

    /// <summary>A fork on a road with this cross-section whose frames run from <paramref name="first"/> to <paramref name="last"/> metres along.</summary>
    public RoadForkShape(RoadCrossSection section, ForkSpec fork, float first, float last)
    {
        Section = section;
        Fork = fork;
        Length = Math.Clamp(fork.Length, 1f, MathF.Max(last - first - 1f, 1f));
        From = fork.AtEnd ? last - Length : first;
        To = fork.AtEnd ? last : first + Length;
        var lanes = section.Bands().Where(b => b.Kind == BandKind.Lane).OrderBy(b => b.Middle).ToList();
        TwoWay = section.ForwardLanes > 0 && section.BackwardLanes > 0;
        var leftCount = TwoWay ? section.BackwardLanes : Math.Clamp(fork.LeftLanes, 1, Math.Max(lanes.Count - 1, 1));
        Left = lanes.Take(leftCount).ToList();
        Right = lanes.Skip(leftCount).ToList();
        HasMedian = TwoWay && section.MedianWidth > 0f;
        // Far enough apart, each group half the way, for both inner shoulders and the gore between.
        var startGap = Right.Count > 0 ? Right[0].Left - Left[^1].Right : 0f;
        aside = MathF.Max(fork.Separation + 2f * section.ShoulderWidth - startGap, 0f) / 2f;
    }

    public RoadCrossSection Section { get; }
    public ForkSpec Fork { get; }

    /// <summary>How long it is along its trunk: no longer than the trunk leaves room for.</summary>
    public float Length { get; }

    /// <summary>Where along the trunk it starts and ends.</summary>
    public float From { get; }
    public float To { get; }

    /// <summary>Whether its trunk goes both ways: its groups are its two directions.</summary>
    public bool TwoWay { get; }
    public bool HasMedian { get; }

    /// <summary>The trunk's lanes in each group: the traffic's left (the trunk's left) and its right.</summary>
    public IReadOnlyList<RoadBand> Left { get; }
    public IReadOnlyList<RoadBand> Right { get; }

    /// <summary>Whether it can part the trunk: it needs a lane for each group.</summary>
    public bool Valid => Left.Count > 0 && Right.Count > 0;

    /// <summary>How far along it a distance along the trunk is, from where it starts parting.</summary>
    public float Along(float distance) => Math.Clamp(Fork.AtEnd ? distance - From : To - distance, 0f, Length);

    /// <summary>How far a group has moved aside at a distance along the trunk, signed across it: easing out, level at both ends.</summary>
    public float Offset(bool left, float distance)
    {
        var t = Along(distance) / Length;
        return (left ? -aside : aside) * t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Across it at a distance along the trunk, signed from the trunk's centre line: each group's outer paved edge, the
    /// edges of its lanes, and its inner paved edge. While the gap between the groups is too narrow for both their
    /// inner shoulders, those meet halfway, and it is paved across.
    /// </summary>
    public (float OutL, float LaneOutL, float LaneInL, float InL, float InR, float LaneInR, float LaneOutR, float OutR) Across(float distance)
    {
        var (offL, offR) = (Offset(true, distance), Offset(false, distance));
        var shoulder = Section.ShoulderWidth;
        var (laneOutL, laneInL) = (Left[0].Left + offL, Left[^1].Right + offL);
        var (laneInR, laneOutR) = (Right[0].Left + offR, Right[^1].Right + offR);
        var (inL, inR) = Together(distance) ? ((laneInL + laneInR) / 2f, (laneInL + laneInR) / 2f) : (laneInL + shoulder, laneInR - shoulder);
        return (laneOutL - shoulder, laneOutL, laneInL, inL, inR, laneInR, laneOutR, laneOutR + shoulder);
    }

    /// <summary>Whether its two groups are paved as one here, their gap no wider than their inner shoulders.</summary>
    public bool Together(float distance) =>
        Right[0].Left + Offset(false, distance) - (Left[^1].Right + Offset(true, distance)) <= 2f * Section.ShoulderWidth + 0.01f;

    /// <summary>Where along the trunk its groups stop being paved as one: none if they never do.</summary>
    public float? Parting()
    {
        var (joined, apart) = Fork.AtEnd ? (From, To) : (To, From);
        if (Together(apart) || !Together(joined))
            return null;
        for (var i = 0; i < 40; i++)
        {
            var middle = (joined + apart) / 2f;
            if (Together(middle))
                joined = middle;
            else
                apart = middle;
        }
        return apart;
    }

    /// <summary>Whether a group's traffic goes out of the fork into its branch, rather than in from it.</summary>
    public bool FlowsOut(bool left) => ((left ? Left : Right)[0].Direction == LaneDirection.Forward) == Fork.AtEnd;

    /// <summary>The middle of a group's lanes where its branch carries on, and the way out of the fork there, in the trunk's space.</summary>
    public (Vector3 Position, Vector3 Outward) BranchEnd(IReadOnlyList<RoadFrame> frames, bool left)
    {
        var end = Fork.AtEnd ? To : From;
        var frame = RoadCurve.At(frames, end);
        var group = left ? Left : Right;
        var middle = (group[0].Left + group[^1].Right) / 2f + Offset(left, end);
        return (frame.Across(middle), Fork.AtEnd ? frame.Forward : -frame.Forward);
    }
}
