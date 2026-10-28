using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Rendering;

namespace Stride.Roads;

/// <summary>
/// A road through its entity's children, in their order in the hierarchy: each child is a point it
/// runs through (a <see cref="RoadPointComponent"/> on one says how), with what it looks like across
/// set here: lanes each way, their width, shoulders, a median and sidewalks. <see cref="RoadProcessor"/>
/// builds its surface and lines whenever a point or a setting changes, in the editor too; what it
/// builds is never saved. Traffic keeps right: the lanes going forwards (from the first point towards
/// the last) are on the right.
/// </summary>
[DataContract("RoadComponent")]
[Display("Road", Expand = ExpandRule.Once)]
[ComponentCategory("Roads")]
[ComponentOrder(30010)]
[DefaultEntityComponentProcessor(typeof(RoadProcessor), ExecutionMode = ExecutionMode.Runtime | ExecutionMode.Editor)]
public sealed class RoadComponent : EntityComponent
{
    /// <summary>How far apart along the road its shape is sampled, in metres.</summary>
    public const float Spacing = 1f;

    /// <summary>How many lanes go forwards, from the first point towards the last: on the right.</summary>
    [DataMember(0), Display(category: "Lanes"), DataMemberRange(0, 8, 1, 1, 0), DefaultValue(1)]
    public int ForwardLanes { get; set; } = 1;

    /// <summary>How many lanes go backwards, from the last point towards the first: on the left.</summary>
    [DataMember(10), Display(category: "Lanes"), DataMemberRange(0, 8, 1, 1, 0), DefaultValue(1)]
    public int BackwardLanes { get; set; } = 1;

    /// <summary>How wide each lane is, in metres.</summary>
    [DataMember(20), Display(category: "Lanes"), DataMemberRange(1, 10, 0.05, 0.5, 2), DefaultValue(3.5f)]
    public float LaneWidth { get; set; } = 3.5f;

    /// <summary>How wide the shoulder either side of the lanes is, in metres.</summary>
    [DataMember(30), Display(category: "Lanes"), DataMemberRange(0, 10, 0.05, 0.5, 2), DefaultValue(1f)]
    public float ShoulderWidth { get; set; } = 1f;

    /// <summary>How wide the median between the two directions is, in metres: none at 0.</summary>
    [DataMember(40), Display(category: "Lanes"), DataMemberRange(0, 50, 0.1, 1, 1), DefaultValue(0f)]
    public float MedianWidth { get; set; }

    /// <summary>What fills the median: asphalt with lines either side, or the ground showing through.</summary>
    [DataMember(50), Display(category: "Lanes"), DefaultValue(MedianKind.Paved)]
    public MedianKind Median { get; set; } = MedianKind.Paved;

    /// <summary>How wide the sidewalk on the left is, in metres, in place of its shoulder: none at 0.</summary>
    [DataMember(60), Display(category: "Sidewalks"), DataMemberRange(0, 10, 0.1, 1, 1), DefaultValue(0f)]
    public float SidewalkLeft { get; set; }

    /// <summary>How wide the sidewalk on the right is, in metres, in place of its shoulder: none at 0.</summary>
    [DataMember(70), Display(category: "Sidewalks"), DataMemberRange(0, 10, 0.1, 1, 1), DefaultValue(0f)]
    public float SidewalkRight { get; set; }

    /// <summary>How far a sidewalk's outer face reaches down below the road, in metres, to meet the ground beside it.</summary>
    [DataMember(75), Display(category: "Sidewalks"), DataMemberRange(0, 50, 0.1, 1, 1), DefaultValue(RoadMeshBuilder.SidewalkSkirt)]
    public float SidewalkFalloff { get; set; } = RoadMeshBuilder.SidewalkSkirt;

    /// <summary>How far the ground falls away past each shoulder, in metres: negative rises, as out of a cutting.</summary>
    [DataMember(80), Display(category: "Ground"), DataMemberRange(-20, 20, 0.1, 1, 1), DefaultValue(1f)]
    public float ShoulderHeight { get; set; } = 1f;

    /// <summary>How steep the bank past each shoulder is, in degrees.</summary>
    [DataMember(90), Display(category: "Ground"), DataMemberRange(1, 89, 0.5, 5, 1), DefaultValue(RoadCrossSection.WalkableAngle)]
    public float ShoulderAngle { get; set; } = RoadCrossSection.WalkableAngle;

    /// <summary>
    /// Whether its points sit on the ground (a terrain's, with the terrain plugin, through the plugin API), however they
    /// are moved: those carrying a viaduct, or marked to keep their height, keep theirs.
    /// </summary>
    [DataMember(92), Display("Follow ground", category: "Ground"), DefaultValue(true)]
    public bool FollowGround { get; set; } = true;

    /// <summary>
    /// How far above the ground its points sit where it follows it, in metres: as far as fitting sinks the ground under
    /// its surface, so on flat ground fitting leaves the ground under it as it is.
    /// </summary>
    [DataMember(93), Display("Ground clearance", category: "Ground"), DataMemberRange(-5, 5, 0.05, 0.5, 2), DefaultValue(RoadGround.Sink)]
    public float GroundClearance { get; set; } = RoadGround.Sink;

    /// <summary>Whether the ground (a terrain's) is fitted to it: flat under it, down its banks, easing back past them; again as it changes.</summary>
    [DataMember(94), Display("Fit ground", category: "Ground"), DefaultValue(true)]
    public bool FitGround { get; set; } = true;

    /// <summary>How far, in metres, the fitted ground eases back to the ground around past its banks.</summary>
    [DataMember(96), Display("Fit falloff", category: "Ground"), DataMemberRange(0, 100, 0.5, 5, 1), DefaultValue(12f)]
    public float FitFalloff { get; set; } = 12f;

    /// <summary>The terrain layer painted under it and its banks while fitting (none below 0).</summary>
    [DataMember(98), Display("Paint layer", category: "Ground"), DataMemberRange(-1, 31, 1, 1, 0), DefaultValue(-1)]
    public int GroundLayer { get; set; } = -1;

    /// <summary>Whether the road runs on from its last point back to its first, as a ring.</summary>
    [DataMember(100), Display(category: "Shape"), DefaultValue(false)]
    public bool Closed { get; set; }

    /// <summary>Whether the road has collision in the game: its asphalt, banks and sidewalks, as a static mesh.</summary>
    [DataMember(110), Display(category: "Shape"), DefaultValue(true)]
    public bool Collision { get; set; } = true;

    /// <summary>
    /// The ramp this road carries on from, if any: its first point is held at the middle of the ramp's lanes where it
    /// has come away from its road, and it runs on from there the way the ramp does.
    /// </summary>
    [DataMember(120), Display(category: "Joins")]
    public RoadRampComponent? FromRamp { get; set; }

    /// <summary>The road whose end this road carries on from, if any (and no <see cref="FromRamp"/>): its first point is held there.</summary>
    [DataMember(130), Display(category: "Joins")]
    public RoadComponent? FromRoad { get; set; }

    /// <summary>Whether it carries on from <see cref="FromRoad"/>'s last point, rather than its first.</summary>
    [DataMember(140), Display(category: "Joins"), DefaultValue(true)]
    public bool FromRoadsEnd { get; set; } = true;

    /// <summary>The ramp this road runs into, if any: its last point is held at the ramp's free end, arriving the way the ramp runs.</summary>
    [DataMember(150), Display(category: "Joins")]
    public RoadRampComponent? ToRamp { get; set; }

    /// <summary>The road whose end this road runs into, if any (and no <see cref="ToRamp"/>): its last point is held there.</summary>
    [DataMember(160), Display(category: "Joins")]
    public RoadComponent? ToRoad { get; set; }

    /// <summary>Whether it runs into <see cref="ToRoad"/>'s last point, rather than its first.</summary>
    [DataMember(170), Display(category: "Joins"), DefaultValue(false)]
    public bool ToRoadsEnd { get; set; }

    /// <summary>The fork this road carries on from as a branch, if any: its first point is held at the end of one of its groups.</summary>
    [DataMember(180), Display(category: "Joins")]
    public RoadForkComponent? FromFork { get; set; }

    /// <summary>Whether it carries on from <see cref="FromFork"/>'s left group (the trunk's left), rather than its right.</summary>
    [DataMember(190), Display(category: "Joins"), DefaultValue(false)]
    public bool FromForksLeft { get; set; }

    /// <summary>The fork this road runs into as a branch, if any: its last point is held at the end of one of its groups.</summary>
    [DataMember(200), Display(category: "Joins")]
    public RoadForkComponent? ToFork { get; set; }

    /// <summary>Whether it runs into <see cref="ToFork"/>'s left group (the trunk's left), rather than its right.</summary>
    [DataMember(210), Display(category: "Joins"), DefaultValue(false)]
    public bool ToForksLeft { get; set; }

    /// <summary>The junction this road starts at, if any: its first point is held at its middle, and it is cut back to meet it.</summary>
    [DataMember(220), Display(category: "Joins")]
    public RoadJunctionComponent? FromJunction
    {
        get => fromJunction;
        set
        {
            fromJunction = value;
            // None leaves its id be: the editor's copy of a new road for the game names a junction not there yet as
            // none, and the game finds it by its id (see RoadProcessor.Relink). It goes with the junction.
            if (value != null)
                FromJunctionId = value.Id;
        }
    }

    /// <summary>
    /// <see cref="FromJunction"/>'s id, kept with it: the editor hands the game a value like this reliably, but which
    /// component a road names only once that component is there, so a new junction's can come as none. Cleared with it
    /// by the editor's draw tool; a junction deleted, nothing has it.
    /// </summary>
    [DataMember(225), Display(category: "Joins")]
    public Guid FromJunctionId { get; set; }

    /// <summary>The junction this road ends at, if any: its last point is held at its middle, and it is cut back to meet it.</summary>
    [DataMember(230), Display(category: "Joins")]
    public RoadJunctionComponent? ToJunction
    {
        get => toJunction;
        set
        {
            toJunction = value;
            // None leaves its id be: the editor's copy of a new road for the game names a junction not there yet as
            // none, and the game finds it by its id (see RoadProcessor.Relink). It goes with the junction.
            if (value != null)
                ToJunctionId = value.Id;
        }
    }

    /// <summary><see cref="ToJunction"/>'s id, kept with it (see <see cref="FromJunctionId"/>).</summary>
    [DataMember(235), Display(category: "Joins")]
    public Guid ToJunctionId { get; set; }

    private RoadJunctionComponent? fromJunction, toJunction;

    /// <summary>What stands along its left edge, past its shoulder (and sidewalk): carried on along its ramps and forks on that side.</summary>
    [DataMember(112), Display(category: "Barriers"), DefaultValue(RoadBarrier.None)]
    public RoadBarrier LeftBarrier { get; set; }

    /// <summary>What stands along its right edge, past its shoulder (and sidewalk): carried on along its ramps and forks on that side.</summary>
    [DataMember(114), Display(category: "Barriers"), DefaultValue(RoadBarrier.None)]
    public RoadBarrier RightBarrier { get; set; }

    /// <summary>What stands down its middle, between its two directions, on a road both ways.</summary>
    [DataMember(116), Display(category: "Barriers"), DefaultValue(RoadBarrier.None)]
    public RoadBarrier MedianBarrier { get; set; }

    /// <summary>Its barriers, as its maths takes them.</summary>
    [DataMemberIgnore]
    public RoadBarriers Barriers => new(LeftBarrier, RightBarrier, MedianBarrier);

    // A material left empty draws with RoadMaterials' plain colour for it.
    [DataMember(200), Display(category: "Materials")]
    public Material? SurfaceMaterial { get; set; }

    [DataMember(210), Display(category: "Materials")]
    public Material? WhiteLineMaterial { get; set; }

    [DataMember(220), Display(category: "Materials")]
    public Material? YellowLineMaterial { get; set; }

    [DataMember(230), Display(category: "Materials")]
    public Material? SidewalkMaterial { get; set; }

    /// <summary>The banks sloping away past the shoulders: as its surface (asphalt), left empty.</summary>
    [DataMember(240), Display(category: "Materials")]
    public Material? BankMaterial { get; set; }

    /// <summary>Its guard rails, and its gores' (concrete barriers are drawn with <see cref="StructureMaterial"/>).</summary>
    [DataMember(245), Display(category: "Materials")]
    public Material? BarrierMaterial { get; set; }

    /// <summary>Its viaducts' concrete: decks, barriers and pillars.</summary>
    [DataMember(250), Display(category: "Materials")]
    public Material? StructureMaterial { get; set; }

    /// <summary>What the road looks like across.</summary>
    [DataMemberIgnore]
    public RoadCrossSection Section => new(
        Math.Max(ForwardLanes, BackwardLanes == 0 ? 1 : 0), BackwardLanes, LaneWidth, ShoulderWidth, MedianWidth, Median,
        ShoulderHeight, ShoulderAngle, SidewalkLeft, SidewalkRight);

    /// <summary>Samples along it as last built, in its entity's space, in metres: none with fewer than two points.</summary>
    [DataMemberIgnore]
    public IReadOnlyList<RoadFrame> Frames { get; internal set; } = Array.Empty<RoadFrame>();
}
