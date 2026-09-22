# Late Game Performance

Fewer lag spikes in big Timberborn colonies. Late Game Performance is a Timberborn 1.1 mod that removes work the game repeats over and over in large colonies, and it is built to be safe in [BeaverBuddies](https://github.com/thomaswp/BeaverBuddies) co-op.

[![Latest release](https://img.shields.io/github/v/release/timbermods/LateGamePerformance?label=latest&labelColor=172620&color=9a8be0&style=flat-square)](https://github.com/timbermods/LateGamePerformance/releases/latest) ![Timberborn 1.1.2.4](https://img.shields.io/badge/Timberborn-1.1.2.4-2a4034?labelColor=172620&style=flat-square) [![MIT license](https://img.shields.io/badge/license-MIT-2a4034?labelColor=172620&style=flat-square)](LICENSE)

**[Download](https://github.com/timbermods/LateGamePerformance/releases/latest)** · [Website](https://timbermods.github.io/LateGamePerformance/) · [Install guide](https://timbermods.github.io/LateGamePerformance/install.html) · [FAQ](https://timbermods.github.io/LateGamePerformance/faq.html) · [Technical details](TECHNICAL.md)

## What it does for you

In a big late-game colony the game does the same work again and again: finding jobs for haulers, rebuilding routes after every road change, searching for trees, cleaning up memory. You feel it as lag spikes and freezes. This mod keeps the answers the game has already worked out, does the heavy parts on other CPU cores, and skips lookups that cannot change the result. Your beavers make the same choices they would have made anyway, just sooner.

- **Hauling job list cache.** Every time a hauler looks for work, the game rebuilds and sorts a list of every hauling job in the district. The mod keeps each list until something that feeds it changes, and hands haulers the same list, in the same order.
- **Route maps rebuilt in the background.** After a road, stair or platform is finished, the game rebuilds the route map of every building with an entrance on the main thread. The mod spreads that over up to 7 worker threads. In the test harness, 420 maps took about 1.8 s one at a time and about 0.23 s on 7 workers.
- **Faster tree and plant search.** The game measures the path to every marked tree before asking whether it has anything to take, though in a late-game colony most are still growing. The mod leaves out the measurements that cannot change the answer: in a test forest of 2,000 marked trees with 50 grown, 51 lookups instead of 2,000.
- **More work on other CPU cores.** The water check on every plant, terrain route maps, each district's resource count, the water map copy and most of each save's work run on worker threads; the soil moisture and contamination scans skip tiles where nothing changed; water rendering stops re-sending what the graphics card already has. Each gives the game's own result.
- **Three more per-tick chores leave the main thread (0.4.23).** The water level of every plant is now read on the water worker right after the water map is copied there, so the tick only applies the changes; the list of soil cells whose moisture or contamination changed is written by the worker that finishes the soil simulation, instead of the main thread scanning the whole map for it; and the every-tick search for a beaver to move into a home asks the same beavers the same questions, in the same order, without the component lookup and the enumerators the game makes for each of them. Each gives the game's own result and has a verify setting.
- **Preview 0.4.26 (pre-release, not yet played): the next round from the logged session.** The tree search skips dead plants and, with a box measured once per terrain route map, plants the map cannot reach; the plant water tick matches the worker's levels against the game's list object by object, so a plant planted or cut no longer sends the whole tick back to the slow path; the home search keeps its lookups beside the game's own lists; the mod's own worker threads replace the thread pool for the district counts and the map batches; route map caches are walked only after something could have changed; beavers' behaviour log lines are formatted only when a save or the panel reads them, and their walk no longer allocates a delegate per tick; far-away animated objects write their pose every second or fourth frame; the save snapshot takes 63 more building parts and the BeaverBuddies fork's colony stamp, each guarded by a hash of its saving code and, since 0.4.26, by a look at Harmony's registry, so a component whose saving code another mod has patched stays on the main thread unless that patch has been read. Every simulation change gives the game's own result and has a verify mode; one settings-page box turns them all on.
- **Saves freeze the game for less (0.4.22).** The snapshot a save starts with was the last part of a save still on the main thread. Trees, crops, paths, levees and platforms, nearly all of a big colony, are now snapshotted on worker threads, using only components whose saving code has been read; beavers and workplaces stay on the main thread, which does its share alongside. The save is the same file.
- **Trees, crops and paths stop being visited every tick (0.4.21).** Every one of them is a tickable entity whose only tick part is switched off nearly all the time, yet the game visits all ten thousand of them on every tick. The mod ticks only entities with a part switched on, in the game's own order, and moves entities in and out the moment a part is switched. Same result, about a third of the tick's entity work gone.
- **Beavers off the roads stop re-searching the same ground (0.4.18).** A beaver standing in a field or forest that weighs up where to eat, drink or rest makes the game search the terrain once per building it considers, restarting from the same tile each time. The mod continues the previous search instead, so ground already covered is never covered again. Distances are the game's; when two routes are equally short the mod can pick the other one.
- **Less work per frame around menus and off screen (0.4.17).** The audio listener is placed only when the camera moved instead of casting rays every frame; the alert lists and the selected entity's panel refresh every few frames instead of every one; and animated objects that are entirely off screen keep their timing but skip writing a pose nobody sees. None of it touches the simulation.
- **No stutter tail after a hitch (0.4.16).** After a long frame such as an autosave or a memory clean-up, the game tries to run all the simulation that frame missed in the next one, so one 300 ms hitch became about a second of stutter in a logged session. The mod limits what one frame catches up; the same ticks still run, in the same order, just spread over ordinary frames.
- **Incremental garbage collection (optional, one tick-box).** The game cleans up memory with everything stopped, which in a big colony can freeze it for most of a second about once a minute. Incremental collection spreads the same work over many frames.
- **Timing lines in the game log**, so you can measure your own colony.

## What to expect

- Played in a late-game save of about 350 beavers: noticeably fewer lag spikes as 0.4.8, reported as working well as 0.4.14, and as 0.4.23, the latest release, reported as working amazingly well.
- Preview 0.4.26 (pre-release, not yet played) implements the plan drawn from the recorded 0.4.23 session ([PLAN.md](PLAN.md)): see the bullet above. 0.4.26 adds the guard against other mods' patches on the saving code that a code review of 0.4.25 asked for; 0.4.25 was never played. It includes 0.4.24's removal of the memory clean-up right after each save, which that session showed as a 0.9 to 1.3 s freeze at every save on a 3 GB heap. Two boxes on the settings page measure live memory and write a memory snapshot for the heap question the session left open.
- 0.4.23 (the latest release) brings together everything since 0.4.14 and has been played in that colony. What 0.4.21 to 0.4.23 add: entities with nothing to tick are left out of the tick loop (0.4.21); the save snapshot of trees, crops, paths, levees and platforms moves to worker threads, with a verify box, and the top bar and a selected entity's reachability check refresh less often (0.4.22); the plant water levels and the soil changed-cell lists are made on the workers that already hold the data, and the home search drops its per-beaver overhead (0.4.23). Two code reviews, before 0.4.20 and before 0.4.22, had their findings fixed. 0.4.15 to 0.4.20: terrain path searches from the same tile continue instead of restarting (0.4.18), aimed at the single slow beaver ticks and the end-of-day slump; after a long frame (an autosave, a memory clean-up) the simulation no longer catches up all at once (0.4.16); the audio listener and two interface systems stop redoing their work every frame, and off-screen animated objects skip their pose update (0.4.17); the district resource counts stay on worker threads with MixedStorage installed (0.4.15); the diagnostics timers and the terrain search check are boxes on the settings page (0.4.19). 0.4.10 to 0.4.14: one checkbox on the settings page; a faster tree search when lumberjack flags are full; plant water checks, terrain route maps and district resource counts on worker threads; autosaves and menu saves finish on a worker thread, so the freeze is roughly halved; the water map copy on a worker thread, faster soil scans and less water rendering work (0.4.14). See [TECHNICAL.md](TECHNICAL.md).
- Incremental garbage collection, in one 35-minute multiplayer session on two computers: 39 freezes with a median of 675 ms (about 30 s in total) on the computer without it, one collection frame over 50 ms on the computer with it.
- It is aimed at large colonies. A small colony has little repeated work to remove, so you are unlikely to notice much.
- How much you gain depends on your colony and your computer. There is no controlled frame rate benchmark yet.

## Built for BeaverBuddies co-op

Compatibility with BeaverBuddies has been the top priority from the start. In multiplayer every player's game must calculate exactly the same thing, so a speed-up is only worth having if it gives the game's own answer.

- Hauling lists and tree searches are the game's own results, and rebuilt route maps have exactly the game's contents. Resumed terrain searches (0.4.18) give the game's distances; an equally short route can differ, the same for every player.
- **Nothing that affects the simulation is a setting**, so every player on the same version runs the same code and there is nothing to keep in sync by hand. A startup line in `Player.log` reads the same on every computer.
- Which route maps get built never depends on what any single player's screen asked for.
- Memory clean-up, thread counts and timing logs do not affect the simulation, so each player can choose them separately.

One difference is documented: route maps are filled before the game first asks for them, so a few code paths that use a map only if it is already filled can take the cached route. That is why route maps are always on rather than a setting.

## Tested

Tested extensively against the game's own code and in play:

- 343 automated checks pass against the installed game's assemblies, including 119 patch targets (0.4.26 preview; 0.4.24 had 293 and 105).
- Rebuilt route maps are identical to the game's, node for node (9.5 million compared), and every map is complete when it is asked for.
- The tree and plant search matches a model of the game's search in 4,000 random forests, with 0 differences.
- Resumed terrain searches run against the game's real search classes on a random terrain: every search from scratch is identical node for node, and 1,200 searches in pricing runs all give the game's distance (88% bit for bit, the rest within rounding), exploring half the tiles.
- Played in multiplayer sessions on two computers, and in the 350-beaver late-game save, most recently as 0.4.23.

Not yet done: the stats lines of one 0.4.23 session have been read and the next round built from them as the 0.4.26 preview ([PLAN.md](PLAN.md)), but no feature has been measured before and after in the same save; the verify settings have not been run inside the game (0.4.26 adds one box that turns them all on for a session); and the tree search has not been compared against the game's own search inside a running game. It is still a young mod, so try it on a copy of a save first. The full list is on the [website](https://timbermods.github.io/LateGamePerformance/#status).

## Install

1. Install **Harmony** (2.4.1 or newer) and **Mod Settings** from the Steam Workshop.
2. Close Timberborn and extract the [release ZIP](https://github.com/timbermods/LateGamePerformance/releases/latest) into `Documents\Timberborn\Mods`. It contains one `LateGamePerformance` folder. Previews are on the same page as pre-releases; the current one, [0.4.26](https://github.com/timbermods/LateGamePerformance/releases/tag/v0.4.26), has not been played yet, so try it on a copy of a save.
3. Start Timberborn, enable **Late Game Performance**, and restart when prompted. Look for `[LateGamePerformance]` lines in `Player.log` (`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\Player.log`).

**Multiplayer:** every player installs the same version. That is all.

To turn on incremental garbage collection, tick **Incremental garbage collection** under **Mods > Late Game Performance** and restart the game. Details, updating and uninstalling are in the [install guide](https://timbermods.github.io/LateGamePerformance/install.html).

## More

- [Website](https://timbermods.github.io/LateGamePerformance/) with the install guide, troubleshooting and FAQ
- [TECHNICAL.md](TECHNICAL.md): how each feature works, every setting and log line, and how to build and test it
- [Issues](https://github.com/timbermods/LateGamePerformance/issues) for problems and questions

## License

MIT. See [LICENSE](LICENSE).
