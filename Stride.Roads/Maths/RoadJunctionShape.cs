using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Stride.Roads;

/// <summary>
/// A road meeting a junction: which way it leaves the junction's centre (level), what it is like
/// across, and whether it starts there (its first point at the junction) or ends there. Its
/// <see cref="Along"/>, if it has one, says where the road really is a distance out along it from
/// the junction, facing out: a road that bends near the junction is cut off, and met, there,
/// rather than where it would be if it ran straight out.
/// </summary>
/// <remarks>Its road is never cut back further than <see cref="MaxSetback"/>: so a short road between two junctions keeps some of itself.</remarks>
public readonly record struct JunctionArm(Vector3 Direction, RoadCrossSection Section, bool StartsHere, Func<float, RoadFrame>? Along = null,
    float MaxSetback = float.MaxValue)
{
    /// <summary>
    /// Its bands as seen looking out from the junction along it: a road ending here is seen from
    /// its far end, so left and right, and which way its lanes go, turn about.
    /// </summary>
    public IReadOnlyList<(RoadBand Band, bool Incoming)> OutwardBands()
    {
        var bands = Section.Bands();
        var seen = new List<(RoadBand, bool)>();
        foreach (var band in bands)
        {
            // Outgoing traffic (away from the junction) is the road's Forward if it starts here.
            var outgoing = band.Direction == (StartsHere ? LaneDirection.Forward : LaneDirection.Backward);
            var mirrored = StartsHere ? band : band with { Left = -band.Right, Right = -band.Left };
            seen.Add((mirrored, band.Kind == BandKind.Lane && !outgoing));
        }

        return seen.OrderBy(b => b.Item1.Left).ToList();
    }

    /// <summary>How far its road reaches left and right of its centre line, looking out along it: its sidewalks are past that.</summary>
    public (float Left, float Right) Edges()
    {
        var bands = OutwardBands().Where(b => b.Band.Kind != BandKind.Sidewalk).ToList();
        return (bands[0].Band.Left, bands[^1].Band.Right);
    }

    /// <summary>Its outer edges left and right, looking out along it, sidewalks and all: where signs stand next to.</summary>
    public (float Left, float Right) OuterEdges()
    {
        var bands = OutwardBands().ToList();
        return (bands[0].Band.Left, bands[^1].Band.Right);
    }

    /// <summary>How wide its sidewalks are left and right, looking out along it; 0 for none.</summary>
    public (float Left, float Right) Sidewalks()
    {
        var (left, right) = Edges();
        var walks = OutwardBands().Where(b => b.Band.Kind == BandKind.Sidewalk).Select(b => b.Band).ToList();
        return (walks.Where(b => b.Right <= left + 1e-3f).Sum(b => b.Width), walks.Where(b => b.Left >= right - 1e-3f).Sum(b => b.Width));
    }

    /// <summary>Where its outer lanes end and shoulders begin, left and right, looking out along it.</summary>
    public (float Left, float Right) LaneEdges()
    {
        var lanes = OutwardBands().Where(b => b.Band.Kind == BandKind.Lane).ToList();
        return (lanes.Min(b => b.Band.Left), lanes.Max(b => b.Band.Right));
    }

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Direction, Vector3.UnitY));
}

/// <summary>
/// A path traffic can take through a junction, from the end of one arm's incoming lane (counted
/// from the inside out) to the start of another's outgoing lane.
/// </summary>
public sealed record LaneConnection(int FromArm, int FromLane, int ToArm, int ToLane, IReadOnlyList<Vector3> Points);

/// <summary>
/// What a junction is: how far each arm's road stops short of its centre, the outline of its
/// surface (round clockwise on a map, north up), its surface and lines, the paths through it, and
/// the banks sloping away round its corners, and those corners' edges (each running clockwise
/// round it, from one road to the next), and where each road is cut off (facing out along it, in
/// the arms' order); and its middle, where that is not the junction's own place (a cul-de-sac's
/// is at its road's height).
/// </summary>
/// <remarks>
/// Its sidewalks, round the corners between roads with sidewalks on the sides facing each other,
/// have a mesh of their own; its corners' edges are then their far edges, where the banks start,
/// and its <see cref="Reach"/> takes them in, for the ground to be fitted under them.
/// </remarks>
public sealed record JunctionShape(IReadOnlyList<float> Setbacks, IReadOnlyList<Vector3> Outline, MeshData Surface, MeshData WhiteLines,
    IReadOnlyList<LaneConnection> Connections, MeshData Banks, IReadOnlyList<IReadOnlyList<Vector3>> Corners,
    IReadOnlyList<RoadFrame>? Ends = null, System.Numerics.Vector3? Middle = null, MeshData? Sidewalks = null, IReadOnlyList<Vector3>? OuterOutline = null)
{
    /// <summary>Its outline with its sidewalks: all the ground it covers.</summary>
    public IReadOnlyList<Vector3> Reach => OuterOutline ?? Outline;
}

/// <summary>
/// Works out a junction from the roads meeting at it. Each arm's road is cut back from the centre
/// until its edge meets its neighbours', with a rounded corner of the given radius between; the
/// junction's surface fills from those cut ends round the corners, from the centre's height out to
/// each road's own height where it is cut off, so a road climbing to it meets it without a step.
/// Edge lines carry on round the corners; each arm has a stop line across its incoming lanes.
/// Round each corner a bank slopes away, bankHeight down (up, if negative) at bankAngle degrees,
/// meeting the banks of the roads either side where they are cut off.
/// </summary>
public static class RoadJunctionShaper
{
    /// <summary>How high a fan from the middle out to the outline is over a point; none if the point is not over it.</summary>
    public static float? FanHeight(Vector3 middle, IReadOnlyList<Vector3> outline, Vector2 at)
    {
        var m = new Vector2(middle.X, middle.Z);
        for (var i = 0; i < outline.Count; i++)
        {
            var (p, q) = (outline[i], outline[(i + 1) % outline.Count]);
            var (a, b) = (new Vector2(p.X, p.Z), new Vector2(q.X, q.Z));
            // Barycentric weights of the point in the triangle middle, a, b.
            var d = (b.Y - m.Y) * (a.X - m.X) + (m.X - b.X) * (a.Y - m.Y);
            if (MathF.Abs(d) < 1e-8f)
                continue;
            var wa = ((b.Y - m.Y) * (at.X - m.X) + (m.X - b.X) * (at.Y - m.Y)) / d;
            var wb = ((m.Y - a.Y) * (at.X - m.X) + (a.X - m.X) * (at.Y - m.Y)) / d;
            var wm = 1f - wa - wb;
            if (wa >= -1e-4f && wb >= -1e-4f && wm >= -1e-4f)
                return wm * middle.Y + wa * p.Y + wb * q.Y;
        }

        return null;
    }

    /// <summary>The least any arm is cut back, so even two roads meeting in a line have a junction between them.</summary>
    public const float MinimumSetback = 2f;

    public const float StopLineWidth = 0.4f;

    /// <summary>How far apart the points along a junction's lines are, so they follow the folds in its surface.</summary>
    private const float LineStep = 0.25f;

    /// <summary>How far a junction's lines stand above its surface: a little more than a road's, over its folds.</summary>
    public const float LineLift = RoadMeshBuilder.LineLift * 2f;

    /// <summary>How far past a stop line a turning path starts, and how far before the next road's start it ends.</summary>
    private const int CurvePoints = 12;

    public static JunctionShape Build(Vector3 centre, IReadOnlyList<JunctionArm> arms, float cornerRadius = 6f,
        float bankHeight = 0f, float bankAngle = RoadCrossSection.WalkableAngle, float turningRadius = 0f, bool carried = false)
    {
        // Its sidewalks' far sides run deep into the ground, but not under a viaduct's deck.
        var sidewalkDepth = carried ? RoadMeshBuilder.SkirtDepth : RoadMeshBuilder.SidewalkSkirt;
        // A single road: a cul-de-sac, turning round at its end.
        if (arms.Count == 1)
            return CulDeSac(centre, arms[0], cornerRadius, bankHeight, bankAngle, turningRadius, sidewalkDepth);

        var order = Enumerable.Range(0, arms.Count).OrderBy(i => Angle(arms[i].Direction)).ToList();
        var setbacks = new float[arms.Count];
        for (var i = 0; i < arms.Count; i++)
            setbacks[i] = MinimumSetback;

        // Each arm and the next round (clockwise on a map with north, -z, up): where their facing edges meet.
        var corners = new List<Corner>();
        for (var k = 0; k < order.Count && order.Count > 1; k++)
        {
            var (a, b) = (order[k], order[(k + 1) % order.Count]);
            // Where their edges meet, past any sidewalks on them: so round a sharp corner the roads are
            // cut back until their sidewalks have come apart, rather than leave a sliver of ground between them.
            var walk = (arms[a].Sidewalks().Right, arms[b].Sidewalks().Left);
            var corner = Meet(centre, arms[a], arms[b], useLanes: false, walk.Right, walk.Left);
            if (corner is { } c)
            {
                // Rounder by a sidewalk's width round it, so the sidewalk's own curve is no tighter than the corner's.
                var radius = cornerRadius + MathF.Max(walk.Right, walk.Left);
                setbacks[a] = MathF.Max(setbacks[a], c.AlongA + radius);
                setbacks[b] = MathF.Max(setbacks[b], c.AlongB + radius);
            }
        }

        // No further than each road can be cut back.
        for (var i = 0; i < arms.Count; i++)
            setbacks[i] = MathF.Min(setbacks[i], MathF.Max(arms[i].MaxSetback, MinimumSetback));

        var surface = new MeshData();
        var white = new MeshData();
        var connections = new List<LaneConnection>();
        var banks = new MeshData();
        if (arms.Count == 0)
            return new JunctionShape(setbacks, Array.Empty<Vector3>(), surface, white, connections, banks, Array.Empty<IReadOnlyList<Vector3>>());

        // The outline: round the arms in order, each arm's cut end, then the corner to the next.
        var outline = new List<Vector3>();
        for (var k = 0; k < order.Count; k++)
        {
            var (a, b) = (order[k], order[(k + 1) % order.Count]);
            var arm = arms[a];
            var (left, right) = arm.Edges();
            var end = EndOf(centre, arm, setbacks[a]);
            // Going round clockwise, an arm's left edge comes first, then its right, facing the next.
            outline.Add(end.Across(left));
            outline.Add(end.Across(right));
            if (order.Count > 1)
                outline.AddRange(CornerCurve(centre, arms[a], setbacks[a], arms[b], setbacks[b], useLanes: false).Skip(1).SkipLast(1));
        }

        Fan(surface, centre, outline);

        // Lines and paths lie on the surface: it rises or falls from the centre out to each road.
        float? Surface(Vector3 p) => RoadJunctionShaper.FanHeight(centre, outline, new Vector2(p.X, p.Z));
        IReadOnlyList<Vector3> OnSurface(IEnumerable<Vector3> points) => points.Select(p => p with { Y = Surface(p) ?? p.Y }).ToList();
        // Lines closely follow its folds, rather than cutting under them between their points.
        IReadOnlyList<Vector3> AlongSurface(IReadOnlyList<Vector3> line) => OnSurface(Subdivided(line, LineStep));

        // Which roads have a crosswalk: those with a sidewalk.
        bool Crossed(int arm) => arms[arm].Sidewalks() is var (l, r) && (l > 0f || r > 0f) && setbacks[arm] - CrosswalkIn - CrosswalkLength >= 0.5f;

        // Edge lines round each corner, where they meet the roads' own; stopping short of a crosswalk at either end.
        for (var k = 0; k < order.Count && order.Count > 1; k++)
        {
            var (a, b) = (order[k], order[(k + 1) % order.Count]);
            // None along a curb: where either road has a sidewalk on the side facing the other.
            if (arms[a].Sidewalks().Right > 0f || arms[b].Sidewalks().Left > 0f)
                continue;
            IEnumerable<Vector3> line = CornerCurve(centre, arms[a], setbacks[a], arms[b], setbacks[b], useLanes: true);
            foreach (var arm in new[] { a, b }.Where(Crossed))
            {
                var end = EndOf(centre, arms[arm], setbacks[arm]);
                var clear = CrosswalkIn + CrosswalkLength + CrosswalkClearance;
                line = line.Where(p => Vector3.Dot(p - end.Position, -end.Forward) > clear);
            }

            var kept = Subdivided(line.ToList(), LineStep);
            if (kept.Count >= 2)
                Polyline(white, AlongSurface(kept), RoadMeshBuilder.LineWidth, LineLift, Surface);
        }

        // Crosswalks across each road with a sidewalk, inside its cut end past its stop line.
        for (var i = 0; i < arms.Count; i++)
        {
            if (Crossed(i))
                Crosswalk(white, centre, arms[i], setbacks[i], AlongSurface, Surface);
        }

        // Stop lines across each arm's incoming lanes, just inside its cut end.
        for (var i = 0; i < arms.Count; i++)
        {
            var incoming = arms[i].OutwardBands().Where(b => b.Incoming).Select(b => b.Band).ToList();
            if (incoming.Count == 0)
                continue;
            var end = EndOf(centre, arms[i], setbacks[i]);
            var at = end with { Position = end.Position - end.Forward * (StopLineWidth / 2f) };
            var from = at.Across(incoming.Min(b => b.Left));
            var to = at.Across(incoming.Max(b => b.Right));
            Polyline(white, AlongSurface(new[] { from, to }), StopLineWidth, LineLift, Surface);
        }

        // Banks round each corner, where the ground is open between two roads.
        var edges = new List<IReadOnlyList<Vector3>>();
        for (var k = 0; k < order.Count && order.Count > 1; k++)
        {
            var (a, b) = (order[k], order[(k + 1) % order.Count]);
            edges.Add(CornerCurve(centre, arms[a], setbacks[a], arms[b], setbacks[b], useLanes: false));
        }

        // Sidewalks round the corners, the banks starting past them.
        var sidewalks = new MeshData();
        var outer = new List<IReadOnlyList<Vector3>>();
        for (var k = 0; k < edges.Count; k++)
        {
            var (a, b) = (order[k], order[(k + 1) % order.Count]);
            var (endA, endB) = (EndOf(centre, arms[a], setbacks[a]), EndOf(centre, arms[b], setbacks[b]));
            outer.Add(CornerSidewalk(sidewalks, edges[k], arms[a].Sidewalks().Right, arms[b].Sidewalks().Left, endA.Right, -endB.Right, sidewalkDepth) ?? edges[k]);
        }

        var bankWidth = RoadCrossSection.Bank(bankHeight, bankAngle);
        if (bankWidth > 0.01f)
        {
            // None round a corner with a sidewalk: the ground meets it there.
            for (var k = 0; k < outer.Count; k++)
            {
                if (ReferenceEquals(outer[k], edges[k]))
                    Bank(banks, outer[k], bankHeight, bankWidth);
            }
        }

        // The outline taking in the sidewalks: each arm's cut end across them, then round the corners past them.
        var outerOutline = new List<Vector3>();
        for (var k = 0; k < order.Count; k++)
        {
            var arm = arms[order[k]];
            var (left, right) = arm.Edges();
            var (walkLeft, walkRight) = arm.Sidewalks();
            var end = EndOf(centre, arm, setbacks[order[k]]);
            outerOutline.Add(end.Across(left - walkLeft));
            outerOutline.Add(end.Across(right + walkRight));
            if (k < outer.Count)
                outerOutline.AddRange(outer[k].Skip(1).SkipLast(1));
        }

        connections.AddRange(Connections(centre, arms, setbacks).Select(c => c with { Points = OnSurface(c.Points) }));
        return new JunctionShape(setbacks, outline, surface, white, connections, banks, outer,
            arms.Select((arm, i) => EndOf(centre, arm, setbacks[i])).ToList(), null, sidewalks, outerOutline);
    }

    /// <summary>How many points round a cul-de-sac's circle, all the way round.</summary>
    private const int CirclePoints = 48;

    /// <summary>
    /// A cul-de-sac: one road ending in a round turning space, centred on the junction, its radius
    /// the turning radius (or, with none, the road's half width and the corner radius more), flat
    /// at the height its road comes in at. The road is cut where its edges meet the
    /// circle; its edge line, bank and outline run round the far side from its right edge to its
    /// left; traffic comes in and turns round it (keeping right, so round it anticlockwise, seen
    /// from above) back out.
    /// </summary>
    private static JunctionShape CulDeSac(Vector3 centre, JunctionArm arm, float cornerRadius, float bankHeight, float bankAngle, float turningRadius,
        float sidewalkDepth)
    {
        var (left, right) = arm.Edges();
        var half = MathF.Max(-left, right);
        // Its turning radius if given (never narrower than the road), else the road's half width and the corner radius.
        var radius = turningRadius > 0f ? MathF.Max(turningRadius, half + 1f) : half + MathF.Max(cornerRadius, 1f);
        var setback = MathF.Max(MathF.Sqrt(radius * radius - half * half), MinimumSetback);
        var end = EndOf(centre, arm, setback);
        // Flat at the height its road comes in at, all the way round: the road runs straight in.
        centre = centre with { Y = end.Position.Y };

        // Round the circle from a point at the cut out to another, the long way, away from the road
        // (increasing angle round from the right edge is clockwise on a map, away from the road).
        IReadOnlyList<Vector3> Arc(Vector3 from, Vector3 to, bool clockwise)
        {
            var (a0, a1) = (Angle(from - centre), Angle(to - centre));
            if (clockwise && a1 <= a0)
                a1 += 2f * MathF.PI;
            if (!clockwise && a1 >= a0)
                a1 -= 2f * MathF.PI;
            var (r0, r1) = (Flat(from - centre).Length(), Flat(to - centre).Length());
            var steps = Math.Max(4, (int)MathF.Ceiling(MathF.Abs(a1 - a0) / (2f * MathF.PI) * CirclePoints));
            var points = new List<Vector3>();
            for (var i = 0; i <= steps; i++)
            {
                var t = i / (float)steps;
                var angle = a0 + (a1 - a0) * t;
                var r = r0 + (r1 - r0) * t;
                points.Add(new Vector3(centre.X + MathF.Cos(angle) * r, from.Y + (to.Y - from.Y) * t, centre.Z + MathF.Sin(angle) * r));
            }

            return points;
        }

        // Its outline: the road's cut end, left edge then right, then round the circle back.
        var edge = Arc(end.Across(right), end.Across(left), clockwise: true).Select(p => p with { Y = centre.Y }).ToList();
        edge[0] = end.Across(right);
        edge[^1] = end.Across(left);
        var outline = new List<Vector3> { end.Across(left), end.Across(right) };
        outline.AddRange(edge.Skip(1).SkipLast(1));
        var surface = new MeshData();
        Fan(surface, centre, outline);

        float? Surface(Vector3 p) => RoadJunctionShaper.FanHeight(centre, outline, new Vector2(p.X, p.Z));
        IReadOnlyList<Vector3> OnSurface(IEnumerable<Vector3> points) => points.Select(p => p with { Y = Surface(p) ?? p.Y }).ToList();

        // Its edge line round the circle, from the right lane edge to the left.
        var white = new MeshData();
        var (laneLeft, laneRight) = arm.LaneEdges();
        var line = Arc(end.Across(laneRight), end.Across(laneLeft), clockwise: true);
        Polyline(white, OnSurface(Subdivided(line, LineStep)), RoadMeshBuilder.LineWidth, LineLift, Surface);

        // Its sidewalk round the circle, from its road's right side to its left, and its bank past it.
        var sidewalks = new MeshData();
        var (walkLeft, walkRight) = arm.Sidewalks();
        var outerEdge = CornerSidewalk(sidewalks, edge, walkRight, walkLeft, end.Right, -end.Right, sidewalkDepth) ?? edge;
        var banks = new MeshData();
        var bankWidth = RoadCrossSection.Bank(bankHeight, bankAngle);
        if (bankWidth > 0.01f)
            if (ReferenceEquals(outerEdge, edge))
                Bank(banks, outerEdge, bankHeight, bankWidth);
        var outerOutline = new List<Vector3> { end.Across(left - walkLeft), end.Across(right + walkRight) };
        outerOutline.AddRange(outerEdge.Skip(1).SkipLast(1));

        // Turning round: from each incoming lane round the far side to the outgoing lane opposite it.
        var connections = new List<LaneConnection>();
        var lanes = arm.OutwardBands().Where(b => b.Band.Kind == BandKind.Lane).ToList();
        var incoming = lanes.Where(b => b.Incoming).Select(b => b.Band).OrderBy(b => -b.Middle).ToList();
        var outgoing = lanes.Where(b => !b.Incoming).Select(b => b.Band).OrderBy(b => b.Middle).ToList();
        for (var i = 0; i < incoming.Count && outgoing.Count > 0; i++)
        {
            var o = Math.Min(i, outgoing.Count - 1);
            var path = Arc(end.Across(incoming[i].Middle), end.Across(outgoing[o].Middle), clockwise: false);
            connections.Add(new LaneConnection(0, i, 0, o, OnSurface(path)));
        }

        return new JunctionShape(new[] { setback }, outline, surface, white, connections, banks, new[] { outerEdge }, new[] { end }, centre, sidewalks, outerOutline);
    }

    private readonly record struct Corner(float AlongA, float AlongB, Vector3 Point);

    /// <summary>
    /// Where arm a's edge facing arm b meets arm b's edge facing a, as how far out along each it
    /// is; none if the edges run parallel (two roads meeting in a straight line). Going round
    /// clockwise (on a map, north up) from a to b, a's right edge faces b's left.
    /// </summary>
    private static Corner? Meet(Vector3 centre, JunctionArm a, JunctionArm b, bool useLanes, float pastA = 0f, float pastB = 0f)
    {
        // So far past those edges, if asked: a's to its right, b's to its left.
        var (_, aRight) = useLanes ? a.LaneEdges() : a.Edges();
        var (bLeft, _) = useLanes ? b.LaneEdges() : b.Edges();
        (aRight, bLeft) = (aRight + pastA, bLeft - pastB);
        var pa = Flat(centre + a.Right * aRight);
        var pb = Flat(centre + b.Right * bLeft);
        var da = Flat(a.Direction);
        var db = Flat(b.Direction);
        var cross = da.X * db.Y - da.Y * db.X;
        if (MathF.Abs(cross) < 1e-4f)
            return null;
        var d = pb - pa;
        var t = (d.X * db.Y - d.Y * db.X) / cross;
        var s = (d.X * da.Y - d.Y * da.X) / cross;
        if (t < 0f || s < 0f)
            return null; // the edges meet behind the centre: the arms splay apart
        var point = new Vector3(pa.X + da.X * t, centre.Y, pa.Y + da.Y * t);
        return new Corner(t, s, point);
    }

    /// <summary>
    /// The rounded corner from arm a's right edge at its cut end to arm b's left edge at its cut
    /// end: a curve pulled towards where the edges would meet, or straight if they never do.
    /// With useLanes, the same between the edges of their outer lanes, for the edge lines.
    /// </summary>
    private static IReadOnlyList<Vector3> CornerCurve(Vector3 centre, JunctionArm a, float setbackA, JunctionArm b, float setbackB, bool useLanes)
    {
        var (_, aRight) = useLanes ? a.LaneEdges() : a.Edges();
        var (bLeft, _) = useLanes ? b.LaneEdges() : b.Edges();
        var endA = EndOf(centre, a, setbackA);
        var endB = EndOf(centre, b, setbackB);
        var start = endA.Across(aRight);
        var end = endB.Across(bLeft);
        // Pulled towards where the two edges, carried on in from the ends, would meet.
        var control = Crossing(start, -endA.Forward, end, -endB.Forward) ?? (start + end) / 2f;
        var points = new List<Vector3>();
        for (var i = 0; i <= CurvePoints; i++)
        {
            var t = i / (float)CurvePoints;
            var u = 1f - t;
            var p = u * u * start + 2f * u * t * control + t * t * end;
            // From one road's height to the other's.
            points.Add(p with { Y = start.Y + (end.Y - start.Y) * t });
        }

        return points;
    }

    /// <summary>The paths from each incoming lane to the lanes it may turn into.</summary>
    private static IEnumerable<LaneConnection> Connections(Vector3 centre, IReadOnlyList<JunctionArm> arms, float[] setbacks)
    {
        List<RoadBand> Lanes(int arm, bool incoming) => arms[arm].OutwardBands()
            .Where(b => b.Band.Kind == BandKind.Lane && b.Incoming == incoming)
            // Inside out: incoming lanes lie left of the centre looking out, so from the right.
            .OrderBy(b => incoming ? -b.Band.Middle : b.Band.Middle)
            .Select(b => b.Band).ToList();

        for (var from = 0; from < arms.Count; from++)
        {
            var inLanes = Lanes(from, true);
            if (inLanes.Count == 0)
                continue;
            var fromEnd = EndOf(centre, arms[from], setbacks[from]);
            var heading = -fromEnd.Forward; // travelling into the junction
            for (var to = 0; to < arms.Count; to++)
            {
                if (to == from)
                    continue;
                var outLanes = Lanes(to, false);
                if (outLanes.Count == 0)
                    continue;

                var toEnd = EndOf(centre, arms[to], setbacks[to]);
                var leaving = toEnd.Forward;
                var turn = Flat(heading).X * Flat(leaving).Y - Flat(heading).Y * Flat(leaving).X;
                var straight = Vector3.Dot(Flat3(heading), Flat3(leaving)) > 0.7f;
                IEnumerable<(int In, int Out)> pairs = straight
                    ? Enumerable.Range(0, Math.Min(inLanes.Count, outLanes.Count)).Select(i => (i, i))
                    // Turning towards its left crosses the other traffic: from the inside lane to the
                    // inside lane. Towards its right: outside lane to outside lane.
                    : IsLeftTurn(turn) ? new[] { (0, 0) } : new[] { (inLanes.Count - 1, outLanes.Count - 1) };

                foreach (var (i, o) in pairs)
                {
                    var start = fromEnd.Across(inLanes[i].Middle);
                    var end = toEnd.Across(outLanes[o].Middle);
                    var reach = Vector3.Distance(start, end) / 2f;
                    var points = new List<Vector3>();
                    for (var n = 0; n <= CurvePoints; n++)
                    {
                        var t = n / (float)CurvePoints;
                        var u = 1f - t;
                        var p = u * u * u * start + 3f * u * u * t * (start + heading * reach) + 3f * u * t * t * (end - leaving * reach) + t * t * t * end;
                        points.Add(p with { Y = start.Y + (end.Y - start.Y) * t });
                    }

                    yield return new LaneConnection(from, i, to, o, points);
                }
            }
        }
    }

    /// <summary>
    /// Whether a turn, as the cross product of the way in and the way out seen from above
    /// (x, z), goes to the traveller's left. Facing -z with +x on the right, a left turn swings
    /// towards -x: the cross is negative.
    /// </summary>
    private static bool IsLeftTurn(float cross) => cross < 0f;

    /// <summary>A triangle fan round the centre out to the outline (each point at its own height), going round clockwise on a map, north up.</summary>
    private static void Fan(MeshData mesh, Vector3 centre, IReadOnlyList<Vector3> outline)
    {
        var middle = mesh.Add(centre, Vector3.UnitY, new Vector2(centre.X, centre.Z) / RoadStructureBuilder.TextureSize);
        var first = mesh.Vertices.Count;
        foreach (var p in outline)
            mesh.Add(p, Vector3.UnitY, new Vector2(p.X, p.Z) / RoadStructureBuilder.TextureSize);
        for (var i = 0; i < outline.Count; i++)
        {
            var a = first + i;
            var b = first + (i + 1) % outline.Count;
            // Facing up, whichever way round the outline goes.
            mesh.Triangle(middle, a, b);
        }
    }

    /// <summary>
    /// A bank from the edge of the surface along a corner (running clockwise round the junction,
    /// so away from it is on the left), sloping out width wide and down height, facing up and out;
    /// with a skirt down from its far edge into the ground.
    /// </summary>
    private static void Bank(MeshData mesh, IReadOnlyList<Vector3> edge, float height, float width)
    {
        if (edge.Count < 2)
            return;
        var slope = MathF.Sqrt(width * width + height * height);
        var first = mesh.Vertices.Count;
        var bottoms = new List<(Vector3 At, Vector3 Outward)>();
        var along = 0f;
        for (var i = 0; i < edge.Count; i++)
        {
            if (i > 0)
                along += Vector3.Distance(edge[i - 1], edge[i]);
            var way = Flat3(edge[Math.Min(i + 1, edge.Count - 1)] - edge[Math.Max(i - 1, 0)]);
            var outward = -Vector3.Normalize(Vector3.Cross(way, Vector3.UnitY));
            var normal = Vector3.Normalize(Vector3.UnitY * width + outward * height);
            var bottom = edge[i] + outward * width - Vector3.UnitY * height;
            var v = along / RoadStructureBuilder.TextureSize;
            // Left (outward) then right, as a strip's pairs go, to wind it facing up.
            mesh.Add(bottom, normal, new Vector2(slope / RoadStructureBuilder.TextureSize, v));
            mesh.Add(edge[i], normal, new Vector2(0f, v));
            bottoms.Add((bottom, outward));
        }

        for (var i = 0; i + 1 < edge.Count; i++)
        {
            var a = first + 2 * i;
            mesh.Quad(a, a + 2, a + 3, a + 1);
        }

        // Its skirt, facing out: lower then upper, as a strip's pairs go on a left edge.
        first = mesh.Vertices.Count;
        foreach (var (at, outward) in bottoms)
        {
            mesh.Add(at - Vector3.UnitY * RoadMeshBuilder.SkirtDepth, outward, Vector2.Zero);
            mesh.Add(at, outward, Vector2.Zero);
        }

        for (var i = 0; i + 1 < bottoms.Count; i++)
        {
            var a = first + 2 * i;
            mesh.Quad(a, a + 2, a + 3, a + 1);
        }

        // Its ends closed, where the roads come in: from the slope straight down below its foot,
        // facing along the corner, out of it.
        foreach (var (i, back) in new[] { (0, true), (edge.Count - 1, false) })
        {
            var way = Flat3(back ? edge[1] - edge[0] : edge[^1] - edge[^2]);
            var top = edge[i];
            var bottom = bottoms[i].At;
            var low = MathF.Min(top.Y, bottom.Y) - RoadMeshBuilder.SkirtDepth;
            var normal = back ? -way : way;
            var (a, b, c, d) = (top, bottom, bottom with { Y = low }, top with { Y = low });
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) > 0f)
                (b, d) = (d, b);
            var at = mesh.Vertices.Count;
            foreach (var p in new[] { a, b, c, d })
                mesh.Add(p, normal, new Vector2(p.X + p.Z, p.Y) / RoadStructureBuilder.TextureSize);
            mesh.Quad(at, at + 1, at + 2, at + 3);
        }
    }

    /// <summary>How far in from a road's cut end its crosswalk starts, and how long it is along the road.</summary>
    private const float CrosswalkIn = 0.8f;
    private const float CrosswalkLength = 3f;

    /// <summary>How wide a crosswalk's stripes are, and the gaps between them.</summary>
    private const float CrosswalkStripe = 0.5f;

    /// <summary>How far clear of a crosswalk the edge lines round a junction's corners stop.</summary>
    private const float CrosswalkClearance = 0.5f;

    /// <summary>
    /// A crosswalk across an arm's road, from edge to edge, just inside its cut end (past its stop
    /// line, as traffic comes in): stripes running along the road. None where the arm is cut too
    /// close to the centre for one.
    /// </summary>
    private static void Crosswalk(MeshData mesh, Vector3 centre, JunctionArm arm, float setback,
        Func<IReadOnlyList<Vector3>, IReadOnlyList<Vector3>> alongSurface, Func<Vector3, float?> surface)
    {
        var (near, far) = (setback - CrosswalkIn, setback - CrosswalkIn - CrosswalkLength);
        if (far < 0.5f)
            return;
        // Between the edge lines, clear of them.
        var (laneLeft, laneRight) = arm.LaneEdges();
        var (left, right) = (laneLeft + RoadMeshBuilder.LineWidth, laneRight - RoadMeshBuilder.LineWidth);
        var (from, to) = (EndOf(centre, arm, near), EndOf(centre, arm, far));
        for (var x = left + CrosswalkStripe / 2f; x + CrosswalkStripe / 2f <= right; x += CrosswalkStripe * 2f)
            Polyline(mesh, alongSurface(new[] { from.Across(x), to.Across(x) }), CrosswalkStripe, LineLift, surface);
    }

    /// <summary>
    /// A sidewalk along the edge of a corner (running clockwise round the junction, so away from it
    /// is on the left), from the sidewalk of the road it starts at (fromWidth wide) to that of the
    /// road it ends at (toWidth): all round it, widening or narrowing between them where both have
    /// one; where only one has, narrowing from its width to nothing halfway round. Returns its
    /// far edge, where the ground and bank start; none where neither road has a sidewalk there. Its
    /// ends run straight out the ways given: across each road, to meet its own sidewalk's end.
    /// </summary>
    private static IReadOnlyList<Vector3>? CornerSidewalk(MeshData mesh, IReadOnlyList<Vector3> edge, float fromWidth, float toWidth, Vector3 awayAtStart, Vector3 awayAtEnd,
        float deepBy)
    {
        if (edge.Count < 2 || (fromWidth <= 0f && toWidth <= 0f))
            return null;
        // Between two sidewalks, from the one's width to the other's; from one only, narrowing to
        // nothing halfway round the corner, the corner's own edge from there to the road without one.
        var both = fromWidth > 0f && toWidth > 0f;
        var (first, last) = (0, edge.Count - 1);

        // How far along the corner each point is, for the width between the two roads'.
        var along = new float[edge.Count];
        for (var i = 1; i < edge.Count; i++)
            along[i] = along[i - 1] + Vector3.Distance(edge[i - 1], edge[i]);
        var length = MathF.Max(along[^1], 1e-3f);

        // From one only: its far edge runs straight from the road's own sidewalk's to the corner's
        // edge halfway round, where it ends.
        var half = length / 2f;
        Vector3 Halfway()
        {
            for (var i = 1; i < edge.Count; i++)
                if (along[i] >= half)
                    return Vector3.Lerp(edge[i - 1], edge[i], (half - along[i - 1]) / MathF.Max(along[i] - along[i - 1], 1e-6f));
            return edge[^1];
        }

        var middle = Halfway();
        var start = fromWidth > 0f ? edge[0] + Flat3(awayAtStart) * fromWidth : edge[^1] + Flat3(awayAtEnd) * toWidth;

        var inner = new List<Vector3>();
        var outerEdge = new List<Vector3>();
        var outward = new List<Vector3>();
        for (var i = first; i <= last; i++)
        {
            var way = Flat3(edge[Math.Min(i + 1, edge.Count - 1)] - edge[Math.Max(i - 1, 0)]);
            // Square to each road where it meets it, so it meets the road's own sidewalk edge to edge.
            var away = i == 0 ? Flat3(awayAtStart) : i == edge.Count - 1 ? Flat3(awayAtEnd) : -Vector3.Normalize(Vector3.Cross(way, Vector3.UnitY));
            var fromSidewalk = fromWidth > 0f ? along[i] : length - along[i];
            inner.Add(edge[i]);
            if (both)
                outerEdge.Add(edge[i] + away * (fromWidth + (toWidth - fromWidth) * along[i] / length));
            else
                outerEdge.Add(fromSidewalk < half ? Vector3.Lerp(start, middle, fromSidewalk / half) with { Y = edge[i].Y } : edge[i]);
            outward.Add(away);
        }

        var up = Vector3.UnitY * RoadCrossSection.CurbHeight;
        var down = Vector3.UnitY * RoadMeshBuilder.SkirtDepth;
        // Its far side and closed end a long way into the ground, so ground lowered beside it never shows under it.
        var deep = Vector3.UnitY * deepBy;
        for (var i = 0; i + 1 < inner.Count; i++)
        {
            // Past where it has narrowed to nothing, none.
            if (Vector3.DistanceSquared(inner[i], outerEdge[i]) < 1e-6f && Vector3.DistanceSquared(inner[i + 1], outerEdge[i + 1]) < 1e-6f)
                continue;
            var away = Vector3.Normalize(outward[i] + outward[i + 1]);
            Face(mesh, inner[i] + up, inner[i + 1] + up, outerEdge[i + 1] + up, outerEdge[i] + up, Vector3.UnitY);
            Face(mesh, inner[i] + up, inner[i + 1] + up, inner[i + 1] - down, inner[i] - down, -away);
            Face(mesh, outerEdge[i] + up, outerEdge[i + 1] + up, outerEdge[i + 1] - deep, outerEdge[i] - deep, away);
        }

        return outerEdge;
    }

    /// <summary>A flat four-cornered face, wound to face the way it is told, whichever way round its corners come.</summary>
    private static void Face(MeshData mesh, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) > 0f)
            (b, d) = (d, b);
        var first = mesh.Vertices.Count;
        foreach (var p in new[] { a, b, c, d })
            mesh.Add(p, normal, new Vector2(p.X + p.Y, p.Z + p.Y) / RoadStructureBuilder.TextureSize);
        mesh.Quad(first, first + 1, first + 2, first + 3);
    }

    /// <summary>A line with more points put in along it, so none are further apart than step.</summary>
    private static IReadOnlyList<Vector3> Subdivided(IReadOnlyList<Vector3> line, float step)
    {
        var points = new List<Vector3>();
        for (var i = 0; i + 1 < line.Count; i++)
        {
            var pieces = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(line[i], line[i + 1]) / step));
            for (var k = 0; k < pieces; k++)
                points.Add(Vector3.Lerp(line[i], line[i + 1], k / (float)pieces));
        }

        if (line.Count > 0)
            points.Add(line[^1]);
        return points;
    }

    /// <summary>A strip width wide along a line, lifted height above it.</summary>
    /// <remarks>With a surface, each edge of the strip sits that far above it where it is, rather than above the line.</remarks>
    private static void Polyline(MeshData mesh, IReadOnlyList<Vector3> line, float width, float height, Func<Vector3, float?>? surface = null)
    {
        if (line.Count < 2)
            return;
        var first = mesh.Vertices.Count;
        for (var i = 0; i < line.Count; i++)
        {
            var from = line[Math.Max(i - 1, 0)];
            var to = line[Math.Min(i + 1, line.Count - 1)];
            var way = Flat3(to - from);
            var right = Vector3.Normalize(Vector3.Cross(way, Vector3.UnitY)) * width / 2f;
            foreach (var edge in new[] { line[i] - right, line[i] + right })
                mesh.Add(edge with { Y = (surface?.Invoke(edge) ?? edge.Y) + height }, Vector3.UnitY, Vector2.Zero);
        }

        for (var i = 0; i + 1 < line.Count; i++)
        {
            var a = first + 2 * i;
            var b = a + 2;
            mesh.Quad(a, b, b + 1, a + 1);
        }
    }

    /// <summary>
    /// Where an arm's road is cut off, setback out from the junction: its centre there (at the
    /// road's own height), facing out, with right across it looking out. Where it really is, if the
    /// arm knows; straight out from the centre if not.
    /// </summary>
    private static RoadFrame EndOf(Vector3 centre, JunctionArm arm, float setback)
    {
        if (arm.Along is not { } along)
            return new RoadFrame(centre + arm.Direction * setback, arm.Direction, arm.Right, setback);
        var frame = along(setback);
        var outward = Flat3(frame.Forward);
        return new RoadFrame(frame.Position, outward, Vector3.Normalize(Vector3.Cross(outward, Vector3.UnitY)), setback);
    }

    /// <summary>Where two lines on the level, each from a point going a way, cross ahead of both; none if they do not.</summary>
    private static Vector3? Crossing(Vector3 a, Vector3 wayA, Vector3 b, Vector3 wayB)
    {
        var (pa, da, pb, db) = (Flat(a), Flat(wayA), Flat(b), Flat(wayB));
        var cross = da.X * db.Y - da.Y * db.X;
        if (MathF.Abs(cross) < 1e-4f)
            return null;
        var d = pb - pa;
        var t = (d.X * db.Y - d.Y * db.X) / cross;
        var s = (d.X * da.Y - d.Y * da.X) / cross;
        if (t < 0f || s < 0f)
            return null;
        return new Vector3(pa.X + da.X * t, a.Y, pa.Y + da.Y * t);
    }

    /// <summary>Its angle round the vertical, for putting arms in order: clockwise on a map, north (-z) up.</summary>
    private static float Angle(Vector3 direction) => MathF.Atan2(direction.Z, direction.X);

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    private static Vector3 Flat3(Vector3 v)
    {
        var flat = new Vector3(v.X, 0f, v.Z);
        return flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : Vector3.UnitZ;
    }
}
