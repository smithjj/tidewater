# Asset sources

The source models the game's runtime copies come from. They live here so the exact bytes that produced a
shipped model stay recoverable from a clone; the game itself loads from `public/models/`, never this folder.

| Model | The game loads | Notes |
|---|---|---|
| `lobster_trap_decimated.glb` | `public/models/props/lobster_trap_decimated.glb` | The trap alone, 85,616 triangles (from 1,066,100 in `lobster_trap.glb`). One scene (`LOBSTER TRAP \| Decimated`). The game takes the pot from it by material family: WOOD/TWINE/CORD/IRON. An earlier export also carried the full buoy as a second scene (231,100 triangles in all); the game never used that part, so the runtime copy is this file. |
| `lobster_trap.glb` | — | The full-detail original (1,066,100 tris). No longer loaded — kept as the source. |
| `lobster_trap_buoy.glb` | `public/models/props/lobster_trap_buoy.glb` | The marker float: 92,180 triangles (FLOAT, STEM, WEAR, METAL, PAINT — a trimmed copy of the 145,484-triangle original, without its rope loop, knots and peg). The game paints the FLOAT material red with a yellow band, since the export has no colour for it. Byte-identical to the runtime copy. |
| `pelagic_30.glb` | `public/models/boats/pelagic_30.glb` | Byte-identical to the runtime copy. |

The copies the game loads are kept in step by hand, so a re-export means copying the file into
`public/models/` as well. If they ever differ, the game is using the `public/` one.

## Provenance

Per model: who made it, under what licence, and where the original came from. The repository is public, so
treat anything here as redistributed with it.

- `lobster_trap.glb` — (the same object as the decimated one above, at full detail)
- `lobster_trap_decimated.glb` —
- `lobster_trap_buoy.glb` —
- `pelagic_30.glb` —
