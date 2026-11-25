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

    public static BuiltMap Build(MapDocument doc)
    {
        var map = new BuiltMap();
        var s = doc.Build;
        float thick = MathF.Max(1f, s.FloorThickness);

        var levels = new SortedDictionary<int, PathsD>(); // floor height -> footprint polygons
        var footprint = new PathsD();                     // every walkable area, for perimeter walls
        var ramps = new List<(PathD quad, Vector2 a, float za, Vector2 b, float zb)>();
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

        // ---- Paths: split into flat runs (merged per height) and ramp segments.
        foreach (var path in doc.Paths)
        {
            var pts = path.Points;
            if (pts.Count < 2) continue;
            float hw = MathF.Max(8f, path.Width * 0.5f);
            Vector2 P(int i) => new(pts[i].X, pts[i].Y);

            int runStart = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                if (MathF.Abs(pts[i].Z - pts[i - 1].Z) >= FlatEpsilon)
                {
                    EmitFlatRun(runStart, i - 1);
                    var a = P(i - 1); var b = P(i);
                    if (Vector2.Distance(a, b) > 1f)
                    {
                        ramps.Add((Poly2.SegmentQuad(a, b, hw), a, pts[i - 1].Z, b, pts[i].Z));
                        footprint.Add(Poly2.SegmentQuad(a, b, hw));
                        minZ = MathF.Min(minZ, MathF.Min(pts[i - 1].Z, pts[i].Z));
                        maxZ = MathF.Max(maxZ, MathF.Max(pts[i - 1].Z, pts[i].Z));
                    }
                    runStart = i;
                }
            }
            EmitFlatRun(runStart, pts.Count - 1);

            // Wedges fill the gaps on the outside of corners where a flat run meets a ramp (or two ramps meet).
            for (int i = 1; i < pts.Count - 1; i++)
            {
                bool inFlat = MathF.Abs(pts[i].Z - pts[i - 1].Z) < FlatEpsilon;
                bool outFlat = MathF.Abs(pts[i + 1].Z - pts[i].Z) < FlatEpsilon;
                if (inFlat && outFlat) continue;
                var p = P(i);
                var d0 = Vector2.Normalize(p - P(i - 1)); var d1 = Vector2.Normalize(P(i + 1) - p);
                if (float.IsNaN(d0.X) || float.IsNaN(d1.X)) continue;
                var n0 = new Vector2(-d0.Y, d0.X) * hw; var n1 = new Vector2(-d1.Y, d1.X) * hw;
                AddLevel(pts[i].Z, Poly2.Triangle(p, p + n0, p + n1));
                AddLevel(pts[i].Z, Poly2.Triangle(p, p - n0, p - n1));
            }

            // Free ends get a square cap so dead ends and T-junctions overlap neatly.
            void Cap(int end, int inner)
            {
                if (MathF.Abs(pts[end].Z - pts[inner].Z) >= FlatEpsilon) return;
                var dir = Vector2.Normalize(P(end) - P(inner));
                if (float.IsNaN(dir.X)) return;
                AddLevel(pts[end].Z, Poly2.SegmentQuad(P(end), P(end) + dir * hw, hw));
            }
            Cap(0, 1);
            Cap(pts.Count - 1, pts.Count - 2);

            void EmitFlatRun(int from, int to)
            {
                if (to <= from) return;
                var line = new List<Vector2>();
                for (int k = from; k <= to; k++) line.Add(P(k));
                foreach (var poly in Poly2.ThickenPolyline(line, hw)) AddLevel(pts[from].Z, poly);
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

        // ---- Ramps: sloped slabs.
        n = 0;
        foreach (var (quad, a, za, b, zb) in ramps)
        {
            var dir = b - a; float len2 = dir.LengthSquared();
            float Top(Vector2 p) => za + (zb - za) * Math.Clamp(Vector2.Dot(p - a, dir) / len2, 0f, 1f);
            foreach (var shape in Poly2.ToShapes(new PathsD { quad }))
            {
                var m = new PolyMesh();
                Prism(m, shape, Top, p => Top(p) - thick, s.FloorMaterial, s.FloorMaterial);
                map.Meshes.Add(new BuiltMesh { Name = $"ramp_{n++}", Kind = MeshKind.Ramp, Mesh = m });
            }
            if (MathF.Abs(zb - za) / MathF.Sqrt(len2) > 0.7f)
                map.Warnings.Add($"Ramp from {za} to {zb} is steeper than ~35 degrees; players may slide.");
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
            var all = Poly2.Union(footprint);
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
