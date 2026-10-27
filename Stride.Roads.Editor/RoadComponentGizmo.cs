using Stride.Assets.Presentation.AssetEditors.Gizmos;
using Stride.Engine;
using Stride.Engine.Gizmos;

namespace Stride.Roads.Editor;

/// <summary>
/// A billboard drawn at the road's entity in the scene editor, so the road can be found and picked
/// where it has no points yet. The point gizmo comes later.
/// </summary>
[GizmoComponent(typeof(RoadComponent), true)]
public class RoadComponentGizmo : BillboardingGizmo<RoadComponent>
{
    public RoadComponentGizmo(EntityComponent component)
        : base(component, "Road", RoadsEditorPlugin.LoadResource("RoadsGizmo.png"))
    {
    }
}
