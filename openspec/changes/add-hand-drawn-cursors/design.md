# Design

## Context

The website defines the cursors in `../portfolio/src/styles/global.css` as two 40×40 SVGs: a filled outline plus thin detail strokes at 55 % opacity, using only absolute `M`, `L`, `Q` and `Z` path commands.

## Goals / Non-Goals

**Goals:**
- Look the same as in the browser.
- Never fight a program that picks its own cursor.

**Non-Goals:**
- The website's context logic (arrow only over cards). Windows has no notion of cards, so the arrow applies everywhere the standard arrow would.
- Pen, pin and person pointers (rare, not in the standard set).

## Decisions

- **`SetSystemCursor` on the 14 standard `OCR_*` IDs** (started with `OCR_NORMAL` and `OCR_HAND`). This replaces the images behind the standard `IDC_*` cursors. Programs with their own cursors (CSS cursors in browsers, games) pass their own handle and are never affected, which is the requested "let them win" rule with no detection needed.
- **Restore with `SystemParametersInfo(SPI_SETCURSORS)`.** It reloads the user's scheme from their settings, so whatever scheme they have comes back. Called on clean shutdown and from the unhandled-exception handler. If the process is killed, the cursors stay until the next start or logoff, because `SetSystemCursor` isn't persisted.
- **Render natively, not via an SVG library.** .NET has no SVG renderer, and the paths only use `M/L/Q/Z`. `CursorArt.Parse` (pure, tested) converts them to cubic Béziers: a quadratic converts to a cubic exactly. `CursorTheme.Render` draws them with GDI+ at `40 × dpi/96`, the same device size the browser uses. Hotspots scale the same way, rounding halves up.
- **Checked against the browser:** the website's SVGs screenshotted in headless Edge at 150 % look the same as the native renders side by side. The live system cursors were read back with `LoadCursor` + `DrawIconEx`: standard before start, hand-drawn while running, standard after quit.

- **Re-apply when Windows resets them (found in use).** Windows reloads the cursors from the user's scheme on its own (resume, display/DPI or theme change), silently undoing `SetSystemCursor`. Measured: applied once at 09:44, gone the next morning after several real sleep/wake cycles, while hotkeys still worked. `CursorTheme.EnsureApplied` reads the system arrow's hotspot and width (`GetIconInfo`) and re-applies if they aren't ours (`CursorArt.IsOurs`). It runs every 5th 700 ms tick and after each wake/display repair. Verified by reloading the scheme with `SPI_SETCURSORS` (what Windows does): standard arrow immediately, hand-drawn again within 5 s.

- **All standard pointers, drawn to match (user request).** Beyond the website's arrow and hand, 12 more pointers are drawn in the same style. `CursorArt.Shape` is now layers (outline filled even-odd for rings, faint details, full-strength marks), an optional rotation (the four resize pointers are one double-arrow turned 0/90/±45°), and spinning layers. Circles are generated as 8 slightly wobbly quadratic segments, so the M/L/Q/Z parser still covers everything.
- **Animated busy pointers via a generated `.ani`.** Windows only animates `.ani` cursors. `CursorFile` writes `.cur` frames (32bpp DIB with alpha, hotspot in the directory entry) into a RIFF `ACON` with an `anih` header. The result is written to a temp file and loaded with `LoadImage(LR_LOADFROMFILE)`, then the file is deleted. Verified: the system busy pointer drawn at animation steps 0 and 3 shows the arc in different places.

## Risks / Trade-offs

- [One cursor size for all monitors: system DPI (150 % here), so on a 100 % monitor it looks 1.5× larger than the website does there] → `ponytail:` re-apply at the monitor's DPI on monitor change if that bothers.
- [Killed process leaves the cursors until logoff] → The next start re-applies them and a clean quit restores them.
