# WindowManager

A Windows 11 hotkey daemon and borderless window manager. It replaces `binds.ahk`.

- Every app window loses its title bar, border and rounded corners. Layout stays with Windows: maximize, Snap, Win+Arrow and moving between monitors all work as usual.
- Apps that draw their own title bar or tab strip (Chrome, Edge, Vivaldi, VS Code, Electron apps, Explorer, Outlook, Terminal) have it cropped off. Win+Alt shows it again on the active window.
- Window control is done with hotkeys. The `binds.ahk` binds are ported: line navigation, scrolling, desktop switching, hover-to-focus, and Alt+TrackPoint scrolling.

## Run

```powershell
dotnet publish WindowManager -r win-x64 -o dist   # build dist\WindowManager.exe (single file, ~250 KB)
dist\WindowManager.exe                              # runs in the background, no window
```

| Option | Does |
|---|---|
| `--verbose` | Also log every raw key and TrackPoint sample |
| `--dump` | Print every window and how it would be handled, then exit (changes nothing) |
| `--hotkeys` | Show the hotkey table on its own, then exit |

- Starting it again replaces every running instance. Each old instance is asked to quit, so it restores its windows. One that doesn't respond within 3 s is terminated, and the new instance repairs the windows it left behind, using `state.json`.
- Quit with **Ctrl+Alt+Shift+Q**. This restores every window's title bar, border and position.
- **Don't run `binds.ahk` at the same time.** Every bind fires twice, and its TrackPoint handler stalls input for about 300 ms at a time. The script is started from `binds - Shortcut.lnk` in the Startup folder.
- To start it at logon, put a shortcut to `dist\WindowManager.exe` in `shell:startup`. To manage windows that run as administrator, run it elevated instead, for example from a Task Scheduler task with "Run with highest privileges".
- The exe needs the .NET 10 runtime. Add `--self-contained` to the publish command for a roughly 70 MB exe that runs without it.

## Hotkeys

| Keys | Action |
|---|---|
| Alt+Q or Win+Alt+X / Alt+M / Alt+N | Close (like Alt+F4) / maximize or restore / minimize the active window |
| Alt, Alt, Alt (quickly) | Flash a border in the accent colour around the active window |
| Both Alt keys | Show the hotkey table (also: `WindowManager.exe --hotkeys`) |
| Alt+T | Show or hide the title bar and tab strip of the active window |
| Win+Alt (tap) | Show or hide the cropped tab strip of the active window |
| Ctrl+Win+Alt (tap) | Open Snap Layouts (Win+Z) to tile the active window |
| Copilot key + 1…9, or Win+Alt+1…9 | Move the active window to monitor 1…9 (Windows display numbers). The Copilot key itself is disabled |
| Alt + TrackPoint | Scroll in any direction. The cursor stays still |
| Alt+H / J / K / L | Scroll left / down / up / right |
| Alt+[ / Alt+] | Home / End. Add Shift to select |
| Alt+O | New line below (End, Shift+Enter) |
| Alt+Z / Alt+C | Context-menu key / left click |
| Left Alt+X | Escape |
| PageUp / PageDown | Previous / next virtual desktop |
| Ctrl+Alt+Shift+Q | Quit and restore all windows |

Hover over a window for 0.7 s to focus it. After Alt+Tab, the cursor jumps to the center of the new window.

## Files

All files live in `%LOCALAPPDATA%\WindowManager\`.

| File | Contents |
|---|---|
| `wm.log` | Every decision: windows stripped or skipped (and why), crops, hotkey actions, Win32 failures. It rotates to `wm.log.old` past 5 MB |
| `crop.json` | Optional, hand-edited, e.g. `{ "Notepad": 44 }`: per-app crop heights in DPI-independent units (process name → units), overriding `Crop.Defaults`. The program never writes it |
| `state.json` | Windows the running instance has changed: original style, crop slot and backdrop. It exists only while the program runs. If one is left behind, the next start undoes those changes |

## Troubleshooting

Start with the log, not the app.

- **A window looks wrong:**
  1. Run `dist\WindowManager.exe --dump | Out-String -Width 400`. It lists every window with how it is classified (manageable, has caption, self-drawn, crop height) and changes nothing.
  2. Then search `wm.log` for that window's process name.
- **The crop is too big or too small:** the log line `crop <app> n=<px>` shows the height in use. Set a different value for that app in `crop.json` (in units: px × 96 / DPI), then restart the program.
- **Keys or scrolling feel off:** run with `--verbose`. The log then also gets every raw key and every TrackPoint sample: `dx/dy`, the wheel delta sent, and `lag`, the time the sample waited in our queue.
  - Gaps between samples with `lag=0`: something upstream is stalling input, usually another program's low-level hook.
  - High `lag`: this program's thread was busy.
- **The TrackPoint isn't found:** the log lists every mouse device at startup. Put the right hardware ID in `TrackPointMatch` in Program.cs (it is currently `LEN0325`).
- **Windows stay without a title bar after a crash:** start the program again. It repairs what the crashed instance left behind, and Ctrl+Alt+Shift+Q then restores everything.
- **`dotnet publish` fails because the exe is locked:** an old instance is still running, or is a zombie stuck terminating; those last until reboot. Quit it, or rename `dist\WindowManager.exe` (Windows allows renaming a running exe) and publish again.

## How it works

- **No title bars:** the program removes `WS_CAPTION` and keeps `WS_THICKFRAME`, which Snap and maximize need. It also turns off the DWM border color and corner rounding. A maximized window is grown so its content area covers the whole monitor.
- **No self-drawn bars:** windows whose content starts at the top edge (no system caption) are stretched upward by the bar's height and clipped with a window region. `SWP_NOSENDCHANGING` lets this work on maximized windows too.
  - **No gaps:**
    - Snapped windows are widened by their invisible borders so the content fills the snap area.
    - Maximized Chromium windows are stretched over their 2 px auto-hide-taskbar sliver.
    - The part of a bar already above the monitor edge isn't cut twice.
    - After Snap Layouts re-applies a position, the crop is re-checked after 300 ms.
  - **No leaks:** the DWM backdrop and frame ignore the clip, so they are turned off while a window is cropped and restored afterwards. Otherwise they paint a band onto a monitor above.
  - **Left alone:** app fullscreen (F11) and tool windows.
- **Moving between monitors:** monitor numbers come from the display device name (`\\.\DISPLAY3` → 3). The window keeps its relative position and size. It is placed normal first, refitted after the DPI change, then maximized again if it was.
- **Highlight:** four click-through, topmost strips in the accent colour (`DwmGetColorizationColor`). The DWM border can't show on a cropped window.
- **TrackPoint scrolling:** raw input identifies the TrackPoint. A low-level mouse hook blocks cursor movement during the gesture. The motion is sent as smooth fractional wheel deltas, and Alt is released once per gesture so apps see plain wheel input.
- **Threading:**
  - Hooks run on the message-loop thread.
  - All synthetic input goes through one sender thread. `SendInput` from the hook thread deadlocked once.
  - Window moves reported by WinEvents are handled from the message loop, never inside the callback. In-callback handling was re-entered by our own window calls.

The reasoning behind each decision, including the measurements from the spikes, is in `openspec/changes/*/design.md`.

## Code

| Path | Contents |
|---|---|
| `WindowManager/Logic.cs` | All decisions, as pure code: key state machine (bindings, taps, Copilot key, triple Alt), hotkey table data, window classification, cover and crop geometry, `CropTracker`, monitor mapping, TrackPoint curve |
| `WindowManager/Frames.cs` | Win32 operations on other windows (style, DWM border/backdrop/frame, region, moves, monitors) |
| `WindowManager/Program.cs` | Message loop, hooks, WinEvents, timers, input sender, border overlay, moves between monitors. Applies the decisions and logs each one |
| `WindowManager/State.cs` | `state.json` persistence and recovery after a terminated instance |
| `WindowManager/HelpWindow.cs` | The themed hotkey table (WinForms ListView, light/dark) |
| `WindowManager/Native.cs`, `Log.cs` | P/Invoke declarations, logging |
| `WindowManager.Tests/` | xUnit tests. Most are pure-logic tests named after spec scenarios. The Win32 tests use windows the test creates itself, never your apps |

```powershell
dotnet test
```

## Specs (OpenSpec)

Behavior is specified in `openspec/`. Each feature is a change folder containing `proposal.md`, `design.md`, `tasks.md` and spec deltas. Every scenario maps to a test.

```powershell
openspec list                      # changes in progress
openspec validate --all --strict
```

New work: `/opsx:propose <idea>`, then `/opsx:apply`, then `/opsx:archive`. Archiving a change merges its deltas into `openspec/specs/`.

| Change | Status |
|---|---|
| `add-core-and-hotkeys` | Implemented; manual check of the ported binds pending |
| `add-focus-and-trackpoint` | Implemented; TrackPoint scrolling verified in use; hover and Alt+Tab check pending |
| `add-borderless-windows` | Implemented, including Snap Layouts, monitor moves, highlight and hotkey table; real-key checks pending |
| `add-chrome-crop` | Implemented; gaps/leaks fixed and measured live; manual reveal check pending |

Each change's `tasks.md` lists what is left to check by hand. Archive a change once its checks pass.
