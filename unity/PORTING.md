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
| Shore field (fast-marching wave travel times) | `world/ShoreField.js` | ported, **bit-exact** (all 1M floats of the 512x512 field and the depth map) |
| Shore waves: per-wave heights, shoaling, plunging / bore profile, swash run-up, surf medium, crest path; in the water mesh, fragment and WaterQuery | `ocean/ShoreWaves.js` | ported (`ShoreWaves.hlsl`, `ShoreWaves.cs`); heights in the surf zone match the JS to 2.7e-5 rms (max 1.7e-4), 0 of 255 points off by more than 1 cm. **Caveat**: the per-wave random is `fract(sin(x*127.1+311.7)*43758.5)`, which amplifies the last bits of GPU `sin()` ~40,000x, so the wave heights differ between GPUs (also in the original). The comparison swaps in an exact integer hash on both sides (`TW_SHORE_ORACLE`, `WaterQuery.oracleHash`) |
| Lace texture (foam pattern shared by the sim, the surf and the sand) | `ocean/SurfFoam.js` makeLaceTexture | ported, **byte-exact** (512x512 RGBA8) |
| Shore sim (foam carried by the water, sand wetness, stranded foam) and the surf foam look (whitewater, lace, flow map); the terrain's swash wetness and foam on the sand | `ocean/ShoreSim.js`, `ocean/SurfFoam.js`, `App.js` terrainWetness | ported (`ShoreSim.compute/.hlsl`, `SurfFoam.hlsl`); renders rolling whitewater and trailing lace at the beach. Not compared numerically (a dynamic GPU field); spray deposit buffer exists but nothing writes it yet |
| Sea detail (wind gusts, slicks, streaks): noise texture + the gust / slick factor | `ocean/SeaDetail.js` | ported (`SeaDetail.cs/.hlsl`); noise texture matches the JS (3 half-floats off by 1 ulp); in the water shader |
| Caustics: photon splatting of the FFT cascades into a fine and a broad map, sampled with depth, refraction tilt, foam shading, chromatic dispersion | `ocean/Caustics.js` | ported (`Caustics.cs`, `CausticsSplat.shader`, `Caustics.hlsl`); maps match the JS on Dawn (means within 0.1 %) |
| Underwater lighting of lit surfaces: wave maps baked around the camera (`UnderwaterLight.compute`, `UnderwaterLighting.cs`) + an HDRP custom pass (`UnderwaterLightingPass.cs`, `UnderwaterLighting.shader`) that multiplies the lit colour of everything below the water by the direct (Beer-Lambert + caustics) and ambient factors | `ocean/UnderwaterLighting.js` | ported and working; the JS modulates the direct and ambient terms of each material, HDRP lights its own materials, so the pass blends the two factors by the share of the pixel's light that comes from the sun (estimated from its normal; shadows are not known to it). Baked height at the camera is within 1.2 cm of WaterQuery. `PassDebug.Underwater(bool)`, `PassDebug.Debug(bool)` (shows factor / foam / caustic) and `PassDebug.BakeCheck()` are the Editor tools |
| Underwater composite: medium at the lens (air / water per pixel), absorption + analytic in-scatter of sun and sky, ray-marched caustic shafts, the meniscus band (refraction, contact line, rim) | `post/Underwater.js` | ported (`UnderwaterComposite.shader`, `UnderwaterCompositeCore.hlsl`, `UnderwaterCompositePass.cs`, an HDRP custom pass at BeforePostProcess); depth fade, Snell's window, split view at the waterline all work. Differences: the JS reads the medium side from a mask the water material writes, here the surface within the few cm of the lens is the plane through the camera's water query (height + normal); the shaft march runs per pixel (16 steps) instead of half resolution; no diver's torch yet (needs the flashlight). Editor tools: `PassDebug.Composite(bool)`, `UnderwaterCompositePass.debugMode` (1 medium, 2 distance, 3 raw depth) |
| Spray particles: drops, ligaments, dense spray, mist, bow sheets; ring buffer with GPU emitters (atomic head) and a CPU emit API, hull / box collision, foam deposit into the shore sim | `fx/Spray.js` | ported (`Spray.cs`, `Spray.compute`, `SprayEmit.hlsl`, `Spray.shader` + `SprayCore.hlsl`: procedural quads, premultiplied transparent pass); renders every kind (`SprayDebug.Burst`). Positions are in sim space; the cloud shadow hook returns 1 (no clouds yet), the wave shadow hook waits for Breakers' crest table (`_BrkCrest`). The mipmapped puff / dots textures are generated on the CPU like the JS (not byte-compared) |
| Breakers: shoreline stations, crest finder (wave phase crossings along transects), the spray emitters of a plunging breaker (lip drops, splash-up, roller, bore clash, spindrift, mist), the emission budget (ring head read back), the thrown lip sheet (ribbon mesh) | `ocean/Breakers.js` | ported (`Breakers.cs`, `Breakers.compute`, `BreakersLip.shader` + `BreakersLipCore.hlsl`); the 577 stations are **bit-exact** against the JS (`BreakersOracle`, 2308 / 2308 values). The crest table and spray are GPU-random and dynamic: checked by eye (`BreakersDebug.Stats()`, `BreakersDebug.Frame(...)` frames a crest with its lip in the air; `tools/surf.sh` sets the sea state and runs it forward). Spray's wave-shadow hook now reads the crest table. Not byte-compared: the lip's lace streaks use the shared lace texture |
| Ocean still to port: wake, hull mask, local lights, whale water, sun shadow on the water. (The refraction pass is replaced by HDRP's colour pyramid.) | `WakeSim.js` (needs the hull lines from the boat model), `WhaleWater.js` | not started (hooks marked in `Water.hlsl`) |
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
- `OceanDebug.Advance` is heavy now (the sea, shore sim, spray and breakers all step): more than ~1.5 s of sea time in one eval
  hits the 5 s limit and aborts before anything after it runs. `tools/surf.sh` advances in 1.2 s chunks. A recompile
  resets the whole ocean (sea state included).
- Shader compile errors only show in the console when the shader is first used (`tools/console.sh 10 error`): a shader that
  fails to compile just draws nothing. (HLSL reserved words such as `line` bite.) `SceneBuilder.Build()` times out the 5 s eval
  limit: run it through `delayCall`, or use `PassDebug.AddPasses()` to add the pass volumes to the open scene.
- The underwater lighting pass multiplies after HDRP lighting, so shadowed pixels get slightly too much caustic; fine on the
  seabed, check once boats cast shadows on the water.
