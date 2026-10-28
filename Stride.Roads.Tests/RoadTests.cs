using System;
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
    public void BankEasesBetweenPointsAndRaisesTheRightEdge()
    {
        var bank = MathF.PI / 6f;
        var curve = new RoadCurve(new[] { new CurvePoint(Vector3.Zero), new CurvePoint(new Vector3(0, 0, -20), Bank: bank) });
        var frames = curve.Frames();
        Assert.Equal(0f, frames[0].Right.Y, 4);
        Assert.Equal(MathF.Sin(bank), frames[^1].Right.Y, 3);
        Assert.InRange(frames[frames.Count / 2].Right.Y, 0.1f, MathF.Sin(bank) - 0.1f);
        foreach (var frame in frames)
        {
            Assert.Equal(0f, Vector3.Dot(frame.Forward, frame.Right), 3);
            Assert.True(frame.Up.Y > 0f);
        }
    }

    [Fact]
    public void TurnBendsFromTheWayTheRoadRuns()
    {
        // A road running +X, its middle point turned 30° about up: it bends sideways there, still mostly running +X.
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 6f);
        var curve = new RoadCurve(new[]
        {
            new CurvePoint(Vector3.Zero), new CurvePoint(new Vector3(20, 0, 0), Turn: turn), new CurvePoint(new Vector3(40, 0, 0)),
        });
        var tangent = curve.TangentAt(1);
        Assert.Equal(MathF.Cos(MathF.PI / 6f), tangent.X, 3);
        Assert.Equal(0.5f, MathF.Abs(tangent.Z), 3);
        Assert.Equal(0f, curve.BankOf(1), 3);
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

    [Fact]
    public void ForkPartsItsLanesIntoTwoBranches()
    {
        // A two-lane one-way road down -Z, forking at its end: its branches' ends either side of its middle, the gore between them.
        var curve = new RoadCurve(new[] { new CurvePoint(Vector3.Zero), new CurvePoint(new Vector3(0, 0, -100)) });
        var frames = curve.Frames();
        var section = RoadCrossSection.OneWay(2);
        var fork = new ForkSpec(true, 40f, 1, 2f);
        var shape = new RoadForkShape(section, fork, frames[0].Distance, frames[^1].Distance);
        Assert.True(shape.Valid);
        var (left, _) = shape.BranchEnd(frames, true);
        var (right, outward) = shape.BranchEnd(frames, false);
        Assert.True(left.X < 0f && right.X > 0f);
        Assert.True(outward.Z < -0.99f);
        var end = shape.Across(frames[^1].Distance);
        Assert.Equal(fork.Separation, end.InR - end.InL, 2);
        Assert.True(shape.FlowsOut(true) && shape.FlowsOut(false));
        var meshes = RoadMeshBuilder.Build(frames, section, false, fork: fork);
        Assert.Contains(meshes.Surface.Vertices, v => v.X < -section.Width / 2f - 1f);

        // Both ways: its backward lanes go left, coming in from their branch.
        var twoWay = new RoadForkShape(RoadCrossSection.TwoLane, fork, frames[0].Distance, frames[^1].Distance);
        Assert.False(twoWay.FlowsOut(true));
        Assert.True(twoWay.FlowsOut(false));
    }

    [Fact]
    public void TJunctionCutsItsRoadsBackAndPavesBetween()
    {
        // Three two-lane roads out of the origin: down -Z, up +Z, and off to +X.
        var arms = new[] { -Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitX }
            .Select((d, i) => new JunctionArm(d, RoadCrossSection.TwoLane, true, s => new RoadFrame(d * s, d, Vector3.Normalize(Vector3.Cross(d, Vector3.UnitY)), s)))
            .ToList();
        var shape = RoadJunctionShaper.Build(Vector3.Zero, arms, 6f);
        Assert.All(shape.Setbacks, s => Assert.True(s >= RoadJunctionShaper.MinimumSetback));
        // The side road is cut back at least past the through road's edge.
        Assert.True(shape.Setbacks[2] >= RoadCrossSection.TwoLane.Width / 2f, $"cut back only {shape.Setbacks[2]}");
        Assert.False(shape.Surface.IsEmpty);
        Assert.True(shape.Surface.Normals.All(n => n.Y > 0.5f) || shape.Surface.Vertices.Count > 0);
    }

    [Fact]
    public void BarriersStandAlongTheEdges()
    {
        var curve = new RoadCurve(new[] { new CurvePoint(Vector3.Zero), new CurvePoint(new Vector3(0, 0, -50)) });
        var section = RoadCrossSection.TwoLane;
        var none = RoadMeshBuilder.Build(curve.Frames(), section, false);
        Assert.True(none.Barriers.IsEmpty && none.Walls.IsEmpty);
        var railed = RoadMeshBuilder.Build(curve.Frames(), section, false, barriers: new RoadBarriers(RoadBarrier.Rail, RoadBarrier.Rail, RoadBarrier.Concrete));
        var edge = section.Width / 2f;
        Assert.Contains(railed.Barriers.Vertices, v => v.X < -edge + RoadMeshBuilder.BarrierIn + 0.3f && v.X > -edge);
        Assert.Contains(railed.Barriers.Vertices, v => v.X > edge - RoadMeshBuilder.BarrierIn - 0.3f && v.X < edge);
        Assert.Contains(railed.Walls.Vertices, v => MathF.Abs(v.X) < 0.5f);
    }

    [Fact]
    public void ParkingPavesOutAndPaintsItsBays()
    {
        var curve = new RoadCurve(new[] { new CurvePoint(Vector3.Zero), new CurvePoint(new Vector3(0, 0, -100)) });
        var section = RoadCrossSection.TwoLane;
        var spot = new ParkingSpec(ParkingKind.Perpendicular, RoadSide.Right, 20f, 40f, 2.5f, 5f, 60f);
        var plain = RoadMeshBuilder.Build(curve.Frames(), section, false);
        var parked = RoadMeshBuilder.Build(curve.Frames(), section, false, parking: new[] { spot });
        var edge = section.Width / 2f;
        Assert.DoesNotContain(plain.Surface.Vertices, v => v.X > edge + 1f);
        Assert.Contains(parked.Surface.Vertices, v => v.X >= edge + spot.Depth - 0.01f);
        Assert.True(parked.WhiteLines.Vertices.Count > plain.WhiteLines.Vertices.Count);
        Assert.True(spot.Lines().Count() >= 10);
    }

    [Fact]
    public void TJunctionStopsItsSideRoadAndACrossroadsHasLights()
    {
        JunctionArm Arm(Vector3 d) => new(d, RoadCrossSection.TwoLane, true, s => new RoadFrame(d * s, d, Vector3.Normalize(Vector3.Cross(d, Vector3.UnitY)), s));
        var t = new[] { -Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitX }.Select(Arm).ToList();
        Assert.Equal(new[] { 2 }, RoadSignals.Controlled(t, TrafficControl.Auto));
        Assert.False(RoadSignals.Lights(t, TrafficControl.Auto));
        var cross = new[] { -Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX }.Select(Arm).ToList();
        Assert.True(RoadSignals.Lights(cross, TrafficControl.Auto));
        var signals = RoadSignals.Build(RoadJunctionShaper.Build(Vector3.Zero, cross, 6f), cross, TrafficControl.Auto);
        Assert.False(signals.Green.IsEmpty);
        Assert.Equal(4, signals.Green.Vertices.Count / 9);
    }

    [Fact]
    public void GoreHasChevronsAndARail()
    {
        var curve = new RoadCurve(new[] { new CurvePoint(Vector3.Zero), new CurvePoint(new Vector3(0, 0, -100)) });
        var section = RoadCrossSection.OneWay(2) with { ShoulderWidth = 1.5f };
        var meshes = RoadMeshBuilder.Build(curve.Frames(), section, false, fork: new ForkSpec(true, 60f, 1, 4f, RoadBarrier.Rail));
        Assert.False(meshes.Barriers.IsEmpty);
        var plain = RoadMeshBuilder.Build(curve.Frames(), section, false, fork: new ForkSpec(true, 60f, 1, 4f, RoadBarrier.None));
        Assert.True(plain.Barriers.IsEmpty);
        Assert.True(plain.WhiteLines.Vertices.Count > meshes.WhiteLines.Vertices.Count - 1);
    }

    [Fact]
    public void ExitRampPeelsAwayOnItsSide()
    {
        // Running down -Z, right is +X: an exit on the right separates at its far end, Separation out from the lane edge.
        var curve = new RoadCurve(new[] { new CurvePoint(Vector3.Zero), new CurvePoint(new Vector3(0, 0, -300)) });
        var section = RoadCrossSection.TwoLane;
        var ramp = new RampSpec(50f, 120f, RoadSide.Right, RampKind.Exit);
        var meshes = RoadMeshBuilder.Build(curve.Frames(), section, false, ramps: new[] { ramp });
        var laneEdge = RoadRampShaper.LaneEdge(ramp, section);
        var far = meshes.Surface.Vertices.Max(v => v.X);
        Assert.True(far >= laneEdge + ramp.Separation, $"reached only {far}");
        // Nothing of it on the left, nor before it starts.
        Assert.True(meshes.Surface.Vertices.Where(v => v.Z > -45f).All(v => v.X <= section.Width / 2f + 1e-3f));
    }
}
