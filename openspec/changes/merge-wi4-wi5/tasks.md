# Tasks

## 1. better mouse control · Focus

- [x] 1.1 Add `Rect`, `Focus.IsRealWindow`, `ShouldHoverFocus` and `ShouldCenter` to Logic.cs; add unit tests for every scenario in specs/focus
- [x] 1.2 Wire the hover timer, the one-shot timer after Alt is released, window activation (with the attach-thread-input fallback) and `SetCursorPos` in Program.cs, logging each activation and cursor jump
- [ ] 1.3 Verify manually that hovering focuses a window and that Alt+Tab centers the cursor

## 2. better mouse control · TrackPoint scroll

- [x] 2.1 Add `RawMouse.Parse` and `TrackPointScroller` (curve, cap, accumulation, reversal, stop) plus `KeyEngine.MarkChordUsed`; add unit tests for every scenario in specs/trackpoint-scroll, including the AHK raw-buffer offset case
- [x] 2.2 Register raw input on the message window, find the device by `LEN0325` (logging all mouse devices), handle `WM_INPUT` and the 30 ms watchdog; verify with the log that the TrackPoint is found, and manually that Alt+TrackPoint scrolls
- [x] 2.3 Replace the jittery AHK mechanics: block cursor movement with a `WH_MOUSE_LL` hook, send smooth fractional wheel deltas, and lift Alt once per gesture; add unit tests for smoothness, accumulation and the cap, and verify in use (304 samples at a steady 10 ms, lag 0.3 ms)

## 3. custom styling · Logic

- [x] 3.1 Add `CursorArt` (the website's paths, colours, hotspots; `Parse` for absolute M/L/Q/Z; exact `QuadToCubic`; DPI scaling); add unit tests for parsing, the conversion, scaling and rejecting unsupported commands

## 4. custom styling · Win32

- [x] 4.1 Add `CursorTheme` (GDI+ render, `CreateIconIndirect`, `SetSystemCursor` on the standard arrow and hand, `SPI_SETCURSORS` restore); apply at startup, restore on quit and crash; add a render test (yellow inside, transparent outside)
- [x] 4.2 Verify the look against the website's SVGs rendered by headless Edge at 150 %, and verify live that the system cursors are hand-drawn while running and the user's own after quitting
- [x] 4.3 Re-apply when Windows reloads the cursor scheme (wake, display/theme change): check the system arrow every 3.5 s and after each wake repair; add unit tests for recognising ours vs a reset arrow, and verify by reloading the scheme live
- [x] 4.4 Draw the other 12 standard pointers in the same style (layers, rotation, wobbly circles), animate busy/working with a generated `.ani` (`CursorFile`); add tests for coverage, parsing, rotation, the ring hole and the `.cur`/`.ani` layout; review a contact sheet, and verify live that all 14 system pointers change, busy animates, and quitting restores them

## 5. custom styling · Manual checks

- [ ] 5.1 In daily use: all pointers are hand-drawn (text fields show the I-beam, window edges the resize arrows, loading the busy spinner); the portfolio site, a game and an I-beam text field keep their own cursors
