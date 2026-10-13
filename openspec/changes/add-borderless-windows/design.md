# Design

## Context

Windows has no per-window virtual monitor. Faking one would mean injecting code into processes, writing a display driver, or reparenting windows across processes, and all three are fragile. Instead, this change removes the non-client frame and leaves layout to Windows. The client area then fills whatever rect Windows assigns. komorebi and GlazeWM use the same technique.

## Goals / Non-Goals

**Goals:**
- Work with Windows' layout (Snap, maximize, Win+Shift+Arrow) instead of replacing it.
- Every decision is pure and unit-tested. The Win32 side gets one integration test against a window the test creates itself.

**Non-Goals:**
- Apps that draw their own title bar inside the client area. That is change 4.
- Persisting original styles across crashes.

## Decisions

- **Strip only `WS_CAPTION`.** Keep `WS_THICKFRAME` and the min/max box bits. Snap and maximize need a resizable frame. On Windows 10/11 the resize border is drawn transparent, so a caption-less resizable window looks frameless. Apply with `SetWindowPos(SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)`.
- **Turn off the DWM border and rounding** with `DwmSetWindowAttribute`:
  - `DWMWA_BORDER_COLOR` (34) set to `DWMWA_COLOR_NONE` (0xFFFFFFFE)
  - `DWMWA_WINDOW_CORNER_PREFERENCE` (33) set to `DWMWCP_DONOTROUND` (1)
  - Restoring sets them back to `DWMWA_COLOR_DEFAULT` (0xFFFFFFFF) and `DWMWCP_DEFAULT` (0).
  - Failures are logged with their HRESULT. Whether the call works on other processes' windows is checked in the smoke test.
- **Cover the taskbar with `Chrome.CoverRect(monitor, window, client)`.** A maximized frame-only window has its client area on the work area, with the frame hanging just off it. The target window rect is the monitor rect grown by the current frame insets (window rect minus client rect), so the client area lands exactly on the monitor. The function is pure. It is applied only when `IsZoomed` is true and the client area differs from the monitor, so applying it twice changes nothing and no loop guard is needed.
- **Detect windows with events.** `SetWinEventHook` runs out of context and skips this program's own process. It listens for:
  - `EVENT_OBJECT_SHOW`: strip new windows
  - `EVENT_OBJECT_LOCATIONCHANGE`: re-strip a returning caption, and cover the monitor when maximized
  - `EVENT_OBJECT_DESTROY`: forget the window

  Only `OBJID_WINDOW` with child id 0 is handled. `EnumWindows` runs once at startup.
- **Keep state in `managed: Dictionary<hwnd, uint origStyle>` and `userShown: HashSet<hwnd>`.** Alt+T moves a window into or out of `userShown`. The re-strip skips windows in that set.
- **Caption hotkeys post `WM_SYSCOMMAND`** (`SC_CLOSE`, `SC_MAXIMIZE`, `SC_RESTORE`, `SC_MINIMIZE`) to the foreground window. Each app then reacts as if its own button had been clicked, which works better than calling `ShowWindow` from outside. The messages are posted, not sent, so a hung app can't block the hook thread.
- **Use `SWP_ASYNCWINDOWPOS` for every cross-process `SetWindowPos`**, so a hung target can't block this program's message loop.
- **Make the process per-monitor DPI aware (V2)** before doing anything else. Without it, Windows hands over DPI-virtualized rects. The first `--dump` on a 150 % display showed this: 1280x800 instead of 1920x1200, which would make the cover math wrong.
- **Don't retry a refused cover.** Electron apps snap back from the cover move, and every snap-back fires another `LOCATIONCHANGE`. `Chrome.AlreadyTried` skips a move whose `(window, target)` pair matches the last attempt. A retry happens once the window has moved somewhere new.
- **Win32 code lives in a public `Frames` class** (Frames.cs), so `Win32Tests` can strip, restore and cover a window the test creates. One test maximizes such a window and asserts that its client area equals the monitor, which is the measured behavior.
- **`--dump`** runs before the single-instance takeover and changes nothing. It writes to stdout, so it works when piped, and to the log.
- **Survive sleep and display changes (found in use).** After an overnight sleep the log showed zero window events handled on wake: the program never noticed, and crops and layouts stayed broken.
  - **Detecting it:** the program's window is message-only, so it gets no broadcasts (`WM_DISPLAYCHANGE`, `WM_POWERBROADCAST`). It registers with `RegisterSuspendResumeNotification`, polls the monitor set (device + bounds signature) on the existing 700 ms timer and in every move batch, and treats a >30 s gap between timer ticks as a wake. `GetTickCount64` counts through sleep while timers don't fire, so this works even if the power notification never arrives.
  - **Freezing:** while suspended or while the monitor set is changing, window moves are ignored entirely: not cropped, not covered, not recorded. Windows' own evacuation moves would otherwise overwrite the known-good layout.
  - **Repairing:** once the monitor set is unchanged for 2 s, phase 1 drops every clip and crop history, and puts windows whose monitor is back unchanged at their recorded place (placement normal, refit, re-maximize, as in moving between monitors). 500 ms later phase 2 resumes tracking and re-applies `Strip` (DWM attributes can reset on wake), crops and covers.
  - **What is recorded:** the rect Windows laid out, never our stretched crop rect. For a cropped floating/snapped window that is its visible clip on screen, not the slot widened by the invisible borders: recording the widened slot made it come back +20×10 px after a simulated wake.
  - **Verified** with a simulated suspend/resume (power messages sent to the message window) on a floating Edge window that was moved away and un-clipped in between: it came back to the exact pre-sleep rect and clip. Not live-tested: a real monitor dropping out, and a real overnight wake (the log lines `resume`/`woke up`/`displays settled` will show which path fired).

## Risks / Trade-offs

- [Some apps add `WS_CAPTION` back on every resize] → The re-strip runs on `LOCATIONCHANGE`. The log shows any flapping.
- [A crash leaves windows without a title bar] → Restarting the program manages them again, and Alt+T brings a title bar back. `ponytail:` persisting styles to disk is the upgrade if this happens in practice.
- [`SetWindowLong` sends `WM_STYLECHANGING` synchronously, so a hung app can stall the call] → This is accepted and noted in a `ponytail:` comment. The fix would be to move style changes to a worker thread.
- [Logoff without a clean quit] → The windows are being destroyed anyway, so no restore is needed.
