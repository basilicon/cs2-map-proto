using System;
using System.Collections.Generic;
using System.Numerics;
using Cs2MapProto.Model;

namespace Cs2MapProto.Geometry;

/// <summary>One point of a flattened path.</summary>
/// <param name="P">Position; Z already includes the elevation profile offset.</param>
/// <param name="Segment">Index of the path point the sample's segment starts at.</param>
/// <param name="IsNode">True for samples that are actual path points.</param>
/// <param name="T">Normalized distance along the path (0 first point, 1 last point).</param>
/// <param name="WidthScale">Width profile multiplier at this sample.</param>
/// <param name="WallScale">Wall height profile multiplier at this sample.</param>
public readonly record struct PathSample(Vector3 P, int Segment, bool IsNode, float T = 0, float WidthScale = 1, float WallScale = 1);

/// <summary>Flattens a path's cubic Bezier segments into a polyline. Segment i runs from point i to i+1 with
/// control points P[i] + T[i] and P[i+1] - T[i+1]; height is interpolated linearly along the segment.
/// When the path has profile curves, straight segments are subdivided too so the curves have samples to act on.</summary>
public static class PathSampler
{
    public static List<PathSample> Flatten(PathShape path, float maxStep = 32f)
    {
        var pts = path.Points;
        var result = new List<PathSample>();
        if (pts.Count == 0) return result;
        bool profiled = path.AnyProfile;

        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = V(pts[i]); var b = V(pts[i + 1]);
            result.Add(new PathSample(a, i, true));
            var ta = path.TangentAt(i); var tb = path.TangentAt(i + 1);
            var p0 = new Vector2(a.X, a.Y); var p3 = new Vector2(b.X, b.Y);
            bool curved = !PathShape.IsZero(ta) || !PathShape.IsZero(tb);
            if (!curved && !profiled) continue;

            var p1 = p0 + new Vector2(ta.X, ta.Y);
            var p2 = p3 - new Vector2(tb.X, tb.Y);
            float approxLen = curved
                ? Vector2.Distance(p0, p1) + Vector2.Distance(p1, p2) + Vector2.Distance(p2, p3)
                : Vector2.Distance(p0, p3);
            int steps = Math.Clamp((int)MathF.Ceiling(approxLen / maxStep), curved ? 2 : 1, 64);
            for (int k = 1; k < steps; k++)
            {
                float t = k / (float)steps, u = 1 - t;
                var xy = curved ? u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3 : p0 + (p3 - p0) * t;
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
        if (!profiled) return result;

        // Distance parameter, then apply the profile curves.
        var dist = new float[result.Count];
        for (int i = 1; i < result.Count; i++)
        {
            var a = result[i - 1].P; var b = result[i].P;
            dist[i] = dist[i - 1] + new Vector2(b.X - a.X, b.Y - a.Y).Length();
        }
        float total = MathF.Max(1e-3f, dist[^1]);
        for (int i = 0; i < result.Count; i++)
        {
            float t = dist[i] / total;
            var s = result[i];
            var p = s.P with { Z = s.P.Z + path.ElevationCurve.Sample(t, 0) };
            result[i] = s with
            {
                P = p, T = t,
                WidthScale = MathF.Max(0.01f, path.WidthCurve.Sample(t, 1)),
                WallScale = MathF.Max(0f, path.WallCurve.Sample(t, 1)),
            };
        }
        return result;
    }

    static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);
}
