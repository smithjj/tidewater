# Tidewater

An island fishing game for the browser. Cast from the pier, the beach or your own boat, fight the fish,
sell your catch to Joe at the fish stand, and spend it on better gear at Marta's chandlery — or on a trap
licence and a gear of lobster pots. Around it is a real-time tropical island and ocean: swim the reef, drive
out to deep water, and watch a humpback breach. The island runs on its own clock: days pass, the weather
comes and goes, and the fish feed at dawn and dusk. It runs directly on WebGPU and WGSL with its own small
rendering engine, no framework.

**Play it:** https://dgreenheck.github.io/tidewater/

![Fishing off the pier at golden hour](docs/screenshot.jpg)

![The beach in the late afternoon](docs/screenshot-beach.jpg)

## Requirements

- A browser with WebGPU: a recent Chrome, Edge or Safari.
- A capable GPU. It targets 60 fps at 2560×1267 on an Apple M5 Pro, and dynamic resolution scales
  the render down on slower machines.
- The first load compiles several hundred shaders, which can take a minute or more. Later visits are
  faster because the browser caches them.

## Features

**Fishing**
- A spinning rod and reel that cast, reel and bend under load, with the bail, rotor and crank animated.
- Bites that depend on the water (shallows, pier, reef, bay, deep water), depth and time of day, across
  19 Caribbean species: 18 that take a hook, and the spiny lobster that only comes up in a pot.
- A line-tension fight: keep the tension in the green band, ease off when the fish runs.
- A full-screen catch card with the fish's length and weight, a fish log with records, and a cooler.
- Joe's fish stand buys your catch; Marta's chandlery sells line, reels, rods, a bigger hold, fuel, a rebuilt
  engine, a fish finder and deck floodlights for night fishing.
- A trap line: buy the licence and the pots from Marta, set them from the working boat, and haul them for
  lobster (and whatever else walked in) once they have soaked. They fish on the world clock, and the map
  marks where you left them.
- A fish guide (J): every species, a blacked-out silhouette until you catch one. What it shows grows with how
  many you have caught (a rough size range, then a tighter one, its waters and when it bites) and how many you
  have sold (a guess at its price, then the real one), with a map of where you caught it over an estimate of
  its water that sharpens as you learn it.
- A daily market: Joe pays a different rate per species every day, so holding a catch overnight is a
  decision. His board lists the day's movers.
- Joe's order of the day: from day 2 he asks for one species, a decent size or bigger, and pays 25% more on
  every fish of it you sell that day, on top of the market rate. It is on his board, and a ★ marks the fish in
  your cooler that fill it.
- Walk the deck and the wheelhouse while the boat drifts; the boat burns fuel.
- Two boats with decks you can stand on and fish from: the lobster boat (which carries the
  hauler and the pots) and the Pelagic 30 off the pier head; and a mini fishing boat off the end of the pier,
  which you board straight to its seat.
- A first-play guide, contextual tips and a minimap. Progress is saved in the browser.

**The day**
- A world clock that runs by default (a day in about twenty minutes, `T` to pause) with a day counter, shown
  in the HUD. The time, the day and the weather are saved, so a session resumes where it left off.
- Island hours: Joe opens early and shuts around dusk, Marta keeps shop hours.
- Weather that arrives on its own: the sea state walks between calm and storm on in-game time, biased to
  calm mornings and rare blows, with the waves, foam, shore surf, wind and cloud cover all following it.
  Purely atmospheric — nothing in the weather can hurt you, your boat or your gear.
- Bite tables, the dawn chorus, night tarpon, the lamps and the deck floodlights all key off the clock.

**Ocean**
- Four-cascade FFT ocean (Tessendorf spectra) with foam, whitecaps, wind streaks and swell.
- Depth-aware breaking waves with peeling shoulders, whitewater, spray and foam lace.
- A shallow-water simulation for swash running up and down the sand.
- Boat wake and bow spray, and a whale wake.
- Caustics on the seabed and in the water, with light shafts.
- A split underwater/above-water view at the waterline, with water droplets on the lens after surfacing.
- Refraction of the seabed through the surface, including behind the pier and boats.

**Sky**
- Physically based atmosphere (Hillaire 2020) with a sun, moon and stars.
- Volumetric cumulus and wispy cirrus with cloud shadows on the land.
- Aerial perspective and sea haze.
- God rays, and a lens flare with occlusion.

**World**
- An island with a beach, hills, headlands and rocks.
- A fishing village, a pier, and the vendors' stalls built from Poly Haven scans.
- Three boats at the pier, all drivable: a lobster boat lofted from its own hull lines, a Pelagic 30
  centre console, and a one-man mini fishing boat, the last two loaded from authored models.
- Realistic vendor characters (Microsoft Rocketbox) with skinned animation.
- A coral reef with fish, and a spotted eagle ray (a modelled asset, baked into the fish frame) over the drop-off.
- Palms, bananas, monstera, elephant ear, heliconia, bird of paradise, broadleaf trees, shrubs and dune
  grass, with impostors and dithered LOD fades.
- Beach debris.
- Birds, crabs and marine snow.
- A humpback whale with an escort of fish, blows, fluke dives and breaches.

**Lighting and post**
- Cascaded shadows with contact-hardening penumbrae, and screen-space contact shadows.
- Ground bounce light.
- GTAO ambient occlusion.
- Temporal upscaling and sharpening.
- Bloom, auto exposure and motion blur.
- Night lighting from lanterns, windows and the boat, plus a flashlight that also works underwater.

**Audio**
- Positional audio from real CC0 field recordings: surf timed to each breaking wave, wind, birds, the boat
  engine, footsteps by surface, underwater ambience, whale song, and the rod and reel (casts, the bail,
  reeling, the drag, line snaps, splashes).

## Controls

| Key | Action |
|---|---|
| W A S D | Move |
| Mouse | Look (click to capture the mouse, Esc to release) |
| Shift | Sprint / boat boost |
| Space | Jump / swim up |
| C | Crouch / dive |
| E | Interact: board the boat, take or leave the helm, step ashore, trade with the fish buyer or the chandlery, and set or haul a lobster pot from the working boat |
| V | Boat camera at the helm (1st / 3rd person) |
| R | Take out / put away the fishing rod |
| Left mouse | Hold to wind up, release to cast · strike when a fish takes the bait · hold to reel |
| Right mouse | Reel an empty line in |
| I or Tab | Cooler / fish hold and the fish log |
| X | Drop or weigh the anchor, aboard a boat (an orange buoy and a minimap marker show where it lies) |
| J | Fish guide: what you have learned about each species |
| F | Free camera |
| L | Flashlight |
| T | Run or pause the day |
| M | Mute |
| N | Large map (north up) |
| H | Settings panel |
| P | Photo mode |
| F1 | All controls |
| Esc | Close a panel, release the mouse |

**Every one of these can be rebound**, and a controller can drive the whole game — see below.

### Controller

Plug in a gamepad and it works out of the box: the interface switches to controller glyphs the moment you
touch a button, the prompts and the controls sheet follow, and the menu walks with the d-pad. The default
layout:

| Input | Action |
|---|---|
| Left stick | Move (analogue: push it halfway to walk slowly) · throttle and rudder at the helm |
| Right stick | Look |
| RT | Cast, strike, reel · at the helm, set and haul pots |
| RB | Reel in an empty line |
| LT | Sprint, boat boost |
| LB | Take out / put away the rod |
| A | Interact: board, helm, step ashore, trade, set and haul traps on deck |
| B | Back, close, dismiss the catch card |
| X | Jump, swim up |
| Y | Crouch, dive |
| L3 / R3 | Boat camera · cooler and fish log |
| D-pad | Run or pause the day · flashlight · mute · photo mode |
| View / Menu | All controls · settings panel |
| Left stick + d-pad | Walk the panels and lists: up and down between rows, left and right to change a slider, A to pick, B to go back, LB / RB to change tab |

Rebinding lives in **Settings → Controls** (`H`, then the Controls tab): one row per action showing what is
bound on each device, with the deadzone, look sensitivity, invert-Y and rumble. Click a row and press the
key, mouse button or controller button you want; a × removes one input, the ↺ button puts a row back to its
default, and binding something another action already had takes it from that action (the row says so).
Plain Esc cancels a capture, Shift+Esc binds Escape itself, Del clears the row.

Bindings are saved on their own (`tidewater.controls.v1` in the browser), with everything from before the
Controls tab treated as "use the defaults", and the debug console can read the table (`window.__tw.app.bindings`).

### Fishing

Walk the deck of the boat while it drifts, or fish from the pier and the beach. Cast, wait for the bobber
to dip and strike when it's pulled under, then play the fish: keep the line tension in the green band,
ease off when it runs. Different water holds different fish (the shallows, the pier, the reef, the bay and
deep water offshore), and some bite best at dawn, dusk or night. Sell your catch to Joe at the fish stand
on the beach by the pier, and spend it at Marta's chandlery by the boathouse: stronger line, a faster reel,
a longer rod, a bigger fish hold, a larger fuel tank, a rebuilt engine, a fish finder and deck floodlights for
night fishing. The boat burns diesel at the helm; fill up at the chandlery. Progress is saved in the browser.

### Lobstering

Marta also sells a **trap licence** and wooden lobster pots. Aboard the working boat, **E** on deck (or the
cast button, **LMB** or **RT**, at the helm) puts a pot over the stern and hauls the nearest one back up, at
a crawl: the boat has to be under about 2 m/s, and **E** at the helm still just leaves it. The deck stack
shows how many pots are aboard, and each one visibly goes over the stern or comes up on the hauler. Timing is
by the clock: a pot dropped in the morning is
worth pulling after lunch, and one left overnight is full. What comes up is mostly spiny lobster on the
deeper ground, with the odd fish that wandered in, and the map marks every pot you have in the water.

Joe's prices move with the day: each species pays somewhere between three quarters of the standard rate and
a third above it, and every fish you are carrying shows today's rate with a mark against the usual one, so
it pays to hold a catch for a better morning. He lists the day's movers on his board.

### The day

Time runs by default: a full day takes about twenty minutes, and **T** pauses it. Fish feed at dawn and
dusk, tarpon after dark, the lamps and windows come on at night, and the sea state gets up and lies down on
its own — calm mornings, a sea breeze through the afternoon, and the occasional blow. The clock, the day
and the weather are saved with everything else.

The settings panel (H) exposes the sea state, dynamic weather and its pace, time of day, sun azimuth,
clouds, haze, post-processing and more — and a **Controls** tab with the controller options and a
rebindable row for every action.

## URL options

Add these to the URL, for example `?fly&noAudio`:

| Option | Effect |
|---|---|
| `fly` | Start in the free camera |
| `noAudio` | Disable sound |
| `noClouds` | Skip the volumetric clouds |
| `noHaze` | Skip the haze and sun shafts |
| `noCaustics` | Skip caustics |
| `noVeg` | Skip vegetation |
| `noSim` | Skip the swash (shallow-water) simulation |

## Running locally

```sh
npm install
npm run dev      # http://127.0.0.1:5189
npm run build    # static build in dist/
```

Every push to `main` deploys to GitHub Pages through `.github/workflows/deploy.yml`.

In the dev server the console has helpers for testing — `window.__tw` sets the wallet, moves the clock,
jumps the weather, lands a catch and drives the trap line. See [cheats.md](cheats.md).

## Project layout

| Folder | Contents |
|---|---|
| `src/game/` | The fishing game: rod, bites, the fight, catch card, cooler and log, the trap line, vendors and stalls, guide, minimap, HUD, and the console helpers (`Debug.js`) |
| `src/engine/` | The rendering engine: math, scene graph and geometry, GPU resources, WGSL shader composition, materials, lighting and shadows |
| `src/ocean/` | FFT ocean, water surface and material, shore waves, breakers, swash, wake, caustics, underwater lighting, sea conditions |
| `src/sky/` | Atmosphere, clouds, sky and environment |
| `src/world/` | Terrain, village, pier, reef, fish, vegetation, rocks, debris, wildlife, whale, the boats, the weather and the static model loader |
| `src/post/` | Post chain: AO, underwater composite, haze, TAAU, motion blur, bloom, lens flare, droplets |
| `src/materials/` | Shared lighting: shadow filtering, bounce light, contact shadows, local lights, LOD fades |
| `src/player/` | Walking, swimming, the boat and the free camera |
| `src/audio/` | The sample-based soundscape |
| `src/ui/` | Settings panel, loading screen and HUD |
| `tools/` | Scripts that fetch and convert the characters, stall props and fishing sounds |
| `test/` | Headless engine smoke test and game-logic and input tests (`npm test`), and the HUD / loader / Controls-tab dev pages |

## Credits and license

The code is released under the MIT license; see [LICENSE](LICENSE). Third-party assets (CC0 audio from
Freesound, CC0 scans from Poly Haven, MIT characters from Microsoft Rocketbox, OFL / Apache fonts) and
technique references are listed in [CREDITS.md](CREDITS.md).
