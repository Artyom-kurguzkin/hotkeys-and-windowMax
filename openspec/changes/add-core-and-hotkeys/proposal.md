# Proposal

## Why

`binds.ahk` currently provides the hotkeys. The next goal is a borderless window manager, which needs window-event hooks, DWM calls and logic that can be tested, all of which AutoHotkey makes awkward. This change sets up the C# program and moves the keyboard binds into it, so it can replace the hotkey half of the AHK script.

## What Changes

- Add a new .NET 10 Windows app, `WindowManager`. It has a single-thread message loop and a low-level keyboard hook.
- Port all keyboard binds from `binds.ahk` with identical behavior:
  - mouse emulation
  - Escape
  - line navigation
  - new line below
  - desktop switching
  - Alt+hjkl scrolling
- Pressing a bound chord never opens an app's menu bar or the Start menu.
- Ctrl+Alt+Shift+Q quits. Starting a second instance replaces the first, like `#SingleInstance Force`.
- A log file records startup, every hotkey action and every failed Win32 call. `--verbose` also logs raw key events.
- Add an xUnit test project that covers the key-handling logic.

## Capabilities

### New Capabilities
- `hotkeys`: global keyboard shortcuts, what each one emits, and the guarantee that shortcuts using Alt or Win don't open menus.
- `diagnostics`: the log file, what it contains and how verbose it is, plus the single-instance and quit lifecycle.

### Modified Capabilities

## Impact

- Adds `WindowManager/`, `WindowManager.Tests/` and `WindowManager.sln`.
- `binds.ahk` is not touched. Running both programs at the same time makes every bind fire twice.
