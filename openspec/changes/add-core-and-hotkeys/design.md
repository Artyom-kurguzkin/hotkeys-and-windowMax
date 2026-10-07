# Design

## Context

This is a new C# project next to `binds.ahk`. The AHK script relies on AutoHotkey's hotkey engine for two things: it lifts held modifiers before sending output, and it masks Alt and Win releases so menus don't open. Both have to be reimplemented explicitly here.

## Goals / Non-Goals

**Goals:**
- Key handling is a pure state machine, so every bind and every masking rule is unit-tested.
- One thread owns the hook and the message loop, the same model as AHK. That thread will also host WinEvent hooks and raw input in later changes.

**Non-Goals:**
- A configurable keymap file. The table lives in code until it needs to change often.
- Focus, TrackPoint and window-chrome features. Later changes add them.

## Decisions

- **Use `WH_KEYBOARD_LL` instead of `RegisterHotKey`.** RegisterHotKey can't reliably bind a bare PageUp, can't tell left Alt from right Alt, and can't observe modifier releases, which the masking needs.
- **Put key handling in a `KeyEngine` state machine in Logic.cs.**
  - Input is `(vk, isDown, injected)`.
  - It tracks which modifiers are physically held from the events it sees, instead of calling `GetAsyncKeyState`, so tests are deterministic.
  - Output is `{Suppress, Act, MaskFirst}`.
  - If a key-down was suppressed, its key-up is suppressed too.
- **Store bindings in a table** that maps `(required modifiers, leftAltOnly, vk)` to an `Act`. The held modifiers must match the required set exactly. Left and right are ignored except where `leftAltOnly` is set. This mirrors AHK hotkeys written without `*`.
- **Build output with `KeyEngine.Lift(held)` and `KeyEngine.Restore(lifted, nowHeld)`.** Together they produce the injected sequence in this order:
  1. the mask key (0xE8) if Alt or Win is held
  2. key-ups for the held modifiers
  3. the output itself
  4. key-downs that restore the held modifiers

  Both are pure and unit-tested. Program.cs only passes their result to `SendInput`. `Restore` re-presses only modifiers that are still physically held, so a modifier released while output is being sent never gets stuck down.
- **Mark modifiers dirty.** When a shortcut fires, any held Alt or Win becomes dirty, and its later physical release gets `MaskFirst`. This is needed because Windows sees the injected re-press followed by the physical release as a lone Alt tap, which opens the menu bar.
- **Run wheel scrolling on a worker task.** The 10 ms gaps would otherwise block the hook thread. `SendInput` is safe to call from any thread.
- **Enforce a single instance through a message-only window** of class `WindowManagerMsg`.
  - A new instance finds that window, posts `WM_CLOSE` to it, and waits up to 3 s for the old process to exit.
  - The old instance therefore runs its normal shutdown path. In a later change, that path restores window styles.
  - The same window becomes the Raw Input target in a later change.
- **Logging** uses a static `Log` class with no package. It opens the file, appends one line and closes it again for every line, sharing the file with other processes. A long-lived writer was tried first: while a replacing instance and the old one both ran, they overwrote each other's lines, because each process kept its own file position. `Log.Win32(what)` records `Marshal.GetLastWin32Error()`.

## Risks / Trade-offs

- [Windows silently removes the hook if the callback runs longer than `LowLevelHooksTimeout`] → The callback only does a table lookup and a `SendInput` call. Slow work goes to a worker.
- [The hook can't see keys while an elevated window has focus (UIPI)] → Documented. Run the program elevated through a scheduled task if this matters.
- [Running alongside binds.ahk fires every bind twice] → Stop the AHK script before starting the program.
