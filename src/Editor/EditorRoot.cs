using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Cs2MapProto.Export;
using Cs2MapProto.Geometry;
using Cs2MapProto.Model;

namespace Cs2MapProto.Editor;

public enum HandleType { None, PathPoint, RegionPoint, Spawn, TangentOut, TangentIn }

/// <summary>Something the user can pick. Point = -1 on a PathPoint/RegionPoint handle means the whole shape.</summary>
public readonly record struct Handle(HandleType Type, int Shape, int Point)
{
    public static readonly Handle None = new(HandleType.None, -1, -1);
    public bool IsNone => Type == HandleType.None;
    public bool IsWhole => Point < 0 && Type is HandleType.PathPoint or HandleType.RegionPoint;
    public bool IsTangent => Type is HandleType.TangentOut or HandleType.TangentIn;
    public static Handle WholePath(int s) => new(HandleType.PathPoint, s, -1);
    public static Handle WholeRegion(int s) => new(HandleType.RegionPoint, s, -1);
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

    /// <summary>Path whose curve handles are shown (selected path, point or tangent), or -1.</summary>
    public int SelectedPath => Selected.Type is HandleType.PathPoint or HandleType.TangentOut or HandleType.TangentIn ? Selected.Shape : -1;
    public int SelectedRegion => Selected.Type == HandleType.RegionPoint ? Selected.Shape : -1;

    readonly Stack<string> _undo = new(), _redo = new();
    string _docPath;
    bool _dirtyGeometry = true;
    bool _busy;
    Handle _branchFrom = Handle.None; // path point the current Path draft started on

    // Drag state
    bool _dragging, _dragVertical, _dragPushedUndo;
    Vector2 _dragStartMouse;
    Vec3 _dragAnchor;
    float _dragAccumZ;
    List<(Handle h, Vec3 start)> _dragItems = new();

    const float PickRadiusPx = 10f;
    const float WeldEpsilon = 0.5f;

    public override void _Ready()
    {
        if (TryRunCli()) return;

        Ui.SetTool(CurrentTool);
        Ui.ToolChosen += SetTool;
        Ui.NewRequested += () => { PushUndo(); Doc = new MapDocument(); _docPath = null; AfterDocReplaced("New map"); };
        Ui.OpenChosen += path =>
        {
            try { PushUndo(); Doc = MapDocument.Load(path); _docPath = path; AfterDocReplaced($"Opened {path}"); }
            catch (Exception e) { Ui.SetStatus($"Open failed: {e.Message}"); }
        };
        Ui.SaveChosen += SaveTo;
        Ui.SaveRequested += SaveCurrent;
        Ui.ExportRequested += () => _ = ExportAsync();
        Ui.UndoRequested += Undo;
        Ui.RedoRequested += Redo;
        Ui.ViewModeChosen += top => { if (Rig.IsTopDown != top) Rig.ToggleMode(); };
        Ui.PropertiesEdited += ApplyPropertyEdits;
        Ui.SettingsEdited += () => { ApplySettingsEdits(); MarkUnsaved(); };
        Rig.RightClicked += HandleRightClick;
        Ui.ProfileKindChanged += RefreshProfileUi;
        Ui.ProfileEditStarted += PushUndo;
        Ui.ProfileChanged += ApplyProfileEdit;
        Ui.ProfileEnabledToggled += SetProfileEnabled;
        Ui.ProfileResetRequested += ResetProfile;

        // godot -- --load <layout.json> opens a layout at startup.
        var args = OS.GetCmdlineUserArgs();
        int li = Array.IndexOf(args, "--load");
        if (li >= 0 && li + 1 < args.Length)
        {
            try { Doc = MapDocument.Load(args[li + 1]); _docPath = args[li + 1]; AfterDocReplaced($"Opened {_docPath}"); return; }
            catch (Exception e) { GD.PrintErr($"--load failed: {e.Message}"); }
        }
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
        _unsaved = false;
        RefreshDocInfo();
    }

    bool _unsaved;

    void MarkUnsaved()
    {
        if (_unsaved) return;
        _unsaved = true;
        RefreshDocInfo();
    }

    void RefreshDocInfo() =>
        Ui.SetDocInfo(_docPath != null ? System.IO.Path.GetFileName(_docPath) : "Untitled layout", _unsaved);

    void PushUndo()
    {
        MarkUnsaved();
        _undo.Push(Doc.ToJson());
        _redo.Clear();
        if (_undo.Count > 200) { var keep = _undo.Take(200).Reverse().ToList(); _undo.Clear(); keep.ForEach(_undo.Push); }
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Doc.ToJson());
        Doc = MapDocument.FromJson(_undo.Pop());
        MarkUnsaved();
        AfterEdit(keepSelection: false);
        Ui.SetStatus("Undo");
    }

    void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Doc.ToJson());
        Doc = MapDocument.FromJson(_redo.Pop());
        MarkUnsaved();
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
        try { Doc.Save(path); _docPath = path; _unsaved = false; RefreshDocInfo(); Ui.SetStatus($"Saved {path}"); }
        catch (Exception e) { Ui.SetStatus($"Save failed: {e.Message}"); }
    }

    void SaveCurrent()
    {
        if (_docPath != null) SaveTo(_docPath);
        else Ui.SaveDialog.PopupCentered(new Vector2I(900, 600));
    }

    void RebuildNow()
    {
        if (!_dirtyGeometry) return;
        _dirtyGeometry = false;
        try
        {
            Built = MapBuilder.Build(Doc);
            Preview.Show(Built);
            Ui.SetWarnings(Built.Warnings.Distinct().ToList());
        }
        catch (Exception e)
        {
            Ui.SetWarnings(Array.Empty<string>(), "Build error: " + e.Message);
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
        if (string.IsNullOrWhiteSpace(path))
        {
            Ui.PromptExportPath(exportAfter: true);
            Ui.SetStatus("Choose where to write the .vmap: ...\\content\\csgo_addons\\<addon>\\maps\\<name>.vmap");
            return;
        }
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
                if (!Cs2Tools.IsInAddon(path, out _))
                    msg += " | Note: Hammer/the compiler only see maps in ...\\content\\csgo_addons\\<addon>\\maps\\";
                else if (compile) msg += " | " + Cs2Tools.Compile(path).summary;
                else msg += " | Hammer: reopen this file to see it. The game still has the old build until you compile (tick 'Compile after export').";
                return msg;
            });
            Ui.SetStatus(result);
            Ui.SetLastExport(true, $"Exported {DateTime.Now:HH:mm}");
            if (_docPath != null) { Doc.Save(_docPath); _unsaved = false; RefreshDocInfo(); }
        }
        catch (Exception e) { Ui.SetStatus($"Export failed: {e.Message}"); Ui.SetLastExport(false, "Export failed"); GD.PrintErr(e); }
        finally { _busy = false; }
    }

    // ------------------------------------------------------------------ handles

    /// <summary>Every point marker (path points, region points, spawns). Curve handles are separate.</summary>
    public IEnumerable<Handle> AllHandles()
    {
        for (int s = 0; s < Doc.Paths.Count; s++)
            for (int p = 0; p < Doc.Paths[s].Points.Count; p++) yield return new Handle(HandleType.PathPoint, s, p);
        for (int s = 0; s < Doc.Regions.Count; s++)
            for (int p = 0; p < Doc.Regions[s].Points.Count; p++) yield return new Handle(HandleType.RegionPoint, s, p);
        for (int s = 0; s < Doc.Spawns.Count; s++) yield return new Handle(HandleType.Spawn, s, 0);
    }

    /// <summary>Bezier handles of the selected path's curved points.</summary>
    public IEnumerable<Handle> TangentHandles()
    {
        int s = SelectedPath;
        if (s < 0 || s >= Doc.Paths.Count) yield break;
        var path = Doc.Paths[s];
        for (int i = 0; i < path.Points.Count; i++)
            if (path.IsCurved(i))
            {
                yield return new Handle(HandleType.TangentOut, s, i);
                yield return new Handle(HandleType.TangentIn, s, i);
            }
    }

    bool HandleExists(Handle h) => h.Type switch
    {
        HandleType.PathPoint or HandleType.TangentOut or HandleType.TangentIn =>
            h.Shape < Doc.Paths.Count && h.Point < Doc.Paths[h.Shape].Points.Count && (h.Point >= 0 || h.Type == HandleType.PathPoint),
        HandleType.RegionPoint => h.Shape < Doc.Regions.Count && h.Point < Doc.Regions[h.Shape].Points.Count,
        HandleType.Spawn => h.Shape < Doc.Spawns.Count,
        _ => false,
    };

    public Vec3 PositionOf(Handle h)
    {
        int pi = Math.Max(0, h.Point); // whole shapes are anchored on their first point
        switch (h.Type)
        {
            case HandleType.PathPoint: return Doc.Paths[h.Shape].Points[pi];
            case HandleType.RegionPoint:
                var r = Doc.Regions[h.Shape];
                return new Vec3(r.Points[pi].X, r.Points[pi].Y, r.Elevation);
            case HandleType.Spawn: return Doc.Spawns[h.Shape].Position;
            case HandleType.TangentOut or HandleType.TangentIn:
                var path = Doc.Paths[h.Shape];
                var p = path.Points[h.Point]; var t = path.TangentAt(h.Point);
                float sign = h.Type == HandleType.TangentOut ? 1 : -1;
                return new Vec3(p.X + t.X * sign, p.Y + t.Y * sign, p.Z);
        }
        return default;
    }

    /// <summary>Sets a single point handle (not whole shapes).</summary>
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
            case HandleType.TangentOut or HandleType.TangentIn:
                var path = Doc.Paths[h.Shape];
                var p = path.Points[h.Point];
                float sign = h.Type == HandleType.TangentOut ? 1 : -1;
                path.SetTangent(h.Point, new Vec3((v.X - p.X) * sign, (v.Y - p.Y) * sign, 0));
                break;
        }
    }

    static bool Same(Vec3 a, Vec3 b) =>
        MathF.Abs(a.X - b.X) < WeldEpsilon && MathF.Abs(a.Y - b.Y) < WeldEpsilon && MathF.Abs(a.Z - b.Z) < WeldEpsilon;

    /// <summary>The point handles that move together with h. Path points sitting exactly on top of each other
    /// (branch junctions, joined ends) are welded and move as one unless detach is set.</summary>
    List<Handle> MoveSet(Handle h, bool detach)
    {
        var set = new List<Handle>();
        switch (h.Type)
        {
            case HandleType.PathPoint when h.IsWhole:
                for (int i = 0; i < Doc.Paths[h.Shape].Points.Count; i++) set.Add(new Handle(HandleType.PathPoint, h.Shape, i));
                break;
            case HandleType.RegionPoint when h.IsWhole:
                for (int i = 0; i < Doc.Regions[h.Shape].Points.Count; i++) set.Add(new Handle(HandleType.RegionPoint, h.Shape, i));
                break;
            case HandleType.TangentOut or HandleType.TangentIn:
                set.Add(new Handle(HandleType.PathPoint, h.Shape, h.Point));
                break;
            default:
                set.Add(h);
                break;
        }
        if (detach) return set;

        // Pull in welded path points from other paths.
        var anchors = set.Where(x => x.Type == HandleType.PathPoint).Select(PositionOf).ToList();
        for (int s = 0; s < Doc.Paths.Count; s++)
        {
            if (h.IsWhole && h.Type == HandleType.PathPoint && s == h.Shape) continue;
            var pts = Doc.Paths[s].Points;
            for (int i = 0; i < pts.Count; i++)
            {
                var cand = new Handle(HandleType.PathPoint, s, i);
                if (!set.Contains(cand) && anchors.Any(a => Same(a, pts[i]))) set.Add(cand);
            }
        }
        return set;
    }

    Handle PickHandle(Vector2 mouse, bool includeTangents = true, Func<Handle, bool> filter = null)
    {
        var cam = Rig.Camera;
        Handle best = Handle.None; float bestD = PickRadiusPx;
        var candidates = includeTangents ? TangentHandles().Concat(AllHandles()) : AllHandles();
        foreach (var h in candidates)
        {
            if (filter != null && !filter(h)) continue;
            var g = Space.ToGodot(PositionOf(h));
            if (cam.IsPositionBehind(g)) continue;
            float d = cam.UnprojectPosition(g).DistanceTo(mouse);
            // Prefer the selected shape's markers when they overlap others (e.g. at a branch junction).
            if (h.Shape == Selected.Shape && (h.Type == Selected.Type || (h.IsTangent && SelectedPath == h.Shape))) d -= 2f;
            if (h.IsTangent) d -= 1f;
            if (d < bestD) { bestD = d; best = h; }
        }
        return best;
    }

    /// <summary>Finds the path or region edge under the mouse. For paths, insertAt is the index a new point would get.</summary>
    (HandleType type, int shape, int insertAt, float z) PickEdge(Vector2 mouse)
    {
        var cam = Rig.Camera;
        (HandleType, int, int, float) best = (HandleType.None, -1, -1, 0);
        float bestD = PickRadiusPx;
        void Test(HandleType t, int s, Vec3 a, Vec3 b, int insertAt)
        {
            var ga = Space.ToGodot(a); var gb = Space.ToGodot(b);
            if (cam.IsPositionBehind(ga) || cam.IsPositionBehind(gb)) return;
            var sa = cam.UnprojectPosition(ga); var sb = cam.UnprojectPosition(gb);
            var closest = Geometry2D.GetClosestPointToSegment(mouse, sa, sb);
            float d = closest.DistanceTo(mouse);
            if (d < bestD)
            {
                float f = sa.DistanceTo(sb) > 0.01f ? sa.DistanceTo(closest) / sa.DistanceTo(sb) : 0;
                bestD = d; best = (t, s, insertAt, a.Z + (b.Z - a.Z) * f);
            }
        }
        for (int s = 0; s < Doc.Paths.Count; s++)
        {
            var samples = PathSampler.Flatten(Doc.Paths[s]);
            for (int i = 0; i + 1 < samples.Count; i++)
            {
                var a = samples[i].P; var b = samples[i + 1].P;
                Test(HandleType.PathPoint, s, new Vec3(a.X, a.Y, a.Z), new Vec3(b.X, b.Y, b.Z), samples[i].Segment + 1);
            }
        }
        for (int s = 0; s < Doc.Regions.Count; s++)
        {
            var r = Doc.Regions[s];
            float top = r.Kind == Model.RegionKind.Obstacle ? r.Elevation + r.Height : r.Elevation;
            for (int i = 0; i < r.Points.Count; i++)
            {
                var a = r.Points[i]; var b = r.Points[(i + 1) % r.Points.Count];
                Test(HandleType.RegionPoint, s, new Vec3(a.X, a.Y, top), new Vec3(b.X, b.Y, top), i + 1);
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
            case Key.S when ctrl: SaveCurrent(); break;
            case Key.E when ctrl: _ = ExportAsync(); break;
            case Key.Tab: Rig.ToggleMode(); break;
            case Key.F when !ctrl: FrameAll(); break;
            case Key.C when !ctrl: ToggleCurve(); break;
            case Key.B when !ctrl: StartBranchFromSelection(); break;
            case Key.Key1: SetTool(Tool.Select); break;
            case Key.Key2: SetTool(Tool.Path); break;
            case Key.Key3: SetTool(Tool.Floor); break;
            case Key.Key4: SetTool(Tool.Obstacle); break;
            case Key.Key5: SetTool(Tool.Pit); break;
            case Key.Key6: SetTool(Tool.SpawnT); break;
            case Key.Key7: SetTool(Tool.SpawnCT); break;
            case Key.Enter or Key.KpEnter: CommitDraft(); break;
            case Key.Escape:
                if (Draft.Count > 0) { Draft.Clear(); _branchFrom = Handle.None; Ui.SetStatus("Drawing cancelled"); }
                else { Selected = Handle.None; RefreshSelectionUi(); }
                break;
            case Key.Backspace when Draft.Count > 0: Draft.RemoveAt(Draft.Count - 1); break;
            case Key.Delete or Key.Backspace:
                if (k.ShiftPressed && !Selected.IsNone && !Selected.IsTangent && Selected.Type != HandleType.Spawn)
                    Delete(new Handle(Selected.Type, Selected.Shape, -1));
                else Delete(Selected);
                break;
            case Key.Pageup: Nudge(+StepZ()); break;
            case Key.Pagedown: Nudge(-StepZ()); break;
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    float StepZ() => Doc.GridSize > 0 ? Doc.GridSize : 16f;

    /// <summary>PageUp/PageDown: raise/lower the selection (welded points follow); while drawing, the working height.</summary>
    void Nudge(float dz)
    {
        if (CurrentTool != Tool.Select || Selected.IsNone)
        {
            SetWorkZ(WorkZ + dz);
            Ui.SetStatus($"Working elevation {WorkZ}");
            return;
        }
        PushUndo();
        foreach (var h in MoveSet(Selected, Input.IsKeyPressed(Key.Ctrl)))
        {
            var p = PositionOf(h);
            SetPosition(h, new Vec3(p.X, p.Y, p.Z + dz));
        }
        AfterEdit();
    }

    void SetWorkZ(float z)
    {
        WorkZ = z;
        Ui.Silently(() => Ui.WorkElevation.Value = z);
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

        // In the free cam the right button is "look"; CameraRig reports quick clicks via RightClicked.
        if (mb.ButtonIndex == MouseButton.Right && mb.Pressed && Rig.IsTopDown)
        {
            HandleRightClick(mb.Position);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (mb.ButtonIndex != MouseButton.Left) return;
        if (Input.IsKeyPressed(Key.Space)) return; // space-drag pans

        if (!mb.Pressed)
        {
            if (_dragging) { _dragging = false; if (_dragPushedUndo) Ui.SetStatus("Moved"); }
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

    /// <summary>Right-click: delete the marker under the cursor (draft point, placed point, spawn or curve handle).
    /// On empty space it finishes the current drawing, or clears the selection.</summary>
    void HandleRightClick(Vector2 mouse)
    {
        for (int i = Draft.Count - 1; i >= 0; i--)
        {
            if (Rig.Camera.UnprojectPosition(Space.ToGodot(Draft[i])).DistanceTo(mouse) < PickRadiusPx)
            {
                Draft.RemoveAt(i);
                if (i == 0) _branchFrom = Handle.None;
                Ui.SetStatus($"Removed draft point ({Draft.Count} left)");
                return;
            }
        }

        var h = PickHandle(mouse);
        if (!h.IsNone) { Delete(h); return; }

        if (Draft.Count > 0) CommitDraft();
        else { Selected = Handle.None; RefreshSelectionUi(); }
    }

    void SelectClick(InputEventMouseButton mb)
    {
        bool ctrl = mb.CtrlPressed;
        var hit = PickHandle(mb.Position);
        if (!hit.IsNone)
        {
            Selected = hit;
            RefreshSelectionUi();
            BeginDrag(mb.Position, pushedUndo: false, detach: ctrl);
            return;
        }

        var (type, shape, at, z) = PickEdge(mb.Position);
        if (type == HandleType.None)
        {
            Selected = Handle.None;
            RefreshSelectionUi();
            return;
        }

        if (ctrl)
        {
            // Ctrl+click on an edge inserts a point there and starts dragging it.
            if (PickPlane(mb.Position, z) is not Vector3 h) return;
            PushUndo();
            var v = SnapXY(h);
            if (type == HandleType.PathPoint)
            {
                var path = Doc.Paths[shape];
                path.InsertPoint(at, new Vec3(v.X, v.Y, Snap(z)));
                if (path.IsCurved(at - 1) || (at + 1 < path.Points.Count && path.IsCurved(at + 1)))
                    path.SetTangent(at, path.AutoTangent(at));
            }
            else Doc.Regions[shape].Points.Insert(at, new Vec3(v.X, v.Y, 0));
            Selected = new Handle(type, shape, at);
            AfterEdit();
            BeginDrag(mb.Position, pushedUndo: true, detach: true);
            return;
        }

        // Plain click on a path/region line selects the whole shape (drag moves all of its points).
        Selected = new Handle(type, shape, -1);
        RefreshSelectionUi();
        BeginDrag(mb.Position, pushedUndo: false, detach: false);
        Ui.SetStatus("Whole shape selected: drag to move, PgUp/PgDn to raise/lower, C to curve, Del to delete.");
    }

    void BeginDrag(Vector2 mouse, bool pushedUndo, bool detach)
    {
        _dragging = true;
        _dragPushedUndo = pushedUndo;
        _dragStartMouse = mouse;
        _dragAnchor = PositionOf(Selected);
        _dragAccumZ = 0;
        _dragVertical = Input.IsKeyPressed(Key.Shift) && !Selected.IsTangent;
        _dragItems = Selected.IsTangent
            ? new List<(Handle, Vec3)> { (Selected, _dragAnchor) }
            : MoveSet(Selected, detach).Select(h => (h, PositionOf(h))).ToList();
    }

    void HandleMouseMotion(InputEventMouseMotion mm)
    {
        float hoverZ = !Selected.IsNone && CurrentTool == Tool.Select ? PositionOf(Selected).Z : WorkZ;
        CursorHammer = PickPlane(mm.Position, _dragging ? _dragAnchor.Z : hoverZ);
        if (CursorHammer is Vector3 c) CursorHammer = new Vector3(Snap(c.X), Snap(c.Y), c.Z);

        if (!_dragging || Selected.IsNone)
        {
            Hovered = CurrentTool switch
            {
                Tool.Select => PickHandle(mm.Position),
                Tool.Path => PickHandle(mm.Position, false, h => h.Type == HandleType.PathPoint), // branch targets
                _ => Handle.None,
            };
            return;
        }
        if (!_dragPushedUndo)
        {
            if (mm.Position.DistanceTo(_dragStartMouse) < 3f) return; // ignore click jitter
            PushUndo();
            _dragPushedUndo = true;
        }

        if (Selected.IsTangent)
        {
            // Curve handles move freely (no snapping) for smooth shapes.
            if (PickPlane(mm.Position, _dragAnchor.Z) is Vector3 raw)
                SetPosition(Selected, new Vec3(raw.X, raw.Y, _dragAnchor.Z));
        }
        else
        {
            float dx = 0, dy = 0, dz = 0;
            if (_dragVertical)
            {
                _dragAccumZ += -mm.Relative.Y * Rig.WorldPerPixel(Space.ToGodot(_dragAnchor));
                dz = Snap(_dragAnchor.Z + _dragAccumZ) - _dragAnchor.Z;
            }
            else if (PickPlane(mm.Position, _dragAnchor.Z) is Vector3 h)
            {
                // Snap the anchor to the grid; everything else moves by the same offset.
                var target = SnapXY(h);
                dx = target.X - _dragAnchor.X; dy = target.Y - _dragAnchor.Y;
            }
            foreach (var (item, start) in _dragItems)
                SetPosition(item, new Vec3(start.X + dx, start.Y + dy, start.Z + dz));
        }
        AfterEdit();
        GetViewport().SetInputAsHandled();
    }

    void Delete(Handle h)
    {
        if (h.IsNone || !HandleExists(h)) return;
        PushUndo();
        switch (h.Type)
        {
            case HandleType.PathPoint:
                var path = Doc.Paths[h.Shape];
                if (h.IsWhole || path.Points.Count <= 2) Doc.Paths.RemoveAt(h.Shape);
                else path.RemovePoint(h.Point);
                break;
            case HandleType.RegionPoint:
                var r = Doc.Regions[h.Shape];
                if (h.IsWhole || r.Points.Count <= 3) Doc.Regions.RemoveAt(h.Shape);
                else r.Points.RemoveAt(h.Point);
                break;
            case HandleType.Spawn: Doc.Spawns.RemoveAt(h.Shape); break;
            case HandleType.TangentOut or HandleType.TangentIn:
                Doc.Paths[h.Shape].SetTangent(h.Point, default); // removing a curve handle makes the point sharp
                Selected = new Handle(HandleType.PathPoint, h.Shape, h.Point);
                AfterEdit();
                Ui.SetStatus("Point made sharp");
                return;
        }
        if (h == Selected || (Selected.Shape == h.Shape && Selected.Type == h.Type)) Selected = Handle.None;
        AfterEdit();
        Ui.SetStatus("Deleted");
    }

    // ------------------------------------------------------------------ drawing

    void DraftClick(InputEventMouseButton mb)
    {
        Vec3 p;
        // Path tool: clicking an existing path marker snaps to it exactly, so new paths branch off or join it.
        var hit = CurrentTool == Tool.Path ? PickHandle(mb.Position, false, h => h.Type == HandleType.PathPoint) : Handle.None;
        if (!hit.IsNone)
        {
            p = PositionOf(hit);
            SetWorkZ(p.Z);
            if (Draft.Count == 0) _branchFrom = hit;
        }
        else
        {
            // The working elevation sets each new point's height, so changing it mid-path makes a ramp.
            if (PickPlane(mb.Position, WorkZ) is not Vector3 h) return;
            p = SnapXY(h);
        }

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
        string from = !_branchFrom.IsNone && Draft.Count == 1 ? $" from path {_branchFrom.Shape}" : "";
        Ui.SetStatus($"{CurrentTool}{from}: {Draft.Count} point(s). Enter / right-click empty space to finish, right-click a point removes it, Esc cancels.");
    }

    /// <summary>B: start drawing a new path from the selected path point (extends the path if it's an end point).</summary>
    void StartBranchFromSelection()
    {
        if (Selected.Type != HandleType.PathPoint || Selected.IsWhole) { Ui.SetStatus("Select a path point first, then press B to branch from it."); return; }
        var from = Selected;
        SetTool(Tool.Path);
        var p = PositionOf(from);
        SetWorkZ(p.Z);
        Draft.Add(p);
        _branchFrom = from;
        var path = Doc.Paths[from.Shape];
        bool isEnd = from.Point == 0 || from.Point == path.Points.Count - 1;
        Ui.SetStatus(isEnd ? $"Extending path {from.Shape}: click to add points, Enter to finish." : $"Branching from path {from.Shape}: click to add points, Enter to finish.");
    }

    void CommitDraft()
    {
        if (Draft.Count == 0) return;
        if (CurrentTool == Tool.Path)
        {
            if (Draft.Count < 2) { Ui.SetStatus("A path needs at least 2 points."); return; }
            PushUndo();
            var branch = _branchFrom;
            bool canExtend = !branch.IsNone && HandleExists(branch) && Same(PositionOf(branch), Draft[0]);
            var target = canExtend ? Doc.Paths[branch.Shape] : null;
            if (target != null && branch.Point == target.Points.Count - 1)
            {
                foreach (var d in Draft.Skip(1)) target.AddPoint(d);
                Selected = new Handle(HandleType.PathPoint, branch.Shape, target.Points.Count - 1);
                Ui.SetStatus($"Extended path {branch.Shape}");
            }
            else if (target != null && branch.Point == 0)
            {
                foreach (var d in Draft.Skip(1)) target.InsertPoint(0, d);
                Selected = new Handle(HandleType.PathPoint, branch.Shape, 0);
                Ui.SetStatus($"Extended path {branch.Shape}");
            }
            else
            {
                var path = new PathShape { Width = target?.Width ?? (float)Ui.PathWidth.Value };
                foreach (var d in Draft) path.AddPoint(d);
                Doc.Paths.Add(path);
                Selected = new Handle(HandleType.PathPoint, Doc.Paths.Count - 1, path.Points.Count - 1);
                Ui.SetStatus(target != null ? $"Branch added off path {branch.Shape}" : "Path added");
            }
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
            Ui.SetStatus("Shape added");
        }
        Draft.Clear();
        _branchFrom = Handle.None;
        AfterEdit();
    }

    const int SmoothCornerId = 3; // Corner dropdown: Model.CornerStyle values, then Smooth

    /// <summary>Corner dropdown id of a point: Smooth when it has a Bezier tangent, otherwise its corner style.</summary>
    static int CornerId(PathShape p, int i) => p.IsCurved(i) ? SmoothCornerId : (int)p.CornerAt(i);

    static void ApplyCorner(PathShape p, int i, int id)
    {
        if (id == SmoothCornerId) { if (!p.IsCurved(i)) p.SetTangent(i, p.AutoTangent(i)); return; }
        p.SetTangent(i, default);
        p.SetCorner(i, (CornerStyle)id);
    }

    /// <summary>C: toggle the selected path point between Smooth and its corner style (or the whole path if it's selected).</summary>
    void ToggleCurve()
    {
        int s = SelectedPath;
        if (s < 0) { Ui.SetStatus("Select a path point (or click a path line) first, then press C to curve it."); return; }
        PushUndo();
        var path = Doc.Paths[s];
        if (Selected.IsWhole)
        {
            bool makeCurved = !path.AnyCurved();
            var autos = Enumerable.Range(0, path.Points.Count).Select(path.AutoTangent).ToList();
            for (int i = 0; i < path.Points.Count; i++) path.SetTangent(i, makeCurved ? autos[i] : default);
            Ui.SetStatus(makeCurved ? "Path smooth (drag the handles to shape it)" : "Path corners back to their corner styles");
        }
        else
        {
            int i = Selected.Point;
            bool makeCurved = !path.IsCurved(i);
            path.SetTangent(i, makeCurved ? path.AutoTangent(i) : default);
            Selected = new Handle(HandleType.PathPoint, s, i);
            Ui.SetStatus(makeCurved ? "Point smooth (drag the handles to shape it)" : $"Point corner: {path.CornerAt(i)}");
        }
        AfterEdit();
    }

    void SetTool(Tool t)
    {
        if (Draft.Count > 0 && t != CurrentTool) { Draft.Clear(); _branchFrom = Handle.None; }
        CurrentTool = t;
        Ui.SetTool(t);
        Ui.SetStatus(t switch
        {
            Tool.Select => "Select: drag points (Shift = vertical, Ctrl = unweld), click a line to select the whole shape, Ctrl+click a line inserts a point, right-click deletes, C curves, B branches.",
            Tool.Path => "Path: click points at the working elevation (PgUp/PgDn changes it, so the next point ramps). Click an existing path point to branch from / join it. Enter finishes.",
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
            // Only the rows that apply to the selection are shown.
            Ui.KindRow.Visible = Ui.ZRow.Visible = Ui.HeightRow.Visible = Ui.WidthRow.Visible = false;
            Ui.CornerRow.Visible = Ui.EndpointRow.Visible = Ui.YawRow.Visible = false;
            Ui.PropsSection.Title = "Properties";
            Ui.WidthLabel.Text = "Width";
            Ui.YawLabel.Text = "Yaw";
            switch (Selected.Type)
            {
                case HandleType.PathPoint or HandleType.TangentOut or HandleType.TangentIn:
                {
                    var p = Doc.Paths[Selected.Shape];
                    int pi = Math.Max(0, Selected.Point);
                    bool endpoint = !Selected.IsWhole && Selected.Type == HandleType.PathPoint && IsEndpoint(p, pi);
                    string sub;
                    if (Selected.IsWhole) sub = $"Whole path · {p.Points.Count} points";
                    else
                    {
                        sub = endpoint ? (pi == 0 ? "Start point" : "End point") : "Point";
                        if (Selected.IsTangent) sub = "Curve handle";
                        if (Selected.Type == HandleType.PathPoint && MoveSet(Selected, false).Count > 1) sub += " · welded";
                    }
                    Ui.SetSelectionHeader(Tool.Path,
                        Selected.IsWhole ? $"Path {Selected.Shape}" : $"Path {Selected.Shape} · point {pi} of {p.Points.Count - 1}", sub);
                    Ui.PropsSection.Title = Selected.IsWhole ? "Path" : "Path point";
                    Ui.WidthRow.Visible = true; Ui.PathWidth.Value = p.Width;
                    Ui.ZRow.Visible = true; Ui.ZLabel.Text = Selected.IsWhole ? "Z (first point)" : "Z";
                    Ui.PointZ.Value = p.Points[pi].Z;
                    Ui.CornerRow.Visible = true;
                    if (Selected.IsWhole)
                    {
                        // Whole path: show the shared corner option, or nothing when points differ.
                        var ids = Enumerable.Range(0, p.Points.Count).Select(i => CornerId(p, i)).Distinct().ToList();
                        Ui.SetCorner(ids.Count == 1 ? ids[0] : -1);
                    }
                    else Ui.SetCorner(CornerId(p, pi));
                    if (endpoint)
                    {
                        Ui.EndpointRow.Visible = true;
                        Ui.EndpointDepth.Value = (pi == 0 ? p.StartDepth : p.EndDepth) ?? p.Width * 0.5f;
                    }
                    break;
                }
                case HandleType.RegionPoint:
                {
                    var r = Doc.Regions[Selected.Shape];
                    var tool = r.Kind switch { Model.RegionKind.Floor => Tool.Floor, Model.RegionKind.Obstacle => Tool.Obstacle, _ => Tool.Pit };
                    Ui.SetSelectionHeader(tool, $"{r.Kind} {Selected.Shape}",
                        Selected.IsWhole ? $"Whole shape · {r.Points.Count} points" : $"Point {Selected.Point} of {r.Points.Count - 1}");
                    Ui.PropsSection.Title = r.Kind.ToString();
                    Ui.KindRow.Visible = true; Ui.RegionKind.Selected = (int)r.Kind;
                    Ui.ZRow.Visible = true; Ui.ZLabel.Text = "Elevation"; Ui.PointZ.Value = r.Elevation;
                    if (r.Kind != Model.RegionKind.Floor)
                    {
                        Ui.HeightRow.Visible = true;
                        Ui.HeightLabel.Text = r.Kind == Model.RegionKind.Pit ? "Depth" : "Height";
                        Ui.RegionHeight.Value = r.Height;
                    }
                    break;
                }
                case HandleType.Spawn:
                {
                    var s = Doc.Spawns[Selected.Shape];
                    Ui.SetSelectionHeader(s.Team == Team.T ? Tool.SpawnT : Tool.SpawnCT, $"{s.Team} spawn {Selected.Shape}",
                        $"x {s.Position.X:0}  y {s.Position.Y:0}");
                    Ui.PropsSection.Title = "Spawn";
                    Ui.ZRow.Visible = true; Ui.ZLabel.Text = "Z"; Ui.PointZ.Value = s.Position.Z;
                    Ui.YawRow.Visible = true; Ui.SpawnYaw.Value = s.Yaw;
                    break;
                }
                default:
                    Ui.SetSelectionHeader(null, "Nothing selected", "Click a point to edit it, or a line to pick the whole shape.");
                    Ui.PropsSection.Title = "New shapes";
                    Ui.WidthRow.Visible = true; Ui.WidthLabel.Text = "Path width";
                    Ui.YawRow.Visible = true; Ui.YawLabel.Text = "Spawn yaw";
                    break;
            }
        });
        RefreshProfileUi();
    }

    static bool IsEndpoint(PathShape p, int i) => i == 0 || i == p.Points.Count - 1;

    // ------------------------------------------------------------------ path profile curves

    int ProfileKindIndex => Ui.ProfileKind.Selected < 0 ? 0 : Ui.ProfileKind.Selected;

    ProfileCurve CurrentProfile(PathShape p) => ProfileKindIndex switch
    {
        1 => p.ElevationCurve,
        2 => p.WallCurve,
        _ => p.WidthCurve,
    };

    (float min, float max, float def, string unit) ProfileRange() => ProfileKindIndex switch
    {
        1 => (-256f, 256f, 0f, "u"),  // elevation offset
        2 => (0f, 3f, 1f, "x"),       // wall height multiplier (0 = no wall)
        _ => (0.25f, 3f, 1f, "x"),    // width multiplier
    };

    void RefreshProfileUi()
    {
        int s = SelectedPath;
        Ui.ProfilePanel.Visible = s >= 0;
        if (s < 0) return;
        var curve = CurrentProfile(Doc.Paths[s]);
        var (min, max, def, unit) = ProfileRange();
        Ui.Silently(() => Ui.ProfileEnabled.ButtonPressed = curve.Enabled);
        Ui.ProfileEditor.SetCurve(curve, min, max, def, unit, curve.Enabled);
    }

    void ApplyProfileEdit()
    {
        int s = SelectedPath;
        if (s < 0) return;
        var target = CurrentProfile(Doc.Paths[s]);
        target.Keys = Ui.ProfileEditor.Curve.Clone().Keys;
        target.Enabled = true; // editing a curve switches it on
        Ui.Silently(() => Ui.ProfileEnabled.ButtonPressed = true);
        Ui.ProfileEditor.Active = true;
        _dirtyGeometry = true;
    }

    void SetProfileEnabled(bool on)
    {
        int s = SelectedPath;
        if (s < 0) return;
        PushUndo();
        CurrentProfile(Doc.Paths[s]).Enabled = on;
        _dirtyGeometry = true;
        RefreshProfileUi();
    }

    void ResetProfile()
    {
        int s = SelectedPath;
        if (s < 0) return;
        PushUndo();
        var p = Doc.Paths[s];
        switch (ProfileKindIndex)
        {
            case 1: p.ElevationCurve = ProfileCurve.Flat(0); break;
            case 2: p.WallCurve = ProfileCurve.Flat(1); break;
            default: p.WidthCurve = ProfileCurve.Flat(1); break;
        }
        _dirtyGeometry = true;
        RefreshProfileUi();
        Ui.SetStatus("Profile curve reset");
    }

    void ApplyPropertyEdits()
    {
        if (Selected.IsNone) return;
        PushUndo();
        switch (Selected.Type)
        {
            case HandleType.PathPoint or HandleType.TangentOut or HandleType.TangentIn:
                var p = Doc.Paths[Selected.Shape];
                p.Width = (float)Ui.PathWidth.Value;
                float newZ = (float)Ui.PointZ.Value;
                if (Selected.IsWhole)
                {
                    // Whole path: Z shows the first point; editing it shifts every point, keeping ramps intact.
                    float dz = newZ - p.Points[0].Z;
                    for (int i = 0; i < p.Points.Count; i++) p.Points[i] = new Vec3(p.Points[i].X, p.Points[i].Y, p.Points[i].Z + dz);
                    int want = Ui.CornerSelected;
                    if (want >= 0)
                        for (int i = 0; i < p.Points.Count; i++)
                            if (CornerId(p, i) != want) ApplyCorner(p, i, want);
                }
                else
                {
                    var pt = p.Points[Selected.Point];
                    p.Points[Selected.Point] = new Vec3(pt.X, pt.Y, newZ);
                    if (Selected.Type == HandleType.PathPoint && IsEndpoint(p, Selected.Point))
                    {
                        if (Selected.Point == 0) p.StartDepth = (float)Ui.EndpointDepth.Value;
                        else p.EndDepth = (float)Ui.EndpointDepth.Value;
                    }
                    int want = Ui.CornerSelected;
                    if (want >= 0 && want != CornerId(p, Selected.Point)) ApplyCorner(p, Selected.Point, want);
                }
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
        // Debug aid: --screenshot <png> [--free] [--select-path N] [--curve-demo] saves the viewport after a few frames and quits.
        ++_frames;
        if (_frames == 2)
        {
            var args = OS.GetCmdlineUserArgs();
            if (args.Contains("--curve-demo"))
            {
                for (int p = 0; p < Doc.Paths.Count; p++)
                    for (int i = 1; i < Doc.Paths[p].Points.Count - 1; i++) Doc.Paths[p].SetTangent(i, Doc.Paths[p].AutoTangent(i));
                _dirtyGeometry = true;
            }
            // --cam px py pz tx ty tz (Hammer coords): free cam at p looking at t.
            int ci = Array.IndexOf(args, "--cam");
            if (ci >= 0 && ci + 6 < args.Length)
            {
                var v = args.Skip(ci + 1).Take(6).Select(a => float.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                Rig.LookFrom(Space.ToGodot(v[0], v[1], v[2]), Space.ToGodot(v[3], v[4], v[5]));
            }
            int sp = Array.IndexOf(args, "--select-path");
            if (sp >= 0 && sp + 1 < args.Length && int.TryParse(args[sp + 1], out int pathIdx) && pathIdx < Doc.Paths.Count)
            {
                Selected = Handle.WholePath(pathIdx);
                int pk = Array.IndexOf(args, "--profile");
                if (pk >= 0 && pk + 1 < args.Length && int.TryParse(args[pk + 1], out int kind)) Ui.ProfileKind.Select(kind);
                RefreshSelectionUi();
            }
        }
        if (_frames == 30)
        {
            var args = OS.GetCmdlineUserArgs();
            int si = Array.IndexOf(args, "--screenshot");
            if (si >= 0 && si + 1 < args.Length)
            {
                if (args.Contains("--free") && Rig.IsTopDown) { Rig.ToggleMode(); _frames = 3; }
                else { GetViewport().GetTexture().GetImage().SavePng(args[si + 1]); GetTree().Quit(); }
            }
        }

        if (Ui.WorkElevation != null && !Mathf.IsEqualApprox((float)Ui.WorkElevation.Value, WorkZ))
            WorkZ = (float)Ui.WorkElevation.Value;
        RebuildNow();
        UpdateHud();
        Overlay?.Draw(this);
    }

    /// <summary>Per-frame bits of the UI that track editor state: view switch, tool chip, cursor readout, undo buttons.</summary>
    void UpdateHud()
    {
        Ui.SetViewMode(Rig.IsTopDown);
        string tool = CurrentTool switch { Tool.SpawnT => "T spawn", Tool.SpawnCT => "CT spawn", var t => t.ToString() };
        string info = $"{tool}  ·  Z {WorkZ:0}u  ·  grid {Doc.GridSize:0}u";
        if (Draft.Count > 0) info += $"  ·  {Draft.Count} point{(Draft.Count == 1 ? "" : "s")}";
        Ui.SetToolInfo(info);
        string cursor = CursorHammer is Vector3 c ? $"x {c.X,6:0}  y {c.Y,6:0}  z {c.Z,5:0}" : "";
        if (_dragging && _dragPushedUndo && _dragAccumZ != 0) cursor += $"  Δz {_dragAccumZ:+0;-0}";
        Ui.SetCursor(cursor);
        Ui.SetUndoAvailable(_undo.Count > 0, _redo.Count > 0);
    }
}
