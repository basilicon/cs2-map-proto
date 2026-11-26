using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Clipper2Lib;
using Cs2MapProto.Model;

namespace Cs2MapProto.Geometry;

public enum MeshKind { Floor, Ramp, Wall, Obstacle, PitBottom, PitLiner }

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

/// <summary>A floor edge segment (2D) with its height, half width and wall height at both ends.</summary>
public readonly record struct WallRef(Vector2 A, Vector2 B, float ZA, float ZB, float HwA, float HwB, float WallA, float WallB);

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
            Vector2 P(int i) => new(samples[i].P.X, samples[i].P.Y);
            float Z(int i) => samples[i].P.Z;
            bool Flat(int seg) => MathF.Abs(Z(seg + 1) - Z(seg)) < FlatEpsilon;
            float Wall(int i) => s.WallHeight * samples[i].WallScale;
            // Corner style of a sample: path points use their own; in-between samples (curve steps) are mitred,
            // which is exact for their small turns.
            CornerStyle Corner(int i) => samples[i].IsNode ? path.CornerAt(samples[i].Segment) : CornerStyle.Sharp;
            for (int i = 0; i + 1 < m; i++)
                wallRefs.Add(new WallRef(P(i), P(i + 1), Z(i), Z(i + 1), HW(i), HW(i + 1), Wall(i), Wall(i + 1)));

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
                if (JointFill(P(i - 1), P(i), P(i + 1), HW(i), Corner(i)) is { } fill) AddLevel(Z(i), fill);
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
                // Free flat ends are extended by half the width (a square end), so dead ends and T-junctions
                // overlap neatly. Extending the line itself (rather than adding a separate cap shape) keeps the
                // end in one piece with the corridor, so there's no seam or wedge gap between them.
                if (from == 0 && startDepth > 0.5f && EndDir(0, +1) is var d0 && d0 != Vector2.Zero) { line.Add(P(0) + d0 * startDepth); widths.Add(HW(0)); corners.Add(CornerStyle.Sharp); }
                for (int q = from; q <= to; q++) { line.Add(P(q)); widths.Add(HW(q)); corners.Add(Corner(q)); }
                if (to == m - 1 && endDepth > 0.5f && EndDir(m - 1, -1) is var d1 && d1 != Vector2.Zero) { line.Add(P(m - 1) + d1 * endDepth); widths.Add(HW(m - 1)); corners.Add(CornerStyle.Sharp); }
                // One trapezoid per step (square ends, each with its own end widths) plus the chosen corner shape
                // filling the outside of every joint, unioned. Unlike mitred sections this can't overshoot on the
                // inside of a bend when the width exceeds the step length.
                for (int q = 0; q + 1 < line.Count; q++)
                {
                    var d = Vector2.Normalize(line[q + 1] - line[q]);
                    if (float.IsNaN(d.X)) continue;
                    var nq = new Vector2(-d.Y, d.X);
                    AddLevel(Z(from), Poly2.Ccw(new[] { line[q] - nq * widths[q], line[q + 1] - nq * widths[q + 1], line[q + 1] + nq * widths[q + 1], line[q] + nq * widths[q] }));
                }
                for (int q = 1; q + 1 < line.Count; q++)
                    if (JointFill(line[q - 1], line[q], line[q + 1], widths[q], corners[q]) is { } fill) AddLevel(Z(from), fill);
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
                for (int q = 0; q + 1 < center.Count; q++)
                {
                    var section = Poly2.Ccw(new[] { right[q], right[q + 1], left[q + 1], left[q] });
                    footprint.Add(section);
                    rampSections.Add((section, MathF.Min(zs[q], zs[q + 1]), MathF.Max(zs[q], zs[q + 1])));
                    float run = Vector2.Distance(center[q], center[q + 1]);
                    if (run > 0.01f && MathF.Abs(zs[q + 1] - zs[q]) / run > 0.7f) steep = true;
                }
                ramps.Add((left, right, zl, zr));
                foreach (var z in zs) { minZ = MathF.Min(minZ, z); maxZ = MathF.Max(maxZ, z); }
            }
        }

        // ---- Floor regions join the level at their elevation.
        foreach (var r in doc.Regions.Where(r => r.Kind == RegionKind.Floor && r.Points.Count >= 3))
        {
            AddLevel(r.Elevation, Poly2.Ccw(r.Points.Select(p => new Vector2(p.X, p.Y))));
            for (int i = 0; i < r.Points.Count; i++)
            {
                var a = r.Points[i]; var b = r.Points[(i + 1) % r.Points.Count];
                wallRefs.Add(new WallRef(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), r.Elevation, r.Elevation, 0, 0, s.WallHeight, s.WallHeight));
            }
        }

        var pits = doc.Regions.Where(r => r.Kind == RegionKind.Pit && r.Points.Count >= 3).ToList();

        // ---- Flat floors, one mesh per connected shape.
        int n = 0;
        foreach (var (z, polys) in levels)
        {
            var area = Poly2.Union(polys);
            var cut = new PathsD();
            // Where a ramp passes within player height of this floor, the ramp is the floor: cut it out.
            // Otherwise end caps / branch ends that reach over a ramp leave flat slabs hanging above or
            // poking through its slope. Floors further above/below (e.g. a corridor under a ramp) are untouched.
            foreach (var (quad, zMin, zMax) in rampSections)
                if (z >= zMin - RampClearance && z <= zMax + RampClearance) cut.Add(quad);
            foreach (var pit in pits.Where(p => MathF.Abs(p.Elevation - z) < 1f))
                cut.Add(Poly2.Ccw(pit.Points.Select(p => new Vector2(p.X, p.Y))));
            if (cut.Count > 0) area = Poly2.Difference(area, cut);

            foreach (var shape in Poly2.ToShapes(area))
            {
                var m = new PolyMesh();
                Prism(m, shape, _ => z, _ => z - thick, s.FloorMaterial, s.FloorMaterial);
                map.Meshes.Add(new BuiltMesh { Name = $"floor_{z}_{n++}", Kind = MeshKind.Floor, Mesh = m });
            }
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
            // Curves flatten into many tiny segments; a 1u simplification keeps walls light without visible change.
            var all = Clipper.SimplifyPaths(Poly2.Union(footprint), 1.0);
            // Closing merges places where the ring touches itself at a single corner (a pinch), which would
            // otherwise give vertical edges shared by four faces - not representable in Hammer's mesh format.
            var ring = Poly2.Close(Poly2.Difference(Poly2.Inflate(all, s.WallThickness), all));
            float wallBottom = minZ - thick;
            // Each wall vertex takes its height from the nearest floor edge: that floor's height plus its wall
            // height (scaled by the path's wall profile). Walls therefore follow ramps and vary along paths.
            float WallTop(Vector2 p)
            {
                float best = float.MaxValue, top = maxZ + s.WallHeight;
                foreach (var w in wallRefs)
                {
                    var ab = w.B - w.A; float len2 = ab.LengthSquared();
                    float u = len2 < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - w.A, ab) / len2, 0f, 1f);
                    float score = Vector2.Distance(p, w.A + ab * u) - (w.HwA + (w.HwB - w.HwA) * u);
                    if (score < best)
                    {
                        best = score;
                        top = w.ZA + (w.ZB - w.ZA) * u + w.WallA + (w.WallB - w.WallA) * u;
                    }
                }
                return MathF.Max(top, wallBottom + 1f);
            }
            n = 0;
            foreach (var shape in Poly2.ToShapes(ring))
            {
                var m = new PolyMesh();
                Prism(m, shape, WallTop, _ => wallBottom, s.WallMaterial, s.WallMaterial);
                map.Meshes.Add(new BuiltMesh { Name = $"wall_{n++}", Kind = MeshKind.Wall, Mesh = m });
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

        float lightZ = (maxZ > float.MinValue ? maxZ : 0) + 1024;
        map.Entities.Add(new BuiltEntity
        {
            ClassName = "light_environment",
            Origin = new Vector3(0, 0, lightZ),
            Angles = new Vector3(50, 35, 0),
            Properties = { ["color"] = "255 244 224", ["brightness"] = "2", ["skycolor"] = "160 190 230", ["skyintensity"] = "1", ["castshadows"] = "1" },
        });

        return map;
    }

    const float MiterLimit = 4f;     // sharp corners whose miter would reach further than this many half-widths get bevelled
    const float RoundStepDeg = 15f;  // arc resolution of rounded corners

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

        var topIdx = new Dictionary<int, int>();
        var botIdx = new Dictionary<int, int>();
        int T(int i) => topIdx.TryGetValue(i, out int v) ? v : topIdx[i] = mesh.AddVertex(new Vector3(pts[i], top(pts[i])));
        int B(int i) => botIdx.TryGetValue(i, out int v) ? v : botIdx[i] = mesh.AddVertex(new Vector3(pts[i], bottom(pts[i])));

        var edges = new HashSet<(int, int)>();
        foreach (var f in faces)
            for (int i = 0; i < f.Length; i++) edges.Add((f[i], f[(i + 1) % f.Length]));

        foreach (var f in topFaces) mesh.AddFace(f.Select(T).ToArray(), capMaterial);
        foreach (var f in faces) mesh.AddFace(f.Reverse().Select(B).ToArray(), capMaterial);

        foreach (var (a, b) in edges)
            if (!edges.Contains((b, a)))
                mesh.AddFace(new[] { B(a), B(b), T(b), T(a) }, sideMaterial);
    }
}
