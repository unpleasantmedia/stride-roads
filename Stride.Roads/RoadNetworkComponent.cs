using System.Collections.Generic;
using System.Linq;
using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.Roads;

/// <summary>
/// A road network: its entity's children are its roads (entities with a <see cref="RoadComponent"/>). In the scene
/// editor, drawing with the network selected starts a new road in it.
/// </summary>
[DataContract("RoadNetworkComponent")]
[Display("Road network")]
[ComponentCategory("Roads")]
public sealed class RoadNetworkComponent : EntityComponent
{
    /// <summary>The roads in this network, in order: its entity's children with a road on.</summary>
    [DataMemberIgnore]
    public IEnumerable<RoadComponent> Roads =>
        Entity.Transform.Children.Select(t => t.Entity.Get<RoadComponent>()).OfType<RoadComponent>();
}
