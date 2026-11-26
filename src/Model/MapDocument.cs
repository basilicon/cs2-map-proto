using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cs2MapProto.Model;

// All coordinates are in Hammer units, Hammer axes: X east, Y north, Z up.
// The editor converts to Godot space (Y up) only for display; see Space.cs.

[JsonConverter(typeof(Vec3JsonConverter))]
public struct Vec3
{
    public float X, Y, Z;
    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public override string ToString() => $"({X}, {Y}, {Z})";
}

/// <summary>A walkable corridor: a polyline with a width. Each point carries its own floor height,
/// so consecutive points at different Z become ramps.</summary>
/// <summary>How the corridor's outer edge turns at a path point.</summary>
public enum CornerStyle
{
    /// <summary>Edges meet in a point (mitred; very acute corners are bevelled so they can't spike out).</summary>
    Sharp,
    /// <summary>Corner cut off with one straight edge.</summary>
    Bevel,
    /// <summary>Corner rounded with an arc around the point.</summary>
    Round,
}

public sealed class PathShape
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public List<Vec3> Points { get; set; } = new();
    public float Width { get; set; } = 192f;

    /// <summary>Per-point Bezier tangent (XY offset to the outgoing control point; the incoming one is mirrored).
    /// Zero = sharp corner. Kept parallel to Points; use the helpers below to edit both together.</summary>
    public List<Vec3> Tangents { get; set; } = new();

    /// <summary>Per-point corner style (only matters at interior points). Kept parallel to Points.</summary>
    public List<CornerStyle> Corners { get; set; } = new();

    /// <summary>How far the floor extends past the first / last point when that end is a free, flat end
    /// (0 = stop exactly at the point). Independent of Width; older files default to half the width.</summary>
    public float? StartDepth { get; set; }
    public float? EndDepth { get; set; }

    /// <summary>Optional variation along the path's length (t = 0 at the first point, 1 at the last).</summary>
    public ProfileCurve WidthCurve { get; set; } = ProfileCurve.Flat(1);     // multiplier of Width
    public ProfileCurve ElevationCurve { get; set; } = ProfileCurve.Flat(0); // offset added to point heights (u)
    public ProfileCurve WallCurve { get; set; } = ProfileCurve.Flat(1);      // multiplier of the wall height

    /// <summary>Walls along each side of the path (left/right as seen walking from the first point to the last).
    /// Off leaves that edge open, e.g. a drop or a view into another area; the end walls at free ends stay.</summary>
    public bool WallLeft { get; set; } = true;
    public bool WallRight { get; set; } = true;
    /// <summary>Wall height on each side; null = the build's default wall height. The wall profile scales it.</summary>
    public float? WallHeightLeft { get; set; }
    public float? WallHeightRight { get; set; }
    /// <summary>Roof over the path: clearance from the floor to the ceiling's underside, following ramps and the
    /// elevation profile. null = open to the sky.</summary>
    public float? CeilingHeight { get; set; }

    public bool AnyProfile => WidthCurve.Enabled || ElevationCurve.Enabled || WallCurve.Enabled;

    public void Normalize()
    {
        WidthCurve ??= ProfileCurve.Flat(1);
        ElevationCurve ??= ProfileCurve.Flat(0);
        WallCurve ??= ProfileCurve.Flat(1);
        StartDepth ??= Width * 0.5f;
        EndDepth ??= Width * 0.5f;
        while (Tangents.Count < Points.Count) Tangents.Add(default);
        if (Tangents.Count > Points.Count) Tangents.RemoveRange(Points.Count, Tangents.Count - Points.Count);
        Corners ??= new();
        while (Corners.Count < Points.Count) Corners.Add(CornerStyle.Sharp);
        if (Corners.Count > Points.Count) Corners.RemoveRange(Points.Count, Corners.Count - Points.Count);
    }

    public CornerStyle CornerAt(int i) => i >= 0 && i < Corners.Count ? Corners[i] : CornerStyle.Sharp;
    public void SetCorner(int i, CornerStyle c) { Normalize(); Corners[i] = c; }

    public Vec3 TangentAt(int i) => i >= 0 && i < Tangents.Count ? Tangents[i] : default;
    public static bool IsZero(Vec3 t) => MathF.Abs(t.X) < 0.01f && MathF.Abs(t.Y) < 0.01f;
    public bool IsCurved(int i) => !IsZero(TangentAt(i));
    public bool AnyCurved() { for (int i = 0; i < Points.Count; i++) if (IsCurved(i)) return true; return false; }

    public void SetTangent(int i, Vec3 t) { Normalize(); Tangents[i] = new Vec3(t.X, t.Y, 0); }
    public void InsertPoint(int i, Vec3 p, Vec3 tangent = default, CornerStyle corner = CornerStyle.Sharp) { Normalize(); Points.Insert(i, p); Tangents.Insert(i, tangent); Corners.Insert(i, corner); }
    public void AddPoint(Vec3 p, Vec3 tangent = default, CornerStyle corner = CornerStyle.Sharp) => InsertPoint(Points.Count, p, tangent, corner);
    public void RemovePoint(int i) { Normalize(); Points.RemoveAt(i); Tangents.RemoveAt(i); Corners.RemoveAt(i); }

    /// <summary>Catmull-Rom style tangent through the neighbours: a sensible default curve.</summary>
    public Vec3 AutoTangent(int i)
    {
        if (Points.Count < 2) return default;
        var prev = Points[Math.Max(0, i - 1)];
        var next = Points[Math.Min(Points.Count - 1, i + 1)];
        float k = (i == 0 || i == Points.Count - 1) ? 1f / 3f : 1f / 6f;
        return new Vec3((next.X - prev.X) * k, (next.Y - prev.Y) * k, 0);
    }

    /// <summary>Reverses direction (tangents flip so the curve keeps its shape).</summary>
    public void Reverse()
    {
        Normalize();
        Points.Reverse();
        Tangents.Reverse();
        Corners.Reverse();
        (StartDepth, EndDepth) = (EndDepth, StartDepth);
        (WallLeft, WallRight) = (WallRight, WallLeft);
        (WallHeightLeft, WallHeightRight) = (WallHeightRight, WallHeightLeft);
        for (int i = 0; i < Tangents.Count; i++) Tangents[i] = new Vec3(-Tangents[i].X, -Tangents[i].Y, 0);
    }
}

public enum RegionKind
{
    /// <summary>Extra walkable floor (sites, open areas). Merged with paths at the same height.</summary>
    Floor,
    /// <summary>Solid block rising from Elevation by Height. Taller than ~64u can't be jumped on.</summary>
    Obstacle,
    /// <summary>Hole cut out of every floor it overlaps. Height = drop depth to the pit bottom (0 = bottomless).</summary>
    Pit,
    /// <summary>Roof slab over the floor at Elevation. Height = clearance from that floor to the slab's underside.</summary>
    Ceiling,
}

/// <summary>A closed flat polygon at one elevation. Points' Z is ignored; Elevation applies to all.</summary>
public sealed class RegionShape
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public RegionKind Kind { get; set; }
    public List<Vec3> Points { get; set; } = new();
    public float Elevation { get; set; }
    public float Height { get; set; } = 128f;
}

public enum Team { T, CT }

public sealed class SpawnPoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Team Team { get; set; }
    public Vec3 Position { get; set; }
    public float Yaw { get; set; }
}

public sealed class BuildSettings
{
    public float FloorThickness { get; set; } = 16f;
    public bool PerimeterWalls { get; set; } = true;
    public float WallHeight { get; set; } = 192f;
    public float WallThickness { get; set; } = 16f;
    public float ObstacleUnjumpableHeight { get; set; } = 65f;
    public string FloorMaterial { get; set; } = "materials/dev/reflectivity_30.vmat";
    public string WallMaterial { get; set; } = "materials/dev/dev_measuregeneric01.vmat";
    public string ObstacleMaterial { get; set; } = "materials/dev/dev_measuregeneric01.vmat";
    public string CeilingMaterial { get; set; } = "materials/dev/dev_measuregeneric01.vmat";
}

public sealed class ExportSettings
{
    /// <summary>Full path of the .vmap to write, e.g. ...\content\csgo_addons\myaddon\maps\proto.vmap</summary>
    public string VmapPath { get; set; } = "";
    /// <summary>Convert the text DMX to binary with CS2's dmxconvert.exe (what Hammer saves itself).</summary>
    public bool ConvertToBinary { get; set; }
    /// <summary>Run CS2's resourcecompiler after exporting so the map can be loaded with `map name` right away.</summary>
    public bool CompileAfterExport { get; set; }
}

public sealed class MapDocument
{
    public int Version { get; set; } = 1;
    public float GridSize { get; set; } = 16f;
    public List<PathShape> Paths { get; set; } = new();
    public List<RegionShape> Regions { get; set; } = new();
    public List<SpawnPoint> Spawns { get; set; } = new();
    public BuildSettings Build { get; set; } = new();
    public ExportSettings Export { get; set; } = new();
    /// <summary>Add-on data by plugin id (see Plugins.IEditorHost.SetDocumentData). Left out of the file when empty.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement> Plugins { get; set; }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
    public static MapDocument FromJson(string json)
    {
        var d = JsonSerializer.Deserialize<MapDocument>(json, JsonOpts) ?? new MapDocument();
        foreach (var p in d.Paths) p.Normalize(); // older files have no Tangents
        return d;
    }
    public MapDocument Clone() => FromJson(ToJson());

    public void Save(string path) => File.WriteAllText(path, ToJson());
    public static MapDocument Load(string path) => FromJson(File.ReadAllText(path));

    /// <summary>A small layout that exercises every feature: two levels, a ramp, an obstacle, a pit, a covered
    /// corridor, a ceiling region, spawns.</summary>
    public static MapDocument CreateDemo()
    {
        var d = new MapDocument();
        d.Paths.Add(new PathShape
        {
            Width = 256,
            Points = { new(-1024, -768, 0), new(-1024, 256, 0), new(0, 256, 0), new(512, 256, 64), new(1024, 256, 64), new(1024, 1024, 64) },
        });
        d.Paths.Add(new PathShape
        {
            Width = 160,
            CeilingHeight = 160,
            Points = { new(0, 256, 0), new(0, -768, 0), new(1024, -768, 0) },
        });
        d.Regions.Add(new RegionShape
        {
            Kind = RegionKind.Floor, Elevation = 64,
            Points = { new(768, 896, 0), new(1408, 896, 0), new(1408, 1408, 0), new(768, 1408, 0) },
        });
        d.Regions.Add(new RegionShape
        {
            Kind = RegionKind.Obstacle, Elevation = 64, Height = 128,
            Points = { new(1024, 1088, 0), new(1152, 1088, 0), new(1152, 1216, 0), new(1024, 1216, 0) },
        });
        d.Regions.Add(new RegionShape
        {
            Kind = RegionKind.Obstacle, Elevation = 0, Height = 48,
            Points = { new(-1088, -64, 0), new(-1008, -64, 0), new(-1008, 16, 0), new(-1088, 16, 0) },
        });
        d.Regions.Add(new RegionShape
        {
            Kind = RegionKind.Pit, Elevation = 0, Height = 256,
            Points = { new(-64, -448, 0), new(64, -448, 0), new(64, -256, 0), new(-64, -256, 0) },
        });
        d.Regions.Add(new RegionShape
        {
            Kind = RegionKind.Ceiling, Elevation = 64, Height = 224,
            Points = { new(1088, 896, 0), new(1408, 896, 0), new(1408, 1408, 0), new(1088, 1408, 0) },
        });
        // 64u apart, two rows: well clear of each other and the walls.
        for (int i = 0; i < 5; i++)
        {
            d.Spawns.Add(new SpawnPoint { Team = Team.T, Position = new(-1088 + (i % 3) * 64, -768 + (i / 3) * 64, 0), Yaw = 90 });
            d.Spawns.Add(new SpawnPoint { Team = Team.CT, Position = new(864 + (i % 3) * 64, 1280 + (i / 3) * 64, 64), Yaw = 270 });
        }
        return d;
    }
}

public sealed class Vec3JsonConverter : JsonConverter<Vec3>
{
    public override Vec3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var a = JsonSerializer.Deserialize<float[]>(ref reader, options) ?? Array.Empty<float>();
        return new Vec3(a.Length > 0 ? a[0] : 0, a.Length > 1 ? a[1] : 0, a.Length > 2 ? a[2] : 0);
    }

    public override void Write(Utf8JsonWriter writer, Vec3 v, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(v.X);
        writer.WriteNumberValue(v.Y);
        writer.WriteNumberValue(v.Z);
        writer.WriteEndArray();
    }
}
