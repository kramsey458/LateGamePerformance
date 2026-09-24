# CLAUDE.md

Late Game Performance (timbermods/LateGamePerformance): a Timberborn 1.1 mod (built against 1.1.2.4, needs Harmony
2.4.1+ and Mod Settings) that removes repeated work in big colonies without changing any game result. Mod source in
`source/`, the C# test harness in `tests/`, packaging in `packaging/`, site in `docs/`. Changes land on main by PR →
merge. Sources of truth: README.md, TECHNICAL.md (every feature, setting, log line), PLAN.md §6, the GitHub release
notes. There is no CHANGELOG file and no CI (`.github/workflows/` doesn't exist).

- Build: `.\build.ps1` (dotnet build + ZIP into `dist/`). Never use `-Install` (it writes into the game's Mods folder).
- Test: `dotnet run --project tests -c Release` → must end `ALL PASSED`. It loads the installed game's assemblies from
  Steam and never starts the game (about a minute). Counts: 0.4.29 = 509 checks / 129 patch targets, 0.4.30 = 523 /
  131. It also hashes whatever BeaverBuddies MultiColony is *installed on this machine* (`ColonyStamp.Save`), so the
  result is machine-specific: with a MultiColony build newer than the one reviewed, 3 save-snapshot checks fail (seen
  on 2026-09-23) until that code is read and its hash added (`-- --hashes` prints it). That is not a repo defect.
  The harness never reads `docs/`.

## Standing rules

- Never launch or drive Timberborn, and never touch installed mods or saves. The maintainer (Kyler) playtests himself.
- Commit on a branch and open a PR. Kyler has said to merge PRs automatically: merge, then check the page live.
- Release loop (PLAN.md §6): a GitHub pre-release ("preview") first; the site gets only a mention of it. The full site
  update happens when a version becomes Latest, after Kyler has played it, with the docs saying it was played.
- Nothing that affects the simulation is ever a setting (co-op safety); don't write copy that implies otherwise.

## Website

- **Where:** `docs/`: `index.html`, `install.html`, `troubleshooting.html`, `faq.html`, `404.html`; shared
  `style.css`, `site.js`, `release.js`, `favicon.svg`, `fonts/`, `textures/`. Live at
  https://timbermods.github.io/LateGamePerformance/.
- **Published:** GitHub Pages (legacy build) serves `main:/docs`, so merging to main publishes (about a minute).
  `docs/.nojekyll` must stay. No build step.
- **Latest releases update themselves:** when a release becomes GitHub's Latest, `.github/workflows/latest-release.yml`
  (the shared timbermods workflow) appends the standard footer to its notes, sets the site's
  `data-release="version|tag|asset-name"` fallback text and the README lines ending in `<!-- latest -->` to the new
  version, runs the site checks and commits to main. Pre-releases change nothing. Descriptions, status lists and FAQs
  stay manual (the checklist below). Dry run: Actions → Latest release → Run workflow.
- **Look:** "Pit Crew". A pit lane at dusk: the race and car are the game's, the crew (the mod) only makes every stop
  shorter. Concrete by day, asphalt by night, painted yellow structure, purple for the mod's result. The look is
  fixed: updates extend it and never restyle it.
- **Design records (read these before any site change):**
  - `PRODUCT.md`: the facts, voice, honest status, and every site contract (release.js, pinned text, ids, 404).
  - `DESIGN.md`: the visual system and its named rules, the source of truth for the look.
  - `.impeccable/surfaces/docs-index-html.md`: the direction contract (pre-build; where it differs from DESIGN.md or
    the CSS, e.g. its purple #7657e6 or Saira 500, the shipped CSS and DESIGN.md win).
  - `.impeccable/design.json`: tokens and component snippets. `.impeccable/critique/`: the pre-redesign critique.

### Design rules (from DESIGN.md; keep them)

- **The Paint Is Structure Rule:** yellow #f2c230 paints lines, lanes, frames and numbers only (5px header/footer/section
  rules, the pit-box frame, station/step number squares, current-tab underline). Never a panel fill, never text on
  concrete, never a result.
- **The Fastest Lap Rule:** purple is the mod's figure and the way forward (links, Download). The game's figure is
  always grey (`--bar-game`), never red.
- **The Colour Carries A Word Rule:** green, amber and red always sit beside their word ("faster", "same", "on",
  "tested", "open", a warning heading).
- **The Numbers Are Display Rule:** every compared figure is Saira 800 with tabular numerals.
- **The Board Speaks In Capitals Rule:** spaced uppercase (0.06–0.1em) only on the pit board rows and the tower's "on"
  tags.
- **The Painted Not Lifted Rule:** no shadows except the pit board (board drop) and chart tooltips.
- Tokens live in `docs/style.css` `:root`; dark in `@media (prefers-color-scheme: dark) :root:not([data-theme="light"])`
  and again in `:root[data-theme="dark"]` (keep both blocks identical). Light / dark: ground #dcd9d2 / #141619, ink
  #16181b / #e9e7e1, muted #45484d / #aeb2b8, rule #b3afa6 / #33373d, link #5a3bd0 / #b9a8ff (hover #3e22a8 / #d6cbff),
  mod bar #6d4fe0 / #a893ff, game bar #6f7379 / #8b9097, good #1d7442 / #6fd79a, amber #8a5d00 / #e8b54a, warn #a8241c
  / #ff8f85. Asphalt kit panel #141619 (dark #0d0f11), chalk #eceae4, chalk-muted #b3b6bb. Pit-board steel/slat
  colours are fixed literals in `.board`, not tokens.
- Asphalt kit in both themes: pit board, pit box, timing tower, code blocks, tooltips, header, footer.
- Fonts: Saira Semi Condensed 700 and 800 only, self-hosted in `docs/fonts/` (OFL, `OFL-Saira.txt`); body is
  system-ui, identifiers in the mono stack. No other webfonts, nothing from a CDN at runtime.
- Textures: `docs/textures/concrete.webp` (light ground), `asphalt.webp` (dark ground and kit), `board.webp` (pit
  board), made by `docs/textures/make_textures.py` (numpy + Pillow, seeds 2101–2103; run from that folder). Change the
  script and re-run it, never edit images. Each has a `.webp.json` provenance sidecar: after regenerating run
  `"$IMP/scripts/impeccable" embed-prompt docs/textures/<file>.webp --prompt "Origin: procedural texture produced by
  docs/textures/make_textures.py ..."` (`$IMP` as in step 6 below).
- Themes: light and dark. An inline head snippet reads `localStorage` key `lgp-theme` into `data-theme`; `site.js`
  drives `#theme-toggle` ("Dark mode"/"Light mode"). Check both.
- Phones: no horizontal scroll at 390px, tap targets ≥ 44px. Breakpoints 1000 / 860 / 720px.
- Motion: the timing tower's rows switch "on" in order once (120ms + 90ms apart) the first time it is 35% in view
  (`site.js`, `[data-tower]`). FAQ chevrons turn 0.2s; buttons rise 1px. All off under `prefers-reduced-motion`.
- Signature components: the pit board (hero, one played game-vs-mod figure, `role="img"` with the readout spelled
  out, a caption naming source and limit), the pit box (six numbered stations, each with Work and Checked/Choice
  fields) plus the roster (`dl.roster`, a yellow-underlined `span.where` tag), the timing tower, timing sheets
  (`.sheet`, `.src` source line, `.limit` line; `table.timing` with `td.mod`, `span.state`), bar charts
  (`.chart-card` with `.row` `data-name/value/color/desc`, a `.tip`, and a "View chart data as a table" `<details>`).
- Don't: return to the old Timbermods docs template (cream paper, icon tiles, identical card grids, pill badges,
  eyebrows/kickers over headings); draw any curve or chart that isn't measured data; use yellow as fill or result;
  colour the game red; add panel/button shadows; nest cards; use side-stripe accents, gradient text or new accent
  colours; use Timberborn art, logos or screenshots, or stock/generated imagery.
- New components: build them from the tokens and components above, match the neighbouring sections, and add them to
  DESIGN.md.

### Content rules

- Describe the mod as it is now. No "New in <version>", "added in …" or version history on player pages; that belongs
  in the release notes and TECHNICAL.md. Only upgrade facts players need stay (close the game, delete the old folder,
  co-op updates together, old `.cfg` keys are ignored, 0.4.3 crashes on load).
- Every number is measured, sourced and bounded: it names where it came from (test harness or a named played session)
  and what it doesn't promise. The hero board's 0.94 ms game vs 0.24 ms mod (hauler job list, per request) is from
  the 362-beaver **Verify every feature** session. The save-freeze figure (0.84–1.26 s → 325–430 ms) is against an
  **earlier build of this mod**, never "the game", and must stay labelled so. No FPS/"X% faster" figures, benchmarks,
  testimonials, download counts or screenshots exist; never invent them.
- Played / not-played status matches the README exactly. The young-mod caution matches README's wording: "It is still
  a young mod, so try it on a copy of a save first." (`install.html`'s slightly different wording is known and kept.)
- Keep the credits: MIT, maintained by Timbermods, BeaverBuddies is thomaswp's project, and the "unofficial … not
  affiliated with or endorsed by Mechanistry" footer line.
- Terminology (as in game, README, TECHNICAL): Late Game Performance; hauling job list cache; route maps; tree and plant
  search; terrain path searches; incremental garbage collection; verify settings; **Verify every feature**; startup
  line / `Simulation features:` line; "turned itself off"; worker threads; **preview** (GitHub pre-release) vs
  **latest release**. Log identifiers (HaulCache, RouteMaps, …) are exact; never paraphrase them in code.
- `docs/release.js` is shared across timbermods sites and byte-identical (SHA-1
  `f771fa55eea5db13c26043fdde5a374d1944aa0b`): replace it, never edit it. Content pages load it last, after `site.js`,
  with `data-repo="timbermods/LateGamePerformance" data-asset="^LateGamePerformance-[\d.]+\.zip$"`.
- `404.html` uses absolute `/LateGamePerformance/` paths, is `noindex`, and loads no script but the theme snippet.
- Keep every existing id (README links `index.html#status`, `install.html#checksum`, …; troubleshooting's log/symptom ids).

### Update the website for a new release

When asked to "update the website for the latest release, consistent with the design":
1. Read the release and the docs: `gh release list -R timbermods/LateGamePerformance -L 5`,
   `gh release view <tag> -R timbermods/LateGamePerformance`, README, TECHNICAL.md, PRODUCT.md. List every
   player-facing change. A new **preview** gets only the `#status` mention (step 2); a new **Latest** gets all of it.
2. Update every place the site states a changed fact:
   - Static release fallbacks: `grep -rn "0\.4\.30\|3393e5ab" docs` (today's Latest is 0.4.30): `data-release="tag"`
     on both index Download buttons; `data-release="asset-name"` in install step 3 and `#checksum`; the
     `data-release="sha256"` hash in `install.html#checksum` (get it with `gh release view <tag> -R
     timbermods/LateGamePerformance --json assets -q '.assets[]|.name+" "+.digest'`); `data-release="version"` in the
     install `#verify` and troubleshooting `#log` `loading.` lines and troubleshooting `#crash`.
   - Pinned text (`grep -rn data-release-pinned docs`): index hero `.status-note`, index `#status .section-head`
     (includes the preview paragraph), `faq.html#how-much`. Rewrite each for the new build, then bump the attribute.
   - Status: `index.html#status` Tested / Not yet lists, the hero status note, `#coop` "Played in multiplayer" item,
     `faq.html#co-op-tested`, all matching README's "What to expect" and "Tested".
   - Test counts (523 / 131 today): `index.html` `.proofs` and the `#status` list.
   - Startup line (12 features today): the tower `<ol>` in `index.html#coop`, the full `Simulation features:` line in
     `install.html#verify` and `#multiplayer`, `faq.html#co-op` and the `troubleshooting.html#log` table. If features
     change, update the count everywhere: site.js's comment, DESIGN.md (tower), PRODUCT.md, the surface brief.
   - Features: a simulation feature goes into the pit box only if it is one of the six headline jobs; otherwise a new
     `dl.roster` item. Settings: `faq.html#settings`, `install.html#gc`, troubleshooting `#slower`/`#ignored`.
   - Requirements and game version: `install.html#requirements`, `troubleshooting.html#needs-mods`, `faq.html#game-version`.
   - Numbers: the `#results` timing sheets and the hero board, only with a new measured, sourced figure.
   - `<meta name="description">` / `og:*` on each page; PRODUCT.md's Operating Context and Honest status.
3. Put new content into the existing components (above). Don't restyle anything.
4. Test: no site test exists. Check by hand: `sha1sum docs/release.js` is `f771fa55…`, no `grep -rn "New in\|added
   in" docs`, every README-linked id still resolves. For mod changes, `dotnet run --project tests -c Release` must
   print `ALL PASSED` with the counts the site quotes.
5. Preview: `python -m http.server 8783 -d docs` (background), then open http://localhost:8783/. Capture light, dark
   and a 390px phone. If the personal `impeccable-site-flow` skill is available, use
   `python <skill>/scripts/capsite.py http://localhost:8783/ <out> "" install.html troubleshooting.html faq.html
   404.html`; otherwise use the Browser pane in both colour schemes at desktop and mobile sizes. Check the changed
   sections and that there's no horizontal scroll. Stop the server afterwards.
6. Optional but recommended: run the detector,
   `IMP="$(ls -d ~/.claude/plugins/cache/impeccable/impeccable/*/skills/impeccable | tail -1)"; "$IMP/scripts/impeccable" detect --json docs`
   (exits 2 when it finds anything; parse from the first `[`). The shipped design gives 56 known false positives:
   side-tab ×22 and border-accent-on-rounded ×2 (the 5px/3px painted rules, TOC rule), design-system-font-size ×13
   (off-ramp label/guide sizes), cramped-padding ×11 (page-head, bands, board, TOC list), design-system-color ×6 (#fff
   hovers and button ink, the station code wash, 404/tower black), wide-tracking ×1 (pit board), flat-type-hierarchy
   ×1 (footer h2s). Triage only what's new.
7. If the look changed (a new component or layout), update DESIGN.md and `.impeccable/design.json`.
8. Update the README (and TECHNICAL.md's Website section) if it repeats the facts.
9. Ship: branch → commit → push → `gh pr create`. After Kyler says merge: `gh pr merge <n> --merge` (that publishes),
   then verify:
   - `MSYS_NO_PATHCONV=1 gh api repos/timbermods/LateGamePerformance/pages/builds/latest -q .status` is `built`;
   - `curl -s https://timbermods.github.io/LateGamePerformance/ | grep -c "<a changed string>"` finds the change.

### Full redesign

A new look goes through the whole Impeccable flow (init → critique → audit → direction → build → finish review →
DESIGN.md). With the personal skill: "use the impeccable-site-flow skill to redesign this site".
