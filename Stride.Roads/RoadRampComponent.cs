using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.Roads;

/// <summary>
/// A ramp built into the road whose entity it is on: over Length metres from Start along the road, on one side, a lane
/// tapers in beside its outer lane, runs alongside, and peels away until its middle is Separation metres out from the
/// road's lane edge, heading End Angle degrees away from the road there. An exit peels away in the way that side's
/// traffic goes; an entrance comes in from that way. A road may have several.
/// </summary>
[DataContract("RoadRampComponent")]
[Display("Road ramp")]
[ComponentCategory("Roads")]
[AllowMultipleComponents]
public sealed class RoadRampComponent : EntityComponent
{
    [DataMember(0), DefaultValue(RampKind.Exit)]
    public RampKind Kind { get; set; } = RampKind.Exit;

    /// <summary>Which side of the road, looking along it from its first point.</summary>
    [DataMember(10), DefaultValue(RoadSide.Right)]
    public RoadSide Side { get; set; } = RoadSide.Right;

    /// <summary>How far along the road the ramp's stretch begins, in metres.</summary>
    [DataMember(20), DataMemberRange(0, 10000, 0.5, 5, 1), DefaultValue(50f)]
    public float Start { get; set; } = 50f;

    /// <summary>How long its stretch of the road is, in metres.</summary>
    [DataMember(30), DataMemberRange(20, 500, 1, 10, 0), DefaultValue(120f)]
    public float Length { get; set; } = 120f;

    /// <summary>How long its lane takes to widen in from nothing, at the end where it joins the road.</summary>
    [DataMember(40), DataMemberRange(5, 200, 1, 10, 0), DefaultValue(40f)]
    public float Taper { get; set; } = 40f;

    /// <summary>How long its lane runs at full width beside the road, between its taper and where it splits off.</summary>
    [DataMember(50), DataMemberRange(0, 1000, 1, 10, 0), DefaultValue(0f)]
    public float ExitLaneLength { get; set; }

    /// <summary>How far out from the road's lane edge the middle of its lanes ends up, in metres.</summary>
    [DataMember(60), DataMemberRange(3, 60, 0.5, 5, 1), DefaultValue(12f)]
    public float Separation { get; set; } = 12f;

    /// <summary>How far its lanes head away from the road where they end, in degrees: none runs alongside it again.</summary>
    [DataMember(70), DataMemberRange(0, 80, 0.5, 5, 1), DefaultValue(0f)]
    public float EndAngle { get; set; }

    [DataMember(80), DataMemberRange(1, 3, 1, 1, 0), DefaultValue(1)]
    public int Lanes { get; set; } = 1;

    [DataMember(90), DataMemberRange(2, 6, 0.05, 0.5, 2), DefaultValue(4f)]
    public float LaneWidth { get; set; } = 4f;

    /// <summary>How wide its paved shoulders are, before its banks.</summary>
    [DataMember(100), Display(category: "Banks"), DataMemberRange(0, 5, 0.05, 0.5, 2), DefaultValue(1.5f)]
    public float ShoulderWidth { get; set; } = 1.5f;

    /// <summary>How far the banks past its shoulders drop from it (negative: rise).</summary>
    [DataMember(110), Display(category: "Banks"), DataMemberRange(-30, 30, 0.1, 1, 1), DefaultValue(1f)]
    public float ShoulderHeight { get; set; } = 1f;

    /// <summary>How steep its banks are, in degrees.</summary>
    [DataMember(120), Display(category: "Banks"), DataMemberRange(5, 85, 0.5, 5, 1), DefaultValue(RoadCrossSection.WalkableAngle)]
    public float ShoulderAngle { get; set; } = RoadCrossSection.WalkableAngle;

    /// <summary>
    /// How wide the sidewalk along its outer edge (away from the road) is, in metres, past its shoulder: none at 0. It
    /// carries on from the road's sidewalk on that side, where the road has one.
    /// </summary>
    [DataMember(130), Display(category: "Sidewalks"), DataMemberRange(0, 10, 0.1, 1, 1), DefaultValue(0f)]
    public float Sidewalk { get; set; }

    /// <summary>
    /// Whether it has a sidewalk along its inner edge too, between it and the road where it has come away: as wide as
    /// <see cref="Sidewalk"/>, or as there is room for, narrowing to the nose where it splits off.
    /// </summary>
    [DataMember(140), Display(category: "Sidewalks"), DefaultValue(false)]
    public bool InnerSidewalk { get; set; }

    /// <summary>It, as the road's maths takes it.</summary>
    [DataMemberIgnore]
    public RampSpec Spec => new(Start, Length, Side, Kind, Lanes, LaneWidth, ShoulderWidth, Taper, Separation, ShoulderHeight, ShoulderAngle,
        ExitLaneLength, EndAngle, Sidewalk, InnerSidewalk);
}
