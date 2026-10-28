using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.Roads;

/// <summary>
/// Parking along one side of the road whose entity it is on, over <see cref="Length"/> metres from <see cref="Start"/>
/// along it: a parking lane, a lay-by set into the sidewalk, or angled or square bays. The road builds it in: its side
/// paved out as far as the bays reach, its sidewalk (or bank) set back round them, and the bays painted. A road may have
/// several.
/// </summary>
[DataContract("RoadParkingComponent")]
[Display("Road parking")]
[ComponentCategory("Roads")]
[AllowMultipleComponents]
public sealed class RoadParkingComponent : EntityComponent
{
    [DataMember(0), DefaultValue(ParkingKind.Parallel)]
    public ParkingKind Kind { get; set; } = ParkingKind.Parallel;

    /// <summary>Which side of the road, looking along it from its first point.</summary>
    [DataMember(10), DefaultValue(RoadSide.Right)]
    public RoadSide Side { get; set; } = RoadSide.Right;

    /// <summary>How far along the road it begins, in metres.</summary>
    [DataMember(20), DataMemberRange(0, 10000, 0.5, 5, 1), DefaultValue(0f)]
    public float Start { get; set; }

    [DataMember(30), DataMemberRange(5, 2000, 0.5, 5, 1), DefaultValue(30f)]
    public float Length { get; set; } = 30f;

    /// <summary>How wide a bay is: a car's width and room either side.</summary>
    [DataMember(40), DataMemberRange(1.8, 5, 0.05, 0.5, 2), DefaultValue(2.5f)]
    public float BayWidth { get; set; } = 2.5f;

    /// <summary>How long a bay is: a car's length and room (end to end along a parking lane, or nose in to angled and square bays).</summary>
    [DataMember(50), DataMemberRange(3, 10, 0.05, 0.5, 2), DefaultValue(6f)]
    public float BayLength { get; set; } = 6f;

    /// <summary>For angled bays: the angle between them and the road, in degrees.</summary>
    [DataMember(60), DataMemberRange(15, 80, 1, 5, 0), DefaultValue(60f)]
    public float Angle { get; set; } = 60f;

    /// <summary>How far along the road its bay lines are shifted from where they would be (middled along it).</summary>
    [DataMember(70), DataMemberRange(-20, 20, 0.05, 0.5, 2), DefaultValue(0f)]
    public float BayOffset { get; set; }

    /// <summary>It, as the road's maths takes it.</summary>
    [DataMemberIgnore]
    public ParkingSpec Spec => new(Kind, Side, Start, Length, BayWidth, BayLength, Angle, BayOffset);
}
