using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Stride.Roads;

/// <summary>How the traffic into a junction is told when it may go.</summary>
public enum TrafficControl
{
    /// <summary>By how many roads meet it: at a T-junction, a stop sign on the road ending at the through road; at a crossroads or more, traffic lights.</summary>
    Auto,
    None,
    /// <summary>A stop sign on each road into it.</summary>
    StopSigns,
    /// <summary>Traffic lights on each road into it.</summary>
    TrafficLights,
}

/// <summary>A junction's signs and signals, as meshes by colour: their posts and heads' steel and black, a stop sign's red, and the lamps.</summary>
public sealed record SignalMeshes(MeshData Steel, MeshData Black, MeshData Red, MeshData Amber, MeshData Green, MeshData White);

/// <summary>Puts up a junction's stop signs or traffic lights, each beside its road's incoming lanes where it is cut off, facing the traffic.</summary>
public static class RoadSignals
{
    /// <summary>How far out from a road's paved edge (or past its sidewalk's curb) a post stands, and how far back from the junction.</summary>
    public const float PostOut = 0.6f, PostBack = 1f;

    public const float PostWidth = 0.1f, SignHeight = 2.2f, SignSize = 0.75f, SignalHeight = 3.6f;

    /// <summary>Which roads get a sign or signal, for how it is controlled and how many roads meet it.</summary>
    public static IEnumerable<int> Controlled(IReadOnlyList<JunctionArm> arms, TrafficControl control)
    {
        if (control == TrafficControl.None || arms.Count < 2)
            return Array.Empty<int>();
        if (control != TrafficControl.Auto || arms.Count >= 4)
            return Enumerable.Range(0, arms.Count);
        if (arms.Count < 3)
            return Array.Empty<int>();
        // A T: the road the other two run on through (nearest opposite each other) is the through road; the third stops.
        var (a, b) = (from i in Enumerable.Range(0, arms.Count) from j in Enumerable.Range(i + 1, arms.Count - i - 1) select (i, j))
            .MinBy(p => Vector3.Dot(arms[p.i].Direction, arms[p.j].Direction));
        return Enumerable.Range(0, arms.Count).Where(i => i != a && i != b);
    }

    /// <summary>Whether a junction's roads get traffic lights, rather than stop signs.</summary>
    public static bool Lights(IReadOnlyList<JunctionArm> arms, TrafficControl control) =>
        control == TrafficControl.TrafficLights || (control == TrafficControl.Auto && arms.Count >= 4);

    /// <summary>A junction's signs or signals, in the world, at its roads' cut ends (<see cref="JunctionShape.Ends"/>).</summary>
    public static SignalMeshes Build(JunctionShape shape, IReadOnlyList<JunctionArm> arms, TrafficControl control)
    {
        var meshes = new SignalMeshes(new MeshData(), new MeshData(), new MeshData(), new MeshData(), new MeshData(), new MeshData());
        if (shape.Ends is not { } ends)
            return meshes;
        var lights = Lights(arms, control);
        foreach (var i in Controlled(arms, control))
        {
            var arm = arms[i];
            var end = ends[i];
            // Traffic coming in has the arm's left (looking out along it) on its right: there, out past the paving or
            // the sidewalk's curb, a little back along the road.
            if (!arm.OutwardBands().Any(b => b.Incoming))
                continue;
            var (left, _) = arm.Edges();
            var (walkLeft, _) = arm.Sidewalks();
            var across = walkLeft > 0f ? left - 0.5f : left - PostOut;
            var outward = Level(end.Forward);
            var at = end.Across(across) + outward * PostBack;
            if (lights)
                Signal(meshes, at, outward);
            else
                Stop(meshes, at, outward);
        }
        return meshes;
    }

    /// <summary>A stop sign: a post, and a red octagon on it with a white band round, facing the way it is told.</summary>
    private static void Stop(SignalMeshes meshes, Vector3 at, Vector3 facing)
    {
        var right = Vector3.Normalize(Vector3.Cross(facing, Vector3.UnitY));
        Post(meshes.Steel, at, SignHeight + SignSize / 2f);
        var middle = at + Vector3.UnitY * SignHeight + facing * (PostWidth / 2f + 0.01f);
        Octagon(meshes.White, middle, right, facing, SignSize / 2f);
        Octagon(meshes.Red, middle + facing * 0.005f, right, facing, SignSize / 2f * 0.88f);
        Octagon(meshes.Steel, middle - facing * 0.005f, right, -facing, SignSize / 2f);
    }

    /// <summary>Traffic lights: a pole, and a black head on it with red, amber and green lamps, facing the way it is told.</summary>
    private static void Signal(SignalMeshes meshes, Vector3 at, Vector3 facing)
    {
        var right = Vector3.Normalize(Vector3.Cross(facing, Vector3.UnitY));
        Post(meshes.Steel, at, SignalHeight);
        var (width, height, depth) = (0.35f, 1f, 0.25f);
        var middle = at + Vector3.UnitY * (SignalHeight - height / 2f) + facing * (PostWidth / 2f + depth / 2f);
        Box(meshes.Black, middle, right, Vector3.UnitY, facing, new Vector3(width, height, depth) / 2f);
        foreach (var (mesh, up) in new[] { (meshes.Red, 0.3f), (meshes.Amber, 0f), (meshes.Green, -0.3f) })
            Octagon(mesh, middle + Vector3.UnitY * up + facing * (depth / 2f + 0.01f), right, facing, 0.11f);
    }

    /// <summary>A square post from a little into the ground up this high.</summary>
    private static void Post(MeshData mesh, Vector3 at, float height) =>
        Box(mesh, at + Vector3.UnitY * (height - 0.5f) / 2f, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(PostWidth, height + 0.5f, PostWidth) / 2f);

    /// <summary>A flat octagon, this far out to its corners, facing the way it is told.</summary>
    private static void Octagon(MeshData mesh, Vector3 middle, Vector3 right, Vector3 facing, float radius)
    {
        var centre = mesh.Add(middle, facing, Vector2.Zero);
        var first = mesh.Vertices.Count;
        for (var k = 0; k < 8; k++)
        {
            var angle = MathF.PI / 8f + k * MathF.PI / 4f;
            mesh.Add(middle + (right * MathF.Cos(angle) + Vector3.UnitY * MathF.Sin(angle)) * radius, facing, Vector2.Zero);
        }
        for (var k = 0; k < 8; k++)
            mesh.Triangle(centre, first + k, first + (k + 1) % 8);
    }

    private static void Box(MeshData mesh, Vector3 centre, Vector3 x, Vector3 y, Vector3 z, Vector3 half)
    {
        foreach (var (n, u, v, h, hu, hv) in new[]
        {
            (x, y, z, half.X, half.Y, half.Z), (-x, y, z, half.X, half.Y, half.Z),
            (y, x, z, half.Y, half.X, half.Z), (-y, x, z, half.Y, half.X, half.Z),
            (z, x, y, half.Z, half.X, half.Y), (-z, x, y, half.Z, half.X, half.Y),
        })
        {
            var c = centre + n * h;
            mesh.Quad(mesh.Add(c - u * hu - v * hv, n, Vector2.Zero), mesh.Add(c + u * hu - v * hv, n, Vector2.Zero),
                mesh.Add(c + u * hu + v * hv, n, Vector2.Zero), mesh.Add(c - u * hu + v * hv, n, Vector2.Zero));
        }
    }

    private static Vector3 Level(Vector3 v)
    {
        var flat = v with { Y = 0f };
        return flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : Vector3.UnitZ;
    }
}
