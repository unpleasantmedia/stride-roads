using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.Roads;

/// <summary>
/// A fork built into one end of the road whose entity it is on (its trunk): there its lanes part into two groups,
/// easing apart until a gore lies between them, and a branch road carries on from each (drawn from the ring at its
/// end). On a road one way only, the traffic's left <see cref="LeftLanes"/> lanes go one way and the rest the other;
/// on one both ways, its two directions part, each into a one-way branch. It splits where the trunk's traffic goes
/// into it, and merges where it comes out of it: the same shape either way.
/// </summary>
[DataContract("RoadForkComponent")]
[Display("Road fork")]
[ComponentCategory("Roads")]
public sealed class RoadForkComponent : EntityComponent
{
    /// <summary>Whether it is at the road's last point, rather than its first.</summary>
    [DataMember(0), DefaultValue(true)]
    public bool AtEnd { get; set; } = true;

    /// <summary>How long it takes its groups to part, along the road, in metres.</summary>
    [DataMember(10), DataMemberRange(5, 300, 1, 10, 0), DefaultValue(40f)]
    public float Length { get; set; } = 40f;

    /// <summary>On a road one way only: how many of its lanes, from the traffic's left, go to the left branch. Both ways, its backward lanes do.</summary>
    [DataMember(20), DataMemberRange(1, 8, 1, 1, 0), DefaultValue(1)]
    public int LeftLanes { get; set; } = 1;

    /// <summary>How wide the gore between its groups' shoulders is where its branches carry on, in metres.</summary>
    [DataMember(30), DataMemberRange(0, 30, 0.1, 1, 1), DefaultValue(2f)]
    public float Separation { get; set; } = 2f;

    /// <summary>It, as the road's maths takes it.</summary>
    [DataMemberIgnore]
    public ForkSpec Spec => new(AtEnd, Length, LeftLanes, Separation);
}
