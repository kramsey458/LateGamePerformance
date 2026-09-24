# Late Game Performance

Fewer lag spikes in big Timberborn colonies. Late Game Performance is a Timberborn 1.1 mod that removes work the game repeats over and over in large colonies, and it is built to be safe in [BeaverBuddies](https://github.com/thomaswp/BeaverBuddies) co-op.

[![Latest release](https://img.shields.io/github/v/release/timbermods/LateGamePerformance?label=latest&labelColor=172620&color=9a8be0&style=flat-square)](https://github.com/timbermods/LateGamePerformance/releases/latest) ![Timberborn 1.1.2.4](https://img.shields.io/badge/Timberborn-1.1.2.4-2a4034?labelColor=172620&style=flat-square) [![MIT license](https://img.shields.io/badge/license-MIT-2a4034?labelColor=172620&style=flat-square)](LICENSE)

**[Download](https://github.com/timbermods/LateGamePerformance/releases/latest)** · [Website](https://timbermods.github.io/LateGamePerformance/) · [Install guide](https://timbermods.github.io/LateGamePerformance/install.html) · [FAQ](https://timbermods.github.io/LateGamePerformance/faq.html) · [Technical details](TECHNICAL.md)

## What it does for you

In a big late-game colony the game does the same work again and again: finding jobs for haulers, rebuilding routes after every road change, searching for trees, cleaning up memory. You feel it as lag spikes and freezes. This mod keeps the answers the game has already worked out, does the heavy parts on other CPU cores, and skips lookups that cannot change the result. Your beavers make the same choices they would have made anyway, just sooner.

- **Hauling job list cache.** Every time a hauler looks for work, the game asks every building in the district for its hauling jobs and sorts the whole list. The mod keeps each building's jobs until something that feeds them changes, then assembles and sorts the list the game's way, so haulers get the same list in the same order.
- **Route maps rebuilt in the background.** After a road, stair or platform is finished, the game rebuilds the route map of every building with an entrance on the main thread. The mod spreads that over up to 7 worker threads. In the test harness, 420 maps took about 1.8 s one at a time and about 0.23 s on 7 workers.
- **Faster tree and plant search.** The game measures the path to every marked tree before asking whether it has anything to take, though in a late-game colony most are still growing. The mod leaves out the measurements that cannot change the answer: in a test forest of 2,000 marked trees with 50 grown, 51 lookups instead of 2,000.
- **More work on other CPU cores.** The water check on every plant, terrain route maps, each district's resource count, the water map copy and most of each save's work run on worker threads; the soil moisture and contamination scans skip tiles where nothing changed; water rendering stops re-sending what the graphics card already has. Each gives the game's own result.
- **Saves freeze the game for less.** Trees, crops, paths, levees and platforms, nearly all of a big colony, are snapshotted on worker threads, using only components whose saving code has been read (a component whose saving code another mod has patched stays on the main thread unless that patch has been read); beavers and workplaces stay on the main thread, which does its share alongside. The save is the same file.
- **Trees, crops and paths stop being visited every tick.** Every one of them is a tickable entity whose only tick part is switched off nearly all the time, yet the game visits all ten thousand of them on every tick. The mod ticks only entities with a part switched on, in the game's own order.
- **Beavers off the roads stop re-searching the same ground.** A beaver in a field or forest weighing up where to eat, drink or rest makes the game search the terrain once per building, restarting from the same tile each time. The mod continues the previous search instead. Distances are the game's; when two routes are equally short the mod can pick the other one, the same for every player.
- **Less repeated work every tick.** District resource counts, the home search, the "can a beaver reach that?" flood after a road change and the hauling cache stop redoing unchanged work; the tree search for a full lumberjack flag stops at the first tree it finds.
- **Less work per frame.** The audio listener, the alert lists, the selected entity's panel, far and off-screen animated objects and the physics sync stop redoing their work every frame. None of it touches the simulation.
- **No stutter tail after a hitch.** After a long frame such as an autosave or a memory clean-up, the game tried to run all the simulation that frame missed in the next one, so one 300 ms hitch became about a second of stutter in a logged session. The same ticks still run, in the same order, spread over ordinary frames.
- **Incremental garbage collection (optional, one tick-box).** The game cleans up memory with everything stopped, which in a big colony can freeze it for most of a second about once a minute. Incremental collection spreads the same work over many frames.
- **Timing lines in the game log**, so you can measure your own colony.

## What to expect

- **0.4.30 is the latest release** and the recommended download. It has been played, on a second computer whose logs have not been read yet. It keeps up to 32 terrain searches by start tile (at most 32 MB, the same on every computer) for the need picks of about 100 ms seen in the verify session, compiles its search loop, and accepts Timber Together's newer saving code after a review. 523 automated checks pass, including 131 patch targets.
- The build before it was played as 0.4.28, the same code as 0.4.29 plus one walking replacement that gained nothing and was removed, in two sessions in a 362-beaver colony: saves froze for 325 to 430 ms instead of 0.8 to 1.3 s on an earlier build of this mod, and with **Verify every feature** ticked every verify setting reported 0 differences from the game's own code, the save snapshot included (the hauling lists took 0.24 ms per request against the game's 0.94 ms). The overall tick time did not measurably drop in that short session.
- Earlier builds were played in a late-game save of about 350 beavers: noticeably fewer lag spikes, then reported as working well and as working amazingly well.
- Incremental garbage collection, in one 35-minute multiplayer session on two computers: 39 freezes with a median of 675 ms (about 30 s in total) on the computer without it, one collection frame over 50 ms on the computer with it.
- It is aimed at large colonies. A small colony has little repeated work to remove, so you are unlikely to notice much.
- How much you gain depends on your colony and your computer. There is no controlled frame rate benchmark yet.
- All co-op players update together. Version history is in the [release notes](https://github.com/timbermods/LateGamePerformance/releases) and [TECHNICAL.md](TECHNICAL.md).

## Built for BeaverBuddies co-op

Compatibility with BeaverBuddies has been the top priority from the start. In multiplayer every player's game must calculate exactly the same thing, so a speed-up is only worth having if it gives the game's own answer.

- Hauling lists and tree searches are the game's own results, and rebuilt route maps have exactly the game's contents. Resumed terrain searches give the game's distances; an equally short route can differ, the same for every player.
- **Nothing that affects the simulation is a setting**, so every player on the same version runs the same code and there is nothing to keep in sync by hand. A startup line in `Player.log` reads the same on every computer.
- Which route maps get built never depends on what any single player's screen asked for.
- Memory clean-up, thread counts, timing logs and the verify settings do not affect the simulation, so each player can choose them separately.

One difference is documented: route maps are filled before the game first asks for them, so a few code paths that use a map only if it is already filled can take the cached route. That is why route maps are always on rather than a setting.

## Tested

Tested extensively against the game's own code and in play:

- 523 automated checks pass against the installed game's assemblies, including 131 patch targets.
- Rebuilt route maps are identical to the game's, node for node (9.5 million compared), and every map is complete when it is asked for.
- The tree and plant search matches a model of the game's search in 4,000 random forests, with 0 differences.
- Resumed terrain searches run against the game's real search classes on a random terrain: every search from scratch is identical node for node, and 1,200 searches in pricing runs all give the game's distance (88% bit for bit, the rest within rounding), exploring half the tiles. With searches kept for up to 32 start tiles, the need-pick pattern (5,280 questions with 48 terrain changes) explored 59% fewer tiles, with every distance the game's (4,817 bit for bit, 463 within rounding).
- Played in multiplayer sessions on two computers, in the 350-beaver late-game save, and in a 362-beaver colony with **Verify every feature** on (0 differences).

Not yet done: the logs of the 0.4.30 session have not been read, so its effect on the need-pick hitches is not measured yet; the frames that get slower each evening are still unexplained; the verify session covers about twenty minutes of play; and groups larger than two players haven't been reported. It is still a young mod, so try it on a copy of a save first. The full list is on the [website](https://timbermods.github.io/LateGamePerformance/#status).

## Install

1. Install **Harmony** (2.4.1 or newer) and **Mod Settings** from the Steam Workshop.
2. Download the ZIP from the [latest release](https://github.com/timbermods/LateGamePerformance/releases/latest) (currently v0.4.30), under **Assets**, not **Source code**. The [install guide](https://timbermods.github.io/LateGamePerformance/install.html#checksum) has its checksum. <!-- latest -->
3. Close Timberborn, delete any older `LateGamePerformance` folder, and extract the ZIP into `Documents\Timberborn\Mods`. It contains one `LateGamePerformance` folder.
4. Start Timberborn, enable **Late Game Performance** in the mod manager, and restart when prompted. Look for `[LateGamePerformance]` lines in `Player.log` (`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\Player.log`).

Previews, when there is one, are on the same Releases page, marked **Pre-release**.

**Multiplayer:** every player installs the same version of the mod and runs the same game version. Nothing else has to match.

To turn on incremental garbage collection, tick **Incremental garbage collection** under **Mods > Late Game Performance** and restart the game. Details, updating and uninstalling are in the [install guide](https://timbermods.github.io/LateGamePerformance/install.html).

## More

- [Website](https://timbermods.github.io/LateGamePerformance/) with the install guide, troubleshooting and FAQ
- [TECHNICAL.md](TECHNICAL.md): how each feature works, every setting and log line, and how to build and test it
- [Issues](https://github.com/timbermods/LateGamePerformance/issues) for problems and questions

## License

MIT. See [LICENSE](LICENSE).
