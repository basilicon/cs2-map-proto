using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Cs2MapProto.Geometry;

namespace Cs2MapProto.Export;

/// <summary>Writes a BuiltMap as a Hammer (CS2) .vmap: a DMX document whose root is CMapRootElement,
/// with each mesh as a CMapMesh holding a half-edge CDmePolygonMesh. Schema mirrors what
/// CS2 Hammer saves (format "vmap" version 40, editorversion 400).</summary>
public static class VmapExporter
{
    const float TextureScale = 0.25f;
    const float AssumedTextureSize = 512f;

    sealed class Ctx
    {
        public int NextNodeId = 2;
        public readonly Random Rng = new();
        public ulong RefId()
        {
            Span<byte> b = stackalloc byte[8];
            Rng.NextBytes(b);
            return BitConverter.ToUInt64(b) | 1;
        }
    }

    public static string ToKeyValues2(BuiltMap map) => ToKeyValues2(map, out _);

    /// <param name="skipped">Names of meshes left out because they weren't manifold (e.g. a self-overlapping curve).</param>
    public static string ToKeyValues2(BuiltMap map, out List<string> skipped)
    {
        var ctx = new Ctx();
        var children = new List<DmElement>();
        skipped = new List<string>();
        foreach (var m in map.Meshes)
        {
            if (m.Mesh.Faces.Count == 0) continue;
            try { children.Add(MapMesh(ctx, m)); }
            catch (InvalidOperationException) { skipped.Add(m.Name); }
        }
        foreach (var e in map.Entities)
            children.Add(MapEntity(ctx, e));

        var materials = map.Meshes.SelectMany(m => m.Mesh.Materials).Distinct().ToList();

        var prefix = new DmElement("$prefix_element$")
            .Strings("map_asset_references", materials);

        var world = Node(new DmElement("CMapWorld"), 1, 0)
            .Elements("children", children)
            .Strings("variableTargetKeys", Array.Empty<string>())
            .Strings("variableNames", Array.Empty<string>())
            .Element("relayPlugData", PlugList())
            .Elements("connectionsData", Array.Empty<DmElement>())
            .Element("entity_properties", new DmElement("EditGameClassProps")
                .Str("classname", "worldspawn")
                .Str("targetname", "")
                .Str("skyname", "sky_day01_01")
                .Str("startdark", "0")
                .Str("startcolor", "0 0 0")
                .Str("pvstype", "10")
                .Str("newunit", "0")
                .Str("maxpropscreenwidth", "-1")
                .Str("minpropscreenwidth", "0")
                .Str("vrchaperone", "0")
                .Str("vrmovement", "0")
                .Str("baked_light_index_min", "0")
                .Str("baked_light_index_max", "256")
                .Str("max_lightmap_resolution", "0")
                .Str("lightmap_queries", "1")
                .Str("prefab_has_runtime_entity_by_default", "0"))
            .Int("nextDecalID", 0)
            .Bool("fixupEntityNames", true)
            .Str("mapUsageType", "standard");
        Transform(world, Vector3.Zero);

        var root = new DmElement("CMapRootElement")
            .Bool("isprefab", false)
            .Int("editorbuild", 9806)
            .Int("editorversion", 400)
            .Str("itemFile", "")
            .Element("defaultcamera", new DmElement("CStoredCamera")
                .Vec3("position", new Vector3(0, -1024, 1024))
                .Vec3("lookat", new Vector3(0, -1023.3f, 1023.3f)))
            .Element("3dcameras", new DmElement("CStoredCameras")
                .Int("activecamera", -1)
                .Elements("cameras", Array.Empty<DmElement>()))
            .Element("world", world)
            .Element("visbility", Transform(Node(new DmElement("CVisibilityMgr"), 0, 0)
                .Elements("children", Array.Empty<DmElement>())
                .Strings("variableTargetKeys", Array.Empty<string>())
                .Strings("variableNames", Array.Empty<string>())
                .Elements("nodes", Array.Empty<DmElement>())
                .Ints("hiddenFlags", Array.Empty<int>()), Vector3.Zero))
            .Element("mapVariables", new DmElement("CMapVariableSet")
                .Strings("variableNames", Array.Empty<string>())
                .Strings("variableValues", Array.Empty<string>())
                .Strings("variableTypeNames", Array.Empty<string>())
                .Strings("variableTypeParameters", Array.Empty<string>())
                .Elements("m_ChoiceGroups", Array.Empty<DmElement>()))
            .Element("rootSelectionSet", new DmElement("CMapSelectionSet")
                .Elements("children", Array.Empty<DmElement>())
                .Str("selectionSetName", "")
                .Element("selectionSetData", null))
            .Elements("m_ReferencedMeshSnapshots", Array.Empty<DmElement>())
            .Bool("m_bIsCordoning", false)
            .Bool("m_bCordonsVisible", false)
            .Elements("nodeInstanceData", Array.Empty<DmElement>());

        return KeyValues2Writer.Write("vmap", 40, prefix, root);
    }

    /// <summary>Writes the .vmap. If dmxConvertPath points at CS2's dmxconvert.exe the file is converted
    /// to binary DMX in place (what Hammer itself saves). Returns a log line.</summary>
    public static string Export(BuiltMap map, string vmapPath, string dmxConvertPath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(vmapPath))!);
        File.WriteAllText(vmapPath, ToKeyValues2(map, out var skipped));
        string note = skipped.Count == 0 ? "" : $" - skipped invalid meshes: {string.Join(", ", skipped)} (tight curve folding over itself?)";
        if (string.IsNullOrWhiteSpace(dmxConvertPath) || !File.Exists(dmxConvertPath))
            return $"Wrote {vmapPath} (text DMX){note}";

        var psi = new ProcessStartInfo(dmxConvertPath)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-i", vmapPath, "-o", vmapPath, "-oe", "binary" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        return (p.ExitCode == 0 ? $"Wrote {vmapPath} (binary DMX)" : $"Wrote {vmapPath} as text; dmxconvert failed: {output.Trim()}") + note;
    }

    // ------------------------------------------------------------------ nodes

    static DmElement Node(DmElement e, int nodeId, ulong refId) =>
        e.Int("nodeID", nodeId).UInt64("referenceID", refId);

    static DmElement Transform(DmElement e, Vector3 origin) => e
        .Vec3("origin", origin)
        .QAngle("angles", Vector3.Zero)
        .Vec3("scales", Vector3.One)
        .Bool("transformLocked", false)
        .Bool("force_hidden", false)
        .Bool("editorOnly", false);

    static DmElement PlugList() => new DmElement("DmePlugList")
        .Strings("names", Array.Empty<string>())
        .Ints("dataTypes", Array.Empty<int>())
        .Ints("plugTypes", Array.Empty<int>())
        .Strings("descriptions", Array.Empty<string>());

    static DmElement MapEntity(Ctx ctx, BuiltEntity ent)
    {
        var props = new DmElement("EditGameClassProps").Str("classname", ent.ClassName).Str("targetname", "");
        foreach (var (k, v) in ent.Properties) props.Str(k, v);

        var e = Node(new DmElement("CMapEntity"), ctx.NextNodeId++, ctx.RefId())
            .Elements("children", Array.Empty<DmElement>())
            .Strings("variableTargetKeys", Array.Empty<string>())
            .Strings("variableNames", Array.Empty<string>())
            .Element("relayPlugData", PlugList())
            .Elements("connectionsData", Array.Empty<DmElement>())
            .Element("entity_properties", props)
            .Vec3("hitNormal", Vector3.UnitZ)
            .Bool("isProceduralEntity", false)
            .Vec3("origin", ent.Origin)
            .QAngle("angles", ent.Angles)
            .Vec3("scales", Vector3.One)
            .Bool("transformLocked", false)
            .Bool("force_hidden", false)
            .Bool("editorOnly", false);
        return e;
    }

    static DmElement MapMesh(Ctx ctx, BuiltMesh built)
    {
        var mesh = built.Mesh;
        var (min, max) = mesh.Bounds();
        var origin = (min + max) * 0.5f;
        origin = new Vector3(MathF.Round(origin.X), MathF.Round(origin.Y), MathF.Round(origin.Z));

        var e = Node(new DmElement("CMapMesh"), ctx.NextNodeId++, ctx.RefId())
            .Elements("children", Array.Empty<DmElement>())
            .Strings("variableTargetKeys", Array.Empty<string>())
            .Strings("variableNames", Array.Empty<string>())
            .Str("cubeMapName", "")
            .Str("lightGroup", "")
            .Bool("visexclude", false)
            .Bool("disablemerging", false)
            .Bool("renderwithdynamic", false)
            .Bool("disableHeightDisplacement", false)
            .Float("fademindist", -1)
            .Float("fademaxdist", 0)
            .Bool("bakelighting", true)
            .Bool("precomputelightprobes", true)
            .Bool("renderToCubemaps", true)
            .Bool("emissiveLightingEnabled", true)
            .Float("emissiveLightingBoost", 1)
            .Int("disableShadows", 0)
            .Bool("lightingDummy", false)
            .Float("smoothingAngle", 40)
            .Color("tintColor", 255, 255, 255, 255)
            .Int("renderAmt", 255)
            .Str("physicsType", "default")
            .Str("physicsGroup", "")
            .Str("physicsInteractsAs", "")
            .Str("physicsInteractsWith", "")
            .Str("physicsInteractsExclude", "")
            .Element("meshData", PolygonMesh(mesh, origin))
            .Bool("physicsSimplificationOverride", false)
            .Float("physicsSimplificationError", 0);
        return Transform(e, origin);
    }

    // ------------------------------------------------------------------ half-edge mesh

    /// <summary>Converts face loops into Hammer's half-edge layout:
    ///  - half-edges come in opposite pairs (2k, 2k+1); edgeVertexIndices = the vertex a half-edge points TO
    ///  - vertexEdgeIndices = one half-edge leaving each vertex
    ///  - edgeNextIndices walks each face counter-clockwise (seen from outside); open borders get face -1.</summary>
    static DmElement PolygonMesh(PolyMesh mesh, Vector3 origin)
    {
        mesh = Compact(mesh);
        var heTo = new List<int>();
        var heFrom = new List<int>();
        var heFace = new List<int>();
        var heNext = new List<int>();
        var lookup = new Dictionary<(int, int), int>();

        int GetPair(int a, int b)
        {
            if (lookup.TryGetValue((a, b), out int he)) return he;
            int h = heTo.Count;
            heTo.Add(b); heFrom.Add(a); heFace.Add(-1); heNext.Add(-1);
            heTo.Add(a); heFrom.Add(b); heFace.Add(-1); heNext.Add(-1);
            lookup[(a, b)] = h; lookup[(b, a)] = h + 1;
            return h;
        }

        var faceEdge = new int[mesh.Faces.Count];
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var loop = mesh.Faces[f];
            var hes = new int[loop.Length];
            for (int i = 0; i < loop.Length; i++)
            {
                int he = GetPair(loop[i], loop[(i + 1) % loop.Length]);
                if (heFace[he] != -1)
                    throw new InvalidOperationException($"Non-manifold edge in mesh (face {f}); export aborted.");
                heFace[he] = f;
                hes[i] = he;
            }
            for (int i = 0; i < hes.Length; i++) heNext[hes[i]] = hes[(i + 1) % hes.Length];
            faceEdge[f] = hes[0];
        }

        // Border half-edges (open meshes): link them into loops around the holes.
        for (int he = 0; he < heTo.Count; he++)
        {
            if (heFace[he] != -1) continue;
            int v = heTo[he];
            for (int c = 0; c < heTo.Count; c++)
                if (heFace[c] == -1 && heFrom[c] == v) { heNext[he] = c; break; }
        }

        var vertexEdge = new int[mesh.Vertices.Count];
        Array.Fill(vertexEdge, -1);
        for (int he = 0; he < heTo.Count; he++)
            if (vertexEdge[heFrom[he]] == -1 || heFace[he] == -1) vertexEdge[heFrom[he]] = he;

        int heCount = heTo.Count;
        int faceCount = mesh.Faces.Count;

        // Per-face texture projection: world aligned on the dominant axis, like Hammer's default.
        var faceNormals = new Vector3[faceCount];
        var axisU = new Vector4[faceCount];
        var axisV = new Vector4[faceCount];
        for (int f = 0; f < faceCount; f++)
        {
            var n = mesh.FaceNormal(f);
            faceNormals[f] = n;
            Vector3 u, w;
            var an = Vector3.Abs(n);
            if (an.Z >= an.X && an.Z >= an.Y) { u = Vector3.UnitX; w = -Vector3.UnitY; }
            else if (an.X >= an.Y) { u = Vector3.UnitY; w = -Vector3.UnitZ; }
            else { u = Vector3.UnitX; w = -Vector3.UnitZ; }
            axisU[f] = new Vector4(u, 0);
            axisV[f] = new Vector4(w, 0);
        }

        var texcoords = new Vector2[heCount];
        var normals = new Vector3[heCount];
        var tangents = new Vector4[heCount];
        for (int he = 0; he < heCount; he++)
        {
            int f = heFace[he];
            if (f < 0) continue;
            var p = mesh.Vertices[heTo[he]];
            var u = new Vector3(axisU[f].X, axisU[f].Y, axisU[f].Z);
            var v = new Vector3(axisV[f].X, axisV[f].Y, axisV[f].Z);
            texcoords[he] = new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v)) / (TextureScale * AssumedTextureSize);
            normals[he] = faceNormals[f];
            tangents[he] = new Vector4(u, -1);
        }

        var positions = mesh.Vertices.Select(p => p - origin).ToList();

        var meshEl = new DmElement("CDmePolygonMesh")
            .Str("name", "meshData")
            .Ints("vertexEdgeIndices", vertexEdge)
            .Ints("vertexDataIndices", Enumerable.Range(0, mesh.Vertices.Count))
            .Ints("edgeVertexIndices", heTo)
            .Ints("edgeOppositeIndices", Enumerable.Range(0, heCount).Select(i => i ^ 1))
            .Ints("edgeNextIndices", heNext)
            .Ints("edgeFaceIndices", heFace)
            .Ints("edgeDataIndices", Enumerable.Range(0, heCount).Select(i => i / 2))
            .Ints("edgeVertexDataIndices", Enumerable.Range(0, heCount))
            .Ints("faceEdgeIndices", faceEdge)
            .Ints("faceDataIndices", Enumerable.Range(0, faceCount))
            .Strings("materials", mesh.Materials)
            .Element("vertexData", DataArray(mesh.Vertices.Count,
                Stream("position", "vector3_array", 3, positions)))
            .Element("faceVertexData", DataArray(heCount,
                Stream("texcoord", "vector2_array", 1, texcoords),
                Stream("normal", "vector3_array", 1, normals),
                Stream("tangent", "vector4_array", 1, tangents)))
            .Element("edgeData", DataArray(heCount / 2,
                Stream("flags", "int_array", 3, new int[heCount / 2])))
            .Element("faceData", DataArray(faceCount,
                Stream("textureScale", "vector2_array", 0, Enumerable.Repeat(new Vector2(TextureScale), faceCount).ToList()),
                Stream("textureAxisU", "vector4_array", 0, axisU),
                Stream("textureAxisV", "vector4_array", 0, axisV),
                Stream("materialindex", "int_array", 8, mesh.FaceMaterials),
                Stream("flags", "int_array", 3, new int[faceCount]),
                Stream("lightmapScaleBias", "int_array", 1, new int[faceCount])))
            .Element("subdivisionData", new DmElement("CDmePolygonMeshSubdivisionData")
                .Ints("subdivisionLevels", new int[heCount])
                .Elements("streams", Array.Empty<DmElement>()));
        return meshEl;
    }

    /// <summary>Drops vertices no face references (Hammer expects every vertex to have an edge).</summary>
    static PolyMesh Compact(PolyMesh src)
    {
        var dst = new PolyMesh();
        var remap = new Dictionary<int, int>();
        for (int f = 0; f < src.Faces.Count; f++)
        {
            var loop = src.Faces[f].Select(v => remap.TryGetValue(v, out int r) ? r : remap[v] = dst.AddVertex(src.Vertices[v])).ToArray();
            dst.AddFace(loop, src.Materials[src.FaceMaterials[f]]);
        }
        return dst;
    }

    static DmElement DataArray(int size, params DmElement[] streams) =>
        new DmElement("CDmePolygonMeshDataArray").Int("size", size).Elements("streams", streams);

    static DmElement Stream<T>(string attribute, string arrayType, int stateFlags, IEnumerable<T> data) =>
        new DmElement("CDmePolygonMeshDataStream")
            .Str("name", attribute + ":0")
            .Str("standardAttributeName", attribute)
            .Str("semanticName", attribute)
            .Int("semanticIndex", 0)
            .Int("vertexBufferLocation", 0)
            .Int("dataStateFlags", stateFlags)
            .Element("subdivisionBinding", null)
            .Set("data", arrayType, new List<T>(data));
}
