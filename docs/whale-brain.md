# The whale's brain

`src/world/marine/WhaleBrain.js` (517 lines) drives the humpback in the browser game, and the C#
`unity/Assets/Tidewater/Runtime/World/Marine/WhaleBrain.cs` is a line-by-line port of it.

Despite the name there is no neural network in it. No neurons, no weights, no learning, no memory of the
player. It is a deterministic scripted behaviour: a route follower, a keyframe timeline for each surfacing,
and a handful of damped springs. All variation comes from one seeded PRNG (`mulberry32`, `WhaleBrain.js:506`),
so a run is reproducible from `(seed, dt)` alone. The game always uses the default `seed = 1` (`Whale.js:50`);
the oracle runs other seeds.

## Who reads it

| Reader | What it takes | For |
|---|---|---|
| `Whale.js` (`update`, `_pose`, `_effects`) | root pose, `quaternion`, `pathRotation(d)`, `strokePhase`/`strokeAmp`, `arch`, `follow`, `headPitch`, `flipperRotation(side)`, `blow`, `flukeUp`, `slaps`, `water`, `wetAge`, `backDepth` | the K = 40 spine rig, blow/fluke/slap spray, LOD and culling |
| `WhaleWater.js` | `state`, `water - position.y`, `breaches`, `splashes`, `flukeUp` | six analytic foam / slick marks and spray bursts on the surface |
| `SoundScape.js` (`_whale`, `:1322`) | `state`, `position`, `yaw`, `water`, `blow`, the three counters, `flukeUp` | the song bed while cruising, and blow / breach / splash / fluke one-shots within 100 m |
| `Fish.js` (`stepEscort`, `:1349`) | `state`, `water`, `position` (and `velocity`, see loose ends) | escort fish scatter while the whale is at the surface, and spring back to their slots |
| `unity/tools/dump-whale-brain.mjs` | everything | the oracle rows compared against the C# port |

## The route

`ROUTE` (`:13`) is nine waypoints through the bay mouth and past the reef drop-off, made into a closed
Catmull-Rom curve (centripetal, 0.5) and sampled into an arc-length table every 0.25 m (`_buildRoute`,
`:84`). The table stores position and heading; `routeYaw` averages the table yaw over ±2 m so the heading is
smooth, and the yaw is unwrapped across laps (`yawWrap`) so turning is continuous over loops.

`u` advances by `speed * cos(pitch) * dt` (`:489`), so the horizontal speed shrinks when the whale pitches
steeply - that is what makes a breach rise and a fluke-up dive fall rather than slide. Cruise speed is
2.6 m/s, surfacing 1.5; a lap takes about 4.5 minutes. The comment on the route records what was checked
against the terrain: it passes ~130 m off the beach, ~65 m from the pier head, with at least 8.8 m of water
everywhere. The whale starts at `u = length * 0.19` - just before the surfacing zone.

## The sequencer

There is exactly one discrete decision in the brain. With `SURFACE_AT = 0.25` (`:14`), when the route
fraction `u / length` (mod 1) enters `0.25 .. 0.30` and a `lastWrap` latch is clear, `_startSequence()`
runs and the state becomes `'surface'`. The latch re-arms once the fraction leaves `[0.15, 0.55]`, so there
is one surfacing per lap, heading in toward the beach.

A sequence is a list of keyframes - plain `{ t, depth, pitch, arch, follow, speed, stroke, blow, breach,
fluke }` objects: duration in seconds, target root depth below the water, pitch offset, back arch, how hard
the body follows the path, speed, tail stroke amplitude, and flags. The list is built fresh each surfacing:

| Key | t (s) | depth (m) | pitch | follow | speed | flags |
|---|---|---|---|---|---|---|
| rise | 9 | 1.3 | 0.08 | 0.85 | 1.5 | |
| blow (x 3-6) | 3.5 | 1.12 | 0.1 | 0.85 | 1.5 | `blow`, arch 0.02 |
| roll back under (between breaths) | 4 | 2.2 | -0.14 | 0.92 | 1.5 | arch -0.05 |
| under (between breaths) | 7 + rand x 6 | 3.4 | 0 | 0.85 | 1.5 | |
| rise again (between breaths) | 5 | 1.3 | 0.08 | 0.85 | 1.5 | |
| breach: sound | 7 | 9 | -0.2 | 0.6 | 2.4 | optional |
| breach: launch | 6 | 9 | 0.45 | 0.3 | 3.5 | `breach: 'launch'` |
| breach: air | 6 | 2 | 0 | 0.3 | 2.5 | `breach: 'air'` |
| breach: fall back | 6 | 2.4 | 0.05 | 0.85 | 1.5 | |
| arch before the dive | 3 | 1.25 | -0.12 | 0.85 | 1.8 | arch -0.14 |
| fluke-up dive | 4.5 | 5.5 | -0.95 | 0.12 | 1.9 | `fluke` |
| climb out | 6 | 11 | -0.45 | 0.6 | 2.2 | |
| settle to cruise | 8 | 10 | 0 | 0.85 | 2.6 | |

The last breath is followed either by the breach (a 50% coin flip, forced by `forceBreach`) or directly by
the terminal dive - the under-breath keys are only inserted between breaths. The blow itself starts 0.6 s
into a 3.5 s hold and lasts 1.4 s, a `sin(pi t / 1.4) ** 0.5` envelope; `onBlow()` fires once per breath
(the dump tool counts blows with it). While a sequence runs, each key replaces the cruise target, which is
the constant `{ depth: 9.5, pitch: 0, arch: 0, follow: 0.85, speed: 2.6, stroke: 0.13 }`.

Three counters are the event interface for everything downstream - they are read as rising edges:
`breaches++` when the launch key hands over to `'air'` (the whale has broken through, `y > water - 2.5`),
`splashes++` on re-entry (`vy < 0` and `y < water - 1.5`), `slaps++` when a flipper hits the water.

## Depth, breach and attitude

The depth controller (`:434-473`) is the only perception-to-actuator loop:

```js
let floor = -1e9;
for ( const d of [ -9, -5, 0, 5, 10, 16, 24 ] ) floor = Math.max( floor, this.floorAt( x + sy * d, z + cy * d ) );
const yT = Math.max( this.water - target.depth, floor + 5.2 );
const w = target.fluke ? 1.0 : 0.7;
let ay = ( yT - this.y ) * w * w - 2 * w * this.vy;
```

Terrain is probed up to 24 m ahead along the heading so the whale rises before the seabed does, and the
seabed arm (`floor + 5.2`) wins when the water is shallow. The formula is a critically damped spring
(natural frequency `w`, damping `2w`), stiffer during a fluke dive. There is also a hard floor at
`floor + 4.6` that zeroes downward velocity - the comment notes that leaves ~3.3 m under the root, ~1.9 m
under the belly.

In a breach the spring is overridden: `ay = 14` on the way up until the root is within 2.5 m of the surface
(that handover increments `breaches`), then true ballistics, `ay = -9.81` while rising or above the water,
falling back to the spring below. `breachRoll` twists the body to 1.9 rad onto its side in the air and eases
back to zero in other keys.

Attitude and posture are all exponential easings with per-key rates: `speed` (dt x 0.5), `pitch` - the
pitch target is `atan2( vy, max( speed, 0.5 ) ) * 0.8` plus the key's pitch offset, eased at dt x (2.5
breach, 1.1 fluke, 0.9 otherwise) - `arch` (dt x 1.2), `follow` (dt x 1.5/0.8), `strokeAmp` (dt x 0.6)
and `headPitch` (0.03 during a blow). Banking comes from the route curvature: `rollT = clamp( -curv *
speed * 1.6, +-0.22 )` through a small spring. The root quaternion is the Euler `( -( pitch + bob ), yaw,
roll + breachRoll, 'YXZ' )`. The tail beat advances `TAU * ( 0.09 + 0.042 * speed ) * dt` - about 5 s per
stroke cruising, longer at the surface.

## The path history (the closest thing to memory)

The body follows the route by sampling where the root *was*, not by extrapolating. Every update records the
root quaternion stamped with the travelled distance into a ring buffer of 2048 entries (`hArc` Float64,
`hQ` Float32 x 4, `:65-73`). `pathRotation(d)` (`:159`) binary-searches the arc for `arc - d` and slerps
between neighbours; past the oldest entry it clamps. Because entries are stamped with *distance*, the look-up
is framerate-independent: the rig can ask for "the orientation 12 m back" at any dt and get the same answer.

The constructor seeds 61 entries of straight run-in at 0.4 m spacing so the tail has history on frame 0.
One entry per update is roughly 2-4 cm of travel, so the buffer holds on the order of 90 m of path at 60 fps.

## The flippers

Each pectoral flipper has sweep, lift and twist, each a heavy damped spring toward a target (`c =
2 * sqrt( k ) * 0.9`, slightly underdamped; `:266-272`). The targets combine:

- a rowing cycle, one heavy stroke every ~11 s (`row = time * TAU / 11`, phase offset 0.35 between sides);
- turn banking: `turn = clamp( yawRate * 25, +-1 )` dips the inside flipper and reaches the outer one forward;
- surface and dive angles (a little spread at the surface, angled with the dive);
- in the air, a stiff spread (sweep 0.45, lift 0.7, stiffness 2.5);
- a slap, now and then, while the back is at the surface (`water - y < 1.35`, not in the air). A timer
  starts at 12 s and resets to `18 + rand * 25` s; one flipper lifts 1.55 rad over 3.5 s (soft, k = 0.9),
  then comes down hard (k = 9), and at t > 3.8 s of the downstroke `slaps++` - it hits the water at t = 6.

`flipperRotation(side)` maps that to Euler `( twist, sg * ( 0.12 + sweep ), sg * ( -0.28 + lift ), 'YZX' )`
for the rig.

## What it senses

Only its own state, the terrain, and the water:

- **Terrain height** at seven look-ahead points along the heading (`floorAt` -> `terrain.heightAt`).
- **Water height at the head**: one water-query slot (`'whale', 1` - see `docs/boat-query-slots.md`),
  sampled 3 m in front of the root. The readback is 1-3 frames old, so it is exponentially smoothed
  (`water += ( h - water ) * min( 1, dt * 4 )`, a ~0.25 s time constant) and guarded against non-finite
  values. `water` also drives `wetAge` (a dry film after a few seconds out of the water; `Whale.js` uses
  `exp( -wetAge / 12 )` for the skin shader).
- **A seeded RNG** for breath count, under-breath duration, the breach coin flip and the slap timer/side.

It does not sense the player, the boat, the fish, or the time of day. The whale does not react to anything.

## The rig side (`Whale.js`)

`Whale._pose` (K = 40 spine frames) reads the brain at `Whale.js:161-270`: head and chest are rigid with the
root plus a recoil against the stroke; tail frames are `slerp( root, pathRotation( d ), follow )` plus a
travelling stroke wave `strokeAmp * env * ( sin( ph ) + 0.22 sin( 2 ph ) )` with `ph = strokePhase - d *
0.52`, an extra angle-of-attack term for the flukes (`d > 7.6`), and the arch term. Positions integrate
outward from the root frame. The vertex shader then poses every vertex twice (this frame and the last) so
the motion vectors are exact. LODs at 48 / 170 m, culled past 1800 m, and a deep whale is hidden past 70 m
from above the water (past 120 m underwater, where visibility is short anyway).

`Whale._effects` turns the brain's events into spray: the blow column at the blowhole while `blow > 0`,
drips off the fluke trailing edge while `flukeUp > 0.05`, and a sheet of spray where a flipper slap lands.
`WhaleWater` keeps six foam/slick marks in a uniform array: churn patches every 1.2 s while the back is at
the surface, big bursts on breach and re-entry, and the flat "fluke print" slick after the dive.

## Framerate and determinism

`update( dt )` runs once per render frame from `App.js:799` (`Whale.update`), with dt clamped to 0.1 s
(`:363`). There is no fixed timestep; framerate independence comes from the distance-stamped history, the
exponential easings (`min( 1, dt * rate )`), and the `dt`-scaled integration. A run is fully deterministic
given `(seed, dt)` - not across different dt, since event timing quantises to frames.

## The oracle and the Unity port

`unity/tools/dump-whale-brain.mjs` imports the real `WhaleBrain.js` and runs it on the real terrain with no
water query (sea level 0), in three scenarios:

| Name | Seed | dt | Frames | |
|---|---|---|---|---|
| a | 1 | 1/30 | 36 000 | |
| breach | 7 | 1/60 | 40 000 | `forceBreach` |
| slow | 123 456 789 | 0.09 | 9 000 | |

Every 20th frame it records 35 columns (route position, pose, angles, flippers, the root quaternion, the
path rotation 12 m behind, `wetAge`), and every blow / breach / splash / slap / state change with its frame,
into `whale-brain.json`. `WhaleOracle.Compare` replays the same runs in C# and asserts agreement within
1e-6 on every column and on every event frame; it prints the worst diff.

Workflow (Editor open on `unity/`):

```
node unity/tools/dump-whale-brain.mjs unity/Temp/oracle/whale
unity/tools/run-oracle.sh WhaleOracle
```

## Loose ends

- `brain.velocity` is constructed (`WhaleBrain.js:34`) but never written by the brain, and the C# port has
  the same field. `Fish.js:1391-1394` reads it for the escort's "match the whale's own velocity" term and
  its speed cap - so both are inert, and only the position spring actually tracks the whale. Either a
  missing write or something to delete.
- `dump-whale-brain.mjs` and `WhaleOracle.cs` both mention `unity/tools/whale-oracle.sh` in their headers,
  but there is no such file; `run-oracle.sh` takes the oracle class name, so `run-oracle.sh WhaleOracle` is
  the runner.
- `test/life-whale.mjs` exercises the brain headless: it runs it to the surface, renders the whale, and
  prints what the ten-minute run emitted (spray particles, slaps). It is a smoke test, not a comparison.
