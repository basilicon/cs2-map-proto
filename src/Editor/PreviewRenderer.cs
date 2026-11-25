using System.Collections.Generic;
using Godot;
using Cs2MapProto.Geometry;

namespace Cs2MapProto.Editor;

/// <summary>Shows the exact meshes the exporter will write, colored by kind with a world-space grid.</summary>
public partial class PreviewRenderer : Node3D
{
    [Export] public Shader PreviewShader { get; set; }

    readonly Dictionary<MeshKind, ShaderMaterial> _materials = new();
    MeshInstance3D _instance;

    static readonly Dictionary<MeshKind, Color> Colors = new()
    {
        [MeshKind.Floor] = new Color(0.62f, 0.66f, 0.70f),
        [MeshKind.Ramp] = new Color(0.55f, 0.70f, 0.55f),
        [MeshKind.Wall] = new Color(0.42f, 0.44f, 0.52f),
        [MeshKind.Obstacle] = new Color(0.85f, 0.52f, 0.30f),
        [MeshKind.PitBottom] = new Color(0.30f, 0.28f, 0.34f),
        [MeshKind.PitLiner] = new Color(0.36f, 0.34f, 0.40f),
    };

    public override void _Ready()
    {
        _instance = new MeshInstance3D { Name = "PreviewMesh" };
        AddChild(_instance);
    }

    Material MaterialFor(MeshKind kind)
    {
        if (_materials.TryGetValue(kind, out var m)) return m;
        m = new ShaderMaterial { Shader = PreviewShader };
        m.SetShaderParameter("base_color", Colors[kind]);
        _materials[kind] = m;
        return m;
    }

    public void Show(BuiltMap map)
    {
        var mesh = new ArrayMesh();
        var byKind = new Dictionary<MeshKind, (List<Vector3> v, List<Vector3> n)>();
        foreach (var bm in map.Meshes)
        {
            if (!byKind.TryGetValue(bm.Kind, out var lists)) byKind[bm.Kind] = lists = (new(), new());
            var pm = bm.Mesh;
            for (int f = 0; f < pm.Faces.Count; f++)
            {
                var loop = pm.Faces[f];
                var nrm = Space.ToGodot(pm.FaceNormal(f));
                // Godot treats clockwise triangles as front-facing; our loops are counter-clockwise, so fan in reverse.
                for (int i = 1; i < loop.Length - 1; i++)
                {
                    lists.v.Add(Space.ToGodot(pm.Vertices[loop[0]]));
                    lists.v.Add(Space.ToGodot(pm.Vertices[loop[i + 1]]));
                    lists.v.Add(Space.ToGodot(pm.Vertices[loop[i]]));
                    lists.n.Add(nrm); lists.n.Add(nrm); lists.n.Add(nrm);
                }
            }
        }

        foreach (var (kind, (v, n)) in byKind)
        {
            if (v.Count == 0) continue;
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = n.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, MaterialFor(kind));
        }
        _instance.Mesh = mesh;
    }
}
