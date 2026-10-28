using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.EntityFactories;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Core;
using Stride.Engine;

namespace Stride.Roads.Editor;

/// <summary>Add entity > Roads > Road network.</summary>
[Display(10, "Road network", "Roads")]
public class RoadNetworkEntityFactory : EntityFactory
{
    public override Task<Entity> CreateEntity(EntityHierarchyItemViewModel parent) =>
        CreateEntityWithComponent(ComputeNewName(parent, "Road network"), new RoadNetworkComponent());
}

/// <summary>Add entity > Roads > Road: one with no points yet, for its draw tool to add them to.</summary>
[Display(20, "Road", "Roads")]
public class RoadEntityFactory : EntityFactory
{
    public override Task<Entity> CreateEntity(EntityHierarchyItemViewModel parent) =>
        CreateEntityWithComponent(ComputeNewName(parent, "Road"), new RoadComponent());
}
