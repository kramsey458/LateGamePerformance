# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Timberborn players whose colony has grown big enough to hitch: typically hundreds of beavers late in a game, lag spikes
when roads change or haulers pile up, a freeze of most of a second about once a minute, a long pause at every autosave.
Some play single player; many play co-op through BeaverBuddies and arrive asking first "will this desync us?". Mostly
non-technical, but this audience reads log lines when asked to and a fair share are testers who will tick a verify box
for a session. They find the mod through the Timbermods org site, the GitHub repo, a friend or a co-op partner, want to
know in one look whether it helps *their* colony, install it without breaking the game, and confirm it is running.
Returning players come back to update (co-op groups together), to try a preview, or to troubleshoot a line in
`Player.log`.

## Product Purpose

The website for **Late Game Performance** (https://github.com/timbermods/LateGamePerformance), a Timberborn 1.1 mod
that gives big colonies fewer lag spikes by removing work the game repeats over and over: it keeps answers the game has
already worked out (hauling job lists), does heavy parts on other CPU cores (route maps after road changes, plant water
checks, soil scans, the water map copy, most of each save), skips lookups that cannot change the result (tree and plant
search, entities with nothing to tick), stops a long frame's catch-up from turning one hitch into a second of stutter,
and offers optional incremental garbage collection. Beavers make the same choices they would have made anyway, just
sooner. It is aimed at large colonies; a small colony has little repeated work to remove.

Success, in order:
1. **Understand it:** the visitor sees what it changes (speed, not decisions), that it targets big colonies, and that
   it is safe in co-op, and reads the honest played/not-played picture.
2. **Install it right:** Harmony and Mod Settings present, the ZIP from Assets (not Source code), one
   `LateGamePerformance` folder in `Documents\Timberborn\Mods` (no doubled folder, older folder deleted, game closed),
   enabled and restarted; in co-op every player on the same mod version and game version.
3. **Use it:** confirm it runs from the `[LateGamePerformance]` startup lines, decide on **Incremental garbage
   collection**, optionally run a verify session.
4. **Report problems:** a GitHub issue with mod and game versions, single player or multiplayer, other mods, the
   `[LateGamePerformance]` lines (every player's log in co-op), and whether it stops without the mod.

## Positioning

The base game redoes the same calculations every time a hauler looks for work, a road is finished, a lumberjack looks
for a tree or a save starts, and in a big colony that is felt as stutter. This mod changes **how quickly the game finds
its answers, not what the answers are**: every simulation change is built to give the game's own result, is tested
against the game's own classes, and has a verify mode that runs the game's code alongside it. The one documented
difference: route maps are filled before the game first asks for them, so a few code paths that use a map only if it is
already filled can take the cached route (and resumed terrain searches can pick an equally short different route); both
are the same for every player, which is why they are always on rather than settings.

Versus neighbouring mods, truthfully: it is built to run under **BeaverBuddies** co-op (the README links thomaswp's
original; the repo does not document the Stability Fork separately, so the site should not claim Fork-specific
testing): its prefixes run last so other mods' prefixes on the same methods run first on every computer, and it has
reviewed and accepted **Timber Together**'s saving code and **MixedStorage**'s capacity rule so those stay on
worker threads. It does not claim to beat or replace any other performance mod, and the site should make no such
comparison. On the rendering side it only trims a few chores (water rendering uploads, audio listener, UI refresh,
off-screen and far animation, physics sync); Unity's own rendering work is most of a late-game frame and is untouched,
so a frame rate bound by rendering gains little. The site says so where it matters (troubleshooting "I don't notice
any difference").

## Operating Context

- **Current release:** **0.4.30**, the Latest release (made Latest 2026-09-23). It is 0.4.29 plus terrain searches kept
  per start tile (up to 32, capped at 32 MB) for the ~100 ms need-pick hitches, a compiled search loop, and acceptance
  of Timber Together 1.4.0-beta12's `ColonyStamp.Save`; 0.4.29 is 0.4.28 without the walking replacement (PathFollow).
  There is no preview at the moment. Previews live on the same Releases page marked **Pre-release**.
- **Game:** built against Timberborn **1.1.2.4** (`MinimumGameVersion` 1.1.2.4). If a game update moves a method a
  feature needs, that feature does not start, the game runs its own code, and the startup line says OFF.
- **Requires:** **Harmony 2.4.1+** and **Mod Settings** (`eMka.ModSettings` 1.1.0.0+), both from the Steam Workshop.
  The mod itself is a GitHub ZIP only (not on the Workshop). Folders shown are the Windows ones.
- **ZIP layout:** `LateGamePerformance\` with `LICENSE`, `README.md`, `version-1.1\` (`LateGamePerformance.cfg`,
  `manifest.json`, `Scripts\LateGamePerformance.dll`). Stores nothing in saves; uninstall = disable and restart.
- **Co-op:** every player installs the **same mod version** and runs the **same game version**; nothing else has to
  match, because **nothing that affects the simulation is a setting**. The startup line reads the same on every
  computer; for 0.4.30 it lists twelve simulation features:
  `Simulation features: HaulCache on, RouteMaps on, YielderSearch on, TerrainMaps on, PlantWater on, DistrictCounts on,
  WaterMapCopy on, SoilScans on, TerrainSearch on, IdleEntities on, HomeSearch on, Reachability on.`
  Incremental garbage collection, thread counts, timing logs and the verify settings may differ between players. All
  co-op players update together.
- **In-game settings** (the Mods list, main menu or Esc in a game → the settings button beside Late Game Performance;
  exact labels): **Incremental garbage collection** (the one real player choice: adds `gc-max-time-slice=3` to
  `Timberborn_Data\boot.config`, backup `boot.config.before-incremental-gc.bak`, restart needed, startup line
  `GC: incremental=True`); **Diagnostics timers**; **Verify terrain path searches**; **Verify save snapshots**; **Verify
  every feature (one test session)**; **Measure live memory now (freezes the game for a moment)**; **Write a memory
  snapshot file (slow, large)** (the last two act once and untick themselves). Nothing on the page affects the
  simulation. Everything else is in `version-1.1\LateGamePerformance.cfg` (verify keys, thread counts, timing lines,
  `RecordTimings`).
- **Dialogs players may meet:** a main-menu notice when incremental collection is off (*Turn it on* / *Not now*, asked
  once), and a dialog if a feature turns itself off after an error, asking every co-op player to restart.
- **Logs and reporting:** `%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\Player.log` (`Player-prev.log` is the
  session before); search `[LateGamePerformance]`; a stats line every 1,000 ticks, a `Save:` line per save; in verify
  sessions look for `verify mismatches 0`. Issues: https://github.com/timbermods/LateGamePerformance/issues.

## Capabilities and Constraints

- **Stack and hosting:** plain static HTML, CSS and small vanilla JS in `docs/` on `main`, no build step. GitHub Pages
  (legacy build) serves `main` `/docs` at https://timbermods.github.io/LateGamePerformance/; merging to `main`
  publishes. `docs/.nojekyll` must stay. Pages: `index.html`, `install.html`, `troubleshooting.html`, `faq.html`,
  `404.html`, plus `style.css`, `site.js`, `release.js`, `favicon.svg`, `fonts/` (Saira Semi Condensed 700/800,
  self-hosted, OFL) and `textures/` (three procedural WebP surfaces from `make_textures.py`). Fast, lightweight,
  mobile-friendly, no external fonts or CDNs at runtime.
- **Site tests and CI: none.** The repo has no `.github/workflows/`, and the C# harness in `tests/`
  (`dotnet run --project tests -c Release`; 509 checks / 129 patch targets for 0.4.29, 523 / 131 for 0.4.30 (the current release)) checks the
  mod against the game's assemblies and never reads `docs/`. The contracts below are therefore unenforced; every site
  change must keep them by hand (and could add a site check like MixedStorage's `tests/test-site.mjs` only if asked):
  1. `docs/release.js` is the shared Timbermods release script, byte-identical to MixedStorage's copy (SHA-1
     `f771fa55eea5db13c26043fdde5a374d1944aa0b`). **Replace it with a newer shared copy, never edit it.** It lives at
     `docs/release.js` here (not `assets/`); TECHNICAL.md's Website section documents that path.
  2. Every content page (index, install, troubleshooting, faq) loads it last with
     `<script src="release.js" defer data-repo="timbermods/LateGamePerformance" data-asset="^LateGamePerformance-[\d.]+\.zip$">`,
     after `site.js`. It fills `data-release="version|tag|asset-name|sha256"`, `data-release-href="download|notes"`,
     `data-release-show="prerelease|stable"`, and appends a note to `data-release-pinned="X"` elements when the Latest
     release is not X. It reads only the release GitHub marks Latest (pre-releases ignored), caches 30 minutes in
     `localStorage` (`tbmods.release.v1.timbermods/LateGamePerformance`), writes only text and `href`.
  3. The HTML keeps working fallbacks: download links point at `/releases/latest`; the version (`0.4.30`), tag, ZIP
     name `LateGamePerformance-0.4.30.zip` and SHA-256 `3393e5ab…7a45ccb7` are written in by hand. Current uses:
     the index hero and closing Download buttons (`tag`), install step 3 and `#checksum` (`Get-FileHash` line and hash), the
     install and troubleshooting `loading.` log lines, troubleshooting `#crash`.
  4. Text written for one build carries `data-release-pinned="0.4.30"`: the index hero notice, the index `#status`
     head, and the FAQ "How much faster" answer. When a new Latest ships, rewrite that text and bump the attribute.
  5. `404.html` loads `style.css` and `favicon.svg` and links by absolute `/LateGamePerformance/` paths, is
     `noindex`, and loads no scripts except the theme snippet.
  6. Theme: an inline head snippet reads `localStorage` key `lgp-theme` into `data-theme` before CSS loads; `site.js`
     drives `#theme-toggle` (label "Dark mode"/"Light mode") and the chart tooltips (`.chart-card .tip`, rows with
     `data-name`/`data-value`/`data-color`/`data-desc`), plus the timing tower's one-time switch-on (`[data-tower]`) and
     opening the `<details>` a link's hash points at. Everything works without `site.js`: charts have direct labels
     and a "View chart data as a table" `<details>`.
  7. Inbound links that must keep resolving: README links `index.html#status`, `install.html`, `install.html#checksum`,
     `faq.html`; the site links `faq.html#same-results`, `faq.html#timing`, `install.html#gc`, `install.html#layout`,
     `troubleshooting.html#off`, `#report`, `index.html#features`, `#coop`. Keep existing ids (also the log-line and
     symptom ids in troubleshooting).
- **Release loop for the site (PLAN.md §6):** a preview gets a mention on the site; the full site update happens when
  a version becomes Latest, after the owner has played it, with docs saying it was played.
- **Sources of truth:** `README.md`, `TECHNICAL.md` (every feature, setting, log line; its Website section), the
  release notes. There is no CHANGELOG file: version history lives in the release notes and TECHNICAL.md's "new in"
  markers. Where site and README disagree, flag it.
- **Site and README agree** as of the Pit Crew redesign (2026-09-23): every copy of the startup line lists the twelve
  0.4.29 features (unchanged in 0.4.30), player pages carry no version tags or version histories, and the README's played-session wording is
  consistent. One small wording difference is left on purpose: `install.html` says "It is a young mod, so try it on a
  copy of your save" where the README says "It is still a young mod, so try it on a copy of a save first".
- **Standing rules:** player pages describe the mod **as it is now**; version history belongs in the release notes and
  TECHNICAL.md, keeping only upgrade facts players need (close the game, delete the old folder, co-op updates
  together; an old `.cfg` with `HaulCache`, `HaulCacheFlushEveryTicks`, `RouteMaps`, `YielderSearch` or
  `PathFollowVerify` is ignored and harmless; 0.4.3 crashes on load and must be replaced). Never invent claims,
  especially performance numbers.
- **Terminology** (as in game, README and TECHNICAL): Late Game Performance (display name; folder, DLL, log tag
  `LateGamePerformance`); hauling job list cache; route maps (rebuilt on worker threads); tree and plant search; terrain
  route maps; terrain path searches (resumed); incremental garbage collection / memory clean-up; verify settings (verify
  modes, "only measure"); **Verify every feature**; startup line / `Simulation features:` line; stats lines; `Timing:`
  line; "turned itself off"; worker threads; main thread; **preview** (a GitHub pre-release) vs **latest release**.
  Feature names in logs are exact identifiers (HaulCache, RouteMaps, YielderSearch, …): never paraphrase them in code
  blocks.
- **Honest status** (0.4.30): played in a ~350-beaver late-game save (0.4.8 noticeably fewer lag spikes, 0.4.14
  "working well", 0.4.23 "working amazingly well"); 0.4.28 played in a 362-beaver colony (saves 325–430 ms instead of
  0.8–1.3 s) and again with **Verify every feature**: every verify mode reported 0 differences, the save snapshot at an
  autosave included. 0.4.30 has been played by the owner on a second computer; its logs have not been read, so its
  effect on the need-pick hitches is unmeasured. Played in multiplayer on two computers (two players only; larger
  groups unreported). **Not yet verified:** the frames that get slower each evening (unexplained); a co-op game with
  verify on for one player and off for another; the main-menu incremental-collection notice; any controlled
  frame-rate benchmark. Say this plainly, with "try it on a copy of a save", without alarm.

## Brand Commitments

- **Voice:** a fellow player explaining a useful mod. Clear, exact, calm, never hype. "Straight answers, including the
  ones that aren't flattering." Numbers only where they were measured, each with where it came from (test harness or a
  named played session) and what it does not promise. Short and plain: one idea per sentence, each thing said once, no
  internals and no history (see CLAUDE.md, *Writing README and website text*).
- **No official Timberborn logos or key art.** The game's own item icons are allowed where used; the current site uses
  none, and the favicon is the mod's own frame-time mark (`docs/favicon.svg`: purple tile, spiky line flattening out).
- **License:** MIT (the mod's code, docs and site). Credit Mechanistry for any game icons used.
- **Unofficial community mod**, not affiliated with or endorsed by Mechanistry. Maintained by **Timbermods**
  (https://github.com/timbermods); footer links "More mods from Timbermods" to https://timbermods.github.io/.
- **Co-op respect:** BeaverBuddies is thomaswp's project; name it accurately and never imply endorsement.

## Evidence on Hand

- **Images:** `docs/favicon.svg` and three procedural textures (`docs/textures/concrete.webp`, `asphalt.webp`,
  `board.webp`, each with a provenance sidecar). The old hero's illustrative frame-time line was removed in the
  redesign; the hero now shows the pit board with one measured figure, and no drawn curve that isn't real data may
  return. **No in-game screenshots, no video, no profiler captures, no frame-rate graphs exist.** The site has no image
  slots; add the maintainer's own shots (a big colony, the settings page, a `Player.log` excerpt) only when he
  provides them, never faked.
- **Real numbers, test harness** (not a live game): tree search in a model forest of 2,000 marked trees with 50 grown,
  2,000 path lookups vs 51; 420 route maps on a 22,600-tile road network, about 1.8 s one at a time vs about 0.23 s on 7
  workers (harness network is heavier than a real colony's); 9.5 million route-map nodes compared, all identical; 0
  differences from a model of the game's tree search in 4,000 random forests; resumed terrain searches, 1,200 searches
  all at the game's distance (88% bit for bit, the rest within rounding), half the tiles explored. 0.4.30 only:
  need-pick pattern 1.38 M tiles vs 3.37 M (59% fewer), 4,817 bit for bit + 463 within float rounding, 0 different.
- **Real numbers, played:** incremental GC, one 35-minute two-computer multiplayer session: 39 freezes, median 675 ms
  (~30 s total) without, one frame over 50 ms (outside saves) with; two different computers, so hardware counts too;
  1.6–2.7 GB in use. 0.4.28 vs the 0.4.23 recording (362 vs 359 beavers): autosave freeze 0.84–1.26 s → 325–430 ms;
  entities saved on the main thread 3,670 → 411; plant water 0.31 → 0.03 ms per tick; hauling list 0.73 → 0.24 ms per
  request; animations 1.64 → 0.87 ms per frame; beaver decisions 9.3 → 8.6 ms per tick. In the verify session, hauling
  lists 0.24 ms vs the game's 0.94 ms per request; district counts 0.56 vs 0.68 ms. The overall tick time did not
  measurably drop in that short session; say so if the table is shown. Qualitative reports from the ~350-beaver save
  (quoted phrases above). A 0.4.23 profile in PLAN.md §2 (11.2 ms mean frame, Unity's own work 62% of it) explains why
  a rendering-bound game gains less.
- **Does not exist, never fake:** FPS before/after or "X% faster" figures, a controlled benchmark, testimonials or
  quotes beyond the reported phrases, download or player counts, press, Workshop pages, player screenshots.

## Product Principles

1. **Same answers, sooner.** Lead with what never changes (the game's decisions, co-op sync) as much as with what gets
   faster; the one documented difference is stated, not hidden.
2. **Measured, sourced, bounded.** Every number names its source (harness or a named session) and its limits; nothing
   is extrapolated to "your colony". No number is better than an invented one.
3. **Co-op safe by default.** Same mod version, same game version, update together: never buried. What may differ
   between players is listed just as clearly.
4. **The log is the proof.** Show players the exact lines to look for (startup line, `GC: incremental=`, `verify
   mismatches 0`, OFF) so they can check the mod and their colony themselves.
5. **Honest about what's been played, described as it is now.** Played and not-yet-played are both stated plainly;
   version history stays in the release notes.
