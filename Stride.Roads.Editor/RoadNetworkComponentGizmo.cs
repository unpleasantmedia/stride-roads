using Stride.Assets.Presentation.AssetEditors.Gizmos;
using Stride.Engine;
using Stride.Engine.Gizmos;

namespace Stride.Roads.Editor;

/// <summary>
/// A billboard at a road network's entity in the scene editor, so the network can be found and picked to start roads in it.
/// </summary>
[GizmoComponent(typeof(RoadNetworkComponent), true)]
public class RoadNetworkComponentGizmo : BillboardingGizmo<RoadNetworkComponent>
{
    public RoadNetworkComponentGizmo(EntityComponent component)
        : base(component, "Road network", RoadsEditorPlugin.LoadResource("RoadsGizmo.png"))
    {
    }
}
