# Tidewater cheats

Console helpers for poking a running game. Open the dev server
(`http://127.0.0.1:5189`), open the browser console, and use `window.__tw`.

Everything goes through the game's own code paths, so the HUD, the save and the world
react exactly as they would in play: setting money saves it, landing a catch logs it,
hauling a pot runs the same roll the player gets. Nothing here needs a reload.

The helpers are installed by `App.init` from `src/game/Debug.js`.

## The wallet

| command | what it does |
|---|---|
| `__tw.money()` | what you have |
| `__tw.money( 5000 )` | set it (saves, and the purse updates) |
| `__tw.add( 250 )` | add to it; negative takes away, and it floors at 0 |

```js
__tw.money( 25000 )     // flush, for testing the shop
__tw.add( - 20000 )     // back to where you were
```

## The world clock

| command | what it does |
|---|---|
| `__tw.day()` / `__tw.day( 4 )` | the day; setting it re-rolls Joe's prices for that day |
| `__tw.hour()` / `__tw.hour( 22 )` | the clock, 0–24 (values outside wrap). Time then keeps running from there |

```js
__tw.hour( 5.5 )        // dawn: the bite tables are at their dawn window
__tw.hour( 22 )         // night: tarpon, lanterns, and a reason for the deck floodlights
__tw.day( 8 )           // a week in: prices have moved around
```

## Sea state

| command | what it does |
|---|---|
| `__tw.weather()` | the condition you are on |
| `__tw.weather( 'Storm' )` | jump to a rung: `Calm`, `Breezy`, `Choppy`, `Storm` |

Jumping the weather switches dynamic weather back on and rebuilds the wave spectrum, so
the sea and the sky change within a frame or two. An unknown name just returns the ladder.

```js
__tw.weather( 'Storm' ) // test the boat, the spray and the ride home
__tw.weather( 'Calm' )  // and back again
```

## Fishing

| command | what it does |
|---|---|
| `__tw.fish( species, kg? )` | land a catch: cooler, log, records and the catch card |
| `__tw.fish( 'nope' )` | an unknown species hands back the list of ids |

`species` is an id from the table below, not the display name. `kg` defaults to the middle
of the species' weight range; the roll is the fish's own, so lengths and values come out
sensible.

```js
__tw.fish( 'lobster' )          // a mid-sized one
__tw.fish( 'redSnapper', 8.5 )  // a trophy: a record, and Joe pays more per kg
__tw.fish( 'tarpon', 40 )       // bigger than the starting cooler, so it is logged and let go
```

## The trap line

| command | what it does |
|---|---|
| `__tw.traps()` | `{ aboard, set }` — pots on the boat and pots in the water |
| `__tw.traps( 6 )` | set the stock aboard (six is the licence maximum) |
| `__tw.setTrap( x?, z? )` | put a pot over the side; the boat's position by default, or world coordinates |
| `__tw.haul( id? )` | haul a pot: the nearest to the boat, to you when you are ashore, or a given id |
| `__tw.soak( hours )` | age every pot in the water by that many game hours ("come back later") |
| `__tw.clearTraps()` | pull the whole gear out of the water and back aboard |

Setting still obeys the game's rules: a pot aboard, water between 2 m and 45 m, and no
other pot within 8 m. Hauling returns what the soak and the ground earned, and puts a
catch card up. `soak` moves the pots backwards in time, which is what makes their soak
longer — it does not move the clock.

```js
__tw.state.buy( 'trapLicence' )   // the $450 licence, without the walk to Marta
__tw.traps( 3 )                   // three pots aboard
__tw.setTrap()                    // one over the side where the boat is
__tw.setTrap( - 30, 160 )         // or placed at coordinates you name (they must be 8 m apart)
__tw.soak( 9 )                    // let them fish for nine hours
__tw.haul()                       // see what is in the nearest one
__tw.clearTraps()                 // tidy up
```

## Recipes

```js
// A wealthy angler, ready to test the shop
__tw.money( 50000 )

// Joe's prices on a given day, and what he pays for the cooler
__tw.day( 6 ); __tw.fish( 'grouper', 6 ); __tw.state.holdValue
__tw.game.sellAll()                        // same as selling at the stand

// A full hold of mixed fish, then sell it
[ 'grunt', 'yellowtail', 'grouper', 'lobster', 'mahi' ].forEach( ( id ) => __tw.fish( id ) )

// The world as it was: wallet, cooler, log, gear, fuel, day, traps
__tw.state.reset()
```

## Species ids

`__tw.fish` takes these ids. `$ /kg` is the standard rate, before the day's market
multiplier.

| id | name | $ /kg | kg |
|---|---|---|---|
| `silverside` | Hardhead silverside | 3 | 0.02–0.08 |
| `mullet` | Striped mullet | 5 | 0.4–2.2 |
| `needlefish` | Houndfish | 4 | 0.5–2.5 |
| `sergeant` | Sergeant major | 6 | 0.1–0.35 |
| `grunt` | Bluestriped grunt | 7 | 0.3–1.2 |
| `yellowtail` | Yellowtail snapper | 12 | 0.4–1.6 |
| `chromis` | Blue chromis | 4 | 0.03–0.1 |
| `tang` | Blue tang | 8 | 0.2–0.6 |
| `wrasse` | Hogfish | 16 | 0.5–4 |
| `parrot` | Stoplight parrotfish | 9 | 0.8–4 |
| `angel` | Queen angelfish | 14 | 0.4–1.6 |
| `jack` | Crevalle jack | 6 | 1–9 |
| `barracuda` | Great barracuda | 5 | 2–16 |
| `grouper` | Nassau grouper | 15 | 3–14 |
| `redSnapper` | Red snapper | 18 | 1.5–9 |
| `tuna` | Blackfin tuna | 16 | 3–14 |
| `mahi` | Mahi-mahi | 14 | 4–18 |
| `tarpon` | Tarpon | 4 | 10–45 |
| `lobster` | Caribbean spiny lobster | 30 | 0.4–2.4 |

`lobster` is trap-only: it has no rod habitat, so a cast can never bring one up.

## The rest of the debug surface

Already in the game, and useful next to the above:

| command | what it does |
|---|---|
| `__tw.help()` | print the command list in the console |
| `__tw.state`, `__tw.game`, `__tw.app`, `__tw.trapLine` | the objects themselves, for anything not covered here |
| `window.__app` | the whole app: `__app.terrainData`, `__app.player`, `__app.lobsterCtl`, `__app.pelagicCtl`… |
| `window.__views`, `window.__view( 'pier' )` | the named review cameras (`__pose()` prints the current one) |
| `window.__ui` | the interface itself, if you want to drive a panel |

`state`, `game` and `app` reach most of what you would want without a helper:
`__tw.state.buy( 'engine' )`, `__tw.game.refuel()`, `__tw.state.inventory`, and so on.

## Resetting

`__tw.state.reset()` puts the wallet, cooler, log, gear, fuel, the day, the trap line and
Joe's prices back to the start, and writes that to the save. The running world keeps its
clock and its weather until you reload, so reset then reload for a completely fresh
island — or drop the save entirely:

```js
__tw.state.reset()                                // then reload the page
localStorage.removeItem( 'tidewater.save.v1' )    // or forget the save altogether
```
