using System;
using System.Linq;
using System.Threading.Tasks;
using Stride.Assets.Entities;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Game;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Services;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Game;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Services;
using Stride.Assets.Presentation.AssetEditors.Gizmos;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.BuildEngine;
using Stride.Core.Mathematics;
using Stride.Editor.EditorGame.Game;
using Stride.Engine;
using Stride.Input;

namespace Stride.Roads.Editor;

/// <summary>
/// Draws roads in the scene editor. <see cref="ToggleKey"/> turns drawing on and off (Escape turns it off);
/// while it is on, with a road or one of its points selected:
/// <list type="bullet">
/// <item>a click on the ground adds a point: after the selected point, or at the end of the selected road;</item>
/// <item>a ctrl-click deletes the road's point nearest to where it lands, within <see cref="DeleteReach"/>.</item>
/// </list>
/// The selected road's outline (<see cref="RoadComponentGizmo"/>) turns from yellow to cyan while drawing is on.
/// Points are moved with the editor's own move tool. Every change is one step to undo. The ground is the
/// scene's ground plane (y = 0), as where Game Studio places new entities.
/// </summary>
[EditorGameService(typeof(EntityHierarchyEditorController))]
public sealed class RoadDrawService : EditorGameMouseServiceBase
{
    /// <summary>The key that turns drawing on and off: the scene editor's own keys leave B free.</summary>
    public const Keys ToggleKey = Keys.B;

    /// <summary>How far from a point, in metres on the ground, a ctrl-click still deletes it.</summary>
    public const float DeleteReach = 2f;

    private readonly EntityHierarchyEditorViewModel editor;
    private readonly IEditorGameController controller;
    private EntityHierarchyEditorGame? game;
    private IEditorGameEntitySelectionService? selection;
    private bool pressed;
    private bool releaseNextFrame;
    private Vector2 moved;

    public RoadDrawService(EntityHierarchyEditorViewModel editor, IEditorGameController controller)
    {
        this.editor = editor ?? throw new ArgumentNullException(nameof(editor));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    /// <summary>Whether drawing is on.</summary>
    public bool IsDrawing { get; private set; }

    public override bool IsControllingMouse { get; protected set; }

    protected override Task<bool> Initialize(EditorServiceGame editorGame)
    {
        game = (EntityHierarchyEditorGame)editorGame;
        game.Script.AddTask(Execute);
        return Task.FromResult(true);
    }

    private async Task Execute()
    {
        MicrothreadLocalDatabases.MountCommonDatabase();

        while (game!.IsRunning)
        {
            await game.Script.NextFrame();
            if (!IsActive)
                continue;

            // Let go of the mouse a frame after the click, so the selection service, which acts on the same
            // release, has seen it taken and left the selection alone.
            if (releaseNextFrame)
            {
                releaseNextFrame = false;
                IsControllingMouse = false;
            }

            var input = game.Input;
            if (input.IsKeyPressed(ToggleKey))
                SetDrawing(!IsDrawing);
            else if (IsDrawing && input.IsKeyPressed(Keys.Escape))
                SetDrawing(false);
            if (!IsDrawing)
                continue;

            var screenSize = new Vector2(game.GraphicsDevice.Presenter.BackBuffer.Width, game.GraphicsDevice.Presenter.BackBuffer.Height);
            if (input.IsMouseButtonPressed(MouseButton.Left) && IsMouseAvailable && !input.IsMouseButtonDown(MouseButton.Right))
            {
                pressed = true;
                moved = Vector2.Zero;
                IsControllingMouse = true;
            }
            if (!pressed)
                continue;

            moved += new Vector2(Math.Abs(input.MouseDelta.X * screenSize.X), Math.Abs(input.MouseDelta.Y * screenSize.Y));
            if (input.IsMouseButtonReleased(MouseButton.Left))
            {
                pressed = false;
                releaseNextFrame = true;
                // A drag (orbiting the camera, or a slip of the hand) adds nothing.
                if (moved.Length() < TransformationGizmo.TransformationStartPixelThreshold)
                    Click(input.IsKeyDown(Keys.LeftCtrl) || input.IsKeyDown(Keys.RightCtrl), input.MousePosition);
            }
        }
    }

    private void SetDrawing(bool drawing)
    {
        IsDrawing = drawing;
        pressed = false;
        if (!drawing && IsControllingMouse)
            releaseNextFrame = true;
    }

    /// <summary>Adds a point where the mouse is, or with ctrl held, deletes the road's nearest point.</summary>
    private void Click(bool delete, Vector2 mouse)
    {
        if (SelectedRoad() is not var (road, roadId, selectedPoint))
            return;

        var world = game!.GetPositionInScene(mouse);
        var toRoad = Matrix.Invert(road.Entity.Transform.WorldMatrix);
        var local = Vector3.TransformCoordinate(world, toRoad);
        var points = RoadProcessor.Points(road);

        if (delete)
        {
            var nearest = points
                .Select(p => (Point: p, Away: Vector2.Distance(new Vector2(p.Transform.WorldMatrix.TranslationVector.X, p.Transform.WorldMatrix.TranslationVector.Z), new Vector2(world.X, world.Z))))
                .Where(x => x.Away <= DeleteReach)
                .OrderBy(x => x.Away)
                .FirstOrDefault();
            if (nearest.Point != null)
            {
                var pointId = new AbsoluteId(roadId.AssetId, nearest.Point.Id);
                editor.Dispatcher.InvokeAsync(() => DeletePoint(roadId, pointId));
            }
            return;
        }

        var index = selectedPoint != null ? points.IndexOf(selectedPoint) + 1 : points.Count;
        editor.Dispatcher.InvokeAsync(() => AddPoint(roadId, local, index));
    }

    /// <summary>
    /// The road being drawn, from the editor's selection: a selected road, or the road a selected point is on
    /// (then that point too). The first that is one, if several are selected.
    /// </summary>
    private (RoadComponent Road, AbsoluteId RoadId, Entity? Point)? SelectedRoad()
    {
        selection ??= game!.EditorServices.Get<IEditorGameEntitySelectionService>();
        if (selection == null)
            return null;
        foreach (var id in selection.GetSelectedIds())
        {
            if (controller.FindGameSidePart(id) is not Entity entity)
                continue;
            if (entity.Get<RoadComponent>() is { } road)
                return (road, id, null);
            var parent = entity.GetParent();
            if (parent?.Get<RoadComponent>() is { } owner)
                return (owner, new AbsoluteId(id.AssetId, parent.Id), entity);
        }
        return null;
    }

    /// <summary>Adds a point to the road, at this place in its space and this place among its points, and selects it.</summary>
    private void AddPoint(AbsoluteId roadId, Vector3 local, int index)
    {
        if (editor.FindPartViewModel(roadId) is not EntityViewModel road)
            return;

        var point = new Entity($"Point {index + 1}");
        point.Transform.Position = local;
        AssetCollectionItemIdHelper.GenerateMissingItemIds(point);
        var collection = new AssetPartCollection<EntityDesign, Entity> { new EntityDesign(point, "") };
        index = Math.Clamp(index, 0, road.AssetSideEntity.Transform.Children.Count);

        using (var transaction = editor.UndoRedoService.CreateTransaction())
        {
            road.Asset.AssetHierarchyPropertyGraph.AddPartToAsset(collection, collection.Single().Value, road.AssetSideEntity, index);
            editor.UndoRedoService.SetName(transaction, $"Add road point '{point.Name}'");
        }

        // Select it, so the next click carries on after it.
        if (editor.FindPartViewModel(new AbsoluteId(roadId.AssetId, point.Id)) is { } added)
        {
            editor.ClearSelection();
            editor.SelectedContent.Add(added);
        }
    }

    /// <summary>Deletes a point of the road, and selects the road if the point was selected.</summary>
    private void DeletePoint(AbsoluteId roadId, AbsoluteId pointId)
    {
        if (editor.FindPartViewModel(pointId) is not EntityViewModel point)
            return;

        var asset = point.Asset;
        var design = asset.Asset.Hierarchy.Parts[pointId.ObjectId];
        var wasSelected = editor.SelectedContent.Contains(point);
        if (wasSelected)
            editor.ClearSelection();

        using (var transaction = editor.UndoRedoService.CreateTransaction())
        {
            asset.AssetHierarchyPropertyGraph.DeleteParts(new[] { design }, out var mapping);
            editor.UndoRedoService.PushOperation(new DeletedPartsTrackingOperation<EntityDesign, Entity>(asset, mapping));
            editor.UndoRedoService.SetName(transaction, $"Delete road point '{design.Entity.Name}'");
        }

        if (wasSelected && editor.FindPartViewModel(roadId) is { } road)
            editor.SelectedContent.Add(road);
    }
}
