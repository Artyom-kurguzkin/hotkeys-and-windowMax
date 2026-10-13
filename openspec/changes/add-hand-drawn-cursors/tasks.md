# Tasks

## 1. Logic

- [x] 1.1 Add `CursorArt` (the website's paths, colours, hotspots; `Parse` for absolute M/L/Q/Z; exact `QuadToCubic`; DPI scaling); add unit tests for parsing, the conversion, scaling and rejecting unsupported commands

## 2. Win32

- [x] 2.1 Add `CursorTheme` (GDI+ render, `CreateIconIndirect`, `SetSystemCursor` on the standard arrow and hand, `SPI_SETCURSORS` restore); apply at startup, restore on quit and crash; add a render test (yellow inside, transparent outside)
- [x] 2.2 Verify the look against the website's SVGs rendered by headless Edge at 150 %, and verify live that the system cursors are hand-drawn while running and the user's own after quitting

## 3. Manual checks

- [ ] 3.1 In daily use: the arrow and link hand are hand-drawn; the portfolio site, a game and an I-beam text field keep their own cursors

## Workflow follow-up

- Archive the change once 3.1 passes.
