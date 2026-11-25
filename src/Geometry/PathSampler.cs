using System;
using System.Collections.Generic;
using System.Numerics;
using Cs2MapProto.Model;

namespace Cs2MapProto.Geometry;

/// <summary>One point of a flattened path. Segment = index of the path point the sample's segment starts at.</summary>
public readonly record struct PathSample(Vector3 P, int Segment, bool IsNode);

/// <summary>Flattens a path's cubic Bezier segments into a polyline. Segment i runs from point i to i+1 with
/// control points P[i] + T[i] and P[i+1] - T[i+1]; height is interpolated linearly along the segment.</summary>
public static class PathSampler
{
    public static List<PathSample> Flatten(PathShape path, float maxStep = 32f)
    {
        var pts = path.Points;
        var result = new List<PathSample>();
        if (pts.Count == 0) return result;

        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = V(pts[i]); var b = V(pts[i + 1]);
            result.Add(new PathSample(a, i, true));
            var ta = path.TangentAt(i); var tb = path.TangentAt(i + 1);
            if (PathShape.IsZero(ta) && PathShape.IsZero(tb)) continue;

            var p0 = new Vector2(a.X, a.Y); var p3 = new Vector2(b.X, b.Y);
            var p1 = p0 + new Vector2(ta.X, ta.Y);
            var p2 = p3 - new Vector2(tb.X, tb.Y);
            float approxLen = Vector2.Distance(p0, p1) + Vector2.Distance(p1, p2) + Vector2.Distance(p2, p3);
            int steps = Math.Clamp((int)MathF.Ceiling(approxLen / maxStep), 2, 64);
            for (int k = 1; k < steps; k++)
            {
                float t = k / (float)steps, u = 1 - t;
                var xy = u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
                result.Add(new PathSample(new Vector3(xy, a.Z + (b.Z - a.Z) * t), i, false));
            }
        }
        result.Add(new PathSample(V(pts[^1]), pts.Count - 1, true));

        // Drop zero-length steps (coincident points) so later direction maths stays finite.
        for (int i = result.Count - 1; i > 0; i--)
        {
            var d = result[i].P - result[i - 1].P;
            if (d.X * d.X + d.Y * d.Y < 0.01f && MathF.Abs(d.Z) < 0.01f) result.RemoveAt(i);
        }
        return result;
    }

    static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);
}
