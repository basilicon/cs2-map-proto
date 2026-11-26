# CS2 Map Proto

Godot 4.7 (C#) tool for blocking out Counter-Strike 2 layouts quickly: draw corridors, floors, obstacles and pits,
then export straight to a Hammer `.vmap` (and optionally compile it so `map <name>` works in CS2 right away).

Open the folder in Godot 4.7.2 mono and press Play (F5). Everything is C#; the only non-C# file is the preview shader.

## Concepts (all in Hammer units, Z up)

| Shape | What it builds |
|---|---|
| **Path** | Polyline + width. Each point has its own Z; segments between different Z become ramps. Same-height parts are merged into one floor. |
| **Floor** | Extra walkable polygon at an elevation (sites, open areas). Merged with paths at that height. |
| **Obstacle** | Solid block from its elevation up by Height. Warns if lower than 65u (jumpable). |
| **Pit** | Hole cut from the floor at its elevation. Height = depth of a floor + liner walls below; 0 = bottomless. |
| **Spawns** | `info_player_terrorist` / `info_player_counterterrorist`. |

Perimeter walls are generated around everything walkable. Each wall takes its height from the floor next to it
(floor height + wall height), so walls follow ramps and stay low beside low corridors.

### Path profiles

With a path selected, the **Path profile** panel under the viewport edits a curve over the path's length (left = first point,
right = last point). Pick which value it controls:

| Curve | Meaning |
|---|---|
| Width | Multiplier of the path width (0.25x-3x) |
| Elevation | Offset added to the points' heights (-256..256u); a bump turns that stretch into a smooth ramp |
| Wall height | Multiplier of the wall height (0x = no wall along that stretch) |

Click the graph to add a key, drag keys, right-click a key to remove it. Editing a curve switches it on; the
checkbox toggles it and Reset flattens it. Curves are saved in the layout file.

## Layout

- **Top bar**: file name (with an *unsaved* marker), undo/redo, Top/Free camera switch, the File menu (new, open, save, save as) and **Export .vmap**.
- **Tool rail** (left): the seven tools, coloured like their shapes in the viewport, with their hotkeys.
- **Inspector** (right): a card naming the selection, then only the properties that apply to it; Drawing, Build and Export sections fold away.
- **Viewport chips**: current tool, working elevation and grid (top left); build warnings (top right, click for the list; hidden when there are none).
- **Status bar**: hints and results, last export time, cursor position (and Δz while dragging vertically).

## Controls

| Key | Action |
|---|---|
| 1-7 | Select, Path, Floor, Obstacle, Pit, T spawn, CT spawn |
| Tab | Toggle top-down (ortho) / free fly camera |
| Middle-drag, Space+drag, WASD | Pan (top-down) / move (free) |
| Wheel | Zoom (top-down) / move towards the cursor (free) |
| Right mouse (free cam) | Hold to look; Q/E down/up; wheel while holding = fly speed |
| Click / drag a marker | Select & move a point (snapped; hold Alt for off-grid) |
| Click a path / region line | Select the **whole shape**: drag, PgUp/PgDn, width, Z, C and Del then apply to all its points |
| Shift+drag, PgUp/PgDn, Shift+wheel | Vertical offset of selection (or the working elevation while drawing) |
| Ctrl+drag a marker | Move just that point, unwelding it from paths that share its position |
| Ctrl+click a line | Insert a point |
| Right-click a marker | Delete it (placed point, draft point, spawn; on a curve handle it makes the point sharp). In free cam use a quick click. |
| Right-click empty space / Enter | Finish drawing; Backspace removes last draft point; Esc cancels |
| Path tool: click an existing path point | Start from / snap to it: from an end point it **extends** that path, elsewhere it starts a **branch** |
| B | Branch (or extend) from the selected path point |
| "Endpoint depth" field | With a path's first or last point selected: how far the floor runs past that end (0 = stop at the point). Separate per end and independent of width. |
| "Corner" buttons | How the path turns at the selected point (or every point of a selected path): **Sharp** (edges meet in a point; corners sharper than ~30° are bevelled), **Bevel** (cut straight), **Round** (arc), **Smooth** (Bezier curve; drag the pink handles to shape it) |
| C | Toggle the selected point (or whole path) between Smooth and its Sharp/Bevel/Round style |
| Del / Shift+Del | Delete point / whole shape |

Path points that sit exactly on top of each other (branch junctions, joined ends) are welded: dragging one moves all of them.
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Ctrl+S, Ctrl+E | Save layout (.json), export .vmap |
| F | Frame everything |

## Export

Set the export target to `...\Counter-Strike Global Offensive\content\csgo_addons\<addon>\maps\<name>.vmap`
(use the CS2 install that has the Workshop Tools). Clicking **Export .vmap** with no target set opens a file
picker in that folder and exports as soon as you choose a file.

There are two files per map, and they update separately:

| File | Who reads it | Updated by |
|---|---|---|
| `content\csgo_addons\<addon>\maps\<name>.vmap` | Hammer | Every export. Reopen it in Hammer to see changes (Hammer doesn't reload open files). |
| `game\csgo_addons\<addon>\maps\<name>.vpk` | The game (`map <name>`) | Only a compile: tick **Compile after export**, or build the map in Hammer. |

Saving in Hammer overwrites the `.vmap`; the next export from this tool overwrites Hammer's edits.

- The `.vmap` is written as text DMX (keyvalues2). Valve's `dmxconvert` and `resourcecompiler` both accept it.
- **Convert to binary** runs CS2's `dmxconvert.exe` so the file matches what Hammer saves itself.
- **Compile after export** runs `resourcecompiler -world -phys -fshallow` (no lighting bake, a few seconds). Then launch CS2 with Workshop Tools for that addon and run `map <name>`.

Headless: `Godot_..._console.exe --headless --path . -- --export <layout.json|demo> <out.vmap> [--compile]`

## Plugins

Add-ons live outside the editor as their own assemblies: `plugins/<Name>/<Name>.dll` (next to the executable, or
in the project folder when running from Godot). The editor loads them at startup; their commands appear in a
**Tools** menu in the top bar, which stays hidden when no plugin is installed.

A plugin is a plain .NET class library (`addons/<Name>/<Name>.csproj`, usually its own repository added as a submodule) that references `Cs2MapProto.csproj` with
`Private="false"`, builds into `plugins/<Name>/`, and has a public class implementing `Cs2MapProto.Plugins.IEditorPlugin`.
`IEditorHost` (see `src/Plugins/PluginApi.cs`) gives it:

- the document, the built map, and change / rebuild events;
- Tools menu entries, a UI root for its windows, and the status bar;
- `CaptureAsync`: off-screen renders from any camera, with **beauty** (the preview look), **depth** (white near,
  black far) and **semantic** (flat colour per mesh kind, ADE20K palette, see `CapturePalette`) passes;
- a per-plugin data folder, and per-layout data saved in the layout file under `"Plugins": {"<id>": ...}`.

Godot only registers script classes from the main assembly, so plugins build their UI in code from stock controls.

### Add-ons in this repository

Add-ons are git submodules under `addons/`; building the solution (or pressing Play in Godot) builds every add-on
that is checked out, into `plugins/<Name>/`. The editor works without any of them.

| Add-on | Repository | What it does |
|---|---|---|
| Concept sketch | [cs2-concept-sketch](https://github.com/basilicon/cs2-concept-sketch) (private) | Renders sampled views of the map and turns them into concept art with Stable Diffusion + ControlNet |

`git submodule update --init` fetches the ones you have access to; without access, the clone just has an empty folder.

## Code map

- `src/Model` – the document (JSON-serialisable).
- `src/Geometry` – `MapBuilder` turns the document into closed meshes (Clipper2 for 2D booleans, hole bridging + ear clipping, convex face merge).
- `src/Export` – DMX keyvalues2 writer, `.vmap` (CMapRootElement / CMapMesh / half-edge CDmePolygonMesh) exporter, CS2 tool runner.
- `src/Plugins` – plugin API (`PluginApi.cs`), loader, and `EditorHost` (the editor side of the API).
- `src/Editor` – Godot nodes: `EditorRoot` (input, tools, undo), `EditorUi` (`[Export]` bindings to the premade controls in `scenes/Main.tscn`, found by unique name), camera, preview, overlay.
- `ui/` – `editor_theme.tres` (the dark theme, set as the project's custom theme; style variations such as `RailButton`, `SegmentButton`, `Chip`) and the SVG icons.
