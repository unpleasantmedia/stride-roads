using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Editor.EditorGame.Game;
using Stride.Engine;
using Stride.Input;

namespace Stride.Roads.Editor;

/// <summary>
/// Draws roads in the scene editor, with a road network, a road or a road's point selected. <see cref="ToggleKey"/>
/// or a toggle button in the viewport toolbar (as an <see cref="IEditorGameToolService"/>) turns drawing on and off;
/// Escape turns it off. With none of them selected the button is hidden and drawing is off. While it is on:
/// <list type="bullet">
/// <item>with a road network selected, a click on the ground starts a new road in it, with its first point there;</item>
/// <item>with a road or point selected, a click adds a point: after the selected point, or at the end of the road;</item>
/// <item>a ctrl-click deletes the selected road's point nearest to where it lands, within <see cref="DeleteReach"/>.</item>
/// </list>
/// The selected road's outline (<see cref="RoadComponentGizmo"/>) turns from yellow to cyan while drawing is on.
/// Points are moved with the editor's own move tool. Every change is one step to undo. A click lands on the model
/// under the mouse (<see cref="SurfacePicker"/>), else on the scene's ground plane (y = 0).
/// </summary>
[EditorGameService(typeof(EntityHierarchyEditorController))]
public sealed class RoadDrawService : EditorGameMouseServiceBase, IEditorGameToolService
{
    /// <summary>The key that turns drawing on and off: the scene editor's own keys leave B free.</summary>
    public const Keys ToggleKey = Keys.B;

    /// <summary>How far from a point, in metres on the ground, a ctrl-click still deletes it.</summary>
    public const float DeleteReach = 2f;

    /// <summary>
    /// How many frames in a row with nothing to draw on selected make drawing unavailable: not at once, as selecting
    /// what was just added clears the selection for a moment.
    /// </summary>
    private const int UnselectedFramesToStop = 15;

    private static readonly Logger Logger = GlobalLogger.GetLogger("Roads");

    private readonly EntityHierarchyEditorViewModel editor;
    private readonly IEditorGameController controller;
    private EntityHierarchyEditorGame? game;
    private IEditorGameEntitySelectionService? selection;
    private SurfacePicker? surface;
    private bool pressed;
    private bool releaseNextFrame;
    private Vector2 moved;
    private int unselectedFrames = UnselectedFramesToStop + 1;
    private volatile bool available;
    private readonly EditorTool drawTool;

    /// <summary>Drawing turned on (1) or off (0) by the toolbar button, on the UI thread, for the next frame; -1 for neither.</summary>
    private int requested = -1;

    public RoadDrawService(EntityHierarchyEditorViewModel editor, IEditorGameController controller)
    {
        this.editor = editor ?? throw new ArgumentNullException(nameof(editor));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        drawTool = new EditorTool("Stride.Roads.Draw", $"Draw roads ({ToggleKey})", RoadsEditorPlugin.LoadResource("DrawRoad.png"),
            () => IsDrawing, active => Interlocked.Exchange(ref requested, active ? 1 : 0), () => available);
        Tools = [drawTool];
    }

    /// <summary>Whether drawing is on.</summary>
    public bool IsDrawing { get; private set; }

    public IReadOnlyList<EditorTool> Tools { get; }

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
            // One frame failing (an entity gone after an undo, say) must not end the loop: drawing would stay on
            // with no key read, and the mouse could stay held from every other service.
            try
            {
                Step();
            }
            catch (Exception e)
            {
                Logger.Error("Road drawing failed; drawing is off.", e);
                pressed = false;
                IsDrawing = false;
                IsControllingMouse = false;
                releaseNextFrame = false;
                drawTool.RaiseChanged();
            }
        }
    }

    private void Step()
    {
        // Let go of the mouse a frame after the click, so the selection service, which acts on the same
        // release, has seen it taken and left the selection alone.
        if (releaseNextFrame)
        {
            releaseNextFrame = false;
            IsControllingMouse = false;
        }

        // Drawing is there to use only with something to draw on selected: else its button hides, and it turns off.
        unselectedFrames = IsActive && SelectedTarget() != null ? 0 : unselectedFrames + 1;
        var nowAvailable = unselectedFrames <= UnselectedFramesToStop;
        if (nowAvailable != available)
        {
            available = nowAvailable;
            drawTool.RaiseChanged();
        }
        if (!available || !IsActive)
        {
            Interlocked.Exchange(ref requested, -1);
            if (IsDrawing && !pressed)
                SetDrawing(false);
            return;
        }

        var fromButton = Interlocked.Exchange(ref requested, -1);
        if (fromButton >= 0)
            SetDrawing(fromButton == 1);

        var input = game!.Input;
        if (input.IsKeyPressed(ToggleKey))
            SetDrawing(!IsDrawing);
        else if (IsDrawing && input.IsKeyPressed(Keys.Escape))
            SetDrawing(false);
        if (!IsDrawing)
            return;

        var screenSize = new Vector2(game.GraphicsDevice.Presenter.BackBuffer.Width, game.GraphicsDevice.Presenter.BackBuffer.Height);
        if (input.IsMouseButtonPressed(MouseButton.Left) && IsMouseAvailable && !input.IsMouseButtonDown(MouseButton.Right))
        {
            pressed = true;
            moved = Vector2.Zero;
            IsControllingMouse = true;
        }
        if (!pressed)
            return;

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

    private void SetDrawing(bool drawing)
    {
        var changed = IsDrawing != drawing;
        IsDrawing = drawing;
        // The toolbar button follows, however it was turned (key, deselecting, the button itself).
        if (changed)
            drawTool.RaiseChanged();
        pressed = false;
        if (!drawing && IsControllingMouse)
            releaseNextFrame = true;
    }

    /// <summary>
    /// Adds a point where the mouse is, or with ctrl held, deletes the road's nearest point; with a road network selected,
    /// starts a new road there.
    /// </summary>
    private void Click(bool delete, Vector2 mouse)
    {
        if (SelectedTarget() is not { } target)
            return;

        surface ??= new SurfacePicker(game!, selection!, game!.EditorServices.Get<IEditorGameCameraService>());
        var world = surface.PointAt(mouse);
        if (target.Network is { } network)
        {
            if (!delete)
            {
                var inNetwork = Vector3.TransformCoordinate(world, Matrix.Invert(network.Entity.Transform.WorldMatrix));
                var roadCount = network.Entity.Transform.Children.Count;
                editor.Dispatcher.InvokeAsync(() => AddRoad(target.Id, inNetwork, roadCount));
            }
            return;
        }

        var (road, roadId, selectedPoint) = (target.Road!, target.Id, target.Point);
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
    /// What drawing works on: a road (and the selected point of it, if a point is selected), or a road network to start
    /// roads in. <see cref="Id"/> is the road's, or the network's.
    /// </summary>
    private readonly record struct Target(AbsoluteId Id, RoadComponent? Road, Entity? Point, RoadNetworkComponent? Network);

    /// <summary>
    /// What drawing works on, from the editor's selection: a selected road, the road a selected point is on, or else
    /// a selected road network. Roads come first, if several things are selected.
    /// </summary>
    private Target? SelectedTarget()
    {
        selection ??= game!.EditorServices.Get<IEditorGameEntitySelectionService>();
        if (selection == null)
            return null;
        Target? network = null;
        foreach (var id in selection.GetSelectedIds().ToList())
        {
            if (FindEntity(id) is not { } entity)
                continue;
            if (entity.Get<RoadComponent>() is { } road)
                return new Target(id, road, null, null);
            var parent = entity.GetParent();
            if (parent?.Get<RoadComponent>() is { } owner)
                return new Target(new AbsoluteId(id.AssetId, parent.Id), owner, entity, null);
            if (network == null && entity.Get<RoadNetworkComponent>() is { } roads)
                network = new Target(id, null, null, roads);
        }
        return network;
    }

    /// <summary>The game-side entity of a part, or null if it is gone (the selection can name one an undo just removed).</summary>
    private Entity? FindEntity(AbsoluteId id)
    {
        try
        {
            return controller.FindGameSidePart(id) as Entity;
        }
        catch (Exception)
        {
            return null;
        }
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

    /// <summary>
    /// Starts a new road in the network, at this place in its space and this place among its children, with its first
    /// point there; selects that point, so the next click carries the road on. One step to undo.
    /// </summary>
    private void AddRoad(AbsoluteId networkId, Vector3 local, int index)
    {
        if (editor.FindPartViewModel(networkId) is not EntityViewModel network)
            return;

        var road = new Entity($"Road {index + 1}") { new RoadComponent() };
        road.Transform.Position = local;
        var point = new Entity("Point 1");
        AssetCollectionItemIdHelper.GenerateMissingItemIds(road);
        AssetCollectionItemIdHelper.GenerateMissingItemIds(point);
        var roads = new AssetPartCollection<EntityDesign, Entity> { new EntityDesign(road, "") };
        var points = new AssetPartCollection<EntityDesign, Entity> { new EntityDesign(point, "") };
        index = Math.Clamp(index, 0, network.AssetSideEntity.Transform.Children.Count);

        using (var transaction = editor.UndoRedoService.CreateTransaction())
        {
            var graph = network.Asset.AssetHierarchyPropertyGraph;
            graph.AddPartToAsset(roads, roads.Single().Value, network.AssetSideEntity, index);
            graph.AddPartToAsset(points, points.Single().Value, road, 0);
            editor.UndoRedoService.SetName(transaction, $"Add road '{road.Name}'");
        }

        if (editor.FindPartViewModel(new AbsoluteId(networkId.AssetId, point.Id)) is { } added)
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
