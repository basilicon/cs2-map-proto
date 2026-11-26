using System;
using System.Collections.Generic;
using Godot;

namespace Cs2MapProto.Editor;

public enum Tool { Select, Path, Floor, Obstacle, Pit, SpawnT, SpawnCT }

/// <summary>Thin binding layer over the scene's premade controls. Every control is an [Export] so the
/// layout can be rearranged freely in the Godot editor (the scene binds them by unique name, %Name);
/// EditorRoot only talks to the events and setters here. Look and feel lives in ui/editor_theme.tres.</summary>
public partial class EditorUi : Control
{
    [ExportGroup("Top bar")]
    [Export] public MenuButton FileMenu { get; set; }
    [Export] public Button ExportButton { get; set; }
    [Export] public Button UndoButton { get; set; }
    [Export] public Button RedoButton { get; set; }
    [Export] public Button TopViewButton { get; set; }
    [Export] public Button FreeViewButton { get; set; }
    [Export] public Label DocName { get; set; }
    [Export] public Control DirtyChip { get; set; }
    [Export] public FileDialog OpenDialog { get; set; }
    [Export] public FileDialog SaveDialog { get; set; }
    [Export] public FileDialog ExportDialog { get; set; }

    [ExportGroup("Tool rail")]
    [Export] public Button SelectTool { get; set; }
    [Export] public Button PathTool { get; set; }
    [Export] public Button FloorTool { get; set; }
    [Export] public Button ObstacleTool { get; set; }
    [Export] public Button PitTool { get; set; }
    [Export] public Button SpawnTTool { get; set; }
    [Export] public Button SpawnCTTool { get; set; }

    [ExportGroup("Viewport overlay")]
    [Export] public Label ToolInfo { get; set; }
    [Export] public Button WarningButton { get; set; }
    [Export] public Control WarningsPopover { get; set; }
    [Export] public Button WarningsClose { get; set; }
    [Export] public Label Warnings { get; set; }

    [ExportGroup("Inspector: selection")]
    [Export] public TextureRect SelectionIcon { get; set; }
    [Export] public Label SelectionLabel { get; set; }
    [Export] public Label SelectionSub { get; set; }
    [Export] public FoldableContainer PropsSection { get; set; }
    [Export] public Control KindRow { get; set; }
    [Export] public OptionButton RegionKind { get; set; }
    [Export] public Control ZRow { get; set; }
    [Export] public Label ZLabel { get; set; }
    [Export] public SpinBox PointZ { get; set; }
    [Export] public Control HeightRow { get; set; }
    [Export] public Label HeightLabel { get; set; }
    [Export] public SpinBox RegionHeight { get; set; }
    [Export] public Control WidthRow { get; set; }
    [Export] public Label WidthLabel { get; set; }
    [Export] public SpinBox PathWidth { get; set; }
    [Export] public Control CornerRow { get; set; }
    [Export] public Button CornerSharp { get; set; }
    [Export] public Button CornerBevel { get; set; }
    [Export] public Button CornerRound { get; set; }
    [Export] public Button CornerSmooth { get; set; }
    [Export] public Control EndpointRow { get; set; }
    [Export] public SpinBox EndpointDepth { get; set; }
    [Export] public Control YawRow { get; set; }
    [Export] public Label YawLabel { get; set; }
    [Export] public SpinBox SpawnYaw { get; set; }

    [ExportGroup("Inspector: drawing")]
    [Export] public SpinBox WorkElevation { get; set; }
    [Export] public SpinBox GridSize { get; set; }

    [ExportGroup("Path profile")]
    [Export] public Control ProfilePanel { get; set; }
    [Export] public OptionButton ProfileKind { get; set; }
    [Export] public CheckBox ProfileEnabled { get; set; }
    [Export] public Button ProfileReset { get; set; }
    [Export] public CurveEditor ProfileEditor { get; set; }

    [ExportGroup("Inspector: walls")]
    [Export] public FoldableContainer WallsSection { get; set; }
    [Export] public CheckBox PerimeterWalls { get; set; }
    [Export] public SpinBox WallHeight { get; set; }
    /// <summary>Per-path part of the section, shown while a path is selected.</summary>
    [Export] public Control WallsRow { get; set; }
    [Export] public CheckBox WallLeftOn { get; set; }
    [Export] public SpinBox WallLeftHeight { get; set; }
    [Export] public CheckBox WallRightOn { get; set; }
    [Export] public SpinBox WallRightHeight { get; set; }
    [Export] public Button WallProfile { get; set; }

    [ExportGroup("Inspector: build")]
    [Export] public SpinBox FloorThickness { get; set; }

    [ExportGroup("Inspector: export")]
    [Export] public LineEdit VmapPath { get; set; }
    [Export] public Button BrowseVmap { get; set; }
    [Export] public CheckBox ConvertBinary { get; set; }
    [Export] public CheckBox CompileAfter { get; set; }

    [ExportGroup("Status bar")]
    [Export] public Label Status { get; set; }
    [Export] public Label CursorLabel { get; set; }
    [Export] public Label LastExport { get; set; }

    public event Action<Tool> ToolChosen;
    public event Action NewRequested, SaveRequested, ExportRequested, UndoRequested, RedoRequested;
    public event Action<bool> ViewModeChosen;  // true = top-down
    public event Action<string> OpenChosen, SaveChosen;
    public event Action PropertiesEdited;   // any selection property control changed
    public event Action SettingsEdited;     // build/export/grid controls changed
    public event Action WallProfileRequested;
    public event Action ProfileKindChanged, ProfileResetRequested, ProfileEditStarted, ProfileChanged;
    public event Action<bool> ProfileEnabledToggled;

    enum FileItem { New, Open, Save, SaveAs }

    bool _silent;
    bool _exportAfterPick;
    readonly ButtonGroup _toolGroup = new(), _viewGroup = new(), _cornerGroup = new();
    Button[] _corners;

    public override void _Ready()
    {
        void BindTool(Button b, Tool t)
        {
            if (b == null) return;
            b.ToggleMode = true;
            b.ButtonGroup = _toolGroup;
            b.Pressed += () => ToolChosen?.Invoke(t);
        }
        BindTool(SelectTool, Tool.Select);
        BindTool(PathTool, Tool.Path);
        BindTool(FloorTool, Tool.Floor);
        BindTool(ObstacleTool, Tool.Obstacle);
        BindTool(PitTool, Tool.Pit);
        BindTool(SpawnTTool, Tool.SpawnT);
        BindTool(SpawnCTTool, Tool.SpawnCT);

        // File menu
        var menu = FileMenu.GetPopup();
        if (menu.ItemCount == 0)
        {
            menu.AddItem("New map", (int)FileItem.New);
            menu.AddItem("Open layout...", (int)FileItem.Open);
            menu.AddSeparator();
            menu.AddItem("Save  (Ctrl+S)", (int)FileItem.Save);
            menu.AddItem("Save as...", (int)FileItem.SaveAs);
        }
        menu.IdPressed += id =>
        {
            switch ((FileItem)id)
            {
                case FileItem.New: NewRequested?.Invoke(); break;
                case FileItem.Open: OpenDialog.PopupCentered(new Vector2I(900, 600)); break;
                case FileItem.Save: SaveRequested?.Invoke(); break;
                case FileItem.SaveAs: SaveDialog.PopupCentered(new Vector2I(900, 600)); break;
            }
        };

        ExportButton.Pressed += () => ExportRequested?.Invoke();
        UndoButton.Pressed += () => UndoRequested?.Invoke();
        RedoButton.Pressed += () => RedoRequested?.Invoke();
        foreach (var (b, top) in new[] { (TopViewButton, true), (FreeViewButton, false) })
        {
            b.ToggleMode = true;
            b.ButtonGroup = _viewGroup;
            b.Pressed += () => ViewModeChosen?.Invoke(top);
        }
        PressOnly(TopViewButton);

        BrowseVmap.Pressed += () => PromptExportPath(exportAfter: false);
        OpenDialog.FileSelected += p => OpenChosen?.Invoke(p);
        SaveDialog.FileSelected += p => SaveChosen?.Invoke(p);
        ExportDialog.FileSelected += p =>
        {
            if (!p.EndsWith(".vmap", StringComparison.OrdinalIgnoreCase)) p += ".vmap";
            VmapPath.Text = p;
            SettingsEdited?.Invoke();
            // Picking a file from the Export button exports straight away (no second click needed).
            if (_exportAfterPick) { _exportAfterPick = false; ExportRequested?.Invoke(); }
        };
        ExportDialog.Canceled += () => _exportAfterPick = false;

        // Warnings popover
        WarningButton.ToggleMode = true;
        WarningButton.Toggled += on => WarningsPopover.Visible = on;
        WarningsClose.Pressed += () => WarningButton.ButtonPressed = false;
        WarningsPopover.Visible = false;

        if (RegionKind != null && RegionKind.ItemCount == 0)
        {
            RegionKind.AddItem("Floor", 0);
            RegionKind.AddItem("Obstacle", 1);
            RegionKind.AddItem("Pit", 2);
        }

        foreach (var s in new[] { PathWidth, EndpointDepth, PointZ, RegionHeight, SpawnYaw, WallLeftHeight, WallRightHeight })
            s.ValueChanged += _ => { if (!_silent) PropertiesEdited?.Invoke(); };
        WallLeftOn.Toggled += _ => { if (!_silent) PropertiesEdited?.Invoke(); };
        WallRightOn.Toggled += _ => { if (!_silent) PropertiesEdited?.Invoke(); };
        WallProfile.Pressed += () => WallProfileRequested?.Invoke();
        RegionKind.ItemSelected += _ => { if (!_silent) PropertiesEdited?.Invoke(); };

        // Corner segmented control. Index matches Model.CornerStyle, plus Smooth (a Bezier point) last.
        _corners = new[] { CornerSharp, CornerBevel, CornerRound, CornerSmooth };
        foreach (var b in _corners)
        {
            b.ToggleMode = true;
            b.ButtonGroup = _cornerGroup;
            b.Pressed += () => { if (!_silent) PropertiesEdited?.Invoke(); };
        }

        if (ProfileKind != null && ProfileKind.ItemCount == 0)
        {
            ProfileKind.AddItem("Width", 0);
            ProfileKind.AddItem("Elevation", 1);
            ProfileKind.AddItem("Wall height", 2);
        }
        ProfileKind.ItemSelected += _ => { if (!_silent) ProfileKindChanged?.Invoke(); };
        ProfileEnabled.Toggled += on => { if (!_silent) ProfileEnabledToggled?.Invoke(on); };
        ProfileReset.Pressed += () => ProfileResetRequested?.Invoke();
        ProfileEditor.EditStarted += () => ProfileEditStarted?.Invoke();
        ProfileEditor.Changed += () => ProfileChanged?.Invoke();

        foreach (var s in new[] { WallHeight, FloorThickness, GridSize })
            s.ValueChanged += _ => { if (!_silent) SettingsEdited?.Invoke(); };
        PerimeterWalls.Toggled += _ => { if (!_silent) SettingsEdited?.Invoke(); };
        ConvertBinary.Toggled += _ => { if (!_silent) SettingsEdited?.Invoke(); };
        CompileAfter.Toggled += _ => { if (!_silent) SettingsEdited?.Invoke(); };
        VmapPath.TextSubmitted += _ => SettingsEdited?.Invoke();
        VmapPath.FocusExited += () => SettingsEdited?.Invoke();

        LastExport.Text = "";
    }

    /// <summary>Opens the .vmap save dialog, starting in the current target's folder or, if none is set, in
    /// CS2's content\csgo_addons folder. With exportAfter, choosing a file also runs the export.</summary>
    public void PromptExportPath(bool exportAfter)
    {
        _exportAfterPick = exportAfter;
        var current = VmapPath.Text.Trim();
        string dir = !string.IsNullOrEmpty(current) ? System.IO.Path.GetDirectoryName(current) : Export.Cs2Tools.FindAddonsContentDir();
        if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir)) ExportDialog.CurrentDir = dir;
        ExportDialog.CurrentFile = !string.IsNullOrEmpty(current) ? System.IO.Path.GetFileName(current) : "proto.vmap";
        ExportDialog.PopupCentered(new Vector2I(900, 600));
    }

    /// <summary>Run UI updates without echoing them back as user edits.</summary>
    public void Silently(Action a)
    {
        _silent = true;
        try { a(); } finally { _silent = false; }
    }

    Button ToolButton(Tool t) => t switch
    {
        Tool.Select => SelectTool, Tool.Path => PathTool, Tool.Floor => FloorTool, Tool.Obstacle => ObstacleTool,
        Tool.Pit => PitTool, Tool.SpawnT => SpawnTTool, _ => SpawnCTTool,
    };

    /// <summary>SetPressedNoSignal doesn't release the rest of a ButtonGroup, so release them here.</summary>
    static void PressOnly(Button b)
    {
        if (b == null || b.ButtonPressed) return;
        if (b.ButtonGroup != null)
            foreach (var other in b.ButtonGroup.GetButtons()) if (other != b) other.SetPressedNoSignal(false);
        b.SetPressedNoSignal(true);
    }

    public void SetTool(Tool t) => PressOnly(ToolButton(t));

    public void SetViewMode(bool topDown) => PressOnly(topDown ? TopViewButton : FreeViewButton);

    public void SetStatus(string text) { if (Status != null) Status.Text = text; }

    static void SetText(Label l, string text) { if (l != null && l.Text != text) l.Text = text; }

    public void SetToolInfo(string text) => SetText(ToolInfo, text);
    public void SetCursor(string text) => SetText(CursorLabel, text);

    public void SetDocInfo(string name, bool dirty)
    {
        SetText(DocName, name);
        if (DirtyChip != null) DirtyChip.Visible = dirty;
    }

    public void SetUndoAvailable(bool undo, bool redo)
    {
        UndoButton.Disabled = !undo;
        RedoButton.Disabled = !redo;
    }

    public void SetLastExport(bool ok, string text)
    {
        LastExport.Text = text;
        LastExport.ThemeTypeVariation = ok ? "SuccessLabel" : "ErrorLabel";
    }

    /// <summary>Warning chip over the viewport: hidden when the build is clean.</summary>
    public void SetWarnings(IReadOnlyCollection<string> warnings, string error = null)
    {
        int n = error != null ? 1 : warnings.Count;
        WarningButton.Visible = n > 0;
        if (n == 0) WarningButton.ButtonPressed = false;
        WarningButton.Text = error != null ? "Build error" : n == 1 ? "1 warning" : $"{n} warnings";
        Warnings.Text = error != null ? error : string.Join("\n", System.Linq.Enumerable.Select(warnings, w => "•  " + w));
    }

    /// <summary>Header card of the inspector. kind picks the icon and colour of that tool's rail button.</summary>
    public void SetSelectionHeader(Tool? kind, string title, string subtitle)
    {
        SetText(SelectionLabel, title);
        SetText(SelectionSub, subtitle);
        SelectionSub.Visible = !string.IsNullOrEmpty(subtitle);
        var b = kind is Tool t ? ToolButton(t) : null;
        SelectionIcon.Visible = b != null;
        if (b != null)
        {
            SelectionIcon.Texture = b.Icon;
            SelectionIcon.Modulate = b.GetThemeColor("icon_normal_color");
        }
    }

    /// <summary>Per-path wall controls: a side's height only edits while that side has a wall, and nothing
    /// edits while walls are off for the whole map.</summary>
    public void SetPathWallsEnabled(bool walls)
    {
        WallLeftOn.Disabled = WallRightOn.Disabled = WallProfile.Disabled = !walls;
        WallLeftHeight.Editable = walls && WallLeftOn.ButtonPressed;
        WallRightHeight.Editable = walls && WallRightOn.ButtonPressed;
        WallHeight.Editable = walls;
    }

    /// <summary>Selected corner option (0 Sharp, 1 Bevel, 2 Round, 3 Smooth), or -1 when none / mixed.</summary>
    public int CornerSelected => Array.FindIndex(_corners, b => b.ButtonPressed);

    public void SetCorner(int id)
    {
        for (int i = 0; i < _corners.Length; i++) _corners[i].SetPressedNoSignal(i == id);
    }
}
