using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Services;
using Stride.Editor.EditorGame.Game;

namespace Stride.Roads.Editor.Experiments;

/// <summary>
/// Experiment (option 2): road drawing as a checkbox in the scene editor's Overlays popup, through the existing
/// <see cref="IEditorGameOverlayService"/>, without any change to the editor.
/// <para>
/// Its shortcomings, kept on purpose to compare: the overlays popup has no way to hear back from a service, so the
/// checkbox doesn't follow drawing turned on or off by B, Escape or deselecting; and the editor saves overlays' state
/// with the scene, so drawing left ticked comes back on when the scene is opened again.
/// </para>
/// </summary>
[EditorGameService(typeof(EntityHierarchyEditorController))]
public sealed class RoadDrawOverlayService : EditorGameServiceBase, IEditorGameOverlayService
{
    private const string Key = "Stride.Roads.DrawExperiment";

    private EditorServiceGame? game;

    public override IEnumerable<Type> Dependencies => [typeof(RoadDrawService)];

    public string DisplayName => "Roads (experiment)";

    public IReadOnlyList<EditorOverlay> Overlays { get; } = [new EditorOverlay(Key, "Draw roads")];

    // The overlays never change.
    public event EventHandler? OverlaysChanged { add { } remove { } }

    protected override Task<bool> Initialize(EditorServiceGame editorGame)
    {
        game = editorGame;
        return Task.FromResult(true);
    }

    public void SetOverlayVisible(string key, bool visible)
    {
        if (key != Key)
            return;
        // The editor shows its overlays (from the scene settings) as it loads, maybe before this is initialized.
        var tool = game?.EditorServices.Get<RoadDrawService>()?.Tools[0];
        tool?.SetActive(visible);
    }
}
