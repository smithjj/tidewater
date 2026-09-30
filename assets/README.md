# Asset sources

The source models the game's runtime copies come from. They live here so the exact bytes that produced a
shipped model stay recoverable from a clone; the game itself loads from `public/models/`, never this folder.

| Model | The game loads | Notes |
|---|---|---|
| `lobster_trap.glb` | `public/models/props/lobster_trap.glb` | Byte-identical to the runtime copy. Hauled aboard as the hero pot, scaled down at load time. |
| `pelagic_30.glb` | `public/models/boats/pelagic_30.glb` | Byte-identical to the runtime copy. |
| `lobster_trap_buoy.glb` | — | Not referenced by the code. |

The two copies are kept in step by hand, so a re-export means copying the file into `public/models/` as
well. If they ever differ, the game is using the `public/` one.

## Provenance

Per model: who made it, under what licence, and where the original came from. The repository is public, so
treat anything here as redistributed with it.

- `lobster_trap.glb` —
- `pelagic_30.glb` —
- `lobster_trap_buoy.glb` —
