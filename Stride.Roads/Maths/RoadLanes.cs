using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Stride.Roads;

/// <summary>
/// The line traffic follows along one lane of a road, in the way it goes: its points down the
/// middle of the lane, first to last. <see cref="Index"/> counts the lanes going that way from the
/// inside (the one nearest the other way, or the median) outwards.
/// </summary>
public sealed record LanePath(LaneDirection Direction, int Index, IReadOnlyList<Vector3> Points);

public static class RoadLanes
{
    /// <summary>A path down the middle of every lane, each in the way its traffic goes.</summary>
    public static IReadOnlyList<LanePath> Paths(IReadOnlyList<RoadFrame> frames, RoadCrossSection section, bool closed)
    {
        var paths = new List<LanePath>();
        var lanes = section.Bands().Where(b => b.Kind == BandKind.Lane).ToList();
        foreach (var direction in new[] { LaneDirection.Forward, LaneDirection.Backward })
        {
            // Inside out: forward lanes are to the right, so from the leftmost; backward, the reverse.
            var going = lanes.Where(l => l.Direction == direction).OrderBy(l => direction == LaneDirection.Forward ? l.Middle : -l.Middle).ToList();
            for (var index = 0; index < going.Count; index++)
            {
                var middle = going[index].Middle;
                var points = frames.Select(f => f.Across(middle)).ToList();
                if (closed && points.Count > 0)
                    points.Add(points[0]);
                if (direction == LaneDirection.Backward)
                    points.Reverse();
                paths.Add(new LanePath(direction, index, points));
            }
        }

        return paths;
    }
}
