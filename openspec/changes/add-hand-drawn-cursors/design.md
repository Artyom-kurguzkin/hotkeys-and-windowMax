# Design

## Context

The website defines the cursors in `../portfolio/src/styles/global.css` as two 40×40 SVGs: a filled outline plus thin detail strokes at 55 % opacity, using only absolute `M`, `L`, `Q` and `Z` path commands.

## Goals / Non-Goals

**Goals:**
- Look the same as in the browser.
- Never fight a program that picks its own cursor.

**Non-Goals:**
- The website's context logic (arrow only over cards). Windows has no notion of cards, so the arrow applies everywhere the standard arrow would.
- Other cursor kinds (I-beam, resize, busy), which the website doesn't customise either.

## Decisions

- **`SetSystemCursor` on `OCR_NORMAL` and `OCR_HAND`.** This replaces the images behind the standard `IDC_ARROW` and `IDC_HAND`. Programs with their own cursors (CSS cursors in browsers, games) pass their own handle and are never affected, which is the requested "let them win" rule with no detection needed.
- **Restore with `SystemParametersInfo(SPI_SETCURSORS)`.** It reloads the user's scheme from their settings, so whatever scheme they have comes back. Called on clean shutdown and from the unhandled-exception handler. If the process is killed, the cursors stay until the next start or logoff, because `SetSystemCursor` isn't persisted.
- **Render natively, not via an SVG library.** .NET has no SVG renderer, and the paths only use `M/L/Q/Z`. `CursorArt.Parse` (pure, tested) converts them to cubic Béziers: a quadratic converts to a cubic exactly. `CursorTheme.Render` draws them with GDI+ at `40 × dpi/96`, the same device size the browser uses. Hotspots scale the same way, rounding halves up.
- **Checked against the browser:** the website's SVGs screenshotted in headless Edge at 150 % look the same as the native renders side by side. The live system cursors were read back with `LoadCursor` + `DrawIconEx`: standard before start, hand-drawn while running, standard after quit.

## Risks / Trade-offs

- [One cursor size for all monitors: system DPI (150 % here), so on a 100 % monitor it looks 1.5× larger than the website does there] → `ponytail:` re-apply at the monitor's DPI on monitor change if that bothers.
- [Killed process leaves the cursors until logoff] → The next start re-applies them and a clean quit restores them.
