# Late Game Performance

Fewer lag spikes in big Timberborn colonies. This Timberborn 1.1 mod removes work the game repeats over and over, and it is built to be safe in [BeaverBuddies](https://github.com/thomaswp/BeaverBuddies) co-op.

[![Latest release](https://img.shields.io/github/v/release/timbermods/LateGamePerformance?label=latest&labelColor=172620&color=9a8be0&style=flat-square)](https://github.com/timbermods/LateGamePerformance/releases/latest) ![Timberborn 1.1.2.4](https://img.shields.io/badge/Timberborn-1.1.2.4-2a4034?labelColor=172620&style=flat-square) [![MIT license](https://img.shields.io/badge/license-MIT-2a4034?labelColor=172620&style=flat-square)](LICENSE)

**[Download](https://github.com/timbermods/LateGamePerformance/releases/latest)** · [Website](https://timbermods.github.io/LateGamePerformance/) · [Install guide](https://timbermods.github.io/LateGamePerformance/install.html) · [FAQ](https://timbermods.github.io/LateGamePerformance/faq.html) · [Technical details](TECHNICAL.md)

## What it does for you

In a big colony the game does the same work again and again, and you feel it as lag spikes and freezes. This mod keeps answers the game has already worked out, does heavy work on other CPU cores, and skips lookups that can't change the result. Your beavers make the same choices, just sooner.

- **Hauling job list cache.** Each building's hauling jobs are kept until something that feeds them changes. Haulers get the game's own list, in the game's order.
- **Route maps rebuilt in the background.** After a path, stair or platform is finished, route maps are rebuilt on up to 7 worker threads.
- **Faster tree and plant search.** Lumberjacks, gatherers and farmers skip path lookups for growing plants that can't change the answer.
- **Shorter save freezes.** Trees, crops, paths, levees and platforms, nearly all of a big colony, are saved on worker threads. The save file is the same.
- **Less work every tick.** Trees, crops and paths with nothing to do are skipped. Beavers off the roads continue their last terrain search instead of starting over. Plant water, soil, the water map, district counts, the home search and "can a beaver reach that?" redo less or run on other cores.
- **Less work every frame.** Sound, alerts, the selection panel, far and off-screen animation, physics and water rendering redo less. None of it touches the simulation.
- **One hitch stays one hitch.** After a long frame, such as an autosave, the missed simulation is spread over ordinary frames instead of crammed into the next few.
- **Incremental garbage collection (optional).** The game's memory clean-up can freeze a big colony for most of a second, about once a minute. One tick-box spreads that work over many frames.
- **Timing lines in the game log**, so you can measure your own colony.

## What to expect

- **0.4.30 is the latest release** and the recommended download. It has been played on a second computer, but the logs of that session have not been read yet.
- **0.4.31 is a preview**, not played yet. A lumberjack with no grown tree in reach stops looking sooner, with the same result. The **Diagnostics timers** also time each part of a beaver's path search and every game event.
- 0.4.28 was played in two sessions in a 362-beaver colony. Saves froze for 325 to 430 ms, against 0.8 to 1.3 s on an earlier build of this mod. The overall tick time did not measurably drop in that short session.
- With **Verify every feature** ticked in that colony, every verify setting reported 0 differences from the game's own code, the save snapshot included. The hauling list took 0.24 ms per request against the game's 0.94 ms.
- Earlier builds were played in a late-game save of about 350 beavers: noticeably fewer lag spikes, then "working well" and "working amazingly well".
- Incremental garbage collection was played in one 35-minute multiplayer session on two computers. Without it: 39 freezes, median 675 ms, about 30 s in total. With it: one collection frame over 50 ms.
- It is aimed at large colonies. In a small colony you are unlikely to notice much.
- How much you gain depends on your colony and your computer. There is no controlled frame-rate benchmark yet.

## Built for BeaverBuddies co-op

In multiplayer every player's game must calculate exactly the same thing. So every speed-up gives the game's own answer.

- **Nothing that affects the simulation is a setting.** The same version runs the same code on every computer.
- **The startup line** in `Player.log` (`Simulation features: …`) should read the same for every player.
- **Each player may choose** incremental garbage collection, thread counts, timing logs and the verify settings. They don't affect the simulation.

One difference from the unmodded game is known. Route maps are ready before the game first asks for them, so a few parts of the game can take the cached route instead of searching again. And when two routes are equally short, a beaver off the roads may take the other one. Both are the same for every player, which is why they are always on.

## Tested

- 523 automated checks pass against the installed game's own code, including 131 patch targets.
- Rebuilt route maps match the game's node for node. The tree and plant search matches a model of the game's search in 4,000 random forests. Resumed terrain searches give the game's distances.
- Played in multiplayer on two computers. Larger groups haven't been reported.

[TECHNICAL.md](TECHNICAL.md#what-the-tests-check) lists what the tests check.

Not yet done:

- The logs of the 0.4.30 session haven't been read, so its gain isn't measured.
- The frames that get slower each evening are still unexplained.
- The verify session covered about twenty minutes of play.

The full list is on the [website](https://timbermods.github.io/LateGamePerformance/#status). It is still a young mod, so try it on a copy of a save first.

## Install

1. Install **Harmony** (2.4.1 or newer) and **Mod Settings** from the Steam Workshop.
2. Download the ZIP from the [latest release](https://github.com/timbermods/LateGamePerformance/releases/latest) (currently v0.4.30), under **Assets**, not **Source code**. <!-- latest -->
3. Close Timberborn. Delete any older `LateGamePerformance` folder, then extract the ZIP into `Documents\Timberborn\Mods`. You should get one `LateGamePerformance` folder.
4. Start Timberborn, enable **Late Game Performance** in the Mods menu, and restart when asked.

**Playing co-op?** Every player installs the same version of the mod and runs the same game version. Update together.

To check it's running, search `%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\Player.log` for `[LateGamePerformance]`.

To turn on incremental garbage collection, open the **Mods** list (main menu, or Esc in a game), press the settings button beside **Late Game Performance**, tick **Incremental garbage collection** and restart the game.

The [install guide](https://timbermods.github.io/LateGamePerformance/install.html) covers previews, updating, uninstalling and [checking the download](https://timbermods.github.io/LateGamePerformance/install.html#checksum).

## More

- [Website](https://timbermods.github.io/LateGamePerformance/): install guide, troubleshooting and FAQ.
- [TECHNICAL.md](TECHNICAL.md): how each feature works, every setting and log line, and how to build and test the mod.
- [Issues](https://github.com/timbermods/LateGamePerformance/issues) for problems and questions.

## License

MIT. See [LICENSE](LICENSE). Maintained by [Timbermods](https://github.com/timbermods). An unofficial community mod, not affiliated with or endorsed by Mechanistry. BeaverBuddies is thomaswp's project.
