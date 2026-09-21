# Late Game Performance: technical reference

The detailed description of each feature, the settings file, the log lines and the build. For an overview, see the
[README](README.md) or the [website](https://timbermods.github.io/LateGamePerformance/).

## What it does

### Hauling job list cache (on by default)

Every time a hauler looks for work, the game asks every building with an inventory in the district for its
hauling jobs and their weights (which scans that building's inventories), sorts the whole list, and then tries
the jobs in order. That is repeated for every hauler decision, even when nothing changed in between.

This mod keeps each building's weighted jobs until something that feeds them changes (its stock, reservations,
allowed goods, enabled inventories, blocked state, emptying mark, obtain/supply setting, recipe, or haul
priority). The district's list is assembled from those on every request, in the same building order and sorted
with the same comparison as the game, so haulers get the same list the game would have produced.

Up to 0.4.11 the district's sorted list was kept as well, until any building in it changed. In every session
measured, on two computers, it was never served from cache once: a hauler that takes a job reserves stock, which
changes a building, so the next request always found the list out of date. It was removed in 0.4.12. What does
get reused is the per-building part, about a third of the time.

As a safety net, everything cached is dropped every tick. An input
the mod does not track, such as one added by another mod, can then be out of date only within a single tick.
(Up to 0.4.8 that interval was a setting; it is fixed now, see Settings.)

If anything throws inside the cache, it switches itself off for the session and the game's own code runs.
If a required game method is missing (for example after a game update), the feature does not enable at all.

### Tree and plant search (on by default, new in 0.4.8)

Every time a lumberjack looks for a tree (and a gatherer or a farmer for a plant), the game goes through every
candidate, and for each one first looks up the path distance from the building, and only then asks whether the
plant has anything to take. For lumberjacks the candidates are every unreserved marked tree on the map. In a
late game colony most of them are still growing, so nearly all of those lookups are thrown away. In the colony
this mod is measured in, it was the largest single item in the simulation: 2.9 ms per tick, 16% of all tick
time, on a fast computer.

The distance of a plant with nothing to take matters for exactly one thing: whether it can be reached, and only
until the search has found something reachable that is grown or alive. After that it cannot change the answer,
and the mod leaves its lookup out. Plants that do have something to take are looked up exactly as before, in the
same order. The answer is the game's answer, not an approximation: the tests run the rule against a model of the
game's search on 4000 random forests (unreachable, dead and destroyed plants and ties included) and require the
same result every time. In a forest of 2000 marked trees with 50 grown, 51 lookups are left of 2000.

**Full buildings (new in 0.4.11).** A session on 0.4.9 showed the search still cost 4.3 ms per tick, with only 7% of
the lookups left out: in that colony nearly every marked tree is grown (6208 of 6263 in the save), so the rule above
had little to skip. The reason the search ran about 5 times per tick was in the save too: all 20 lumberjack flags
held 20 logs, which is full, so 18 lumberjacks asked for work again on every decision and were told "nothing to do"
after the game had measured the distance to about 1300 trees. The game's last step takes the closest plant of each
good and accepts the first whose carry amount is above zero; that amount is the smallest of what the worker can
lift, what the plant yields and the room left in the building, and the first two are at least 1 for any grown
plant. So it is zero exactly when the building has no room for that good, whichever plant it is, and then the
distance to plants of that good cannot change the answer. From 0.4.11 those lookups are left out too (the mod asks
the game's own `CarryAmountCalculator`, once per good per search). A full flag among 1300 grown trees needs one
lookup. The tests check the whole result, not only the candidates, against a model of the game's search on a
further 4000 random forests with full and part-full buildings. The stats line now also says how searches ended:
`outcomes: 12 found work, 5188 found nothing the building has room for or the worker can take, 0 found nothing
in range`.

- The first candidate of a search is always looked up, because that lookup is also what refills the building's
  terrain route map after a terrain change, and that has to happen on the same tick as without the mod.
- `YielderSearchVerify = true` runs the game's own search as well, compares, logs any difference and uses the
  game's result. It is slower than no mod and only for testing.
- If anything throws, the feature switches itself off for the session and the game's own code runs.
- It is always on. Up to 0.4.8 it could be switched off in the settings file; see Settings for why not any more.

### Plant water check on worker threads (always on, new in 0.4.12)

Every tick the game asks, for every plant and every other object that cares about flooding (about 8000 in the
colony this is measured in), how high the water stands at its tile, one after another on the main thread
(`WaterObjectService.Tick`): 1.3 ms per tick, 8% of all tick time. Almost none of them change from one tick to
the next.

The question is a pure read: the object's fixed tile, looked up in the water map the game keeps for readers on
other threads, which is only rewritten in its own tick and never during this one. So the reads are spread over
worker threads, and then the main thread goes through the results in the game's own order and, for each object
whose level changed, does exactly what the game does: stores the new level and raises the change event. Those
handlers (a plant starting to drown, a building flooding) run on the main thread, in the same order, with the
same values as without the mod.

Reading first gives the same values because a handler cannot change the water map or an object's tile, and it
cannot add or remove an object from the list: the game walks that list with `foreach` and would throw if one
did. The result does not depend on the number of threads, so it is the same on every computer.

- The tests build two identical sets of 6000 of the game's real `WaterObject`s over a stand-in water map that
  moves between ticks, run one through the game's own loop and one through the mod, and require the same stored
  levels and the same events in the same order with the same values, for six ticks (4316 level changes).
- Below 512 objects the game's own loop runs; starting workers would cost more than it saves.
- `PlantWaterVerify = true` reads everything again on the main thread and compares. For testing.
- If anything throws (a handler included), the feature switches itself off and hands that tick to the game's
  own loop. Objects already updated compare equal there and are passed over, so nothing is applied twice.

### Terrain route maps on worker threads (always on, new in 0.4.12)

Every building that works the land around it (lumberjack flag, gatherer, farmhouse, forester...) keeps a second
route map next to its road one: the walking distance over open ground from its entrance to every tile within 20
steps. Every search for a tree or a plant reads it. When the ground changes anywhere inside one, the game throws
that map away and rebuilds it on the main thread the next time its building searches, one building at a time.

At the end of every navigation tick the mod builds every terrain map that is cached and not built, with the
game's own generator, one private instance per worker thread, and the main thread waits for the batch. It is a
few tens of maps of at most a few thousand tiles (in the test harness 90 maps take 32 ms one by one and 7 ms on
7 workers), so waiting costs little and needs none of the gates the road maps' background rebuild has. If the
stats line ever shows the wait matter, the same background scheme can be put under it. **How often the ground
changes in a real colony, and so how much this saves, has not been measured; the stats line is there to say.**

- Ground changes are applied inside the navigation tick, and the main thread is inside the mod's call until
  every worker is done, so nothing changes the terrain graph while it is read. Each map is written by exactly
  one thread.
- A map depends only on the terrain graph, its start tile and the range. The range is the game's own, read from
  the game, and it is the only range the game ever builds these maps with.
- Which maps are built is simulation state: a terrain map is cached exactly while its building is finished, and
  unlike road maps nothing a player looks at builds one (the range overlay uses a map of its own). Every peer
  ends every navigation tick with the same maps built, whatever the thread count.
- The tests build a terrain graph with the game's `TerrainNavMeshGraph`, fill 90 maps one by one and in parallel
  and require them identical node for node and in node order, then drive the hook against a real
  `TerrainFlowFieldCache`: everything cached gets built, a map whose start is off the graph is left alone, a
  second tick builds nothing, and after a ground change exactly the cleared maps are rebuilt.

The same behaviour difference as the road maps: maps are built before the first request instead of on it, so the
few code paths that use a terrain map only "if it is already filled" find it filled. Every player on the same
version gets the same.

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
game would have searched again. That is why route maps are not a setting: every player on the same version runs them alike.

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
- **everything else, split** (new in 0.4.8): `everything else 30.0 ms per frame = 60% (per-frame systems of the
  game and mods 6.0 ms, their late-update systems 9.0 ms, the rest 15.0 ms: rendering, animation and Unity
  itself)`. The game runs its own per-frame systems, and those that mods register, from two calls; both are
  timed, and what remains is Unity. In a multiplayer session one computer spent 40 ms per tick in "everything
  else", more than in the simulation, and this is what says in which part.
- **simulation** is time inside the game's tick call; **everything else** is the rest of each frame (rendering,
  animation, UI, other mods' per-frame work). Paused frames are left out.
- **wall clock, paused, not counted** make pauses visible that nothing logs, such as a multiplayer mod setting the
  speed to zero while it waits.
- **garbage collections** shows how long the frames containing a collection were. With incremental collection off
  (see the startup `GC:` line) a collection stops every thread until it is done, so these are hitches.
- **managed memory** (new in 0.4.6), at the end of the line: `managed memory 1664-2687 MB in use, allocating
  5.3 MB/s, 1.0 collection(s) per minute freeing about 270 MB each`. These two numbers explain collections: every
  collection marks everything still in use again, so its length follows the amount in use, and how often one
  happens follows the allocation rate. Growth between two frames counts as allocation and a drop as freed, so
  the rate is a lower bound: memory freed and allocated within the same frame cancels out.
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

Set `RecordTimings = true` in `LateGamePerformance.cfg` and restart the game (up to 0.4.9 this was a box on the
settings page; it is a profiling tool, so from 0.4.10 it lives in the file). The mod switches the game's timers on for that session and writes the report
during normal play, multiplayer included, to `Documents\Timberborn\LateGamePerformance` every `MetricsEveryTicks`
ticks (default 3000), then resets the timers so each file covers one interval. Each group lists its total seconds
and every entry's share of it.

- It needs a **restart of the game** after editing the file: every building and beaver decides whether to time
  itself at the moment it is created, and the file is read once at startup.
- The game's timers add two stopwatch calls around every component tick, so expect it to run a little slower
  while this is on. Set it back to `false` after a profiling session.
- It does not affect the simulation, so multiplayer peers may have it set differently.
- Launching the game with `-metrics` still works and has the same effect.

### Garbage collector report (on by default)

Logs at startup whether incremental garbage collection is on, and when it is off, how to turn it on.

### Incremental garbage collection (menu setting, new in 0.4.6)

The game cleans up memory with every thread stopped, and each time it goes over everything that is still in
use. In a late game colony that is 1.6 GB and more. Measured in one 35 minute multiplayer session, same save,
same mods: on the computer without incremental collection, 39 collections with a median of **675 ms** each,
about 30 seconds frozen in total, each followed by a burst of catching up; on the computer with it, one
collection frame over 50 ms outside of saves. Incremental collection does the same work a few milliseconds at a
time across many frames.

Unity reads that choice from `boot.config` before any mod loads, so a mod cannot switch it while the game runs.
**Incremental garbage collection** in this mod's settings page (**Mods > Late Game Performance**) edits the file
for you:

- Ticking it adds the line `gc-max-time-slice=3` to `Timberborn_Data\boot.config` in the game's install folder.
  Unticking it removes that line. Nothing else in the file is touched.
- Before the first change a backup is written beside it, `boot.config.before-incremental-gc.bak`, and never
  overwritten.
- The file is only ever written because you clicked the box, never at startup. If the line is already there,
  the box shows ticked.
- It takes effect the **next time the game starts**; the startup line then says `GC: incremental=True`.
  `Player.log` says whether the file could be written. If it could not (a read-only install folder), add the
  line by hand with the game closed.
- Steam's "verify integrity of game files" and game updates restore the original file, so tick it again
  afterwards.
- It does not affect the simulation, so multiplayer peers may differ. Every player benefits separately.

New in 0.4.7:

- **It stays on.** A game update, or Steam's file check, puts the original `boot.config` back without telling
  anyone. If the box is ticked and the line is gone at startup, the mod puts the line back (once per launch, and
  `Player.log` says so). Ticking the box is the consent for that; for a player who never ticked it, nothing is
  written. Untick the box to stop it.
- **It says so when it is off.** While collection is not incremental and the line is not in `boot.config`, a
  message in the main menu explains it, with **Turn it on** and **Not now**. From 0.4.10 it asks once: **Not
  now** is remembered (in the game's own settings store, so it survives updating the mod), and the checkbox on
  the settings page remains for a player who changes their mind. 0.4.7 to 0.4.9 asked once per launch and had a
  separate setting to switch the message off.
- Every `Timing:` line ends with the state, so any log answers the question:
  `garbage collection is incremental, slice 3.0 ms` or `garbage collection is NOT incremental, so every
  collection freezes the game`.

#### Adaptive garbage collection pacing (0.4.7 to 0.4.9, removed in 0.4.10)

An experimental setting that changed how much clean-up work a frame may carry (Unity's
`incrementalTimeSliceNanoseconds`) from the frame time. One multiplayer session each way, on two computers, showed
no difference that could be told from noise, and a player has no way to judge it, so it is gone, together with
the per-frame hook it needed. Unity's fixed 3 ms slice from `boot.config` applies, and the `Timing:` line still
reports it.

### Diagnostics (off by default)

`Diagnostics = true` times route map (road flow field) fills, including the ones the game still does on
demand, and need selection, which this mod does not change. It adds overhead to hot
code, so switch it off again after collecting numbers.

### Stats

Every `StatsEveryTicks` ticks (default 1000) one line is logged, for example:

```
[LateGamePerformance] Last 1000 ticks. HaulCache: 517 hauler list requests built in 310.0 ms (0.600 ms each);
buildings reused 42010/129250, recomputed 87240
```

"buildings reused" is the work saved: each reused building is one the game would have scanned again.

Two lines for the features added in 0.4.12:

```
[LateGamePerformance] Last 1000 ticks. PlantWater: 1000 passes over 8100 objects on 7 workers; reading 240.0 ms
(0.240 ms per pass), 310 level changes applied in 1.2 ms
[LateGamePerformance] Last 1000 ticks. TerrainMaps: 3 rebuilds of 96 terrain route maps on 7 workers; the main
thread waited 9.5 ms in total, longest 4.1 ms; 5 more built directly in batches of fewer than 4
```

A second line reports route map rebuilds:

```
[LateGamePerformance] Last 1000 ticks. RouteMaps: 4 background rebuilds of 1435 route maps; main thread spent
61.0 ms on them, longest single pause 1.9 ms (built 70 itself, waited for 12), workers were busy 180.4 ms
alongside the game; 6 more built directly on the main thread in batches of fewer than 4
```

A third reports the tree and plant search:

```
[LateGamePerformance] Last 1000 ticks. YielderSearch: 640 searches for trees and plants over 1280000 candidates;
32640 distance lookups, 1247360 left out (97%); 410.0 ms in total (0.641 ms each)
```

(The numbers in these examples are illustrations, not measurements. 0.4.6 and 0.4.7 also printed what the mod's
own work allocated; the game's runtime turned out not to keep the counter that needs, so those figures were never
shown and the code is gone. A separate per-entity profile put the whole simulation at under a tenth of all
allocation, and this mod's part of it at about 0.3%.)

"longest single pause" is the figure to watch for hitching.

## Settings

`version-1.1/LateGamePerformance.cfg`, plain `key = value`. Restart after editing.

**Nothing in this file changes what the game simulates** (from 0.4.9), so players in a multiplayer game may have
different values. The hauling cache, the route maps and the tree and plant search are always on, with the same
fixed values for everyone. Up to 0.4.8 they were settings (`HaulCache`, `HaulCacheFlushEveryTicks`, `RouteMaps`,
`YielderSearch`) that every player had to keep identical by hand, and nothing could check that for them: a
multiplayer mod can compare mod versions, but it cannot see inside another mod's settings file. An older file
that still has those keys is fine: they are ignored, and `Player.log` says so. To run without one of those
features, disable the mod.

| Key | Default | Meaning |
|---|---|---|
| `HaulCacheVerify` | `false` | Recompute the game's list on every request and compare; logs mismatches and uses the game's list. Slower than no mod. For testing. |
| `RouteMapsBackground` | `true` | Rebuild in the background; `false` = main thread waits for the whole batch. May differ between peers. |
| `RouteMapsMinFields` | `4` | Fewer unbuilt maps than this are built directly on the main thread instead of on workers. May differ between peers. |
| `RouteMapsWorkers` | `0` | Worker threads; `0` = automatic, up to 7. May differ between peers. |
| `YielderSearchVerify` | `false` | Run the game's own search as well and compare; logs mismatches and uses the game's result. Slower than no mod. For testing. |
| `Timing` | `true` | The timing stats line. |
| `SaveTiming` | `true` | One line per save with the time of each stage; also lets the timing line report saves separately. Measurement only. |
| `RecordTimings` | `false` | A profiling tool: switch on the game's per-component tick timers and write their report. Slows the game a little. |
| `MetricsEveryTicks` | `3000` | While per-component timings are on: write them every N ticks. `0` = never (disables `RecordTimings` too). |
| `GcReport` | `true` | Startup garbage collector report. |
| `Diagnostics` | `false` | Timers for route map rebuilds and need selection. |
| `PlantWaterVerify` | `false` | Read every water level again on the main thread and compare with the worker threads' result. For testing. |
| `StatsEveryTicks` | `1000` | Stats line interval. `0` = never. |

One setting is on the in-game settings page (**Mods > Late Game Performance**), not in this file:
**Incremental garbage collection**, described above. It is there because it is the one decision that is the
player's to make: it edits a file in the game's folder. Up to 0.4.9 the page had three more boxes; from 0.4.10
per-component timings are `RecordTimings` in this file, the warning asks once and needs no setting, and adaptive
pacing is removed.

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

## Website

The website in `docs/` is plain HTML, CSS and JavaScript with no build step, served by GitHub Pages from `main` and
`/docs`.

**The version, the download links and the SHA-256 update themselves.** `docs/release.js` asks GitHub for the
repository's latest official release (the one marked Latest, so pre-releases are ignored) and fills them in, so
nothing has to be edited when a release is published. The script is configured by the `data-repo` and `data-asset`
attributes on its `<script>` tag, and the pages mark what to fill in with `data-release` attributes; the header
comment in the file lists them. The HTML keeps the values of the last release as a fallback and its links point at
`/releases/latest`, so with no script, no network or a rate-limited lookup the page still works. The answer is
cached in the browser for 30 minutes.

**What is not automatic:** text that describes one specific build, such as the tested and not-yet-verified lists
and "played as 0.4.8". Those elements carry `data-release-pinned="0.4.9"`. When the newest release is a different
version, the script adds a note saying the text was written for 0.4.9. Rewrite the text for the new release and
change the attribute. The fallback values in the HTML can be refreshed then too, but they only show without the
script.
