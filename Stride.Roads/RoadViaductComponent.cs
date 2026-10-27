using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.Roads;

/// <summary>
/// A viaduct carrying the road whose entity it is on, over <see cref="Length"/> metres from <see cref="Start"/> along
/// it: a deck under the road, barriers along its edges, and pillars down to the ground. Along it the road has no
/// skirts or banks. A road can have several.
/// </summary>
[DataContract("RoadViaductComponent")]
[Display("Road viaduct")]
[ComponentCategory("Roads")]
[AllowMultipleComponents]
public sealed class RoadViaductComponent : EntityComponent
{
    /// <summary>How far along the road it begins, in metres.</summary>
    [DataMember(0), DataMemberRange(0, 100000, 0.5, 5, 1), DefaultValue(0f)]
    public float Start { get; set; }

    /// <summary>How long it is, in metres.</summary>
    [DataMember(10), DataMemberRange(1, 100000, 0.5, 5, 1), DefaultValue(60f)]
    public float Length { get; set; } = 60f;

    /// <summary>How thick its deck is under the road, in metres.</summary>
    [DataMember(20), DataMemberRange(0.3, 5, 0.05, 0.5, 2), DefaultValue(1.2f)]
    public float DeckDepth { get; set; } = 1.2f;

    /// <summary>How high the barriers along its edges are, in metres: none at 0.</summary>
    [DataMember(30), DataMemberRange(0, 3, 0.05, 0.5, 2), DefaultValue(1f)]
    public float BarrierHeight { get; set; } = 1f;

    /// <summary>How thick the barriers are, in metres.</summary>
    [DataMember(40), DataMemberRange(0.1, 2, 0.05, 0.5, 2), DefaultValue(0.4f)]
    public float BarrierWidth { get; set; } = 0.4f;

    /// <summary>How far apart its pillars stand along it, in metres.</summary>
    [DataMember(50), DataMemberRange(5, 200, 1, 10, 0), DefaultValue(30f)]
    public float PillarSpacing { get; set; } = 30f;

    /// <summary>How wide each pillar is, in metres.</summary>
    [DataMember(60), DataMemberRange(0.3, 10, 0.05, 0.5, 2), DefaultValue(1.5f)]
    public float PillarWidth { get; set; } = 1.5f;

    /// <summary>It, as the road's maths takes it.</summary>
    [DataMemberIgnore]
    public StructureSpec Spec => new(Start, Length, DeckDepth, BarrierHeight, BarrierWidth, PillarSpacing, PillarWidth);
}
