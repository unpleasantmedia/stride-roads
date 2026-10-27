using System;
using System.Collections.Generic;
using System.Numerics;

namespace Stride.Roads;

/// <summary>
/// A point a road runs through, in world space (y up), and how it runs through it. With no
/// <see cref="Tangent"/> the road runs smoothly on through it towards its neighbours; with one, it
/// runs that way. <see cref="Handle"/> is how far the curve holds that way either side, as a share
/// of the distance to each neighbour (a third makes an even curve).
/// </summary>
public readonly record struct CurvePoint(Vector3 Position, Vector3? Tangent = null, float Handle = 1f / 3f);

/// <summary>
/// Where a road is, a sample along it: its centre, which way it runs, and which way is right
/// across it (always level: roads do not tilt sideways), at <see cref="Distance"/> metres from
/// its start.
/// </summary>
public readonly record struct RoadFrame(Vector3 Position, Vector3 Forward, Vector3 Right, float Distance)
{
    /// <summary>The point this far right of the centre line, on the road's surface.</summary>
    public Vector3 Across(float offset) => Position + Right * offset;
}

/// <summary>
/// A road's centre line: cubic Bézier curves from each point to the next, each point's tangent
/// shared by the curves either side so the line runs smoothly through it. A closed road runs from
/// its last point back to its first.
/// </summary>
public sealed class RoadCurve
{
    private readonly IReadOnlyList<CurvePoint> _points;
    private readonly bool _closed;

    public RoadCurve(IReadOnlyList<CurvePoint> points, bool closed = false)
    {
        if (points.Count < 2)
            throw new ArgumentException("A road runs through at least two points.", nameof(points));
        _points = points;
        _closed = closed && points.Count > 2;
    }

    public bool Closed => _closed;

    public int Spans => _closed ? _points.Count : _points.Count - 1;

    /// <summary>The four control points of the curve from point span to the next.</summary>
    public (Vector3 A, Vector3 B, Vector3 C, Vector3 D) Span(int span)
    {
        var from = _points[span];
        var to = _points[(span + 1) % _points.Count];
        var reach = Vector3.Distance(from.Position, to.Position);
        return (from.Position,
            from.Position + TangentAt(span) * reach * from.Handle,
            to.Position - TangentAt((span + 1) % _points.Count) * reach * to.Handle,
            to.Position);
    }

    /// <summary>
    /// The way the road runs through a point: its own if it has one, else along the line from the
    /// point before it to the point after. The start and end of an open road have only one
    /// neighbour: there the road bends as it does through that neighbour, mirrored about the line
    /// to it, as a smooth curve would.
    /// </summary>
    public Vector3 TangentAt(int index)
    {
        if (_points[index].Tangent is { } set && set.LengthSquared() > 1e-8f)
            return Vector3.Normalize(set);

        var n = _points.Count;
        if (!_closed && (index == 0 || index == n - 1))
        {
            var here = _points[index].Position;
            var next = index == 0 ? 1 : n - 2;
            var chord = _points[next].Position - here;
            if (chord.LengthSquared() < 1e-8f)
                return Vector3.UnitZ;
            chord = Vector3.Normalize(chord);
            if (n == 2)
                return index == 0 ? chord : -chord;
            // Through the neighbour the road runs along its tangent; at this end it runs the same
            // angle the other side of the chord.
            var through = index == 0 ? TangentAt(next) : -TangentAt(next);
            var end = 2f * Vector3.Dot(through, chord) * chord - through;
            end = end.LengthSquared() > 1e-8f ? Vector3.Normalize(end) : chord;
            return index == 0 ? end : -end;
        }

        var before = _points[(index - 1 + n) % n].Position;
        var after = _points[(index + 1) % n].Position;
        var way = after - before;
        return way.LengthSquared() > 1e-8f ? Vector3.Normalize(way) : Vector3.UnitZ;
    }

    public static Vector3 Bezier((Vector3 A, Vector3 B, Vector3 C, Vector3 D) span, float t)
    {
        var u = 1f - t;
        return u * u * u * span.A + 3f * u * u * t * span.B + 3f * u * t * t * span.C + t * t * t * span.D;
    }

    /// <summary>
    /// Samples all along the road, about every spacing metres, evenly by distance (not by curve
    /// parameter, which bunches samples on tight bends): the first at its start, the last at its
    /// end (or, closed, just short of its start again).
    /// </summary>
    public IReadOnlyList<RoadFrame> Frames(float spacing = 1f) => Frames(spacing, out _);

    /// <summary>
    /// As <see cref="Frames(float)"/>, and how far along it each of its points is (a closed road's
    /// last entry its whole length, back round at its first point).
    /// </summary>
    public IReadOnlyList<RoadFrame> Frames(float spacing, out float[] pointsAt)
    {
        // A fine polyline first, then walked at even distances.
        var line = new List<Vector3>();
        const int Steps = 64;
        for (var span = 0; span < Spans; span++)
        {
            var curve = Span(span);
            for (var step = 0; step < Steps; step++)
                line.Add(Bezier(curve, step / (float)Steps));
        }
        line.Add(_closed ? line[0] : _points[^1].Position);

        var along = new float[line.Count];
        for (var i = 1; i < line.Count; i++)
            along[i] = along[i - 1] + Vector3.Distance(line[i - 1], line[i]);
        var length = along[^1];
        pointsAt = new float[Spans + 1];
        for (var span = 0; span <= Spans; span++)
            pointsAt[span] = along[Math.Min(span * Steps, along.Length - 1)];

        var count = Math.Max(1, (int)MathF.Round(length / spacing));
        var frames = new List<RoadFrame>(count + 1);
        var segment = 0;
        for (var k = 0; k <= count; k++)
        {
            if (_closed && k == count)
                break;
            var at = length * k / count;
            while (segment < line.Count - 2 && along[segment + 1] < at)
                segment++;
            var start = line[segment];
            var end = line[segment + 1];
            var piece = along[segment + 1] - along[segment];
            var t = piece > 1e-6f ? (at - along[segment]) / piece : 0f;
            var position = Vector3.Lerp(start, end, t);
            // An open road's ends run exactly the way it does through its first and last points,
            // not smoothed with the curve ahead, so it meets what it joins square on.
            var heading = !_closed && k == 0 ? TangentAt(0)
                : !_closed && k == count ? TangentAt(_points.Count - 1)
                : Heading(line, segment);
            frames.Add(Frame(position, heading, at));
        }

        return frames;
    }

    /// <summary>
    /// The part of an open road's frames from one distance along it to another, with frames made
    /// exactly at both ends (between the samples either side), so a road cut back for a junction
    /// ends right where the junction begins.
    /// </summary>
    public static IReadOnlyList<RoadFrame> Trim(IReadOnlyList<RoadFrame> frames, float from, float to)
    {
        if (frames.Count < 2 || to <= from)
            return Array.Empty<RoadFrame>();

        var kept = new List<RoadFrame> { At(frames, from) };
        foreach (var frame in frames)
        {
            if (frame.Distance > from + 1e-3f && frame.Distance < to - 1e-3f)
                kept.Add(frame);
        }
        kept.Add(At(frames, to));
        return kept;
    }

    /// <summary>The frame at a distance along the road, between the samples either side of it.</summary>
    public static RoadFrame At(IReadOnlyList<RoadFrame> frames, float distance)
    {
        distance = Math.Clamp(distance, frames[0].Distance, frames[^1].Distance);
        for (var i = 0; i + 1 < frames.Count; i++)
        {
            var (a, b) = (frames[i], frames[i + 1]);
            if (distance > b.Distance && i + 2 < frames.Count)
                continue;
            var span = b.Distance - a.Distance;
            var t = span > 1e-6f ? Math.Clamp((distance - a.Distance) / span, 0f, 1f) : 0f;
            return new RoadFrame(Vector3.Lerp(a.Position, b.Position, t), Vector3.Normalize(Vector3.Lerp(a.Forward, b.Forward, t)),
                Vector3.Normalize(Vector3.Lerp(a.Right, b.Right, t)), distance);
        }

        return frames[^1];
    }

    /// <summary>Which way the fine line runs at this piece of it, smoothed with its neighbours.</summary>
    private static Vector3 Heading(List<Vector3> line, int segment)
    {
        var from = line[Math.Max(segment - 1, 0)];
        var to = line[Math.Min(segment + 2, line.Count - 1)];
        var way = to - from;
        return way.LengthSquared() > 1e-10f ? Vector3.Normalize(way) : Vector3.UnitZ;
    }

    private static RoadFrame Frame(Vector3 position, Vector3 forward, float distance)
    {
        var level = new Vector3(forward.X, 0f, forward.Z);
        level = level.LengthSquared() > 1e-8f ? Vector3.Normalize(level) : Vector3.UnitZ;
        // Right of the way the road runs, seen from above, y up: Stride is right-handed, so forward crossed with up.
        var right = Vector3.Normalize(Vector3.Cross(level, Vector3.UnitY));
        return new RoadFrame(position, forward, right, distance);
    }
}
