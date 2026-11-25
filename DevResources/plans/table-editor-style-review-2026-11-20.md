# Table editor popup: why it still looks wrong, and what the design system says

Status: research and diagnosis only, 2026-11-20. No code under `WindowManager/` was changed. The mock renders were made from copies in `tests/out/style-review/`.

## 1. What a style guide / design system is

- A **design system** is "a centralized collection of reusable components, design tokens, style rules, and documentation" ([Magic Patterns, What is a design system](https://www.magicpatterns.com/blog/what-is-a-design-system)). A **style guide** is one layer of it: "Style guides translate raw token values into documented, human-readable rules" (same article).
- **Tokens** are "named variables that store individual visual decisions" (same article): colours, type sizes, spacing, radius. A rule refers to the token, not the raw value: "use spacing-4 for card padding" rather than "16px" ([GitLab Pajamas, using design tokens](https://design.gitlab.com/product-foundations/design-tokens-using); [Figma, write design system guidelines](https://developers.figma.com/docs/code/write-design-system-guidelines/)). A card, an input and a button should all take the same radius token.
- **Components** are documented with variants, states, accessibility notes and do/don't usage rules (Figma guide above). A component is only specified together with the surface it sits on and the rules for what may sit inside it.
- **Why a lifted component looks wrong.** The usage rules are written for a context, and the component's look quietly depends on it. A popover's container "supplies the shape, elevation and shadow", and those should follow the token values so depth stays consistent (this is how a search summary of [Persona's popover guidance](https://pds.withpersona.com/components/popover) puts it; I could not open the page itself). Material's elevation model is the same idea: a surface is read against what is behind it, as separation and hierarchy ([Material 2, elevation](https://m2.material.io/design/environment); the page is script-rendered, so I am relying on a search summary). Four things come from the context and are lost when you lift a component out of its page:
  1. **Surface**: what colour the component sits on (here: `--paper`).
  2. **Elevation**: how it separates from that surface (here: line + hard shadow, which only reads against a light surface).
  3. **Spacing rhythm**: the gaps between neighbours (here: the tile's padding, 8/12/16px steps).
  4. **Containment**: one frame holds a group of things (here: the tile holds the textarea; the ribbon floats over the tile's edge).

## 2. The owner's design system, the rules that matter for a floating editor

Sources: `repo-task-manager/docs/design/mockup.html` (it is in `docs/design/`, not `design/`; the "Design system" tab is `renderSystem()`), `WindowManager/web/css/app.css` (tokens lines 36-69, `.tile` 502, `textarea.desc-in` 584-585, `.md-*` 992-1025), `README.md` acknowledgement (inspired by Pharrell Tan's Dribbble shot "Personal Task Management App", dribbble.com/shots/16333822). The mockup's own description: "soft-brutalist task app. White cards with firm 2px outlines and generous radius, pastel fills for state, dashed icon frames as the one sketch-like detail. Dark mode is designed, not inverted."

| Topic | Rule (token) |
|---|---|
| Surfaces | Two only. `--paper` #f7f4f1 is the ground; `--card` #fff is a raised object on it. Text is `--ink` #24211f, secondary `--ink-2`. A card is never shown without paper behind it: every screen in the system is cards on paper. |
| Outline | Every card has a firm 2px `--line` (#2b2725) outline. Dividers inside use `--line-soft` (#d9d3cd), 1-1.5px, or dashed. |
| Elevation | Not a blur. A floating or focused card gets a **hard** offset shadow: `4px 4px 0 var(--shadow)` (popover, menu, ribbon, picker, focused tile); hover lifts 3px. Shadow, not tint, is the whole elevation vocabulary. |
| Radius | `--r` 14px for cards (tile 16px, `.md-pop` 14px); controls 8px; small chips 5-6px. One shape family, always rounded. |
| Spacing | `--s1..s6` = 4, 8, 12, 16, 24, 32. Tile padding 14x16, popover padding 6, button padding 5x9 / 4x16, gaps 2-12. |
| Type | Plus Jakarta Sans; `--t-xs .75rem, sm .875, md 1, lg 1.2, xl 1.5`; body 15px/1.5; mono for paths only. Text areas use line-height 1.55. |
| State colour | Colour marks state only: `--sun` (yellow) selected/current, `--sage` (green) hover, `--stone` neutral hover, `--peach` problem. |
| Focus | Dashed 2px accent outline; a text-editing tile instead lifts with the 4px shadow ("focus shows on the tile, not as a box in it", app.css 585). |
| How the note page framed the textarea | `.tile` = `--card`, 2px `--line`, radius 16, padding 14x16, sitting on `--paper`, with its heading pill; the textarea inside is transparent, borderless, Plus Jakarta Sans 15px, line-height 1.55. md-editor copies the textarea's surface, font and line-height onto the grid overlay (`position()` in md-editor.js, lines 277-292: `fontFamily`, `fontSize`, `--lh`, `background: surface(ta)`), so the grid was a transparent-looking set of cells printed straight onto the **tile's white card**, inside a framed, shadowed object on paper. The ribbon (`.md-pop.md-bar`) was a second card floating over the tile's top edge. |

The key point: the grid itself never had a frame, an outline or a shadow. It never needed one, because the tile gave it all three.

## 3. What the current popup shows (evidence)

All paths are in `C:\Users\artyo\Documents\5-mine\window-manager\tests\out\style-review\` unless stated. The "seen" images use the last posted `shape` message (ribbon `{x:42,y:40,w:616,h:74,r:14}` and overlay `{x:42,y:116,w:284.58,h:93,r:0}`) cut out of a transparent render and put on #1f1f1f / #fff, like the host's window region does. A fake host text is drawn behind so you can see what the cards cover.

- `cur-on-dark.png`, `cur-on-light.png`: the table editor now.
- `cur-picker-on-dark.png`, `cur-picker-on-light.png`: the picker now.
- Originals: `C:\Users\artyo\AppData\Local\Temp\claude\9422ccb6cfc1df36\2c36960f-bce1-4440-83a6-d7c427e8b024\scratchpad\old\old-grid.png`, `old-picker.png`.

### Reasons (what you see, rule broken, evidence, fix)

**1. The grid is not a card at all: no outline, no radius, no shadow.**
What you see: a bare white rectangle, cut to the table's width, with square corners and only the hairline cell borders. Next to the ribbon (a framed, shadowed card) it reads as something that fell off. On a white host app it dissolves into the page; on a dark host it is a naked white patch.
Rule broken: "a raised thing is a `--card` with a 2px `--line` outline, `--r` radius and a hard 4px `--shadow`"; the editor has two elevated things but only one is drawn that way. `host.js` `shape()` reports the overlay with `r: 0` and no shadow allowance, so the region clip is a plain rectangle too.
Evidence: `cur-on-light.png` (grid at lower left of the ribbon, no edge on its right/bottom beyond hairlines); `old-grid.png` (the grid sits inside the tile's frame and shadow); `cur-picker-on-light.png` (the picker, which the owner likes, is a complete card: the contrast between it and the grid is the diagnosis).
Fix: make `.md-ov` a card: `background: var(--card); border: 2px solid var(--line); border-radius: var(--r); box-shadow: 4px 4px 0 var(--shadow)`; `width: fit-content; height: auto; clip-path: none` (md-editor sets an inline `clip-path` and width, which would cut the frame off; `!important` in the popup's own `<style>` beats inline). `host.js` shape: report `o.left-2 .. width+2*2+SHADOW`, `r = 14+2`. Mock: `fix.css` rule 1, `fix-on-light.png`.

**2. The ribbon and the grid have no spatial relationship: 6px apart, ribbon 616px wide over a 285px grid, with its shadow touching the grid's top row.**
What you see: a wide banner above a small table, the table starting at the banner's left edge; the 4px shadow ends 2px above the header row, so it looks like a black underline on the grid.
Rule broken: spacing rhythm (4/8/12/16) and containment: in the original, ribbon and grid were one composition, the ribbon floating over the tile's edge on a 640px tile of the same width. `place()` in md-editor.js uses a fixed 6px gap (line 96) and the ribbon's `maxWidth` is the textarea width (line 154, 612px here, `host.js` MIN_W).
Evidence: `cur-on-light.png` (dark bar directly above "A B C" row); `old-grid.png` (ribbon width = tile width).
Fix: gap of one spacing step between the two outlines: `.md-bar { margin-top: -8px }` (about 12px outline to outline). Width: either accept it (the ribbon is a toolbar; left edges align) or, optionally, `max-width: 392px !important; .md-grp { flex-wrap: wrap }`, which gives a 3-row ribbon that matches a small table (`fix2-on-dark.png`; needs `body` padding-top about 170px). I recommend leaving the width and fixing only the gap. Mock: `fix.css` rules 3, `fix-on-dark.png`.

**3. The ribbon has a dangling divider and no grouping.**
What you see: a 2px separator hangs at the end of the first row, and the second row starts with icons. The flex-wrapped ribbon has 4 groups and 3 `.md-sep` bars as siblings, so a bar can land at a line end.
Rule broken: the system draws dividers as `--line-soft` between peers inside one row, not as stray marks; it groups with fills (`--stone`) elsewhere (`.md-b:hover`, `.tag`).
Evidence: `cur-on-light.png` (separator at the far right of row 1); also present in the original `old-grid.png` (so it is an old flaw, not a regression).
Fix: hide `.md-sep` and make each group a `--stone` pill: `.md-sep{display:none} .md-grp{padding:2px;border-radius:11px;background:color-mix(in srgb,var(--stone) 38%,transparent)} .md-bar{gap:var(--s2) var(--s3);padding:var(--s2)}`. Mock: `fix.css` rule 4. Note the ribbon gets about 18px taller (88px vs 70px), so `editor.html`'s `body { padding: 90px }` must grow to about 110px, or the ribbon flips below the caret (I saw this in my first fix2 attempt).

**4. The `--paper` ground is gone, so the outline and the hard shadow vanish on dark or coloured hosts.**
What you see: on #1f1f1f the ribbon and picker are white pills with no edge: `--line` (#2b2725) and `--shadow` (#2b2725) are the same near-black as the host. The elevation language (line + offset shadow) only works against a light surface, and the system always put its cards on `--paper`. Lifted into VS Code, the context is whatever the host is.
Rule broken: surface and elevation context (section 1): a component is specified together with its ground.
Evidence: `cur-on-dark.png` (no visible outline or shadow on the ribbon or picker; compare `cur-on-light.png`), `cur-picker-on-dark.png`.
Fix, staying in the system (no restyle to the host): make each popup carry its own ground, a 6px `--paper` mat round card and shadow, the way a sticker carries its white border: `box-shadow: 4px 4px 0 var(--shadow), 4px 4px 0 6px var(--paper), 0 0 0 6px var(--paper)` on `.md-pop` and `.md-ov`; `host.js` `card()` adds the same 6px to the region rect and `r + 6`. Mock: `fix.css` rule 2, `fix-on-dark.png`, `fix-picker-on-dark.png`. The mat is subtle on a light host (`fix-on-light.png`), and I judge it a good trade; if it looks too heavy, 4px works too (one number in the mock). Not tested: other host colours (blue, black).

**5. The region clip leaves unpainted slivers at the cards' corners.**
What you see: small white arcs at the ribbon's bottom-left and the picker's top-right on the dark host (`cur-on-dark.png`, `cur-picker-on-dark.png`). Cause: `ApplyRegion` makes a round-rect of the card plus its 4px shadow with the card's radius, but the real shape (a rounded card plus an offset rounded shadow) leaves 2-3px corner areas that the region includes and the page does not paint; `TablePopup.cs` line 304 says that window background shows there (COLOR_WINDOW). I could not test the real window (no live tests); this is what my compose of the same region shows, with the capture's white page background as the fill, so the real colour may differ.
Rule broken: not a design-system rule, a craft one: the outer silhouette must be exactly the card.
Fix: with reason 4's mat the silhouette is the mat's rounded rectangle and the corner mismatch is a paper-coloured pixel or two instead of white; for an exact result, build the region from the card and the shadow separately. Unresolved, small.

**6. The grid has no breathing room inside its frame (consequence of fixing reason 1).**
What you see: rows are 23.25px (`--lh` = the hidden textarea's 15px x 1.55), cell text 15px, header letters .78em, as in the original. There the tile padded the grid (14x16 on `.tile`, 10x12 on the textarea) so the grid floated inside a frame; now the row-number column and header row touch the new outline (`fix-on-light.png`: the "A B C" row and the "1 2 3" column run to the frame edge).
Rule broken: card padding in the system is 8-16px (`.tile` 14x16), never zero against an outline.
Fix: one spacing step of padding on the overlay card (`var(--s2)`, 8px), and move the card up/left by the same amount (plus the 2px border) so the rows still land on the table's lines. Not mocked, and the 8px of extra frame covers 8px more host text on each side; the owner should choose between that and the tight frame of the mock.

**7. The overlay is "exactly over the table's lines" in the textarea, but not in the host.**
What you see (inferred, not rendered with a real host): the host app's line height is not 23.25px (VS Code is about 19px, Notepad about 15-16px), so a framed card of 4 x 23.25px hides a different amount of text than the table's lines, and with an outline the card's edge cuts through text lines, which the frameless white patch hid better.
Rule broken: the popup should stand as a card, not pretend to be the text it covers.
Fix: treat the card as a free-standing editor anchored at the first line: size it from its own content (design-system row heights, 28-32px as for `.box.cell` rather than the host line height) and let the host's write-back replace the original lines. This is a design decision for the owner; it makes the "overlay laid over the lines" idea unnecessary and removes the need for `--lh` entirely. Not mocked.

### What is not wrong (so it is not changed)
The picker (already a complete card), the colours (`--sun` selection, `--stone` header tint), Plus Jakarta Sans (bundled; renders correctly in my captures), the cell hairlines.

## 4. Before/after

- `compare-before-after.png`: six panels, before left, after right: editor on dark, editor on light, picker on dark.
- Mock sources: `fix.css` (the CSS), `host-fixed.js` (a copy of `host.js` with the 6px mat in the region rects, and the overlay reported as a card), `fix.html` / `fix-picker.html` (the generated pages), `mk.py` (builds a page from `editor.html` with a `window.chrome.webview` shim and the `wm.open(...)` call), `cdp.js` (headless Edge over the DevTools protocol, transparent capture), `comp.py` / `run.sh` (cut the rects of the last `shape` message and composite on #1f1f1f and #fff).
- Outputs: `cur-on-dark.png`, `cur-on-light.png`, `cur-picker-on-dark.png`, `cur-picker-on-light.png`, `fix-on-dark.png`, `fix-on-light.png`, `fix-picker-on-dark.png`, `fix-picker-on-light.png`, `fix2-on-dark.png` / `fix2-on-light.png` (optional narrow ribbon), and the raw transparent renders `*-raw.png`.
- Note on the method: `--screenshot` with the stated flags gave a blank result in this setup (the page's `wm.open` ran but the capture had an empty page until the page had focus), the first attempt showed an empty tile (no ribbon, no grid; the cause was not diagnosed, probably the page not having focus in headless mode), so I drove headless Edge over the DevTools protocol (`cdp.js`, with focus emulation on) at the same scale factor 1.5 and 900x640 window. The transparent-background override did not take: the captures have a white page background, so the corner pixels outside the cards are white in my composites.

## 5. The change in one block (what to put in `editor.html`'s `<style>`)

```css
.md-ov { clip-path: none !important; width: fit-content !important; height: auto !important; margin: -2px 0 0 -2px;
  background: var(--card) !important; border: 2px solid var(--line); border-radius: var(--r); }
.md-pop, .md-ov { box-shadow: 4px 4px 0 var(--shadow), 4px 4px 0 6px var(--paper), 0 0 0 6px var(--paper); }
.md-bar { margin-top: -8px; gap: var(--s2) var(--s3); padding: var(--s2); }
.md-sep { display: none; }
.md-grp { padding: 2px; border-radius: 11px; background: color-mix(in srgb, var(--stone) 38%, transparent); }
/* body { padding-top } 90px -> about 110px for the taller ribbon */
```
plus the two `host.js` `shape()` edits (halo of 6px on every rect; the overlay as a card with `r = 14 + 6`, size `o.width + 4 + SHADOW + 12`). All colours, radii and spacing are the system's tokens; no host colour is used.

## 6. Unresolved

- Reasons 5-7 are not mocked or measured against the real window (no live tests on the desktop).
- The mat (reason 4) is my proposal, not something in the owner's system; it is the one new idea. It is optional on a light host and needed on a dark one.
- Ribbon width versus table width (reason 2): left as is; the narrow variant is in `fix2-*.png` for the owner to judge.
- Not seen: the editing state (a cell in edit with `.md-in`), the selected-cells state, and the grid with more than 3 rows or a wide table (overflow scroll in `.md-gx`).
- `repo-task-manager/design/mockup.html` does not exist; the system mockup is at `repo-task-manager/docs/design/mockup.html`.
