# Design

## Context
Ported from `repo-task-manager/TaskManager/wwwroot/js/app.js` (`_hints`) and its `.hint-badge` CSS. The web version:
Alt+F shows a badge on every element matching a clickable selector, nav items first so their
letters are stable; labels are single letters from `asdghjklqwertyuiopzxcvbnm` (no f), then pairs; typing dims
non-matching badges; a full label clicks (`el.click()`), Shift+letter dispatches a Shift-click and keeps the badges;
nothing-can-match resets; Esc hides; a badge sits at the element's bottom-right corner, inset 6px, centred.

## Decisions
- **Finding clickable things: UI Automation**, every visible top-level window, foreground window first (stable
  letters, like the web's nav-first rule), then z-order, each top-left to bottom-right. Control types: Button,
  Hyperlink, CheckBox, RadioButton, ComboBox, Edit, ListItem, TreeItem, TabItem, MenuItem, SplitButton; enabled,
  not off-screen. Visibility ≈ the web's `elementFromPoint`: `WindowFromPoint` at the badge spot must belong to the
  element's own top-level window. Capped at 400 targets / 6 windows (ponytail: FindAll on a browser tab is big).
  `UseWPF=true` only to get `System.Windows.Automation`.
- **The toggle is Alt+F, not F.** The first version used a plain F gated on "is a text field focused" (UIA query kept
  current from focus events). It broke in Word: the document is not reliably reported as editable, so F opened
  the hints instead of typing. Guessing what the user is typing into is the wrong layer; a chord has no such
  ambiguity, and that machinery (focus hook, UIA focus query, timer) was deleted. Alt+F is a normal KeyEngine
  binding (`Act.ToggleHints`), so its Alt release is masked like Alt+Q's.
- **Key routing**: Alt+F goes through `KeyEngine.Feed` (Act.ToggleHints). While hints are up, `RouteHint` runs on the
  hook thread *before* the engine, A–Z pick (Shift held = Shift-pick)
  and Esc hides; other keys pass so Alt chords still work. The same function is used by the test channel, so hint
  mode is driven by the scenario harness exactly like a physical key.
- **The overlay** is one layered (`UpdateLayeredWindow`, per-pixel alpha), click-through, topmost, never-activated
  window spanning the virtual screen, drawn with GDI+ (System.Drawing is already in the project). Badge style =
  the web's: `font 700`, `padding 1px 5px`, `radius 4px`, fill `--sun`, text `--on-fill`, `1.5px solid --line`,
  shadow `0 1px 3px rgba(0,0,0,.18)`, `.dim` = 25% opacity, "Plus Jakarta Sans" then "Segoe UI". A bit smaller:
  10px font, 1px 4px padding, 3.5px radius. Light/dark palette follows Windows' app theme; CSS px scale with DPI.
- **Picking = a real click** at the element's clickable point (the web's `el.click()`), on the input-sender thread
  (never the hook thread), Shift held around the click for a Shift-pick. Universal: it works for anything a mouse can
  click, with no per-control-type pattern code.
- **Stale badges**: a foreground change hides them (the layout was for the previous window set).

## Risks / trade-offs
- [UIA scans can be slow on huge trees] → capped; scan and focus query run on pool threads with a CacheRequest
  (measured: 2s on the loop before; 200-300ms off it); fully covered windows are skipped (Hints.AnyVisible).
  Never the lie-worker: F right after an activation queued behind that window's fullscreen transition.
- [Alt+F is taken by an app (a menu mnemonic)] → hint mode wins; the app's Alt+F is swallowed like Alt+Q is.
- [Overlap within one window] → a badge may label an element hidden behind a sibling; clicking lands on what is
  actually there.

## Prefetch (2026-10-31)
On real windows a scan costs 350-800ms (live log: 798ms for a browser tab + a ribbon), and doing it AFTER Alt+F made the key feel slow.
The labels are now worked out ahead of time: when the foreground window has settled (250ms), when the focused window finishes a move or
resize, the moment Alt goes down (if no usable scan exists), 700ms after a pick, every 3s while the user is working (input within 20s),
and once at start (after warming the overlay and GDI+ fonts, which cost ~100ms the first time). Alt+F draws from the finished scan; if it
arrives mid-scan it waits for that one rather than starting over. A finished scan is used only if `HintCache.Usable`: same foreground,
same windows in the same order at the same rectangles, under 6s old (unit-tested). After the badges are shown one more scan runs; if the
page changed and nothing has been typed yet, the badges are redrawn (letters never change under the user's fingers). The overlay bitmap
covers the badges' bounding box instead of the whole virtual screen (46MB on three monitors, redrawn per letter). `--no-prefetch` turns the
prefetch off. Measured on the hidden desktop: Alt+F to badges 3-30ms from a prefetched scan, ~190ms when it has to finish an in-flight one.
Side effect to know: Chromium/Electron apps switch their accessibility tree on when first asked by a UI Automation client; prefetching
does that on first use of an app rather than on the first Alt+F in it.
