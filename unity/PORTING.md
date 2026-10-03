# Porting Tidewater to Unity (HDRP, Unity 6.6.3f1)

Goal: full-fidelity port of the web game (`../src`, three.js/WebGPU) to Unity HDRP. The JS version is the
reference: read the original file before porting it, keep its structure, names, constants and comments, and
write down in the file why anything could not be ported 1:1. Same method as `../docs/PORTING.md`.

## Layout

- `Assets/Tidewater/Runtime`  game code, assembly `Tidewater.Runtime` (namespaces `Tidewater.Util`, `Tidewater.World`, ...)
- `Assets/Tidewater/Editor`   editor-only tools and oracle comparers, assembly `Tidewater.Editor`
- `tools/`                    Node scripts that dump the JS side's output for comparison (the oracles)

## Conventions

- **Numerics**: JS computes in doubles and rounds only when storing into a `Float32Array`/`Uint8Array`. Use `double`
  for all arithmetic and `float[]`/`byte[]` for stored grids. Cast to `double` before subtracting two floats.
  `Math.round` is `MathX.Round` (halves up), `Math.imul`/`>>>` are `unchecked` int/uint.
- **Coordinates**: the simulation keeps the three.js axes (x east, y up, z south, right-handed). Unity is
  left-handed, so mirror at the render boundary only (decision still to confirm when the first mesh is built).
- **Names**: C# PascalCase for methods (`heightAt` -> `HeightAt`); constants and fields keep the JS names where practical.
  `TerrainData` clashes with `UnityEngine.TerrainData`: inside `Tidewater.World` ours wins, elsewhere alias it.
- **Proof**: every ported system gets a JS-side dump in `tools/` and an Editor comparer; report exact-match counts.

## Running an oracle comparison (terrain)

```
node unity/tools/dump-terrain.mjs unity/Temp/oracle/terrain
unity recompile                      # with the Editor open on unity/
unity command eval 'UnityEditor.EditorApplication.delayCall += () => System.IO.File.WriteAllText("...report.txt", Tidewater.EditorTools.TerrainOracle.Compare());'
```
Editor calls time out after 5 s on the main thread, so long jobs go through `delayCall` and write a report file.
There is also a menu item, Tidewater > Compare terrain with JS oracle.

## Status

| Area | JS | State |
|---|---|---|
| util/Noise, terrain/TerrainNoise, terrain/IslandShape, WorldLayout | `src/util`, `src/world/terrain`, `src/world` | ported, verified |
| TerrainData (heightfield + masks + queries) | `src/world/TerrainData.js` | ported, **bit-exact** on all 8 grids (heights, rock, sand, path, gully, seagrass, rubble, scarp); 2.1 s generate |
| Terrain rendering (CDLOD, TerrainGPU, shading) | `Terrain.js`, `TerrainGPU.js`, `core/CDLOD.js`, `terrain/*` | not started |
| Ocean (FFT, shore, wake, WaterQuery) | `src/ocean` | not started |
| Boats, player, fishing, economy, UI, audio, sky, post | | not started |

JS comment drift noticed: the `IslandShape.js` header says the default ridge half width is `2.2 * height + 40`; the
code uses `2.5 * height + 50` and that is what the port follows.
