# Proposal

<!-- merged from #wi1 Add borderless windows -->
## Why

Title bars, borders and caption buttons waste space and add visual noise. The goal is for every application window to fill the area Windows gives it, as if that area were its own monitor. Window control moves to hotkeys. Layout stays with Windows: maximize, Snap and moving between monitors.

## What Changes

- Every real application window has its title bar removed. Windows' own borders, accent color and rounded corners are also turned off. Dialogs, popups, tool windows, File Explorer and Notepad are left alone.
- Windows can still be resized, snapped, maximized and moved between monitors with Windows' own keys (Win+Arrow, Win+Shift+Arrow).
- A maximized window covers the whole monitor, taskbar included.
- If an app puts its title bar back, it is removed again.
- New hotkeys:
  - Alt+Q or Win+Alt+X closes the active window (like Alt+F4).
  - Alt+M toggles maximize. Alt+N minimizes.
  - Alt+T toggles the title bar of the active window back on or off.
  - A Ctrl+Win+Alt tap opens Snap Layouts (Win+Z) to tile windows without gaps.
  - Copilot key + 1–9, or Win+Alt+1–9, moves the active window to that monitor. The Copilot key itself is disabled.
  - Alt tapped three times quickly flashes a border in the accent colour around the active window.
  - Both Alt keys open, and close again, a themed table of every hotkey. `--hotkeys` shows it on its own.
- Quitting (Ctrl+Alt+Shift+Q or a replacing instance) restores every window's title bar and border.
- `--dump` lists every top-level window and how it is classified, which helps diagnose odd apps.

## Capabilities

### New Capabilities
- `borderless-windows`: which windows lose their frame, how they keep working with Windows' layout, the taskbar cover, and restoring frames.
- `window-hotkeys`: hotkeys that replace the caption buttons and manage windows: close, maximize, minimize, title bar, Snap Layouts, moves between monitors, highlight, and the hotkey list.

### Modified Capabilities

## Impact

- New in Program.cs: WinEvent hooks, style changes on other processes' windows, DWM attribute calls, the border overlay, and moves between monitors. New `Chrome` and `Monitors` logic in Logic.cs.
- `HelpWindow.cs` uses WinForms (part of the .NET desktop runtime; no NuGet package) for the hotkey table.
- Apps that draw their own title bar are handled by `add-chrome-crop`.
- Elevated windows are only affected when the program itself runs elevated.

<!-- merged from #wi2 Add chrome crop -->
## Why

Many apps draw their own title bar inside the window: Chrome, Edge, Vivaldi, VS Code, Electron apps (Slack, Discord, Obsidian), new Outlook and Windows Terminal. Removing `WS_CAPTION` doesn't touch those bars, so tab strips and min/max/close buttons stay visible. They should be hidden by default and shown on demand. (UWP apps also draw their own bar but are not cropped, see below. File Explorer and Notepad are excluded from all management, see `add-borderless-windows`.)

## What Changes

- Windows that draw their own top bar have it cropped off. The window is stretched upward by the bar's height and the bar is clipped, so the content still fills the area Windows gave it.
  - This works for maximized, snapped (including Snap Layouts) and floating windows, with no gaps at the edges.
  - App fullscreen (F11) is left alone.
- Nothing of a cropped bar shows on a monitor above. Windows' backdrop and frame, which ignore the clip, are turned off while a window is cropped.
- A tap of Win+Alt (press both, release, no other key) shows the hidden bar of the active window only. A second tap hides it again. Neither tap opens the Start menu.
- Alt+T on a cropped window shows its bar until Alt+T is pressed again.
- Crop height is set per app (in DPI-independent units) and scaled to each window's DPI.
  - A hand-edited `crop.json` can override the defaults. No hotkey changes crop heights.
- UWP app frames (`ApplicationFrameWindow`) become manageable even though they carry the popup style, but are never cropped (detected dynamically by the frame class): the frame host keeps fighting a crop.
- Quitting removes all crops and restores the backdrop and frame.

## Capabilities

### New Capabilities
- `chrome-crop`: detecting self-drawn top bars, cropping them without gaps or leaks, revealing them, and the per-app crop heights.

### Modified Capabilities

## Impact

- Adds crop state and logic to Logic.cs (`Crop`, `CropTracker`), and region, unclamped-move and DWM backdrop/frame calls to Frames.cs.
- Location changes are handled from the program's own message loop instead of inside the WinEvent callback. This avoids re-entrancy and merges event bursts.
- Adds a small JSON file, `%LOCALAPPDATA%\WindowManager\crop.json`, hand-edited and only read by the program, with System.Text.Json (part of the BCL).
- Spike findings this design depends on (Edge at 144 DPI):
  - A maximized Chromium window ignores a normal `SetWindowPos` stretch, but accepts one with `SWP_NOSENDCHANGING` and stays maximized.
  - Restored windows are otherwise clamped to the monitor's max track size.

<!-- merged from #wi6 borderless windows -->
no useless windows window border!
-> show it with shortcut when needed
