# Tasks

## 1. Logic

- [x] 1.1 Add `CursorArt` (the website's paths, colours, hotspots; `Parse` for absolute M/L/Q/Z; exact `QuadToCubic`; DPI scaling); add unit tests for parsing, the conversion, scaling and rejecting unsupported commands

## 2. Win32

- [x] 2.1 Add `CursorTheme` (GDI+ render, `CreateIconIndirect`, `SetSystemCursor` on the standard arrow and hand, `SPI_SETCURSORS` restore); apply at startup, restore on quit and crash; add a render test (yellow inside, transparent outside)
- [x] 2.2 Verify the look against the website's SVGs rendered by headless Edge at 150 %, and verify live that the system cursors are hand-drawn while running and the user's own after quitting
- [x] 2.3 Re-apply when Windows reloads the cursor scheme (wake, display/theme change): check the system arrow every 3.5 s and after each wake repair; add unit tests for recognising ours vs a reset arrow, and verify by reloading the scheme live
- [x] 2.4 Draw the other 12 standard pointers in the same style (layers, rotation, wobbly circles), animate busy/working with a generated `.ani` (`CursorFile`); add tests for coverage, parsing, rotation, the ring hole and the `.cur`/`.ani` layout; review a contact sheet, and verify live that all 14 system pointers change, busy animates, and quitting restores them

## 3. Manual checks

- [ ] 3.1 In daily use: all pointers are hand-drawn (text fields show the I-beam, window edges the resize arrows, loading the busy spinner); the portfolio site, a game and an I-beam text field keep their own cursors

## Workflow follow-up

- Archive the change once 3.1 passes.
