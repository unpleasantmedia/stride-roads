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

    /// <summary>Whether the road runs on from its last point back to its first, as a ring.</summary>
    [DataMember(100), Display(category: "Shape"), DefaultValue(false)]
    public bool Closed { get; set; }

    /// <summary>Whether the road has collision in the game: its asphalt, banks and sidewalks, as a static mesh.</summary>
    [DataMember(110), Display(category: "Shape"), DefaultValue(true)]
    public bool Collision { get; set; } = true;

    // A material left empty draws with RoadMaterials' plain colour for it.
    [DataMember(200), Display(category: "Materials")]
    public Material? SurfaceMaterial { get; set; }

    [DataMember(210), Display(category: "Materials")]
    public Material? WhiteLineMaterial { get; set; }

    [DataMember(220), Display(category: "Materials")]
    public Material? YellowLineMaterial { get; set; }

    [DataMember(230), Display(category: "Materials")]
    public Material? SidewalkMaterial { get; set; }

    /// <summary>The banks sloping away past the shoulders.</summary>
    [DataMember(240), Display(category: "Materials")]
    public Material? BankMaterial { get; set; }

    /// <summary>Its viaducts' concrete: decks, barriers and pillars.</summary>
    [DataMember(250), Display(category: "Materials")]
    public Material? StructureMaterial { get; set; }

    /// <summary>The viaducts carrying it: the <see cref="RoadViaductComponent"/>s on its entity.</summary>
    [DataMemberIgnore]
    public List<StructureSpec> Structures => Entity?.GetAll<RoadViaductComponent>().Select(v => v.Spec).ToList() ?? new List<StructureSpec>();

    /// <summary>What the road looks like across.</summary>
    [DataMemberIgnore]
    public RoadCrossSection Section => new(
        Math.Max(ForwardLanes, BackwardLanes == 0 ? 1 : 0), BackwardLanes, LaneWidth, ShoulderWidth, MedianWidth, Median,
        ShoulderHeight, ShoulderAngle, SidewalkLeft, SidewalkRight);

    /// <summary>Samples along it as last built, in its entity's space, in metres: none with fewer than two points.</summary>
    [DataMemberIgnore]
    public IReadOnlyList<RoadFrame> Frames { get; internal set; } = Array.Empty<RoadFrame>();
}
