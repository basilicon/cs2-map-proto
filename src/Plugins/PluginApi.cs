using System;
using System.Collections.Generic;
using Vector3 = System.Numerics.Vector3;
using System.Threading.Tasks;
using Godot;
using Cs2MapProto.Geometry;
using Cs2MapProto.Model;

namespace Cs2MapProto.Plugins;

/// <summary>An add-on loaded from plugins/&lt;Name&gt;/&lt;Name&gt;.dll at startup. The editor creates one instance of
/// every public non-abstract IEditorPlugin type in that assembly.
///
/// Plugin assemblies can't register Godot script classes (only the main assembly's are), so a plugin builds its
/// UI in code from stock controls (or loads a script-free .tscn and binds nodes by unique name).</summary>
public interface IEditorPlugin
{
    /// <summary>Stable id, also the key of the plugin's data in saved layouts. Lowercase-dashed, e.g. "concept-sketch".</summary>
    string Id { get; }
    string Name { get; }
    void Initialize(IEditorHost host);
    void Shutdown();
}

/// <summary>What a plugin can see and do. Everything runs on the main thread; await the async calls from there.</summary>
public interface IEditorHost
{
    /// <summary>The live document. Treat it as read-only (Clone() it to keep a snapshot).</summary>
    MapDocument Document { get; }
    /// <summary>The meshes and entities built from the document (what the preview shows and the exporter writes).</summary>
    BuiltMap Built { get; }
    /// <summary>Path of the saved layout, or null for an unsaved one.</summary>
    string DocumentPath { get; }

    /// <summary>The document was edited or replaced (fires often while dragging).</summary>
    event Action DocumentChanged;
    /// <summary>Built was rebuilt from the document.</summary>
    event Action Rebuilt;

    /// <summary>Adds an entry to the top bar's Tools menu (shown once any plugin adds one).</summary>
    void AddMenuItem(string label, Action onPressed);
    /// <summary>A node to parent plugin windows and helper nodes under.</summary>
    Node UiRoot { get; }
    void SetStatus(string text);

    /// <summary>Shows these meshes in the editor's view instead of its own preview (Godot space, as Space.ToGodot
    /// maps Hammer units); null restores the preview. The ceilings mesh is hidden in the top-down view like the
    /// preview's roofs. Captures always render the editor's own meshes. Clear or rebuild it on Rebuilt if it
    /// mirrors the map's geometry.</summary>
    void SetPreviewOverride(Mesh main, Mesh ceilings = null);

    /// <summary>Renders the built map from a camera into images, off screen (the editor's own view is untouched).</summary>
    Task<CaptureResult> CaptureAsync(CaptureRequest request);

    /// <summary>Folder for this plugin's machine-local files (settings, caches). Created on demand.</summary>
    string PluginDataDir(string pluginId);
    /// <summary>Plugin data stored inside the layout file under "Plugins": {"&lt;id&gt;": ...}; default when absent.</summary>
    T GetDocumentData<T>(string pluginId) where T : class;
    /// <summary>Stores plugin data in the layout and marks it unsaved. null removes the entry.</summary>
    void SetDocumentData<T>(string pluginId, T value) where T : class;

    /// <summary>What's selected in the editor. Shapes are identified by their Id, which survives reordering and
    /// saving, so it's the key to store per-shape plugin data under.</summary>
    EditorSelection Selection { get; }
    /// <summary>The selection, or the selected shape's properties, changed (fires often while editing).</summary>
    event Action SelectionChanged;
    /// <summary>Adds a control to the inspector, below the selection's properties. It stays where it is; the plugin
    /// shows and hides it, typically on SelectionChanged.</summary>
    void AddInspectorSection(Control section);
}

public enum SelectionKind { None, Path, Region, Spawn }

/// <summary>A selected shape: its kind, Id and index in the document's list. Whole is false when a single point
/// (or curve handle) of it is selected.</summary>
public readonly record struct EditorSelection(SelectionKind Kind, Guid Id, int Index, bool Whole)
{
    public static readonly EditorSelection None = new(SelectionKind.None, Guid.Empty, -1, false);
}

[Flags]
public enum CapturePass
{
    /// <summary>The editor's lit preview look.</summary>
    Beauty = 1,
    /// <summary>Grayscale distance from the camera, white at DepthNear to black at DepthFar (ControlNet depth convention):
    /// inverse depth like MiDaS for perspective cameras, linear for orthographic ones.</summary>
    Depth = 2,
    /// <summary>Flat unlit colour per mesh kind, see CapturePalette.</summary>
    Semantic = 4,
    All = Beauty | Depth | Semantic,
}

/// <summary>A camera for CaptureAsync. Positions are Hammer units (X east, Y north, Z up).</summary>
public sealed class CaptureRequest
{
    public Vector3 Eye;
    public Vector3 Target;
    /// <summary>Vertical field of view for a perspective camera.</summary>
    public float FovDegrees = 75f;
    /// <summary>Above 0 renders orthographically with this vertical extent (in units) instead.</summary>
    public float OrthoSize;
    public int Width = 768, Height = 512;
    public CapturePass Passes = CapturePass.All;
    public float DepthNear = 96f, DepthFar = 4096f;
}

public sealed class CaptureResult
{
    public Image Beauty, Depth, Semantic;
    public int Width, Height;
    internal Projection Projection;
    internal Transform3D View;
    internal bool Ortho;

    /// <summary>Pixel position and view distance of a Hammer-space point, or null when it's behind the camera
    /// or outside the image. Doesn't test occlusion (compare against the Depth image for that).</summary>
    public (Godot.Vector2 Pixel, float Distance)? Project(Vector3 hammer)
    {
        var v = View * Editor.Space.ToGodot(hammer);
        if (!Ortho && v.Z >= -0.01f) return null;
        var clip = Projection * new Godot.Vector4(v.X, v.Y, v.Z, 1f);
        if (MathF.Abs(clip.W) < 1e-6f) return null;
        float x = (clip.X / clip.W + 1f) * 0.5f * Width, y = (1f - clip.Y / clip.W) * 0.5f * Height;
        if (x < 0 || y < 0 || x >= Width || y >= Height) return null;
        return (new Godot.Vector2(x, y), v.Length());
    }
}

/// <summary>Colours of the Semantic pass. They follow the ADE20K segmentation palette (wall, floor, stairs, box,
/// earth, sky), so the image can be fed straight to a segmentation ControlNet.</summary>
public static class CapturePalette
{
    public static readonly Color Sky = Color.Color8(6, 230, 230);

    public static readonly IReadOnlyDictionary<MeshKind, Color> Kinds = new Dictionary<MeshKind, Color>
    {
        [MeshKind.Floor] = Color.Color8(80, 50, 50),
        [MeshKind.Ramp] = Color.Color8(255, 224, 0),
        [MeshKind.Wall] = Color.Color8(120, 120, 120),
        [MeshKind.Obstacle] = Color.Color8(0, 255, 20),
        [MeshKind.PitBottom] = Color.Color8(120, 120, 70),
        [MeshKind.PitLiner] = Color.Color8(120, 120, 120),
        [MeshKind.Ceiling] = Color.Color8(120, 120, 80),
    };

    /// <summary>Kind whose colour matches a Semantic pixel, or null for sky / anything else.</summary>
    public static MeshKind? KindAt(Color pixel)
    {
        foreach (var (kind, c) in Kinds)
            if (MathF.Abs(c.R - pixel.R) < 0.03f && MathF.Abs(c.G - pixel.G) < 0.03f && MathF.Abs(c.B - pixel.B) < 0.03f)
                return kind;
        return null;
    }
}
