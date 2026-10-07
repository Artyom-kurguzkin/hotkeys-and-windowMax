# Tasks

## 1. Spike (done before the specs)

- [x] 1.1 Confirm that Chromium accepts stretch + region when restored, and with `SWP_NOSENDCHANGING` when maximized; measure strip heights with PrintWindow. Results are in design.md

## 2. Logic

- [x] 2.1 Add `Crop.IsSelfDrawn`, `Crop.Plan`, `Crop.Pixels`, `Crop.Tune` and the default height table; add unit tests for the detection, crop, clip, height and tuning scenarios
- [x] 2.2 Add the `CropTracker` state machine (apply, own-move, history region-only, refused, uncrop); add unit tests for each crop scenario
- [x] 2.3 Add Win+Alt tap detection, Win+Alt+PageUp/PageDown bindings, and allow `ApplicationFrameWindow` in `Chrome.IsManageable`; add unit tests for the reveal and UWP scenarios

## 3. Win32

- [x] 3.1 Add `Frames.SetRegion` and the `SWP_NOSENDCHANGING` move; add a Win32 test that crops a window the test creates and checks its window rect and `GetWindowRgnBox`
- [x] 3.2 Wire classification, crop on location change, global and per-window reveal, tuning with crop.json persistence, and uncrop on quit in Program.cs, logging every crop and uncrop; verify through the log with a fresh Edge window

## 4. Manual checks

- [ ] 4.1 With Edge, Explorer and Slack: no tab strip or top bar, nothing visible on a monitor above, Win+Alt reveals and hides without opening Start, Alt+T works per window, and the crop tuning hotkeys adjust and persist

## Workflow follow-up

- Archive the change once 4.1 passes.
