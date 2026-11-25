using System;
using System.Collections.Generic;
using System.Numerics;

namespace Cs2MapProto.Geometry;

/// <summary>Indexed polygon mesh in Hammer space. Faces are vertex loops, counter-clockwise when
/// seen from outside (the convention Hammer's CDmePolygonMesh uses). Vertices are welded on insert.</summary>
public sealed class PolyMesh
{
    public readonly List<Vector3> Vertices = new();
    public readonly List<int[]> Faces = new();
    public readonly List<int> FaceMaterials = new();
    public readonly List<string> Materials = new();

    readonly Dictionary<(long, long, long), int> _weld = new();
    const float WeldScale = 64f; // 1/64 unit

    public int AddVertex(Vector3 p)
    {
        var key = ((long)MathF.Round(p.X * WeldScale), (long)MathF.Round(p.Y * WeldScale), (long)MathF.Round(p.Z * WeldScale));
        if (_weld.TryGetValue(key, out int idx)) return idx;
        idx = Vertices.Count;
        Vertices.Add(p);
        _weld[key] = idx;
        return idx;
    }

    public int MaterialIndex(string material)
    {
        int i = Materials.IndexOf(material);
        if (i >= 0) return i;
        Materials.Add(material);
        return Materials.Count - 1;
    }

    public void AddFace(IReadOnlyList<int> loop, string material)
    {
        var clean = new List<int>(loop.Count);
        foreach (int v in loop)
            if (clean.Count == 0 || clean[^1] != v) clean.Add(v);
        while (clean.Count > 1 && clean[0] == clean[^1]) clean.RemoveAt(clean.Count - 1);
        if (clean.Count < 3) return;
        if (new HashSet<int>(clean).Count != clean.Count) return; // degenerate loop
        Faces.Add(clean.ToArray());
        FaceMaterials.Add(MaterialIndex(material));
    }

    public Vector3 FaceNormal(int f)
    {
        // Newell's method: robust for any planar-ish polygon.
        var loop = Faces[f];
        Vector3 n = Vector3.Zero;
        for (int i = 0; i < loop.Length; i++)
        {
            var p = Vertices[loop[i]];
            var q = Vertices[loop[(i + 1) % loop.Length]];
            n.X += (p.Y - q.Y) * (p.Z + q.Z);
            n.Y += (p.Z - q.Z) * (p.X + q.X);
            n.Z += (p.X - q.X) * (p.Y + q.Y);
        }
        float len = n.Length();
        return len > 1e-9f ? n / len : Vector3.UnitZ;
    }

    public (Vector3 min, Vector3 max) Bounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in Vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        return (min, max);
    }
}
