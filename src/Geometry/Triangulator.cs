using System;
using System.Collections.Generic;
using System.Numerics;

namespace Cs2MapProto.Geometry;

/// <summary>Turns a polygon-with-holes into convex faces: holes are bridged into the outer loop,
/// the result is ear-clipped, then triangles are greedily merged back into convex polygons
/// (Hertel–Mehlhorn) so Hammer gets clean n-gon faces instead of a triangle soup.</summary>
public static class Triangulator
{

    public static List<int[]> ConvexFaces(Shape2 shape, out List<Vector2> points) => ConvexFaces(shape, out points, out _);

    /// <param name="triangles">The triangulation before merging (same vertex indices); use it when the
    /// faces won't be planar in 3D, e.g. a top surface whose height varies per vertex.</param>
    public static List<int[]> ConvexFaces(Shape2 shape, out List<Vector2> points, out List<int[]> triangles)
    {
        triangles = new List<int[]>();
        points = new List<Vector2>();
        var loops = new List<List<Vector2>> { RemoveCollinear(shape.Outer) };
        foreach (var h in shape.Holes) loops.Add(RemoveCollinear(h));
        SplitTouches(loops);
        var outer = AddLoop(points, loops[0]);
        var holes = new List<List<int>>();
        for (int k = 1; k < loops.Count; k++)
        {
            var loop = AddLoop(points, loops[k]);
            if (loop.Count >= 3) holes.Add(loop);
        }
        if (outer.Count < 3) return new List<int[]>();

        var merged = BridgeHoles(points, outer, holes);
        triangles = Canonical(points, EarClip(points, merged));
        return MergeConvex(points, triangles);
    }

    /// <summary>Bridge vertices are duplicated points; collapse them to one index so bridge edges become shared edges.</summary>
    static List<int[]> Canonical(List<Vector2> pts, List<int[]> tris)
    {
        var canon = new Dictionary<Vector2, int>();
        var result = new List<int[]>(tris.Count);
        foreach (var t in tris)
        {
            var f = new int[t.Length];
            for (int k = 0; k < t.Length; k++)
            {
                if (!canon.TryGetValue(pts[t[k]], out int c)) { c = t[k]; canon[pts[t[k]]] = c; }
                f[k] = c;
            }
            result.Add(f);
        }
        return result;
    }

    static List<int> AddLoop(List<Vector2> pts, List<Vector2> loop)
    {
        var idx = new List<int>(loop.Count);
        foreach (var p in loop) { idx.Add(pts.Count); pts.Add(p); }
        return idx;
    }

    // Points closer than this are merged. Must be well above PolyMesh's weld size (1/64u): otherwise points the
    // triangulation keeps apart get welded into one afterwards, and a face holding both is dropped as degenerate.
    const float MergeDistSq = 0.05f * 0.05f;

    /// <summary>Drops near-duplicate points (see MergeDistSq) and points on a straight line.</summary>
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
                if (Vector2.DistanceSquared(a, b) < MergeDistSq || MathF.Abs(Cross(b - a, c - b)) < 1e-3f * (b - a).Length() * (c - b).Length() + 1e-6f)
                {
                    l.RemoveAt(i); changed = true; i--;
                }
            }
        }
        return l;
    }

    /// <summary>Clipper's outlines may touch themselves (or a hole) at a point: a vertex lying on another edge.
    /// Ear clipping can't see such a touch and may build triangles across it, so a copy of the vertex is
    /// inserted into that edge; the touch becomes a shared point, handled like a hole bridge.</summary>
    const float TouchTol = 0.05f; // a point this close to an edge touches it

    static void SplitTouches(List<List<Vector2>> loops)
    {
        var all = new List<Vector2>();
        foreach (var l in loops) all.AddRange(l);
        for (int li = 0; li < loops.Count; li++)
        {
            var l = loops[li];
            if (l.Count < 3) continue;
            var result = new List<Vector2>(l.Count);
            for (int i = 0; i < l.Count; i++)
            {
                Vector2 p = l[i], q = l[(i + 1) % l.Count];
                result.Add(p);
                var d = q - p; float len2 = d.LengthSquared();
                if (len2 < 1e-6f) continue;
                List<(float t, Vector2 v)> hits = null;
                foreach (var v in all)
                {
                    if (Vector2.DistanceSquared(v, p) < TouchTol * TouchTol || Vector2.DistanceSquared(v, q) < TouchTol * TouchTol) continue;
                    float t = Vector2.Dot(v - p, d) / len2;
                    if (t <= 0 || t >= 1) continue;
                    if (Vector2.DistanceSquared(p + d * t, v) >= TouchTol * TouchTol) continue;
                    (hits ??= new()).Add((t, v));
                }
                if (hits == null) continue;
                hits.Sort((x, y) => x.t.CompareTo(y.t));
                foreach (var (_, v) in hits) if (v != result[^1]) result.Add(v);
            }
            loops[li] = result;
        }
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
            // When the ray hits P itself the triangle is degenerate and P is visible: skip the search (a flat
            // triangle would count every vertex on the ray's line as inside, even ones left of M).
            float bestAngle = float.MaxValue, bestDist = float.MaxValue;
            for (int k = 0; k < poly.Count && Vector2.DistanceSquared(I, P) > 1e-6f; k++)
            {
                var v = pts[poly[k]];
                if (k == pk || v == P || v.X < M.X) continue;
                if (!InTriangle(v, M, I, P)) continue;
                float ang = MathF.Abs(MathF.Atan2(v.Y - M.Y, v.X - M.X));
                float dist = Vector2.DistanceSquared(v, M);
                if (ang < bestAngle - 1e-6f || (MathF.Abs(ang - bestAngle) <= 1e-6f && dist < bestDist))
                {
                    bestAngle = ang; bestDist = dist; pk = k;
                }
            }

            // An earlier bridge duplicates its end points, so the chosen point may appear twice in the loop; only
            // the copy whose interior angle faces M can take the bridge (the other lies on the far side of that
            // earlier bridge, and joining there would tangle the loop).
            var chosen = pts[poly[pk]];
            for (int k = 0; k < poly.Count; k++)
                if (pts[poly[k]] == chosen && LocallyInside(pts, poly, k, M)) { pk = k; break; }

            var bridged = new List<int>(poly.Count + hole.Count + 2);
            for (int k = 0; k <= pk; k++) bridged.Add(poly[k]);
            for (int i = 0; i <= hole.Count; i++) bridged.Add(hole[(m + i) % hole.Count]);
            bridged.Add(poly[pk]);
            for (int k = pk + 1; k < poly.Count; k++) bridged.Add(poly[k]);
            poly = bridged;
        }
        return poly;
    }

    /// <summary>Whether direction v→m starts into the polygon's interior at loop position k (counter-clockwise loop).</summary>
    static bool LocallyInside(List<Vector2> pts, List<int> loop, int k, Vector2 m)
    {
        var u = pts[loop[(k + loop.Count - 1) % loop.Count]]; var v = pts[loop[k]]; var w = pts[loop[(k + 1) % loop.Count]];
        bool leftOfIn = Cross(v - u, m - v) >= 0, leftOfOut = Cross(w - v, m - v) >= 0;
        return Cross(v - u, w - v) >= 0 ? leftOfIn && leftOfOut : leftOfIn || leftOfOut;
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

    /// <summary>Removes one repeated point (same position as the next) or one spike (both neighbours at the same
    /// position: the point and one of the copies go). Returns whether anything was removed.</summary>
    static bool DropDegenerate(List<Vector2> pts, List<int> l)
    {
        int n = l.Count;
        for (int i = 0; i < n && n > 3; i++)
        {
            int next = (i + 1) % n;
            if (pts[l[i]] == pts[l[next]]) { l.RemoveAt(next); return true; }
            if (pts[l[(i + n - 1) % n]] == pts[l[next]])
            {
                l.RemoveAt(Math.Max(i, next)); l.RemoveAt(Math.Min(i, next));
                return true;
            }
        }
        return false;
    }

    /// <summary>Reflex, or convex but thinner than the touch tolerance: such slivers come from zero-width channels
    /// (a hairline slit whose sides SplitTouches made coincident) and would overlap their neighbours. Left alone,
    /// the channel collapses through DropDegenerate once the area around it is clipped.</summary>
    static bool Thin(Vector2 a, Vector2 b, Vector2 c) => Cross(b - a, c - b) <= TouchTol * (c - a).Length();

    /// <summary>True when segments a-b and c-d cross at a point inside both.</summary>
    static bool Crosses(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float d1 = Cross(b - a, c - a), d2 = Cross(b - a, d - a), d3 = Cross(d - c, a - c), d4 = Cross(d - c, b - c);
        return ((d1 > 1e-6f && d2 < -1e-6f) || (d1 < -1e-6f && d2 > 1e-6f)) && ((d3 > 1e-6f && d4 < -1e-6f) || (d3 < -1e-6f && d4 > 1e-6f));
    }

    static List<int[]> EarClip(List<Vector2> pts, List<int> poly)
    {
        var tris = new List<int[]>();
        var l = new List<int>(poly);
        int guard = l.Count * l.Count + 10;
        while (l.Count > 3 && guard-- > 0)
        {
            // Clipping a lobe that hangs off a shared point (a hole bridge or a split touch) can leave a zero-area
            // spike x-b-x, or a point repeated back to back; neither is ever an ear, so drop them first.
            if (DropDegenerate(pts, l)) continue;
            bool clipped = false;
            // Passes, strictest first: 0 also rejects slivers (see Thin) and ears whose new edge (a-c) crosses an
            // outline edge (outlines from Clipper can cross themselves by a few hundredths where rounding meets a
            // near-pinch, and an edge can then pass through an ear without any vertex inside it); 1 allows
            // slivers; 2 drops the crossing check.
            for (int pass = 0; pass < 3 && !clipped; pass++)
            for (int i = 0; i < l.Count; i++)
            {
                int ia = l[(i + l.Count - 1) % l.Count], ib = l[i], ic = l[(i + 1) % l.Count];
                Vector2 a = pts[ia], b = pts[ib], c = pts[ic];
                if (pass == 0 ? Thin(a, b, c) : Cross(b - a, c - b) <= 1e-6f) continue; // reflex or flat
                bool blocked = false;
                for (int j = 0; j < l.Count && !blocked; j++)
                {
                    var p = pts[l[j]];
                    if (p == a || p == b || p == c) continue;
                    blocked = InTriangle(p, a, b, c);
                }
                for (int j = 0; j < l.Count && !blocked && pass < 2; j++)
                {
                    Vector2 p = pts[l[j]], q = pts[l[(j + 1) % l.Count]];
                    blocked = p != a && p != c && q != a && q != c && Crosses(a, c, p, q);
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
