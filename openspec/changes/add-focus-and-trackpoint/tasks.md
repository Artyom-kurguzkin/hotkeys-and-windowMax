# Tasks

## 1. Focus

- [x] 1.1 Add `Rect`, `Focus.IsRealWindow`, `ShouldHoverFocus` and `ShouldCenter` to Logic.cs; add unit tests for every scenario in specs/focus
- [x] 1.2 Wire the hover timer, the one-shot timer after Alt is released, window activation (with the attach-thread-input fallback) and `SetCursorPos` in Program.cs, logging each activation and cursor jump
- [ ] 1.3 Verify manually that hovering focuses a window and that Alt+Tab centers the cursor

## 2. TrackPoint scroll

- [x] 2.1 Add `RawMouse.Parse` and `TrackPointScroller` (curve, cap, accumulation, reversal, stop) plus `KeyEngine.MarkChordUsed`; add unit tests for every scenario in specs/trackpoint-scroll, including the AHK raw-buffer offset case
- [x] 2.2 Register raw input on the message window, find the device by `LEN0325` (logging all mouse devices), handle `WM_INPUT` and the 30 ms watchdog; verify with the log that the TrackPoint is found, and manually that Alt+TrackPoint scrolls
- [x] 2.3 Replace the jittery AHK mechanics: block cursor movement with a `WH_MOUSE_LL` hook, send smooth fractional wheel deltas, and lift Alt once per gesture; add unit tests for smoothness, accumulation and the cap, and verify in use (304 samples at a steady 10 ms, lag 0.3 ms)

## Workflow follow-up

- Archive the change once 1.3 passes, then stop using `binds.ahk` (and remove its Startup shortcut).
