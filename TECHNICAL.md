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

### District resource counts on worker threads (always on, new in 0.4.13)

Every tick each district adds up, for every good, what all its storage holds and how much room it has
(`DistrictResourceCounter.Tick`): 1.07 ms per tick in the colony this is measured in. The numbers feed the top
bar and the stockpile tooltips and, inside the simulation, the automation resource counter, so they have to be
the game's numbers at the game's moment. Counting less often, or only what changed, was looked at and dropped:
what an inventory may hold depends on things with no reliable change signal, and a stale number would change
what an automation building does.

Most of the cost is the room question, which the game asks per inventory per allowed good with a linear search
inside (a warehouse that allows 30 goods does some 900 string comparisons to report one number). Both questions
are pure reads of the inventory, and the answer is a sum of whole numbers, which comes out the same in any
order. So the inventories are dealt out to worker threads, each worker asks the game's own questions
(`Inventory.Stock`, `Gives`, `PublicInput`, `GetCapacity`) and adds up into its own tables, and the main thread
adds the workers' tables into the game's tables. The two steps that go through interfaces a mod may implement
(goods inside workshops, goods being carried) then run as the game's own code on the main thread.

- What may run on a worker is decided per inventory. `GetCapacity` asks the inventory's `IGoodDisallower`, which
  a mod may implement; only the game's four implementations, which were read and are pure, go to workers. Any
  other inventory is counted on the main thread by the same code.
- If another mod has patched one of the methods the workers would call, the feature stands down with one log
  line and the game's own count runs. The numbers are the same either way.
- Exception (0.4.15): patches that were read and are safe on worker threads are accepted, by mod id, patch method
  and target together. So far one: MixedStorage's `LimitPatch.Prefix` on `SingleGoodAllower.AllowedAmount` (read
  at MixedStorage 0.5.7). It answers from the allocation the player set and the storage's capacity, and the only
  thing it writes is its own per-storage cache of limits, rebuilt from those same values; each storage is counted
  by exactly one worker. The log says `DistrictCounts: another mod patches ... that patch was read and is safe`.
  Up to 0.4.14 MixedStorage's patch made the feature stand down.
- Districts with fewer than 96 storage inventories are left to the game's loop.
- The tests fill one counter with the game's `UpdateCounters` and one with the mod over 700 real `Inventory`
  objects with the game's own capacity rules (and one stand-in mod rule, which must only ever be asked on the
  main thread), and compare every good through the game's public `GetResourceCount`, for six rounds with stock
  moving in between. In the harness one count takes the game 1.9 ms and the mod 0.5 ms.
- `DistrictCountsVerify = true` lets the game count as well and compares. If anything throws, the feature
  switches itself off; the game's count starts by clearing every table, so nothing of the failed one is left.

### Water map copy on a worker thread (always on, new in 0.4.14)

Every tick the game copies the whole water map into the copy that readers on other threads use
(`ThreadSafeWaterMap.Tick`): every water column of every tile, then a flow direction for every column, on the main
thread. 0.70 ms per tick in the colony this is measured in; 0.83 ms for a 256 x 256 map with three water layers in
the harness.

What that copy will hold is known earlier than the game makes it. The water simulation runs on worker threads
during the tick and ends with `UpdateWaterChangesTask`; after that nothing touches the simulation's arrays until
the main thread's next tick. So right after that last task, on the same worker thread, the mod makes the same copy
into a second set of arrays, calling the game's own `FlowVectorCalculator` for the flow directions. In the next
tick, where the game would copy, the main thread only swaps the two sets (under 0.001 ms). In the harness the
worker's copy takes 0.54 ms, alongside the game's own work.

The hook is on `ThreadSafeWaterMap.Update`, the method that copies (from 0.4.15; 0.4.14 hooked `Tick`, which calls
it). Harmony still runs other mods' postfixes on a method whose prefix skipped it, so a mod that looks at the map
after `Update` sees the swapped map, the same bytes the game's copy would be. BeaverBuddies' desync trace (with its
Debug setting on) hashes the map there; under 0.4.14 it missed every swapped tick.

- Nobody reads the second set while it is written: the soil, water rendering and other parallel tasks read the set
  that was current when their tick started, and the main thread reads through the map's own fields, which only
  change in the swap.
- The copy is not used, and the game copies on the main thread as without the mod, on any tick where the
  simulation's arrays may have changed after the copy was made: a queued change to the water layout was applied
  (`WaterSimulator.ProcessModifications` with anything queued: a building, dam or terrain change), the simulation
  was reset, a water layer was added, the game says a column changed, or the copy was not ready. Those are the
  only ways the arrays change outside the water tasks.
- The tests drive two real `ThreadSafeWaterMap`s over one real `WaterSimulator`, one ticking the game's way and one
  through the mod, for 16 ticks with water, flows and column counts moving, a layout change, a reset, a new layer
  and a missing copy, and require the same bytes in both after every tick.
- `WaterMapCopyVerify = true` lets the game copy every tick as well and compares byte for byte (nothing is swapped
  in that mode). If anything throws, the feature switches itself off and the game copies.

### Soil moisture and contamination scans (always on, new in 0.4.14)

Every tick the game walks every tile of the map, every soil layer of it, to find the few cells whose moisture or
contamination changed (`SoilMoistureService.UpdateMoistureLevels`,
`SoilContaminationService.UpdateContaminationLevels`); for each one it finds it updates the soil's look and tells
plants that dry out, recover or get contaminated. The simulation marks what changed in a flag array.

The mod walks the same tiles in the same order, but first reads eight tiles' flags at once, in every layer, and
passes over the eight when none is set. For the others it runs the game's loop, calling the game's own
`SetMoistureLevel` / `SetContaminationLevel` for each changed cell. A group passed over is one where the game's
loop would only have read flags that are not set, so the same cells are updated in the same order with the same
values.

- In the harness, a 256 x 256 map with three layers and one cell in 500 changed: the game's loop 0.59 ms, the mod
  0.10 ms, and there are two of these scans per tick.
- The tests compare the order of cells with the game's loop on 90 random maps, and drive the real services: the
  game's per-cell method is called for the same cells, coordinates and levels, in the same order. (The game's
  per-cell method itself cannot run in the harness because the soil texture map it updates calls into Unity.)
- `SoilScansVerify = true` walks every cell the game's way as well and compares the list of cells updated.

### Water rendering (on by default, new in 0.4.14)

Rendering only; the simulation never reads any of it, so it cannot affect multiplayer. `WaterRendering = false`
turns it off.

- **Tiles.** The water surface is drawn in 16 x 16 tiles per water layer. Every tick the game switches every tile it
  ever made off and the ones with water on again, one native Unity call each. The mod remembers which tiles are on
  and only switches the ones whose state changes. The tiles that are on afterwards are the same.
- **Uploads.** Each water data texture exists twice (before and after the tick); every tick the game swaps them and
  uploads both, layer by layer. For flow directions and flow limits the "before" upload sends exactly what the
  graphics card already has: what the game uploaded as "after" one tick earlier, into the same texture, from the
  same array, which nothing wrote since. The mod checks that per layer (same texture, same array, exactly one data
  swap in between) and otherwise lets the game upload both. Depths, contamination, columns and link barriers are
  left alone because the game edits their "before" data between the two uploads.
- Not done: skipping layers that hold no water. That needs comparing whole layers, which costs about what the upload
  does.
- Unlike the rest, this part cannot be tested outside the game (it needs Unity's renderer). If water looks wrong
  (tiles missing or flickering, flow patterns frozen), set `WaterRendering = false` and report it.

### Background save (on by default, new in 0.4.13)

A save freezes the game for 0.8 s on a fast computer and up to 2.4 s on a slower one in the colony this is
measured in. The save timing line (below) showed where that goes: about a third is the snapshot, which reads
live game objects and has to be on the main thread, and more than half is turning the snapshot into JSON,
compressing it and writing the file, which does not. The snapshot is plain data from the moment it is taken:
every value is converted to a number, a string or a nested plain object when it is stored, and the game's JSON
writer keeps no state. So for the saves the game queues (**autosaves and saves from the menu**), that second
half runs on a worker thread and the game carries on.

How it fits into the game's own save, without replacing it:

```
GameSaver.Save                         NOT patched (see "Save timing line"); BeaverBuddies hooks it to move the
                                       save to a tick boundary, and everything below happens inside it
  Ticker.FinishFullTick                the game's
  SaveWriter.WriteToSaveStream         replaced for this save: the snapshot is taken and every other entry
                                       (thumbnail, metadata, anything a mod adds) is written, on the main
                                       thread, in the game's order. Only the world entry's JSON is left.
  GameSaveRepository.CreateSave...     the game's: name validation, settlement directory
    FileService.CreateFile             replaced for this save: opens "<name>.timber.saving", starts the worker
  onSaveCompleted                      wrapped when the save was queued: the game's callback (autosave: delete
                                       the oldest autosaves; menu: close the box) runs on the main thread once
                                       the file is in place
```

The worker builds the archive exactly as the game does (same zip mode, entry order and compression level),
reads it back and checks every entry, writes it to `<name>.timber.saving`, flushes it to the disk, and only then
renames it over the target. The game lists saves by the `.timber` extension, so an unfinished file is never
offered for loading, and **a save that is overwritten stays intact until its replacement is complete** (the
game itself truncates the old file first and then writes). A `.saving` file left by a crash is cleared by a later
save in that folder.

What keeps a save safe:

- **Only queued saves.** The save on exit, BeaverBuddies' rehost save (both "instant": the caller expects the
  file on return) and saves to a stream (BeaverBuddies sends those to joining players) run as the game's own
  code, untouched. The writer is only replaced when its caller is `GameSaver.Save` itself (or a detour's copy
  of it), a queued save is pending, and the repository is then asked for that same save; any other caller gets
  the game's writer.
- **One job at a time.** Any save of any kind first waits for a job still running. So does anything that asks
  the save repository about files (listing, opening, deleting, "does it exist"), a new scene, and quitting the
  game; the worker is a foreground thread, so the process does not end under it.
- **A prepared save never ends as an empty file.** If the `.saving` file cannot be opened, or another mod
  supplies the file service, or the save turns out not to be the queued one, it is written into the stream the
  game opened, there and then, errors included.
- **If the worker fails**, the save is done again on the main thread from the same snapshot, the game's way. If
  that fails too, the game's callback does not run (so no old autosave is deleted because of it), the game
  gets the same `GameSaverException` it would have had, and the log says `SAVE FAILED`. After any worker failure
  background saving stays off for the session.
- **If preparing fails**, nothing has been written: the feature turns itself off and the game's own code
  performs that save in full.

In multiplayer nothing here touches the simulation, so it does not matter whether every player has it, and
BeaverBuddies' own handling of saves (moving them to a tick boundary, not finishing a tick while saving) runs as
before: it wraps `GameSaver.Save`, and this works inside it.

Two log lines per background save:

```
[LateGamePerformance] Save: 310 ms total = finishing the tick 14 ms + snapshot 190 ms + world JSON and
compression 0 ms + thumbnail 60 ms + everything else 46 ms (world JSON, compression and the file write follow on
a worker thread)
[LateGamePerformance] BackgroundSave: 2026-09-20 21h14m, Day 412.autosave.timber written (14.2 MB). Off the game
thread: world JSON, compression and check 540 ms, file 35 ms. On the game thread: snapshot and the other entries
262 ms.
```

(Illustrations, not measurements.) `BackgroundSave = false` in the settings file switches it off.

Tested in the harness: the file writer against every failure above with real files; the game's real
`WorldSerializer` writing the same bytes on a worker thread as on the main thread, and the game's reader loading
the result; and the whole chain of hooks in the order `GameSaver.Save` calls them, with a real `SaveWriter` and
the real `WorldEntryWriter` type. **Not tested: inside the game.** In particular, whether BeaverBuddies' detour
leaves `GameSaver.Save` recognisable as the caller can only be seen there; if it does not, saves simply stay the
game's own and one log line (`the writer was called by ...`) says what was seen.

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

These hooks are measurement only: they read a clock around the game's own methods and run only during a save.
(From 0.4.13 a separate feature, "Background save" above, moves the JSON stage of queued saves to a worker
thread; this line then shows what the main thread still spends and says so.) The game's own `Saved game in 0.80s` line starts its clock after the tick
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

One for the district counts (0.4.13):

```
[LateGamePerformance] Last 1000 ticks. DistrictCounts: 1000 counts of 640 inventories on 7 workers in 310.0 ms
(0.310 ms each); 0 inventories per count on the main thread; 0 counts of small districts left to the game
```

"ms each" is to be compared with the 1.07 ms the game's own count was measured at. "on the main thread" counts
inventories whose capacity rule comes from a mod.

Three for the features added in 0.4.14:

```
[LateGamePerformance] Last 1000 ticks. WaterMapCopy: 996 ticks swapped in the copy made on a worker thread
(0.540 ms there per tick); the game copied on the main thread in 3 ticks where the water layout changed and 1 where
no copy was ready
[LateGamePerformance] Last 1000 ticks. SoilScans: 2000 moisture and contamination passes in 200.0 ms (0.100 ms
each); 97.0% of 8-tile groups had nothing changed and were passed over; 5400 changed cells updated
[LateGamePerformance] Last 1000 ticks. WaterRendering: 1000 updates switched water tiles 800 times where the game
switches them 900000 times; 3000 of 6000 flow direction and flow limit uploads left out because the graphics card
already had them
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
| `DistrictCountsVerify` | `false` | Let the game count each district's resources as well and compare. For testing. |
| `WaterMapCopyVerify` | `false` | Let the game copy the water map every tick as well and compare with the worker's copy. For testing. |
| `SoilScansVerify` | `false` | Walk every soil cell the game's way as well and compare which cells were updated. For testing. |
| `WaterRendering` | `true` | Water tiles are only switched when their state changes, and texture uploads the graphics card already has are left out. Rendering only; may differ between peers. |
| `BackgroundSave` | `true` | Autosaves and menu saves finish (JSON, compression, file) on a worker thread. `false` = the game saves by itself. May differ between peers. |
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
