# Writing plugins

CS2 Map Proto can load add-ons: separate .NET assemblies that add commands to the editor, read the layout, and
render it off screen. The editor itself works without any of them. This page covers how plugins are loaded, the
API they get, and how to write, build and ship one.

- [How plugins are loaded](#how-plugins-are-loaded)
- [Your first plugin](#your-first-plugin)
- [API reference](#api-reference)
- [Off-screen capture](#off-screen-capture)
- [Storing data](#storing-data)
- [UI](#ui)
- [Threading and async work](#threading-and-async-work)
- [Shipping a plugin](#shipping-a-plugin)
- [Rules of thumb](#rules-of-thumb)

## How plugins are loaded

At startup the editor scans these folders for `plugins/<Name>/<Name>.dll`:

1. `res://plugins/` (the project folder), when running from the Godot editor;
2. `plugins/` next to the executable, for exported builds.

Each DLL is loaded into the **same assembly load context as the editor**, so the plugin and the editor share
`MapDocument`, `BuiltMap`, Godot's types and so on. Any other DLL in the plugin's folder (for example a NuGet
package it uses) is found when the plugin needs it.

For every public, non-abstract class implementing `Cs2MapProto.Plugins.IEditorPlugin`, the editor creates one
instance and calls `Initialize(host)`. When the editor quits it calls `Shutdown()`. A plugin that throws while
loading is skipped with an error in the output; the rest of the editor carries on. Two plugins with the same `Id`
are an error, and the second is skipped.

The source code: [`src/Plugins/PluginLoader.cs`](../src/Plugins/PluginLoader.cs) (loading),
[`src/Plugins/PluginApi.cs`](../src/Plugins/PluginApi.cs) (the API) and
[`src/Plugins/EditorHost.cs`](../src/Plugins/EditorHost.cs) (the editor's side of it).

## Your first plugin

A plugin is a plain class library in `addons/<Name>/`. By convention each one is its own git repository, added to
this one as a submodule (see [Shipping a plugin](#shipping-a-plugin)). The editor's own build builds everything in
`addons/*/*.csproj` after itself, so pressing Play in Godot or running `dotnet build Cs2MapProto.sln` builds your
plugin too.

**`addons/Snapshot/Snapshot.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <!-- Build straight into the folder the editor loads plugins from. -->
    <OutDir>$(MSBuildThisFileDirectory)..\..\plugins\Snapshot\</OutDir>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <GenerateDependencyFile>false</GenerateDependencyFile>
  </PropertyGroup>
  <ItemGroup>
    <!-- The editor (and GodotSharp through it) is already loaded at runtime: reference it, don't copy it. -->
    <ProjectReference Include="..\..\Cs2MapProto.csproj" Private="false" />
  </ItemGroup>
</Project>
```

The three things that matter: the assembly name matches the folder (`Snapshot` → `plugins/Snapshot/Snapshot.dll`),
it builds into `plugins/<Name>/`, and the editor reference has `Private="false"`. A second copy of
`Cs2MapProto.dll` or `GodotSharp.dll` in your output folder would give you types that don't match the editor's.

**`addons/Snapshot/SnapshotPlugin.cs`**

```csharp
using System;
using System.IO;
using System.Linq;
using Cs2MapProto.Plugins;
using Godot;
using Vector3 = System.Numerics.Vector3;

namespace Snapshot;

/// <summary>Tools > Snapshot: renders the whole map from above and opens the image.</summary>
public sealed class SnapshotPlugin : IEditorPlugin
{
    public string Id => "snapshot";
    public string Name => "Snapshot";

    IEditorHost _host;

    public void Initialize(IEditorHost host)
    {
        _host = host;
        host.AddMenuItem("Top-down snapshot", () => _ = TakeAsync());
    }

    public void Shutdown() { }

    async System.Threading.Tasks.Task TakeAsync()
    {
        var points = _host.Document.Paths.SelectMany(p => p.Points).ToList();
        if (points.Count == 0) { _host.SetStatus("Snapshot: nothing to render"); return; }
        float minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        float minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        var center = new Vector3((minX + maxX) / 2, (minY + maxY) / 2, 0);

        var shot = await _host.CaptureAsync(new CaptureRequest
        {
            Eye = center with { Z = 4096 }, Target = center,             // straight down, north up
            OrthoSize = Math.Max(maxY - minY, (maxX - minX) / 1.5f) + 512,
            Width = 1536, Height = 1024,
            Passes = CapturePass.Beauty,
        });

        var file = Path.Combine(_host.PluginDataDir(Id), $"snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        shot.Beauty.SavePng(file);
        _host.SetStatus($"Snapshot saved to {file}");
        OS.ShellOpen(file);
    }
}
```

Build, start the editor, and a **Tools** menu appears in the top bar with "Top-down snapshot" in it. (The Tools
menu stays hidden until a plugin adds an item.) The output panel shows `Plugin loaded: Snapshot (snapshot) from ...`.

## API reference

### `IEditorPlugin`

| Member | |
|---|---|
| `string Id` | Stable, lowercase-dashed id, e.g. `concept-sketch`. Used as the key for the plugin's data in layout files and its data folder name, so don't change it once people have saved layouts. |
| `string Name` | Display name, used in log messages. |
| `void Initialize(IEditorHost host)` | Called once at startup, before the first layout is opened. Keep the host; add menu items here. |
| `void Shutdown()` | Called when the editor quits. Stop background work, kill child processes, release files. |

### `IEditorHost`

| Member | |
|---|---|
| `MapDocument Document` | The live layout. Treat it as **read-only**: edits made here bypass undo and the rebuild. Use `Document.Clone()` to keep a snapshot. The object is replaced on New / Open / Undo / Redo, so read it again rather than holding on to it. |
| `BuiltMap Built` | What the layout builds into: `Meshes` (each with a `MeshKind` and a closed `PolyMesh`), `Entities` (spawns etc.) and `Warnings`. This is exactly what the preview shows and the exporter writes. Can be null before the first build. |
| `string DocumentPath` | The saved layout's path, or null while it's untitled. |
| `event Action DocumentChanged` | The layout was edited or replaced. Fires often (every step of a drag), so debounce expensive work. |
| `event Action Rebuilt` | `Built` was rebuilt. |
| `void AddMenuItem(string label, Action onPressed)` | Adds an item to the top bar's Tools menu. |
| `Node UiRoot` | The editor's UI root control. Parent your windows and helper nodes to it. |
| `void SetStatus(string text)` | Shows a message in the status bar. |
| `void SetPreviewOverride(Mesh main, Mesh ceilings = null)` | Shows your meshes (Godot space) in the editor view instead of its own preview, e.g. a textured map; `null` restores the preview. `ceilings` is hidden in the top-down view like the preview's roofs. Captures still render the editor's meshes. If your meshes mirror the map, clear or rebuild them on `Rebuilt`. |
| `Task<CaptureResult> CaptureAsync(CaptureRequest request)` | Renders the built map off screen. See [Off-screen capture](#off-screen-capture). |
| `string PluginDataDir(string pluginId)` | A folder for machine-local files (settings, caches, logs, venvs), created on demand: `user://plugins/<id>/`, which on Windows is `%APPDATA%\Godot\app_userdata\CS2 Map Proto\plugins\<id>\`. |
| `T GetDocumentData<T>(string pluginId)` | Reads this plugin's data from the layout, or null if there is none. |
| `void SetDocumentData<T>(string pluginId, T value)` | Writes this plugin's data into the layout and marks it unsaved (only if it changed). `null` removes it. |

Coordinates in the document, `BuiltMap` and capture requests are **Hammer units with Hammer axes** (X east, Y north,
Z up), as `System.Numerics.Vector3` or the model's `Vec3`. Godot's own space (Y up) only matters if you add nodes
to the 3D scene yourself; `Cs2MapProto.Editor.Space` converts between the two.

## Off-screen capture

`CaptureAsync` renders the built map from any camera into images, through a separate `SubViewport` that shares the
editor's 3D world. The editor's own view, its camera and its selection handles are never affected, and the
handles and grid overlay never appear in captures.

```csharp
var shot = await host.CaptureAsync(new CaptureRequest
{
    Eye = new Vector3(0, -512, 64),       // Hammer units; 64 is player eye height above a floor at Z 0
    Target = new Vector3(0, 0, 40),
    FovDegrees = 80,                       // vertical field of view
    Width = 768, Height = 512,
    Passes = CapturePass.Depth | CapturePass.Semantic,
});
```

| `CaptureRequest` field | Default | |
|---|---|---|
| `Eye`, `Target` | | Camera position and the point it looks at. Looking straight down puts north at the top of the image. |
| `FovDegrees` | 75 | Vertical field of view (perspective). |
| `OrthoSize` | 0 | Above 0: orthographic, showing this many units vertically. |
| `Width`, `Height` | 768 × 512 | Image size, 16 to 4096. |
| `Passes` | `All` | Which images to render. Each pass takes two frames. |
| `DepthNear`, `DepthFar` | 96, 4096 | Range of the depth pass. |

The passes, as `Image`s in `RGB8` format on the result (null for passes you didn't ask for):

| Pass | Looks like | Use it for |
|---|---|---|
| `Beauty` | The editor's lit preview, with its grid texture. | Thumbnails, references for people. |
| `Depth` | Grayscale: white at `DepthNear`, black at `DepthFar` and in empty space. Inverse depth (1/d, like MiDaS output) for perspective cameras, linear for orthographic ones. | Depth ControlNets; occlusion tests. |
| `Semantic` | Flat, unlit colour per mesh kind with no anti-aliasing, so each pixel is exactly one class. | Segmentation ControlNets; measuring what a view shows. |

The semantic colours come from the ADE20K segmentation palette, so the image can go straight into models trained on
it. They're in `CapturePalette`:

| Class | Colour | ADE20K class |
|---|---|---|
| `Floor` | 80, 50, 50 | floor |
| `Ramp` | 255, 224, 0 | stairs |
| `Wall`, `PitLiner` | 120, 120, 120 | wall |
| `Obstacle` | 0, 255, 20 | box |
| `PitBottom` | 120, 120, 70 | earth |
| `Ceiling` | 120, 120, 80 | ceiling |
| sky / nothing | 6, 230, 230 | sky |

`CapturePalette.KindAt(pixel)` maps a semantic pixel back to its `MeshKind`, or null for sky (walls and pit liners
share a colour and both come back as `Wall`).

`CaptureResult.Project(point)` gives the pixel position and distance of a Hammer-space point in that capture, or
null when it's behind the camera or off the image. It doesn't test occlusion; compare the distance with the depth
pass for that.

Captures run one at a time: concurrent calls queue up. A capture needs the renderer, so it doesn't work with
`--headless` (see [tooling](tooling.md)).

## Storing data

There are two places, for two kinds of data:

- **Per layout** (`GetDocumentData` / `SetDocumentData`): settings that belong to the map, such as a theme or tags
  for areas. They're saved in the layout's JSON under `"Plugins": { "<id>": { ... } }`, serialized with
  System.Text.Json defaults (public properties, PascalCase). A layout opened without your plugin keeps the data
  untouched, and undo / redo includes it. Keep it small: it's rewritten with every save and every undo step.
- **Per machine** (`PluginDataDir`): paths, executables, ports, caches, logs, generated files.

```csharp
public sealed class MySettings { public string Theme { get; set; } = "industrial port"; }

var s = host.GetDocumentData<MySettings>(Id) ?? new MySettings();
s.Theme = "snowy village";
host.SetDocumentData(Id, s);   // the layout now shows as unsaved
```

## UI

Godot only registers script classes (C# classes deriving from `Node`, `Control`, ...) from the editor's own
assembly. So a plugin **can't attach its own C# scripts to nodes**, and can't subclass `Control` for use in a scene.
Build the UI in code from stock controls instead, and hook their signals with C# events:

```csharp
var window = new Window { Title = "My tool", Visible = false, Transient = true };
window.CloseRequested += window.Hide;
host.UiRoot.AddChild(window);

var box = new VBoxContainer();
box.SetAnchorsPreset(Control.LayoutPreset.FullRect);
window.AddChild(box);
var run = new Button { Text = "Run" };
run.Pressed += () => _ = RunAsync();
box.AddChild(run);

window.PopupCentered(new Vector2I(800, 600));
```

The editor's theme (`ui/editor_theme.tres`) applies to plugin controls automatically, including its style
variations such as `PrimaryButton` (`button.ThemeTypeVariation = "PrimaryButton"`). For bigger layouts you can load a
`.tscn` that has no scripts attached and look nodes up by unique name (`GetNode<Button>("%Run")`).

## Threading and async work

Plugin code runs on Godot's main thread, like the rest of the editor. `await` in plugin code resumes on the main
thread (Godot installs a synchronization context), so after an `await` you can touch nodes and the document again.

- Use `async` methods for anything slow (captures, HTTP, child processes) and start them from UI events with
  `_ = DoWorkAsync();`. Catch exceptions inside: nothing above an event handler will.
- Don't block the main thread waiting for work (`.Result`, `.Wait()`): the editor freezes and a capture waiting for
  the next frame never finishes.
- Code on other threads (`Task.Run`, process output callbacks) must not touch nodes or the document. Hand results
  back with `await`, or `Callable.From(...).CallDeferred()`.

## Shipping a plugin

**As a submodule of this repository** (how first-party add-ons are kept):

```powershell
git submodule add https://github.com/<you>/<repo>.git addons/<Name>
```

The editor's build picks it up from then on. People who clone this repository get the add-on with
`git submodule update --init`, provided they can access its repository; a clone without it still builds.
Changes to the add-on are committed and pushed from inside `addons/<Name>`; then commit in this repository to point
it at the new version.

**As a binary:** the plugin is the folder `plugins/<Name>/` (your DLL, its dependencies and any data files such as
scripts). See below for installing it in an exported editor.

### Plugins in an exported editor

An export doesn't include any plugins: the DLLs aren't Godot resources, and the loader reads plain files, not the
`.pck`. An exported editor only looks in the `plugins` folder next to its executable, so install a plugin by copying
its built folder there:

```
CS2MapProto.exe
CS2MapProto.pck
data_Cs2MapProto_windows_x86_64/     the .NET runtime and the editor's assemblies
plugins/
  ConceptSketch/
    ConceptSketch.dll
    python/...
```

Plugins built in the normal development setup (Debug) work in a Release export: they bind to the editor's assemblies
by name, and the export already has those loaded. Data folders are the same as in development, because `user://`
depends only on the project name.

A plugin is compiled against one version of the editor's API. After `IEditorHost` or model types it uses change,
rebuild it for that editor; a mismatch shows up as a load error in the output (the editor keeps running without it).

## Rules of thumb

- Don't change the document from a plugin. There is no edit API yet (edits wouldn't go through undo or trigger a
  rebuild); if you need one, add it to `IEditorHost` in the editor.
- Debounce `DocumentChanged`: it fires on every mouse move during a drag.
- Clean up in `Shutdown`: cancel work and stop any child processes, or they outlive the editor.
- Add a command-line hook for testing (see [tooling](tooling.md#testing-plugins-from-the-command-line)), so your
  plugin can be checked without clicking through it.
- Keep `Id` stable, and keep layout data backwards-compatible (new properties with defaults).
