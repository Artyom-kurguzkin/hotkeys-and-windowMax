# Proposal: hint mode (Alt+F) — keyboard-click anything on screen

## Why
repo-task-manager has a hint mode (`_hints` in its app.js): press F (here: Alt+F), every clickable element gets a small lettered
badge, type the letters to click it, Shift+letter to Shift-click and keep picking, Alt+F or Esc to hide. The user wants
the very same thing working globally across Windows, with the same badge styling, a bit smaller.

## What changes
- A new capability `hint-mode` in the window manager: Alt+F labels every clickable control of the
  windows on screen with a letter badge; typing a label clicks it.
- Clickable controls are found with UI Automation (buttons, links, inputs, check boxes, list/tree/tab/menu items, …).
- The badges are drawn on one click-through, topmost, never-activated layered overlay, styled like the web app's
  `.hint-badge` (bold, sun-yellow fill, dark outline, soft shadow, 25% opacity when ruled out), 10px instead of 11px.
- The pure decisions (label scheme, narrowing, pick / Shift-pick / reset) live in `HintEngine` (Logic.cs) and are
  unit-tested; Win32 work (scan, overlay, click) lives in Hints.cs; the key routing in Program.cs, reachable from the
  test channel so it is covered by the scenario harness.

## Out of scope
- Elements inside windows that UI Automation does not expose (custom-drawn canvases, some games).
- Overlap *within* a window (an element covered by another element of the same window): the check is per window,
  like `elementFromPoint` only at the window level.
