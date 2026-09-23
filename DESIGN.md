---
name: Late Game Performance
description: "Pit Crew: the project site for a Timberborn mod that makes every stop shorter without touching the race."
colors:
  concrete-ground: "#dcd9d2"
  asphalt-ink: "#16181b"
  concrete-muted-ink: "#45484d"
  concrete-rule: "#b3afa6"
  pit-box-yellow: "#f2c230"
  fastest-lap-purple: "#6d4fe0"
  lap-link: "#5a3bd0"
  lap-link-deep: "#3e22a8"
  timing-green: "#1d7442"
  timing-amber: "#8a5d00"
  signal-red: "#a8241c"
  game-grey: "#6f7379"
  asphalt-ground: "#141619"
  chalk-ink: "#e9e7e1"
  asphalt-muted-ink: "#aeb2b8"
  asphalt-rule: "#33373d"
  fastest-lap-purple-dark: "#a893ff"
  lap-link-dark: "#b9a8ff"
  lap-link-hover-dark: "#d6cbff"
  lap-focus-dark: "#c3b5ff"
  lap-button-ink-dark: "#120c2b"
  timing-green-dark: "#6fd79a"
  timing-amber-dark: "#e8b54a"
  signal-red-dark: "#ff8f85"
  game-grey-dark: "#8b9097"
  night-asphalt-panel: "#0d0f11"
  panel-chalk: "#eceae4"
  panel-chalk-muted: "#b3b6bb"
  board-slat: "#1a1c1f"
  board-frame-steel: "#a9afb5"
  board-pole-steel: "#8f959b"
  board-inner-edge: "#5d6268"
  board-chalk: "#f4f2ec"
  board-label: "#c9cdd2"
typography:
  display:
    fontFamily: "Saira Semi Condensed, Arial Narrow, sans-serif"
    fontSize: "clamp(2.7rem, 6.2vw, 4.3rem)"
    fontWeight: 800
    lineHeight: 1.04
    letterSpacing: "-0.005em"
  headline:
    fontFamily: "Saira Semi Condensed, Arial Narrow, sans-serif"
    fontSize: "clamp(1.9rem, 3.8vw, 2.75rem)"
    fontWeight: 800
    lineHeight: 1.04
    letterSpacing: "-0.005em"
  pitch:
    fontFamily: "Saira Semi Condensed, Arial Narrow, sans-serif"
    fontSize: "clamp(1.5rem, 2.8vw, 2rem)"
    fontWeight: 800
    lineHeight: 1.12
  title:
    fontFamily: "Saira Semi Condensed, Arial Narrow, sans-serif"
    fontSize: "1.32rem"
    fontWeight: 700
    lineHeight: 1.15
  board-numeral:
    fontFamily: "Saira Semi Condensed, Arial Narrow, sans-serif"
    fontSize: "clamp(2.2rem, 4.6vw, 3.4rem)"
    fontWeight: 800
    lineHeight: 1
    fontFeature: "tnum"
  figure:
    fontFamily: "Saira Semi Condensed, Arial Narrow, sans-serif"
    fontSize: "1.15rem"
    fontWeight: 800
    lineHeight: 1
    fontFeature: "tnum"
  label:
    fontFamily: "Saira Semi Condensed, Arial Narrow, sans-serif"
    fontSize: "0.92rem"
    fontWeight: 700
    lineHeight: 1.3
  body:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, Helvetica Neue, Arial, sans-serif"
    fontSize: "1.0625rem"
    fontWeight: 400
    lineHeight: 1.62
  lead:
    fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, Helvetica Neue, Arial, sans-serif"
    fontSize: "1.12rem"
    fontWeight: 400
    lineHeight: 1.62
  mono:
    fontFamily: "ui-monospace, Cascadia Mono, SFMono-Regular, Consolas, Liberation Mono, monospace"
    fontSize: "0.9em"
    fontWeight: 400
rounded:
  none: "0px"
  hair: "1px"
  xs: "2px"
  sm: "3px"
  board: "5px"
  brand: "6px"
spacing:
  xs: "6px"
  sm: "12px"
  md: "18px"
  lg: "22px"
  gutter: "clamp(16px, 4vw, 32px)"
  column-gap: "clamp(28px, 5vw, 64px)"
  section: "clamp(56px, 8vw, 104px)"
  wrap: "1180px"
components:
  button-primary:
    backgroundColor: "{colors.fastest-lap-purple}"
    textColor: "#ffffff"
    typography: "{typography.figure}"
    rounded: "{rounded.sm}"
    padding: "0 22px"
    height: "50px"
  button-primary-hover:
    backgroundColor: "{colors.lap-link-deep}"
    textColor: "#ffffff"
  button-primary-dark:
    backgroundColor: "{colors.fastest-lap-purple-dark}"
    textColor: "{colors.lap-button-ink-dark}"
  button-ghost:
    backgroundColor: "transparent"
    textColor: "{colors.asphalt-ink}"
    rounded: "{rounded.sm}"
    padding: "0 22px"
    height: "50px"
  nav-link:
    textColor: "{colors.panel-chalk-muted}"
    typography: "{typography.label}"
    padding: "0 12px"
    height: "44px"
  nav-link-current:
    textColor: "{colors.panel-chalk}"
  station-number:
    backgroundColor: "{colors.pit-box-yellow}"
    textColor: "{colors.asphalt-ink}"
    rounded: "{rounded.xs}"
    size: "36px"
  step-number:
    backgroundColor: "{colors.pit-box-yellow}"
    textColor: "{colors.asphalt-ink}"
    rounded: "{rounded.xs}"
    size: "40px"
  pit-board:
    backgroundColor: "{colors.board-slat}"
    textColor: "{colors.board-chalk}"
    typography: "{typography.board-numeral}"
    rounded: "{rounded.board}"
    padding: "0 18px"
  pit-box-station:
    backgroundColor: "{colors.asphalt-ground}"
    textColor: "{colors.panel-chalk}"
    rounded: "{rounded.none}"
    padding: "22px 22px 20px"
  timing-tower:
    backgroundColor: "{colors.asphalt-ground}"
    textColor: "{colors.panel-chalk}"
    typography: "{typography.mono}"
    rounded: "{rounded.none}"
  timing-tag-on:
    textColor: "{colors.timing-green-dark}"
    rounded: "{rounded.xs}"
    padding: "3px 7px"
  chart-tooltip:
    backgroundColor: "{colors.asphalt-ground}"
    textColor: "{colors.panel-chalk}"
    rounded: "{rounded.sm}"
    padding: "10px 12px"
  code-block:
    backgroundColor: "{colors.asphalt-ground}"
    textColor: "{colors.panel-chalk}"
    typography: "{typography.mono}"
    rounded: "{rounded.sm}"
    padding: "14px 16px"
  notice:
    rounded: "{rounded.none}"
    padding: "14px 18px"
  notice-warn:
    textColor: "{colors.asphalt-ink}"
    padding: "14px 18px"
---

# Design System: Late Game Performance

## Overview

**Creative North Star: "Pit Crew"**

The race and the car are the game's own; the crew only makes every stop shorter, with the parts laid out ready and many hands working at once. The site is a pit lane at dusk: poured concrete by day, asphalt by night, painted pit-box yellow marking out structure, and one colour, fastest-lap purple, reserved for the mod's result. Every figure is presented the way a pit wall presents it: a single readout with the game's number beside the mod's and a source line underneath.

Density is moderate and editorial. Body text is achromatic system-ui in comfortable measures (58–62ch), and colour lives only in paint, numbers and edges. The heavy objects (the pit board, the pit box of six stations, the timing tower, code blocks, header and footer) are set in textured asphalt in both themes, so they read as the same physical kit whichever way the lights are.

The world explicitly rejects the shared Timbermods docs template this site used to wear (cream paper, icon tiles, a grid of identical cards, pill badges, eyebrows) and the fake "smooth line" performance sketch: a drawn curve that looks like data but isn't.

**Key Characteristics:**
- Concrete (light) and asphalt (dark) grounds, each with a procedural aggregate texture from `docs/textures/make_textures.py`.
- Painted yellow is structure only: rules, box lines, lanes, numbers.
- Purple marks the mod's figure and the act of going (links, Download); the game's figure is grey.
- Saira Semi Condensed 700/800 for headings and every number; system-ui for reading; mono for exact identifiers.
- Square, painted, flat: 0–3px corners, 5px painted rules, almost no shadow.
- Asphalt kit in both themes: board, pit box, tower, code, tooltips, header, footer.

## Colors

A grey-concrete and asphalt palette with one structural paint (yellow), one result colour (purple), and timing states that always travel with a word.

### Primary
- **Fastest-Lap Purple** (`fastest-lap-purple`, dark `fastest-lap-purple-dark`): the mod's result. The mod's bar in every chart, the "MOD" value's column in timing tables, the pitch line under the title, and the primary Download button's fill. Taken from the favicon's violet and deepened for contrast on concrete.
- **Lap Link** (`lap-link`, hover `lap-link-deep`; dark `lap-link-dark`, hover `lap-link-hover-dark`): links, the focus ring (`lap-focus-dark` in dark), FAQ summary hover. The deep step is also the primary button's hover fill.

### Secondary
- **Pit-Box Yellow** (`pit-box-yellow`): painted structure. The 5px rule under the header and over the footer, the 5px top rule of painted sections, the pit box frame and its tick bar, the station and install-step numbers, the lane that runs through each row of stations, the current nav tab's underline, the status note's top rule, the underline of roster location tags, the open FAQ entry's top rule, text selection, the skip link. On asphalt it may be text (the board's title row); on concrete it never is.

### Tertiary
- **Timing Green** (`timing-green`, dark `timing-green-dark`): "faster" state words, check marks in the always list, the "tested" column rule, the right-hand folder tree caption. On asphalt surfaces the dark value is used in both themes (the tower's "on" tags, the board's foot line).
- **Timing Amber** (`timing-amber`, dark `timing-amber-dark`): open/untested items only (dashed-circle marks, the open column's rule).
- **Signal Red** (`signal-red`, dark `signal-red-dark`): warnings only (the warn notice's rule and wash, the wrong folder tree caption).

### Neutral
- **Pit-Lane Concrete** (`concrete-ground`) with **Asphalt Ink** (`asphalt-ink`), **Concrete Muted Ink** (`concrete-muted-ink`) and **Concrete Rule** (`concrete-rule`): the light theme's ground, text, secondary text and hairlines. Strong rules use the ink itself.
- **Asphalt Ground** (`asphalt-ground`) with **Chalk Ink** (`chalk-ink`), **Asphalt Muted Ink** (`asphalt-muted-ink`) and **Asphalt Rule** (`asphalt-rule`): the dark theme's ground, text, secondary text and hairlines. Asphalt Ground is also the asphalt kit's panel colour in light; in dark the kit drops to **Night Asphalt Panel** (`night-asphalt-panel`) so it still sits below the ground.
- **Panel Chalk** (`panel-chalk`) and **Panel Chalk Muted** (`panel-chalk-muted`): text on the asphalt kit in both themes; Asphalt Rule is its hairline.
- **Game Grey** (`game-grey`, dark `game-grey-dark`): the game's own bar in every chart.
- **Band**: a 4.5% ink wash (light) / 3.5% chalk wash (dark) behind alternating sections, inline code, notices and pinned notes.
- **Pit board material** (`board-slat`, `board-frame-steel`, `board-pole-steel`, `board-inner-edge`, `board-chalk`, `board-label`): fixed painted-steel and slat colours used only by the pit board and its pole; they do not change with the theme and are not general tokens.

### Named Rules
**The Paint Is Structure Rule.** Yellow paints lines, lanes, frames and numbers. It never fills a panel, never tints text on concrete, and never marks a result.

**The Fastest Lap Rule.** Purple is the mod's figure and the way forward (links, Download). The game's figure is always grey, never red: the game is not the villain, it is the race.

**The Colour Carries A Word Rule.** Green, amber and red always sit beside the word that says what they mean ("faster", "same", "on", "tested", "open", a warning heading). Colour alone never states a result.

## Typography

**Display Font:** Saira Semi Condensed 800 and 700 (self-hosted woff2, OFL; fallback Arial Narrow, sans-serif)
**Body Font:** system-ui (with -apple-system, Segoe UI, Roboto, Helvetica Neue, Arial)
**Label/Mono Font:** ui-monospace (with Cascadia Mono, SFMono-Regular, Consolas, Liberation Mono) for exact identifiers, log lines and the tower rows

**Character:** A condensed, squared motorsport face for everything that is a heading, a label or a number, against a neutral system text face for reading. The contrast is between the timing screen and the explanation beside it.

### Hierarchy
- **Display** (800, `clamp(2.7rem, 6.2vw, 4.3rem)`, 1.04): the home page title. Guide page titles use a smaller clamp (`2.4rem`–`3.8rem`).
- **Headline** (800, `clamp(1.9rem, 3.8vw, 2.75rem)`, 1.04): section headings, balanced wrap. Guide h2s run `1.6rem`–`2.1rem` with a hairline above.
- **Pitch** (800, `clamp(1.5rem, 2.8vw, 2rem)`, 1.12): the single line under the title, in Fastest-Lap Purple.
- **Title** (700, 1.32rem, 1.15): station names, sheet titles, sub-sections.
- **Board numeral** (800, `clamp(2.2rem, 4.6vw, 3.4rem)`, 1, tabular): the pit board's two values; units in a 700 small at max(.55em, 1rem).
- **Figure** (800, 1.15–1.35rem, tabular): chart values, proof figures, tooltip values, button labels.
- **Body** (400, 1.0625rem, 1.62): reading text; lead 1.12rem at 58ch; section intros 1.1rem in muted ink; bold is 650.
- **Label** (700, 0.92–1.02rem, 1.3, Saira): table heads, station field names, nav, TOC heading, footer headings. Mixed case, no tracking, except on the pit board and tower tags.

### Named Rules
**The Numbers Are Display Rule.** Every figure a reader compares is set in Saira at 800 with tabular numerals; tables use `font-variant-numeric: tabular-nums` throughout.

**The Board Speaks In Capitals Rule.** Uppercase with open tracking (0.06–0.1em) belongs to the pit board's rows and the tower's "on" tags, where it is the native voice of the object. Nowhere else on the site is set in spaced capitals.

## Layout

A single centred wrap (1180px max, gutter `clamp(16px, 4vw, 32px)`) with full-bleed section bands. Sections are separated by generous vertical padding (`clamp(56px, 8vw, 104px)`), alternating plain ground with a faint band, and painted sections open with a 5px yellow rule across the full width.

- **Hero:** a two-column grid (1.08fr / 0.92fr, gap `clamp(32px, 5vw, 72px)`). Left: title, pitch, lead, a ruled three-line fact list, two buttons, and the status note, which ends above a 900px fold. Right: the pit board on its pole, the pole running down through the hero's bottom padding to stand on the next section's yellow rule.
- **The pit box:** one framed diagram, not a card grid. A tick bar across the top, then six stations in a 3×2 grid, each crossed by one 4px yellow lane at the height of its number so each row reads left to right in station order.
- **Co-op:** a 400px timing tower (sticky at 96px) beside the prose column.
- **Timing sheets:** a two-column grid of sheets, some spanning both columns; each sheet has a 3px ink top rule.
- **Guides:** a 220px sticky table of contents beside a 760px body column, under a page head closed by a 5px yellow rule.
- **Rhythm:** a loose 6 / 12 / 18 / 22px inner scale; column gaps `clamp(28px, 5vw, 64px)`.

Responsive behaviour:
- **≤1000px:** stations stack into one column; the lane turns vertical and runs down the left edge (4px at 22px in), with numbers set on it. The tower stops being sticky (max 520px) and the co-op grid goes to one column.
- **≤860px:** hero, two-column, sheets, status, roster, closing and folder-tree pairs go to one column; the pit board caps at 460px; the TOC goes static in two columns.
- **≤720px:** the header stops being sticky, the nav wraps to its own row, board rows drop from 80px to 68px, the TOC and proofs go to one column.

## Elevation & Depth

Flat and painted. Depth comes from material, not light: textured asphalt panels set against textured concrete (or darker asphalt against asphalt at night), 5px paint lines and 1px hairlines. Only two objects cast shadows, both because they physically stand off the ground.

### Shadow Vocabulary
- **Board drop** (`box-shadow: inset 0 0 0 1px #5d6268, 0 18px 30px -18px rgba(0,0,0,.55)`): the pit board held out over the pit wall; the inset is the inner lip of its steel frame.
- **Pole shading** (`box-shadow: inset -4px 0 0 rgba(0,0,0,.18)`): the shaded side of the board's flat painted-steel pole.
- **Tooltip lift** (`box-shadow: 0 10px 24px -10px rgba(0,0,0,.6)`): chart readouts floating over the sheet.

### Named Rules
**The Painted Not Lifted Rule.** Panels, stations, sheets, notices and buttons sit flat on the ground. A shadow is permitted only for an object that stands off it (the board) or floats over content (a tooltip).

## Shapes

Square and industrial. Corners run 0 to 3px: the pit box, stations, tower and notices are square (0); chart bars 1px; station numbers, step numbers and "on" tags 2px; buttons, code, code blocks, tooltips and the theme button 3px. The pit board alone is rounded at 5px, like a real slatted board, and the favicon mark in the header at 6px. Borders are structural: 5px paint for frames and section rules, 3px for strong rules (sheet tops, notices, current tab, open FAQ entry, status headings), 1px for hairlines. Icons are inline stroked SVG (2–2.6px strokes, round caps), drawn in the text colour or masked in a state colour.

## Components

### Buttons
Tactile, squared, heavy.
- **Shape:** gently squared (3px), 50px tall, 0 22px padding, Saira 800 at 1.12rem, 18px inline SVG icon at 10px gap.
- **Primary:** Fastest-Lap Purple fill, white label (dark: purple-dark fill, deep violet ink). Reserved for Download.
- **Hover / Focus:** fill steps to the deep link purple and the button rises 1px (0.15s, `cubic-bezier(.2, .8, .2, 1)`); focus is a 3px purple outline at 3px offset (yellow inside the header and footer).
- **Ghost:** transparent with a 2px ink border; hover adds the band wash and the same 1px rise. Used for the install guide and "Report a problem".

### Navigation
The pit wall: a sticky asphalt header (64px) closed by a 5px yellow rule. Brand is the favicon mark and the name in Saira 800. Links are Saira 700, chalk-muted, 44px targets; hover goes to chalk; the current page carries chalk text over a 3px yellow underline. A bordered "Dark mode"/"Light mode" text button sits at the end (stored as `lgp-theme`). Under 720px the header scrolls away and the links wrap to their own row. The footer mirrors it: asphalt, 5px yellow top rule, three columns.

### The Pit Board (signature)
A black slatted board (board texture, 320×160px tile) in an 8px painted-steel frame with a 5px corner, on a flat 16px steel pole that stands down to the next section's yellow rule. Rows are 80px: a yellow tracked title, a GAME row and a MOD row (labels in tracked board-label grey, values as board numerals, the MOD value in lap purple), and a green tracked foot line. It shows one played game-vs-mod figure; its caption, indented past the pole, names where and how it was measured and what it does not show. The whole board is a single `role="img"` with the readout spelled out.

### The Pit Box (signature)
An asphalt panel framed in 5px yellow. A tick bar names the game's own tick. Six stations sit around it, numbered 1–6 in 36px yellow squares; one 4px yellow lane runs through each row (vertical under 1000px), and it is the only yellow inside the frame besides the numbers and the tick bar's rule. Every station has the same fixed fields at its foot: what it does (the paragraph), where the work goes ("Work"), and how it is checked ("Checked", or "Choice"). The rest of the crew follows outside the box as a ruled two-column roster, each item tagged with where its work goes in a yellow-underlined Saira label.

### The Timing Tower (signature)
An asphalt column under a 5px yellow top rule: the mod's startup log line as a race order. The head shows the log prefix in muted mono and "Simulation features:" in Saira. Twelve numbered mono rows (odd rows washed 3%) each end in a squared green "on" tag. Motion: the first time it scrolls into view (35% visible) every tag starts dimmed, then the rows switch on in order, 120ms + 90ms apart, 0.3s each, once. Without the script or with reduced motion every row simply reads "on".

### Timing Sheets
Each sheet has a 3px ink top rule, a title, a muted source line under it, the readout, and a muted limit line after. Timing tables use Saira label heads over a 2px ink rule, right-aligned tabular figures, the mod's column in bold purple, and a Saira state word ("faster" in green, "same" in muted). Bar charts label rows directly (game grey, mod purple, 22px bars, dashed gridlines, value in Saira figure at the end of the track), open an asphalt tooltip on hover or keyboard focus, and always offer a table view in a disclosure.

### Lists and Notices
- **Checks:** 20px masked marks: a green tick (always), an ink circle-slash (never), an amber dashed circle (open).
- **Notice:** band wash under a 3px ink top rule; the warn variant uses a red rule and red wash.
- **Steps:** install steps numbered in 40px yellow squares with a hairline above each step.
- **FAQ entries:** ruled disclosures, 56px summaries with a masked chevron that turns 180° (0.2s); an open entry's top rule becomes 3px yellow.
- **Code blocks:** asphalt, chalk text, 3px corners; folder trees come in right/wrong pairs captioned in green and red.

## Do's and Don'ts

### Do:
- **Do** pair every figure with its source line and its limit, and show the game's number beside the mod's.
- **Do** keep yellow (#f2c230) to painted structure: 5px section and frame rules, lanes, station and step numbers, the current-tab underline.
- **Do** colour the mod's figure purple and the game's figure grey, and put the state word next to any green, amber or red.
- **Do** set the pit board, pit box, timing tower, code blocks, tooltips, header and footer in asphalt in both themes.
- **Do** set every compared number in Saira Semi Condensed 800 with tabular numerals, and keep body text achromatic system-ui.
- **Do** keep nav, button and disclosure targets at 44px or more, and the 3px focus outline at 3px offset (yellow on asphalt chrome).
- **Do** give every chart direct labels, a keyboard-reachable tooltip and a table view.

### Don't:
- **Don't** return to the Timbermods docs template: cream paper, icon tiles, grids of identical cards, pill badges, eyebrows or kickers above headings.
- **Don't** draw a performance curve or chart that is not real measured data.
- **Don't** use yellow as a panel fill, as text on concrete, or to mark a result.
- **Don't** colour the game's figure red or treat it as a failure; red is only for warnings.
- **Don't** use spaced uppercase outside the pit board and the tower's "on" tags.
- **Don't** add shadows to panels, cards or buttons; only the board and tooltips stand off the page.
- **Don't** use Timberborn art or screenshots.
