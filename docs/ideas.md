# Ideas for making Tidewater better

A brainstorm, not a plan: nothing here is committed to. It was written against the README and what the game
did at the time; check the code before building on any of it.

## What I'd pick first

1. **A goal ladder with a fish-log bounty.** Cheap, and it gives the loop a direction.
2. **Weather with stakes for the trap line.** It uses the realistic boat seakeeping and the trap work.
3. **A boat and hauler upgrade at Marta's.** Small, and it ties the boat to the economy.
4. **Village NPCs.** The biggest jump in atmosphere for the effort.

The open question for choosing between them: should the game feel more cozy and exploratory, or more of a
challenge?

## The biggest gap: no reason to keep playing

The loop is cast, sell, buy gear. There is no goal at the end of it.

- **A goal ladder.** Pay off Marta's loan, buy the Pelagic, then a bigger boat. Or a "season" that scores
  your total haul. A few named milestones, each with a small ceremony, would give the days some direction.
- **Fish-log completion.** There are 19 species with records already, so a "complete the log" reward is
  cheap. Joe or Marta could pay a bounty for a species you haven't caught yet.
- **Trophy fish.** A rare, huge individual of some species, found only in certain conditions (dusk on the
  reef, a particular tide), mounted on a wall in the village once caught.

## Make the weather matter

The README says nothing in the weather can hurt you. The lobster boat's seakeeping is now realistic, so the
sea can be a real challenge.

- **Storms with stakes.** Heavy seas could drag or lose pots, burn more fuel, or make landings at the pier
  harder. That needs a reason to watch the forecast: a barometer in the wheelhouse, or a radio report from
  Marta.
- **Trap line risk and reward.** Pots lost in a blow, so hauling early becomes a decision. A pot that
  soaked through a storm could also fish better.
- **Fog.** Cheap to add, and it makes the helm and the minimap more useful.

## The helm and the boat

- **Low-speed steering.** It is sluggish (about 2.8 boat lengths of turning radius at 0.2 throttle). A
  speed-dependent turn model would fix it without spoiling cruise turning. A single constant can't.
- **Docking as a skill.** Mooring lines and fenders.
- **Boat upgrades at Marta's.** Engine, tank, and a hauler upgrade that raises how many pots you can carry
  (the deck stack already shows how many are aboard).
- **A radar or chartplotter.** A screen in the wheelhouse that reuses the large map (N).
- **Tighter haul reach.** The reach is 12 m and the speed limit is `TRAP_MAX_SPEED`; both are left loose
  until playtested.

## Lobstering depth

- **Bait.** Different baits attract different catches and change soak times.
- **Pot placement matters.** Rocky versus sandy bottom, depth bands, and spacing, so your own pots don't
  compete with each other.
- **Trap-line management.** Name the pots, see their status on the map, haul them along a route.
- **Hauling polish.** Sound and animation for the hauler and line coming in, and the catch spilling out
  onto the deck.

## World and people

- **Village NPCs.** Joe and Marta are the only people. A fisherman on the pier, kids on the beach, someone
  leaving the boathouse would make the village feel alive.
- **Reasons to explore.** Wrecks to swim to, a hidden cove, a lighthouse, or a second island with different
  fish and a different market.
- **Night.** Squid fishing under the deck floodlights, or bioluminescence in the wake.

## Feel and polish

- **Fishing fight variety.** Species fight differently: a tarpon jumps, a grouper dives for cover.
- **Controller and accessibility.** The pad has no button for the large map. A colourblind-safe tension
  band, and remappable everything.
- **Onboarding.** The first load can take a minute or more while shaders compile. A real loading screen
  with tips would help, especially for someone trying the game for the first time.
- **Photo mode extras.** A catch snapshot, or a trophy shot saved to the log.

## Technical health

- **Automated visual checks.** All the tests so far are headless logic. A small screenshot regression
  suite would catch visual breakage such as the loose pier braces.
- **The water-query slot budget.** Done: the table was raised from 64 to 256 slots, with about 62 in use
  (see `boat-query-slots.md`). Past a few hundred, distant things should get a cheaper way to read water
  height rather than a slot each.
