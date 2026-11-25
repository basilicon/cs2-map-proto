using System;
using Godot;

namespace Cs2MapProto.Editor;

public enum Tool { Select, Path, Floor, Obstacle, Pit, SpawnT, SpawnCT }

/// <summary>Thin binding layer over the scene's premade controls. Every control is an [Export] so the
/// layout can be rearranged freely in the Godot editor; EditorRoot only talks to the events and setters here.</summary>
public partial class EditorUi : Control
{
    [ExportGroup("File")]
    [Export] public Button NewButton { get; set; }
    [Export] public Button OpenButton { get; set; }
    [Export] public Button SaveButton { get; set; }
    [Export] public Button ExportButton { get; set; }
    [Export] public FileDialog OpenDialog { get; set; }
    [Export] public FileDialog SaveDialog { get; set; }
    [Export] public FileDialog ExportDialog { get; set; }

    [ExportGroup("Tools")]
    [Export] public Button SelectTool { get; set; }
    [Export] public Button PathTool { get; set; }
    [Export] public Button FloorTool { get; set; }
    [Export] public Button ObstacleTool { get; set; }
    [Export] public Button PitTool { get; set; }
    [Export] public Button SpawnTTool { get; set; }
    [Export] public Button SpawnCTTool { get; set; }
    [Export] public Button ViewToggle { get; set; }

    [ExportGroup("Editing")]
    [Export] public SpinBox WorkElevation { get; set; }
    [Export] public SpinBox GridSize { get; set; }
    [Export] public Label SelectionLabel { get; set; }
    [Export] public SpinBox PathWidth { get; set; }
    [Export] public CheckBox SmoothPoint { get; set; }
    [Export] public SpinBox EndpointDepth { get; set; }
    [Export] public OptionButton RegionKind { get; set; }
    [Export] public SpinBox PointZ { get; set; }
    [Export] public SpinBox RegionHeight { get; set; }
    [Export] public SpinBox SpawnYaw { get; set; }

    [ExportGroup("Path profile")]
    [Export] public Control ProfilePanel { get; set; }
    [Export] public OptionButton ProfileKind { get; set; }
    [Export] public CheckBox ProfileEnabled { get; set; }
    [Export] public Button ProfileReset { get; set; }
    [Export] public CurveEditor ProfileEditor { get; set; }

    [ExportGroup("Build")]
    [Export] public CheckBox PerimeterWalls { get; set; }
    [Export] public SpinBox WallHeight { get; set; }
    [Export] public SpinBox FloorThickness { get; set; }

    [ExportGroup("Export")]
    [Export] public LineEdit VmapPath { get; set; }
    [Export] public Button BrowseVmap { get; set; }
    [Export] public CheckBox ConvertBinary { get; set; }
    [Export] public CheckBox CompileAfter { get; set; }

    [ExportGroup("Feedback")]
    [Export] public Label Warnings { get; set; }
    [Export] public Label Status { get; set; }

    public event Action<Tool> ToolChosen;
    public event Action NewRequested, ViewToggled, ExportRequested;
    public event Action<string> OpenChosen, SaveChosen;
    public event Action PropertiesEdited;   // any selection property control changed
    public event Action SettingsEdited;     // build/export/grid controls changed
    public event Action ProfileKindChanged, ProfileResetRequested, ProfileEditStarted, ProfileChanged;
    public event Action<bool> ProfileEnabledToggled;

    bool _silent;
    readonly ButtonGroup _toolGroup = new();

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

        if (RegionKind != null && RegionKind.ItemCount == 0)
        {
            RegionKind.AddItem("Floor", 0);
            RegionKind.AddItem("Obstacle", 1);
            RegionKind.AddItem("Pit", 2);
        }

        NewButton.Pressed += () => NewRequested?.Invoke();
        OpenButton.Pressed += () => OpenDialog.PopupCentered(new Vector2I(900, 600));
        SaveButton.Pressed += () => SaveDialog.PopupCentered(new Vector2I(900, 600));
        ExportButton.Pressed += () => ExportRequested?.Invoke();
        BrowseVmap.Pressed += () => ExportDialog.PopupCentered(new Vector2I(900, 600));
        ViewToggle.Pressed += () => ViewToggled?.Invoke();
        OpenDialog.FileSelected += p => OpenChosen?.Invoke(p);
        SaveDialog.FileSelected += p => SaveChosen?.Invoke(p);
        ExportDialog.FileSelected += p => { VmapPath.Text = p; SettingsEdited?.Invoke(); };

        foreach (var s in new[] { PathWidth, EndpointDepth, PointZ, RegionHeight, SpawnYaw })
            s.ValueChanged += _ => { if (!_silent) PropertiesEdited?.Invoke(); };
        RegionKind.ItemSelected += _ => { if (!_silent) PropertiesEdited?.Invoke(); };
        SmoothPoint.Toggled += _ => { if (!_silent) PropertiesEdited?.Invoke(); };

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
    }

    /// <summary>Run UI updates without echoing them back as user edits.</summary>
    public void Silently(Action a)
    {
        _silent = true;
        try { a(); } finally { _silent = false; }
    }

    public void SetTool(Tool t)
    {
        var b = t switch
        {
            Tool.Select => SelectTool, Tool.Path => PathTool, Tool.Floor => FloorTool, Tool.Obstacle => ObstacleTool,
            Tool.Pit => PitTool, Tool.SpawnT => SpawnTTool, _ => SpawnCTTool,
        };
        b?.SetPressedNoSignal(true);
    }

    public void SetStatus(string text) { if (Status != null) Status.Text = text; }
}
