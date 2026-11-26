# Command-line tooling

The editor can be driven from the command line, for scripted exports and for checking changes (to the editor or
to a plugin) without clicking through the UI. Everything after `--` on Godot's command line goes to the editor.

```powershell
$godot = "D:\path\to\Godot_v4.7.2-stable_mono_win64_console.exe"   # the _console build prints output to the terminal
& $godot --path . -- <editor arguments>
```

Build first (`dotnet build Cs2MapProto.sln`, which also builds the checked-out add-ons): running Godot from the
command line doesn't compile C#.

## Export

```powershell
& $godot --headless --path . -- --export <layout.json|demo> <out.vmap> [--compile]
```

Builds the layout (or the built-in demo), writes the `.vmap`, optionally compiles it with CS2's resourcecompiler,
prints warnings and the result, and quits. This mode runs before the UI starts, so plugins aren't loaded.

## Exporting the editor

The repository has a "Windows Desktop" export preset (`export_presets.cfg`), writing to `build/windows/` (ignored by
git). It needs Godot's 4.7.2 mono export templates.

```powershell
& $godot --headless --path . --export-release "Windows Desktop" build/windows/CS2MapProto.exe
```

The export builds the editor's C# itself, in its own `ExportRelease` configuration; the add-ons under `addons/` are
not built or included. To ship plugins with it, build the solution as usual and copy `plugins/<Name>/` folders to
`build/windows/plugins/` (see [plugins in an exported editor](plugins.md#plugins-in-an-exported-editor)).
The exported executable takes the same arguments as below, after `--`:

```powershell
build\windows\CS2MapProto.exe -- --concept-sketch out\sketch
```

## Opening a layout

`--load <layout.json>` opens that layout at startup instead of the demo. It combines with every option below.

## Screenshots

These save the editor's own window, for checking how the viewport and UI look:

| Argument | |
|---|---|
| `--screenshot <file.png>` | After 30 frames, save the window to the file and quit. |
| `--free` | Take it with the free (perspective) camera instead of the top view. |
| `--cam px py pz tx ty tz` | Free camera at p looking at t (Hammer units). |
| `--select-path N` | Select path N (0-based) first, which shows its curve handles and the path profile panel. |
| `--profile K` | With `--select-path`, show profile curve K (0 width, 1 elevation, 2 wall height). |
| `--curve-demo` | Turn every interior path point into a smooth (Bezier) point first. |

```powershell
& $godot --path . -- --load maps/test.json --cam 0 -600 200 0 0 0 --screenshot shot.png
```

These need a window, so leave out `--headless`.

## Testing plugins from the command line

Plugins read the same arguments with `OS.GetCmdlineUserArgs()`, and should offer a mode that runs their main job and
quits, so they can be tested like the editor. A hook that waits a moment for the first layout to load:

```csharp
public void Initialize(IEditorHost host)
{
    var args = OS.GetCmdlineUserArgs();
    int i = Array.IndexOf(args, "--my-plugin");
    if (i >= 0 && i + 1 < args.Length)
        host.UiRoot.GetTree().CreateTimer(1.0).Timeout += async () =>
        {
            int code = 0;
            try { await RunJobAsync(args[i + 1]); }
            catch (Exception e) { GD.PrintErr(e); code = 1; }
            host.UiRoot.GetTree().Quit(code);
        };
}
```

Off-screen captures need the renderer, so plugin tests run **without** `--headless` (a window opens briefly).

Concept sketch, for example:

```powershell
& $godot --path . -- --concept-sketch out\sketch [--no-generate] [--load layout.json]
```

This samples and renders the views into `out\sketch` (renders and `manifest.json`), generates images unless
`--no-generate` is given, prints each view's prompt, and quits.

## Where things go

| | |
|---|---|
| Editor and plugin messages | stdout of the `_console` Godot build. `Plugin loaded: <Name> (<id>) from <dll>` per plugin; load failures print as errors. |
| Plugin data (settings, logs) | `%APPDATA%\Godot\app_userdata\CS2 Map Proto\plugins\<id>\` |
| Built plugins | `plugins\<Name>\` in the project folder (build output; not committed). |
