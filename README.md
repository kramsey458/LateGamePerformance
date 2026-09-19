# Late Game Performance

A Timberborn 1.1 mod (built against **1.1.2.4**) that removes repeated CPU work in large colonies.
Version **0.4.5** is a preview. 0.4.2 has been played in multiplayer; the save timing (0.4.4) and the change to
which route maps are built (0.4.5) have been tested against the game's assemblies but **not yet played
in-game**. Do not use 0.4.3: it crashes the game while loading.
Test on a copy of a save first.

## Installation

1. Close Timberborn. Extract the release ZIP into `Documents/Timberborn/Mods`. It contains one
   `LateGamePerformance` folder.
2. Requires the **Harmony** (2.4.1 or newer) and **Mod Settings** mods from the Steam Workshop.
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

At the end of every navigation tick, this mod builds every cached route map that is not built, spread over
worker threads (up to 7).

- Road changes are only applied at one point in the tick (`NavigationSynchronizer.Tick`). The rebuild runs right
  after it, so nothing modifies the road network while workers read it.
- Each worker runs the game's own map generator on a private copy, so a rebuilt map has exactly the contents
  the game would have produced. The tests check this node for node against the installed game's code.
- **Which maps:** a map is cached exactly while its building is finished, so the set of cached maps is part of
  the simulation and identical on every multiplayer peer. After each navigation tick all of them that can be
  built are built, on every peer. How the work is split (thread count, background or not, the small-batch
  limit) changes who builds a map and when, never which maps end up built, so those settings may differ
  between peers.
- Fewer than `RouteMapsMinFields` unbuilt maps (a new building or two) are built directly on the main thread,
  because starting workers would cost more. They are still built straight away, not left for later.
- **Changed in 0.4.5.** Earlier versions rebuilt "the maps that were filled before the road change". That was
  wrong for multiplayer: a map also gets filled when a player's range overlay asks for a route, on that
  player's computer only, so two peers could rebuild different sets and then disagree about which maps are
  filled. Building everything that is cached removes the dependence on what anyone looked at. It does more work
  (never-used maps are built too), but on worker threads in the background.

One behaviour difference from the unmodded game: maps are now filled before the first request instead of on it.
A few code paths use a map only "if it is already filled", so they can take the cached route where the unmodded
game would have searched again. That is why every multiplayer peer needs `RouteMaps` on or off alike.

In the test harness, 420 maps on a 22,600-tile road network took about 1.8 s one by one and about 0.23 s on
7 workers.

#### Background rebuild (on by default, new in 0.3.0)

In 0.2.0 the main thread waited for the whole batch: one pause per road change (about 43 ms for 360 maps in the
colony it was measured in). Now the workers rebuild in the background and the tick carries on. The main thread
only ever waits for the single map it is about to use:

- already rebuilt: no wait
- not started yet: the main thread builds that one map itself, right there
- a worker is on it: it waits for that one map

The game reaches a cached route map only through two methods on `RoadFlowFieldCache`, and both are gated, so
whenever the game looks at a map from the batch it is complete. That is exactly what it would see if the whole
batch had been waited for, so results are identical to 0.2.0: timing decides who builds a map and when, never
what the game observes. Anything that changes what the workers read (the road graph, the district maps) first
finishes the rebuild, as does the start of the next navigation tick. If any gate cannot be installed, the mod
falls back to waiting for the whole batch.

What to expect: the total main-thread time does not drop much, because a map the game needs right now has to be
built by someone. What changes is its shape: many short pauses of at most about one map build, spread over the
frames of that tick, instead of one long freeze. In the harness (a heavier road network than a real colony) the
longest single pause went from about 230 ms to about 16 ms. `RouteMapsBackground = false` restores 0.2.0.

### Timing line (on by default, new in 0.4.0)

One hook on the game's per-frame simulation call shows where the main thread's time goes:

```
[LateGamePerformance] Last 1000 ticks. Timing: 1000 ticks in 176.9 s unpaused = 5.7 ticks/s (game time scale
3.4, which is the speed buttons after the game's large-colony throttle, asks for 5.7); wall clock 214.0 s, of
which paused 11.2 s and not counted 25.9 s (loading, saving, window in the background); 20637 frames = 117 fps;
simulation 29.1 ms per tick = 16% of the main thread, everything else 7.2 ms per frame = 84%; longest frame
105 ms, longest simulation slice 98 ms; 3 garbage collection(s): the 3 frame(s) containing one took 240 ms in
total, longest 105 ms (an average frame is 8.6 ms); 1 save(s): 812 ms, in frame(s) of 830 ms with
1 garbage collection(s), all left out of the other figures
```

- **ticks/s against what the time scale asks for** shows whether the game is keeping up at all. The time scale is
  not the speed button: above speed 1 the game itself slows large colonies down (button speed 7 came out as 3.4
  in a colony of about 350 beavers), and the figure here is the speed after that.
- **saves** (new in 0.4.4, needs `SaveTiming`) are reported on their own. A save freezes the game for most of a
  second and usually contains a garbage collection, so before 0.4.4 it showed up as the longest frame and as a
  very long collection. The frame a save ran in is now left out of every other figure; its time is part of
  "not counted".
- **simulation** is time inside the game's tick call; **everything else** is the rest of each frame (rendering,
  animation, UI, other mods' per-frame work). Paused frames are left out.
- **wall clock, paused, not counted** make pauses visible that nothing logs, such as a multiplayer mod setting the
  speed to zero while it waits.
- **garbage collections** shows how long the frames containing a collection were. With incremental collection off
  (see the startup `GC:` line) a collection stops every thread until it is done, so these are hitches.
- If "everything else" is a large share, a frame rate cap gives the simulation more of each second, because the
  per-frame cost is paid less often.

### Save timing line (on by default, new in 0.4.4)

One line per save (autosave, manual save, save on exit) shows which stage of it costs the time:

```
[LateGamePerformance] Save: 812 ms total = finishing the tick 14 ms + snapshot 190 ms + world JSON and
compression 520 ms + thumbnail 60 ms + everything else 28 ms
```

- **finishing the tick**: the game completes the current tick before it saves.
- **snapshot**: copying the state of every beaver, building and system into a save structure. It reads live game
  objects, so it can only happen on the main thread.
- **world JSON and compression**: turning that structure into `world.json` and compressing it into the save.
- **thumbnail**: rendering and encoding the save's picture.
- **everything else**: the small entries, writing the file, and whatever runs when the save completes.

This is measurement only. The hooks read a clock around the game's own methods, run only during a save, and do
not change how or when the game saves. The game's own `Saved game in 0.80s` line starts its clock after the tick
is finished, so it can be a little lower than the total here. A line starting `Save to a stream` is a save that
is not written to a file, which a multiplayer mod uses for a joining player; `Save (writing only...)` is one
started in a way the mod does not know, so only the writing part is covered. The numbers are there to decide
whether part of a save is worth moving off the main thread.

### Per-component timings (menu setting, new in 0.4.x)

The game can time every tickable component, every once-per-tick system and every root behaviour, but only when
launched with a `-metrics` option, and it only writes the result at the end of a benchmark run.

Tick **Record per-component timings** in this mod's settings page (**Mods > Late Game Performance**, from the main
menu or in-game) and **load a save**. The mod switches the game's timers on for that session and writes the report
during normal play, multiplayer included, to `Documents\Timberborn\LateGamePerformance` every `MetricsEveryTicks`
ticks (default 3000), then resets the timers so each file covers one interval. Each group lists its total seconds
and every entry's share of it.

- It applies from the **next save load**, not immediately: every building and beaver decides whether to time
  itself at the moment it is created.
- The game's timers add two stopwatch calls around every component tick, so expect it to run a little slower
  while this is on. Untick it after a profiling session.
- It does not affect the simulation, so multiplayer peers may have it set differently.
- Launching the game with `-metrics` still works and has the same effect.

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
[LateGamePerformance] Last 1000 ticks. RouteMaps: 4 background rebuilds of 1435 route maps; main thread spent
61.0 ms on them, longest single pause 1.9 ms (built 70 itself, waited for 12), workers were busy 180.4 ms
alongside the game; 6 more built directly on the main thread in batches of fewer than 4
```

"longest single pause" is the figure to watch for hitching.

## Settings

`version-1.1/LateGamePerformance.cfg`, plain `key = value`. Restart after editing.

| Key | Default | Meaning |
|---|---|---|
| `HaulCache` (simulation) | `true` | The hauling job list cache. |
| `HaulCacheFlushEveryTicks` (simulation) | `1` | Drop everything cached every N ticks. `0` = never. |
| `HaulCacheVerify` | `false` | Recompute the game's list on every request and compare; logs mismatches and uses the game's list. Slower than no mod. For testing. |
| `RouteMaps` (simulation) | `true` | Build every cached route map on worker threads after each navigation tick. |
| `RouteMapsBackground` | `true` | Rebuild in the background; `false` = main thread waits for the whole batch. May differ between peers. |
| `RouteMapsMinFields` | `4` | Fewer unbuilt maps than this are built directly on the main thread instead of on workers. May differ between peers. |
| `RouteMapsWorkers` | `0` | Worker threads; `0` = automatic, up to 7. May differ between peers. |
| `Timing` | `true` | The timing stats line. |
| `SaveTiming` | `true` | One line per save with the time of each stage; also lets the timing line report saves separately. Measurement only. |
| `MetricsEveryTicks` | `3000` | While per-component timings are on: write them every N ticks. `0` = never (disables the menu setting too). |
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

Requires .NET SDK 8, a local Timberborn installation, and the Harmony and Mod Settings Workshop mods. No NuGet packages and no
redistributed game DLLs.

```powershell
./build.ps1                 # build and package into dist/
./build.ps1 -Install        # also copy into Documents\Timberborn\Mods (game must be closed)
dotnet run --project tests -c Release
```

The tests load the installed game's assemblies and check that every patch target, private field and property
the mod relies on still exists with a compatible signature, plus settings parsing. A patch target containing
an exception filter (`catch ... when`) is refused: Harmony cannot patch those under the game's Mono runtime, and
the failed attempt crashes the game later (0.4.3 did this with `GameSaver.Save`). They also build a road
network with the game's own navigation classes and check that parallel route map rebuilds are identical to the
game's one-by-one rebuilds, that only thrown-away, in-use maps are rebuilt, and that a failing worker is
contained. The background rebuild is run for 25 rounds with maps requested in shuffled order while workers
are busy: every map must be complete and correct at the moment it is asked for. They do not run the game.

`tools/benchmark-timberborn.ps1` runs the game's built-in benchmark on a save with per-component tick timings
(`-metrics`), for before/after comparisons.

## Uninstall

Disable the mod and restart. It stores nothing in saves.

## License

MIT.
