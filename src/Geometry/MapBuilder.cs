using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Clipper2Lib;
using Cs2MapProto.Model;

namespace Cs2MapProto.Geometry;

public enum MeshKind { Floor, Ramp, Wall, Obstacle, PitBottom, PitLiner, Ceiling }

public sealed class BuiltMesh
{
    public string Name;
    public MeshKind Kind;
    public PolyMesh Mesh;
}

public sealed class BuiltEntity
{
    public string ClassName;
    public Vector3 Origin;
    public Vector3 Angles; // pitch yaw roll
    public Dictionary<string, string> Properties = new();
}

/// <summary>A floor edge segment (2D) with its height, half width and the wall height on its left and right
/// (seen from A towards B) at both ends. Ceil is the clearance up to the roof over it (NaN = open).</summary>
public readonly record struct WallRef(Vector2 A, Vector2 B, float ZA, float ZB, float HwA, float HwB, float LeftA, float LeftB, float RightA, float RightB, float Ceil = float.NaN);

public sealed class BuiltMap
{
    public readonly List<BuiltMesh> Meshes = new();
    public readonly List<BuiltEntity> Entities = new();
    public readonly List<string> Warnings = new();
}

/// <summary>Turns the high-level document into closed brush-like meshes. The editor preview and the
/// .vmap exporter both use this output, so what you see is what Hammer gets.</summary>
public static class MapBuilder
{
    const float FlatEpsilon = 0.05f;    // level segments interpolate exactly, so any real height change counts as a ramp
    const float SpawnLift = 16f;        // matches Valve's template maps
    const float MinSpawnSpacing = 48f;  // player hull is 32 wide; leave a margin
    const float EndDirSpan = 24f;       // free-end direction is measured over at least this distance
    const float RampClearance = 72f;    // player height: flat floors closer than this to a ramp yield to it
    const float PlayerHeight = 72f;     // standing hull: ceilings lower than this above their floor get a warning

    public static BuiltMap Build(MapDocument doc)
    {
        var map = new BuiltMap();
        var s = doc.Build;
        float thick = MathF.Max(1f, s.FloorThickness);

        var levels = new SortedDictionary<int, PathsD>(); // floor height -> footprint polygons
        var footprint = new PathsD();                     // every walkable area, for perimeter walls
        var ramps = new List<(List<Vector2> left, List<Vector2> right, List<float> zl, List<float> zr)>();
        bool steep = false;
        var rampSections = new List<(PathD quad, float zMin, float zMax)>(); // ramp footprint pieces with their height range
        float minZ = float.MaxValue, maxZ = float.MinValue;
        var wallRefs = new List<WallRef>(); // floor edges that set the height of nearby perimeter walls
        // Areas where a path side has its wall switched off, and walls of walled sides and floor regions that no
        // cut removes; each with the floor heights it belongs to, so a stacked level only uses its own.
        var wallCuts = new List<(PathD poly, float zMin, float zMax)>();
        var wallKeeps = new List<(PathD poly, float zMin, float zMax)>();
        // Walkable surfaces (flat levels, ramp sections) for finding stacked levels; see the wall section.
        var surfaces = new List<Surface>();
        var rampCenters = new List<List<(Vector2 a, Vector2 b, float za, float zb)>>(); // centreline per ramp run
        bool anyOpenSide = s.PerimeterWalls && doc.Paths.Any(p => !p.WallLeft || !p.WallRight);
        // Roofs of covered paths: underside height -> the floor pieces under it, with their floor height.
        var ceilLevels = new SortedDictionary<int, List<(int floor, PathD poly)>>();
        // Floor without a roof of its own (uncovered paths, floor regions), by height: a covered path's roof stops
        // where it meets one, so a covered branch doesn't reach out over the corridor it joins.
        var openFloor = new Dictionary<int, PathsD>();
        var openRampSections = new List<(PathD quad, float zMin, float zMax)>(); // ramps of uncovered paths
        void AddOpenFloor(float z, PathD poly)
        {
            int key = (int)MathF.Round(z);
            if (!openFloor.TryGetValue(key, out var list)) openFloor[key] = list = new PathsD();
            list.Add(poly);
        }
        var ceilRamps = new List<(List<Vector2> left, List<Vector2> right, List<float> zl, List<float> zr)>(); // top heights
        var ceilRampSections = new List<(PathD quad, float zMin, float zMax)>(); // underside height range
        var ceilCover = new PathsD(); // everything roofed, as seen from above (for splitting the wall ring)
        float roofTop = float.MinValue;

        void AddLevel(float z, PathD poly)
        {
            if (poly.Count < 3) return;
            int key = (int)MathF.Round(z);
            if (!levels.TryGetValue(key, out var list)) levels[key] = list = new PathsD();
            list.Add(poly);
            footprint.Add(poly);
            minZ = MathF.Min(minZ, z); maxZ = MathF.Max(maxZ, z);
        }

        // ---- Paths: flatten Bezier curves, then split into flat runs (merged per height) and ramp ribbons.
        foreach (var path in doc.Paths)
        {
            var samples = PathSampler.Flatten(path);
            if (samples.Count < 2) continue;
            float hw = MathF.Max(8f, path.Width * 0.5f);
            float HW(int i) => MathF.Max(8f, path.Width * 0.5f * samples[i].WidthScale);
            float startDepth = MathF.Max(0f, path.StartDepth ?? hw);
            float endDepth = MathF.Max(0f, path.EndDepth ?? hw);
            int m = samples.Count;
            // Free flat ends take the end point's corner style: how far the cap's corners are bevelled / rounded.
            float startCapR = CapRadius(path.CornerAt(0), HW(0), startDepth);
            float endCapR = CapRadius(path.CornerAt(path.Points.Count - 1), HW(m - 1), endDepth);
            Vector2 P(int i) => new(samples[i].P.X, samples[i].P.Y);
            float Z(int i) => samples[i].P.Z;
            bool Flat(int seg) => MathF.Abs(Z(seg + 1) - Z(seg)) < FlatEpsilon;
            float WallL(int i) => (path.WallHeightLeft ?? s.WallHeight) * samples[i].WallScale;
            float WallR(int i) => (path.WallHeightRight ?? s.WallHeight) * samples[i].WallScale;
            // Corner style of a sample: path points use their own; in-between samples (curve steps) are mitred,
            // which is exact for their small turns.
            CornerStyle Corner(int i) => samples[i].IsNode ? path.CornerAt(samples[i].Segment) : CornerStyle.Sharp;
            float? ceil = path.CeilingHeight is float ch && ch > 0 ? ch : null;
            if (ceil is float c0 && c0 < PlayerHeight)
                map.Warnings.Add($"A path's ceiling is only {c0}u above its floor; players need {PlayerHeight}u to stand.");
            for (int i = 0; i + 1 < m; i++)
                wallRefs.Add(new WallRef(P(i), P(i + 1), Z(i), Z(i + 1), HW(i), HW(i + 1), WallL(i), WallL(i + 1), WallR(i), WallR(i + 1), ceil ?? float.NaN));

            // Floor pieces of a covered path also make up its roof, one clearance higher (widened over the
            // walls once the level is merged).
            void AddPathLevel(float z, PathD poly)
            {
                AddLevel(z, poly);
                if (poly.Count < 3) return;
                if (ceil is not float c) { AddOpenFloor(z, poly); return; }
                int key = (int)MathF.Round(z + c);
                if (!ceilLevels.TryGetValue(key, out var list)) ceilLevels[key] = list = new();
                list.Add(((int)MathF.Round(z), poly));
            }

            // A side without walls: cut a strip along that side out of the wall ring. The strip runs from the
            // centreline out past the wall, segment by segment, plus a wedge on the outside of each bend; free
            // flat ends include their extension, so the end wall itself stays. The same strip (without the
            // margin) along a walled side protects that wall: the inside of a hairpin can't eat the opposite
            // wall, and a branch keeps its wall right up to the corner where it meets an open side.
            if (anyOpenSide)
            {
                List<(PathD, float, float)> SideStrip(float side, float margin)
                {
                    var res = new List<(PathD, float, float)>();
                    float R(int i) => HW(i) + s.WallThickness + margin;
                    Vector2 N(int seg) { var d = Vector2.Normalize(P(seg + 1) - P(seg)); return float.IsNaN(d.X) ? Vector2.Zero : new Vector2(-d.Y, d.X) * side; }
                    for (int i = 0; i + 1 < m; i++)
                    {
                        var n0 = N(i);
                        if (n0 == Vector2.Zero) continue;
                        res.Add((Poly2.Ccw(new[] { P(i), P(i + 1), P(i + 1) + n0 * R(i + 1), P(i) + n0 * R(i) }), MathF.Min(Z(i), Z(i + 1)), MathF.Max(Z(i), Z(i + 1))));
                    }
                    for (int i = 1; i + 1 < m; i++)
                    {
                        var na = N(i - 1); var nb = N(i);
                        if (na == Vector2.Zero || nb == Vector2.Zero) continue;
                        float cr = na.X * nb.Y - na.Y * nb.X;
                        // Outside of the bend on this side: the normals turn away from the side (clockwise for +side).
                        if (cr * side >= -1e-4f) continue;
                        float r = HW(i) + 2f * s.WallThickness + margin;
                        var wedge = CornerArc(P(i), na * r, nb * r, r, CornerStyle.Sharp);
                        wedge.Insert(0, P(i));
                        res.Add((Poly2.Ccw(wedge), Z(i), Z(i)));
                    }
                    // Only the straight part of the end extension: a bevelled / rounded cap is all end wall.
                    void EndStrip(int end, int step, float depth, bool flat)
                    {
                        if (!flat || depth <= 0.5f) return;
                        depth -= end == 0 ? startCapR : endCapR;
                        if (depth <= 0.5f) return;
                        var d = EndDir(end, step);
                        if (d == Vector2.Zero) return;
                        var nrm = new Vector2(-d.Y, d.X) * side * (step > 0 ? -1f : 1f); // d points away from the path
                        float r = R(end);
                        res.Add((Poly2.Ccw(new[] { P(end), P(end) + d * depth, P(end) + d * depth + nrm * r, P(end) + nrm * r }), Z(end), Z(end)));
                    }
                    EndStrip(0, +1, startDepth, Flat(0));
                    EndStrip(m - 1, -1, endDepth, Flat(m - 2));
                    return res;
                }
                if (path.WallLeft) wallKeeps.AddRange(SideStrip(+1f, 0f)); else wallCuts.AddRange(SideStrip(+1f, 2f));
                if (path.WallRight) wallKeeps.AddRange(SideStrip(-1f, 0f)); else wallCuts.AddRange(SideStrip(-1f, 2f));
            }

            // Maximal runs of same-kind segments; run [k, j] covers samples k..j+1.
            for (int k = 0; k < m - 1;)
            {
                bool flat = Flat(k);
                int j = k;
                // A flat run must stay level relative to its first sample, not just step to step; otherwise a
                // gentle slope (e.g. near the ends of an elevation curve) creeps up while being built flat.
                while (j + 1 < m - 1 && Flat(j + 1) == flat && (!flat || MathF.Abs(Z(j + 2) - Z(k)) < FlatEpsilon)) j++;
                if (flat) EmitFlatRun(k, j + 1);
                else EmitRampRun(k, j + 1);
                k = j + 1;
            }

            // Where a flat run meets a ramp both end square at the point; fill the outside of the corner.
            for (int i = 1; i < m - 1; i++)
            {
                if (Flat(i - 1) == Flat(i)) continue;
                if (JointFill(P(i - 1), P(i), P(i + 1), HW(i), Corner(i)) is { } fill) AddPathLevel(Z(i), fill);
            }

            // Direction a free end points in, measured over at least EndDirSpan so a tiny last step can't skew it
            // (a Bezier segment ending on a sharp point slows to near zero length at that end).
            Vector2 EndDir(int end, int step)
            {
                for (int q = end + step; q >= 0 && q < m; q += step)
                    if (Vector2.Distance(P(end), P(q)) >= EndDirSpan || q == 0 || q == m - 1)
                    {
                        var d = Vector2.Normalize(P(end) - P(q));
                        return float.IsNaN(d.X) ? Vector2.Zero : d;
                    }
                return Vector2.Zero;
            }

            void EmitFlatRun(int from, int to)
            {
                var line = new List<Vector2>();
                var widths = new List<float>();
                var corners = new List<CornerStyle>();
                // Free flat ends are extended by their depth, so dead ends and T-junctions overlap neatly.
                // Extending the line itself (rather than adding a separate cap shape) keeps the end in one piece
                // with the corridor, so there's no seam or wedge gap between them; the extension step is then
                // built as a cap shaped by the end point's corner style.
                bool capStart = false, capEnd = false;
                if (from == 0 && startDepth > 0.5f && EndDir(0, +1) is var d0 && d0 != Vector2.Zero) { line.Add(P(0) + d0 * startDepth); widths.Add(HW(0)); corners.Add(CornerStyle.Sharp); capStart = true; }
                for (int q = from; q <= to; q++) { line.Add(P(q)); widths.Add(HW(q)); corners.Add(Corner(q)); }
                if (to == m - 1 && endDepth > 0.5f && EndDir(m - 1, -1) is var d1 && d1 != Vector2.Zero) { line.Add(P(m - 1) + d1 * endDepth); widths.Add(HW(m - 1)); corners.Add(CornerStyle.Sharp); capEnd = true; }
                // One trapezoid per step (square ends, each with its own end widths) plus the chosen corner shape
                // filling the outside of every joint, unioned. Unlike mitred sections this can't overshoot on the
                // inside of a bend when the width exceeds the step length.
                for (int q = 0; q + 1 < line.Count; q++)
                {
                    var d = Vector2.Normalize(line[q + 1] - line[q]);
                    if (float.IsNaN(d.X)) continue;
                    var nq = new Vector2(-d.Y, d.X);
                    if (q == 0 && capStart && startCapR > 0.5f) { AddPathLevel(Z(from), Cap(line[1], -d, widths[1], startDepth, startCapR, path.CornerAt(0))); continue; }
                    if (q == line.Count - 2 && capEnd && endCapR > 0.5f) { AddPathLevel(Z(from), Cap(line[q], d, widths[q], endDepth, endCapR, path.CornerAt(path.Points.Count - 1))); continue; }
                    AddPathLevel(Z(from), Poly2.Ccw(new[] { line[q] - nq * widths[q], line[q + 1] - nq * widths[q + 1], line[q + 1] + nq * widths[q + 1], line[q] + nq * widths[q] }));
                }
                for (int q = 1; q + 1 < line.Count; q++)
                    if (JointFill(line[q - 1], line[q], line[q + 1], widths[q], corners[q]) is { } fill) AddPathLevel(Z(from), fill);
            }

            void EmitRampRun(int from, int to)
            {
                var pts = new List<Vector2>();
                var widths = new List<float>();
                var corners = new List<CornerStyle>();
                for (int q = from; q <= to; q++) { pts.Add(P(q)); widths.Add(HW(q)); corners.Add(Corner(q)); }
                // Bevelled / rounded joints add extra edge points; src maps each back to its sample.
                var (left, right, src) = RibbonEdges(pts, widths, corners);
                var center = src.Select(k => pts[k]).ToList();
                var zs = src.Select(k => Z(from + k)).ToList();
                var zl = new List<float>(zs); var zr = new List<float>(zs);
                RemoveLoops(left, zl, center);
                RemoveLoops(right, zr, center);
                int group = rampCenters.Count;
                var centerline = new List<(Vector2, Vector2, float, float)>();
                rampCenters.Add(centerline);
                for (int q = 0; q + 1 < center.Count; q++)
                {
                    var section = Poly2.Ccw(new[] { right[q], right[q + 1], left[q + 1], left[q] });
                    footprint.Add(section);
                    centerline.Add((center[q], center[q + 1], zs[q], zs[q + 1]));
                    surfaces.Add(new Surface(new PathsD { section }, MathF.Min(zs[q], zs[q + 1]), MathF.Max(zs[q], zs[q + 1]), group));
                    rampSections.Add((section, MathF.Min(zs[q], zs[q + 1]), MathF.Max(zs[q], zs[q + 1])));
                    if (ceil == null) openRampSections.Add(rampSections[^1]);
                    float run = Vector2.Distance(center[q], center[q + 1]);
                    if (run > 0.01f && MathF.Abs(zs[q + 1] - zs[q]) / run > 0.7f) steep = true;
                }
                ramps.Add((left, right, zl, zr));
                foreach (var z in zs) { minZ = MathF.Min(minZ, z); maxZ = MathF.Max(maxZ, z); }

                // Roof over the ramp: the same ribbon widened over the walls, one clearance up.
                if (ceil is float c)
                {
                    var (cl, cr, csrc) = RibbonEdges(pts, widths.Select(w => w + s.WallThickness).ToList(), corners);
                    var cc = csrc.Select(k => pts[k]).ToList();
                    var czs = csrc.Select(k => Z(from + k) + c).ToList();
                    var czl = new List<float>(czs); var czr = new List<float>(czs);
                    RemoveLoops(cl, czl, cc);
                    RemoveLoops(cr, czr, cc);
                    for (int q = 0; q + 1 < cc.Count; q++)
                    {
                        var section = Poly2.Ccw(new[] { cr[q], cr[q + 1], cl[q + 1], cl[q] });
                        ceilRampSections.Add((section, MathF.Min(czs[q], czs[q + 1]), MathF.Max(czs[q], czs[q + 1])));
                        ceilCover.Add(section);
                    }
                    ceilRamps.Add((cl, cr, czl.Select(z => z + thick).ToList(), czr.Select(z => z + thick).ToList()));
                }
            }
        }

        // ---- Floor regions join the level at their elevation.
        foreach (var r in doc.Regions.Where(r => r.Kind == RegionKind.Floor && r.Points.Count >= 3))
        {
            AddLevel(r.Elevation, Poly2.Ccw(r.Points.Select(p => new Vector2(p.X, p.Y))));
            AddOpenFloor(r.Elevation, Poly2.Ccw(r.Points.Select(p => new Vector2(p.X, p.Y))));
            if (anyOpenSide)
                foreach (var k in Poly2.Inflate(new PathsD { Poly2.Ccw(r.Points.Select(p => new Vector2(p.X, p.Y))) }, s.WallThickness))
                    wallKeeps.Add((k, r.Elevation, r.Elevation));
            for (int i = 0; i < r.Points.Count; i++)
            {
                var a = r.Points[i]; var b = r.Points[(i + 1) % r.Points.Count];
                wallRefs.Add(new WallRef(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), r.Elevation, r.Elevation, 0, 0, s.WallHeight, s.WallHeight, s.WallHeight, s.WallHeight));
            }
        }

        var pits = doc.Regions.Where(r => r.Kind == RegionKind.Pit && r.Points.Count >= 3).ToList();

        // ---- Flat floors, one mesh per connected shape.
        int n = 0;
        foreach (var (z, polys) in levels)
        {
            // Closing seals hairline cracks where neighbouring pieces (segment quads, corner fills) miss each
            // other by float error; left open they become zero-width slits in the floor and spikes in the walls.
            var area = Poly2.Close(Poly2.Union(polys));
            var cut = new PathsD();
            // Where a ramp passes within player height of this floor, the ramp is the floor: cut it out.
            // Otherwise end caps / branch ends that reach over a ramp leave flat slabs hanging above or
            // poking through its slope. Floors further above/below (e.g. a corridor under a ramp) are untouched.
            foreach (var (quad, zMin, zMax) in rampSections)
                if (z >= zMin - RampClearance && z <= zMax + RampClearance) cut.Add(quad);
            foreach (var pit in pits.Where(p => MathF.Abs(p.Elevation - z) < 1f))
                cut.Add(Poly2.Ccw(pit.Points.Select(p => new Vector2(p.X, p.Y))));
            if (cut.Count > 0) area = Poly2.Difference(area, cut);
            surfaces.Add(new Surface(area, z, z, -1));

            foreach (var shape in Poly2.ToShapes(area))
            {
                var m = new PolyMesh();
                Prism(m, shape, _ => z, _ => z - thick, s.FloorMaterial, s.FloorMaterial);
                map.Meshes.Add(new BuiltMesh { Name = $"floor_{z}_{n++}", Kind = MeshKind.Floor, Mesh = m });
            }
        }

        // Flat floors on top of each other with less than headroom between them: nobody fits in between.
        var flats = surfaces.Where(sf => sf.Ramp < 0).ToList();
        for (int i = 0; i < flats.Count; i++)
            for (int j = i + 1; j < flats.Count; j++)
            {
                float gap = MathF.Abs(flats[j].ZMin - flats[i].ZMin);
                if (gap >= Headroom(thick) || !flats[i].Box.Touches(flats[j].Box, 0)) continue;
                if (Poly2.Intersect(flats[i].Area, flats[j].Area).Sum(p => Math.Abs(Clipper.Area(p))) > 256)
                    map.Warnings.Add($"Floors at {flats[i].ZMin} and {flats[j].ZMin} overlap with only {gap}u between them; stacked levels need {Headroom(thick)}u.");
            }

        // ---- Ramps: continuous sloped ribbons (straight or curved).
        n = 0;
        foreach (var (left, right, zl, zr) in ramps)
        {
            var m = new PolyMesh();
            Ribbon(m, left, right, zl, zr, thick, s.FloorMaterial);
            map.Meshes.Add(new BuiltMesh { Name = $"ramp_{n++}", Kind = MeshKind.Ramp, Mesh = m });
        }
        if (steep) map.Warnings.Add("A ramp is steeper than ~35 degrees; players may slide.");

        // ---- Roofs of covered paths: flat levels widened over the walls, then the ramp ribbons.
        n = 0;
        foreach (var (z, pieces) in ceilLevels)
        {
            var area = Poly2.Inflate(Poly2.Close(Poly2.Union(new PathsD(pieces.Select(p => p.poly)))), s.WallThickness);
            var cut = new PathsD();
            foreach (int f in pieces.Select(p => p.floor).Distinct())
            {
                if (openFloor.TryGetValue(f, out var open)) cut.AddRange(open);
                foreach (var (quad, zMin, zMax) in openRampSections)
                    if (f >= zMin - RampClearance && f <= zMax + RampClearance) cut.Add(quad);
            }
            if (cut.Count > 0) area = Poly2.Open(Poly2.Difference(area, Poly2.Union(cut)));
            ceilCover.AddRange(area);
            // Where a covered ramp starts, its own sloped roof takes over (as with floors).
            cut = new PathsD();
            foreach (var (quad, zMin, zMax) in ceilRampSections)
                if (z >= zMin - RampClearance && z <= zMax + RampClearance) cut.Add(quad);
            if (cut.Count > 0) area = Poly2.Difference(area, cut);
            foreach (var shape in Poly2.ToShapes(area))
            {
                var m = new PolyMesh();
                Prism(m, shape, _ => z + thick, _ => z, s.CeilingMaterial, s.CeilingMaterial);
                map.Meshes.Add(new BuiltMesh { Name = $"ceiling_{z}_{n++}", Kind = MeshKind.Ceiling, Mesh = m });
            }
            roofTop = MathF.Max(roofTop, z + thick);
        }
        foreach (var (left, right, zl, zr) in ceilRamps)
        {
            var m = new PolyMesh();
            Ribbon(m, left, right, zl, zr, thick, s.CeilingMaterial);
            map.Meshes.Add(new BuiltMesh { Name = $"ceiling_ramp_{n++}", Kind = MeshKind.Ceiling, Mesh = m });
            roofTop = MathF.Max(roofTop, MathF.Max(zl.Max(), zr.Max()));
        }

        // ---- Ceiling regions: a slab over the floor at their elevation, reaching over the walls around it.
        var ceilRegions = new List<(PathsD area, float underside)>();
        foreach (var r in doc.Regions.Where(r => r.Kind == RegionKind.Ceiling && r.Points.Count >= 3))
        {
            float under = r.Elevation + MathF.Max(1f, r.Height);
            if (r.Height < PlayerHeight)
                map.Warnings.Add($"Ceiling at elevation {r.Elevation} is only {r.Height}u above the floor; players need {PlayerHeight}u to stand.");
            var area = Poly2.Inflate(new PathsD { Poly2.Ccw(r.Points.Select(p => new Vector2(p.X, p.Y))) }, s.WallThickness);
            ceilRegions.Add((area, under));
            ceilCover.AddRange(area);
            foreach (var shape in Poly2.ToShapes(area))
            {
                var m = new PolyMesh();
                Prism(m, shape, _ => under + thick, _ => under, s.CeilingMaterial, s.CeilingMaterial);
                map.Meshes.Add(new BuiltMesh { Name = $"ceiling_{n++}", Kind = MeshKind.Ceiling, Mesh = m });
            }
            roofTop = MathF.Max(roofTop, under + thick);
        }

        // ---- Pits: optional bottom + liner walls hanging under the floor.
        n = 0;
        foreach (var pit in pits.Where(p => p.Height > 0))
        {
            var poly = new PathsD { Poly2.Ccw(pit.Points.Select(p => new Vector2(p.X, p.Y))) };
            var outer = Poly2.Inflate(poly, s.WallThickness);
            float bottom = pit.Elevation - pit.Height;
            foreach (var shape in Poly2.ToShapes(outer))
            {
                var m = new PolyMesh();
                Prism(m, shape, _ => bottom, _ => bottom - thick, s.FloorMaterial, s.FloorMaterial);
                map.Meshes.Add(new BuiltMesh { Name = $"pit_bottom_{n}", Kind = MeshKind.PitBottom, Mesh = m });
            }
            if (pit.Height > thick)
                foreach (var shape in Poly2.ToShapes(Poly2.Close(Poly2.Difference(outer, poly))))
                {
                    var m = new PolyMesh();
                    Prism(m, shape, _ => pit.Elevation - thick, _ => bottom, s.WallMaterial, s.WallMaterial);
                    map.Meshes.Add(new BuiltMesh { Name = $"pit_liner_{n}", Kind = MeshKind.PitLiner, Mesh = m });
                }
            minZ = MathF.Min(minZ, bottom);
            n++;
        }

        // ---- Obstacles.
        n = 0;
        foreach (var r in doc.Regions.Where(r => r.Kind == RegionKind.Obstacle && r.Points.Count >= 3))
        {
            float top = r.Elevation + MathF.Max(1f, r.Height);
            foreach (var shape in Poly2.ToShapes(new PathsD { Poly2.Ccw(r.Points.Select(p => new Vector2(p.X, p.Y))) }))
            {
                var m = new PolyMesh();
                Prism(m, shape, _ => top, _ => r.Elevation - thick, s.ObstacleMaterial, s.ObstacleMaterial);
                map.Meshes.Add(new BuiltMesh { Name = $"obstacle_{n++}", Kind = MeshKind.Obstacle, Mesh = m });
            }
            if (r.Height < s.ObstacleUnjumpableHeight)
                map.Warnings.Add($"Obstacle at elevation {r.Elevation} is {r.Height}u tall - players can jump onto it (< {s.ObstacleUnjumpableHeight}).");
        }

        // ---- Perimeter walls around everything walkable.
        if (s.PerimeterWalls && footprint.Count > 0)
        {
            // Curves flatten into many tiny segments; simplifying keeps walls light without visible change.
            // Ramer-Douglas-Peucker keeps every point within the tolerance of the true outline. (Clipper's
            // SimplifyPaths drops points one by one against their current neighbours, so along a dense curve
            // the error adds up and the walls could cut well into the floor.)
            var all = Clipper.RamerDouglasPeucker(Poly2.Close(Poly2.Union(footprint)), 0.5);
            // Closing merges places where the ring touches itself at a single corner (a pinch), which would
            // otherwise give vertical edges shared by four faces - not representable in Hammer's mesh format.
            var ring = Poly2.Close(Poly2.Difference(Poly2.Inflate(all, s.WallThickness), all));
            // Cuts for open path sides, optionally only those of paths at a floor height near [zMin, zMax].
            PathsD Cuts(float zMin = float.MinValue, float zMax = float.MaxValue)
            {
                bool Near((PathD poly, float lo, float hi) c) => c.lo - zMax < Headroom(thick) && zMin - c.hi < Headroom(thick);
                var cuts = Poly2.Union(new PathsD(wallCuts.Where(Near).Select(c => c.poly)));
                var keeps = wallKeeps.Where(Near).Select(c => c.poly).ToList();
                return keeps.Count > 0 && cuts.Count > 0 ? Poly2.Difference(cuts, Poly2.Union(new PathsD(keeps))) : cuts;
            }
            if (wallCuts.Count > 0)
            {
                // Opening first drops hairline slivers where a cut lands almost on the floor edge.
                ring = Poly2.Close(Poly2.Open(Poly2.Difference(ring, Cuts())));
            }
            float wallBottom = minZ - thick;
            // The floor edge a wall vertex belongs to: the nearest one, measured from the floor's side edge.
            // With nearZ, only floor edges within headroom of that height count (the level a stacked wall belongs to).
            (WallRef w, float u, bool left, float score) Nearest(Vector2 p, bool roofedOnly, float? nearZ = null)
            {
                (WallRef, float, bool, float) best = (default, 0f, false, float.MaxValue);
                foreach (var w in wallRefs)
                {
                    if (roofedOnly && float.IsNaN(w.Ceil)) continue;
                    var ab = w.B - w.A; float len2 = ab.LengthSquared();
                    float u = len2 < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - w.A, ab) / len2, 0f, 1f);
                    if (nearZ is float nz && MathF.Abs(w.ZA + (w.ZB - w.ZA) * u - nz) >= Headroom(thick)) continue;
                    float score = Vector2.Distance(p, w.A + ab * u) - (w.HwA + (w.HwB - w.HwA) * u);
                    if (score < best.Item4)
                        best = (w, u, (ab.X * (p.Y - w.A.Y) - ab.Y * (p.X - w.A.X)) >= 0, score);
                }
                return best;
            }
            // Each wall vertex takes its height from the nearest floor edge: that floor's height plus its wall
            // height (scaled by the path's wall profile). Walls therefore follow ramps and vary along paths.
            float WallTopAt(Vector2 p, float? nearZ)
            {
                var (w, u, left, score) = Nearest(p, false, nearZ);
                if (score == float.MaxValue && nearZ != null) (w, u, left, score) = Nearest(p, false);
                float top = maxZ + s.WallHeight;
                if (score < float.MaxValue)
                {
                    float wa = left ? w.LeftA : w.RightA, wb = left ? w.LeftB : w.RightB;
                    top = w.ZA + (w.ZB - w.ZA) * u + wa + (wb - wa) * u;
                }
                return MathF.Max(top, wallBottom + 1f);
            }
            // Under a roof the wall meets the roof's top instead (raised or cut down to it), so there's no gap and
            // nothing pokes through. Covered paths roof their own edges; a ceiling region roofs whatever it covers
            // below its underside. The lowest roof over the vertex wins.
            float RoofedTopAt(Vector2 p, float? nearZ)
            {
                float floorZ = float.MinValue, roof = float.MaxValue;
                var near = Nearest(p, false, nearZ);
                if (near.score < float.MaxValue) floorZ = near.w.ZA + (near.w.ZB - near.w.ZA) * near.u;
                else if (nearZ is float nz) floorZ = nz;
                var (w, u, _, score) = Nearest(p, true, nearZ);
                if (score <= s.WallThickness + 1f) roof = w.ZA + (w.ZB - w.ZA) * u + w.Ceil + thick;
                var pt = new PointD(p.X, p.Y);
                foreach (var (area, under) in ceilRegions)
                    if (under > floorZ && under + thick < roof && area.Any(a => Clipper.PointInPolygon(pt, a) != PointInPolygonResult.IsOutside))
                        roof = under + thick;
                return roof < float.MaxValue ? MathF.Max(roof, wallBottom + 1f) : WallTopAt(p, nearZ);
            }
            var cover = ceilCover.Count > 0 ? Poly2.Union(ceilCover) : null;
            var pieces = new List<(PathsD area, Func<Vector2, float> top, Func<Vector2, float> bottom)>();
            // Split a piece of wall where roofs end, so each part has vertices right at the roof's edge.
            void AddWall(PathsD area, Func<Vector2, float?> nearZ, Func<Vector2, float> bottom, Func<Vector2, float> cap = null)
            {
                Func<Vector2, float> Capped(Func<Vector2, float> top) =>
                    cap == null ? top : p => MathF.Max(MathF.Min(top(p), cap(p)), bottom(p) + 1f);
                if (cover != null)
                {
                    pieces.Add((Poly2.Open(Poly2.Intersect(area, cover)), Capped(p => RoofedTopAt(p, nearZ(p))), bottom));
                    pieces.Add((Poly2.Open(Poly2.Difference(area, cover)), Capped(p => WallTopAt(p, nearZ(p))), bottom));
                }
                else pieces.Add((area, Capped(p => WallTopAt(p, nearZ(p))), bottom));
            }

            // ---- Stacked levels (overpasses, balconies, ledges). Two walkable surfaces are joined when their
            // heights are within headroom of each other (a step, a ramp meeting a floor): players walk from one to
            // the other, so no wall between them. Where a surface meets one that isn't joined (a bridge over a
            // corridor, the corridor under it), the outer ring above has no wall: it's inside the walkable
            // outline. Those walls are added per surface: a railing along the upper one's edge (from its floor
            // up), and the lower one's walls up to the underside of what's above.
            var stacked = StackedContacts(surfaces, s.WallThickness, thick);
            float SurfaceZ(Surface sf, Vector2 p) => sf.Ramp < 0 ? sf.ZMin : RampZ(rampCenters[sf.Ramp], p);
            if (stacked.Count > 0)
            {
                // The outer ring near a stacked level takes its height from that level's floor edges only,
                // highest level first, so a bridge's walls don't slope down to the corridor below at a corner.
                var claimed = new PathsD();
                foreach (var g in stacked.Keys.Select(i => surfaces[i]).GroupBy(sf => sf.Group).OrderByDescending(g => g.Max(sf => sf.ZMax)))
                {
                    var near = Poly2.Inflate(Poly2.Union(new PathsD(g.SelectMany(sf => sf.Area))), s.WallThickness + 1);
                    var part = Poly2.Open(Poly2.Difference(Poly2.Intersect(ring, near), claimed));
                    if (part.Count == 0) continue;
                    claimed = Poly2.Union(new PathsD(claimed.Concat(part)));
                    var sf0 = g.First();
                    AddWall(part, p => SurfaceZ(sf0, p), _ => wallBottom);
                }
                ring = Poly2.Open(Poly2.Difference(ring, claimed));

                // Walls between stacked surfaces, grouped per level (flat height or ramp run).
                var inner = new Dictionary<long, PathsD>();
                foreach (var (i, others) in stacked)
                {
                    var sf = surfaces[i];
                    var band = Poly2.Close(Poly2.Difference(Poly2.Inflate(sf.Area, s.WallThickness), sf.Area));
                    var w = Poly2.Intersect(band, Poly2.Union(new PathsD(others.SelectMany(o => surfaces[o].Area))));
                    var joined = new PathsD();
                    for (int j = 0; j < surfaces.Count; j++)
                        if (j != i && Joined(sf, surfaces[j], thick) && sf.Box.Touches(surfaces[j].Box, s.WallThickness + 1)) joined.AddRange(surfaces[j].Area);
                    if (joined.Count > 0) w = Poly2.Difference(w, Poly2.Union(joined));
                    if (wallCuts.Count > 0) w = Poly2.Difference(w, Cuts(sf.ZMin, sf.ZMax));
                    if (!inner.TryGetValue(sf.Group, out var acc)) inner[sf.Group] = acc = new PathsD();
                    acc.AddRange(w);
                }
                foreach (var (group, area) in inner)
                {
                    var sf0 = surfaces.First(sf => sf.Group == group);
                    float Floor(Vector2 p) => SurfaceZ(sf0, p);
                    // Never through a surface overhead: stop under its floor slab.
                    float Overhead(Vector2 p)
                    {
                        float z = Floor(p), cap = float.MaxValue;
                        var pt = new PointD(p.X, p.Y);
                        foreach (var o in surfaces)
                            if (o.ZMin - z >= Headroom(thick) && o.Box.Contains(p, 1f) && o.Area.Any(a => Clipper.PointInPolygon(pt, a) != PointInPolygonResult.IsOutside))
                                cap = MathF.Min(cap, SurfaceZ(o, p) - thick);
                        return cap;
                    }
                    AddWall(Poly2.Open(Poly2.Close(Poly2.Union(area))), p => Floor(p), p => Floor(p) - thick, Overhead);
                }
            }
            AddWall(ring, _ => null, _ => wallBottom);
            n = 0;
            foreach (var (area, top, bottom) in pieces)
                foreach (var shape in Poly2.ToShapes(area))
                {
                    var m = new PolyMesh();
                    Prism(m, shape, top, bottom, s.WallMaterial, s.WallMaterial);
                    if (m.Faces.Count > 0) map.Meshes.Add(new BuiltMesh { Name = $"wall_{n++}", Kind = MeshKind.Wall, Mesh = m });
                }
        }

        // ---- Entities.
        foreach (var sp in doc.Spawns)
        {
            map.Entities.Add(new BuiltEntity
            {
                ClassName = sp.Team == Team.T ? "info_player_terrorist" : "info_player_counterterrorist",
                // Spawns are stored on the floor; lift them like Hammer does so the player hull isn't
                // flush with the floor (CS2 skips spawns whose hull is blocked, which reads as "team full").
                Origin = new Vector3(sp.Position.X, sp.Position.Y, sp.Position.Z + SpawnLift),
                Angles = new Vector3(0, sp.Yaw, 0),
                Properties = { ["enabled"] = "1", ["priority"] = "0" },
            });
        }
        // Hammer requires an info_player_start (used for "Run map" / listen-server testing).
        // Put it on the first spawn, else the first path point, else the origin.
        var start = doc.Spawns.FirstOrDefault();
        var startPos = start != null ? start.Position
            : doc.Paths.FirstOrDefault(p => p.Points.Count > 0)?.Points[0] ?? new Vec3(0, 0, 0);
        map.Entities.Add(new BuiltEntity
        {
            ClassName = "info_player_start",
            Origin = new Vector3(startPos.X, startPos.Y, startPos.Z + SpawnLift),
            Angles = new Vector3(0, start?.Yaw ?? 0, 0),
            Properties = { ["vscripts"] = "", ["StartDisabled"] = "0", ["spawnflags"] = "0" },
        });

        for (int i = 0; i < doc.Spawns.Count; i++)
            for (int j = i + 1; j < doc.Spawns.Count; j++)
            {
                var a = doc.Spawns[i].Position; var b = doc.Spawns[j].Position;
                if (MathF.Abs(a.X - b.X) < MinSpawnSpacing && MathF.Abs(a.Y - b.Y) < MinSpawnSpacing && MathF.Abs(a.Z - b.Z) < 72f)
                    map.Warnings.Add($"Spawns {i} and {j} are closer than {MinSpawnSpacing}u; CS2 may treat them as blocked.");
            }

        if (!doc.Spawns.Any(sp => sp.Team == Team.T)) map.Warnings.Add("No T spawns.");
        if (!doc.Spawns.Any(sp => sp.Team == Team.CT)) map.Warnings.Add("No CT spawns.");

        float lightZ = MathF.Max(maxZ > float.MinValue ? maxZ : 0, roofTop) + 1024;
        map.Entities.Add(new BuiltEntity
        {
            ClassName = "light_environment",
            Origin = new Vector3(0, 0, lightZ),
            Angles = new Vector3(50, 35, 0),
            Properties = { ["color"] = "255 244 224", ["brightness"] = "2", ["skycolor"] = "160 190 230", ["skyintensity"] = "1", ["castshadows"] = "1" },
        });

        return map;
    }

    /// <summary>Vertical gap two walkable surfaces need to be separate levels: a standing player plus a floor slab.
    /// Closer than this they're the same level (a step, a ramp meeting a floor) and no wall goes between them.</summary>
    static float Headroom(float thick) => PlayerHeight + thick;

    readonly record struct Box2(float MinX, float MinY, float MaxX, float MaxY)
    {
        public static Box2 Of(PathsD area)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var path in area)
                foreach (var v in path) { x0 = MathF.Min(x0, (float)v.x); y0 = MathF.Min(y0, (float)v.y); x1 = MathF.Max(x1, (float)v.x); y1 = MathF.Max(y1, (float)v.y); }
            return new Box2(x0, y0, x1, y1);
        }
        public bool Touches(Box2 o, float margin) => MinX - margin <= o.MaxX && o.MinX - margin <= MaxX && MinY - margin <= o.MaxY && o.MinY - margin <= MaxY;
        public bool Contains(Vector2 p, float margin) => p.X >= MinX - margin && p.X <= MaxX + margin && p.Y >= MinY - margin && p.Y <= MaxY + margin;
    }

    /// <summary>A walkable surface with its height range: a flat level (Ramp = -1) or one section of a ramp run
    /// (Ramp = the run's index). Group is shared by all of one level or one run: the walls are built per group.</summary>
    sealed class Surface
    {
        public readonly PathsD Area; public readonly float ZMin, ZMax; public readonly int Ramp; public readonly Box2 Box;
        public Surface(PathsD area, float zMin, float zMax, int ramp) { Area = area; ZMin = zMin; ZMax = zMax; Ramp = ramp; Box = Box2.Of(area); }
        public long Group => Ramp >= 0 ? 2L * Ramp + 1 : 2L * (long)MathF.Round(ZMin);
    }

    static bool Joined(Surface a, Surface b, float thick) => b.ZMin - a.ZMax < Headroom(thick) && a.ZMin - b.ZMax < Headroom(thick);

    /// <summary>For each surface whose wall band (out to the wall thickness) reaches into a surface it isn't joined
    /// to, the indices of those surfaces. Empty for a single-level map.</summary>
    static Dictionary<int, List<int>> StackedContacts(List<Surface> surfaces, float wallThickness, float thick)
    {
        var res = new Dictionary<int, List<int>>();
        for (int i = 0; i < surfaces.Count; i++)
        {
            PathsD band = null;
            for (int j = 0; j < surfaces.Count; j++)
            {
                if (i == j || Joined(surfaces[i], surfaces[j], thick) || !surfaces[i].Box.Touches(surfaces[j].Box, wallThickness + 1)) continue;
                band ??= Poly2.Difference(Poly2.Inflate(surfaces[i].Area, wallThickness), surfaces[i].Area);
                if (Poly2.Intersect(band, surfaces[j].Area).Sum(p => Math.Abs(Clipper.Area(p))) < 1) continue;
                if (!res.TryGetValue(i, out var list)) res[i] = list = new List<int>();
                list.Add(j);
            }
        }
        return res;
    }

    /// <summary>Height of a ramp run at p: projected onto the nearest step of its centreline.</summary>
    static float RampZ(List<(Vector2 a, Vector2 b, float za, float zb)> line, Vector2 p)
    {
        float best = float.MaxValue, z = line.Count > 0 ? line[0].za : 0;
        foreach (var (a, b, za, zb) in line)
        {
            var ab = b - a; float len2 = ab.LengthSquared();
            float u = len2 < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
            float d = Vector2.DistanceSquared(p, a + ab * u);
            if (d < best) { best = d; z = za + (zb - za) * u; }
        }
        return z;
    }

    const float MiterLimit = 4f;     // sharp corners whose miter would reach further than this many half-widths get bevelled
    const float RoundStepDeg = 15f;  // arc resolution of rounded corners

    /// <summary>How far a free end's corners are cut back, limited by the end depth. Round: up to the half-width,
    /// so the default depth (half the width) gives a semicircle. Bevel: up to half the half-width, so the end
    /// keeps a flat face instead of coming to a point. 0 for a square (sharp) end.</summary>
    static float CapRadius(CornerStyle style, float hw, float depth) => style switch
    {
        CornerStyle.Round => MathF.Min(hw, depth),
        CornerStyle.Bevel => MathF.Min(hw * 0.5f, depth),
        _ => 0f,
    };

    /// <summary>End cap from the end point p running out along dir by depth, hw to each side, with both outer
    /// corners bevelled or rounded by r. Its base edge (p ± normal * hw) matches the corridor's.</summary>
    static PathD Cap(Vector2 p, Vector2 dir, float hw, float depth, float r, CornerStyle style)
    {
        var n = new Vector2(-dir.Y, dir.X);
        var pts = new List<Vector2> { p - n * hw, p - n * hw + dir * (depth - r) };
        void Corner(Vector2 center, Vector2 from, Vector2 to)
        {
            if (style == CornerStyle.Round)
            {
                int steps = Math.Max(2, (int)MathF.Ceiling(90f / RoundStepDeg));
                for (int k = 1; k < steps; k++)
                {
                    float a = MathF.PI / 2 * k / steps;
                    pts.Add(center + (from * MathF.Cos(a) + to * MathF.Sin(a)) * r);
                }
            }
        }
        Corner(p - n * (hw - r) + dir * (depth - r), -n, dir);
        pts.Add(p - n * (hw - r) + dir * depth);
        if (hw - r > 0.01f) pts.Add(p + n * (hw - r) + dir * depth);
        Corner(p + n * (hw - r) + dir * (depth - r), dir, n);
        pts.Add(p + n * hw + dir * (depth - r));
        pts.Add(p + n * hw);
        return Poly2.Ccw(pts.Where((v, i) => i == 0 || Vector2.DistanceSquared(v, pts[i - 1]) > 1e-4f));
    }

    /// <summary>Points of the outer edge around a joint, from the incoming segment's offset (p + na) to the
    /// outgoing one's (p + nb), for the given style. na/nb are the outward normals scaled by the half-width.</summary>
    static List<Vector2> CornerArc(Vector2 p, Vector2 na, Vector2 nb, float hw, CornerStyle style)
    {
        var pts = new List<Vector2> { p + na };
        var bis = Vector2.Normalize(na + nb);
        float cos = float.IsNaN(bis.X) ? 0f : Vector2.Dot(bis, na) / hw; // cos of half the turn
        if (style == CornerStyle.Sharp && cos > 1f / MiterLimit) pts.Add(p + bis * (hw / cos));
        else if (style == CornerStyle.Round)
        {
            float a0 = MathF.Atan2(na.Y, na.X), a1 = MathF.Atan2(nb.Y, nb.X);
            float delta = a1 - a0;
            while (delta > MathF.PI) delta -= 2 * MathF.PI;
            while (delta < -MathF.PI) delta += 2 * MathF.PI;
            int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(delta) / (RoundStepDeg * MathF.PI / 180f)));
            for (int k = 1; k < steps; k++)
            {
                float a = a0 + delta * k / steps;
                pts.Add(p + new Vector2(MathF.Cos(a), MathF.Sin(a)) * hw);
            }
        }
        pts.Add(p + nb);
        return pts;
    }

    /// <summary>Polygon filling the outside of the joint at b (between segments a-b and b-c) for corridors whose
    /// sections end square at b; null when the path runs straight on.</summary>
    static PathD JointFill(Vector2 a, Vector2 b, Vector2 c, float hw, CornerStyle style)
    {
        var da = Vector2.Normalize(b - a); var db = Vector2.Normalize(c - b);
        if (float.IsNaN(da.X) || float.IsNaN(db.X)) return null;
        float cr = da.X * db.Y - da.Y * db.X;
        if (MathF.Abs(cr) < 1e-4f && Vector2.Dot(da, db) > 0) return null;
        float side = cr > 0 ? -1f : 1f; // outside of a left turn is the right
        var na = new Vector2(-da.Y, da.X) * hw * side; var nb = new Vector2(-db.Y, db.X) * hw * side;
        var poly = CornerArc(b, na, nb, hw, style);
        poly.Insert(0, b);
        return Poly2.Ccw(poly);
    }

    /// <summary>Left/right edges of a centreline offset by hw. Joints use their corner style on the outside of
    /// the bend (sharp = mitred, bevel, round: these add edge points, while the inside repeats its miter point);
    /// src gives the centreline index of every edge point.</summary>
    static (List<Vector2> left, List<Vector2> right, List<int> src) RibbonEdges(List<Vector2> c, List<float> hws, List<CornerStyle> corners)
    {
        var left = new List<Vector2>(c.Count);
        var right = new List<Vector2>(c.Count);
        var src = new List<int>(c.Count);
        Vector2 Normal(int seg)
        {
            var d = Vector2.Normalize(c[seg + 1] - c[seg]);
            return new Vector2(-d.Y, d.X);
        }
        for (int i = 0; i < c.Count; i++)
        {
            float hw = hws[i];
            if (i == 0 || i == c.Count - 1)
            {
                var n = Normal(i == 0 ? 0 : i - 1);
                left.Add(c[i] + n * hw); right.Add(c[i] - n * hw); src.Add(i);
                continue;
            }
            var n0 = Normal(i - 1); var n1 = Normal(i);
            var bis = Vector2.Normalize(n0 + n1);
            float cos = float.IsNaN(bis.X) ? 0f : Vector2.Dot(bis, n0);
            if (float.IsNaN(bis.X)) bis = n0;
            // Inside of the bend: the true miter point (overshoot is cleaned up by RemoveLoops).
            var inner = bis * (hw / MathF.Max(0.1f, cos));
            float cr = n0.X * n1.Y - n0.Y * n1.X; // same sign as the turn
            if (MathF.Abs(cr) < 1e-4f && cos > 0)
            {
                left.Add(c[i] + n0 * hw); right.Add(c[i] - n0 * hw); src.Add(i);
                continue;
            }
            bool leftTurn = cr > 0;
            // Outer side is the right of a left turn: normals point left, so flip them.
            var arc = leftTurn ? CornerArc(c[i], -n0 * hw, -n1 * hw, hw, corners[i]) : CornerArc(c[i], n0 * hw, n1 * hw, hw, corners[i]);
            if (corners[i] == CornerStyle.Sharp && arc.Count == 3) arc = new List<Vector2> { arc[1] }; // one miter point
            foreach (var o in arc)
            {
                if (leftTurn) { left.Add(c[i] + inner); right.Add(o); }
                else { left.Add(o); right.Add(c[i] - inner); }
                src.Add(i);
            }
        }
        return (left, right, src);
    }

    /// <summary>Removes self-intersections from one edge of a ribbon. On the inside of a bend that is tighter
    /// than the half-width (a sharp corner between short segments, a tight curve) the mitred offset points
    /// overshoot each other and the edge crosses itself; everything between the crossing collapses onto the
    /// crossing point, which is where the real corridor edge turns. Heights collapse with it. An edge step that
    /// still runs backwards against the centreline (half-width longer than the segments on both sides of a
    /// corner, so nothing crosses) is collapsed onto its start.</summary>
    static void RemoveLoops(List<Vector2> s, List<float> z, List<Vector2> c)
    {
        int n = s.Count;
        for (int a = 0; a + 2 < n; a++)
            for (int b = n - 2; b >= a + 2; b--)
            {
                if (!SegmentIntersect(s[a], s[a + 1], s[b], s[b + 1], out float ua, out float ub)) continue;
                var x = s[a] + (s[a + 1] - s[a]) * ua;
                float zx = 0.5f * (z[a] + (z[a + 1] - z[a]) * ua + z[b] + (z[b + 1] - z[b]) * ub);
                for (int k = a + 1; k <= b; k++) { s[k] = x; z[k] = zx; }
                a = b - 1;
                break;
            }
        for (int a = 0; a + 1 < n; a++)
            if (Vector2.Dot(s[a + 1] - s[a], c[a + 1] - c[a]) < 0) { s[a + 1] = s[a]; z[a + 1] = z[a]; }
    }

    static bool SegmentIntersect(Vector2 p, Vector2 p2, Vector2 q, Vector2 q2, out float u, out float v)
    {
        u = v = 0;
        var r = p2 - p; var sv = q2 - q;
        float den = r.X * sv.Y - r.Y * sv.X;
        if (MathF.Abs(den) < 1e-9f) return false;
        var qp = q - p;
        u = (qp.X * sv.Y - qp.Y * sv.X) / den;
        v = (qp.X * r.Y - qp.Y * r.X) / den;
        return u > 1e-4f && u < 1 - 1e-4f && v > 1e-4f && v < 1 - 1e-4f;
    }

    /// <summary>Closed slab along a ribbon: top follows the per-section heights, bottom is thick below.
    /// Non-planar top quads (curved ramps) are split into triangles so every Hammer face is planar.</summary>
    static void Ribbon(PolyMesh mesh, List<Vector2> left, List<Vector2> right, List<float> zl, List<float> zr, float thick, string material)
    {
        int n = left.Count;
        // Points are identified by index: 0..n-1 on the right edge, n..2n-1 on the left (each edge has its own heights).
        float Zk(int k) => k < n ? zr[k] : zl[k - n];
        Vector3 Tp(Vector2 p, int k) => new(p, Zk(k));
        Vector3 Bp(Vector2 p, int k) => new(p, Zk(k) - thick);

        for (int i = 0; i + 1 < n; i++)
        {
            var top = new[] { Tp(right[i], i), Tp(right[i + 1], i + 1), Tp(left[i + 1], n + i + 1), Tp(left[i], n + i) };
            var bot = new[] { Bp(right[i], i), Bp(right[i + 1], i + 1), Bp(left[i + 1], n + i + 1), Bp(left[i], n + i) };
            var ti = top.Select(mesh.AddVertex).ToArray();
            var bi = bot.Select(mesh.AddVertex).ToArray();
            if (IsPlanar(top))
            {
                mesh.AddFace(ti, material);
                mesh.AddFace(new[] { bi[3], bi[2], bi[1], bi[0] }, material);
            }
            else
            {
                mesh.AddFace(new[] { ti[0], ti[1], ti[2] }, material);
                mesh.AddFace(new[] { ti[0], ti[2], ti[3] }, material);
                mesh.AddFace(new[] { bi[2], bi[1], bi[0] }, material);
                mesh.AddFace(new[] { bi[3], bi[2], bi[0] }, material);
            }
        }

        // Outline counter-clockwise from above: along the right edge, back along the left edge.
        var outline = new List<(Vector2 p, int i)>();
        for (int i = 0; i < n; i++) outline.Add((right[i], i));
        for (int i = n - 1; i >= 0; i--) outline.Add((left[i], n + i));
        for (int k = 0; k < outline.Count; k++)
        {
            var (pa, ia) = outline[k];
            var (pb, ib) = outline[(k + 1) % outline.Count];
            mesh.AddFace(new[] { mesh.AddVertex(Bp(pa, ia)), mesh.AddVertex(Bp(pb, ib)), mesh.AddVertex(Tp(pb, ib)), mesh.AddVertex(Tp(pa, ia)) }, material);
        }
    }

    static bool IsPlanar(Vector3[] q)
    {
        var nrm = Vector3.Cross(q[1] - q[0], q[2] - q[0]);
        float len = nrm.Length();
        if (len < 1e-6f) return false;
        return MathF.Abs(Vector3.Dot(q[3] - q[0], nrm / len)) < 0.05f;
    }

    /// <summary>Extrudes a polygon-with-holes between two height functions into one closed mesh.</summary>
    public static void Prism(PolyMesh mesh, Shape2 shape, Func<Vector2, float> top, Func<Vector2, float> bottom, string capMaterial, string sideMaterial)
    {
        var faces = Triangulator.ConvexFaces(shape, out var pts, out var tris);
        if (faces.Count == 0) return;
        // A top that isn't level (e.g. walls following ramps) can't use merged n-gons: they wouldn't be planar.
        float tMin = float.MaxValue, tMax = float.MinValue;
        foreach (var p in pts) { float t = top(p); tMin = MathF.Min(tMin, t); tMax = MathF.Max(tMax, t); }
        var topFaces = tMax - tMin > 0.01f ? tris : faces;
        float bMin = float.MaxValue, bMax = float.MinValue;
        foreach (var p in pts) { float b = bottom(p); bMin = MathF.Min(bMin, b); bMax = MathF.Max(bMax, b); }
        var bottomFaces = bMax - bMin > 0.01f ? tris : faces;

        var topIdx = new Dictionary<int, int>();
        var botIdx = new Dictionary<int, int>();
        int T(int i) => topIdx.TryGetValue(i, out int v) ? v : topIdx[i] = mesh.AddVertex(new Vector3(pts[i], top(pts[i])));
        int B(int i) => botIdx.TryGetValue(i, out int v) ? v : botIdx[i] = mesh.AddVertex(new Vector3(pts[i], bottom(pts[i])));

        var edges = new HashSet<(int, int)>();
        foreach (var f in faces)
            for (int i = 0; i < f.Length; i++) edges.Add((f[i], f[(i + 1) % f.Length]));

        foreach (var f in topFaces) mesh.AddFace(f.Select(T).ToArray(), capMaterial);
        foreach (var f in bottomFaces) mesh.AddFace(f.Reverse().Select(B).ToArray(), capMaterial);

        foreach (var (a, b) in edges)
            if (!edges.Contains((b, a)))
                mesh.AddFace(new[] { B(a), B(b), T(b), T(a) }, sideMaterial);
    }
}
