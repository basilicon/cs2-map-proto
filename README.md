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

Perimeter walls are generated around everything walkable.

## Controls

| Key | Action |
|---|---|
| 1-7 | Select, Path, Floor, Obstacle, Pit, T spawn, CT spawn |
| Tab | Toggle top-down (ortho) / free fly camera |
| Middle-drag, Space+drag, WASD | Pan (top-down) / move (free) |
| Wheel | Zoom (top-down) / fly speed (free) |
| Right mouse (free cam) | Hold to look; Q/E down/up |
| Click / drag | Select & move points (snapped; hold Alt for off-grid) |
| Shift+drag, PgUp/PgDn, Shift+wheel | Vertical offset of selection (or the working elevation while drawing) |
| Ctrl+click edge | Insert a point |
| Enter / right-click | Finish drawing; Backspace removes last draft point; Esc cancels |
| Del / Shift+Del | Delete point / whole shape |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Ctrl+S, Ctrl+E | Save layout (.json), export .vmap |
| F | Frame everything |

## Export

Set the export target to `...\Counter-Strike Global Offensive\content\csgo_addons\<addon>\maps\<name>.vmap`.

- The `.vmap` is written as text DMX (keyvalues2). Valve's `dmxconvert` and `resourcecompiler` both accept it.
- **Convert to binary** runs CS2's `dmxconvert.exe` so the file matches what Hammer saves itself.
- **Compile after export** runs `resourcecompiler -world -phys -fshallow` (no lighting bake, a few seconds). Then launch CS2 with Workshop Tools for that addon and run `map <name>`.

Headless: `Godot_..._console.exe --headless --path . -- --export <layout.json|demo> <out.vmap> [--compile]`

## Code map

- `src/Model` – the document (JSON-serialisable).
- `src/Geometry` – `MapBuilder` turns the document into closed meshes (Clipper2 for 2D booleans, hole bridging + ear clipping, convex face merge).
- `src/Export` – DMX keyvalues2 writer, `.vmap` (CMapRootElement / CMapMesh / half-edge CDmePolygonMesh) exporter, CS2 tool runner.
- `src/Editor` – Godot nodes: `EditorRoot` (input, tools, undo), `EditorUi` (`[Export]` bindings to the premade controls in `scenes/Main.tscn`), camera, preview, overlay.
