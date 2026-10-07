# Proposal

## Why

Many apps draw their own title bar inside the window: Chrome, Edge, Vivaldi, VS Code, Electron apps (Slack, Discord, Obsidian), File Explorer's tab strip, new Outlook, Windows Terminal, Notepad and UWP apps. Removing `WS_CAPTION` doesn't touch those bars, so tab strips and min/max/close buttons stay visible. They should be hidden by default and shown on demand.

## What Changes

- Windows that draw their own top bar have it cropped off. The window is stretched upward by the bar's height and the bar is clipped, so the content still fills the area Windows gave it. This works for maximized, snapped and floating windows.
- Cropped bars never show on a monitor above.
- A tap of Win+Alt (press both, release, no other key) shows the hidden bars on all windows. A second tap hides them again. Neither tap opens the Start menu.
- Alt+T on a cropped window shows its bar until Alt+T is pressed again.
- Crop height is set per app (in DPI-independent units) and scaled to each window's DPI.
  - Win+Alt+PageDown crops 2 units more for the active app, and Win+Alt+PageUp crops 2 units less.
  - Adjusted values are saved and reused.
- UWP app frames (`ApplicationFrameWindow`) become manageable even though they carry the popup style.
- Quitting removes all crops.

## Capabilities

### New Capabilities
- `chrome-crop`: detecting self-drawn top bars, cropping them, revealing them, and tuning the crop per app.

### Modified Capabilities

## Impact

- Adds crop state and logic to Logic.cs, and region and move calls to Frames.cs.
- Adds a small JSON file, `%LOCALAPPDATA%\WindowManager\crop.json`, read and written with System.Text.Json, which is part of the BCL.
- Spike findings this design depends on (Edge at 144 DPI):
  - A maximized Chromium window ignores a normal `SetWindowPos` stretch, but accepts one with `SWP_NOSENDCHANGING` and stays maximized.
  - Restored windows are otherwise clamped to the monitor's max track size.
