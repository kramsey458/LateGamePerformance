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
- **Beavers off the roads stop re-searching the same ground (preview 0.4.18, not yet played).** A beaver standing in a field or forest that weighs up where to eat, drink or rest makes the game search the terrain once per building it considers, restarting from the same tile each time. The mod continues the previous search instead, so ground already covered is never covered again. Distances are the game's; when two routes are equally short the mod can pick the other one.
- **Fewer and smaller hitches around saves and menus, and less work per frame (preview 0.4.17, not yet played).** The memory clean-up that followed every autosave a second later now happens inside the save's own frame; the audio listener is placed only when the camera moved instead of casting rays every frame; the alert lists and the selected entity's panel refresh every few frames instead of every one; and animated objects that are entirely off screen keep their timing but skip writing a pose nobody sees. None of it touches the simulation.
- **No stutter tail after a hitch (preview 0.4.16, not yet played).** After a long frame such as an autosave or a memory clean-up, the game tries to run all the simulation that frame missed in the next one, so one 300 ms hitch became about a second of stutter in a logged session. The mod limits what one frame catches up; the same ticks still run, in the same order, just spread over ordinary frames.
- **Incremental garbage collection (optional, one tick-box).** The game cleans up memory with everything stopped, which in a big colony can freeze it for most of a second about once a minute. Incremental collection spreads the same work over many frames.
- **Timing lines in the game log**, so you can measure your own colony.

## What to expect

- Played in a late-game save of about 350 beavers (as 0.4.8): noticeably fewer lag spikes. 0.4.14 has been played since and reported as working well.
- Preview 0.4.19 (pre-release): the two measurements a tester is asked for, the diagnostics timers and the terrain search check, are boxes on the settings page; not yet played. Preview 0.4.18 (pre-release): terrain path searches from the same tile continue instead of restarting, aimed at the single slow beaver ticks and the end-of-day slump; not yet played. Preview 0.4.17 (pre-release): the garbage collection after each save moves into the save frame, the audio listener and two interface systems stop redoing their work every frame, and off-screen animated objects skip their pose update; not yet played. Preview 0.4.16 (pre-release): after a long frame (an autosave, a garbage collection) the simulation no longer catches up all at once, which in a logged session turned every such hitch into about a second of stutter; the opt-in diagnostics also time the game's own path searches; not yet played. Preview 0.4.15: the district resource counts stay on worker threads with MixedStorage installed, and BeaverBuddies' water desync trace sees every tick again; not yet played. 0.4.10 to 0.4.14 (the latest release): one checkbox on the settings page; a faster tree search when lumberjack flags are full; plant water checks, terrain route maps and district resource counts on worker threads; autosaves and menu saves finish on a worker thread, so the freeze is roughly halved; the water map copy on a worker thread, faster soil scans and less water rendering work (0.4.14). See [TECHNICAL.md](TECHNICAL.md).
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

- 258 automated checks pass against the installed game's assemblies, including 87 patch targets.
- Rebuilt route maps are identical to the game's, node for node (9.5 million compared), and every map is complete when it is asked for.
- The tree and plant search matches a model of the game's search in 4,000 random forests, with 0 differences.
- Resumed terrain searches run against the game's real search classes on a random terrain: every search from scratch is identical node for node, and 1,200 searches in pricing runs all give the game's distance (88% bit for bit, the rest within rounding), exploring half the tiles.
- Played in multiplayer sessions on two computers, and in the 350-beaver late-game save.

Not yet done: no stats lines from a 0.4.14 session have been read yet, and the tree search has not been compared against the game's own search inside a running game. It is still a young mod, so try it on a copy of a save first. The full list is on the [website](https://timbermods.github.io/LateGamePerformance/#status).

## Install

1. Install **Harmony** (2.4.1 or newer) and **Mod Settings** from the Steam Workshop.
2. Close Timberborn and extract the [release ZIP](https://github.com/timbermods/LateGamePerformance/releases/latest) into `Documents\Timberborn\Mods`. It contains one `LateGamePerformance` folder.
3. Start Timberborn, enable **Late Game Performance**, and restart when prompted. Look for `[LateGamePerformance]` lines in `Player.log` (`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\Player.log`).

**Multiplayer:** every player installs the same version. That is all.

To turn on incremental garbage collection, tick **Incremental garbage collection** under **Mods > Late Game Performance** and restart the game. Details, updating and uninstalling are in the [install guide](https://timbermods.github.io/LateGamePerformance/install.html).

## More

- [Website](https://timbermods.github.io/LateGamePerformance/) with the install guide, troubleshooting and FAQ
- [TECHNICAL.md](TECHNICAL.md): how each feature works, every setting and log line, and how to build and test it
- [Issues](https://github.com/timbermods/LateGamePerformance/issues) for problems and questions

## License

MIT. See [LICENSE](LICENSE).
