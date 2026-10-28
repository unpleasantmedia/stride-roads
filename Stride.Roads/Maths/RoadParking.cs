using System;
using System.Collections.Generic;

namespace Stride.Roads;

/// <summary>How a road's parking bays are laid out along its side.</summary>
public enum ParkingKind
{
    /// <summary>A parking lane along the kerb, square at its ends: cars end to end, parallel to the road.</summary>
    Parallel,

    /// <summary>The same, set into the sidewalk, which bends out round it and back: tapered at its ends.</summary>
    LayBy,

    /// <summary>Bays at an angle to the road (see <see cref="RoadParking.Angle"/>), the kerb set back as deep as they reach.</summary>
    Angled,

    /// <summary>Bays square to the road, the kerb set back a car's length.</summary>
    Perpendicular,
}

/// <summary>
/// A road's parking, as the road maths needs it: its kind, side, where along the road it runs, how
/// big its bays are and (angled) at what angle; how far out its side of the road is set at each place.
/// </summary>
public readonly record struct ParkingSpec(ParkingKind Kind, RoadSide Side, float Start, float Length, float BayWidth, float BayLength, float Angle, float Offset = 0f)
{
    public float End => Start + Length;

    /// <summary>How far out from the road's edge its bays reach (and the kerb is set back).</summary>
    public float Depth => Kind switch
    {
        ParkingKind.Angled => BayLength * MathF.Sin(Radians) + BayWidth * MathF.Cos(Radians),
        ParkingKind.Perpendicular => BayLength,
        _ => BayWidth,
    };

    private float Radians => Math.Clamp(Angle, 10f, 90f) * MathF.PI / 180f;

    /// <summary>How long its ends take to come out from the road's edge to its full depth: a lay-by's taper, others nearly square.</summary>
    public float Taper => Kind == ParkingKind.LayBy ? MathF.Min(Depth * 2.5f, Length / 3f) : MathF.Min(0.3f, Length / 3f);

    /// <summary>How far out its side of the road is set at a distance along the road: none outside it, easing in and out at its ends.</summary>
    public float DepthAt(float distance)
    {
        if (distance <= Start || distance >= End)
            return 0f;
        var t = Math.Clamp(MathF.Min(distance - Start, End - distance) / Taper, 0f, 1f);
        return Depth * t * t * (3f - 2f * t);
    }

    /// <summary>
    /// How far out the far side of the sidewalk beside it is set at a distance along the road, for a
    /// sidewalk so wide. A lay-by's sidewalk bends round it as its kerb does. Any other's runs on past
    /// each end by the sidewalk's width at the full depth, so the sidewalk there is wide enough to
    /// turn in along the end bay's side and meet the sidewalk behind the bays.
    /// </summary>
    public float SidewalkDepthAt(float distance, float sidewalk)
    {
        if (Kind == ParkingKind.LayBy)
            return DepthAt(distance);
        var (start, end) = (Start - sidewalk, End + sidewalk);
        if (distance <= start || distance >= end)
            return 0f;
        var t = Math.Clamp(MathF.Min(distance - start, end - distance) / Taper, 0f, 1f);
        return Depth * t * t * (3f - 2f * t);
    }

    /// <summary>How far apart its bays are along the road.</summary>
    public float Pitch => Kind switch
    {
        ParkingKind.Angled => BayWidth / MathF.Sin(Radians),
        ParkingKind.Perpendicular => BayWidth,
        _ => BayLength,
    };

    /// <summary>Where its painted lines between bays are along the road, from where it is at full depth to where it leaves it.</summary>
    public IEnumerable<float> Lines()
    {
        var (from, to) = (Start + Taper, End - Taper);
        // Angled bays lean along the road: their lines start back by as much, so the last still fits.
        var lean = Kind == ParkingKind.Angled ? Depth / MathF.Tan(Radians) : 0f;
        var count = (int)MathF.Floor((to - from - lean) / Pitch);
        if (count < 1)
            yield break;
        // Middled along it, then shifted by its offset (a bay's pitch round, so any shift fits): as many as then fit.
        var middled = from + (to - from - lean - count * Pitch) / 2f;
        var first = from + PosMod(middled - from + Offset, Pitch);
        for (var at = first; at + lean <= to + 1e-3f; at += Pitch)
            yield return at;
    }

    private static float PosMod(float value, float step) => value - step * MathF.Floor(value / step);

    /// <summary>The way a line between bays runs out from the road's edge, as a share along the road and a share out from it.</summary>
    public (float Along, float Out) LineWay => Kind == ParkingKind.Angled ? (MathF.Cos(Radians), MathF.Sin(Radians)) : (0f, 1f);
}
