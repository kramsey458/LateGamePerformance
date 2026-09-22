# Late Game Performance: the next round

A working brief for a Claude session with full effort. It is self-contained: read it top to bottom before touching code. The
owner (Kyler) is obsessive about performance, plays a 360-beaver colony in BeaverBuddies co-op on two computers, and runs the
Performance Log mod on both, so real measurements are available for the asking. Everything in this file rests on the session
recorded on 2026-09-21 23:13 (0.4.23, deep profile, 10 minutes at speed 7) unless it says otherwise.

## 1. Ground rules (do not skip)

- **Exactness.** Every change to simulation code gives the game's own result, bit for bit where floats are involved, with the
  same events in the same order. Nothing simulation-affecting is ever a setting: it ships fixed and identical for every player
  on the same version, because BeaverBuddies can compare versions but cannot see inside a settings file. Pacing, threading,
  rendering, sound, interface and measurement may be settings.
- **Every simulation change gets a verify mode** (`XxxVerify` in the cfg, or a box on the settings page for things a tester is
  asked to switch on) that runs the game's own code alongside and counts differences, and a harness test that drives the
  game's real classes where they can be constructed outside Unity. `dotnet run --project tests -c Release` must print
  `ALL PASSED` before any release. The harness validates patch targets (the Workshop Harmony cannot run under .NET 8, so
  patches are validated, not applied) and refuses targets with exception filters, which crash the game when patched under Mono.
- **Replacing prefixes carry `[HarmonyPriority(Priority.Last)]`** so BeaverBuddies' own prefixes on the same methods run first.
  Postfixes of other mods still run after a skipped original; count on that, never on being the only patch.
- **A feature that throws switches itself off for the session and hands the call to the game's code**, logged as a warning,
  in a state the game's code can carry on from. No feature may leave half-applied state.
- **Facts about the game's tick** (`TickableSingletonService.TickAll`): `FinishParallelTick` (wait), then every tick singleton
  in registration order, then `StartParallelTick`. So a worker that finishes inside the parallel tick has finished before any
  singleton of the next tick runs, and no singleton runs while the parallel tick is in flight (except the
  `ForcedParallelTickFinished` handlers, which the soil simulators use to tick again before a save). Entities tick in 128
  buckets between `TickAll` calls. The BeaverBuddies fork keeps this order (its synchronous-parallel patch is compiled out).
- **Repository and tools.** Mod: `C:\Users\Kyler\code\LateGamePerformance` (work in a worktree; commits go to `main` by
  `git push origin HEAD:main`; releases by `gh release create vX.Y.Z dist/LateGamePerformance-X.Y.Z.zip --target main
  --prerelease --title "X.Y.Z preview" --notes-file <notes>`; `.\build.ps1` builds and zips; `packaging/manifest.json` and
  `source/LateGamePerformance.csproj` carry the version). Decompiled game source: `%TEMP%\tb-src` (ilspycmd; regenerate
  with `ilspycmd -p -o %TEMP%\tb-src <Managed>\Timberborn.*.dll` if missing). Game 1.1.2.4, Unity 6000.5, Mono. The owner's
  other repositories (`BeaverBuddies-MultiColony`, `BeaverBuddies-Multiplayer-Stability-Fork`, `PerformanceLog`,
  `MixedStorage`, `OptimizedLocalHousing`, `HungryPathing`) may be read; the running BeaverBuddies is MultiColony 1.4.0-beta2
  or later (its worktrees hold the newest code; `git worktree list` in that repo). `TECHNICAL.md` in this repo documents every
  feature; the memory file of this project's Claude sessions has the history.
- **Measurement.** Performance Log writes one folder per session to `Documents\Timberborn\PerformanceLog\<date>` (`summary.md`
  first, then `profile.csv`, `frames.csv`, `spikes.csv`, `events.csv`; `README.md` and `columns.md` in the folder explain
  them). Its `Watch = Namespace.Type.Method; ...` cfg line times any method by name with a Harmony wrapper and puts it in
  `profile.csv` as kind `method`. This mod's own lines are in `Player.log` (`%USERPROFILE%\AppData\LocalLow\Mechanistry\
  Timberborn\Player.log`; `Player-prev.log` is the session before): `Simulation features:` at startup, then every 1000 ticks
  `Timing:`, `PlantWater:`, `SoilScans:`, `HomeSearch:`, `YielderSearch:`, `HaulCache:`, `DistrictCounts:`, `WaterMapCopy:`,
  `TerrainSearch:`, `IdleEntities:`, `SaveSnapshot:`, `UiThrottle:`, `AnimatorCulling:`, `CatchUp:`, and one `Save:` line per
  save. `RecordTimings = true` in the cfg writes the game's own per-component timers to `Documents\Timberborn\
  LateGamePerformance` every 3000 ticks. `Diagnostics` (settings-page box) times route map fills, need selection, walker path
  finding and the A* searches. Allocation figures in Performance Log are coarse (heap-size deltas: the runtime's per-thread
  allocation counter does not work under Unity's Mono), so a large KB/s on one singleton is a lead, not a fact, until a
  `Watch` on the suspect confirms it.
- **The owner's preferences.** Asks for releases explicitly (a pre-release first, "latest" after playing it). Prefers
  in-game toggles over launch options and never wants players to control simulation-affecting values. Wants stats lines that
  can be read at a glance and docs (README, TECHNICAL.md, `docs/` website) kept truthful about what has and has not been
  played. Ask before reading a repository not listed above.

## 2. Where the time goes now (0.4.23, 360 beavers, 11,728 entities, speed 7, host: 9800X3D + RTX 4080S)

Per frame, 11.2 ms mean at 89 fps (12.7 ms at speed 7): entity ticks 1.97, per-frame systems 1.88, tick singletons 0.22,
Unity's own work 6.98 (62%: culling, 18,147 draw calls, 173 set-pass calls, 7.3 M triangles, animation, UI). Main thread
11.5 ms, render thread 5.7, graphics card 6.4, no wait to present (vsync off). The main thread is the bottleneck, and most of it
is not this mod's territory.

Per tick, 21.1 ms of main-thread time at 9.5 to 11.7 ticks per second:

| Where | ms per tick | Notes |
|---|---|---|
| Entity ticks | 18.5 | 766 entities ticked, 10,642 passed over (0.4.21). Components, sampled: `BehaviorManager` 5.7, `WalkerMover` 1.5, `NeedManager` 0.6, `DistrictResourceCounter` 0.5 (this mod's DistrictCounts), `RangedEffectSubject` 0.45, `ContaminationApplier` 0.34, `Walker` 0.34, `Worker` 0.2, `LifeProgressor` 0.2, `CriticalNeedActionStatusRegistrar` 0.15, `WorkplaceIlluminator` 0.12, `SleepSoundEmitter` 0.11. About 7 ms of the 18.5 is not attributed to any component (sampling, the bucket loop, Performance Log's own overhead): treat as unknown. |
| Tick singletons | 2.1 | `WaterRenderer` 0.38, `WaterObjectService` 0.31 (PlantWater's main-thread part), `DistrictCitizenAssigner` 0.28, `NavigationSynchronizer` 0.27 (mostly this mod's two postfixes, which scan the map caches and rebuild the maps they find; see G), `SoilContaminationService` 0.22 (2,645 changed cells per tick, 83 ns each), `DwellerHomeAssigner` 0.19 (HomeSearch 0.17 per search), `OptimizedLocalHousing.HousingService` 0.06, `TimeTriggerService` 0.06 (one 30 ms call). |
| This mod's own lines | | `YielderSearch` 5.4 searches per tick at 0.23 ms = **1.2 ms per tick** (88% end with "nothing the building has room for"; 153 distance lookups per search before the first reachable candidate). `HaulCache` 0.6 requests per tick at 0.7 ms = 0.4 ms per tick with no evidence it beats the game's own build. `DistrictCounts` 1 count per tick, 233 inventories on 7 workers, 0.67 ms (whether the game's own loop is faster at this size was never measured; see the correction in E). `TerrainSearch` 5.3 searches per tick, 0.05 ms in total: solved. `PlantWater` fell back to reading inside the tick in 35 to 49% of ticks because the object list changed after the snapshot. |

Per frame, the systems: `AnimatorRegistry` 1.29 ms (72% of all per-frame system time; AnimatorCulling found nothing to cull
in two of three intervals because the camera saw everything), `InputService` 0.13, `StatusAggregator` 0.10 (already every
fourth frame), `TopBarPanel` 0.02, BeaverBuddies `ReplayService` and `ConnectionPanelService` 0.01 each (but 68 KB/s of
garbage from the panel).

Saves: `Save: 1247 ms = snapshot 264 + thumbnail 14 + everything else 968`. The 968 ms was the memory clean-up this mod added in
0.4.17 (`SaveCollect`): a full blocking collection on a 3.1 GB heap took 894 to 1274 ms per save and freed 150 to 280 MB.
**Removed in 0.4.24** (see section 3). The snapshot's 264 ms is the main thread's share: 3,667 entities stay on it, 2,975 of
them only because they carry the BeaverBuddies fork's `ColonyStamp` component.

Memory: managed heap 2.9 to 3.3 GB in use, 5 collections in 10 minutes (0.5 per minute), allocation about 1.8 MB/s
(192 KB per tick: entities 65, "other" 52, singletons 30, tick loop 22, per-frame 21). What holds 3 GB is unknown; load-time
growth was 411 MB for entities, 243 MB in `EventBus` post-load, 235 MB `BeaverFactory`, 195 MB `BotFactory` (with zero bots),
147 MB Performance Log's own buffers, 122 MB terrain nav mesh.

Loading: 20.3 s (`WorldEntitiesLoader` 6.4 s, `EventBus` post-load 3.1 s, `BottomBarPanel` load 2.9 s, `NavigationSynchronizer`
post-load 2.4 s, `BeaverFactory` 1.0 s).

## 3. Done in this round already: 0.4.24 removes the post-save collection

`SaveCollect` (0.4.17) called `GC.Collect()` right after each save's main-thread part to fold the collection that used to follow a
second later into the save frame. With incremental collection on and a 3 GB heap, a forced full collection is the longest
frame in the session by far (0.9 to 1.3 s), while the collection it pre-empted was 100 to 190 ms. The feature is gone, not a
setting; saves are back to about 300 ms until item A lands. Lesson for everything below: **never force a full collection on this
heap**, and measure any garbage-collector idea on the real heap size before shipping it.

## 4. The work, ranked

Each item has the evidence, the design, why it is exact, how to verify it, and what decides go or no-go. Do them in order
unless a measurement changes the order. Ship each as its own pre-release (0.4.25, 0.4.26, ...), with the harness green, docs
updated, and a release note that says what is and is not tested.

### A. Save snapshot: move the fork's stamped entities to the workers, guarded by an IL hash (saves 264 ms -> ~100 ms per save)

Evidence: `SaveSnapshot:` lists `BeaverBuddies.Colonies.ColonyStamp (2975)` as the component keeping most entities on the main
thread. Its `Save` (`BeaverBuddies/Colonies/ColonyReach.cs`, `class ColonyStamp`) is `if (slot >= 0)
entitySaver.GetComponent(StampKey).Set(SlotKey, slot)`: a field read, safe on a worker.

Design: (1) add an IL-hash guard to `SaveSnapshot.Allowed`: at startup, for every allow-listed type, hash the IL bytes of its
`Save(IEntitySaver)` (`MethodBody.GetILAsByteArray()`) and compare with the audited hash stored beside the type name; a type
whose hash differs is dropped from the list with a log line naming it, so a game or fork update that changes a `Save` cannot
put a Unity call on a worker unnoticed. (2) audit and add `BeaverBuddies.Colonies.ColonyStamp` (the fork is the owner's; the
type may be absent, which is fine). (3) then look at the next names on the unlisted line (`BeaverNeedBehaviorPicker` 363,
`Workplace` 161, `Floodgate` 46, `ZiplineTower` 36, `WaterSource` 28, `CustomizableIlluminator` 25, ...): read each `Save`, add
the pure ones with their hashes. Beavers will stay on the main thread while any of their parts is unsafe (`Character` reads a
Transform, `MovementAnimator` reads `Time.time`); a part-level split (workers snapshot the safe parts of an entity into a
temporary entity, the main thread does the unsafe parts and merges in `AllComponents` order so the file is byte-identical) is
the follow-up if the main thread is still the long pole afterwards.

Correction (2026-09-22) to design (1): a hash of `Save`'s own IL bytes does not see a change in anything `Save` calls. A
`Save` that hands its work to a helper (a private method, a base class, a serializer) can change behaviour while its own bytes
stay the same, so the guard has to hash the IL of every method `Save` calls in its own assembly too (resolve the call
operands with `Module.ResolveMethod`, as `SaveGuard.Read` already does to look for other mods' patches). IL bytes also carry
metadata tokens, which any rebuild of the assembly may renumber: a game or fork update can change a hash without changing
the code. That is a false alarm on the safe side (the type stays on the main thread, with a log line, until it is read
again), so expect it after every game update. As shipped in 0.4.25 and 0.4.26, `SaveSnapshot.SaveHash` still hashes
`Save`'s own body only; `SaveGuard` checks what `Save` calls for Harmony patches and hashes the shared saving helpers, but
not the IL of the other methods `Save` calls.

Verify: the existing `Verify save snapshots` box compares every entity with the game's own snapshot. Test: extend
`tests/SaveSnapshotTests.cs` with a type whose hash is wrong and check it is dropped. Decision: the `Save:` line's snapshot
stage from one session; target under 120 ms.

### B. YielderSearch: the 1.2 ms per tick that is left (largest single simulation item this mod can reach)

Evidence: 5.4 searches per tick, 0.23 ms each, 153 distance lookups per search, 88% of searches end with "nothing the
building has room for or the worker can take". `LazyCandidates` (`source/YielderSearch.cs`) already skips a candidate once
"found something" is true; the 153 lookups happen before that, i.e. the first ~150 candidates in enumeration order are
unreachable from the flag or dead. In a multi-colony game the marked-tree set is global, so a lumberjack of one colony
measures the path to every marked tree of the other colonies first.

Design, in order of certainty:
1. Instrument (Diagnostics only): per search, how many lookups returned unreachable, how many candidates were dead and not
   yielding, and the candidate count. One session tells which of the two cases below matters.
2. Exact skip: a candidate that is not yielding and not alive contributes nothing to the game's answer (it can neither set
   "found something" nor compete), so its lookup can always be skipped, not only after "found something". Read
   `ClosestYielderFinder.FindClosestYielders` again (in `%TEMP%\tb-src\Timberborn.YielderFinding`) before trusting this:
   `flag = flag || isYielding || (isLiving && yielder.IsAlive())` and only yielding ones are added.
3. Dropped (corrected 2026-09-22): the pre-filter by nav-mesh group. `NavMeshGroupService` is not the connected components of
   the terrain graph. It is a table of named group ids (`GetOrAddGroupId(string)`, default id 0), and
   `TerrainFlowFieldGenerator` reads a node's `GroupId` only to choose the step cost, so two nodes in different groups can be
   connected. The game's real connectivity structure, `GlobalReachabilityService`, is a lazy breadth-first "area" map over
   the instant terrain graph, not the synchronized one the flow fields read. It is cleared on every instant nav-mesh update,
   and asking it fills it in, which is a side effect. Terrain route maps are also limited in range, so a shared area does not
   mean reachable either. No pre-filter by group or area is exact as designed; item 2 is the exact and cheaper route. (0.4.25
   shipped item 2 and a reach box measured from each filled map, `TerrainReach`; see TECHNICAL.md.)
4. Not exact, do not do: caching a negative answer across ticks.

Verify: `YielderSearchVerify` (runs the game's search alongside). Test: extend the 4,000-forest model test with dead and
unreachable candidates in front. Decision: the `YielderSearch:` line; target under 0.4 ms per tick.

### C. PlantWater: use the worker's levels even when the list changed, and make the apply loop cheap (0.31 -> ~0.05 ms per tick)

Evidence: `PlantWater: ... 646 used levels computed on the water worker, 354 read them inside the tick ...; the list changed in
354 ticks ...; main thread 0.303 ms per pass`. Two costs: the fallback (`Parallel.For` inside the tick, 0.36 ms) in a third to a
half of all ticks, and 0.3 ms of main-thread work even when the worker's levels are used (6,800 objects: a reference compare, a
`WaterAboveBase` property read and a list index each, slow under Mono without inlining).

Design: (1) the game's list only ever changes by `Add` at the end and `Remove` of one element, so survivors keep their relative
order: at consumption, walk the game's list and the snapshot with two indexes (`objects[i] == snapshot[j]` -> use `levels[j]`,
`i++`, `j++`; otherwise `j++` until it matches or runs out; objects left over at the end are new registrations) and read only
the unmatched objects with the game's own method on the main thread. Exact: every object's level is either precomputed from
the same map the tick reads, or read now. (2) In the apply loop, read the `List<WaterObject>`'s backing array once
(`_items` via a field getter, like the `_version` trick below) and compare the level against a stored copy of the last
applied level per object only when the game's property read is measured to be the cost; otherwise leave the property read.
Measure both halves with a Stopwatch inside `TickPrefix` first (the stats line has "main thread ms per pass"; split it).

Verify: `PlantWaterVerify`. Test: extend `RunPlantWaterOnWorker` in `tests/WaterAndSoilTests.cs` with registrations and
removals between the snapshot and the tick and require the worker path is still used. Decision: `PlantWater:` line; target
under 0.08 ms main-thread per pass with fewer than 5% fallbacks.

### D. HomeSearch: 480 ns per beaver is too much (0.17 -> ~0.04 ms per search)

Evidence: `HomeSearch: ... asked 358778 beavers in 171.7 ms (0.172 ms each)`; the harness does the same scan in 13 us. The
difference is in-game costs: `Dictionary<Beaver, Dweller>` with an interface comparer under Mono, and `IsLookingForBetterHome`
(`(bool)Home` goes through Unity's object liveness check, then two hash-set counts).

Design: (1) split the time with two Stopwatches for one session (lookup vs predicate). (2) Replace the dictionary with a cache
keyed on the game's `List<Beaver>` instance behind each `ReadOnlyList<Beaver>` (`_list` field): a `Dweller[]` aligned with the
list plus the list's `_version` (both `List<T>` fields exist in Mono's class library; fall back to the dictionary if not found),
rebuilt when the version changed. Then a search is an array walk with one predicate call per beaver. Exact by the same
argument as today. (3) If the predicate dominates, stop: it is the game's own and must be asked.

Verify: `HomeSearchVerify`. Test: `tests/HomeSearchTests.cs` with list mutations between searches. Decision: `HomeSearch:` line.

### E. A thread pool of this mod's own for in-tick parallel work (DistrictCounts, the PlantWater fallback, TerrainMaps small batches)

Evidence: `DistrictCounts: 1002 counts of 233 inventories on 7 workers in 690.0 ms (0.689 ms each)`. 233 inventories at the game's
own per-inventory cost (about 4 us) is about 1 ms sequential, so 7 workers should take 0.15 ms; the other 0.5 ms is
`Parallel.For` waking thread-pool threads. The same overhead sits in PlantWater's fallback (0.36 ms for 6,800 trivial reads).

Correction (2026-09-22): these numbers contradicted section 2, which called the same 0.67 ms count "probably slower than
the game's own loop at this size" (corrected there too), while the paragraph above puts the game's loop at about 1 ms,
slower than the mod. Neither was measured in the game: the 4 us per inventory has no source (in the harness the game's
code counts 700 inventories in about 2 ms on .NET 8, under 3 us each, and Mono is slower). To settle it, time the game's
own count inside `DistrictCountsVerify`, as `HaulCacheVerify` does for the hauler lists. The pool itself is no longer
open: 0.4.25 shipped worker threads of the mod's own (`TickWorkers`) for DistrictCounts, the PlantWater fallback and the
terrain and road map batches. What is left of E is that measurement and, if the game's loop still wins at 233 inventories,
raising `MinInventories`.

Design: a small persistent pool (`TickWorkers`): N threads that spin briefly then block on a `ManualResetEventSlim`, fed a
work-item struct (delegate + range), joined with a countdown; dedicated threads like `SaveSnapshot.Build` uses, but kept alive
between ticks. Measure wake latency in the harness (target under 20 us). Then move DistrictCounts, the PlantWater fallback and
TerrainMaps' `FillParallel` onto it, and re-measure `DistrictCounts:`; if the main-thread loop is still cheaper for 233
inventories, raise `MinInventories` accordingly (thread split is not simulation state, so the threshold may change freely).

Verify: existing verify modes. Decision: `DistrictCounts:` ms each; target under 0.25 ms.

### F. HaulCache: prove it pays or delete it (0.4 ms per tick at stake, 13 patches of risk)

Evidence: `HaulCache: 614 hauler list requests built in 461.9 ms (0.752 ms each); buildings reused 47648/153500` and, from every
session since 0.2.0, zero district-list hits: the list is rebuilt on every request because the flush is per tick and there is
less than one request per tick. Whether the mod's build is faster than the game's own has never been measured.

Design: in `HaulCacheVerify` mode, time the game's own build (the verify path already recomputes it) and print both times in
the stats line. One session with the box on. If the mod is not at least 30% faster per request, delete the feature (source,
config, plugin registration, tests, docs), which also removes 13 patches from the startup line's risk. If it is faster, keep it
and say so in TECHNICAL.md with the numbers.

### G. NavigationSynchronizer: this mod's two per-tick cache scans (0.27 ms per tick, and 563 KB/s blamed on it)

Evidence: `NavigationSynchronizer` 2.57 ms/s with 563 KB/s of allocation attributed, yet the game's `Tick` does nothing when no
road or district changed. This mod's `RouteMaps.NavigationTickedPostfix` and `TerrainMaps.NavigationTickedPostfix` run inside
it and scan every cached road and terrain map through reflection-compiled delegates on every tick to find unbuilt ones. The
entries are classes (no boxing), so the 563 KB/s may be Performance Log's coarse attribution landing on the first singleton
after the entity ticks; confirm before believing it.

Correction (2026-09-22): measured offline, the scans are not the 563 KB/s. In the harness (.NET 8, `tests/RouteMapsTests.cs`
with the game's real road cache, 418 cached maps and nothing to build) one `RouteMaps.NavigationTickedPostfix` scan takes
34.5 us (32 us again at 0.4.26) and allocates 80 B, which is about 1 KB/s at ten ticks per second: the allocation figure
belongs to something else. The time cannot be ruled out the same way, because tens of microseconds on .NET 8 can be more
under Mono and the terrain scan comes on top. The postfixes also do rebuild work, not only scans: `TerrainMaps` builds its
whole batch inside the postfix (on the main thread below four maps, otherwise waiting for its workers), and `RouteMaps`
builds a batch smaller than `RouteMapsMinFields` on the main thread. So read step (1)'s Watch against the stats lines'
`TerrainMaps: ... the main thread waited` and `RouteMaps: ... main thread spent` figures: a dirty flag saves only the scan
part. (0.4.25 shipped step (2): `MapChanges` sets the flag, and each cache is still walked every 200th tick as a check;
since 0.4.26 that check only counts the maps the hooks missed and leaves them to the game.)

Design: (1) `Watch = LateGamePerformance.RouteMaps.NavigationTickedPostfix; LateGamePerformance.TerrainMaps.
NavigationTickedPostfix` in `PerformanceLog.cfg` for one session: ms and KB per call. (2) If they are the cost, scan only when
something could have made a map unbuilt: a dirty flag set by postfixes on `FlowFieldCache.StartCachingAtNode` (a new entry),
`FlowFieldCache.OnNodesChanged` (maps cleared) and the nav-mesh update notification, cleared after a scan. Exact: the set of
cached unbuilt maps changes only through those methods (read `FlowFieldCache`, `RoadFlowFieldCache`, `TerrainFlowFieldCache`
and `AccessFlowField.OnNodesChanged` to enumerate every way a map becomes unbuilt or cached, and hook each). Verify: a
periodic full scan in verify mode that must find nothing the flag missed.

### H. Beaver ticks: measure by root behaviour, then two exact trims

Evidence: `BehaviorManager` 5.7 ms per tick and 267 KB/s of garbage (28% of all allocation), `WalkerMover` 1.5 ms and
118 KB/s, `NeedManager` 0.6 ms. About 7 ms of the entity tick is unattributed. Beaver ticks are the only cost that grows with
the colony, so they matter most for 400 to 500 beavers, and nothing measured so far says where inside a decision the time goes.

Design: (1) One session with `RecordTimings = true` (the game's `TimerMetricCache<RootBehavior>` gives time per root behaviour
per beaver) and the `Diagnostics` box on (`PickBestAction` calls, ms and longest); read the CSV in
`Documents\Timberborn\LateGamePerformance`. (2) The garbage: `BehaviorManager.SetRunningBehavior` formats a string
(`$"{name} {PartialDayNumber:0.00}"`) into a `CyclicBuffer<string>` of 10 on every behaviour change; the buffer is saved and shown
in the entity panel. Store (behaviour, day as float) pairs in a ring of this mod's own and materialise the strings only when
`Save` or the panel reads them (patch `SetRunningBehavior` to skip the game's add, patch `Save` and the `TimestampedBehaviorLog`
getter to fill the game's buffer first; loaded strings are kept as strings). Byte-identical saves because the same format is
applied to the same float. Verify: compare a save written with and without the feature (the `Verify save snapshots` box
covers it if the materialisation happens before the snapshot). (3) `WalkerMover`'s 118 KB/s: read `PathFollower.MoveAlongPath`
and `WalkerSpeedManager.GetWalkerSpeedAtCurrentPosition` for per-tick allocations (a closure or a boxed struct is likely).
(4) Skipping empty need-behaviour groups in `DistrictNeedBehaviorService.AppraiseNeedBehaviors` is exact (the sorted set's
comparer is consistent with the total order "points descending, insertion descending", so leaving out a group that can never
be picked changes nothing else) but worth under 0.1 ms per tick; only if the timers show many empty groups.

Not exact, never: ticking beavers on threads, deciding less often, straight-line pruning of need providers (ziplines).

### I. AnimatorRegistry: 1.3 ms per frame, the largest per-frame system (rendering only)

Evidence: `AnimatorRegistry` 124 ms/s at 96 fps; `AnimatorCulling: 0 of 2548204 animator updates left out` when the camera saw the
whole colony. Every tree, crop and building animator (thousands) gets `UpdateAnimation` every frame.

Design: keep `UpdateTime` every frame for every animator (the simulation reads animator time through `WonderAnimationController`,
`ClutchModel`, `AnimationParticlesTrigger` and `AnimateExecutor`, so time must advance exactly as before), and write the pose
(`UpdateAnimationUpdaters`) every Nth frame for animators whose renderer bounds are small on screen or far from the camera
(distance from `CameraService`'s camera to the cached renderer bounds; N = 2 beyond one distance, 4 beyond another; always every
frame for anything the camera is close to). One stale pose frame is invisible at that size. Rendering only, so it may be a
setting with a sensible default; put the thresholds in the cfg. Measure with the `AnimatorCulling:` line extended to count
pose writes skipped by distance, and the Performance Log row for `AnimatorRegistry`. Target: under 0.6 ms per frame zoomed out.

### J. The 3 GB heap: find what holds it, because every collection pays for it

Evidence: heap in use 2.9 to 3.3 GB, reserved 3.45 GB, process 7 GB. Collections are rare (0.5 per minute) but each costs 100
to 190 ms even incrementally, and a full one 0.9 to 1.3 s. A rough count of what should be live (entities and components,
flow fields, nav graphs, water and soil arrays, UI) comes to well under 1 GB, so either something retains large data or the
estimate is wrong.

Design: (1) A settings-page button, diagnostics only, "Measure live memory (freezes the game for a second)": `GC.Collect()` then
log `GetMonoUsedSizeLong` and the process working set; press it once after loading and once after an hour. (2) Sessions that
change one thing each and compare the live figure: Performance Log off (its buffers were 147 MB at load, and deep sampling keeps
per-component tables), BeaverBuddies `VerboseLogging` and desync tracing off (trace strings accumulate), the multi-colony
fork's `ColonyReach` grid. (3) Try `Unity.Profiling.Memory.MemoryProfiler.TakeSnapshot` from a mod behind a setting: if the
release player writes a `.snap`, it opens in any Unity 6 editor's Memory Profiler package and names every object by type;
if it refuses, say so and stop. (4) Ask the game's data structures: `RoadFlowFieldCache` and `TerrainFlowFieldCache` entry
counts and each `AccessFlowField`'s array sizes (a map that keeps arrays sized to the whole graph, times hundreds of maps, is
the likeliest big block); the BeaverBuddies replay buffer since load. Report sizes before proposing any change; a change here
is only worth it if a single block is over 300 MB.

### K. Unity's 7 ms per frame: attribute it before deciding anything

Evidence: `otherMs` 6.98 ms per frame (62%), 18,147 draw calls, 173 set-pass calls, 7.3 M triangles, render thread 5.7 ms,
graphics card 6.4 ms. The main thread's share is culling and draw submission, animation and UI Toolkit.

Design: extend Performance Log (its `ProfilerRecorder` source already records draw calls, set-pass calls and triangles) with a
configurable list of marker names sampled per frame, and a one-time dump of `ProfilerRecorderHandle.GetAvailable()` in the
session folder so the markers the release player exposes are known. Candidates: `Camera.Render`, `Culling`, `Shadows.Draw`,
`RenderForward`/`DrawOpaqueObjects`, `Gfx.WaitForPresentOnGfxThread`, `UIR.ImmediateRenderer`/`UIElements.*`,
`ParticleSystem.*`, `Animators.Update`, and the draw-call counters separately (`Standard Draw Calls Count`, `SRP Batcher
Draw Calls Count`, `Standard Instanced Draw Calls Count`, `Shadow Casters Count`). Then decide with numbers: if shadow casters
are near half the draw calls, a rendering-only option to stop small objects casting shadows beyond a distance is cheap; if the
standard (unbatched) draw calls dominate, note it as the engine's floor and stop. Do not build any rendering change before this
attribution exists.

### L. DistrictCitizenAssigner: 0.28 ms per tick, one 6 ms call

Evidence: `DistrictCitizenAssigner` 2.66 ms/s. Its tick copies the unassigned-citizen set and, for each, asks every finished
district centre `IsGloballyReachableFromCitizen` (a path query) and `DistanceToCitizen`. A steady 0.28 ms means citizens are
unassigned on most ticks and re-queried every tick.

Design: (1) Diagnostics counter: unassigned citizens per tick and district centres per query. (2) If a few citizens stay
unassigned for long (cut off, in another colony's area), memoise the answer per citizen keyed on (the citizen's node id, the
nav-mesh update counter, the finished-district-centre list version): same inputs, same output, so exact; invalidate on
`OnNavMeshUpdated` (the class already receives it) and on district changes. Verify mode runs the game's query alongside.
Read `Citizen.AssignDistrict`, `DistrictCenter.IsGloballyReachableFromCitizen` and `DistanceToCitizen` first for other inputs.

### M. Smaller, with numbers to justify each

- `WaterRenderer` 0.38 ms per tick: split `UpdateMeshAndTextures` (texture uploads vs mesh) with a Watch; the `WaterRendering:` line
  already shows half the uploads skipped; the rest is likely the floor.
- `SoilContaminationService` 0.22 ms per tick is 2,645 changed cells at 83 ns each: the game's own per-cell texture write.
  A prefix on `TerrainMaterialMap.SetSoilContamination` that skips a write of an unchanged texel is exact for rendering if the
  method does nothing else (read it); gain under 0.1 ms.
- `StatusAggregator` 0.4 ms per real run: make the alert lists event-driven (rebuild when a status appeared or cleared) instead of
  every fourth frame; interface only.
- BeaverBuddies fork: `ConnectionPanelService` 68 KB/s of garbage per frame (chat text rebuilt each frame) and `ReplayService`
  one 15 ms call; `ColonyReach` 116 ms once at load. Small; belongs in that repository.
- `TimeTriggerService` one 30 ms call per session (a day change firing many triggers): look once at what fires; probably leave it.
- Loading (20 s): `EventBus` post-load 3.1 s and `BottomBarPanel` 2.9 s are the surprising ones; Performance Log could attribute
  post-load handlers by subscriber. Only if the owner asks.

### N. Ask the owner for the other computer's recording

The guest (Ryzen 3600, vsync on in earlier sessions) is the frame-limited machine. Its Performance Log folder from the same
session says whether it is graphics-card-bound (`ftGpu` near the frame time), main-thread-bound, or waiting to present; each
has a different remedy, and nothing above should be tuned for the host alone.

## 5. Measurement protocol for the next sessions

1. **Correctness session** (once, 30 minutes, single player is fine): every verify key on (`HaulCacheVerify`,
   `YielderSearchVerify`, `PlantWaterVerify`, `DistrictCountsVerify`, `WaterMapCopyVerify`, `SoilScansVerify`,
   `HomeSearchVerify`, `TerrainSearchVerify`, `SaveSnapshotVerify`), then `grep "verify mismatches" Player.log`: every line must
   say 0. None of the verify modes has ever been run inside the game. Do this before any new simulation change.
2. **Measurement session** (both computers, same save, 20 minutes at speed 7, window in front): Performance Log `Profile = deep`,
   `RecordTimings = true`, the `Diagnostics` box on, no verify keys. Collect: both Performance Log folders, both `Player.log`s.
3. After each release: the same measurement session, and compare with `python tools/perflog.py compare <before> <after>` from
   the Performance Log repository. Report per-tick and per-frame deltas, not impressions.
4. **Save benchmark** (added 2026-09-22; repeatable save timing without playing, single player): the game's own
   `SavingBenchmarker` (`Timberborn.Benchmarking.dll`) runs when the game is started with `-settlementName X -saveName Y
   -benchmarkSaveCount 20 -benchmarkWarmUpLength 30 -skipModManager` and without `-benchmarkLength` and `-benchmarkSpeed`
   (given both, the game runs its ordinary benchmark instead). `-skipModManager` loads the mods enabled in the mod manager
   without waiting on its screen; the ordinary benchmark (`-benchmarkLength`) starts the game with every mod switched off,
   so it measures the game alone, while this one measures the mods. It loads the save, waits the warm-up in seconds,
   finishes the tick, saves 20 times into memory (`GameSaver.BenchmarkSavingToMemory`), writes `Finished saving
   benchmark:` with the average, median, 90th percentile, minimum and maximum to `Player.log`, and quits. Those saves go
   through `SaveWithoutFinishingTick` and `SaveWriter.WriteToSaveStream`, so this mod's snapshot workers
   (`SerializedWorldFactory.Create`) are measured, Background save is not involved (it only takes queued saves), and the
   Save timing feature logs one `Save to a stream` line per save with the stages split. `tools/benchmark-timberborn.ps1
   -Settlement X -Save Y -SaveCount 20` launches it (it starts the game through Steam; `-WarmUpSeconds` sets the warm-up).
   Use it for item A's decision beside the `Save:` lines of a played session.

## 6. Definition of done for a round

- The harness prints `ALL PASSED` with the new patch count and check count in `README.md`.
- Every simulation change has a verify mode and a test against the game's real classes; the release note names them.
- `TECHNICAL.md` has a section per feature (what, why exact, tests, settings, stats line), `README.md` a bullet and the counts,
  the website (`docs/`) a mention for previews and a full update when a version becomes the latest release.
- A pre-release on GitHub, then the owner plays it; only then is it flipped to latest, with docs saying it was played.
