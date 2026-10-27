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
/// <item>with a road with a ramp selected, a click on the ramp's free end (within <see cref="RampEndReach"/>, marked by a ring)
/// starts a road carrying on from it, or selects the one that does;</item>
/// <item>a new road started, or a road carried on past its last point, on another road's end or a ramp's free end
/// (within <see cref="RampEndReach"/>) is joined to it there: that end of the road is held to it as it moves;</item>
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

    /// <summary>How near a ramp's free end a click must land to carry a road on from it, in metres.</summary>
    public const float RampEndReach = 4f;

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
                var join = JoinAt(world, null);
                var inNetwork = Vector3.TransformCoordinate(join?.Position ?? world, Matrix.Invert(network.Entity.Transform.WorldMatrix));
                var roadCount = network.Entity.Transform.Children.Count;
                editor.Dispatcher.InvokeAsync(() => AddRoad(target.Id, inNetwork, roadCount, join));
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

        // On one of its fork's branch ends: a branch road carrying on from it, beside it in its network.
        if (road.Entity.Get<RoadForkComponent>() is { } ownFork && road.Entity.GetParent() is { } parentEntity)
        {
            foreach (var left in new[] { true, false })
            {
                if (RoadProcessor.ForkEnd(ownFork, left) is not { } end
                    || Vector2.Distance(new Vector2(end.Position.X, end.Position.Z), new Vector2(world.X, world.Z)) > RampEndReach)
                    continue;
                var join = new Join(road.Entity.Id, null, false, end.Position, left);
                var networkId = new AbsoluteId(roadId.AssetId, parentEntity.Id);
                var inParent = Vector3.TransformCoordinate(end.Position, Matrix.Invert(parentEntity.Transform.WorldMatrix));
                var at = parentEntity.Transform.Children.IndexOf(road.Entity.Transform) + 1;
                editor.Dispatcher.InvokeAsync(() => AddRoad(networkId, inParent, at, join));
                return;
            }
        }

        // On a ramp's free end: a road carrying on from it.
        var ramps = road.Entity.GetAll<RoadRampComponent>().ToList();
        for (var i = 0; i < ramps.Count; i++)
        {
            if (RoadProcessor.RampEnd(ramps[i]) is not { } end)
                continue;
            if (Vector2.Distance(new Vector2(end.Position.X, end.Position.Z), new Vector2(world.X, world.Z)) > RampEndReach)
                continue;
            var rampIndex = i;
            editor.Dispatcher.InvokeAsync(() => ContinueFromRamp(roadId, rampIndex, end.Position));
            return;
        }

        var index = selectedPoint != null ? points.IndexOf(selectedPoint) + 1 : points.Count;
        // Past its last point, on another road's end or a ramp's: run into it, and end there.
        if (index == points.Count && JoinAt(world, road) is { } into)
        {
            var at = Vector3.TransformCoordinate(into.Position, toRoad);
            editor.Dispatcher.InvokeAsync(() => AddPoint(roadId, at, index, into));
            return;
        }
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

    /// <summary>
    /// Something a road's end can be joined to: a ramp's free end (the <see cref="RampIndex"/>th ramp on the road
    /// <see cref="Road"/>), or else one end of that road (its last, if <see cref="Last"/>). <see cref="Position"/> is in the world.
    /// </summary>
    private readonly record struct Join(Guid Road, int? RampIndex, bool Last, Vector3 Position, bool? ForkLeft = null);

    /// <summary>The road end or ramp end nearest a place in the world, within <see cref="RampEndReach"/>, on any road but this one.</summary>
    private Join? JoinAt(Vector3 world, RoadComponent? except)
    {
        var processor = game?.SceneSystem.SceneInstance?.Processors.OfType<RoadProcessor>().FirstOrDefault();
        if (processor == null)
            return null;
        var place = new Vector2(world.X, world.Z);
        Join? best = null;
        var bestAway = RampEndReach;
        void Consider(Guid road, int? ramp, bool last, (Vector3 Position, Vector3 Outward)? end)
        {
            if (end is not { } at)
                return;
            var away = Vector2.Distance(new Vector2(at.Position.X, at.Position.Z), place);
            if (away > bestAway)
                return;
            bestAway = away;
            best = new Join(road, ramp, last, at.Position);
        }
        void ConsiderFork(Guid road, bool left, (Vector3 Position, Vector3 Outward)? end)
        {
            if (end is not { } at)
                return;
            var away = Vector2.Distance(new Vector2(at.Position.X, at.Position.Z), place);
            if (away > bestAway)
                return;
            bestAway = away;
            best = new Join(road, null, false, at.Position, left);
        }
        foreach (var other in processor.Roads.ToList())
        {
            if (other == except || other.Entity == null || other.Closed)
                continue;
            var ramps = other.Entity.GetAll<RoadRampComponent>().ToList();
            for (var i = 0; i < ramps.Count; i++)
                Consider(other.Entity.Id, i, false, RoadProcessor.RampEnd(ramps[i]));
            if (other.Entity.Get<RoadForkComponent>() is { } fork)
            {
                ConsiderFork(other.Entity.Id, true, RoadProcessor.ForkEnd(fork, true));
                ConsiderFork(other.Entity.Id, false, RoadProcessor.ForkEnd(fork, false));
            }
            Consider(other.Entity.Id, null, false, RoadProcessor.RoadEnd(other, false));
            Consider(other.Entity.Id, null, true, RoadProcessor.RoadEnd(other, true));
        }
        return best;
    }

    /// <summary>
    /// Joins one end of a road (asset side) to what a <see cref="Join"/> names, in the same asset: as part of the
    /// transaction it is called in. Nothing, if that isn't in the asset.
    /// </summary>
    private void Joined(EntityViewModel anyInAsset, RoadComponent road, Join join, bool atStart)
    {
        if (!anyInAsset.Asset.Asset.Hierarchy.Parts.TryGetValue(join.Road, out var design) || design.Entity.Get<RoadComponent>() is not { } other)
            return;
        if (join.ForkLeft is { } forkLeft && design.Entity.Get<RoadForkComponent>() is { } fork)
        {
            JoinedToFork(road, other, fork, forkLeft, atStart);
            return;
        }
        var ramp = join.RampIndex is { } i ? design.Entity.GetAll<RoadRampComponent>().ElementAtOrDefault(i) : null;
        // A road not yet in the asset is set directly; one in it, through its node, so the change can be undone.
        if (editor.Session.AssetNodeContainer.GetNode(road) is not { } node)
        {
            if (atStart)
                (road.FromRamp, road.FromRoad, road.FromRoadsEnd) = (ramp, ramp == null ? other : null, join.Last);
            else
                (road.ToRamp, road.ToRoad, road.ToRoadsEnd) = (ramp, ramp == null ? other : null, join.Last);
            return;
        }
        if (atStart)
        {
            node[nameof(RoadComponent.FromRamp)].Update(ramp);
            node[nameof(RoadComponent.FromRoad)].Update(ramp == null ? other : null);
            node[nameof(RoadComponent.FromRoadsEnd)].Update(join.Last);
        }
        else
        {
            node[nameof(RoadComponent.ToRamp)].Update(ramp);
            node[nameof(RoadComponent.ToRoad)].Update(ramp == null ? other : null);
            node[nameof(RoadComponent.ToRoadsEnd)].Update(join.Last);
        }
    }

    /// <summary>
    /// Joins one end of a road (asset side) to one group of a fork as its branch: it takes that group's lanes, one way,
    /// going the way their traffic does, and the trunk's lane width.
    /// </summary>
    private void JoinedToFork(RoadComponent road, RoadComponent trunk, RoadForkComponent fork, bool left, bool atStart)
    {
        var shape = new RoadForkShape(trunk.Section, fork.Spec, 0f, float.MaxValue / 4f);
        if (!shape.Valid)
            return;
        var count = (left ? shape.Left : shape.Right).Count;
        // Going forwards (from its first point) where its traffic goes out of the fork into it at its start, or into the fork at its end.
        var forwards = shape.FlowsOut(left) == atStart;
        var values = new (string Name, object? Value)[]
        {
            (atStart ? nameof(RoadComponent.FromFork) : nameof(RoadComponent.ToFork), fork),
            (atStart ? nameof(RoadComponent.FromForksLeft) : nameof(RoadComponent.ToForksLeft), left),
            (nameof(RoadComponent.ForwardLanes), forwards ? count : 0),
            (nameof(RoadComponent.BackwardLanes), forwards ? 0 : count),
            (nameof(RoadComponent.LaneWidth), trunk.LaneWidth),
            (nameof(RoadComponent.MedianWidth), 0f),
        };
        if (editor.Session.AssetNodeContainer.GetNode(road) is { } node)
        {
            foreach (var (name, value) in values)
                node[name].Update(value);
            return;
        }
        foreach (var (name, value) in values)
            typeof(RoadComponent).GetProperty(name)!.SetValue(road, value);
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
    private void AddPoint(AbsoluteId roadId, Vector3 local, int index, Join? into = null)
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
            if (into is { } join && road.AssetSideEntity.Get<RoadComponent>() is { } component)
                Joined(road, component, join, false);
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
    private void AddRoad(AbsoluteId networkId, Vector3 local, int index, Join? from = null)
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

        if (from is { } join)
            Joined(network, road.Get<RoadComponent>()!, join, true);

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

    /// <summary>
    /// Selects the first point of the road carrying on from one of a road's ramps (its <paramref name="rampIndex"/>th),
    /// starting one there if there is none: beside the road, in the same parent, as wide as the ramp's lanes and going
    /// the way its traffic does, with its first point held at the ramp's end (<see cref="RoadComponent.FromRamp"/>).
    /// The next click carries it on. Starting it is one step to undo.
    /// </summary>
    private void ContinueFromRamp(AbsoluteId roadId, int rampIndex, Vector3 world)
    {
        if (editor.FindPartViewModel(roadId) is not EntityViewModel road)
            return;
        var rampAsset = road.AssetSideEntity.GetAll<RoadRampComponent>().ElementAtOrDefault(rampIndex);
        if (rampAsset == null)
            return;

        var parts = road.Asset.Asset.Hierarchy.Parts;
        var existing = parts.Values.Select(p => p.Entity).FirstOrDefault(e => e.Get<RoadComponent>()?.FromRamp == rampAsset);
        Entity? first;
        if (existing != null)
        {
            first = existing.Transform.Children.FirstOrDefault()?.Entity;
        }
        else
        {
            var parent = road.AssetSideEntity.GetParent();
            var exit = rampAsset.Kind == RampKind.Exit;
            var owner = road.AssetSideEntity.Get<RoadComponent>();
            var branch = new Entity("Ramp road")
            {
                new RoadComponent
                {
                    FromRamp = rampAsset,
                    // Its traffic leaves the ramp at an exit, so goes forwards from its first point; comes in at an entrance.
                    ForwardLanes = exit ? rampAsset.Lanes : 0,
                    BackwardLanes = exit ? 0 : rampAsset.Lanes,
                    LaneWidth = rampAsset.LaneWidth,
                    ShoulderWidth = rampAsset.ShoulderWidth,
                    ShoulderHeight = rampAsset.ShoulderHeight,
                    ShoulderAngle = rampAsset.ShoulderAngle,
                    SidewalkLeft = rampAsset.Sidewalk > 0f && (rampAsset.Side == RoadSide.Left || rampAsset.InnerSidewalk) ? rampAsset.Sidewalk : 0f,
                    SidewalkRight = rampAsset.Sidewalk > 0f && (rampAsset.Side == RoadSide.Right || rampAsset.InnerSidewalk) ? rampAsset.Sidewalk : 0f,
                    SidewalkFalloff = owner?.SidewalkFalloff ?? RoadMeshBuilder.SidewalkSkirt,
                },
            };
            branch.Transform.Position = parent != null
                ? Vector3.TransformCoordinate(world, Matrix.Invert(parent.Transform.WorldMatrix))
                : world;
            first = new Entity("Point 1");
            AssetCollectionItemIdHelper.GenerateMissingItemIds(branch);
            AssetCollectionItemIdHelper.GenerateMissingItemIds(first);
            var roads = new AssetPartCollection<EntityDesign, Entity> { new EntityDesign(branch, "") };
            var points = new AssetPartCollection<EntityDesign, Entity> { new EntityDesign(first, "") };
            var index = parent != null ? parent.Transform.Children.IndexOf(road.AssetSideEntity.Transform) + 1 : parts.Count;

            using (var transaction = editor.UndoRedoService.CreateTransaction())
            {
                var graph = road.Asset.AssetHierarchyPropertyGraph;
                graph.AddPartToAsset(roads, roads.Single().Value, parent, index);
                graph.AddPartToAsset(points, points.Single().Value, branch, 0);
                editor.UndoRedoService.SetName(transaction, $"Add road from ramp '{road.AssetSideEntity.Name}'");
            }
        }

        if (first != null && editor.FindPartViewModel(new AbsoluteId(roadId.AssetId, first.Id)) is { } selected)
        {
            editor.ClearSelection();
            editor.SelectedContent.Add(selected);
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
