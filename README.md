# Late Game Performance

A Timberborn 1.1 mod (built against **1.1.2.4**) that removes repeated CPU work in large colonies.
Version **0.1.0** is a preview: it has been checked against the game's assemblies but **not yet played in-game**.
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

### Garbage collector report (on by default)

Logs at startup whether incremental garbage collection is on. It is decided by `boot.config` before mods load,
so the mod cannot change it; when it is off, the log line says which line to add to `boot.config` to try it.

### Diagnostics (off by default)

`Diagnostics = true` times two other suspects without changing them, to decide what the next version should
target: route map (road flow field) rebuilds after road changes, and need selection. It adds overhead to hot
code, so switch it off again after collecting numbers.

### Stats

Every `StatsEveryTicks` ticks (default 1000) one line is logged, for example:

```
[LateGamePerformance] Last 1000 ticks. HaulCache: 412 hauler list requests, 251 served from cache,
161 rebuilt in 96.3 ms (0.598 ms each); buildings recomputed 40211/61843
```

"ms each" is roughly what the game pays on every request without the mod, so requests served from cache times
that figure is the time saved.

## Settings

`version-1.1/LateGamePerformance.cfg`, plain `key = value`. Restart after editing.

| Key | Default | Meaning |
|---|---|---|
| `HaulCache` (simulation) | `true` | The hauling job list cache. |
| `HaulCacheFlushEveryTicks` (simulation) | `1` | Drop everything cached every N ticks. `0` = never. |
| `HaulCacheVerify` | `false` | Recompute the game's list on every request and compare; logs mismatches and uses the game's list. Slower than no mod. For testing. |
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
  up side storage on every access costs about what the dictionary lookup it replaces does. The diagnostics above
  measure whether rebuilds matter enough to justify a deeper change.

## Build and test

Requires .NET SDK 8, a local Timberborn installation, and the Harmony Workshop mod. No NuGet packages and no
redistributed game DLLs.

```powershell
./build.ps1                 # build and package into dist/
./build.ps1 -Install        # also copy into Documents\Timberborn\Mods (game must be closed)
dotnet run --project tests -c Release
```

The tests load the installed game's assemblies and check that every patch target, private field and property
the mod relies on still exists with a compatible signature, plus settings parsing. They do not run the game.

`tools/benchmark-timberborn.ps1` runs the game's built-in benchmark on a save with per-component tick timings
(`-metrics`), for before/after comparisons.

## Uninstall

Disable the mod and restart. It stores nothing in saves.

## License

MIT.
