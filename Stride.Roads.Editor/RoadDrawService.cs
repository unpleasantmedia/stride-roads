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
using Stride.Core.Quantum;
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
/// <item>likewise on a junction's middle, it joins the junction; and on the middle of another road, that road is split
/// in two there with a new junction between them, which it joins (a T-junction, and drawing on across it from the
/// junction, a crossroads);</item>
/// <item>a road ended at a junction leaves its network selected, and the next click starts a road from that junction to
/// there, carrying straight on across it;</item>
/// <item>deleting a road (however) tidies the junctions it met: one left with a single road goes, that road a dead end;
/// one left with two goes too, the two made one road where one ran on into the other, else joined end to end.</item>
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
        var graph = editor.Asset.AssetHierarchyPropertyGraph;
        foreach (var design in editor.Asset.Asset.Hierarchy.Parts.Values)
        {
            if (design.Entity.Get<RoadComponent>() != null)
                roadIds.Add(design.Entity.Id);
        }
        graph.PartAdded += (_, e) =>
        {
            if (editor.Asset.Asset.Hierarchy.Parts.TryGetValue(e.PartId, out var design) && design.Entity.Get<RoadComponent>() != null)
                roadIds.Add(e.PartId);
        };
        graph.PartRemoved += (_, e) =>
        {
            // A road deleted, not undone or redone (that puts back what was): tidy its junctions once it is all gone.
            if (!roadIds.Remove(e.PartId) || editor.UndoRedoService.UndoRedoInProgress || tidyQueued)
                return;
            tidyQueued = true;
            editor.Dispatcher.InvokeAsync(TidyJunctions);
        };
    }

    /// <summary>The scene's roads, by their entities' ids: to tell a road deleted from any other part.</summary>
    private readonly HashSet<Guid> roadIds = new();

    private bool tidyQueued;

    /// <summary>The junction a road just ended at, and its network: the next click with that network selected starts a road from it.</summary>
    private (Join Junction, AbsoluteId Network)? continueFrom;

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
            if (!delete && continueFrom is { } carry && carry.Network == target.Id)
            {
                // On from the junction a road just ended at: a new road from there to here.
                continueFrom = null;
                var toNetwork = Matrix.Invert(network.Entity.Transform.WorldMatrix);
                var (start, next) = (Vector3.TransformCoordinate(carry.Junction.Position, toNetwork), Vector3.TransformCoordinate(world, toNetwork));
                var count = network.Entity.Transform.Children.Count;
                editor.Dispatcher.InvokeAsync(() => AddRoad(target.Id, start, count, carry.Junction, next - start));
                return;
            }
            continueFrom = null;
            if (!delete)
            {
                var join = JoinAt(world, null);
                var inNetwork = Vector3.TransformCoordinate(join?.Position ?? world, Matrix.Invert(network.Entity.Transform.WorldMatrix));
                var roadCount = network.Entity.Transform.Children.Count;
                editor.Dispatcher.InvokeAsync(() => AddRoad(target.Id, inNetwork, roadCount, join));
            }
            return;
        }

        continueFrom = null;
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
    private readonly record struct Join(Guid Road, int? RampIndex, bool Last, Vector3 Position, bool? ForkLeft = null, bool Junction = false,
        int? SplitIndex = null, Vector3 SplitLocal = default, Vector3 SplitInParent = default, float SplitAlong = 0f);

    /// <summary>How near either end of a road a junction can split it, in metres.</summary>
    public const float SplitFromEnd = 6f;

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
            // Its ends, but not where a junction holds them: that is joined instead.
            if (other.FromJunction == null)
                Consider(other.Entity.Id, null, false, RoadProcessor.RoadEnd(other, false));
            if (other.ToJunction == null)
                Consider(other.Entity.Id, null, true, RoadProcessor.RoadEnd(other, true));
        }
        var junctions = game?.SceneSystem.SceneInstance?.Processors.OfType<RoadJunctionProcessor>().FirstOrDefault()?.Junctions.ToList();
        foreach (var junction in junctions ?? new List<RoadJunctionComponent>())
        {
            var at = junction.Entity.Transform.WorldMatrix.TranslationVector;
            var away = Vector2.Distance(new Vector2(at.X, at.Z), place);
            if (away > bestAway)
                continue;
            bestAway = away;
            best = new Join(junction.Entity.Id, null, false, at, Junction: true);
        }
        return best ?? SplitAt(world, except, processor);
    }

    /// <summary>
    /// A place on the middle of another road (on it, and not within <see cref="SplitFromEnd"/> of either end) where a
    /// junction can split it: the nearest, on its centre line.
    /// </summary>
    private static Join? SplitAt(Vector3 world, RoadComponent? except, RoadProcessor processor)
    {
        Join? best = null;
        var bestAway = float.MaxValue;
        foreach (var other in processor.Roads.ToList())
        {
            var frames = other.Frames;
            if (other == except || other.Entity == null || other.Closed || frames.Count < 2)
                continue;
            var toRoad = Matrix.Invert(other.Entity.Transform.WorldMatrix);
            var local = Vector3.TransformCoordinate(world, toRoad);
            var nearest = frames.MinBy(f => (f.Position.X - local.X) * (f.Position.X - local.X) + (f.Position.Z - local.Z) * (f.Position.Z - local.Z));
            var off = new System.Numerics.Vector3(local.X, nearest.Position.Y, local.Z) - nearest.Position;
            var across = System.Numerics.Vector3.Dot(off, nearest.Right);
            var alongIt = System.Numerics.Vector3.Dot(off, nearest.Forward);
            if (MathF.Abs(across) > other.Section.Width / 2f || MathF.Abs(alongIt) > RoadComponent.Spacing
                || nearest.Distance - frames[0].Distance < SplitFromEnd || frames[^1].Distance - nearest.Distance < SplitFromEnd || MathF.Abs(across) >= bestAway)
                continue;
            // Which of its points come after it: those nearest a frame further along.
            var points = RoadProcessor.Points(other);
            var index = points.Count(p => frames.MinBy(f => System.Numerics.Vector3.DistanceSquared(f.Position,
                new System.Numerics.Vector3(p.Transform.Position.X, p.Transform.Position.Y, p.Transform.Position.Z))).Distance < nearest.Distance);
            if (index < 1 || index >= points.Count + 1)
                continue;
            var centre = new Vector3(nearest.Position.X, nearest.Position.Y, nearest.Position.Z);
            var inWorld = Vector3.TransformCoordinate(centre, other.Entity.Transform.WorldMatrix);
            var parent = other.Entity.GetParent();
            var inParent = parent != null ? Vector3.TransformCoordinate(inWorld, Matrix.Invert(parent.Transform.WorldMatrix)) : inWorld;
            bestAway = MathF.Abs(across);
            best = new Join(other.Entity.Id, null, false, inWorld, SplitIndex: index, SplitLocal: centre, SplitInParent: inParent,
                SplitAlong: nearest.Distance - frames[0].Distance);
        }
        return best;
    }

    /// <summary>
    /// Splits a road in two where a <see cref="Join"/> says (asset side, as part of the transaction it is called in), with a
    /// new junction between them: the road ends there, and a new one like it starts there with its later points and
    /// whatever its end was joined to. The join, then, to that junction. Unchanged if it splits nothing.
    /// </summary>
    private Join Split(EntityViewModel anyInAsset, Join join)
    {
        if (join.SplitIndex is not { } index || !anyInAsset.Asset.Asset.Hierarchy.Parts.TryGetValue(join.Road, out var design)
            || design.Entity.Get<RoadComponent>() is not { } road)
            return join;
        var entity = design.Entity;
        var parent = entity.GetParent();
        var points = entity.Transform.Children.Select(t => t.Entity).ToList();
        index = Math.Clamp(index, 1, points.Count);
        var graph = anyInAsset.Asset.AssetHierarchyPropertyGraph;
        var siblings = parent != null ? parent.Transform.Children.IndexOf(entity.Transform) + 1 : anyInAsset.Asset.Asset.Hierarchy.RootParts.Count;

        var junction = new Entity("Junction") { new RoadJunctionComponent() };
        junction.Transform.Position = join.SplitInParent;
        // The rest of it: like it, from the junction on to wherever it ended.
        var rest = new RoadComponent();
        // Its own settings only: not the component's id (EntityComponent's), which must stay its own.
        foreach (var property in typeof(RoadComponent).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Where(p => p.CanWrite && p.GetCustomAttributes(typeof(DataMemberAttribute), false).Length > 0
            && !p.Name.StartsWith("From", StringComparison.Ordinal)))
            property.SetValue(rest, property.GetValue(road));
        rest.FromJunction = junction.Get<RoadJunctionComponent>();
        var restEntity = new Entity(entity.Name + " (2)") { rest };
        restEntity.Transform.Position = entity.Transform.Position;
        restEntity.Transform.Rotation = entity.Transform.Rotation;
        restEntity.Transform.Scale = entity.Transform.Scale;
        var restPoints = new List<Entity> { new Entity("Point 1") { Transform = { Position = join.SplitLocal } } };
        foreach (var point in points.Skip(index))
        {
            var copy = new Entity(point.Name) { Transform = { Position = point.Transform.Position, Rotation = point.Transform.Rotation, Scale = point.Transform.Scale } };
            if (point.Get<RoadPointComponent>() is { } settings)
                copy.Add(new RoadPointComponent { FollowRotation = settings.FollowRotation, KeepHeight = settings.KeepHeight, Handle = settings.Handle });
            // Its viaducts too (they go with the point, so keep their ids).
            foreach (var viaduct in point.GetAll<RoadViaductComponent>())
                copy.Add(AssetCloner.Clone(viaduct)!);
            restPoints.Add(copy);
        }
        var meeting = new Entity($"Point {index + 1}") { Transform = { Position = join.SplitLocal } };

        // What is built into it past the split goes with the rest of it: ramps starting past it (counted from the
        // junction now), a fork at its end; those keep their ids, so roads joined to them still are. A viaduct carrying
        // all of it carries both.
        var moved = new List<EntityComponent>();
        foreach (var ramp in entity.GetAll<RoadRampComponent>().Where(r => r.Start >= join.SplitAlong))
        {
            var copy = AssetCloner.Clone(ramp)!;
            copy.Start -= join.SplitAlong;
            restEntity.Add(copy);
            moved.Add(ramp);
        }
        if (entity.Get<RoadForkComponent>() is { AtEnd: true } endFork)
        {
            restEntity.Add(AssetCloner.Clone(endFork)!);
            moved.Add(endFork);
        }
        foreach (var viaduct in entity.GetAll<RoadViaductComponent>())
        {
            var copy = AssetCloner.Clone(viaduct)!;
            copy.Id = Guid.NewGuid();
            restEntity.Add(copy);
        }

        foreach (var made in new[] { junction, restEntity, meeting }.Concat(restPoints))
            AssetCollectionItemIdHelper.GenerateMissingItemIds(made);
        void Add(Entity part, Entity? under, int at)
        {
            var parts = new AssetPartCollection<EntityDesign, Entity> { new EntityDesign(part, "") };
            graph.AddPartToAsset(parts, parts.Single().Value, under, at);
        }
        Add(junction, parent, siblings);
        Add(restEntity, parent, siblings + 1);
        for (var i = 0; i < restPoints.Count; i++)
            Add(restPoints[i], restEntity, i);

        // This one ends at the junction now: its later points gone, one there instead, and its end joined to it.
        var gone = points.Skip(index).Select(p => anyInAsset.Asset.Asset.Hierarchy.Parts[p.Id]).ToList();
        if (gone.Count > 0)
        {
            graph.DeleteParts(gone, out var mapping);
            editor.UndoRedoService.PushOperation(new DeletedPartsTrackingOperation<EntityDesign, Entity>(anyInAsset.Asset, mapping));
        }
        Add(meeting, entity, index);
        if (moved.Count > 0)
        {
            var components = editor.Session.AssetNodeContainer.GetOrCreateNode(entity)[nameof(Entity.Components)].Target!;
            foreach (var component in moved)
                components.Remove(component, new NodeIndex(entity.Components.IndexOf(component)));
        }
        if (NodeOf(anyInAsset, road) is { } node)
        {
            Link(node, road, nameof(RoadComponent.ToJunction), junction.Get<RoadJunctionComponent>());
            node[nameof(RoadComponent.ToRamp)].Update(null);
            node[nameof(RoadComponent.ToRoad)].Update(null);
            node[nameof(RoadComponent.ToFork)].Update(null);
        }
        return new Join(junction.Id, null, false, join.Position, Junction: true);
    }

    /// <summary>Joins changed in the asset that the game side is still to be told of, by ids: the road's entity, which of its joins, and the component it names.</summary>
    private readonly List<(Guid Road, string Join, Guid Target)> unlinked = new();

    /// <summary>
    /// Changes a road's join (asset side) to name a component, through its node; and has the game side's follow
    /// (<see cref="LinkGameSide"/>), as the editor's own sync names it by its entity there, which may not be there yet.
    /// </summary>
    private void Link(Stride.Core.Quantum.IObjectNode node, RoadComponent road, string join, EntityComponent? target)
    {
        node[join].Update(target);
        // A junction's id after it, so the game has it even if the component comes as none.
        if (join is nameof(RoadComponent.FromJunction) or nameof(RoadComponent.ToJunction))
            node[join + "Id"].Update(target?.Id ?? Guid.Empty);
        if (target != null && road.Entity != null)
            unlinked.Add((road.Entity.Id, join, target.Id));
    }

    /// <summary>
    /// Points the game side's roads at what their joins changed to just now name, once it is all there: a new junction
    /// reaches the game side after the change naming it, which then finds nothing and names nothing.
    /// </summary>
    private async void LinkGameSide(AssetId assetId)
    {
        var links = unlinked.ToList();
        unlinked.Clear();
        // For a while, not just once: the editor's own change can come after, naming nothing again.
        for (var attempt = 0; attempt < 60 && links.Count > 0; attempt++)
        {
            await controller.InvokeAsync(() =>
            {
                if (game?.SceneSystem.SceneInstance?.RootScene is not { } scene)
                    return;
                var components = Components(scene).ToList();
                foreach (var link in links)
                {
                    if (FindEntity(new AbsoluteId(assetId, link.Road))?.Get<RoadComponent>() is not { } road
                        || components.FirstOrDefault(c => c.Id == link.Target) is not { } target)
                        continue;
                    var property = typeof(RoadComponent).GetProperty(link.Join)!;
                    if (property.GetValue(road) != target)
                        property.SetValue(road, target);
                }
            });
            await Task.Delay(50);
        }
    }

    /// <summary>All the components in a scene and its children's, for finding one by its id.</summary>
    private static IEnumerable<EntityComponent> Components(Scene scene) =>
        scene.Entities.SelectMany(e => e.GetChildren().Prepend(e).SelectMany(Descendants)).SelectMany(e => e.Components)
            .Concat(scene.Children.SelectMany(Components));

    private static IEnumerable<Entity> Descendants(Entity entity) => entity.GetChildren().SelectMany(Descendants).Prepend(entity);

    /// <summary>
    /// The node to change a road's settings through (asset side), so the change is recorded to undo and reaches the
    /// game: made if the editor has none for it yet. None for a road not in the asset yet, whose settings are set directly.
    /// </summary>
    private Stride.Core.Quantum.IObjectNode? NodeOf(EntityViewModel anyInAsset, RoadComponent road) =>
        road.Entity != null && anyInAsset.Asset.Asset.Hierarchy.Parts.ContainsKey(road.Entity.Id)
            ? editor.Session.AssetNodeContainer.GetOrCreateNode(road)
            : null;

    /// <summary>
    /// Joins one end of a road (asset side) to what a <see cref="Join"/> names, in the same asset: as part of the
    /// transaction it is called in. Nothing, if that isn't in the asset.
    /// </summary>
    private void Joined(EntityViewModel anyInAsset, RoadComponent road, Join join, bool atStart)
    {
        if (!anyInAsset.Asset.Asset.Hierarchy.Parts.TryGetValue(join.Road, out var design))
            return;
        if (join.Junction && design.Entity.Get<RoadJunctionComponent>() is { } junction)
        {
            var name = atStart ? nameof(RoadComponent.FromJunction) : nameof(RoadComponent.ToJunction);
            if (NodeOf(anyInAsset, road) is { } junctionNode)
                Link(junctionNode, road, name, junction);
            else if (atStart)
                road.FromJunction = junction;
            else
                road.ToJunction = junction;
            return;
        }
        if (design.Entity.Get<RoadComponent>() is not { } other)
            return;
        if (join.ForkLeft is { } forkLeft && design.Entity.Get<RoadForkComponent>() is { } fork)
        {
            JoinedToFork(anyInAsset, road, other, fork, forkLeft, atStart);
            return;
        }
        var ramp = join.RampIndex is { } i ? design.Entity.GetAll<RoadRampComponent>().ElementAtOrDefault(i) : null;
        if (ramp == null)
            Matched(anyInAsset, road, other, reversed: atStart != join.Last);
        // A road not yet in the asset is set directly; one in it, through its node, so the change can be undone.
        if (NodeOf(anyInAsset, road) is not { } node)
        {
            if (atStart)
                (road.FromRamp, road.FromRoad, road.FromRoadsEnd) = (ramp, ramp == null ? other : null, join.Last);
            else
                (road.ToRamp, road.ToRoad, road.ToRoadsEnd) = (ramp, ramp == null ? other : null, join.Last);
            return;
        }
        if (atStart)
        {
            Link(node, road, nameof(RoadComponent.FromRamp), ramp);
            Link(node, road, nameof(RoadComponent.FromRoad), ramp == null ? other : null);
            node[nameof(RoadComponent.FromRoadsEnd)].Update(join.Last);
        }
        else
        {
            Link(node, road, nameof(RoadComponent.ToRamp), ramp);
            Link(node, road, nameof(RoadComponent.ToRoad), ramp == null ? other : null);
            node[nameof(RoadComponent.ToRoadsEnd)].Update(join.Last);
        }
    }

    /// <summary>
    /// Makes a road (asset side) like another across, so where one's end joins the other's they meet edge to edge: its
    /// lanes, their width, its shoulders, median and sidewalks. Turned about if it runs the other way from there (its
    /// start on the other's start, or its end on the other's end): its lanes each way, and its sidewalks, swap sides.
    /// </summary>
    private void Matched(EntityViewModel anyInAsset, RoadComponent road, RoadComponent other, bool reversed)
    {
        var values = new (string Name, object Value)[]
        {
            (nameof(RoadComponent.ForwardLanes), reversed ? other.BackwardLanes : other.ForwardLanes),
            (nameof(RoadComponent.BackwardLanes), reversed ? other.ForwardLanes : other.BackwardLanes),
            (nameof(RoadComponent.LaneWidth), other.LaneWidth),
            (nameof(RoadComponent.ShoulderWidth), other.ShoulderWidth),
            (nameof(RoadComponent.MedianWidth), other.MedianWidth),
            (nameof(RoadComponent.Median), other.Median),
            (nameof(RoadComponent.SidewalkLeft), reversed ? other.SidewalkRight : other.SidewalkLeft),
            (nameof(RoadComponent.SidewalkRight), reversed ? other.SidewalkLeft : other.SidewalkRight),
        };
        if (NodeOf(anyInAsset, road) is { } node)
        {
            foreach (var (name, value) in values)
                node[name].Update(value);
            return;
        }
        foreach (var (name, value) in values)
            typeof(RoadComponent).GetProperty(name)!.SetValue(road, value);
    }

    /// <summary>
    /// Joins one end of a road (asset side) to one group of a fork as its branch: it takes that group's lanes, one way,
    /// going the way their traffic does, and the trunk's lane width.
    /// </summary>
    private void JoinedToFork(EntityViewModel anyInAsset, RoadComponent road, RoadComponent trunk, RoadForkComponent fork, bool left, bool atStart)
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
        if (NodeOf(anyInAsset, road) is { } node)
        {
            foreach (var (name, value) in values)
            {
                if (value is EntityComponent component)
                    Link(node, road, name, component);
                else
                    node[name].Update(value);
            }
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
        Join? ended = null;

        using (var transaction = editor.UndoRedoService.CreateTransaction())
        {
            road.Asset.AssetHierarchyPropertyGraph.AddPartToAsset(collection, collection.Single().Value, road.AssetSideEntity, index);
            if (into is { } join && road.AssetSideEntity.Get<RoadComponent>() is { } component)
            {
                ended = Split(road, join);
                Joined(road, component, ended.Value, false);
            }
            editor.UndoRedoService.SetName(transaction, $"Add road point '{point.Name}'");
        }
        LinkGameSide(roadId.AssetId);

        // Ended at a junction: its network selected, so the next click carries on from the junction across it.
        if (ended is { Junction: true } atJunction && road.Asset.Asset.Hierarchy.Parts.TryGetValue(atJunction.Road, out var junctionDesign)
            && junctionDesign.Entity.GetParent() is { } network && editor.FindPartViewModel(new AbsoluteId(roadId.AssetId, network.Id)) is { } networkViewModel)
        {
            editor.ClearSelection();
            editor.SelectedContent.Add(networkViewModel);
            continueFrom = (atJunction, new AbsoluteId(roadId.AssetId, network.Id));
            return;
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
    private void AddRoad(AbsoluteId networkId, Vector3 local, int index, Join? from = null, Vector3? next = null)
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
            if (from is { } join)
                Joined(network, road.Get<RoadComponent>()!, Split(network, join), true);
            var graph = network.Asset.AssetHierarchyPropertyGraph;
            graph.AddPartToAsset(roads, roads.Single().Value, network.AssetSideEntity, index);
            graph.AddPartToAsset(points, points.Single().Value, road, 0);
            // A second point too, where it is drawn on to at once.
            if (next is { } onTo)
            {
                point = new Entity("Point 2") { Transform = { Position = onTo } };
                AssetCollectionItemIdHelper.GenerateMissingItemIds(point);
                var second = new AssetPartCollection<EntityDesign, Entity> { new EntityDesign(point, "") };
                graph.AddPartToAsset(second, second.Single().Value, road, 1);
            }
            editor.UndoRedoService.SetName(transaction, $"Add road '{road.Name}'");
        }
        LinkGameSide(networkId.AssetId);

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

    /// <summary>
    /// After a road is deleted: each junction left with fewer than three roads goes. One road left is a dead end there;
    /// two are made one road where one runs on into the other (the second's points after the junction added to the
    /// first, and its far end's join), else joined end to end. One step to undo.
    /// </summary>
    private void TidyJunctions()
    {
        tidyQueued = false;
        var asset = editor.Asset;
        var parts = asset.Asset.Hierarchy.Parts;
        var roads = parts.Values.Select(p => p.Entity).Where(e => e.Get<RoadComponent>() != null).ToList();
        var junctions = parts.Values.Select(p => p.Entity).Where(e => e.Get<RoadJunctionComponent>() != null).ToList();
        var lonely = junctions.Select(j => (Junction: j, Id: j.Get<RoadJunctionComponent>()!.Id))
            .Select(j => (j.Junction, Arms: roads.SelectMany(r => new[] { (Road: r, Start: true), (Road: r, Start: false) })
                .Where(a => (a.Start ? a.Road.Get<RoadComponent>()!.FromJunctionId : a.Road.Get<RoadComponent>()!.ToJunctionId) == j.Id).ToList()))
            .Where(j => j.Arms.Count < 3).ToList();
        if (lonely.Count == 0)
            return;

        var graph = asset.AssetHierarchyPropertyGraph;
        var gone = new List<EntityDesign>();
        using (var transaction = editor.UndoRedoService.CreateTransaction())
        {
            foreach (var (junction, arms) in lonely)
            {
                foreach (var (road, start) in arms)
                    Unjoin(road.Get<RoadComponent>()!, start);
                if (arms.Count == 2)
                {
                    var (a, b) = (arms[0], arms[1]);
                    // One running on into the other: B after A (A ending here, B starting here).
                    if (a.Start && !b.Start)
                        (a, b) = (b, a);
                    var (first, second) = (a.Road.Get<RoadComponent>()!, b.Road.Get<RoadComponent>()!);
                    if (!a.Start && b.Start && a.Road != b.Road)
                    {
                        Merge(a.Road, b.Road);
                        gone.Add(parts[b.Road.Id]);
                    }
                    else
                    {
                        // Else held end to end: the first's end to the second's.
                        var node = editor.Session.AssetNodeContainer.GetOrCreateNode(first);
                        var name = a.Start ? nameof(RoadComponent.FromRoad) : nameof(RoadComponent.ToRoad);
                        Link(node, first, name, second);
                        node[a.Start ? nameof(RoadComponent.FromRoadsEnd) : nameof(RoadComponent.ToRoadsEnd)].Update(!b.Start);
                    }
                }
                gone.Add(parts[junction.Id]);
            }
            if (gone.Count > 0)
            {
                graph.DeleteParts(gone.Distinct().ToList(), out var mapping);
                editor.UndoRedoService.PushOperation(new DeletedPartsTrackingOperation<EntityDesign, Entity>(asset, mapping));
            }
            editor.UndoRedoService.SetName(transaction, "Tidy road junctions");
        }
        LinkGameSide(asset.Id);

        void Unjoin(RoadComponent road, bool start)
        {
            var node = editor.Session.AssetNodeContainer.GetOrCreateNode(road);
            var name = start ? nameof(RoadComponent.FromJunction) : nameof(RoadComponent.ToJunction);
            node[name].Update(null);
            node[name + "Id"].Update(Guid.Empty);
        }

        // B's points after its first (at the junction, as A's last is) added on to A, and B's far end's joins A's.
        void Merge(Entity a, Entity b)
        {
            var points = b.Transform.Children.Select(t => t.Entity).Skip(1).ToList();
            var at = a.Transform.Children.Count;
            Matrix Local(TransformComponent t) => Matrix.Scaling(t.Scale) * Matrix.RotationQuaternion(t.Rotation) * Matrix.Translation(t.Position);
            var toA = Matrix.Invert(Local(a.Transform));
            var fromB = Local(b.Transform);
            foreach (var point in points)
            {
                var copy = new Entity(point.Name) { Transform = { Position = Vector3.TransformCoordinate(Vector3.TransformCoordinate(point.Transform.Position, fromB), toA), Rotation = point.Transform.Rotation } };
                if (point.Get<RoadPointComponent>() is { } settings)
                    copy.Add(new RoadPointComponent { FollowRotation = settings.FollowRotation, KeepHeight = settings.KeepHeight, Handle = settings.Handle });
                AssetCollectionItemIdHelper.GenerateMissingItemIds(copy);
                var collection = new AssetPartCollection<EntityDesign, Entity> { new EntityDesign(copy, "") };
                graph.AddPartToAsset(collection, collection.Single().Value, a, at++);
            }
            var (ra, rb) = (a.Get<RoadComponent>()!, b.Get<RoadComponent>()!);
            var node = editor.Session.AssetNodeContainer.GetOrCreateNode(ra);
            Link(node, ra, nameof(RoadComponent.ToRamp), rb.ToRamp);
            Link(node, ra, nameof(RoadComponent.ToRoad), rb.ToRoad);
            Link(node, ra, nameof(RoadComponent.ToFork), rb.ToFork);
            Link(node, ra, nameof(RoadComponent.ToJunction), rb.ToJunction);
            node[nameof(RoadComponent.ToJunctionId)].Update(rb.ToJunctionId);
            node[nameof(RoadComponent.ToRoadsEnd)].Update(rb.ToRoadsEnd);
            node[nameof(RoadComponent.ToForksLeft)].Update(rb.ToForksLeft);
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
