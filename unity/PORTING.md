# Porting Tidewater to Unity (HDRP, Unity 6.6.3f1)

Goal: full-fidelity port of the web game (`../src`, three.js/WebGPU) to Unity HDRP. The JS version is the
reference: read the original file before porting it, keep its structure, names, constants and comments, and
write down in the file why anything could not be ported 1:1. Same method as `../docs/PORTING.md`.

## Layout

- `Assets/Tidewater/Runtime`  game code, assembly `Tidewater.Runtime` (namespaces `Tidewater.Util`, `Tidewater.World`, ...)
- `Assets/Tidewater/Editor`   editor-only tools (oracle comparer, scene builder, camera placer), assembly `Tidewater.Editor`
- `Assets/Tidewater/Shaders`  HLSL ported from the WGSL (HDRP shaders)
- `Assets/Tidewater/Scenes`   `Island.unity` (built by Tidewater > Build island scene)
- `tools/`                    Node scripts that dump the JS side's output (oracles) and Editor helper scripts

## Conventions

- **Numerics**: JS computes in doubles and rounds only when storing into a `Float32Array`/`Uint8Array`. Use `double`
  for all arithmetic and `float[]`/`byte[]` for stored grids. Cast to `double` before subtracting two floats.
  `Math.round` is `MathX.Round` (halves up), `Math.imul`/`>>>` are `unchecked` int/uint.
- **Coordinates**: the simulation keeps the three.js axes (x east, y up, z south, right-handed). Unity is
  left-handed, so mirror at the render boundary only (decision still to confirm when the first mesh is built).
- **Names**: C# PascalCase for methods (`heightAt` -> `HeightAt`); constants and fields keep the JS names where practical.
  `TerrainData` clashes with `UnityEngine.TerrainData`: inside `Tidewater.World` ours wins, elsewhere alias it.
- **Rendering model**: HDRP does the lighting, shadows, sky and post. What gets ported from the web version is the
  *content*: generation, materials, geometry, simulation. A material is an HDRP shader whose `GetSurfaceAndBuiltinData`
  runs the ported surface code (see `Shaders/Terrain`, modelled on HDRP's own TerrainLit.shader and its
  `HAVE_MESH_MODIFICATION` hook). The web engine's own lighting / shadow / tonemapping / TAA passes are not
  reimplemented; where HDRP lacks something the game needs (e.g. the heightfield sun shadow, wave-aware water
  lighting) it is added as a custom pass or shader term, with a note in the file.
- **Per-camera drawing**: anything that selects geometry per camera (CDLOD) does it in
  `RenderPipelineManager.beginCameraRendering` and draws with that camera, never from `Update`.
- **Proof**: every ported system gets a JS-side dump in `tools/` and an Editor comparer; report exact-match counts.

## Driving the Editor from the CLI

Open it once with `unity open .` in `unity/` (the Unity CLI is `C:\Users\smith\AppData\Local\Unity\bin\unity.exe`).

- `unity recompile --focus` compiles scripts and prints errors. Use `--focus`: an unfocused Editor does not tick
  `EditorApplication.delayCall`, so queued work silently waits until the window is brought forward.
- `unity command eval '<C#>'` runs code on the main thread with a **5 s limit**; anything longer goes through
  `EditorApplication.delayCall` and writes a report file (see `tools/run-oracle.sh`).
- `tools/console.sh [N] [level]` prints the console; `tools/shot.sh name simX simZ eye yaw pitch [sea]` places the
  camera in sim coordinates and saves `Temp/shots/<name>.png` (pitch > 0 looks down).
- Shaders compile when first used: read errors with `tools/console.sh 30 error` after a screenshot.

## Running an oracle comparison (terrain)

```
node unity/tools/dump-terrain.mjs unity/Temp/oracle/terrain
unity/tools/run-oracle.sh            # focuses the Editor, runs TerrainOracle.Compare, prints the report
```
There is also a menu item, Tidewater > Compare terrain with JS oracle.

## Status

| Area | JS | State |
|---|---|---|
| util/Noise, terrain/TerrainNoise, terrain/IslandShape, WorldLayout | `src/util`, `src/world/terrain`, `src/world` | ported, verified |
| TerrainData (heightfield + masks + queries) | `src/world/TerrainData.js` | ported, **bit-exact** on all 8 grids (heights, rock, sand, path, gully, seagrass, rubble, scarp); 2.1 s generate |
| Terrain bakes: normal / rock / AO map, splat map, tileable detail texture | `terrain/TerrainBake.js`, `terrain/DetailTextures.js` | ported, **byte-exact** (16.8 MB + 16.8 MB + 1 MB) |
| Terrain rendering: CDLOD, GPU textures, HDRP shader with the full terrain material | `Terrain.js`, `TerrainGPU.js`, `core/CDLOD.js`, `terrain/TerrainShading.js` | ported and rendering (51 nodes, 163k tris, 2.8 s startup); looks right in the Editor, not yet compared pixel-for-pixel |
| Terrain: heightfield sun shadow, shore field / swash wetness, refraction path | `TerrainGPU.js` (sun shadow), `ShoreField.js`, `ShoreSim.js` | not started (static damp band and HDRP cascades stand in) |
| Ocean FFT: spectrum, 4 cascades, foam accumulation, sea-state presets | `ocean/OceanFFT.js`, `ocean/Conditions.js` | ported (compute shader, `Shaders/Ocean/OceanFFT.compute`); matches the JS running on Dawn to 0.01 % rms (float16 rounding) after 1 and 120 frames, all cascades and channels. Foam is 0 on both sides at the default wind: check it with a storm-wind dump |
| Ocean surface: CDLOD mesh, displacement, shallow-water attenuation, normals, foam, shading (absorption / scattering, Fresnel, sun glitter, SSR, below-water view), foam pattern | `ocean/WaterSurface.js`, `ocean/WaterMaterial.js`, `ocean/FoamTexture.js` | ported and rendering (`Shaders/Ocean/Water.hlsl`, `OceanRenderer`); looks right; not compared pixel for pixel. Units: HDRP's sky cubemap and ambient probe are in physical units and the shader applies the exposure itself |
| Ocean: shore waves / swash, shore sim, breakers, wake, surf foam, sea detail, caustics, refraction pass, underwater post, hull mask, local lights, sun shadow on the water | `ShoreWaves.js`, `ShoreSim.js`, `Breakers.js`, `WakeSim.js`, `SurfFoam.js`, `SeaDetail.js`, `Caustics.js`, `RefractionPass.js`, `post/Underwater.js` | not started (hooks marked in `Water.hlsl`) |
| WaterQuery: GPU height solve + async readback for CPU physics | `ocean/WaterQuery.js` | ported (`WaterQuery.compute`, `WaterQuery.cs`); heights match the JS to 2.7e-4 rms (max 9e-4, float16 level) on 255 deep-water points; normals to 5e-3. Slot 0 follows the camera and the water shader reads it the same frame (`cameraWaterHeight`). Shore and wake terms get added to `WaterQueryDispAt` with their systems |
| Boats, player, fishing, economy, UI, audio, sky, post | | not started (sky, shadows and post come from HDRP) |

JS comment drift noticed: the `IslandShape.js` header says the default ridge half width is `2.2 * height + 40`; the
code uses `2.5 * height + 50` and that is what the port follows.

## Known issues

- Exposure: the template volume's automatic exposure (limits EV 2..14) gives a dark sky and island in the Editor and
  does not settle between scripted camera jumps; `tools/shot.sh` has `EV=` (fixed) / `EV=auto`. The look (sky,
  exposure, bloom) is art direction still to do against the web version.

- Terrain: a few speckled pixels right under the camera at high angles over the seabed (probably derivative use
  across the seabed / land branch in `TerrainSurface.hlsl`); check when the water exists.
- The sea is a flat placeholder plane (`Sea (placeholder)`), to be replaced by the ocean port.
