# Tasks

## 1. Add borderless windows · Logic

- [x] 1.1 Add `Chrome.IsManageable`, `StrippedStyle`, `HasCaption`, `NeedsRestrip` and `CoverRect` to Logic.cs; add unit tests for each scenario in specs/borderless-windows
- [x] 1.2 Add the Alt+Q/M/N/T bindings to `KeyEngine` and the window-command decision `WindowCommand(act, isZoomed)`; add unit tests for each scenario in specs/window-hotkeys

## 2. Add borderless windows · Win32

- [x] 2.1 Add `Strip`/`Unstrip` (style, DWM attributes, frame change) in Frames.cs; add a Win32 test that strips and restores a window the test creates and checks the style bits and the DWM HRESULTs
- [x] 2.2 Add the WinEvent hooks, the startup enumeration, the taskbar cover, the re-strip, Alt+T, and restore-all on quit, logging every strip, restore and cover; verify by checking the log while opening Notepad
- [x] 2.3 Add `--dump` (stdout and log, no changes); verify `WindowManager.exe --dump | Out-String` lists the open windows with their classification

## 3. Add borderless windows · More window hotkeys

- [x] 3.1 Add the Ctrl+Win+Alt tap → Win+Z (Snap Layouts) via `KeyEngine.TapAct`; add unit tests for the tap, a key in between, and the Office-key chord
- [x] 3.2 Add Copilot key (Win+Shift+F23, measured) + 1–9 and Win+Alt+1–9 → move to monitor N (`Monitors.Number`, `Monitors.Map`, restore-then-maximize, DPI re-fit); add unit tests with the measured key sequence, and verify live by moving a Notepad window 1 → 3 → 2 → 1 and maximized → 3
- [x] 3.3 Add Win+Alt+X → close; add a unit test
- [x] 3.4 Add the triple Alt tap → accent-coloured, click-through border overlay; add unit tests for timing, chords and TrackPoint use, and verify live that all four edges show the accent colour and clear after about a second
- [x] 3.5 Add both Alts → the themed, grouped, sorted hotkey table (`HelpWindow.cs`), toggling closed on a second press, plus `--hotkeys`; add tests that every binding is listed and that the window toggles, and verify the look with screenshots
- [x] 3.6 Exclude File Explorer and Notepad from management entirely (`Chrome.ExcludedProcesses`, checked in `IsManageable`); add unit tests for the exclusion and for other apps being unaffected, and verify live with `--dump` and the log that neither app is stripped, cropped or covered while other apps keep working
- [x] 3.7 Survive sleep and display changes: track each window's laid-out place (`Layout`, `LayoutRecord`), freeze on suspend or monitor-set change (power notification, monitor poll, clock-jump wake detection), then put windows back and re-crop once displays settle; add unit tests, and verify with a simulated suspend/resume that an evacuated, un-clipped window returns pixel-exact

## 4. Add borderless windows · Manual checks

- [ ] 4.1 With WordPad or another plain app (not Notepad, which is now excluded): no title bar, Win+Left/Right snaps, Win+Up covers the taskbar, Win+Shift+Right moves it to the other monitor, Alt+Q/M/N/T behave as specified, and Ctrl+Alt+Shift+Q restores every frame
- [ ] 4.2 With real keys: Ctrl+Win+Alt opens Snap Layouts, Copilot+digit and Win+Alt+digit move windows, Win+Alt+X closes, triple Alt flashes the border, both Alts open and close the hotkey table
- [ ] 4.3 Real sleep: put the PC to sleep overnight with windows tiled and cropped across monitors; on wake they return to their places, cropped, and the log shows `displays settled`

## 5. Add chrome crop · Spike (done before the specs)

- [x] 5.1 Confirm that Chromium accepts stretch + region when restored, and with `SWP_NOSENDCHANGING` when maximized; measure strip heights with PrintWindow. Results are in design.md

## 6. Add chrome crop · Logic

- [x] 6.1 Add `Crop.IsSelfDrawn`, `Crop.Plan`, `Crop.Pixels` and the default height table; add unit tests for the detection, crop, clip and height scenarios
- [x] 6.2 Add the `CropTracker` state machine (apply, own-move, history region-only, refused, uncrop); add unit tests for each crop scenario
- [x] 6.3 Add Win+Alt tap detection and allow `ApplicationFrameWindow` in `Chrome.IsManageable`; add unit tests for the reveal and UWP scenarios

## 7. Add chrome crop · Win32

- [x] 7.1 Add `Frames.SetRegion` and the `SWP_NOSENDCHANGING` move; add a Win32 test that crops a window the test creates and checks its window rect and `GetWindowRgnBox`
- [x] 7.2 Wire classification, crop on location change, per-window reveal (Win+Alt) and Alt+T, crop.json overrides, and uncrop on quit in Program.cs, logging every crop and uncrop; verify through the log with a fresh Edge window

## 8. Add chrome crop · Fixes found in use (each with its unit test and a live measurement; see design.md)

- [x] 8.1 Leave app fullscreen alone (Chromium fullscreen is 1 px short of the monitor)
- [x] 8.2 Sideways resize keeps the vertical slot (no double crop while dragging)
- [x] 8.3 Turn off the DWM backdrop and frame while cropped, so nothing leaks onto a monitor above; restore them on every uncrop path
- [x] 8.4 Subtract the part of the bar already above the monitor edge (maximized overhang); measure Vivaldi's bar (30 units)
- [x] 8.5 Fill the snap area for non-maximized windows (`Crop.FillVisible`), without widening twice after reveal
- [x] 8.6 Re-check refused crops after 300 ms (Snap Layouts re-applies positions), up to 3 times per slot, without an event storm; handle location changes from the message loop instead of the WinEvent callback
- [x] 8.7 Close Chromium's 2 px bottom sliver on maximized windows (`Crop.MaximizedVisible`)
- [x] 8.8 Remove the crop-tuning hotkey (user request); crop heights change only via `crop.json`
- [x] 8.9 Never crop UWP frames (`Crop.ShouldCrop`, by frame class; packaged Win32 apps still cropped); add unit tests, and verify live that Calculator is stripped but gets no crop
- [x] 8.10 Close small gaps on the left and right of maximized windows too (VS Code: content from x=2); add unit tests with the measured numbers and verify live that VS Code covers the monitor exactly

## 9. Add chrome crop · Manual checks

- [ ] 9.1 With Edge, Vivaldi and Slack (Explorer is excluded): no tab strip or top bar, nothing visible on a monitor above, Win+Alt reveals and hides the active window only without opening Start, Alt+T works per window, and Snap Layouts leaves no gaps
