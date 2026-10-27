using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.Roads;

/// <summary>
/// How a road runs through one of its points: put on a child entity of a <see cref="RoadComponent"/>'s entity.
/// A point without one runs on smoothly towards its neighbours, with an even curve.
/// </summary>
[DataContract("RoadPointComponent")]
[Display("Road point")]
[ComponentCategory("Roads")]
public sealed class RoadPointComponent : EntityComponent
{
    /// <summary>Whether the road runs the way this point faces (its forward, -Z), rather than on smoothly through it.</summary>
    [DataMember(0), DefaultValue(false)]
    public bool FollowRotation { get; set; }

    /// <summary>How far the road holds its way either side of the point, as a share of the distance to each neighbour: a third makes an even curve.</summary>
    [DataMember(10), DataMemberRange(0.05, 1, 0.01, 0.1, 2), DefaultValue(1f / 3f)]
    public float Handle { get; set; } = 1f / 3f;
}
