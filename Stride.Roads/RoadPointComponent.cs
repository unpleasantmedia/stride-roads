using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.Roads;

/// <summary>
/// How a road runs through one of its points: put on a child entity of a <see cref="RoadComponent"/>'s entity.
/// A point left unrotated runs on smoothly towards its neighbours, with an even curve; rotating it turns the road
/// from there, in the frame its gizmo shows in local space (about Y bends it, about X pitches it, about Z banks it).
/// </summary>
[DataContract("RoadPointComponent")]
[Display("Road point")]
[ComponentCategory("Roads")]
public sealed class RoadPointComponent : EntityComponent
{
    /// <summary>Whether the road runs the way this point faces in the world (its forward, -Z), rather than turned from the way it would run on its own.</summary>
    [DataMember(0), DefaultValue(false)]
    public bool FollowRotation { get; set; }

    /// <summary>Whether it keeps its own height where its road follows the ground (as a point on a bridge's approach).</summary>
    [DataMember(5), DefaultValue(false)]
    public bool KeepHeight { get; set; }

    /// <summary>How far the road holds its way either side of the point, as a share of the distance to each neighbour: a third makes an even curve.</summary>
    [DataMember(10), DataMemberRange(0.05, 1, 0.01, 0.1, 2), DefaultValue(1f / 3f)]
    public float Handle { get; set; } = 1f / 3f;
}
