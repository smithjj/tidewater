# The boats' water-query slots

**Status: resolved** (this started as an open question about headroom; fixing a worse bug settled it).

## Background

`WaterQuery` hands out a fixed table of `MAX_QUERIES = 64` slots (`src/ocean/WaterQuery.js`); allocating
past it throws `WaterQuery: out of slots`. Each hull sample of a `BoatController` asks the GPU for the water
height under it, one slot per sample.

The boat fix (`src/player/BoatController.js`) added **reserve buoyancy**: 5 stations x 2 sides of extra
samples at the rail, so the righting moment lasts to ~88 degrees instead of ~40 and the boat stops rolling
over. Querying those 10 as well would have cost 10 more slots per boat.

## The bug this turned up

Both boats' controllers allocated under the **same name**, `'boatHull'`, and `WaterQuery.allocate` gives back
the same slots for a name it already has. So the lobster boat and the Pelagic 30 shared one block, and
every frame `App.update` queued the lobster boat's sample positions and then the Pelagic's
(`App.js` lines ~728-729), the second overwriting the first. The lobster boat then read back water heights
measured **at the Pelagic's mooring**, for as many samples as the Pelagic has (12 originally, 22 once the
reserve samples were added). Different place, different waves: the lobster boat floated a metre out of
the water, or under it, and tilted to match.

Reproduced headless: with the Pelagic beside it, the lobster boat's heave was off by 1.04 m and its tilt by
16.6 degrees against the same boat alone in the same sea.

## What was done

1. **Each controller gets its own block of slots** (`'boatHull' + n`, a counter).
2. **Reserve samples are not queried.** Each takes the surface plane of its nearest hull sample (that
   sample's height, plus its slope times the offset between the two) every physics step. They sit within
   about a metre of a hull sample, so the difference is small; stability results did not change.

Result: lobster 32 + Pelagic 12 = 44 slots for the boats, and ~62 of 64 for the whole game, the same as
before any of this. Headroom is the original 2 slots, so a future query user should know about the cap.

## Slot budget (my count of every `allocate()` call)

| User | Slots | Where |
|---|---|---|
| Lobster boat hull | 32 | `src/player/BoatController.js` |
| Pelagic 30 hull | 12 | same, its own block |
| Traps (`TRAP_LIMIT`) | 6 | `src/game/Traps.js`, `src/game/Gear.js` |
| Wildlife | 8 | `src/world/wildlife/Wildlife.js` (class default `n = 8`; not confirmed against what `App.js` passes) |
| Bobber, player, whale, Pelagic bob | 1 each | `FishingRod.js`, `Player.js`, `WhaleBrain.js`, `Pelagic30.js` |
| **Total** | **~62 of 64** | |

## Checking

`node test/boat-stability.mjs` covers this: both boats and the rest of the game's allocations must fit in
64 slots, and a lobster boat beside the Pelagic must ride exactly as it does alone. The test fails with
the old shared allocation. The same file checks the stability range, slope following and the no-capsize
runs.
