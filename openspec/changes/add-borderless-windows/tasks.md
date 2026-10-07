# Tasks

## 1. Logic

- [x] 1.1 Add `Chrome.IsManageable`, `StrippedStyle`, `HasCaption`, `NeedsRestrip` and `CoverRect` to Logic.cs; add unit tests for each scenario in specs/borderless-windows
- [x] 1.2 Add the Alt+Q/M/N/T bindings to `KeyEngine` and the window-command decision `WindowCommand(act, isZoomed)`; add unit tests for each scenario in specs/window-hotkeys

## 2. Win32

- [x] 2.1 Add `Strip`/`Unstrip` (style, DWM attributes, frame change) in Frames.cs; add a Win32 test that strips and restores a window the test creates and checks the style bits and the DWM HRESULTs
- [x] 2.2 Add the WinEvent hooks, the startup enumeration, the taskbar cover, the re-strip, Alt+T, and restore-all on quit, logging every strip, restore and cover; verify by checking the log while opening Notepad
- [x] 2.3 Add `--dump` (stdout and log, no changes); verify `WindowManager.exe --dump | Out-String` lists the open windows with their classification

## 3. More window hotkeys

- [x] 3.1 Add the Ctrl+Win+Alt tap → Win+Z (Snap Layouts) via `KeyEngine.TapAct`; add unit tests for the tap, a key in between, and the Office-key chord
- [x] 3.2 Add Copilot key (Win+Shift+F23, measured) + 1–9 and Win+Alt+1–9 → move to monitor N (`Monitors.Number`, `Monitors.Map`, restore-then-maximize, DPI re-fit); add unit tests with the measured key sequence, and verify live by moving a Notepad window 1 → 3 → 2 → 1 and maximized → 3
- [x] 3.3 Add Win+Alt+X → close; add a unit test
- [x] 3.4 Add the triple Alt tap → accent-coloured, click-through border overlay; add unit tests for timing, chords and TrackPoint use, and verify live that all four edges show the accent colour and clear after about a second
- [x] 3.5 Add both Alts → the themed, grouped, sorted hotkey table (`HelpWindow.cs`), toggling closed on a second press, plus `--hotkeys`; add tests that every binding is listed and that the window toggles, and verify the look with screenshots

## 4. Manual checks

- [ ] 4.1 With Notepad: no title bar, Win+Left/Right snaps, Win+Up covers the taskbar, Win+Shift+Right moves it to the other monitor, Alt+Q/M/N/T behave as specified, and Ctrl+Alt+Shift+Q restores every frame
- [ ] 4.2 With real keys: Ctrl+Win+Alt opens Snap Layouts, Copilot+digit and Win+Alt+digit move windows, Win+Alt+X closes, triple Alt flashes the border, both Alts open and close the hotkey table

## Workflow follow-up

- Archive the change once 4.1 and 4.2 pass.
