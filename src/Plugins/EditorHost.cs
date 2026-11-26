using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Cs2MapProto.Editor;
using Cs2MapProto.Geometry;
using Cs2MapProto.Model;

namespace Cs2MapProto.Plugins;

/// <summary>The editor side of IEditorHost: a thin adapter over EditorRoot, so plugins only see the API.</summary>
public sealed class EditorHost : IEditorHost
{
    readonly EditorRoot _root;
    readonly SceneCapture _capture;

    public EditorHost(EditorRoot root, SceneCapture capture)
    {
        _root = root;
        _capture = capture;
        root.DocumentChanged += () => DocumentChanged?.Invoke();
        root.Rebuilt += () => Rebuilt?.Invoke();
    }

    public MapDocument Document => _root.Doc;
    public BuiltMap Built => _root.Built;
    public string DocumentPath => _root.DocPath;
    public event Action DocumentChanged;
    public event Action Rebuilt;
    public Node UiRoot => _root.Ui;

    public void AddMenuItem(string label, Action onPressed) => _root.Ui.AddToolsItem(label, onPressed);
    public void SetStatus(string text) => _root.Ui.SetStatus(text);
    public Task<CaptureResult> CaptureAsync(CaptureRequest request) => _capture.CaptureAsync(request);

    public string PluginDataDir(string pluginId)
    {
        var dir = Path.Combine(ProjectSettings.GlobalizePath("user://plugins"), pluginId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public T GetDocumentData<T>(string pluginId) where T : class
    {
        if (Document.Plugins == null || !Document.Plugins.TryGetValue(pluginId, out var el)) return null;
        try { return el.Deserialize<T>(); }
        catch (JsonException e) { GD.PrintErr($"Plugin data for {pluginId} unreadable: {e.Message}"); return null; }
    }

    public void SetDocumentData<T>(string pluginId, T value) where T : class
    {
        var doc = Document;
        if (value == null)
        {
            if (doc.Plugins == null || !doc.Plugins.Remove(pluginId)) return;
            if (doc.Plugins.Count == 0) doc.Plugins = null;
        }
        else
        {
            var el = JsonSerializer.SerializeToElement(value);
            if (doc.Plugins != null && doc.Plugins.TryGetValue(pluginId, out var old) && old.GetRawText() == el.GetRawText()) return;
            (doc.Plugins ??= new Dictionary<string, JsonElement>())[pluginId] = el;
        }
        _root.MarkUnsaved();
    }
}
