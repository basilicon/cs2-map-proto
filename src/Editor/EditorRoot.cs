using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Cs2MapProto.Export;
using Cs2MapProto.Geometry;
using Cs2MapProto.Model;

namespace Cs2MapProto.Editor;

public enum HandleType { None, PathPoint, RegionPoint, Spawn }

public readonly record struct Handle(HandleType Type, int Shape, int Point)
{
    public static readonly Handle None = new(HandleType.None, -1, -1);
    public bool IsNone => Type == HandleType.None;
}

/// <summary>Owns the document and turns input into edits. Rebuilds the preview from MapBuilder whenever
/// the document changes, so the viewport always shows exactly what gets exported.</summary>
public partial class EditorRoot : Node3D
{
    [Export] public EditorUi Ui { get; set; }
    [Export] public CameraRig Rig { get; set; }
    [Export] public PreviewRenderer Preview { get; set; }
    [Export] public OverlayRenderer Overlay { get; set; }

    public MapDocument Doc { get; private set; } = MapDocument.CreateDemo();
    public Tool CurrentTool { get; private set; } = Tool.Select;
    public Handle Selected { get; private set; } = Handle.None;
    public Handle Hovered { get; private set; } = Handle.None;
    public float WorkZ { get; private set; }
    public readonly List<Vec3> Draft = new();
    public Vector3? CursorHammer { get; private set; }
    public BuiltMap Built { get; private set; }

    readonly Stack<string> _undo = new(), _redo = new();
    string _docPath;
    bool _dirtyGeometry = true;
    bool _busy;

    // Drag state
    bool _dragging, _dragVertical, _dragPushedUndo;
    Vector2 _dragStartMouse;
    float _dragStartZ, _dragAccumZ;

    const float PickRadiusPx = 10f;

    public override void _Ready()
    {
        if (TryRunCli()) return;

        Ui.ToolChosen += SetTool;
        Ui.NewRequested += () => { PushUndo(); Doc = new MapDocument(); _docPath = null; AfterDocReplaced("New map"); };
        Ui.OpenChosen += path =>
        {
            try { PushUndo(); Doc = MapDocument.Load(path); _docPath = path; AfterDocReplaced($"Opened {path}"); }
            catch (Exception e) { Ui.SetStatus($"Open failed: {e.Message}"); }
        };
        Ui.SaveChosen += SaveTo;
        Ui.ExportRequested += () => _ = ExportAsync();
        Ui.ViewToggled += () => Rig.ToggleMode();
        Ui.PropertiesEdited += ApplyPropertyEdits;
        Ui.SettingsEdited += ApplySettingsEdits;

        AfterDocReplaced("Demo layout loaded. 1-7 tools, Tab toggles view, F frames, Ctrl+E exports.");
    }

    // ------------------------------------------------------------------ CLI (headless testing / scripting)

    /// <summary>godot --headless -- --export &lt;in.json|demo&gt; &lt;out.vmap&gt; [--compile]</summary>
    bool TryRunCli()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--export");
        if (i < 0 || i + 2 >= args.Length) return false;
        try
        {
            var doc = args[i + 1] == "demo" ? MapDocument.CreateDemo() : MapDocument.Load(args[i + 1]);
            var map = MapBuilder.Build(doc);
            foreach (var w in map.Warnings) GD.Print("warning: " + w);
            GD.Print(VmapExporter.Export(map, args[i + 2]));
            if (args.Contains("--compile")) GD.Print(Cs2Tools.Compile(args[i + 2]).summary);
        }
        catch (Exception e) { GD.PrintErr(e.ToString()); }
        GetTree().Quit();
        return true;
    }

    // ------------------------------------------------------------------ document lifecycle

    void AfterDocReplaced(string status)
    {
        Selected = Handle.None;
        Draft.Clear();
        SyncSettingsToUi();
        RefreshSelectionUi();
        _dirtyGeometry = true;
        RebuildNow();
        if (Built != null && Built.Meshes.Count > 0) FrameAll();
        Ui.SetStatus(status);
    }

    void PushUndo()
    {
        _undo.Push(Doc.ToJson());
        _redo.Clear();
        if (_undo.Count > 200) { var keep = _undo.Take(200).Reverse().ToList(); _undo.Clear(); keep.ForEach(_undo.Push); }
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Doc.ToJson());
        Doc = MapDocument.FromJson(_undo.Pop());
        AfterEdit(keepSelection: false);
        Ui.SetStatus("Undo");
    }

    void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Doc.ToJson());
        Doc = MapDocument.FromJson(_redo.Pop());
        AfterEdit(keepSelection: false);
        Ui.SetStatus("Redo");
    }

    void AfterEdit(bool keepSelection = true)
    {
        if (!keepSelection || !HandleExists(Selected)) Selected = Handle.None;
        _dirtyGeometry = true;
        RefreshSelectionUi();
    }

    void SaveTo(string path)
    {
        try { Doc.Save(path); _docPath = path; Ui.SetStatus($"Saved {path}"); }
        catch (Exception e) { Ui.SetStatus($"Save failed: {e.Message}"); }
    }

    void RebuildNow()
    {
        if (!_dirtyGeometry) return;
        _dirtyGeometry = false;
        try
        {
            Built = MapBuilder.Build(Doc);
            Preview.Show(Built);
            Ui.Warnings.Text = Built.Warnings.Count == 0 ? "No warnings." : string.Join("\n", Built.Warnings.Distinct().Select(w => "• " + w));
        }
        catch (Exception e)
        {
            Ui.Warnings.Text = "Build error: " + e.Message;
            GD.PrintErr(e);
        }
    }

    void FrameAll()
    {
        var pts = AllHandles().Select(h => Space.ToGodot(PositionOf(h))).ToList();
        if (pts.Count == 0) return;
        var box = new Aabb(pts[0], Vector3.Zero);
        foreach (var p in pts) box = box.Expand(p);
        Rig.Frame(box);
    }

    // ------------------------------------------------------------------ export

    async Task ExportAsync()
    {
        if (_busy) return;
        ApplySettingsEdits();
        var path = Doc.Export.VmapPath;
        if (string.IsNullOrWhiteSpace(path)) { Ui.ExportDialog.PopupCentered(new Vector2I(900, 600)); Ui.SetStatus("Choose where to write the .vmap first."); return; }
        RebuildNow();
        _busy = true;
        Ui.SetStatus("Exporting...");
        var map = Built;
        bool binary = Doc.Export.ConvertToBinary, compile = Doc.Export.CompileAfterExport;
        try
        {
            string result = await Task.Run(() =>
            {
                var msg = VmapExporter.Export(map, path, binary ? Cs2Tools.Tool(path, "dmxconvert.exe") : null);
                if (compile) msg += " | " + Cs2Tools.Compile(path).summary;
                return msg;
            });
            Ui.SetStatus(result);
            if (_docPath != null) Doc.Save(_docPath);
        }
        catch (Exception e) { Ui.SetStatus($"Export failed: {e.Message}"); GD.PrintErr(e); }
        finally { _busy = false; }
    }

    // ------------------------------------------------------------------ handles

    public IEnumerable<Handle> AllHandles()
    {
        for (int s = 0; s < Doc.Paths.Count; s++)
            for (int p = 0; p < Doc.Paths[s].Points.Count; p++) yield return new Handle(HandleType.PathPoint, s, p);
        for (int s = 0; s < Doc.Regions.Count; s++)
            for (int p = 0; p < Doc.Regions[s].Points.Count; p++) yield return new Handle(HandleType.RegionPoint, s, p);
        for (int s = 0; s < Doc.Spawns.Count; s++) yield return new Handle(HandleType.Spawn, s, 0);
    }

    bool HandleExists(Handle h) => h.Type switch
    {
        HandleType.PathPoint => h.Shape < Doc.Paths.Count && h.Point < Doc.Paths[h.Shape].Points.Count,
        HandleType.RegionPoint => h.Shape < Doc.Regions.Count && h.Point < Doc.Regions[h.Shape].Points.Count,
        HandleType.Spawn => h.Shape < Doc.Spawns.Count,
        _ => false,
    };

    public Vec3 PositionOf(Handle h) => h.Type switch
    {
        HandleType.PathPoint => Doc.Paths[h.Shape].Points[h.Point],
        HandleType.RegionPoint => new Vec3(Doc.Regions[h.Shape].Points[h.Point].X, Doc.Regions[h.Shape].Points[h.Point].Y, Doc.Regions[h.Shape].Elevation),
        HandleType.Spawn => Doc.Spawns[h.Shape].Position,
        _ => default,
    };

    void SetPosition(Handle h, Vec3 v)
    {
        switch (h.Type)
        {
            case HandleType.PathPoint: Doc.Paths[h.Shape].Points[h.Point] = v; break;
            case HandleType.RegionPoint:
                Doc.Regions[h.Shape].Points[h.Point] = new Vec3(v.X, v.Y, 0);
                Doc.Regions[h.Shape].Elevation = v.Z; // a region is flat: vertical moves shift the whole region
                break;
            case HandleType.Spawn: Doc.Spawns[h.Shape].Position = v; break;
        }
    }

    Handle PickHandle(Vector2 mouse)
    {
        var cam = Rig.Camera;
        Handle best = Handle.None; float bestD = PickRadiusPx;
        foreach (var h in AllHandles())
        {
            var g = Space.ToGodot(PositionOf(h));
            if (cam.IsPositionBehind(g)) continue;
            float d = cam.UnprojectPosition(g).DistanceTo(mouse);
            // Prefer the selected shape's points when they overlap others.
            if (h.Type == Selected.Type && h.Shape == Selected.Shape) d -= 2f;
            if (d < bestD) { bestD = d; best = h; }
        }
        return best;
    }

    /// <summary>Finds the path or region edge under the mouse, for inserting points (Ctrl+click).</summary>
    (HandleType type, int shape, int insertAt) PickEdge(Vector2 mouse)
    {
        var cam = Rig.Camera;
        (HandleType, int, int) best = (HandleType.None, -1, -1);
        float bestD = PickRadiusPx;
        void Test(HandleType t, int s, Vec3 a, Vec3 b, int insertAt)
        {
            var ga = Space.ToGodot(a); var gb = Space.ToGodot(b);
            if (cam.IsPositionBehind(ga) || cam.IsPositionBehind(gb)) return;
            var sa = cam.UnprojectPosition(ga); var sb = cam.UnprojectPosition(gb);
            var closest = Geometry2D.GetClosestPointToSegment(mouse, sa, sb);
            float d = closest.DistanceTo(mouse);
            if (d < bestD) { bestD = d; best = (t, s, insertAt); }
        }
        for (int s = 0; s < Doc.Paths.Count; s++)
        {
            var p = Doc.Paths[s].Points;
            for (int i = 0; i + 1 < p.Count; i++) Test(HandleType.PathPoint, s, p[i], p[i + 1], i + 1);
        }
        for (int s = 0; s < Doc.Regions.Count; s++)
        {
            var r = Doc.Regions[s];
            for (int i = 0; i < r.Points.Count; i++)
            {
                var a = r.Points[i]; var b = r.Points[(i + 1) % r.Points.Count];
                Test(HandleType.RegionPoint, s, new Vec3(a.X, a.Y, r.Elevation), new Vec3(b.X, b.Y, r.Elevation), i + 1);
            }
        }
        return best;
    }

    Vector3? PickPlane(Vector2 mouse, float hammerZ)
    {
        var cam = Rig.Camera;
        var o = cam.ProjectRayOrigin(mouse);
        var d = cam.ProjectRayNormal(mouse);
        if (Mathf.Abs(d.Y) < 1e-5f) return null;
        float t = (hammerZ - o.Y) / d.Y;
        if (t < 0) return null;
        var g = o + d * t;
        var h = Space.ToHammer(g);
        return new Vector3(h.X, h.Y, hammerZ);
    }

    float Snap(float v) => Input.IsKeyPressed(Key.Alt) || Doc.GridSize <= 0 ? Mathf.Round(v) : Mathf.Round(v / Doc.GridSize) * Doc.GridSize;
    Vec3 SnapXY(Vector3 h) => new(Snap(h.X), Snap(h.Y), h.Z);

    // ------------------------------------------------------------------ input

    public override void _UnhandledInput(InputEvent e)
    {
        if (Rig.HandleInput(e)) { GetViewport().SetInputAsHandled(); return; }

        switch (e)
        {
            case InputEventKey { Pressed: true } k: HandleKey(k); break;
            case InputEventMouseButton mb: HandleMouseButton(mb); break;
            case InputEventMouseMotion mm: HandleMouseMotion(mm); break;
        }
    }

    void HandleKey(InputEventKey k)
    {
        bool ctrl = k.CtrlPressed;
        switch (k.Keycode)
        {
            case Key.Z when ctrl && k.ShiftPressed: Redo(); break;
            case Key.Z when ctrl: Undo(); break;
            case Key.Y when ctrl: Redo(); break;
            case Key.S when ctrl:
                if (_docPath != null) SaveTo(_docPath); else Ui.SaveDialog.PopupCentered(new Vector2I(900, 600));
                break;
            case Key.E when ctrl: _ = ExportAsync(); break;
            case Key.Tab: Rig.ToggleMode(); break;
            case Key.F when !ctrl: FrameAll(); break;
            case Key.Key1: SetTool(Tool.Select); break;
            case Key.Key2: SetTool(Tool.Path); break;
            case Key.Key3: SetTool(Tool.Floor); break;
            case Key.Key4: SetTool(Tool.Obstacle); break;
            case Key.Key5: SetTool(Tool.Pit); break;
            case Key.Key6: SetTool(Tool.SpawnT); break;
            case Key.Key7: SetTool(Tool.SpawnCT); break;
            case Key.Enter or Key.KpEnter: CommitDraft(); break;
            case Key.Escape:
                if (Draft.Count > 0) { Draft.Clear(); Ui.SetStatus("Drawing cancelled"); }
                else { Selected = Handle.None; RefreshSelectionUi(); }
                break;
            case Key.Backspace when Draft.Count > 0: Draft.RemoveAt(Draft.Count - 1); break;
            case Key.Delete or Key.Backspace: DeleteSelected(k.ShiftPressed); break;
            case Key.Pageup: Nudge(+StepZ()); break;
            case Key.Pagedown: Nudge(-StepZ()); break;
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    float StepZ() => Doc.GridSize > 0 ? Doc.GridSize : 16f;

    /// <summary>PageUp/PageDown: raise/lower the selected point (or whole region / spawn); while drawing, the working height.</summary>
    void Nudge(float dz)
    {
        if (CurrentTool != Tool.Select || Selected.IsNone)
        {
            WorkZ += dz;
            Ui.Silently(() => Ui.WorkElevation.Value = WorkZ);
            Ui.SetStatus($"Working elevation {WorkZ}");
            return;
        }
        PushUndo();
        var p = PositionOf(Selected);
        SetPosition(Selected, new Vec3(p.X, p.Y, p.Z + dz));
        AfterEdit();
    }

    void HandleMouseButton(InputEventMouseButton mb)
    {
        // Shift+wheel: vertical offset (same as PageUp/PageDown).
        if (mb.Pressed && mb.ShiftPressed && (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown))
        {
            Nudge(mb.ButtonIndex == MouseButton.WheelUp ? StepZ() : -StepZ());
            GetViewport().SetInputAsHandled();
            return;
        }

        if (mb.ButtonIndex == MouseButton.Right && mb.Pressed && Rig.IsTopDown)
        {
            if (Draft.Count > 0) CommitDraft();
            else { Selected = Handle.None; RefreshSelectionUi(); }
            GetViewport().SetInputAsHandled();
            return;
        }

        if (mb.ButtonIndex != MouseButton.Left) return;
        if (Input.IsKeyPressed(Key.Space)) return; // space-drag pans

        if (!mb.Pressed)
        {
            if (_dragging) { _dragging = false; Ui.SetStatus("Moved"); }
            return;
        }

        switch (CurrentTool)
        {
            case Tool.Select: SelectClick(mb); break;
            case Tool.Path or Tool.Floor or Tool.Obstacle or Tool.Pit: DraftClick(mb); break;
            case Tool.SpawnT or Tool.SpawnCT:
                if (PickPlane(mb.Position, WorkZ) is Vector3 h)
                {
                    PushUndo();
                    Doc.Spawns.Add(new SpawnPoint { Team = CurrentTool == Tool.SpawnT ? Team.T : Team.CT, Position = SnapXY(h), Yaw = (float)Ui.SpawnYaw.Value });
                    Selected = new Handle(HandleType.Spawn, Doc.Spawns.Count - 1, 0);
                    AfterEdit();
                }
                break;
        }
        GetViewport().SetInputAsHandled();
    }

    void SelectClick(InputEventMouseButton mb)
    {
        if (mb.CtrlPressed)
        {
            var (type, shape, at) = PickEdge(mb.Position);
            if (type == HandleType.None) return;
            float z = type == HandleType.PathPoint
                ? (Doc.Paths[shape].Points[at - 1].Z + Doc.Paths[shape].Points[at].Z) * 0.5f
                : Doc.Regions[shape].Elevation;
            if (PickPlane(mb.Position, z) is not Vector3 h) return;
            PushUndo();
            var v = SnapXY(h);
            if (type == HandleType.PathPoint) Doc.Paths[shape].Points.Insert(at, new Vec3(v.X, v.Y, Snap(z)));
            else Doc.Regions[shape].Points.Insert(at, new Vec3(v.X, v.Y, 0));
            Selected = new Handle(type, shape, at);
            AfterEdit();
            BeginDrag(mb.Position, pushedUndo: true);
            return;
        }

        Selected = PickHandle(mb.Position);
        RefreshSelectionUi();
        if (!Selected.IsNone) BeginDrag(mb.Position, pushedUndo: false);
    }

    void BeginDrag(Vector2 mouse, bool pushedUndo)
    {
        _dragging = true;
        _dragPushedUndo = pushedUndo;
        _dragStartMouse = mouse;
        _dragStartZ = PositionOf(Selected).Z;
        _dragAccumZ = 0;
        _dragVertical = Input.IsKeyPressed(Key.Shift);
    }

    void DraftClick(InputEventMouseButton mb)
    {
        // The working elevation sets each new point's height, so changing it mid-path makes a ramp.
        if (PickPlane(mb.Position, WorkZ) is not Vector3 h) return;
        var p = SnapXY(h);

        // Clicking the first point closes a polygon.
        if (CurrentTool != Tool.Path && Draft.Count >= 3)
        {
            var first = Rig.Camera.UnprojectPosition(Space.ToGodot(Draft[0]));
            if (first.DistanceTo(mb.Position) < PickRadiusPx) { CommitDraft(); return; }
        }
        if (Draft.Count > 0 && Mathf.IsEqualApprox(Draft[^1].X, p.X) && Mathf.IsEqualApprox(Draft[^1].Y, p.Y))
        {
            if (mb.DoubleClick) CommitDraft();
            return;
        }
        Draft.Add(p);
        Ui.SetStatus($"{CurrentTool}: {Draft.Count} point(s). Enter / right-click to finish, Backspace undoes a point, Esc cancels.");
    }

    void CommitDraft()
    {
        if (Draft.Count == 0) return;
        if (CurrentTool == Tool.Path)
        {
            if (Draft.Count < 2) { Ui.SetStatus("A path needs at least 2 points."); return; }
            PushUndo();
            Doc.Paths.Add(new PathShape { Points = new List<Vec3>(Draft), Width = (float)Ui.PathWidth.Value });
            Selected = new Handle(HandleType.PathPoint, Doc.Paths.Count - 1, Draft.Count - 1);
        }
        else
        {
            if (Draft.Count < 3) { Ui.SetStatus("A region needs at least 3 points."); return; }
            PushUndo();
            var kind = CurrentTool switch { Tool.Floor => Model.RegionKind.Floor, Tool.Obstacle => Model.RegionKind.Obstacle, _ => Model.RegionKind.Pit };
            float height = kind switch { Model.RegionKind.Obstacle => 128f, Model.RegionKind.Pit => 256f, _ => 0f };
            Doc.Regions.Add(new RegionShape
            {
                Kind = kind, Elevation = Draft[0].Z, Height = height,
                Points = Draft.Select(p => new Vec3(p.X, p.Y, 0)).ToList(),
            });
            Selected = new Handle(HandleType.RegionPoint, Doc.Regions.Count - 1, 0);
        }
        Draft.Clear();
        AfterEdit();
        Ui.SetStatus("Shape added");
    }

    void HandleMouseMotion(InputEventMouseMotion mm)
    {
        float hoverZ = !Selected.IsNone && CurrentTool == Tool.Select ? PositionOf(Selected).Z : WorkZ;
        CursorHammer = PickPlane(mm.Position, _dragging ? _dragStartZ : hoverZ);
        if (CursorHammer is Vector3 c) CursorHammer = new Vector3(Snap(c.X), Snap(c.Y), c.Z);

        if (!_dragging || Selected.IsNone)
        {
            Hovered = CurrentTool == Tool.Select ? PickHandle(mm.Position) : Handle.None;
            return;
        }
        if (!_dragPushedUndo)
        {
            if (mm.Position.DistanceTo(_dragStartMouse) < 3f) return; // ignore click jitter
            PushUndo();
            _dragPushedUndo = true;
        }

        var cur = PositionOf(Selected);
        if (_dragVertical)
        {
            _dragAccumZ += -mm.Relative.Y * Rig.WorldPerPixel(Space.ToGodot(cur));
            SetPosition(Selected, new Vec3(cur.X, cur.Y, Snap(_dragStartZ + _dragAccumZ)));
        }
        else if (PickPlane(mm.Position, _dragStartZ) is Vector3 h)
        {
            var v = SnapXY(h);
            SetPosition(Selected, new Vec3(v.X, v.Y, cur.Z));
        }
        AfterEdit();
        GetViewport().SetInputAsHandled();
    }

    void DeleteSelected(bool wholeShape)
    {
        if (Selected.IsNone) return;
        PushUndo();
        switch (Selected.Type)
        {
            case HandleType.PathPoint:
                var path = Doc.Paths[Selected.Shape];
                if (wholeShape || path.Points.Count <= 2) Doc.Paths.RemoveAt(Selected.Shape);
                else path.Points.RemoveAt(Selected.Point);
                break;
            case HandleType.RegionPoint:
                var r = Doc.Regions[Selected.Shape];
                if (wholeShape || r.Points.Count <= 3) Doc.Regions.RemoveAt(Selected.Shape);
                else r.Points.RemoveAt(Selected.Point);
                break;
            case HandleType.Spawn: Doc.Spawns.RemoveAt(Selected.Shape); break;
        }
        Selected = Handle.None;
        AfterEdit();
    }

    void SetTool(Tool t)
    {
        if (Draft.Count > 0 && t != CurrentTool) Draft.Clear();
        CurrentTool = t;
        Ui.SetTool(t);
        Ui.SetStatus(t switch
        {
            Tool.Select => "Select: click/drag points (Shift+drag = vertical), Ctrl+click an edge inserts a point, Del removes (Shift+Del removes shape).",
            Tool.Path => "Path: click points at the working elevation (PgUp/PgDn or Shift+wheel changes it, so the next point ramps). Enter finishes.",
            Tool.Floor => "Floor: click a polygon outline; Enter or click the first point closes it.",
            Tool.Obstacle => "Obstacle: click a polygon outline; solid block from the working elevation up.",
            Tool.Pit => "Pit: outline a hole in the floor at the working elevation.",
            _ => "Spawn: click to place.",
        });
    }

    // ------------------------------------------------------------------ properties panel

    void RefreshSelectionUi()
    {
        Ui.Silently(() =>
        {
            Ui.PathWidth.Editable = Ui.RegionHeight.Editable = Ui.PointZ.Editable = Ui.SpawnYaw.Editable = false;
            Ui.RegionKind.Disabled = true;
            switch (Selected.Type)
            {
                case HandleType.PathPoint:
                    var p = Doc.Paths[Selected.Shape];
                    Ui.SelectionLabel.Text = $"Path {Selected.Shape}, point {Selected.Point}/{p.Points.Count - 1}";
                    Ui.PathWidth.Value = p.Width; Ui.PathWidth.Editable = true;
                    Ui.PointZ.Value = p.Points[Selected.Point].Z; Ui.PointZ.Editable = true;
                    break;
                case HandleType.RegionPoint:
                    var r = Doc.Regions[Selected.Shape];
                    Ui.SelectionLabel.Text = $"{r.Kind} region {Selected.Shape}, point {Selected.Point}";
                    Ui.RegionKind.Selected = (int)r.Kind; Ui.RegionKind.Disabled = false;
                    Ui.PointZ.Value = r.Elevation; Ui.PointZ.Editable = true;
                    Ui.RegionHeight.Value = r.Height; Ui.RegionHeight.Editable = r.Kind != Model.RegionKind.Floor;
                    break;
                case HandleType.Spawn:
                    var s = Doc.Spawns[Selected.Shape];
                    Ui.SelectionLabel.Text = $"{s.Team} spawn {Selected.Shape}";
                    Ui.PointZ.Value = s.Position.Z; Ui.PointZ.Editable = true;
                    Ui.SpawnYaw.Value = s.Yaw; Ui.SpawnYaw.Editable = true;
                    break;
                default:
                    Ui.SelectionLabel.Text = "Nothing selected (width/yaw apply to new shapes)";
                    Ui.PathWidth.Editable = Ui.SpawnYaw.Editable = true;
                    break;
            }
        });
    }

    void ApplyPropertyEdits()
    {
        if (Selected.IsNone) return;
        PushUndo();
        switch (Selected.Type)
        {
            case HandleType.PathPoint:
                var p = Doc.Paths[Selected.Shape];
                p.Width = (float)Ui.PathWidth.Value;
                var pt = p.Points[Selected.Point];
                p.Points[Selected.Point] = new Vec3(pt.X, pt.Y, (float)Ui.PointZ.Value);
                break;
            case HandleType.RegionPoint:
                var r = Doc.Regions[Selected.Shape];
                r.Kind = (Model.RegionKind)Ui.RegionKind.Selected;
                r.Elevation = (float)Ui.PointZ.Value;
                r.Height = (float)Ui.RegionHeight.Value;
                break;
            case HandleType.Spawn:
                var s = Doc.Spawns[Selected.Shape];
                s.Position = new Vec3(s.Position.X, s.Position.Y, (float)Ui.PointZ.Value);
                s.Yaw = (float)Ui.SpawnYaw.Value;
                break;
        }
        AfterEdit();
    }

    void SyncSettingsToUi()
    {
        Ui.Silently(() =>
        {
            Ui.GridSize.Value = Doc.GridSize;
            Ui.WorkElevation.Value = WorkZ;
            Ui.PerimeterWalls.ButtonPressed = Doc.Build.PerimeterWalls;
            Ui.WallHeight.Value = Doc.Build.WallHeight;
            Ui.FloorThickness.Value = Doc.Build.FloorThickness;
            Ui.VmapPath.Text = Doc.Export.VmapPath;
            Ui.ConvertBinary.ButtonPressed = Doc.Export.ConvertToBinary;
            Ui.CompileAfter.ButtonPressed = Doc.Export.CompileAfterExport;
        });
    }

    void ApplySettingsEdits()
    {
        Doc.GridSize = (float)Ui.GridSize.Value;
        Doc.Build.PerimeterWalls = Ui.PerimeterWalls.ButtonPressed;
        Doc.Build.WallHeight = (float)Ui.WallHeight.Value;
        Doc.Build.FloorThickness = (float)Ui.FloorThickness.Value;
        Doc.Export.VmapPath = Ui.VmapPath.Text.Trim();
        Doc.Export.ConvertToBinary = Ui.ConvertBinary.ButtonPressed;
        Doc.Export.CompileAfterExport = Ui.CompileAfter.ButtonPressed;
        _dirtyGeometry = true;
    }

    int _frames;

    public override void _Process(double delta)
    {
        // Debug aid: --screenshot <png> [--free] saves the viewport after a few frames and quits.
        if (++_frames == 30)
        {
            var args = OS.GetCmdlineUserArgs();
            int si = Array.IndexOf(args, "--screenshot");
            if (si >= 0 && si + 1 < args.Length)
            {
                if (args.Contains("--free") && Rig.IsTopDown) { Rig.ToggleMode(); _frames = 0; }
                else { GetViewport().GetTexture().GetImage().SavePng(args[si + 1]); GetTree().Quit(); }
            }
        }

        if (Ui.WorkElevation != null && !Mathf.IsEqualApprox((float)Ui.WorkElevation.Value, WorkZ))
            WorkZ = (float)Ui.WorkElevation.Value;
        RebuildNow();
        Overlay?.Draw(this);
    }
}
