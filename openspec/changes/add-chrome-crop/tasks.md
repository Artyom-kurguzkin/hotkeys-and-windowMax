# Tasks

## 1. Spike (done before the specs)

- [x] 1.1 Confirm that Chromium accepts stretch + region when restored, and with `SWP_NOSENDCHANGING` when maximized; measure strip heights with PrintWindow. Results are in design.md

## 2. Logic

- [x] 2.1 Add `Crop.IsSelfDrawn`, `Crop.Plan`, `Crop.Pixels` and the default height table; add unit tests for the detection, crop, clip and height scenarios
- [x] 2.2 Add the `CropTracker` state machine (apply, own-move, history region-only, refused, uncrop); add unit tests for each crop scenario
- [x] 2.3 Add Win+Alt tap detection and allow `ApplicationFrameWindow` in `Chrome.IsManageable`; add unit tests for the reveal and UWP scenarios

## 3. Win32

- [x] 3.1 Add `Frames.SetRegion` and the `SWP_NOSENDCHANGING` move; add a Win32 test that crops a window the test creates and checks its window rect and `GetWindowRgnBox`
- [x] 3.2 Wire classification, crop on location change, per-window reveal (Win+Alt) and Alt+T, crop.json overrides, and uncrop on quit in Program.cs, logging every crop and uncrop; verify through the log with a fresh Edge window

## 4. Fixes found in use (each with its unit test and a live measurement; see design.md)

- [x] 4.1 Leave app fullscreen alone (Chromium fullscreen is 1 px short of the monitor)
- [x] 4.2 Sideways resize keeps the vertical slot (no double crop while dragging)
- [x] 4.3 Turn off the DWM backdrop and frame while cropped, so nothing leaks onto a monitor above; restore them on every uncrop path
- [x] 4.4 Subtract the part of the bar already above the monitor edge (maximized overhang); measure Vivaldi's bar (30 units)
- [x] 4.5 Fill the snap area for non-maximized windows (`Crop.FillVisible`), without widening twice after reveal
- [x] 4.6 Re-check refused crops after 300 ms (Snap Layouts re-applies positions), up to 3 times per slot, without an event storm; handle location changes from the message loop instead of the WinEvent callback
- [x] 4.7 Close Chromium's 2 px bottom sliver on maximized windows (`Crop.MaximizedVisible`)
- [x] 4.8 Remove the crop-tuning hotkey (user request); crop heights change only via `crop.json`

## 5. Manual checks

- [ ] 5.1 With Edge, Explorer and Slack: no tab strip or top bar, nothing visible on a monitor above, Win+Alt reveals and hides the active window only without opening Start, Alt+T works per window, and Snap Layouts leaves no gaps

## Workflow follow-up

- Archive the change once 5.1 passes.
