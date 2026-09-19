# Late Game Performance

A Timberborn 1.1 mod (built against **1.1.2.4**) that removes repeated CPU work in large colonies.
Version **0.2.0** is a preview: it has been tested against the game's assemblies but **not yet played in-game**.
Test on a copy of a save first.

## Installation

1. Close Timberborn. Extract the release ZIP into `Documents/Timberborn/Mods`. It contains one
   `LateGamePerformance` folder.
2. Requires the **Harmony** mod (2.4.1 or newer) from the Steam Workshop.
3. Launch Timberborn, enable **Late Game Performance**, and restart.
4. Look for `[LateGamePerformance]` lines in `Player.log`
   (`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\Player.log`).

Multiplayer: every peer must install the identical version with identical `(simulation)` settings.

## What it does

### Hauling job list cache (on by default)

Every time a hauler looks for work, the game asks every building with an inventory in the district for its
hauling jobs and their weights (which scans that building's inventories), sorts the whole list, and then tries
the jobs in order. That is repeated for every hauler decision, even when nothing changed in between.

This mod keeps each building's weighted jobs until something that feeds them changes (its stock, reservations,
allowed goods, enabled inventories, blocked state, emptying mark, obtain/supply setting, recipe, or haul
priority), and keeps the district's sorted list until any building in it changed. When a list is rebuilt it is
assembled in the same building order and sorted with the same comparison as the game, so haulers get the same
list the game would have produced.

As a safety net, everything cached is dropped every tick by default (`HaulCacheFlushEveryTicks = 1`). An input
the mod does not track, such as one added by another mod, can then be out of date only within a single tick.
Setting it to `0` keeps lists across ticks and relies on change tracking alone: faster, less conservative.

If anything throws inside the cache, it switches itself off for the session and the game's own code runs.
If a required game method is missing (for example after a game update), the feature does not enable at all.

### Parallel route map rebuild (on by default, new in 0.2.0)

Every building with an entrance keeps a route map: the road distance from its entrance to every road tile it
can reach. When any road changes, the game throws away every map containing a changed tile, which in a connected
district is nearly all of them, and rebuilds each one on the main thread the next time something asks for it.
In a large colony that is hundreds of rebuilds right after every finished path, stair or platform.

This mod rebuilds the maps that were in use and just got thrown away straight away, spread over worker threads
(up to 7), and the main thread waits for them before the tick continues.

- Road changes are only applied at one point in the tick (`NavigationSynchronizer.Tick`). The rebuild runs right
  after it, so nothing modifies the road network while workers read it.
- Each worker runs the game's own map generator on a private copy, so a rebuilt map has exactly the contents
  the game would have produced. The tests check this node for node against the installed game's code.
- Which maps are rebuilt depends only on game state, never on timing or thread count, so multiplayer peers stay
  in step. The thread count itself may differ between peers.
- Maps that were never used are not rebuilt, and changes that throw away fewer than `RouteMapsMinFields` maps
  are left to the game.

One behaviour difference from the unmodded game: maps are now filled before the first request instead of on it.
A few code paths use a map only "if it is already filled", so they can take the cached route where the unmodded
game would have searched again. That is why every multiplayer peer needs the same `RouteMaps` settings.

The trade-off is that the rebuild happens inside one tick instead of being spread over the following ones: less
total main-thread time, but concentrated. The stats line shows how long each rebuild took.

In the test harness, 420 maps on a 22,600-tile road network took about 1.4 s one by one and about 0.22 s on
7 workers.

### Garbage collector report (on by default)

Logs at startup whether incremental garbage collection is on. It is decided by `boot.config` before mods load,
so the mod cannot change it; when it is off, the log line says which line to add to `boot.config` to try it.

### Diagnostics (off by default)

`Diagnostics = true` times route map (road flow field) fills, including the ones the game still does on
demand, and need selection, which this mod does not change. It adds overhead to hot
code, so switch it off again after collecting numbers.

### Stats

Every `StatsEveryTicks` ticks (default 1000) one line is logged, for example:

```
[LateGamePerformance] Last 1000 ticks. HaulCache: 412 hauler list requests, 251 served from cache,
161 rebuilt in 96.3 ms (0.598 ms each); buildings recomputed 40211/61843
```

"ms each" is roughly what the game pays on every request without the mod, so requests served from cache times
that figure is the time saved.

A second line reports route map rebuilds:

```
[LateGamePerformance] Last 1000 ticks. RouteMaps: 6 parallel rebuilds of 3120 route maps in 410.2 ms
on 7 workers; 2 road changes left to the game (fewer than 16 maps)
```

## Settings

`version-1.1/LateGamePerformance.cfg`, plain `key = value`. Restart after editing.

| Key | Default | Meaning |
|---|---|---|
| `HaulCache` (simulation) | `true` | The hauling job list cache. |
| `HaulCacheFlushEveryTicks` (simulation) | `1` | Drop everything cached every N ticks. `0` = never. |
| `HaulCacheVerify` | `false` | Recompute the game's list on every request and compare; logs mismatches and uses the game's list. Slower than no mod. For testing. |
| `RouteMaps` (simulation) | `true` | Parallel route map rebuild after road changes. |
| `RouteMapsMinFields` (simulation) | `16` | Smaller changes are left to the game. |
| `RouteMapsWorkers` | `0` | Worker threads; `0` = automatic, up to 7. May differ between peers. |
| `GcReport` | `true` | Startup garbage collector report. |
| `Diagnostics` | `false` | Timers for route map rebuilds and need selection. |
| `StatsEveryTicks` | `1000` | Stats line interval. `0` = never. |

## Suggested first test

1. Copy a save. Set `HaulCacheVerify = true`, play a few in-game days single-player, and check `Player.log` for
   `verify mismatches 0` in the stats lines. Any mismatch line is a bug worth reporting.
2. Set `HaulCacheVerify = false` and compare how the game feels at speed 3, and read the stats lines.
3. Optionally set `Diagnostics = true` for one session and keep the `Diagnostics:` lines.

## Not in this version, and why

- **Pruning need selection by straight-line distance.** Ziplines have their own cost per distance, so a straight
  line is not a lower bound on route cost and pruning with it would change what beavers choose.
- **Array-backed route maps.** The game's flow field objects cannot be given extra fields from a mod, and looking
  up side storage on every access costs about what the dictionary lookup it replaces does. 0.2.0 rebuilds the
  maps in parallel instead.
- **Ticking beavers on several threads.** A beaver's tick decides and acts in one step (finding stock and
  reserving it), touches Unity objects that are main-thread only, and multiplayer needs identical results on
  every machine. Only self-contained, read-only calculations can move to other threads.

## Build and test

Requires .NET SDK 8, a local Timberborn installation, and the Harmony Workshop mod. No NuGet packages and no
redistributed game DLLs.

```powershell
./build.ps1                 # build and package into dist/
./build.ps1 -Install        # also copy into Documents\Timberborn\Mods (game must be closed)
dotnet run --project tests -c Release
```

The tests load the installed game's assemblies and check that every patch target, private field and property
the mod relies on still exists with a compatible signature, plus settings parsing. They also build a road
network with the game's own navigation classes and check that parallel route map rebuilds are identical to the
game's one-by-one rebuilds, that only thrown-away, in-use maps are rebuilt, and that a failing worker is
contained. They do not run the game.

`tools/benchmark-timberborn.ps1` runs the game's built-in benchmark on a save with per-component tick timings
(`-metrics`), for before/after comparisons.

## Uninstall

Disable the mod and restart. It stores nothing in saves.

## License

MIT.
