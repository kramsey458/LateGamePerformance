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
(Up to 0.4.8 that interval was a setting; it is fixed now, see Settings.) The drop is the cache's own required
hook at the start of the tick (`TickableSingletonService.TickAll`), so the cache never runs without it; it used to
ride on the optional hook that also writes the stats lines.

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

**Dead plants and the reach box (new in 0.4.25).** The 0.4.23 session's stats showed the search still at 1.2 ms per
tick, with 153 lookups per search before the first reachable candidate and 88% of searches ending with nothing the
building could take. Two more kinds of lookup are left out. A plant that is neither yielding nor alive (a dead tree
still on the list) can neither be the answer nor set "found something", whatever its distance: the game's search
reads its distance and then does nothing with it, so it is never looked up. And a plant outside the bounding box of
the building's terrain route map cannot be one of the map's tiles, so the game's lookup would answer "unreachable"
and the search would drop it: the box is measured once, right after a map is filled (`TerrainReach`, a postfix on the
generator's fill on whichever thread fills, the game's on-demand fill or this mod's batch), and trusted only while
the map is still filled; a cleared map answers "maybe" until it is rebuilt, and a map never filled has no box, so
those candidates are looked up as before. A building with no unblocked access gets "unreachable" from the game for
every candidate (`Accessible.FindTerrainPath` returns false without one), so nothing is looked up for it. The
result is the game's: the tests run the model on a further 4000 random forests with a reach oracle that is only
ever wrong on the safe side and require the same answer, and they measure the boxes of 89 real terrain maps with the
game's own node ids: every tile of every map is inside its box, a tile just outside is refused, a cleared map
answers maybe. The stats line now says how many were left out for each reason (`of which 12000 dead plants and
300000 plants outside the route map's reach`).

- The first lookup of a search is still made, whatever the plant: it is also what fills the building's terrain
  route map, and the game reads whether a cached map is filled without filling it when a walker asks for a path
  (`PathfindingService.FindTerrainPathIfCached`, through `AccessFlowField.FoundPath`), so a map filled a tick
  later could route a beaver differently. Dead plants are left out only once the search has looked something
  up (a review of 0.4.26 caught a draft that dropped the first lookup); the reach box never suppresses a fill,
  because it only answers for a map that is already filled.
- `YielderSearchVerify = true` runs the game's own search as well, compares, and logs and counts any difference;
  the game is handed the mod's result either way (up to 0.4.26 it was handed the game's, see Settings for why that
  changed). It is slower than no mod and only for testing.
- The prefix runs at Harmony's last priority. BeaverBuddies MultiColony has a prefix on the same method that narrows
  the candidates to the worker's own colony, and Harmony skips such a prefix once one before it has answered; with
  equal priorities the order is the mod load order, which each computer sets for itself. Up to 0.4.25 the filter ran
  first only because BeaverBuddies happened to load first.
- If anything throws, the feature switches itself off for the session and the game's own code runs.
- It is always on. Up to 0.4.8 it could be switched off in the settings file; see Settings for why not any more.

### Plant water check on worker threads (always on, new in 0.4.12; on the water worker since 0.4.23)

Every tick the game asks, for every plant and every other object that cares about flooding (about 8000 in the
colony this is measured in), how high the water stands at its tile, one after another on the main thread
(`WaterObjectService.Tick`): 1.3 ms per tick, 8% of all tick time. Almost none of them change from one tick to
the next.

The question is a pure read: the object's fixed tile, looked up in the water map the game keeps for readers on
other threads, which is only rewritten in its own tick and never during this one. Since 0.4.23 the answers are
computed where that map is made: the water map copy (below) fills the next tick's map on the water worker
thread right after the last water task, and this feature reads every object's level from that copy on the same
thread, before the game swaps it in, with the game's own lookup (`WaterColumnRetriever.GetColumn`, the ceiling
of the water surface, the part above the tile). When the tick then asks, the levels are already there, and the
main thread only goes through them in the game's own order and, for each object whose level changed, does
exactly what the game does: stores the new level and raises the change event. Those handlers (a plant starting
to drown, a building flooding) run on the main thread, in the same order, with the same values as without the
mod.

The levels from the worker are used only when three things hold, all checked in the tick: the map's current
arrays are exactly the copy they were computed from (the water map copy swapped it in and nothing has rewritten
it since; whenever the game copied for itself, they are not), the worker finished, and the game's list of
objects is the one they were computed for. For the last, the mod keeps a mirror of the list through the game's
register and unregister methods and takes a numbered snapshot of it whenever it changed; in the tick the
snapshot and the game's list are walked side by side (0.4.25): an object found in the snapshot takes the worker's
level, an object the snapshot does not hold (it joined the list after the snapshot, or the mirror missed it) is
read on the main thread with the game's own method, and an object that left since is passed over. Objects that
stay keep their order, because the game's list only appends and removes, so one index over each suffices; only
when more than a quarter of the list is unmatched is the mirror rebuilt from the game's list. Up to 0.4.24 any
difference between the two made the tick read every level again: 35 to 49% of passes in the 0.4.23 session,
because a plant is planted or cut in most ticks of a large colony. From 0.4.12 to 0.4.22 the reads were spread
over thread-pool workers started inside the tick, 0.4 ms per tick of starting and joining in the logged colony;
that is now the fallback for a tick with no usable copy, on this mod's own worker threads (see Worker threads).
Since 0.4.26 the mirror's two hooks (register and unregister) are required together, so that a game update
which moved only one of them cannot leave a mirror that grows; and a level change handler that changes the
list (which the game's own `foreach` would refuse) makes the feature hand the tick to the game's loop.

Reading first gives the same values because a handler cannot change the water map or an object's tile, and it
cannot add or remove an object from the list: the game walks that list with `foreach` and would throw if one
did. The result does not depend on the number of threads, so it is the same on every computer.

Since 0.4.28 the main thread no longer reads every object's stored level (`WaterAboveBase`, 6,800 objects spread
over memory) to find the few that changed: in the 0.4.23 session 0 to 146 levels changed per 1000 ticks. It keeps
the level each object was left at by its last pass, in an int array index-aligned with the game's list as it was
then, compares the new levels with that array, and looks only at the objects whose level differs or is not known,
in list order, exactly as the game's loop does: the stored level is read at that moment and, if the new level
differs, the game's own `UpdateWaterAboveBase(int)` stores it and raises the event. This is exact because the
stored level has one writer, the private `UpdateWaterAboveBase(int)`, reached from `WaterObjectService.Tick` (through
the public `UpdateWaterAboveBase()`) and from registration (`OnEnterFinishedPostLoadState`), and both store what the
water map shows at that moment. So an object passed over has a new level equal to the one this mod left it at, and
still stores that level, except in three cases, each handled:

- It registered since the last pass. The list only appends, so those objects are the last entries; as many
  entries as registrations were seen (the register hook counts them) are treated as unknown and read.
- The game's own loop ran in between: fewer than 512 objects, a failure, or another mod's prefix skipping this
  one. A postfix on the same method runs after every call whether the prefix ran or not, and counts the calls the
  prefix did not handle; every known level is then forgotten and the next pass reads them all, as 0.4.27 did on
  every pass.
- An earlier handler in the same pass called the public `UpdateWaterAboveBase()` on it: that stores what the map
  shows, which is the level this pass computed, so the game's loop finds it equal and so does the mod (the stored
  level is read before anything is changed). The same holds for an object that is in the list twice.

While the list's own change counter (`List<T>._version`) has not moved since the last pass the array is used as it
is; otherwise the list and the list as it was are walked side by side, and every object found keeps its known level.

- The tests build two identical sets of 6000 of the game's real `WaterObject`s over a stand-in water map that
  moves between ticks, run one through the game's own loop and one through the mod's fallback, and require the
  same stored levels and the same events in the same order with the same values, for six ticks (4316 level
  changes). A second test puts 3000 real `WaterObject`s over the two real `ThreadSafeWaterMap`s of the water map
  copy's test world, one set ticked by the game's own `WaterObjectService` over the map that only runs the game's
  code, the other by the mod over the map that goes through the copy and the swap: fifteen ticks, fourteen of
  which use the worker's levels, among them the tick an object left the list after the snapshot, the tick one left
  without the hook seeing it, the tick one joined after the snapshot (read on the main thread beside the worker's
  levels) and the tick a thousand joined (the mirror is rebuilt); one falls back because the water layout changed;
  three run in verify mode; levels and events identical throughout, verify mismatches 0. Since 0.4.28 the second
  test also registers objects as the game does (level stored from the map at once), registers the last object
  again, and runs one tick of the game's own loop over the mod's world (21 ticks); and the first test puts four
  objects on tiles whose water it sets, to check the known levels through a tick of the game's own loop (the level
  goes 3, 5 behind the mod, back to 3: the event must be raised), a handler that updates a later object itself, and
  an object in the list twice. With the postfix left out on purpose, the mod passes the level 3 over while the
  object stores 7, and verify mode reports it.
- Below 512 objects the game's own loop runs; starting workers would cost more than it saves.
- `PlantWaterVerify = true` reads everything again on the main thread with the game's own method and compares,
  which also checks the worker's lookup against the game's map, and compares every known level with the level the
  object stores. A difference is logged and counted, and the levels read ahead are stored all the same (up to
  0.4.26 the main thread's were); an object whose known level is wrong is still passed over. For testing.
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
- That holds when a build throws, too (see When a map build throws, under the parallel route map rebuild).
- The tests build a terrain graph with the game's `TerrainNavMeshGraph`, fill 90 maps one by one and in parallel
  and require them identical node for node and in node order, then drive the hook against a real
  `TerrainFlowFieldCache`: everything cached gets built, a map whose start is off the graph is left alone, a
  second tick builds nothing, and after a ground change exactly the cleared maps are rebuilt.

The same behaviour difference as the road maps: maps are built before the first request instead of on it, so the
few code paths that use a terrain map only "if it is already filled" find it filled. Every player on the same
version gets the same.

**0.4.25.** The batch runs on this mod's own worker threads (see Worker threads) instead of the thread pool, and the
cache is walked for unbuilt maps only on a tick after something could have changed (see Route map caches scanned
only after a change), with a full walk every 200 ticks as a safety net. The stats line now also counts the maps in
the cache, the largest map, the walks made and skipped, and any unbuilt map the safety net found that the hooks had
missed.

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
- `DistrictCountsVerify = true` lets the game count as well, compares, and puts the mod's numbers back in the
  game's tables (up to 0.4.26 the game's stayed there). If anything throws, the feature switches itself off; the
  game's count starts by clearing every table, so nothing of the failed one is left.

**0.4.25.** The count runs on this mod's own worker threads (see Worker threads) instead of `Parallel.For`. In the
0.4.23 session a count of 233 inventories took 0.67 ms, nearly all of it waking and joining thread-pool threads that
had gone to sleep between ticks; the work itself is a few tens of microseconds.

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
- `WaterMapCopyVerify = true` lets the game copy every tick as well, compares byte for byte, and then swaps the
  worker's copy in as without the setting, in a postfix that runs before other mods' postfixes (up to 0.4.26
  nothing was swapped in that mode, so the map kept the game's copy). If anything throws, the feature switches
  itself off and the game copies.

### Soil moisture and contamination scans (always on, new in 0.4.14; lists from the workers since 0.4.23)

Every tick the game walks every tile of the map, every soil layer of it, to find the few cells whose moisture or
contamination changed (`SoilMoistureService.UpdateMoistureLevels`,
`SoilContaminationService.UpdateContaminationLevels`); for each one it finds it updates the soil's look and tells
plants that dry out, recover or get contaminated. The simulation marks what changed in a flag array.

Since 0.4.23 the list of changed cells is made where the flags are written. The soil simulation runs on the
game's worker threads during the tick, one row of the map per call; the worker that finishes the last row reads
the whole flag array (eight flags at a time) and writes the changed cells down in the game's loop order (tile by
tile as the game walks them, then layer by layer). In the next tick the service only goes through that list,
calling the game's own `SetMoistureLevel` / `SetContaminationLevel` for each cell, with the game's own filter (a
cell above a tile's column count is passed over, as the game's loop never reaches it). Nothing else may touch the
flags between the last row and the service's tick except the simulator's own tick on the main thread (a column
moved, terrain raised or lowered, a reset), which is counted; a list is not used after such an edit, when the
flag array was replaced, or when it was not finished, and the scan below runs instead. The six hooks this needs
are a feature of their own (`SoilLists`), so that if one of them cannot be installed the scan still runs.

The scan (0.4.14) walks the same tiles in the same order as the game, but first reads eight tiles' flags at once,
in every layer, and passes over the eight when none is set. For the others it runs the game's loop, calling the
game's own per-cell method for each changed cell. A group passed over is one where the game's loop would only
have read flags that are not set, so the same cells are updated in the same order with the same values.

- In the harness, a 256 x 256 map with three layers and one cell in 500 changed: the game's loop 0.39 ms on the
  main thread, the mod's scan 0.065 ms on the main thread, the worker's list 0.042 ms on the worker and only the
  changed cells on the main thread; there are two of these per tick.
- The tests drive the real simulators and services through eleven rounds per kind: seven use a worker's list,
  and one each falls back for a terrain height change waiting on the main thread, an unfinished list, a replaced
  flag array and a simulation reset; the game's per-cell method is called for the same cells in the same order in
  every round, and the pure list is checked against every set flag in the game's order on 90 random maps.
- The tests compare the order of cells with the game's loop on 90 random maps, and drive the real services: the
  game's per-cell method is called for the same cells, coordinates and levels, in the same order. (The game's
  per-cell method itself cannot run in the harness because the soil texture map it updates calls into Unity.)
- `SoilScansVerify = true` walks every cell the game's way as well and compares the list of cells updated.

Looked at in 0.4.28 and not built: a fast path for a changed contamination cell (about 5,360 per tick at ~47 ns in
the 0.4.23 session) that stays on the same side of zero and whose soil colour value is bit for bit unchanged,
writing only the level. The game's `SetContaminationLevel` does three things: the contaminated object's enter or
exit call when the cell crosses zero, `TerrainMaterialMap.SetSoilContamination`, and the level write. The second
compares the new colour value with the pixel the texture map holds at those coordinates, not with what the old
level maps to, and the two can differ: a terrain height change resets the pixels of the cells it moved to 0
through the map's own queue while the level stays, and a colour change queued earlier is not in the pixel until
the map's tick applies it. Skipping the call would leave such a pixel uncoloured where the game recolours it, a
lasting difference on screen. An exact form would read that pixel and compare as the game does; it keeps the
coordinates and the pixel read and saves only the calls, a few of the ~47 ns. Instead, every 8th contamination pass
counts the cells the fast path would take (last part of the stats line), so the saving can be judged from a real
session before anything is built. The moisture side (`SetDesertIntensity`, the same shape) costs 0.02 ms per tick
and is left alone. The count is checked in the harness against the game's rule as decompiled.

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
  file on return) and saves to a stream (the game's save benchmark and crash-report save) run as the game's own
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
- **When a map build throws**, the other maps are still built, and the one that threw is built once more on the
  main thread with the game's generator before anything can see it, so every peer still ends the tick with the
  same maps built. See When a map build throws below.
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

**When a map build throws (road and terrain maps).** A map whose build throws on a worker no longer takes other
maps with it. Up to 0.4.26 a worker gave up the rest of its share of the batch at the first exception, and the
small batch on the main thread gave up the rest of the batch, so which maps were left unbuilt depended on the worker
count and on `RouteMapsMinFields` and `RouteMapsBackground`, which differ between computers; and whether a map is
built changes path results (a few of the game's paths use a map only "if it is already filled"). Any exception also
turned the feature off, on that computer only. Now:

- Every map of the batch is tried; one that throws is left unbuilt and its worker goes on with the next. The game's
  generators clear their scratch state at the start of every build, so a generator that threw is fit for the next.
- After the batch, every map that threw is built once more on the main thread, with the main thread's own copy of
  the game's generator. In the background rebuild that happens before the game can see the map: when it is asked
  for, or when the flight lands. A failure only one computer's worker threads have (a patch by another mod that works
  only on the main thread, say) therefore leaves that computer with the same maps built as everyone else. The first
  such failure of the session is logged as a warning, and the stats line counts the maps built again.
- Only if the main thread's build throws as well does the feature turn itself off. That is then the game's generator
  failing on the game's data, which happens alike on every peer (and would happen on demand in the unmodded game),
  with the same maps built before it. It is said loudly: see A simulation feature that turns itself off.

The harness poisons a map so that it throws on any thread but the test's own: with 5 and with 7 workers, the maps
left unbuilt by the batch are the same, all but that one (0.4.26 left maps 3, 8, 13... unbuilt with 5 workers and
3, 10, 17... with 7). Through the navigation tick hook, with every map throwing on the workers, every map is built
on the main thread instead, identical to the game's, with the feature still on, for road and terrain maps and for the
background rebuild (there also for a map asked for while a worker still holds the flight open). A map that throws wherever it is built, in a small batch, leaves the maps after it built and
turns the feature off.

**Scanned only after a change (0.4.25).** Walking every cached map on every navigation tick to find the unbuilt
ones cost 0.27 ms per tick in the logged colony, on ticks where nothing had changed. `MapChanges` hooks the events
after which a map can be unbuilt or newly buildable: a new cache entry (`FlowFieldCache.StartCachingAtNode`, a
building finished), a nav-mesh update on the road or terrain cache (which clears the maps it touches), and a
district centre added or removed, an obstacle changed or the district map's own nav-mesh update (which decide
whether a road map has a district map to be limited by). Each hook sets a flag; the walk itself is unchanged and
runs on the next navigation tick after a flag. Every 200th tick a full walk counts any unbuilt map the hooks
missed and logs it once (`... 0 unbuilt maps were found by the periodic check alone (left to the game)` on the
stats line), but builds nothing: such a map is left to the game to build on demand, as without the mod, which
every peer does alike, whereas building it on a cadence of this machine's own could have filled it on one peer
before another (a review of 0.4.26 caught that). Every peer runs the same hooks on the same events, so the
walks happen on the same ticks everywhere. If any hook cannot be installed, both features walk every tick as
before. The harness drives both features through their navigation tick hooks: flagged, a tick with nothing marked
leaves cleared maps alone, a marked one builds them, and the 200th tick counts three maps cleared behind the
hooks' back, says so and leaves them until the next flag. The batch that is not in the background runs on this mod's own worker threads (see
Worker threads).

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
falls back to waiting for the whole batch. A map whose build threw on a worker is built again on the main thread
when it is asked for or when the rebuild lands, so the game never sees one of these maps unbuilt.

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
is not written to a file: the game's save benchmark (`-benchmarkSaveCount`) or the save it attaches to a crash
report (no multiplayer mod uses it; BeaverBuddies sends its players the save file); `Save (writing only...)` is one
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

### Entities with nothing to tick left out of the tick loop (always on, new in 0.4.21)

Every tree, crop, path, levee and platform is a tickable entity: it carries a tick component that is switched off
nearly all the time (`DemolitionBlockedStatus` only while marked for demolition, `EntityReachabilityStatus` only
while selected). The game still visits every one of them on every tick, 128 buckets a tick: a native call to ask
whether the object is active, a try block and a walk over its components to find each one disabled. In the logged
354-beaver colony about 10,000 of the 11,500 entity ticks per tick were such visits, and the entity kinds Oak,
Path, Thorns, Pine, Chestnut, Wheat, Potato, Maple, Birch, Sunflower and Levee together took about 10 ms of the
28 ms tick in the Performance Log's sampled figures.

The mod keeps, per bucket, the entities that have at least one enabled tick component, in the same order the game
keeps all of them (by entity id), and its replacement of `TickableEntityBucket.TickAll` ticks only those. A
component's `Enabled` can only change through `BaseComponent.EnableComponent` and `DisableComponent` (the setter
is private), so those two are hooked and move the entity in or out; `TickableEntityBucket.Add` and `Remove` add
and drop entities. An entity whose tick components are all disabled does nothing when ticked, so the result is the
game's. The game's own list is left untouched; BeaverBuddies reads it for its per-tick hash.

Changes in the middle of a pass follow the game's own index rule: the game walks its list by index, so an entity
added before the entity being ticked shifts it and is not reached this pass, while one added after it is reached.
The mirror holds every entity of the bucket at the same index as the game's list, each with a flag that says
whether any of its tick components is enabled. An entity is added to it at once, even mid-pass, and a component
switched on or off sets the flag at once, so an entity that wakes up mid-pass is reached in that pass when its
index is still ahead, as in the game's loop; one that falls asleep is skipped (ticking a sleeping entity does
nothing). The game's own deferred removals (`_entitiesToRemove`) are applied at the end of the pass, on both lists,
as the game does. Until then an entity removed mid-pass is ticked through the game's own `Tick`, which checks each
of its parts, so a part switched on after the removal is still reached in that pass, as in the game's loop. If an entity's tick throws, the
pass is left exactly as the game leaves it (mid-pass, removals pending); should the mirror stop being trusted in the middle
of a pass, every remaining entity is ticked, which is the game's loop. The prefix runs at Harmony's last priority,
so BeaverBuddies' own prefix on the same method, which hashes the game's list before the pass, still runs first. The harness
(`tests/IdleEntitiesTests.cs`) replays a scripted history of 300 passes over the game's real bucket, entities
and metered components, with components switched on and off and entities added and removed both between passes
and from inside another entity's tick, and requires the sequence of effective ticks to be identical to a model of
the game's loop; a directed case removes an entity during a pass and switches it on later in the same pass. The
`IdleEntities:` stats line says how many entities per tick were ticked and how many left out, and on how many passes
the keys were walked (below). If any bookkeeping throws, the feature switches itself off and the game's loop runs.

Each pass starts by making sure the mirror's keys are still the game's, and rebuilds the mirror from the game's list
if they are not (the stats line counts rebuilds; there should be none). The walk over every key for that is skipped
while the list's own change counter (`SortedList`'s private `version`, which every insert, removal and clear moves)
stands where it stood when the keys last matched and no `Add` or `Remove` has come through the hooks since. The
mirror's keys change in the `Add` and `Remove` postfixes, which make the next pass walk the keys whatever the game's
list did (Harmony runs them even when another mod's prefix skips the game's method), and in the end-of-pass
removals, which take each key from both lists (moving the counter) or find it in neither. So an unmoved counter with
no hooked change means the walk would have found the keys equal, skipping it changes nothing the game does, and
players with and without the skip stay in step. A bucket whose list changed since its last pass gets the walk once.
Each bucket's keys are walked anyway on every 128th of its passes, which would catch a change that bypasses the
counter (only a write to the list's private fields could). If the runtime's list has no counter under a known name
(`version`, or `_version`), the keys are walked on every pass, as before. In the harness on .NET 8, a tick's 128
passes over 11,700 entities in 128 buckets, 1 in 12 awake, take about 47 us with the walk on every pass and 30 us
with it skipped; it has not been measured under the game's Mono. The harness checks both kinds of change made behind
the hooks: one that moves the counter is found on the next pass, as before, and one that puts the counter back is
found on the 128th. An `Add` and a `Remove` whose game methods were skipped, leaving only the postfixes to run, are
found on the next pass, as is another list object put in the bucket's field. It also replays a 400-pass history that
changes the game's list behind the hooks as well as through them, once with the skip and once with the walk on every
pass, and requires the same ticks and the same rebuilds.

### Terrain path searches resumed instead of restarted (always on, new in 0.4.18)

When no route map answers a path question, the game runs an A* search over the terrain graph on the main thread,
inside the beaver's tick (`TerrainAStarPathfinder.FillFlowFieldWithPath`). That happens when a beaver standing off
the road network, in a field or a forest beyond the roads' spill range, prices every building that could satisfy a
need (`DistrictNeedBehaviorService.PickShortestAction` asks for a round trip per provider), or walks to a random
spot. The game keeps the last search in one `PathFlowField` and answers the next question from it only if that
search happened to reach the new target; otherwise it clears the field and starts again from the same tile. Pricing
thirty buildings from one spot can mean thirty searches over the same ground. In the logged 354-beaver colony single
beaver ticks of 6 to 14 ms and the slump at the end of every day pointed here, by reading the code; the
`Diagnostics` line of 0.4.16 measures it.

The mod replaces the single-destination search with the game's own algorithm, run on the game's own heap and flow
field, operation for operation: a search that has to start from scratch is the game's search to the bit, tie-breaks
included. When the next question starts from the same tile and nothing else has touched the field or the heap
since (the same field object, the same start, the same tile count, no terrain change, not fully explored), the
mod keeps the explored tiles and the frontier, re-prices the frontier for the new target, pushes the neighbours of
the previous target (the game returns the moment it pops its target, before those are pushed, so without this a
route through the previous target would never be found) and carries on. Explored tiles are never explored again, so
a resumed search never does more than the game's restart would.

What is the same and what is not:

- A fresh search is identical to the game's.
- A resumed search gives the same distance. The game's heuristic never overestimates a terrain step (0.9 per
  straight tile and 1.273 per diagonal, against costs of 1 and 1.414), so every explored tile already holds its
  shortest distance when the search continues. The one exception is floating-point rounding in the last bits when
  the shortest distance is reached by a different but equally short route, and in that case the route itself can be
  a different one of equal length. Ziplines and tubes carry their own costs, which the heuristic does not know;
  from 0.4.20 every push checks the step against the heuristic's drop, a search that pushed across a cheaper step
  is never resumed from, and a resumed search that meets one starts over the game's way (counted as "started
  over" in the stats line), so the distance stays the game's there too.
- Every player on this version gets the same answer: the search state depends only on the simulation's own
  sequence of questions. The only other caller of this search is the game's debug-mode cursor tool. In a
  multiplayer game BeaverBuddies has every player, the host included, load the same save file into a new scene,
  and a new scene starts with no history, so the first question is a fresh search on every computer. (From
  0.4.20 the mod also empties the field and forgets its history after `GameSaver.SaveWithoutFinishingTick`,
  which earlier notes took for the save a joining player loads. It is not: the game calls it only for its save
  benchmark, `GameSaver.BenchmarkSavingToMemory`, and for the save it attaches to a crash report once the first
  uncaught exception has stopped the scene, and no BeaverBuddies build calls it. The hook is harmless there.)
- `TerrainSearchVerify = true`, or the **Verify terrain path searches** box on the settings page (0.4.19), runs the
  unmodified algorithm alongside on a shadow field and heap, compares what
  the game is told after every search and counts: identical, same distance within rounding, equally short but
  different route, different distance, different reachability. It never changes the answer, so it may differ
  between players; after a terrain change or a list-of-destinations search the comparison restarts from the mod's
  state, and a failure inside the comparison only switches the comparison off (0.4.20). Slower; for testing.
- The `TerrainSearch:` stats line counts searches answered from the previous search, started from scratch and
  resumed, with the tiles each explored and the time.
- Measured in the harness (`tests/TerrainSearchTests.cs`, the game's real `TerrainAStarPathfinder`, `PathFlowField`,
  `BinaryHeap` and `HeuristicsCalculator` on a random 90 x 90 terrain with the game's default costs): 300 searches
  from scratch identical to the game's node for node; in 1,200 searches from 150 tiles, 8 per tile, reachability and
  distance agree every time (88% bit for bit, 12% within rounding), the mod explored 376,000 tiles where the game's
  restarts explore 747,000, and in 79% of the searches with the same distance the route was a different, equally
  short one. Verify mode over 600 searches with 17 list-of-destinations searches and 11 terrain changes mixed in
  reported no distance or reachability difference.
- If anything throws, the feature switches itself off for the session and the game's own search runs.

### Home search without the per-beaver overhead (always on, new in 0.4.23)

Every tick the game picks the dwelling that has waited longest for a dweller and looks for a beaver to move in
(`DwellerHomeAssigner.AssignDweller`): it walks every adult, then every child, of the district, asking each
whether it is looking for a better home (`Dweller.IsLookingForBetterHome`) and, if so, whether it may move in
(`AutoAssignableDwelling.CanAssignDweller`); the first that may, does. In a settled colony nobody is looking, so
all 350 beavers are asked on every tick, each first found from its `Beaver` component with a component lookup,
through a LINQ concatenation of the two lists: 0.3 ms per tick in the logged colony, growing with the population.

The mod asks exactly the same questions of exactly the same beavers in exactly the same order, with the game's
own methods, and stops at the same first hit. What it leaves out is the LINQ enumerators and the per-beaver
component lookup, because the components of an entity are fixed for its life. Since 0.4.25 the lookups are kept
beside each of the game's two lists (the district's adults and children): an array of their `Dweller` components
in the same order, rebuilt whenever the list's own change counter (`List<T>._version`, which the runtime bumps on
every addition, removal and replacement) or count moved, so a search is one array walk with one predicate call per
beaver and no per-beaver table lookup (0.17 ms per search in the 0.4.23 session, 480 ns per beaver, most of it the
dictionary). On a runtime whose list has no such counter the 0.4.23 table, one entry per beaver, is used instead.
The two lists are the game's own `ReadOnlyList<Beaver>`s, read by index; a list of any other kind (another mod's)
is left to the game's own walk. The result cannot differ, so it is the same on every computer.

Since 0.4.28 the first question is not asked again while its answer cannot have changed: in the 0.4.23 session
about 950 searches per 1000 ticks asked 355 beavers each, for 5 to 12 move-ins. Whether a beaver is looking for a
better home is read from its home (`Dweller.Home`, and whether the home's game object still exists), the home's
numbers of adult and child dwellers (two private sets), the home's slot numbers (set once, in `Awake`) and whether
the beaver is an adult (set once, in `Awake`). The home and the two sets change only in `Dwelling.AssignDweller`,
`Dwelling.UnassignDweller` and `Dweller.AssignToHome` (which the first calls; the only other writers of `Home` are
loading a save, before a scene's first tick). Every other path goes through them: a death or deletion, a dwelling
blocked, demolished or deleted, an unreachable home, a birth into a home, a child growing up (a new entity), and
Optimized Local Housing's moves (`Dweller.UnassignFromHome`, `Dwelling.AssignDweller`). A counter moves before and
after each of those three calls (so neither an exception part-way nor a question asked during the change leaves an
answer current), on a dweller's deletion and in every new scene. Each answer is kept beside the list with the
counter's value when it was given, and used only while the counter still has that value and the list is unchanged;
a changed list asks all its beavers again. A search still asks `CanAssignDweller`, live, of exactly the beavers
that are looking, in list order, and stops at the same first hit: the same calls the game's `&&` makes. A home
whose game object died while a beaver still pointed at it would change an answer without such a call, but the game
never destroys a dwelling that way: deleting it first leaves its finished state, which unassigns every dweller. If
another mod patches the question, anything it reads (the `Dweller` and `Dwelling` getters above) or
`CanAssignDweller`, or the game's question is not the one this was written for, no answer is kept (one info line at
the first search, and the stats line says why) and every beaver is asked on every search, as up to 0.4.27.

- The tests script the two predicates per beaver (the game's own need a live entity) and compare the pick with
  the game's walk as decompiled over 560 searches of 300 adults and 60 children in both orders: the same beaver
  moves in or nobody does, 360 component lookups in total, verify mode agrees, a list of another kind is handed
  back, a throwing lookup switches the feature off and hands the search to the game; and after a beaver leaves a
  list and two are born, the next searches see the new lists, each rebuilt once. Since 0.4.28 the script changes
  who is looking only through the hooks (as the game can), and checks that `CanAssignDweller` is asked of the same
  beavers in the same order as the game's walk (over 500 searches it asks the first question a fifth as often); that a
  kept answer is used until a home changes (forced: a beaver starts looking without one, and is not asked the
  second question), that verify mode counts such a kept answer, and that another mod's patch on the question (not
  this mod's own) stops the answers being kept.
- `HomeSearchVerify = true` runs the game's walk as well, with fresh lookups, and compares the beaver picked;
  a mismatch is logged and counted, and the mod's pick moves in all the same (up to 0.4.26 the game's did). It
  also asks every beaver whose answer is kept and counts the answers that differ (which are still used).
- The stalest-dwelling walk that precedes the search (`StaleAssignableDwellingService.GetStalest`, a linked list
  rotated until a dwelling with a free slot) is left alone: its order is simulation state and its cost is per
  dwelling, not per beaver.

### Save snapshot on worker threads (on by default, new in 0.4.22)

Every save begins with `SerializedWorldFactory.Create`: for each entity every persistent component writes its
state into a tree of dictionaries. Since 0.4.13 moved the JSON, compression and file write off the main thread,
this snapshot is the part of a save still on it: 200 to 285 ms per autosave in the logged colony, 11,000
entities. Nearly all of those are trees, crops, paths, levees and platforms, whose persistent components read
only managed fields of their own entity (growth progress, coordinates, a yield, an inventory's goods, a demolition
mark) or stateless serializers, and write into their own entity's dictionaries. `SaveSnapshot = true` (the
default) snapshots those entities on worker threads while the main thread snapshots the rest, then assembles the
entities in the game's own order and lets the game's own code save the singletons. The save is the one the game
would have written; only the thread that wrote part of it down differs.

- An entity goes to a worker only if every persistent component on it is on the list in `SaveSnapshot.cs`, which
  is exactly the set whose `Save` this mod has read: in 0.4.22 the 27 components of trees, crops, paths, levees and
  platforms (`BlockObject`, `BlockObjectState`, `Yielder`, `Growable`, `LivingNaturalResource`,
  `CoordinatesOffsetter`, `LivingWaterNaturalResource`, `WateredNaturalResource`, `AridNaturalResource`,
  `ContaminatedNaturalResource`, `GatherableYieldGrower`, `DeadCuttableYieldRemover`, `ConstructionSite`,
  `Demolishable`, `BuilderPrioritizable`, `Pollinatee`, `LayeredBlockObstacle`, `FlippableDecal`, `DecalSupplier`,
  `HaulPrioritizable`, `PausableBuilding`, `Emptiable`, `TimedComponentActivator`, `NamedEntity`, `Inventory`,
  `RuinModels`, `BlockObjectPlacementRandomizer`); since 0.4.25 also 63 parts of buildings whose `Save` was read the
  same way, all reading fields of their own entity or a stateless serializer (`Workplace`, `WorkplacePriority`,
  `Manufactory`, `GoodConsumingBuilding`, `FarmHouse`, `Forester`, `Floodgate`, `WaterInput`, `WaterMover`, the
  valves, gauges and sensors, the automation buildings, `Deteriorable`, `Wonder`, `Hive`, `FixedStockpile`,
  `GoodObtainer`, `GoodSupplier`, `DistrictDistributionSetting`, `PopulationDistributor`, `BreedingPod`, the need
  appliers and more; the file lists them all), and the `ColonyStamp` of the owner's BeaverBuddies MultiColony fork
  (a slot number from a field). In the 0.4.23 session 2,975 entities were kept on the main thread by that stamp
  alone. Beavers stay on the main thread (`Character` reads a transform, `MovementAnimator` reads Unity's time,
  `BehaviorManager` and everything else using the reference serializer), as does anything with a component the
  list does not name. Template names, a Unity name lookup, are read on the main thread for every entity first.
- **Guarded by a hash (0.4.25).** Beside each name the list holds a hash (FNV-1a, 64 bits) of the IL bytes of that
  type's `Save(IEntitySaver)` as compiled, what the method does. A type is used on a worker only while its `Save`
  still hashes to what was read; after a game or fork update that changes one, that type keeps its entities on the
  main thread, with one log line naming it, until the method is read again and the hash renewed with
  `dotnet run --project tests -c Release -- --hashes`, which prints the list ready to paste. The harness checks
  every listed type against the installed game and fork on each run.
- **Guarded against other mods' patches (0.4.26).** A Harmony patch leaves a method's IL untouched and runs
  inside the call all the same, on whichever thread makes it, so the hash cannot see one. Before a type is used
  on a worker, `SaveGuard` reads Harmony's registry for its `Save`, for every method the `Save` reaches through
  the game's code up to three calls deep (read from the IL: calls, virtual calls, constructors, delegates; a
  patched method that implements or overrides an interface or virtual method reached on the way counts too)
  and for every value serializer it loads from a field; the shared helpers every entity goes through (`EntitySaver`, `ObjectSaver`, `ValueSaver`, the keys,
  `SerializedEntity`, `SerializedObject`, `SaveConversions`, `PrimitiveTypeSerialization`, the common number,
  date and good serializers) are checked once per session, for patches and, with one hash over all their IL,
  for changes. A patch by this mod, or one that was read and listed as safe in `SaveGuard.ReviewedPatches`, is
  fine; any other keeps that type on the main thread with one log line, or, on a helper, leaves the whole save
  to the game (`SaveSnapshot:` then counts the saves left to it). The registry is read again whenever the number
  of patches changes, so a mod that patches late is seen at the next save. Deeper than three calls, or a
  patched implementation that is only reached through an interface call inside another mod's code, is not seen. Reviewed so far: MixedStorage's
  postfix on `SingleGoodAllower.Save` (it writes its storage's allocation into the same entity from a
  ConditionalWeakTable lookup; read at 1.0.0, the same since 0.5.8), which in 0.4.25 ran on the workers unchecked.
  The harness reads the IL of every listed `Save` in the installed game (386 calls, 7 serializers), and with a
  stand-in registry checks that a foreign patch on a `Save`, on a callee, on a serializer or on a helper is refused,
  that a reviewed one and this mod's own are not, and that a changed registry is judged again.
- Why those are safe: the simulation is not running during a save (the tick was finished first), the main thread
  is inside `Create` until the workers have joined, every worker writes only into its own entities' dictionaries,
  and the shared helpers they call (`PrimitiveTypeSerialization`, `SaveConversions`, `GoodAmountSerializer`,
  `GoodRegistryValueSerializer`) keep no state. The reference serializer's cache is not thread-safe, which is why
  nothing that uses it is on the list.
- If a worker throws for any reason, the results are discarded, the game's own `Create` runs, and the feature is
  off for the session. The `SaveSnapshot:` stats line counts entities on workers and on the main thread and the
  main thread's time per snapshot; once per session a second line names the components that kept entities on the
  main thread, by how many entities carry them, so the list can be extended (since 0.4.25 every unlisted part of
  an entity is counted, not only the first found).
- `SaveSnapshotVerify = true`, or the **Verify save snapshots** box on the settings page, takes the game's own
  entity snapshot as well at every save and compares every entity value by value (the game's own
  `SerializedEntity.Equals` compares lists by reference, so this mod has its own deep comparison). The mod's
  snapshot is the one used, so the singletons are saved once, by the game's code; entity saves are pure reads, so
  taking them twice is safe. Measurement only; slower saves.
- The workers are dedicated threads, not thread-pool tasks, because the pool may still be busy with this mod's
  route maps when a save starts; they and the main thread take eligible entities from one shared counter, so the
  main thread is never idle while workers still have entities left. The prefix runs at Harmony's last priority so
  that measuring prefixes on `Create` (the save timing line's snapshot stage) still run.
- The harness (`tests/SaveSnapshotTests.cs`) runs the builder over 3,000 fake entities with the game's real
  `EntitySaver`, `ObjectSaver`, `SerializedEntity` and `SerializedWorld`: numbers, strings, lists and nested
  objects through a value serializer, a tenth of the entities kept on the calling thread; the result equals the
  sequential snapshot entity for entity and keeps the order, a throwing part comes back as a failure with nothing
  thrown, and one worker or 64 give the same result.

### Memory clean-up right after a save (0.4.17 to 0.4.23, removed in 0.4.24)

0.4.17 ran a full garbage collection right after each save's main-thread part (`GC.Collect()` in a postfix on
`SaveWriter.WriteToSaveStream`), so that the collection which followed every autosave a second later would land in
the frame that was long anyway. The first session recorded with it, 0.4.23 in a 360-beaver colony with a managed heap of
3.1 GB, showed the flaw: a forced full collection is not incremental, and on that heap it took 894 to 1274 ms per save
and freed 150 to 280 MB, while the collection it pre-empted was a 100 to 190 ms frame. Every save was a 1.1 to 1.5 s
freeze. The feature is removed; `CollectAfterSave` in an older settings file is ignored. What remains true: the save's
garbage still brings a collection soon after it, and with incremental collection on that is the 100 to 190 ms frame
the `Timing:` line reports. Lesson: never force a full collection on a heap this size.

### Audio listener placed only when needed (on by default, new in 0.4.17)

Every frame `SoundListener.LateUpdateSingleton` casts a ray from the screen centre against the terrain and every
block object to find what is under it, then moves the listener a tenth of the way there: about 0.3 ms per frame and
50 KB/s of garbage in a large colony. `SoundListener = true` (the default) runs it when the camera moved (position,
rotation or screen size), while the listener is still gliding towards its target (the last placement moved it more
than a hundredth of a unit), and otherwise once every ten frames so that something built under the screen centre is
picked up within a fraction of a second. The `SoundListener:` stats line says in how many frames it ran. Sound only.

### User interface work every few frames (on by default, new in 0.4.17)

`UiThrottle = true` (the default) covers two of the game's per-frame systems that only the screen reads:

- `StatusAggregator.UpdateSingleton` goes through every status in the colony every frame to fill the alert lists
  the top bar and the alert buttons read, about 0.35 ms per frame. It runs every fourth frame now; a status that
  appears or clears reaches the alert count a few frames later. Removing a subject still updates the lists at once.
- `EntityPanel.UpdateSingleton` refreshes every fragment of the selected entity's panel every frame, the largest
  single source of garbage among the game's systems at about 120 KB/s. It runs every second frame now, and always
  on the frame a different entity is shown.
- `TopBarPanel.UpdateSingleton` refreshes the top bar's counters every frame (67 us). Every fourth frame (0.4.22).
- `EntityReachabilityStatus.Tick` is a tick component the game switches on only while its entity is selected; it
  then asks every reachability part of that entity on every tick, and the selected beaver in the logged session
  cost up to ten times an ordinary one. It runs every eighth tick (0.4.22). Selection is per player, so this
  status was never simulation state; the icon it toggles appears up to eight ticks later.

The `UiThrottle:` stats line counts both. Neither is read by the simulation.

### Animated objects off screen (on by default, new in 0.4.17)

Every frame `AnimatorRegistry` advances every Timbermesh animator in the colony and writes its pose: node
animators move child transforms, vertex animators set a material time. In the logged colony that was about 1.1 ms
per frame, and twice that in the working day when every beaver is out walking, whether or not the object was on
screen. `AnimatorCulling = true` (the default) hooks `TimbermeshAnimator.UpdateAnimation`: when none of the
object's renderers is visible (Unity's `Renderer.isVisible`, which counts shadow casters), the mod runs the game's
own private `UpdateTime` and leaves out the pose writes. So `Time`, `RepeatedTime`, `PlayingFinished`, the
`AnimationChanged` event and the wonder's saved animation time are exactly what they would have been, and only the
transforms and material values of something nobody can see go unwritten. When the object comes back into view its
pose is written again on that frame's update, from the time it would have had anyway; on the single frame in which
it first reappears it can show the pose it had when it left the screen. The renderer list of each animator is
looked up on first sight and again every 600 frames or when one was destroyed. An animation that plays once and
finishes while off screen (the wonder, the working-hours bell) gets its final pose written at that moment, because
the game never updates a finished animation again (0.4.20). The `AnimatorCulling:` stats line counts updates left
out. Rendering only: the simulation reads animator time (the wonder, the clutch, particle
triggers, the character model), never a node transform or a material.

**By distance (new in 0.4.25).** Seen from the usual height nearly every animator in a big colony is on screen,
so the culling above had little to leave out (`AnimatorRegistry` was still 1.29 ms per frame in the 0.4.23
session, the largest per-frame item of the game). `AnimatorLod = true` (the default) writes the pose of an animator
farther from the camera than `AnimatorLodDistance` tiles (80) every second frame and beyond twice that distance
every fourth, spread over the frames by a hash of the object so no frame writes them all; time still advances
every frame, so the pose written is the one the game would write on that frame, a frame or three old at a size
where that is not visible. Anything closer is written every frame as before. The camera position is read once per
frame and each object's once per 60 frames. The stats line counts the pose writes left out. Rendering only, like
the rest of this feature.

### Catch-up limit (on by default, new in 0.4.16)

The game turns each frame's `Time.deltaTime` into simulation buckets, and Unity caps that delta at a third of a
second. So after a 300 ms frame (an autosave, a garbage collection) at speed 7 the next frame is asked for 2.1 s
of game time, three and a half ticks: in a 35-minute session log of a 354-beaver colony every autosave was followed
by frames of 131, 66, 142, 290 and 107 ms, and every collection by a similar tail. One hitch became about a second
of stutter.

`LimitCatchUp = true` (the default) limits what one frame may catch up (measured from the same capped unscaled
frame time Unity hands the ticker) to twice the recent ordinary frame time
(at least a thirtieth of a second); the rest is simply not run, so the simulation loses a fraction of a second of
wall clock per hitch. Which ticks run, and in what order, is unchanged, and how many buckets a frame runs already
differs between players and machines, so this is pacing, not simulation. The limit follows each machine's own
frame time: a computer that always needs 70 ms per frame keeps its full share. A `CatchUp:` line is logged with the
stats when it did something: how many frames were limited, how much game time was left out, the longest such frame
and the ordinary frame time it measured. It is hooked into `Ticker.Update`, which BeaverBuddies leaves to the game
(its own patch there only marks that ticking is in progress).

### Worker threads of the mod's own (always on, new in 0.4.25)

The district counts, the plant water levels when they had to be read inside the tick, and the terrain and road
map batches ran on `Parallel.For`, on thread-pool threads that sleep between ticks: waking and joining them cost
about half a millisecond per use in the logged colony (a district count of 233 inventories took 0.67 ms), more
than the work itself. `TickWorkers` keeps a few threads of its own (`RouteMapsWorkers`, or up to 7 by default;
they are also the route map builders' count) that spin briefly after a job in case the next one follows at once
and then block on a signal each; the calling thread takes part as worker 0, and a job started from inside a job,
or while one runs, simply runs alone on the calling thread. A job is told how many workers share it and leaves
its results in slots no two workers share, so how the work is split is never simulation state. The `TickWorkers:`
stats line counts jobs shared, threads, wakes while still spinning and from sleep, and jobs run alone. The
harness runs strided sums over four workers, a throwing worker, a job started inside a job, more workers than the
maximum and a maximum below one.

### A simulation feature that turns itself off (always on, new in 0.4.27)

Each of the eleven simulation features hands its call to the game's own code and stays off for the rest of the
session if something throws inside it that it cannot recover from (road and terrain maps first build a map that threw
again on the main thread, and turn off only if that throws too: see When a map build throws). From then on this computer runs the game's code for it where the other players
may still run the mod's, and a multiplayer game can drift apart; a new game scene (a BeaverBuddies rehost included)
does not bring the feature back, only a restart does. Up to 0.4.26 all a player got was one warning in `Player.log`.
Now a feature that turns itself off after an error is also said:

- **in the game:** a dialog names the feature and asks every player to quit and restart the game before playing on
  together. It is shown from the frame loop, not from inside the tick where the failure happened, when the failure
  happens and again at the start of every later game scene while a feature is off;
- **in the log:** the `Simulation features:` line is written again with that feature `OFF` and the same request,
  right after the warning, then on every stats interval (when `StatsEveryTicks` is above 0) and in every new game
  scene, so two players' logs still compare at a glance:

```
[LateGamePerformance] Simulation features: HaulCache on, RouteMaps OFF, YielderSearch on, TerrainMaps on, ...,
HomeSearch on. RouteMaps turned itself off during this session after an error (see the warning above), so this
computer runs the game's own code for it. In multiplayer every player should restart the game before playing on
together.
```

A feature that stands down by design is not a failure and is not reported: DistrictCounts beside another mod's patch
it has not read does the same on every computer with the same mods, and says so in its own line. The dialog changes
nothing in the simulation.

The harness checks which features the forced failures above turned off, that each name turns exactly that feature
`OFF` in the line, that the line is said at once, in a new game scene and on the next stats interval, and when the
dialog shows: once per feature turning itself off and once per new game scene, never on every frame.

### Route map caches scanned only after a change (always on, new in 0.4.25)

See *Scanned only after a change* under the parallel route map rebuild: `MapChanges` hooks the events after which
a cached road or terrain map can be unbuilt or newly buildable and sets a flag; the two features walk their caches
on the next navigation tick after a flag and on every 200th tick regardless.

### Behaviour log lines written only when read (always on, new in 0.4.25)

Every time a beaver changes behaviour, `BehaviorManager.SetRunningBehavior` formats the behaviour's name and the
day into a string and keeps it in a ring of the last ten. The ring is read in two places only: it is saved with
the beaver, and the debug fragment of the entity panel shows it. At a few thousand changes a second in a large
colony those strings are a steady stream of garbage nothing reads until the next save (the `BehaviorManager` tick
was 5.7 ms and 267 KB/s of garbage in the 0.4.23 session). The mod keeps the name and the day as they are, a
reference and a float, in a ring of its own per beaver, and writes them into the game's ring with the game's own
format only when the game is about to read it: before `Save`, and before the panel reads the log. The strings are
identical, the same format applied to the same name and the same float with the same culture, and the game's ring
ends up holding exactly what it would have after the same sequence of additions (the last ten, in order), so the
save file is byte for byte the same. `Behavior` is a plain class, so the game's `!=` is a reference comparison and
so is the mod's. The harness feeds the game's real `CyclicBuffer<string>` directly and through the mod's ring with
the same changes, flushed at different points, and requires the same contents, and checks the format on awkward
day numbers. If anything throws, the feature switches itself off and the game's method runs; lines already noted
are still written out before the next read.

### Walker moves without a delegate per tick (always on, new in 0.4.25)

`WalkerMover.Move` passes the speed manager's `GetWalkerSpeedAtCurrentPosition` as a method group to the path
follower, which allocates a delegate for every walking beaver on every tick (part of the 118 KB/s the `WalkerMover`
tick produced in the 0.4.23 session). The mod's prefix makes the same calls with the same arguments, with one
delegate per mover made on first use and kept; what the game's method does is done call for call, so what it
would throw is thrown. If the components cannot be read, the game's method runs and the feature is off.

### Memory tools (settings page, new in 0.4.25)

Two boxes on the settings page that untick themselves. **Measure live memory now** forces one full collection
(which freezes the game for about a second on a 3 GB heap; that is why it is a box and not automatic) and logs a
`Memory:` line with the managed heap in use afterwards, the heap reserved and the process working set, so the
heap's live size can be told from its garbage. **Write a memory snapshot file** asks the engine for a memory
snapshot (`Unity.Profiling.Memory.MemoryProfiler.TakeSnapshot`, managed and native objects) into
`Documents\Timberborn\LateGamePerformance\heap-<time>.snap`, which opens in Unity's Memory Profiler package and
names every object by type; the log says whether this build of the engine wrote one. Measurement only.

### Diagnostics (off by default)

`Diagnostics = true`, or the **Diagnostics timers** box on the settings page (0.4.19), times code this mod does not
change: route map (road flow field) fills, including the ones
the game still does on demand; need selection (`DistrictNeedBehaviorService.PickBestAction`); walker path finding
(`Walker.FindPath`, once per new destination); and the game's two A* searches (`RoadAStarPathfinder` and
`TerrainAStarPathfinder`), which it falls back to when no route map answers. That happens when a beaver standing off
the road network (a field, a forest) prices the buildings that could satisfy a need, or walks to a random spot, and
a search for a place that cannot be reached explores the whole area first. The line reports, per method, calls,
total time and the longest single call; for the A* searches also how many calls were real searches (rather than
answers out of the previous search from the same node), how many nodes they explored, and how many explored
everything reachable. It adds overhead to hot code, so switch it off again after collecting numbers. Since 0.4.19
the hooks are installed whether or not the timers are on, so that the box works without a restart; off, each hooked
call costs one boolean check. Since 0.4.25 the line also times `DistrictCitizenAssigner.AssignToClosestDistrict`
(citizens without a district looking for one) and `UnassignCharactersCutOffFromTheirDistricts` (every citizen
checked for a cut-off district), the singleton the 0.4.23 session could not attribute, and samples the engine's
own profiler counters named in `UnityMarkers` once per frame (`Camera.Render`, `Culling`, `Shadows.Draw`, draw and
SetPass call counts, `GC.Collect` and the rest of the default list) with `ProfilerRecorder`, reporting each as time
per frame and the longest frame or as a count; the first session with the timers on writes `unity-markers.txt`
beside the metrics reports with every counter this build of the engine offers, so the list can be changed.

The one-destination terrain A* search is also what `TerrainSearch` replaces. Up to 0.4.25 its prefix ran first
and Harmony then skipped the timer's, so the terrain A* numbers left out every search `TerrainSearch` answered.
Since `TerrainSearch` runs at Harmony's last priority (see Build and test) the timer's prefix runs first, and the
terrain A* numbers include those searches, timed as the mod answers them (resumed or from its previous search).
Numbers from before and after that change are not comparable; the `TerrainSearch:` stats line is unchanged.

### Stats

Every `StatsEveryTicks` ticks (default 1000) one line is logged, for example:

```
[LateGamePerformance] Last 1000 ticks. HaulCache: 517 hauler list requests built in 310.0 ms (0.600 ms each);
buildings reused 42010/129250, recomputed 87240
```

"buildings reused" is the work saved: each reused building is one the game would have scanned again.

Two lines for the features added in 0.4.12:

```
[LateGamePerformance] Last 1000 ticks. PlantWater: 1000 passes over 6800 objects; 996 used the water worker's
levels (0.240 ms there per pass; 6772000 objects matched, 420 that joined the list after the snapshot were read on
the main thread; matching 0.080 ms per pass), 4 read every level inside the tick on 7 workers (0.400 ms per pass),
4 had no copy to use; the list changed in 380 ticks, the mirror was rebuilt 0 times; 146 level changes applied;
main thread 0.100 ms per pass, of which comparing with the levels known from the last pass 0.015 ms and applying
0.001 ms; 420 stored levels read from the objects themselves (new objects, and all of them after the game's own
loop ran, which happened 0 times), the known levels lined up with a changed list in 380 passes
[LateGamePerformance] Last 1000 ticks. TerrainMaps: 3 rebuilds of 96 terrain route maps on 7 workers; the main
thread waited 9.5 ms in total, longest 4.1 ms; 5 more built directly in batches of fewer than 4
```

In the PlantWater line (0.4.28), "comparing" and "applying" split the main thread's own part: comparing the new
levels with the ones known from the last pass, then looking at the few that differ. "stored levels read from the
objects" should stay near the number of objects that joined; "the game's own loop ran" should stay at 0 in a
colony above 512 objects (each time, every level is read once more).

A second line reports route map rebuilds:

```
[LateGamePerformance] Last 1000 ticks. RouteMaps: 4 background rebuilds of 1435 route maps; main thread spent
61.0 ms on them, longest single pause 1.9 ms (built 70 itself, waited for 12), workers were busy 180.4 ms
alongside the game; 6 more built directly on the main thread in batches of fewer than 4
```

Both map lines end with `; N maps whose first build threw were built again on the main thread` when that happened
(see When a map build throws).

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
[LateGamePerformance] Last 1000 ticks. SoilScans: 2000 moisture and contamination passes in 40.0 ms (0.020 ms
each); 1990 went through a list of changed cells made on a worker (0.040 ms there per list), 10 scanned the map
on the main thread (97.0% of 8-tile groups had nothing changed and were passed over); lists not usable: 0 not
finished, 10 after a main-thread edit, 0 after the flag array was replaced; 5400 changed cells updated; in every
8th contamination pass 480000 of 670000 changed cells kept their contaminated state and their soil colour value
[LateGamePerformance] Last 1000 ticks. WaterRendering: 1000 updates switched water tiles 800 times where the game
switches them 900000 times; 3000 of 6000 flow direction and flow limit uploads left out because the graphics card
already had them
```

One for the home search (0.4.23):

```
[LateGamePerformance] Last 1000 ticks. HomeSearch: 950 searches for a beaver to move into a home went through
337250 beavers in 5.0 ms (0.005 ms each), asking 10650 of them whether they were looking for a better home (the
other answers were kept from earlier searches, no home having changed since); 8 moved in; the dweller lists beside
the game's were rebuilt 30 times; 0 searches over lists of another kind were left to the game
```

"asking" (0.4.28) is how many times the game's first question was really asked; the rest were answered from an
answer kept since no home changed. If another mod patches the question the line says so, and "asking" equals
"went through".

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

Lines added in 0.4.25 (illustrations again):

```
[LateGamePerformance] Last 1000 ticks. TerrainReach: 12 terrain route maps measured after a fill, 0 of them empty
[LateGamePerformance] Last 1000 ticks. BehaviorLog: 41200 behaviour changes noted without a string; 310 lines
written into the game's logs before 42 reads (saves and the entity panel's debug view)
[LateGamePerformance] Last 1000 ticks. WalkerMove: 212000 moves through the mod, 360 speed delegates made in total
[LateGamePerformance] Last 1000 ticks. TickWorkers: 1030 jobs shared with 6 worker threads (woken while still
spinning 5900 times, from sleep 280 times); 2 jobs ran on the calling thread alone
```

## Settings

`version-1.1/LateGamePerformance.cfg`, plain `key = value`. Restart after editing.

**Nothing in this file changes what the game simulates** (from 0.4.9), so players in a multiplayer game may have
different values. The hauling cache, the route maps and the tree and plant search are always on, with the same
fixed values for everyone. Up to 0.4.8 they were settings (`HaulCache`, `HaulCacheFlushEveryTicks`, `RouteMaps`,
`YielderSearch`) that every player had to keep identical by hand, and nothing could check that for them: a
multiplayer mod can compare mod versions, but it cannot see inside another mod's settings file. An older file
that still has those keys is fine: they are ignored, and `Player.log` says so. To run without one of those
features, disable the mod.

**The verify keys only measure.** Each runs the game's own code beside the mod's, and logs and counts
every difference in the stats lines, but the game is handed the mod's result either way: the hauling list, the tree
search, the plant water levels, the district counts (put back after the game's count), the water map (the worker's
copy swapped in after the comparison), the soil cells, the terrain path searches, the home search and the save
snapshot. So one player may switch them on alone, from this file or from the settings page, even in the middle of a
game. Up to 0.4.26 the hauling list, tree search, plant water, district count, water map and home search checks
handed over the game's result when they found a difference: harmless alone, but in multiplayer the one player with
a key on then took a different answer from the others the moment the mod had a bug, and the colonies drifted apart.
The tests make every one of those checks see a difference and require the game to be handed the same as with the
key off. What a key on still changes is how much of the game's code runs, and with it any other mod's code hooked
into it: the tree search check looks up every candidate, which fills terrain route maps that a player with the key
off may fill a tick later (a fill's timing cannot change a result, see Tree and plant search); the district count
check asks every good disallower twice; the hauling list check asks every haul provider on every request; the home
search check asks the game's own dwelling predicates. The game's own versions of these only read.

| Key | Default | Meaning |
|---|---|---|
| `HaulCacheVerify` | `false` | Recompute the game's list on every request and compare; logs and counts mismatches, the game still gets the cached list. Slower than no mod. For testing. |
| `RouteMapsBackground` | `true` | Rebuild in the background; `false` = main thread waits for the whole batch. May differ between peers. |
| `RouteMapsMinFields` | `4` | Fewer unbuilt maps than this are built directly on the main thread instead of on workers. May differ between peers. |
| `RouteMapsWorkers` | `0` | Worker threads; `0` = automatic, up to 7. May differ between peers. |
| `YielderSearchVerify` | `false` | Run the game's own search as well and compare; logs and counts mismatches, the game still gets the mod's result. Slower than no mod. For testing. |
| `Timing` | `true` | The timing stats line. |
| `SaveTiming` | `true` | One line per save with the time of each stage; also lets the timing line report saves separately. Measurement only. |
| `RecordTimings` | `false` | A profiling tool: switch on the game's per-component tick timers and write their report. Slows the game a little. |
| `MetricsEveryTicks` | `3000` | While per-component timings are on: write them every N ticks. `0` = never (disables `RecordTimings` too). |
| `GcReport` | `true` | Startup garbage collector report. |
| `LimitCatchUp` | `true` | After a long frame, run at most twice an ordinary frame's simulation time in the next one instead of everything the long frame missed. Pacing only; may differ between peers. |
| `Diagnostics` | `false` | Timers for route map rebuilds, need selection, walker path finding and the game's A* searches. |
| `PlantWaterVerify` | `false` | Read every water level again on the main thread and compare with the worker threads' result, and compare the levels known from the last pass with the stored ones; the worker's levels are stored. For testing. |
| `DistrictCountsVerify` | `false` | Let the game count each district's resources as well and compare; the mod's numbers are put back. For testing. |
| `WaterMapCopyVerify` | `false` | Let the game copy the water map every tick as well and compare with the worker's copy, which is then swapped in. For testing. |
| `TerrainSearchVerify` | `false` | Run the game's own terrain path search alongside on a shadow field and count every difference. Measurement only. For testing. |
| `SoilScansVerify` | `false` | Walk every soil cell the game's way as well and compare which cells were updated. For testing. |
| `HomeSearchVerify` | `false` | Run the game's own walk for a beaver to move in as well and compare the pick, and ask every beaver whose answer was kept again; logs and counts mismatches, the mod's pick moves in. For testing. |
| `WaterRendering` | `true` | Water tiles are only switched when their state changes, and texture uploads the graphics card already has are left out. Rendering only; may differ between peers. |
| `SaveSnapshot` | `true` | Snapshot trees, crops, paths, levees and platforms on worker threads at every save. Saving only; may differ between peers. |
| `SaveSnapshotVerify` | `false` | Take the game's own snapshot as well and compare every entity; the mod's is saved. Measurement only. For testing. |
| `SoundListener` | `true` | Place the audio listener when the camera moved, while it glides, and every tenth frame otherwise. Sound only; may differ between peers. |
| `AnimatorCulling` | `true` | Leave out the pose update of animated objects with no renderer on screen; their time keeps running. Rendering only; may differ between peers. |
| `UiThrottle` | `true` | Status alert lists every fourth frame, the selected entity's panel every second frame. Interface only; may differ between peers. |
| `BackgroundSave` | `true` | Autosaves and menu saves finish (JSON, compression, file) on a worker thread. `false` = the game saves by itself. May differ between peers. |
| `VerifyAll` | `false` | Every verify mode above at once, for one correctness session. Measurement only; slower. Also a box on the settings page. |
| `AnimatorLod` | `true` | Animated objects far from the camera have their pose written every second or fourth frame; their time keeps running. Rendering only; may differ between peers. |
| `AnimatorLodDistance` | `80` | Tiles from the camera beyond which the above applies (twice it: every fourth frame). `0` switches it off. |
| `UnityMarkers` | the list above | The engine's profiler counters sampled per frame while `Diagnostics` is on, separated by `;`. |
| `StatsEveryTicks` | `1000` | Stats line interval. `0` = never. |

Seven settings are on the in-game settings page (**Mods > Late Game Performance**): **Incremental garbage
collection**, described above, which is there because it is the one decision that is the player's to make (it
edits a file in the game's folder); since 0.4.19, **Diagnostics timers** and **Verify terrain path searches**,
plus **Verify save snapshots** since 0.4.22 and **Verify every feature** since 0.4.25, the measurements a tester is
asked to switch on for a session; and since 0.4.25 the two memory measurements, **Measure live memory now** and
**Write a memory snapshot file**, which act once when ticked and untick themselves. The boxes mirror the
`Diagnostics`, `TerrainSearchVerify`, `SaveSnapshotVerify` and `VerifyAll` keys in this file: a box starts from the
file's value and then remembers what was last chosen on the page, so the page wins once it has been used. All can
be switched while a game is running. Nothing on the page affects the simulation. Up to 0.4.9 the page had other boxes; from 0.4.10 per-component timings are
`RecordTimings` in this file, the warning asks once and needs no setting, and adaptive pacing is removed.

## Suggested first test

1. Copy a save. Set `HaulCacheVerify = true`, play a few in-game days single-player, and check `Player.log` for
   `verify mismatches 0` in the stats lines. Any mismatch line is a bug worth reporting.
2. Set `HaulCacheVerify = false` and compare how the game feels at speed 3, and read the stats lines.
3. Optionally set `Diagnostics = true` for one session and keep the `Diagnostics:` lines.
4. For one session tick **Verify every feature** on the settings page (or set `VerifyAll = true`), play a few
   in-game days, and look for `verify mismatches 0` on every stats line that has one. It is slower; untick it after.

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
the failed attempt crashes the game later (0.4.3 did this with `GameSaver.Save`). Every prefix that can skip the
game's method (one that returns `bool` or takes `ref bool __runOriginal`) must carry
`[HarmonyPriority(Priority.Last)]`, because once one has skipped it Harmony skips every later prefix that could
change the call (one returning `bool`, or with a `ref`, `out` or reference-type argument): the tests find them all in
the declared patches, sort each with Harmony's own sorter against another mod's ordinary prefix registered after it,
and require it to come last, so that other mods' prefixes on the same methods (BeaverBuddies', MultiColony's colony
filter) run first on every computer whatever the mod load order. They also build a road
network with the game's own navigation classes and check that parallel route map rebuilds are identical to the
game's one-by-one rebuilds, that only thrown-away, in-use maps are rebuilt, and that a failing worker is
contained: the maps a failing build leaves do not depend on the worker count, and the main thread builds them again. The background rebuild is run for 25 rounds with maps requested in shuffled order while workers
are busy: every map must be complete and correct at the moment it is asked for. They do not run the game.

`dotnet run --project tests -c Release -- --hashes` prints the current hash of every listed snapshot component's
`Save` (game and, when installed, the BeaverBuddies MultiColony fork) and of the shared saving helpers, ready to
paste into `SaveSnapshot.Allowed` and `SaveGuard.HelpersHash` after an update; the ordinary run checks every
listed hash against what is installed.

`tools/benchmark-timberborn.ps1` runs the game's built-in benchmark on a save with per-component tick timings
(`-metrics`), for before/after comparisons of the game itself: `-benchmarkLength` makes the game start with every mod
switched off, so that benchmark never measures this mod. With `-SaveCount N` it runs the game's save benchmark instead
(`-benchmarkSaveCount`, plus `-skipModManager` so the enabled mods load without waiting on the mod manager screen):
after the warm-up the game saves N times into memory, writes the average, median, 90th percentile, minimum and maximum
to `Player.log` and quits; the Save timing line splits each of those saves.

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
