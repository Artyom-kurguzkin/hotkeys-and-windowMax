# Proposal

## Why

Title bars, borders and caption buttons waste space and add visual noise. The goal is for every application window to fill the area Windows gives it, as if that area were its own monitor. Window control moves to hotkeys. Layout stays with Windows: maximize, Snap and moving between monitors.

## What Changes

- Every real application window has its title bar removed. Windows' own borders, accent color and rounded corners are also turned off. Dialogs, popups and tool windows are left alone.
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
