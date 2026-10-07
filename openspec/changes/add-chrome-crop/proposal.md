# Proposal

## Why

Many apps draw their own title bar inside the window: Chrome, Edge, Vivaldi, VS Code, Electron apps (Slack, Discord, Obsidian), File Explorer's tab strip, new Outlook, Windows Terminal, Notepad and UWP apps. Removing `WS_CAPTION` doesn't touch those bars, so tab strips and min/max/close buttons stay visible. They should be hidden by default and shown on demand.

## What Changes

- Windows that draw their own top bar have it cropped off. The window is stretched upward by the bar's height and the bar is clipped, so the content still fills the area Windows gave it.
  - This works for maximized, snapped (including Snap Layouts) and floating windows, with no gaps at the edges.
  - App fullscreen (F11) is left alone.
- Nothing of a cropped bar shows on a monitor above. Windows' backdrop and frame, which ignore the clip, are turned off while a window is cropped.
- A tap of Win+Alt (press both, release, no other key) shows the hidden bar of the active window only. A second tap hides it again. Neither tap opens the Start menu.
- Alt+T on a cropped window shows its bar until Alt+T is pressed again.
- Crop height is set per app (in DPI-independent units) and scaled to each window's DPI.
  - A hand-edited `crop.json` can override the defaults. No hotkey changes crop heights.
- UWP app frames (`ApplicationFrameWindow`) become manageable even though they carry the popup style.
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
