using System;
using System.Collections.Generic;

namespace Stride.Roads;

/// <summary>Which way traffic in a lane goes, along the road from its start to its end.</summary>
public enum LaneDirection
{
    /// <summary>From the road's start towards its end.</summary>
    Forward,

    /// <summary>From the road's end back towards its start.</summary>
    Backward,
}

/// <summary>What fills the gap between a road's two directions of traffic.</summary>
public enum MedianKind
{
    /// <summary>Road surface, with the lines either side of it.</summary>
    Paved,

    /// <summary>No road surface: the ground shows through, as on a divided highway.</summary>
    Grass,
}

/// <summary>What one strip across a road is.</summary>
public enum BandKind
{
    Shoulder,
    Lane,
    Median,

    /// <summary>A raised walkway past a shoulder, behind a curb.</summary>
    Sidewalk,
}

/// <summary>What a line painted along a road looks like.</summary>
public enum MarkingKind
{
    /// <summary>A solid white line, along the outer edge of the outer lanes.</summary>
    EdgeLine,

    /// <summary>A dashed white line, between lanes going the same way.</summary>
    LaneLine,

    /// <summary>Two solid yellow lines, between lanes going opposite ways with nothing between them.</summary>
    CentreLine,

    /// <summary>A solid yellow line, along the inner edge of the lanes beside a median.</summary>
    MedianLine,
}

/// <summary>
/// One strip across a road, from <see cref="Left"/> to <see cref="Right"/> metres to the right of
/// its centre line, looking along the road from its start. A lane's <see cref="Direction"/> is the
/// way its traffic goes; other bands have none.
/// </summary>
public readonly record struct RoadBand(BandKind Kind, float Left, float Right, LaneDirection? Direction = null)
{
    public float Width => Right - Left;
    public float Middle => (Left + Right) / 2f;
}

/// <summary>A line painted along a road, centred <see cref="Offset"/> metres right of its centre line.</summary>
public readonly record struct RoadMarking(MarkingKind Kind, float Offset);

/// <summary>
/// What a road looks like across: its lanes each way, their width, the shoulders either side, and
/// a median between the two directions. Traffic keeps right, so the lanes going backwards (towards
/// the road's start) are on its left, and those going forwards on its right. A road one way only
/// is centred on its lanes; one both ways is centred on the middle of its median.
/// <para>
/// Past each shoulder the ground can slope away in a bank: down by <see cref="ShoulderHeight"/>
/// metres (up, if that is negative, as out of a cutting) at <see cref="ShoulderAngle"/> degrees.
/// The bank is the terrain's, shaped by fitting it to the road; it is not part of the road.
/// </para>
/// <para>
/// A side may have a sidewalk instead of its shoulder, <see cref="SidewalkLeft"/> or <see cref="SidewalkRight"/>
/// metres wide (none at 0) from the edge of the outer lane, <see cref="CurbHeight"/> above the road behind
/// its curb there; the bank then starts past it. The lanes lie where they would without it.
/// </para>
/// </summary>
public sealed record RoadCrossSection(
    int ForwardLanes = 1,
    int BackwardLanes = 1,
    float LaneWidth = 3.5f,
    float ShoulderWidth = 1f,
    float MedianWidth = 0f,
    MedianKind Median = MedianKind.Paved,
    float ShoulderHeight = 0f,
    float ShoulderAngle = RoadCrossSection.WalkableAngle,
    float SidewalkLeft = 0f,
    float SidewalkRight = 0f)
{
    /// <summary>How high a sidewalk stands above the road, at its curb.</summary>
    public const float CurbHeight = 0.15f;

    /// <summary>How wide a sidewalk usually is.</summary>
    public const float SidewalkWidth = 3.5f;

    /// <summary>The steepest ground the player can walk up, in degrees: as CharacterController stands on.</summary>
    public const float WalkableAngle = 45f;

    /// <summary>How far out the bank past each shoulder reaches: none with no height to it.</summary>
    public float BankWidth => Bank(ShoulderHeight, ShoulderAngle);

    /// <summary>How far out a bank this high (or deep) at this angle, in degrees, reaches.</summary>
    public static float Bank(float height, float angle) =>
        MathF.Abs(height) / MathF.Tan(Math.Clamp(angle, 1f, 89f) * MathF.PI / 180f);

    /// <summary>A two-lane road, one lane each way.</summary>
    public static RoadCrossSection TwoLane => new();

    /// <summary>A one-way road of this many lanes, as a highway carriageway or a ramp.</summary>
    public static RoadCrossSection OneWay(int lanes, float laneWidth = 3.5f) => new(lanes, 0, laneWidth);

    /// <summary>A divided highway: this many lanes each way, with a grass median between.</summary>
    public static RoadCrossSection Divided(int lanesEachWay, float medianWidth = 4f, float laneWidth = 4f) =>
        new(lanesEachWay, lanesEachWay, laneWidth, 1.5f, medianWidth, MedianKind.Grass);

    public int Lanes => ForwardLanes + BackwardLanes;

    /// <summary>The road's whole width, shoulder to shoulder: its sidewalks are past that.</summary>
    public float Width => Lanes * LaneWidth + 2 * ShoulderWidth + (HasMedian ? MedianWidth : 0f);

    private bool BothWays => ForwardLanes > 0 && BackwardLanes > 0;
    private bool HasMedian => BothWays && MedianWidth > 0f;

    /// <summary>The strips across the road, left to right.</summary>
    public IReadOnlyList<RoadBand> Bands()
    {
        if (Lanes == 0)
            throw new InvalidOperationException("A road needs at least one lane.");

        var bands = new List<RoadBand>();
        // Where the left shoulder starts: a road both ways is centred on its median, or on the line
        // between its directions; one way only, on its lanes.
        // Where the lanes start is where it would be without sidewalks; a sidewalk takes its side's shoulder's place.
        var lanesLeft = BothWays
            ? -(BackwardLanes * LaneWidth + (HasMedian ? MedianWidth / 2f : 0f))
            : -Width / 2f + ShoulderWidth;
        var left = lanesLeft - (SidewalkLeft > 0f ? SidewalkLeft : ShoulderWidth);

        void Add(BandKind kind, float width, LaneDirection? direction = null)
        {
            bands.Add(new RoadBand(kind, left, left + width, direction));
            left += width;
        }

        if (SidewalkLeft > 0f)
            Add(BandKind.Sidewalk, SidewalkLeft);
        else
            Add(BandKind.Shoulder, ShoulderWidth);
        for (var lane = 0; lane < BackwardLanes; lane++)
            Add(BandKind.Lane, LaneWidth, LaneDirection.Backward);
        if (HasMedian)
            Add(BandKind.Median, MedianWidth);
        for (var lane = 0; lane < ForwardLanes; lane++)
            Add(BandKind.Lane, LaneWidth, LaneDirection.Forward);
        if (SidewalkRight > 0f)
            Add(BandKind.Sidewalk, SidewalkRight);
        else
            Add(BandKind.Shoulder, ShoulderWidth);
        return bands;
    }

    /// <summary>The lines painted along the road: at each edge between bands where one belongs.</summary>
    public IReadOnlyList<RoadMarking> Markings()
    {
        var bands = Bands();
        var markings = new List<RoadMarking>();
        for (var i = 0; i + 1 < bands.Count; i++)
        {
            var (a, b) = (bands[i], bands[i + 1]);
            var at = a.Right;
            // A curb, not a line, between the road and a sidewalk.
            if (a.Kind == BandKind.Sidewalk || b.Kind == BandKind.Sidewalk)
                continue;
            if (a.Kind == BandKind.Shoulder || b.Kind == BandKind.Shoulder)
                markings.Add(new RoadMarking(MarkingKind.EdgeLine, at));
            else if (a.Kind == BandKind.Median || b.Kind == BandKind.Median)
                markings.Add(new RoadMarking(MarkingKind.MedianLine, at));
            else if (a.Direction == b.Direction)
                markings.Add(new RoadMarking(MarkingKind.LaneLine, at));
            else
                markings.Add(new RoadMarking(MarkingKind.CentreLine, at));
        }

        return markings;
    }
}
