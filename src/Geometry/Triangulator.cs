using System;
using System.Collections.Generic;
using System.Numerics;

namespace Cs2MapProto.Geometry;

/// <summary>Turns a polygon-with-holes into convex faces: holes are bridged into the outer loop,
/// the result is ear-clipped, then triangles are greedily merged back into convex polygons
/// (Hertel–Mehlhorn) so Hammer gets clean n-gon faces instead of a triangle soup.</summary>
public static class Triangulator
{
    public static List<int[]> ConvexFaces(Shape2 shape, out List<Vector2> points)
    {
        points = new List<Vector2>();
        var outer = AddLoop(points, RemoveCollinear(shape.Outer));
        var holes = new List<List<int>>();
        foreach (var h in shape.Holes)
        {
            var loop = AddLoop(points, RemoveCollinear(h));
            if (loop.Count >= 3) holes.Add(loop);
        }
        if (outer.Count < 3) return new List<int[]>();

        var merged = BridgeHoles(points, outer, holes);
        var tris = EarClip(points, merged);
        return MergeConvex(points, tris);
    }

    static List<int> AddLoop(List<Vector2> pts, List<Vector2> loop)
    {
        var idx = new List<int>(loop.Count);
        foreach (var p in loop) { idx.Add(pts.Count); pts.Add(p); }
        return idx;
    }

    static List<Vector2> RemoveCollinear(List<Vector2> loop)
    {
        var l = new List<Vector2>(loop);
        bool changed = true;
        while (changed && l.Count > 3)
        {
            changed = false;
            for (int i = 0; i < l.Count && l.Count > 3; i++)
            {
                var a = l[(i + l.Count - 1) % l.Count]; var b = l[i]; var c = l[(i + 1) % l.Count];
                if (Vector2.DistanceSquared(a, b) < 1e-6f || MathF.Abs(Cross(b - a, c - b)) < 1e-3f * (b - a).Length() * (c - b).Length() + 1e-6f)
                {
                    l.RemoveAt(i); changed = true; i--;
                }
            }
        }
        return l;
    }

    static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    static List<int> BridgeHoles(List<Vector2> pts, List<int> outer, List<List<int>> holes)
    {
        var poly = new List<int>(outer);
        // Rightmost holes first so each bridge only has to look right.
        holes.Sort((a, b) => MaxX(pts, b).CompareTo(MaxX(pts, a)));
        foreach (var hole in holes)
        {
            int m = 0;
            for (int i = 1; i < hole.Count; i++) if (pts[hole[i]].X > pts[hole[m]].X) m = i;
            var M = pts[hole[m]];

            // Cast a ray towards +X and find the nearest outer edge it crosses.
            float bestX = float.MaxValue; int edge = -1;
            for (int k = 0; k < poly.Count; k++)
            {
                var p = pts[poly[k]]; var q = pts[poly[(k + 1) % poly.Count]];
                if (p.Y == q.Y) continue;
                if ((p.Y <= M.Y && q.Y >= M.Y) || (q.Y <= M.Y && p.Y >= M.Y))
                {
                    float x = p.X + (M.Y - p.Y) * (q.X - p.X) / (q.Y - p.Y);
                    if (x >= M.X && x < bestX) { bestX = x; edge = k; }
                }
            }
            if (edge < 0) continue; // hole not inside outer; ignore

            var I = new Vector2(bestX, M.Y);
            int pk = pts[poly[edge]].X > pts[poly[(edge + 1) % poly.Count]].X ? edge : (edge + 1) % poly.Count;
            var P = pts[poly[pk]];

            // Any outer vertex inside triangle (M, I, P) would block the bridge; take the one with the smallest angle.
            float bestAngle = float.MaxValue, bestDist = float.MaxValue;
            for (int k = 0; k < poly.Count; k++)
            {
                var v = pts[poly[k]];
                if (k == pk || v == P) continue;
                if (!InTriangle(v, M, I, P)) continue;
                float ang = MathF.Abs(MathF.Atan2(v.Y - M.Y, v.X - M.X));
                float dist = Vector2.DistanceSquared(v, M);
                if (ang < bestAngle - 1e-6f || (MathF.Abs(ang - bestAngle) <= 1e-6f && dist < bestDist))
                {
                    bestAngle = ang; bestDist = dist; pk = k;
                }
            }

            var bridged = new List<int>(poly.Count + hole.Count + 2);
            for (int k = 0; k <= pk; k++) bridged.Add(poly[k]);
            for (int i = 0; i <= hole.Count; i++) bridged.Add(hole[(m + i) % hole.Count]);
            bridged.Add(poly[pk]);
            for (int k = pk + 1; k < poly.Count; k++) bridged.Add(poly[k]);
            poly = bridged;
        }
        return poly;
    }

    static float MaxX(List<Vector2> pts, List<int> loop)
    {
        float m = float.MinValue;
        foreach (int i in loop) m = MathF.Max(m, pts[i].X);
        return m;
    }

    static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
        bool neg = d1 < -1e-4f || d2 < -1e-4f || d3 < -1e-4f;
        bool pos = d1 > 1e-4f || d2 > 1e-4f || d3 > 1e-4f;
        return !(neg && pos);
    }

    static List<int[]> EarClip(List<Vector2> pts, List<int> poly)
    {
        var tris = new List<int[]>();
        var l = new List<int>(poly);
        int guard = l.Count * l.Count + 10;
        while (l.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int i = 0; i < l.Count; i++)
            {
                int ia = l[(i + l.Count - 1) % l.Count], ib = l[i], ic = l[(i + 1) % l.Count];
                Vector2 a = pts[ia], b = pts[ib], c = pts[ic];
                if (Cross(b - a, c - b) <= 1e-6f) continue; // reflex or flat
                bool blocked = false;
                for (int j = 0; j < l.Count && !blocked; j++)
                {
                    var p = pts[l[j]];
                    if (p == a || p == b || p == c) continue;
                    blocked = InTriangle(p, a, b, c);
                }
                if (blocked) continue;
                tris.Add(new[] { ia, ib, ic });
                l.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped)
            {
                // Degenerate input: drop the flattest vertex so we always terminate.
                int best = 0; float bestArea = float.MaxValue;
                for (int i = 0; i < l.Count; i++)
                {
                    var a = pts[l[(i + l.Count - 1) % l.Count]]; var b = pts[l[i]]; var c = pts[l[(i + 1) % l.Count]];
                    float ar = MathF.Abs(Cross(b - a, c - b));
                    if (ar < bestArea) { bestArea = ar; best = i; }
                }
                l.RemoveAt(best);
            }
        }
        if (l.Count == 3 && Cross(pts[l[1]] - pts[l[0]], pts[l[2]] - pts[l[1]]) > 1e-6f)
            tris.Add(new[] { l[0], l[1], l[2] });
        return tris;
    }

    static List<int[]> MergeConvex(List<Vector2> pts, List<int[]> tris)
    {
        // Bridge vertices are duplicated points; collapse them to one index so bridge edges become shared edges.
        var canon = new Dictionary<Vector2, int>();
        var faces = new List<List<int>>();
        foreach (var t in tris)
        {
            var f = new List<int>(3);
            foreach (int i in t)
            {
                if (!canon.TryGetValue(pts[i], out int c)) { c = i; canon[pts[i]] = i; }
                f.Add(c);
            }
            faces.Add(f);
        }

        bool any = true;
        while (any)
        {
            any = false;
            var edgeOwner = new Dictionary<(int, int), int>();
            for (int f = 0; f < faces.Count; f++)
                if (faces[f] != null)
                    for (int i = 0; i < faces[f].Count; i++)
                        edgeOwner[(faces[f][i], faces[f][(i + 1) % faces[f].Count])] = f;

            var touched = new bool[faces.Count];
            for (int f = 0; f < faces.Count; f++)
            {
                if (faces[f] == null || touched[f]) continue;
                var F = faces[f];
                for (int i = 0; i < F.Count; i++)
                {
                    int a = F[i], b = F[(i + 1) % F.Count];
                    if (!edgeOwner.TryGetValue((b, a), out int g) || g == f || faces[g] == null || touched[g]) continue;
                    var G = faces[g];
                    int j = G.IndexOf(b);
                    if (j < 0 || G[(j + 1) % G.Count] != a) continue;

                    var m = new List<int>(F.Count + G.Count - 2);
                    for (int k = 0; k < F.Count; k++) m.Add(F[(i + 1 + k) % F.Count]);      // b ... a
                    for (int k = 2; k < G.Count; k++) m.Add(G[(j + k) % G.Count]);          // after a ... before b
                    if (!IsConvex(pts, m)) continue;

                    faces[f] = m; faces[g] = null;
                    touched[f] = touched[g] = true;
                    any = true;
                    break;
                }
            }
        }

        var result = new List<int[]>();
        foreach (var f in faces) if (f != null) result.Add(f.ToArray());
        return result;
    }

    static bool IsConvex(List<Vector2> pts, List<int> loop)
    {
        if (new HashSet<int>(loop).Count != loop.Count) return false;
        for (int i = 0; i < loop.Count; i++)
        {
            var a = pts[loop[(i + loop.Count - 1) % loop.Count]]; var b = pts[loop[i]]; var c = pts[loop[(i + 1) % loop.Count]];
            if (Cross(b - a, c - b) < -1e-4f) return false;
        }
        return true;
    }
}
