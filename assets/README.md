# Asset sources

The source models the game's runtime copies come from. They live here so the exact bytes that produced a
shipped model stay recoverable from a clone; the game itself loads from `public/models/`, never this folder.

| Model | The game loads | Notes |
|---|---|---|
| `lobster_trap_decimated.glb` | `public/models/props/lobster_trap_decimated.glb` | The trap *and* its buoy in one file, 231,100 triangles for the pair (was 1,211,584 across the two originals). Two scenes: the buoy is scene 0 (`LOBSTER BUOY`), the trap is scene 1 and the default (`LOBSTER TRAP \| Decimated`). The game splits it by material family — WOOD/TWINE/CORD/IRON become the pot (85,616 tris) and FLOAT/STEM/WEAR/METAL/PAINT the marker float (92,180 tris); the rope materials in it are left out, because the line down to the pot is drawn to the actual depth. |
| `lobster_trap.glb` | — | The full-detail original (1,066,100 tris). No longer loaded — kept as the source. |
| `lobster_trap_buoy.glb` | — | The original buoy on its own (145,484 tris). Not loaded: the decimated file carries a decimated copy of it. |
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
