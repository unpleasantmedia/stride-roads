using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Game;
using Stride.Assets.Presentation.AssetEditors.Gizmos;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Gizmos;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Roads.Editor;

/// <summary>
/// A road in the scene editor: a billboard at its entity, so it can be found and picked where it has
/// no points yet, and while it or one of its points is selected, a line along its middle and a cross at
/// each of its points, so their places and order show without picking through the scene tree.
/// </summary>
[GizmoComponent(typeof(RoadComponent), true)]
public class RoadComponentGizmo : BillboardingGizmo<RoadComponent>
{
    /// <summary>How far each arm of a point's cross reaches, in metres.</summary>
    private const float CrossSize = 0.5f;

    /// <summary>How high above the road its line and crosses are drawn, in metres, so the surface doesn't hide them.</summary>
    private const float Lift = 0.05f;

    private static readonly Color LineColor = new(1f, 0.85f, 0.2f, 1f);

    /// <summary>The outline's colour while <see cref="RoadDrawService"/> is drawing.</summary>
    private static readonly Color DrawingColor = new(0.2f, 0.9f, 1f, 1f);

    private Entity? outline;
    private Mesh? mesh;
    private Material? material;
    private IEditorGameEntitySelectionService? selection;
    private RoadDrawService? drawing;
    private bool? drawnAsDrawing;
    private int? builtState;

    public RoadComponentGizmo(EntityComponent component)
        : base(component, "Road", RoadsEditorPlugin.LoadResource("RoadsGizmo.png"))
    {
    }

    protected override Entity Create()
    {
        var root = base.Create();
        mesh = new Mesh { Draw = new MeshDraw { PrimitiveType = PrimitiveType.LineList } };
        material = GizmoEmissiveColorMaterial.Create(GraphicsDevice, LineColor);
        // A child of the root, which sits where the road's entity is and turns with it (only the billboard is
        // scaled), so the outline is drawn in the road's own space, as its frames and points are.
        outline = new Entity("Road outline")
        {
            new ModelComponent
            {
                Model = new Model { material, mesh },
                RenderGroup = RenderGroup,
            },
        };
        return root;
    }

    public override void Update()
    {
        base.Update();
        if (outline == null)
            return;

        selection ??= Game.EditorServices.Get<IEditorGameEntitySelectionService>();
        drawing ??= Game.EditorServices.Get<RoadDrawService>();
        var selected = IsRoadSelected();
        if (selected)
            Rebuild();

        var isDrawing = drawing?.IsDrawing == true;
        if (isDrawing != drawnAsDrawing && material != null)
        {
            GizmoEmissiveColorMaterial.UpdateColor(GraphicsDevice, material, isDrawing ? DrawingColor : LineColor);
            drawnAsDrawing = isDrawing;
        }
        // Shown only with something to draw: an outline with no buffers is never handed to the renderer.
        var shown = selected && mesh?.Draw.VertexBuffers != null;
        if (shown && outline.Transform.Parent == null)
            GizmoRootEntity.AddChild(outline);
        else if (!shown && outline.Transform.Parent != null)
            GizmoRootEntity.RemoveChild(outline);
    }

    protected override void Destroy()
    {
        Release();
        base.Destroy();
    }

    /// <summary>Whether the road, or one of its points, is in the editor's selection.</summary>
    private bool IsRoadSelected()
    {
        if (IsSelected)
            return true;
        if (selection == null)
            return false;
        var ids = selection.GetSelectedIds();
        if (ids.Count == 0)
            return false;
        var road = ContentEntity.Id;
        var points = RoadProcessor.Points(Component).Select(p => p.Id).ToHashSet();
        return ids.Any(id => id.ObjectId == road || points.Contains(id.ObjectId));
    }

    /// <summary>Remakes the line and crosses if the road's frames or points have changed since they were made.</summary>
    private void Rebuild()
    {
        var frames = Component.Frames;
        var points = RoadProcessor.Points(Component).Select(p => p.Transform.Position).ToList();
        var hash = new HashCode();
        foreach (var frame in frames)
            hash.Add(frame.Position);
        foreach (var point in points)
            hash.Add(point);
        var state = hash.ToHashCode();
        if (state == builtState)
            return;
        builtState = state;

        var lines = new List<Vector3>();
        for (var i = 1; i < frames.Count; i++)
        {
            lines.Add(ToStride(frames[i - 1].Position));
            lines.Add(ToStride(frames[i].Position));
        }
        if (Component.Closed && frames.Count > 2)
        {
            lines.Add(ToStride(frames[^1].Position));
            lines.Add(ToStride(frames[0].Position));
        }
        foreach (var point in points)
        {
            var at = point + Vector3.UnitY * Lift;
            lines.Add(at - Vector3.UnitX * CrossSize);
            lines.Add(at + Vector3.UnitX * CrossSize);
            lines.Add(at - Vector3.UnitZ * CrossSize);
            lines.Add(at + Vector3.UnitZ * CrossSize);
            lines.Add(at);
            lines.Add(at + Vector3.UnitY * CrossSize * 2f);
        }

        Release();
        if (lines.Count == 0)
            return;
        var vertices = lines.Select(p => new VertexPositionNormalTexture(p, Vector3.UnitY, Vector2.Zero)).ToArray();
        var indices = Enumerable.Range(0, vertices.Length).ToArray();
        var draw = mesh!.Draw;
        draw.VertexBuffers = [new VertexBufferBinding(Buffer.Vertex.New(GraphicsDevice, vertices), VertexPositionNormalTexture.Layout, vertices.Length)];
        draw.IndexBuffer = new IndexBufferBinding(Buffer.Index.New(GraphicsDevice, indices), true, indices.Length);
        draw.DrawCount = indices.Length;
        var bounds = BoundingBox.FromPoints(lines.ToArray());
        mesh.BoundingBox = bounds;
        mesh.BoundingSphere = BoundingSphere.FromBox(bounds);
    }

    private static Vector3 ToStride(System.Numerics.Vector3 v) => new(v.X, v.Y + Lift, v.Z);

    /// <summary>Frees the line's buffers: they are its own.</summary>
    private void Release()
    {
        if (mesh?.Draw is not { } draw)
            return;
        draw.IndexBuffer?.Buffer.Dispose();
        draw.IndexBuffer = null;
        draw.DrawCount = 0;
        if (draw.VertexBuffers != null)
        {
            foreach (var binding in draw.VertexBuffers)
                binding.Buffer.Dispose();
            draw.VertexBuffers = null;
        }
    }
}
