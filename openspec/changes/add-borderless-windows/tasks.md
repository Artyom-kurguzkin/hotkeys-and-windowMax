# Tasks

## 1. Logic

- [x] 1.1 Add `Chrome.IsManageable`, `StrippedStyle`, `HasCaption`, `NeedsRestrip` and `CoverRect` to Logic.cs; add unit tests for each scenario in specs/borderless-windows
- [x] 1.2 Add the Alt+Q/M/N/T bindings to `KeyEngine` and the window-command decision `WindowCommand(act, isZoomed)`; add unit tests for each scenario in specs/window-hotkeys

## 2. Win32

- [x] 2.1 Add `Strip`/`Unstrip` (style, DWM attributes, frame change) in Frames.cs; add a Win32 test that strips and restores a window the test creates and checks the style bits and the DWM HRESULTs
- [x] 2.2 Add the WinEvent hooks, the startup enumeration, the taskbar cover, the re-strip, Alt+T, and restore-all on quit, logging every strip, restore and cover; verify by checking the log while opening Notepad
- [x] 2.3 Add `--dump` (stdout and log, no changes); verify `WindowManager.exe --dump | Out-String` lists the open windows with their classification

## 3. Manual checks

- [ ] 3.1 With Notepad: no title bar, Win+Left/Right snaps, Win+Up covers the taskbar, Win+Shift+Right moves it to the other monitor, Alt+Q/M/N/T behave as specified, and Ctrl+Alt+Shift+Q restores every frame

## Workflow follow-up

- Archive the change once 3.1 passes.
