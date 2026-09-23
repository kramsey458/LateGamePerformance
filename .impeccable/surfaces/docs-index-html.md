---
version: 1
slug: "docs-index-html"
primary_target: "docs/index.html"
related_targets: ["docs/install.html","docs/troubleshooting.html","docs/faq.html","docs/404.html"]
---

# Surface brief: Late Game Performance site (docs/index.html with install, troubleshooting, faq, 404)

Scope: docs/. Home: Persuade; guides: Read. Audience: players whose big colony stutters (hundreds of beavers), many in BeaverBuddies co-op asking "will this desync us?"; testers who read log lines. Action: understand it (speed, not decisions; big colonies; co-op safe), install it right (Harmony + Mod Settings, ZIP from Assets, one folder, same version for every co-op player), confirm it from the startup line, report problems usefully. Proof: real numbers only, each with its source (harness or a named played session) and its limits; the startup line itself. Constraints: no site test; release.js shared and byte-identical at docs/release.js, loaded last after site.js with data-repo/data-asset; fallbacks to /releases/latest with 0.4.29 written in; data-release-pinned="0.4.29" on the hero notice, #status head and faq#how-much; theme toggle (lgp-theme, "Dark mode"/"Light mode") and both themes; chart cards keep .tip rows with data-name/value/color/desc and a table view; every existing id kept; 404 absolute /LateGamePerformance/ paths, noindex, no scripts but the theme snippet; honest status (0.4.29 = 0.4.28 without the walking change, played as 0.4.28; 0.4.30 preview unplayed); no version history on player pages; no invented numbers; no Timberborn art. Decisions delegated to the agent.

## Direction contract

THESIS: Late Game Performance is a pit crew: the race and the car are the game's own, the crew only makes every stop shorter, with the parts laid out ready and many hands working at once. It refuses the Timbermods docs template the old site wore (cream paper, icon tiles, 14 identical cards, pill badges, eyebrows) and the fake "smooth line" performance sketch.

OWN-WORLD: Pit lane at dusk. Light: pit-lane concrete ground (#dcd9d2, aggregate texture) with asphalt ink (#16181b). Dark: asphalt ground (#141619, aggregate texture) with chalk ink (#e9e7e1). Painted pit-box yellow (#f2c230) for structure (box lines, station numbers, rules), fastest-lap purple (the favicon's violet, #7657e6 light / #ad9bff dark) for the mod's result and links, green (#2f9e5b) and amber only as labelled timing states, signal red only for warnings. Saira Semi Condensed 500/700/800 (self-hosted, OFL) for headings, numbers, station numbers and the pit board; body system-ui; exact identifiers in a mono stack. Pit-box panels and the pit board are set in asphalt in both themes.

STORY: One look: fewer lag spikes in big colonies; beavers decide exactly what they would anyway; built for BeaverBuddies co-op (same code on every computer); the one real played number (saves freeze for 325-430 ms instead of 0.84-1.26 s in a 362-beaver colony) with its source and limit. Then: why big colonies stutter; the crew (six stations, then the rest of the crew as a roster); the startup line as a timing tower (the same on every computer) and what may differ; always / never; the timing sheets (harness, played, memory clean-up); what is tested and what isn't; install.

FIRST VIEWPORT: Left: "Late Game Performance" (display), the pitch "Same decisions, sooner.", a two-sentence lead, a three-line fact list (same decisions / co-op / big colonies), purple Download + outlined Install guide, the pinned status note (short). Right: the pit board, a black slotted board on a pole showing SAVE FREEZE: the game's own 0.84-1.26 s over the mod's 0.33-0.43 s, with a source line under it (played, 362 beavers vs a recording of the same colony at 359; the overall tick time did not measurably drop).

FORM: Pit Crew, candidate 7 of 7 (seed c6fe57e1). Challengers all declined (canopy, cloud edge, minihompy room, night-flight six-pack, silkscreen loft, goods catalog); raises taken: every figure is one readout with its before, after and source line (six-pack); timing colours always carry a word (canopy); colour kept to paint, numbers and edges, text fields stay achromatic (cloud edge); every crew station has the same fixed fields: what it does, where the work goes, how it is checked (goods catalog); the crew and the tick are one diagram, not a card grid (minihompy). Signature interaction: the timing tower's twelve rows switch on in order once, like the startup line (off under reduced motion).

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
