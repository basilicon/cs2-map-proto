using System;
using System.Collections.Generic;
using System.Numerics;
using Clipper2Lib;

namespace Cs2MapProto.Geometry;

/// <summary>A simple polygon with holes. Outer is counter-clockwise, holes clockwise.</summary>
public sealed class Shape2
{
    public List<Vector2> Outer = new();
    public List<List<Vector2>> Holes = new();
}

/// <summary>2D boolean / offset helpers on top of Clipper2, in Hammer units.</summary>
public static class Poly2
{
    const int Precision = 2; // Clipper rounds to 0.01 units, so shared cut points are exact.

    public static PathD ToPath(IEnumerable<Vector2> pts)
    {
        var p = new PathD();
        foreach (var v in pts) p.Add(new PointD(v.X, v.Y));
        return p;
    }

    public static List<Vector2> FromPath(PathD p)
    {
        var l = new List<Vector2>(p.Count);
        foreach (var v in p) l.Add(new Vector2((float)v.x, (float)v.y));
        return l;
    }

    public static PathsD Union(PathsD a) => Clipper.Union(a, FillRule.NonZero);
    public static PathsD Difference(PathsD a, PathsD b) => Clipper.Difference(a, b, FillRule.NonZero, Precision);
    public static PathsD Intersect(PathsD a, PathsD b) => Clipper.Intersect(a, b, FillRule.NonZero, Precision);

    public static PathsD Inflate(PathsD polys, double delta) =>
        Clipper.InflatePaths(polys, delta, JoinType.Miter, EndType.Polygon, 2.0, Precision);

    /// <summary>Morphological closing (grow then shrink by r): joins shapes that touch at a point or nearly
    /// touch, removing pinch vertices, with no visible change elsewhere.</summary>
    public static PathsD Close(PathsD polys, double r = 0.5) => Inflate(Inflate(polys, r), -r);

    /// <summary>Thickens an open polyline into polygons with flat (butt) ends.</summary>
    public static PathsD ThickenPolyline(IEnumerable<Vector2> line, double halfWidth) =>
        Clipper.InflatePaths(new PathsD { ToPath(line) }, halfWidth, JoinType.Miter, EndType.Butt, 2.0, Precision);

    /// <summary>Normalizes arbitrary (possibly overlapping) paths into polygons-with-holes.</summary>
    public static List<Shape2> ToShapes(PathsD paths, double minArea = 1.0)
    {
        var tree = new PolyTreeD();
        Clipper.BooleanOp(ClipType.Union, paths, new PathsD(), tree, FillRule.NonZero, Precision);
        var result = new List<Shape2>();
        Walk(tree, result, minArea);
        return result;
    }

    static void Walk(PolyPathD node, List<Shape2> result, double minArea)
    {
        for (int i = 0; i < node.Count; i++)
        {
            var outer = node[i];
            if (Math.Abs(Clipper.Area(outer.Polygon)) >= minArea)
            {
                var s = new Shape2 { Outer = Oriented(FromPath(outer.Polygon), ccw: true) };
                for (int h = 0; h < outer.Count; h++)
                {
                    var hole = outer[h];
                    if (Math.Abs(Clipper.Area(hole.Polygon)) >= minArea)
                        s.Holes.Add(Oriented(FromPath(hole.Polygon), ccw: false));
                    Walk(hole, result, minArea); // islands inside holes
                }
                result.Add(s);
            }
        }
    }

    public static float SignedArea(IReadOnlyList<Vector2> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var u = p[i]; var v = p[(i + 1) % p.Count];
            a += (double)u.X * v.Y - (double)v.X * u.Y;
        }
        return (float)(a * 0.5);
    }

    public static List<Vector2> Oriented(List<Vector2> p, bool ccw)
    {
        if ((SignedArea(p) > 0) != ccw) p.Reverse();
        return p;
    }

    /// <summary>Rectangle from a to b with the given half width (a "butt" segment).</summary>
    public static PathD SegmentQuad(Vector2 a, Vector2 b, float halfWidth)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return new PathD();
        var n = new Vector2(-d.Y, d.X) / len * halfWidth;
        return ToPath(new[] { a - n, b - n, b + n, a + n });
    }

    public static PathD Triangle(Vector2 a, Vector2 b, Vector2 c) => Ccw(new[] { a, b, c });

    /// <summary>Path forced counter-clockwise, so NonZero unions never treat user input as a hole.</summary>
    public static PathD Ccw(IEnumerable<Vector2> pts) => ToPath(Oriented(new List<Vector2>(pts), ccw: true));
}
