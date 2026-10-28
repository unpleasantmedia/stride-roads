using System.Collections.Generic;
using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Rendering;

namespace Stride.Roads;

/// <summary>
/// Where roads meet (a T-junction, a crossroads, or more): roads whose first or last point is joined to it
/// (<see cref="RoadComponent.FromJunction"/>, <see cref="RoadComponent.ToJunction"/>) end there, cut back from its middle
/// until their edges meet round rounded corners, and <see cref="RoadJunctionProcessor"/> paves the space between them,
/// with their edge lines and sidewalks carried round its corners and a stop line across each road's lanes coming in.
/// Drawing a road onto the middle of another starts one there, splitting that road in two. Its banks are as its roads'
/// mostly are. A <see cref="RoadViaductComponent"/> on it carries it on a deck as a viaduct, with barriers round its
/// corners and pillars under it, and no banks.
/// </summary>
[DataContract("RoadJunctionComponent")]
[Display("Road junction")]
[ComponentCategory("Roads")]
[DefaultEntityComponentProcessor(typeof(RoadJunctionProcessor), ExecutionMode = ExecutionMode.Runtime | ExecutionMode.Editor)]
public sealed class RoadJunctionComponent : EntityComponent
{
    /// <summary>How round its corners are, between one road's edge and the next's, in metres.</summary>
    [DataMember(0), DataMemberRange(0, 50, 0.5, 2, 1), DefaultValue(6f)]
    public float CornerRadius { get; set; } = 6f;

    /// <summary>How the traffic into it is told when it may go: stop signs or traffic lights, or by how many roads meet it.</summary>
    [DataMember(10), DefaultValue(TrafficControl.Auto)]
    public TrafficControl TrafficControl { get; set; } = TrafficControl.Auto;

    // A material left empty draws as the roads meeting it mostly do (else RoadMaterials' plain colour).
    [DataMember(100), Display(category: "Materials")]
    public Material? SurfaceMaterial { get; set; }

    [DataMember(110), Display(category: "Materials")]
    public Material? WhiteLineMaterial { get; set; }

    [DataMember(120), Display(category: "Materials")]
    public Material? SidewalkMaterial { get; set; }

    [DataMember(130), Display(category: "Materials")]
    public Material? BankMaterial { get; set; }

    /// <summary>Its deck and barriers' concrete, where a <see cref="RoadViaductComponent"/> on it carries it.</summary>
    [DataMember(140), Display(category: "Materials")]
    public Material? StructureMaterial { get; set; }

    /// <summary>How far each road meeting it is cut back from its middle, as last built: by the road and whether it is its start.</summary>
    [DataMemberIgnore]
    public Dictionary<(RoadComponent Road, bool Start), float> Setbacks { get; } = new();

    /// <summary>How far a road meeting it at its start (or end) is cut back from its middle; none before it is built.</summary>
    public float SetbackFor(RoadComponent road, bool start) => Setbacks.TryGetValue((road, start), out var setback) ? setback : 0f;
}
