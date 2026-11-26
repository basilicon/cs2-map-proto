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
| **Ceiling** | Roof slab over the floor at its elevation. Height = clearance up to its underside. Warns below 72u (can't stand). |
| **Spawns** | `info_player_terrorist` / `info_player_counterterrorist`. |

Perimeter walls are generated around everything walkable. Each wall takes its height from the floor next to it
(floor height + wall height), so walls follow ramps and stay low beside low corridors.

### Overpasses and stacked levels

Floors, paths and ramps can pass over each other: a bridge across a corridor, a balcony over a site, a corridor
under a ramp. Two walkable surfaces count as one level when their heights are less than 88u apart (72u player
height + the floor thickness): a step, or a ramp meeting a floor, and no wall goes between them. Surfaces further
apart are separate levels:

- the upper one gets walls (railings) along its edges where it passes over the lower one, standing on its own floor;
- the lower one keeps its walls underneath, up to the underside of what's above;
- outer walls next to a stacked level take that level's height, so a bridge's walls don't slope down to the corridor below.

A path side with its wall switched off stays open on its own level only. A drop of 88u or more between two floors
that touch (a ledge) is walled on both sides the same way; switch the path side off to leave it open. Floors that
overlap with less than 88u between them get a warning, since nobody fits in between. The demo layout has an
overpass crossing its covered corridor.

### Ceilings

A path can have its own roof: tick **Ceiling** in the path's wall settings and set the clearance. That roof follows
ramps and the elevation profile, and it reaches over the walls on both sides. A **Ceiling** region roofs whatever
it covers, for example a room or part of a site. Walls under a roof meet it: they're raised or cut down to the
roof's top, so there's no gap and nothing pokes through. Ceilings show in the free camera only; the top view draws
just their outlines.

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
- **Tool rail** (left): the eight tools, coloured like their shapes in the viewport, with their hotkeys.
- **Inspector** (right): a card naming the selection, then only the properties that apply to it; Drawing, Build and Export sections fold away.
- **Viewport chips**: current tool, working elevation and grid (top left); build warnings (top right, click for the list; hidden when there are none).
- **Status bar**: hints and results, last export time, cursor position (and Δz while dragging vertically).

## Controls

| Key | Action |
|---|---|
| 1-8 | Select, Path, Floor, Obstacle, Pit, Ceiling, T spawn, CT spawn |
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
(more in [docs/tooling.md](docs/tooling.md)).

## Plugins

Add-ons live outside the editor as their own assemblies (`plugins/<Name>/<Name>.dll`) and are loaded at startup.
Their commands appear in a **Tools** menu in the top bar, which stays hidden when no plugin is installed. They can
read the layout, render it off screen (preview, depth and semantic passes from any camera), and keep their own data
in the layout file.

- [docs/plugins.md](docs/plugins.md): writing a plugin, the API, off-screen capture, data, UI, shipping.
- [docs/tooling.md](docs/tooling.md): command-line export, screenshots, and testing plugins without the UI.

## Code map

- `src/Model` – the document (JSON-serialisable).
- `src/Geometry` – `MapBuilder` turns the document into closed meshes (Clipper2 for 2D booleans, hole bridging + ear clipping, convex face merge).
- `src/Export` – DMX keyvalues2 writer, `.vmap` (CMapRootElement / CMapMesh / half-edge CDmePolygonMesh) exporter, CS2 tool runner.
- `src/Plugins` – plugin API (`PluginApi.cs`), loader, and `EditorHost` (the editor side of the API).
- `src/Editor` – Godot nodes: `EditorRoot` (input, tools, undo), `EditorUi` (`[Export]` bindings to the premade controls in `scenes/Main.tscn`, found by unique name), camera, preview, overlay.
- `ui/` – `editor_theme.tres` (the dark theme, set as the project's custom theme; style variations such as `RailButton`, `SegmentButton`, `Chip`) and the SVG icons.
