using System.Linq;
using System.Numerics;
using Stride.Roads;
using Xunit;

namespace Stride.Roads.Tests;

public class RoadCurveTests
{
    private static RoadCurve Straight(Vector3 to) => new(new[] { new CurvePoint(Vector3.Zero), new CurvePoint(to) });

    [Fact]
    public void RightIsPlusXWhenRunningDownMinusZ()
    {
        // Stride: right-handed, y up, forward -Z, right +X.
        var frames = Straight(new Vector3(0, 0, -10)).Frames();
        foreach (var frame in frames)
            Assert.True(Vector3.Distance(frame.Right, Vector3.UnitX) < 1e-4f, $"right was {frame.Right}");
    }

    [Fact]
    public void RightIsLevelAndSquareToForward()
    {
        var curve = new RoadCurve(new[]
        {
            new CurvePoint(Vector3.Zero), new CurvePoint(new Vector3(20, 3, -10)), new CurvePoint(new Vector3(40, 0, 10)),
        });
        foreach (var frame in curve.Frames())
        {
            Assert.Equal(0f, frame.Right.Y, 4);
            Assert.Equal(1f, frame.Right.Length(), 4);
            var level = Vector3.Normalize(frame.Forward with { Y = 0 });
            Assert.Equal(0f, Vector3.Dot(level, frame.Right), 3);
        }
    }

    [Fact]
    public void FramesAreEvenlySpacedEndToEnd()
    {
        var frames = Straight(new Vector3(0, 0, -10)).Frames(1f);
        Assert.Equal(11, frames.Count);
        Assert.Equal(0f, frames[0].Distance, 3);
        Assert.Equal(10f, frames[^1].Distance, 3);
        Assert.True(Vector3.Distance(frames[^1].Position, new Vector3(0, 0, -10)) < 1e-3f);
    }

    [Fact]
    public void CurvePassesThroughItsPoints()
    {
        var points = new[] { Vector3.Zero, new Vector3(10, 0, -10), new Vector3(20, 2, 0), new Vector3(30, 0, -5) };
        var curve = new RoadCurve(points.Select(p => new CurvePoint(p)).ToList());
        var frames = curve.Frames(0.5f, out var pointsAt);
        for (var i = 0; i < points.Length; i++)
            Assert.True(Vector3.Distance(RoadCurve.At(frames, pointsAt[i]).Position, points[i]) < 0.1f, $"point {i}");
    }

    [Fact]
    public void ClosedRoadHasNoDuplicateEndFrame()
    {
        var curve = new RoadCurve(new[]
        {
            new CurvePoint(Vector3.Zero), new CurvePoint(new Vector3(10, 0, 0)), new CurvePoint(new Vector3(5, 0, -10)),
        }, closed: true);
        var frames = curve.Frames();
        Assert.True(curve.Closed);
        Assert.True(Vector3.Distance(frames[0].Position, frames[^1].Position) > 0.5f);
    }

    [Fact]
    public void TrimKeepsTheRequestedStretch()
    {
        var frames = Straight(new Vector3(0, 0, -10)).Frames();
        var kept = RoadCurve.Trim(frames, 2.5f, 7.5f);
        Assert.Equal(2.5f, kept[0].Distance, 3);
        Assert.Equal(7.5f, kept[^1].Distance, 3);
    }
}

public class RoadCrossSectionTests
{
    [Fact]
    public void BandsCoverTheWidthLeftToRight()
    {
        var section = RoadCrossSection.Divided(2);
        var bands = section.Bands();
        for (var i = 1; i < bands.Count; i++)
            Assert.Equal(bands[i - 1].Right, bands[i].Left, 4);
        Assert.Equal(section.Width, bands[^1].Right - bands[0].Left, 3);
    }

    [Fact]
    public void BankAtFortyFiveDegreesIsAsWideAsItIsHigh() => Assert.Equal(2f, RoadCrossSection.Bank(2f, 45f), 4);
}

public class RoadLanesTests
{
    [Fact]
    public void ForwardTrafficDrivesOnTheRight()
    {
        var frames = new RoadCurve(new[] { new CurvePoint(Vector3.Zero), new CurvePoint(new Vector3(0, 0, -20)) }).Frames();
        var paths = RoadLanes.Paths(frames, RoadCrossSection.TwoLane, closed: false);
        var forward = paths.Single(p => p.Direction == LaneDirection.Forward);
        var backward = paths.Single(p => p.Direction == LaneDirection.Backward);
        Assert.True(forward.Points[0].X > 0f, "forward lane should be at +X (right when heading -Z)");
        Assert.True(backward.Points[0].X < 0f);
        Assert.True(forward.Points[^1].Z < forward.Points[0].Z, "forward lane runs with the road");
        Assert.True(backward.Points[^1].Z > backward.Points[0].Z, "backward lane runs against it");
    }
}

public class RoadMeshTests
{
    [Fact]
    public void FlatRoadSurfaceFacesUpClockwise()
    {
        var curve = new RoadCurve(new[]
        {
            new CurvePoint(Vector3.Zero), new CurvePoint(new Vector3(15, 0, -15)), new CurvePoint(new Vector3(30, 0, 0)),
        });
        var meshes = RoadMeshBuilder.Build(curve.Frames(), RoadCrossSection.TwoLane, closed: false);
        var surface = meshes.Surface;
        Assert.False(surface.IsEmpty);
        Assert.Equal(0, surface.Indices.Count % 3);
        var up = 0;
        for (var i = 0; i < surface.Indices.Count; i += 3)
        {
            var (ia, ib, ic) = (surface.Indices[i], surface.Indices[i + 1], surface.Indices[i + 2]);
            var (a, b, c) = (surface.Vertices[ia], surface.Vertices[ib], surface.Vertices[ic]);
            var facing = Vector3.Cross(b - a, c - a);
            if (facing.LengthSquared() < 1e-10f)
                continue;
            // Clockwise seen from the side its normals point to (deck up, edge skirts outwards), as Stride culls.
            var normal = surface.Normals[ia] + surface.Normals[ib] + surface.Normals[ic];
            Assert.True(Vector3.Dot(facing, normal) < 0f, $"triangle {i / 3} is wound against its normals");
            if (normal.Y > 2.9f)
            {
                Assert.True(facing.Y < 0f, $"deck triangle {i / 3} faces down");
                up++;
            }
        }
        Assert.True(up > 0, "no deck triangles");
    }
}
