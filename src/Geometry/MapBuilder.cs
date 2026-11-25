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
    const float FlatEpsilon = 0.5f;
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
        var ramps = new List<(List<Vector2> left, List<Vector2> right, List<float> z)>();
        bool steep = false;
        var rampSections = new List<(PathD quad, float zMin, float zMax)>(); // ramp footprint pieces with their height range
        float minZ = float.MaxValue, maxZ = float.MinValue;

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
            int m = samples.Count;
            Vector2 P(int i) => new(samples[i].P.X, samples[i].P.Y);
            float Z(int i) => samples[i].P.Z;
            bool Flat(int seg) => MathF.Abs(Z(seg + 1) - Z(seg)) < FlatEpsilon;

            // Maximal runs of same-kind segments; run [k, j] covers samples k..j+1.
            for (int k = 0; k < m - 1;)
            {
                bool flat = Flat(k);
                int j = k;
                while (j + 1 < m - 1 && Flat(j + 1) == flat) j++;
                if (flat) EmitFlatRun(k, j + 1);
                else EmitRampRun(k, j + 1);
                k = j + 1;
            }

            // Wedges fill the outside of corners where a flat run meets a ramp.
            for (int i = 1; i < m - 1; i++)
            {
                if (Flat(i - 1) == Flat(i)) continue;
                var p = P(i);
                var d0 = Vector2.Normalize(p - P(i - 1)); var d1 = Vector2.Normalize(P(i + 1) - p);
                if (float.IsNaN(d0.X) || float.IsNaN(d1.X)) continue;
                var n0 = new Vector2(-d0.Y, d0.X) * hw; var n1 = new Vector2(-d1.Y, d1.X) * hw;
                AddLevel(Z(i), Poly2.Triangle(p, p + n0, p + n1));
                AddLevel(Z(i), Poly2.Triangle(p, p - n0, p - n1));
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
                // Free flat ends are extended by half the width (a square end), so dead ends and T-junctions
                // overlap neatly. Extending the line itself (rather than adding a separate cap shape) keeps the
                // end in one piece with the corridor, so there's no seam or wedge gap between them.
                if (from == 0 && EndDir(0, +1) is var d0 && d0 != Vector2.Zero) line.Add(P(0) + d0 * hw);
                for (int q = from; q <= to; q++) line.Add(P(q));
                if (to == m - 1 && EndDir(m - 1, -1) is var d1 && d1 != Vector2.Zero) line.Add(P(m - 1) + d1 * hw);
                foreach (var poly in Poly2.ThickenPolyline(line, hw)) AddLevel(Z(from), poly);
            }

            void EmitRampRun(int from, int to)
            {
                var center = new List<Vector2>();
                var zs = new List<float>();
                for (int q = from; q <= to; q++) { center.Add(P(q)); zs.Add(Z(q)); }
                var (left, right) = RibbonEdges(center, hw);
                for (int q = 0; q + 1 < center.Count; q++)
                {
                    var section = Poly2.Ccw(new[] { right[q], right[q + 1], left[q + 1], left[q] });
                    footprint.Add(section);
                    rampSections.Add((section, MathF.Min(zs[q], zs[q + 1]), MathF.Max(zs[q], zs[q + 1])));
                    float run = Vector2.Distance(center[q], center[q + 1]);
                    if (run > 0.01f && MathF.Abs(zs[q + 1] - zs[q]) / run > 0.7f) steep = true;
                }
                ramps.Add((left, right, zs));
                foreach (var z in zs) { minZ = MathF.Min(minZ, z); maxZ = MathF.Max(maxZ, z); }
            }
        }

        // ---- Floor regions join the level at their elevation.
        foreach (var r in doc.Regions.Where(r => r.Kind == RegionKind.Floor && r.Points.Count >= 3))
            AddLevel(r.Elevation, Poly2.Ccw(r.Points.Select(p => new Vector2(p.X, p.Y))));

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
        foreach (var (left, right, zs) in ramps)
        {
            var m = new PolyMesh();
            Ribbon(m, left, right, zs, thick, s.FloorMaterial);
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
                foreach (var shape in Poly2.ToShapes(Poly2.Difference(outer, poly)))
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
            var ring = Poly2.Difference(Poly2.Inflate(all, s.WallThickness), all);
            float wallBottom = minZ - thick;
            float wallTop = maxZ + s.WallHeight;
            n = 0;
            foreach (var shape in Poly2.ToShapes(ring))
            {
                var m = new PolyMesh();
                Prism(m, shape, _ => wallTop, _ => wallBottom, s.WallMaterial, s.WallMaterial);
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

    /// <summary>Left/right edges of a centreline offset by hw, with mitred joints (miter clamped to 2x).</summary>
    static (List<Vector2> left, List<Vector2> right) RibbonEdges(List<Vector2> c, float hw)
    {
        var left = new List<Vector2>(c.Count);
        var right = new List<Vector2>(c.Count);
        Vector2 Normal(int seg)
        {
            var d = Vector2.Normalize(c[seg + 1] - c[seg]);
            return new Vector2(-d.Y, d.X);
        }
        for (int i = 0; i < c.Count; i++)
        {
            Vector2 n;
            float len = hw;
            if (i == 0) n = Normal(0);
            else if (i == c.Count - 1) n = Normal(i - 1);
            else
            {
                var n0 = Normal(i - 1); var n1 = Normal(i);
                n = Vector2.Normalize(n0 + n1);
                if (float.IsNaN(n.X)) n = n0;
                len = hw / MathF.Max(0.5f, Vector2.Dot(n, n0));
            }
            left.Add(c[i] + n * len);
            right.Add(c[i] - n * len);
        }
        return (left, right);
    }

    /// <summary>Closed slab along a ribbon: top follows the per-section heights, bottom is thick below.
    /// Non-planar top quads (curved ramps) are split into triangles so every Hammer face is planar.</summary>
    static void Ribbon(PolyMesh mesh, List<Vector2> left, List<Vector2> right, List<float> z, float thick, string material)
    {
        int n = left.Count;
        Vector3 Tp(Vector2 p, int i) => new(p, z[i]);
        Vector3 Bp(Vector2 p, int i) => new(p, z[i] - thick);

        for (int i = 0; i + 1 < n; i++)
        {
            var top = new[] { Tp(right[i], i), Tp(right[i + 1], i + 1), Tp(left[i + 1], i + 1), Tp(left[i], i) };
            var bot = new[] { Bp(right[i], i), Bp(right[i + 1], i + 1), Bp(left[i + 1], i + 1), Bp(left[i], i) };
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
        for (int i = n - 1; i >= 0; i--) outline.Add((left[i], i));
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
        var faces = Triangulator.ConvexFaces(shape, out var pts);
        if (faces.Count == 0) return;

        var topIdx = new Dictionary<int, int>();
        var botIdx = new Dictionary<int, int>();
        int T(int i) => topIdx.TryGetValue(i, out int v) ? v : topIdx[i] = mesh.AddVertex(new Vector3(pts[i], top(pts[i])));
        int B(int i) => botIdx.TryGetValue(i, out int v) ? v : botIdx[i] = mesh.AddVertex(new Vector3(pts[i], bottom(pts[i])));

        var edges = new HashSet<(int, int)>();
        foreach (var f in faces)
            for (int i = 0; i < f.Length; i++) edges.Add((f[i], f[(i + 1) % f.Length]));

        foreach (var f in faces)
        {
            mesh.AddFace(f.Select(T).ToArray(), capMaterial);
            mesh.AddFace(f.Reverse().Select(B).ToArray(), capMaterial);
        }

        foreach (var (a, b) in edges)
            if (!edges.Contains((b, a)))
                mesh.AddFace(new[] { B(a), B(b), T(b), T(a) }, sideMaterial);
    }
}
