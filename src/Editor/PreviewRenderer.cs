using System.Collections.Generic;
using Godot;
using Cs2MapProto.Geometry;

namespace Cs2MapProto.Editor;

/// <summary>Shows the exact meshes the exporter will write, colored by kind with a world-space grid.</summary>
public partial class PreviewRenderer : Node3D
{
    [Export] public Shader PreviewShader { get; set; }
    /// <summary>Flat-colour / depth shader for the off-screen capture copies (SceneCapture).</summary>
    [Export] public Shader CaptureShader { get; set; }

    /// <summary>Visual layers: the editor camera sees Preview (and the overlay on its own layer); the capture
    /// copies sit on layers only SceneCapture's camera renders.</summary>
    public const uint PreviewLayer = 1, OverlayLayer = 1 << 1, SemanticLayer = 1 << 18, DepthLayer = 1 << 19;

    readonly Dictionary<MeshKind, ShaderMaterial> _materials = new();
    readonly Dictionary<MeshKind, ShaderMaterial> _semanticMaterials = new();
    MeshInstance3D _instance, _semantic, _depth;
    ShaderMaterial _depthMaterial;

    public ShaderMaterial DepthMaterial => _depthMaterial;

    static readonly Dictionary<MeshKind, Color> Colors = new()
    {
        [MeshKind.Floor] = new Color(0.62f, 0.66f, 0.70f),
        [MeshKind.Ramp] = new Color(0.55f, 0.70f, 0.55f),
        [MeshKind.Wall] = new Color(0.42f, 0.44f, 0.52f),
        [MeshKind.Obstacle] = new Color(0.85f, 0.52f, 0.30f),
        [MeshKind.PitBottom] = new Color(0.30f, 0.28f, 0.34f),
        [MeshKind.PitLiner] = new Color(0.36f, 0.34f, 0.40f),
        [MeshKind.Ceiling] = new Color(0.58f, 0.52f, 0.66f),
    };

    MeshInstance3D _ceilings;
    float _hideAbove = float.PositiveInfinity;

    /// <summary>Meshes whose lowest point is at or above this height (Hammer Z) aren't drawn. The capture copies
    /// always draw everything.</summary>
    public float HideAbove
    {
        get => _hideAbove;
        set
        {
            if (value == _hideAbove) return;
            _hideAbove = value;
            foreach (var m in _materials.Values) m.SetShaderParameter("hide_above", float.IsInfinity(value) ? 1e9f : value);
        }
    }

    /// <summary>Roofs hide what's under them, so the editor turns them off in the top-down view. The capture
    /// copies always include them.</summary>
    public bool ShowCeilings
    {
        get => _ceilings?.Visible ?? true;
        set { if (_ceilings != null) _ceilings.Visible = value; }
    }

    public override void _Ready()
    {
        _instance = new MeshInstance3D { Name = "PreviewMesh", Layers = PreviewLayer };
        AddChild(_instance);
        _ceilings = new MeshInstance3D { Name = "PreviewCeilings", Layers = PreviewLayer };
        AddChild(_ceilings);
        if (CaptureShader == null) return;
        _depthMaterial = new ShaderMaterial { Shader = CaptureShader };
        _depthMaterial.SetShaderParameter("mode", 1);
        var noShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _semantic = new MeshInstance3D { Name = "SemanticMesh", Layers = SemanticLayer, CastShadow = noShadow };
        _depth = new MeshInstance3D { Name = "DepthMesh", Layers = DepthLayer, CastShadow = noShadow, MaterialOverride = _depthMaterial };
        AddChild(_semantic);
        AddChild(_depth);
    }

    Material SemanticMaterialFor(MeshKind kind)
    {
        if (_semanticMaterials.TryGetValue(kind, out var m)) return m;
        m = new ShaderMaterial { Shader = CaptureShader };
        m.SetShaderParameter("mode", 0);
        m.SetShaderParameter("flat_color", Plugins.CapturePalette.Kinds[kind]);
        _semanticMaterials[kind] = m;
        return m;
    }

    Material MaterialFor(MeshKind kind)
    {
        if (_materials.TryGetValue(kind, out var m)) return m;
        m = new ShaderMaterial { Shader = PreviewShader };
        m.SetShaderParameter("base_color", Colors[kind]);
        m.SetShaderParameter("hide_above", float.IsInfinity(_hideAbove) ? 1e9f : _hideAbove);
        _materials[kind] = m;
        return m;
    }

    public void Show(BuiltMap map)
    {
        var mesh = new ArrayMesh();      // everything (capture copies)
        var preview = new ArrayMesh();   // everything but ceilings
        var ceilings = new ArrayMesh();
        var byKind = new Dictionary<MeshKind, (List<Vector3> v, List<Vector3> n, List<Vector2> b)>();
        foreach (var bm in map.Meshes)
        {
            if (!byKind.TryGetValue(bm.Kind, out var lists)) byKind[bm.Kind] = lists = (new(), new(), new());
            var pm = bm.Mesh;
            // Every vertex carries its mesh's lowest height (UV2.x), so the shader can hide whole meshes by level.
            float baseZ = float.MaxValue;
            foreach (var v in pm.Vertices) baseZ = Mathf.Min(baseZ, v.Z);
            var b = new Vector2(baseZ, 0);
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
                    lists.b.Add(b); lists.b.Add(b); lists.b.Add(b);
                }
            }
        }

        foreach (var (kind, (v, n, b)) in byKind)
        {
            if (v.Count == 0) continue;
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = n.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV2] = b.ToArray();
            foreach (var target in new[] { mesh, kind == MeshKind.Ceiling ? ceilings : preview })
            {
                target.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                target.SurfaceSetMaterial(target.GetSurfaceCount() - 1, MaterialFor(kind));
            }
        }
        _instance.Mesh = preview;
        _ceilings.Mesh = ceilings;
        if (_semantic == null) return;
        // The capture copies share the mesh; the semantic one swaps each kind's surface for its flat colour.
        _semantic.Mesh = _depth.Mesh = mesh;
        int surface = 0;
        foreach (var (kind, (v, _, _)) in byKind)
            if (v.Count > 0) _semantic.SetSurfaceOverrideMaterial(surface++, SemanticMaterialFor(kind));
    }
}
