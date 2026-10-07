# Proposal

## Why

Title bars, borders and caption buttons waste space and add visual noise. The goal is for every application window to fill the area Windows gives it, as if that area were its own monitor. Window control moves to hotkeys. Layout stays with Windows: maximize, Snap and moving between monitors.

## What Changes

- Every real application window has its title bar removed. Windows' own borders, accent color and rounded corners are also turned off. Dialogs and popups are left alone.
- Windows can still be resized, snapped, maximized and moved between monitors with Windows' own keys (Win+Arrow, Win+Shift+Arrow).
- A maximized window covers the whole monitor, taskbar included.
- If an app puts its title bar back, it is removed again.
- New hotkeys:
  - Alt+Q closes the active window.
  - Alt+M toggles maximize.
  - Alt+N minimizes.
  - Alt+T toggles the title bar of the active window back on or off.
- Quitting (Ctrl+Alt+Shift+Q or a replacing instance) restores every window's title bar and border.
- `--dump` lists every top-level window and how it is classified, which helps diagnose odd apps.

## Capabilities

### New Capabilities
- `borderless-windows`: which windows lose their frame, how they keep working with Windows' layout, the taskbar cover, and restoring frames.
- `window-hotkeys`: hotkeys that replace the caption buttons.

### Modified Capabilities

## Impact

- New in Program.cs: WinEvent hooks, style changes on other processes' windows, and DWM attribute calls. New `Chrome` logic in Logic.cs.
- Apps that draw their own title bar are untouched by this change. That is `add-chrome-crop`.
- Elevated windows are only affected when the program itself runs elevated.
