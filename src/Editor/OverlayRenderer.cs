using System.Collections.Generic;
using Godot;
using Cs2MapProto.Geometry;
using Cs2MapProto.Model;

namespace Cs2MapProto.Editor;

/// <summary>Immediate-mode editor overlay: grid at the working elevation, shape outlines, point handles,
/// spawns, the in-progress draft and the cursor. Redrawn every frame (it's a few hundred vertices).</summary>
public partial class OverlayRenderer : Node3D
{
    MeshInstance3D _onTop, _inWorld;
    ImmediateMesh _onTopMesh, _inWorldMesh;
    StandardMaterial3D _onTopMat, _inWorldMat;

    static readonly Color PathColor = new(0.35f, 0.85f, 1f);
    static readonly Color FloorColor = new(0.6f, 0.9f, 0.6f);
    static readonly Color ObstacleColor = new(1f, 0.6f, 0.3f);
    static readonly Color PitColor = new(0.85f, 0.4f, 0.9f);
    static readonly Color SelectedColor = new(1f, 1f, 0.2f);
    static readonly Color HoverColor = new(1f, 1f, 1f);
    static readonly Color DraftColor = new(1f, 0.95f, 0.4f);
    static readonly Color TangentColor = new(1f, 0.45f, 0.75f);
    static readonly Color CurvedPointColor = new(0.55f, 0.95f, 1f);
    static readonly Color TColor = new(0.95f, 0.65f, 0.2f);
    static readonly Color CTColor = new(0.35f, 0.55f, 1f);

    public override void _Ready()
    {
        _onTopMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            NoDepthTest = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            RenderPriority = 10,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _inWorldMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _onTopMesh = new ImmediateMesh();
        _inWorldMesh = new ImmediateMesh();
        _onTop = new MeshInstance3D { Mesh = _onTopMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _inWorld = new MeshInstance3D { Mesh = _inWorldMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        // Own layer, so off-screen captures (SceneCapture) leave the handles and grid out.
        _onTop.Layers = _inWorld.Layers = PreviewRenderer.OverlayLayer;
        AddChild(_inWorld);
        AddChild(_onTop);
    }

    readonly List<(Vector3, Color)> _lines = new();
    readonly List<(Vector3, Color)> _tris = new();
    readonly List<(Vector3, Color)> _gridLines = new();

    void Line(Vec3 a, Vec3 b, Color c) { _lines.Add((Space.ToGodot(a), c)); _lines.Add((Space.ToGodot(b), c)); }

    public void Draw(EditorRoot ed)
    {
        _lines.Clear(); _tris.Clear(); _gridLines.Clear();
        var doc = ed.Doc;

        DrawGrid(ed);

        // Path centrelines (flattened, so curves show as curves).
        for (int s = 0; s < doc.Paths.Count; s++)
        {
            bool sel = ed.SelectedPath == s;
            bool whole = sel && ed.Selected.IsWhole;
            var col = whole ? SelectedColor : sel ? SelectedColor with { A = 0.75f } : PathColor;
            var samples = PathSampler.Flatten(doc.Paths[s]);
            for (int i = 0; i + 1 < samples.Count; i++)
                Line(Lift(V(samples[i].P)), Lift(V(samples[i + 1].P)), col);
        }
        for (int s = 0; s < doc.Regions.Count; s++)
        {
            var r = doc.Regions[s];
            bool sel = ed.SelectedRegion == s;
            var col = sel ? SelectedColor : r.Kind switch { RegionKind.Floor => FloorColor, RegionKind.Obstacle => ObstacleColor, _ => PitColor };
            float top = r.Kind == RegionKind.Obstacle ? r.Elevation + r.Height : r.Elevation;
            for (int i = 0; i < r.Points.Count; i++)
            {
                var a = r.Points[i]; var b = r.Points[(i + 1) % r.Points.Count];
                Line(new Vec3(a.X, a.Y, top + 1), new Vec3(b.X, b.Y, top + 1), col);
            }
        }

        // Bezier handles of the selected path.
        foreach (var h in ed.TangentHandles())
        {
            if (h.Type != HandleType.TangentOut) continue;
            var pIn = ed.PositionOf(h with { Type = HandleType.TangentIn });
            var pOut = ed.PositionOf(h);
            Line(Lift(pIn), Lift(pOut), TangentColor with { A = 0.7f });
        }

        // Draft.
        var draft = ed.Draft;
        for (int i = 0; i + 1 < draft.Count; i++) Line(Lift(draft[i]), Lift(draft[i + 1]), DraftColor);
        if (draft.Count > 0 && ed.CursorHammer is Vector3 c)
        {
            var cv = new Vec3(c.X, c.Y, ed.WorkZ);
            Line(Lift(draft[^1]), Lift(cv), DraftColor with { A = 0.6f });
            if (ed.CurrentTool != Tool.Path && draft.Count >= 2) Line(Lift(cv), Lift(draft[0]), DraftColor with { A = 0.3f });
        }

        // Handles.
        foreach (var h in ed.AllHandles())
        {
            var pos = ed.PositionOf(h);
            Color col;
            float size = 5f;
            bool inWholeSelection = ed.Selected.IsWhole && h.Type == ed.Selected.Type && h.Shape == ed.Selected.Shape;
            if (h == ed.Selected || inWholeSelection) { col = SelectedColor; size = h == ed.Selected ? 7f : 6f; }
            else if (h == ed.Hovered) { col = HoverColor; size = 7f; }
            else if (h.Type == HandleType.Spawn) col = doc.Spawns[h.Shape].Team == Team.T ? TColor : CTColor;
            else if (h.Type == HandleType.PathPoint) col = doc.Paths[h.Shape].IsCurved(h.Point) ? CurvedPointColor : PathColor;
            else col = doc.Regions[h.Shape].Kind switch { RegionKind.Floor => FloorColor, RegionKind.Obstacle => ObstacleColor, _ => PitColor };
            Square(ed, Space.ToGodot(Lift(pos)), size, col);
            if (h.Type == HandleType.Spawn) SpawnMarker(ed, doc.Spawns[h.Shape], col);
        }
        foreach (var h in ed.TangentHandles())
        {
            var col = h == ed.Selected ? SelectedColor : h == ed.Hovered ? HoverColor : TangentColor;
            Diamond(ed, Space.ToGodot(Lift(ed.PositionOf(h))), h == ed.Selected ? 7f : 5.5f, col);
        }
        foreach (var d in draft) Square(ed, Space.ToGodot(Lift(d)), 5f, DraftColor);
        if (ed.CursorHammer is Vector3 cur && ed.CurrentTool != Tool.Select)
            Square(ed, Space.ToGodot(new Vec3(cur.X, cur.Y, ed.WorkZ)), 4f, DraftColor with { A = 0.7f });

        Flush(_onTopMesh, _onTopMat, _lines, _tris);
        Flush(_inWorldMesh, _inWorldMat, _gridLines, null);
    }

    static Vec3 Lift(Vec3 v) => new(v.X, v.Y, v.Z + 1f);
    static Vec3 V(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    void Diamond(EditorRoot ed, Vector3 g, float px, Color c)
    {
        var cam = ed.Rig.Camera;
        float r = ed.Rig.WorldPerPixel(g) * px;
        var right = cam.GlobalBasis.X * r; var up = cam.GlobalBasis.Y * r;
        _tris.Add((g - right, c)); _tris.Add((g + up, c)); _tris.Add((g + right, c));
        _tris.Add((g - right, c)); _tris.Add((g + right, c)); _tris.Add((g - up, c));
    }

    void Square(EditorRoot ed, Vector3 g, float px, Color c)
    {
        var cam = ed.Rig.Camera;
        float r = ed.Rig.WorldPerPixel(g) * px;
        var right = cam.GlobalBasis.X * r; var up = cam.GlobalBasis.Y * r;
        var a = g - right - up; var b = g + right - up; var cc = g + right + up; var d = g - right + up;
        _tris.Add((a, c)); _tris.Add((cc, c)); _tris.Add((b, c));
        _tris.Add((a, c)); _tris.Add((d, c)); _tris.Add((cc, c));
    }

    void SpawnMarker(EditorRoot ed, SpawnPoint s, Color c)
    {
        // Player-sized box outline (32x32x72) plus a facing arrow.
        var p = s.Position;
        float yaw = Mathf.DegToRad(s.Yaw);
        var f = new Vec3(Mathf.Cos(yaw) * 40, Mathf.Sin(yaw) * 40, 0);
        Line(new Vec3(p.X, p.Y, p.Z + 2), new Vec3(p.X + f.X, p.Y + f.Y, p.Z + 2), c);
        for (int i = 0; i < 4; i++)
        {
            float a0 = Mathf.Pi / 4 + i * Mathf.Pi / 2, a1 = a0 + Mathf.Pi / 2;
            var q0 = new Vec3(p.X + Mathf.Cos(a0) * 22.6f, p.Y + Mathf.Sin(a0) * 22.6f, p.Z);
            var q1 = new Vec3(p.X + Mathf.Cos(a1) * 22.6f, p.Y + Mathf.Sin(a1) * 22.6f, p.Z);
            Line(q0, q1, c);
            Line(new Vec3(q0.X, q0.Y, p.Z + 72), new Vec3(q1.X, q1.Y, p.Z + 72), c);
            Line(q0, new Vec3(q0.X, q0.Y, p.Z + 72), c);
        }
    }

    void DrawGrid(EditorRoot ed)
    {
        var cam = ed.Rig.Camera;
        var vp = ed.GetViewport().GetVisibleRect().Size;
        // Centre the grid on where the camera looks at the working plane.
        var o = cam.ProjectRayOrigin(vp / 2); var d = cam.ProjectRayNormal(vp / 2);
        float z = ed.WorkZ;
        Vector3 center = Mathf.Abs(d.Y) > 1e-4f && (z - o.Y) / d.Y > 0 ? o + d * ((z - o.Y) / d.Y) : new Vector3(o.X, z, o.Z);
        float extent = ed.Rig.IsTopDown ? ed.Rig.TopDownSize * 0.9f * Mathf.Max(1f, vp.X / vp.Y) : 4096f;
        float step = Mathf.Max(ed.Doc.GridSize, 16f);
        while (extent / step > 80) step *= 2;
        float x0 = Mathf.Floor((center.X - extent) / step) * step, x1 = center.X + extent;
        float z0 = Mathf.Floor((center.Z - extent) / step) * step, z1 = center.Z + extent;
        float y = z - 0.5f;
        for (float x = x0; x <= x1; x += step)
        {
            var col = Mathf.IsZeroApprox(Mathf.PosMod(x, step * 8)) ? new Color(1, 1, 1, 0.22f) : new Color(1, 1, 1, 0.08f);
            _gridLines.Add((new Vector3(x, y, z0), col)); _gridLines.Add((new Vector3(x, y, z1), col));
        }
        for (float zz = z0; zz <= z1; zz += step)
        {
            var col = Mathf.IsZeroApprox(Mathf.PosMod(zz, step * 8)) ? new Color(1, 1, 1, 0.22f) : new Color(1, 1, 1, 0.08f);
            _gridLines.Add((new Vector3(x0, y, zz), col)); _gridLines.Add((new Vector3(x1, y, zz), col));
        }
    }

    static void Flush(ImmediateMesh mesh, Material mat, List<(Vector3, Color)> lines, List<(Vector3, Color)> tris)
    {
        mesh.ClearSurfaces();
        if (lines.Count > 0)
        {
            mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, mat);
            foreach (var (p, c) in lines) { mesh.SurfaceSetColor(c); mesh.SurfaceAddVertex(p); }
            mesh.SurfaceEnd();
        }
        if (tris != null && tris.Count > 0)
        {
            mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, mat);
            foreach (var (p, c) in tris) { mesh.SurfaceSetColor(c); mesh.SurfaceAddVertex(p); }
            mesh.SurfaceEnd();
        }
    }
}
