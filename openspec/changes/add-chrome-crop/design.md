# Design

## Context

These apps draw their bar inside the client area, so no window style controls it. Without code injection, the only option is geometric: move the bar off the visible area and clip it. Spike results (Edge, 144 DPI, 1920x1200 monitor, taskbar on auto-hide):

| Attempt | Result |
|---|---|
| Restored window, `SetWindowPos` up by 60 + `SetWindowRgn` | Accepted; the tab strip is hidden and the address bar is intact |
| Maximized window, plain `SetWindowPos` up by 60 | Ignored; Chromium keeps the maximized rect |
| Restored, sized to monitor + 60 | Clamped to the max track size (1226 high), leaving a gap at the bottom |
| Maximized or restored, `SetWindowPos` with `SWP_NOSENDCHANGING` | Accepted; the window stays maximized and the rect is stable after 1.5 s |

Strip heights measured with `PrintWindow`, in pixels at 144 DPI: Edge tab strip 60, Explorer tab strip about 62, Slack top bar about 55, Outlook (olk) header about 75.

## Goals / Non-Goals

**Goals:**
- Crop works with maximize, Snap and multi-monitor moves without fighting Windows.
- All geometry and state decisions are pure and unit-tested.

**Non-Goals:**
- Detecting the bar's height automatically. Per-app values plus a live tuning hotkey are used instead.
- Restoring a window by mouse-dragging its edges while it is cropped. The clip removes the invisible resize border, and window control is done with hotkeys anyway.

## Decisions

- **Classify windows once, before stripping.** `Crop.IsSelfDrawn(window, client, dpi)` checks that `client.Top - window.Top` is at most 16 DIP, which covers the maximized frame overhang. The check must happen before `WS_CAPTION` is removed, because a stripped classic window would look self-drawn afterwards.
- **Compute the crop with `Crop.Plan(window, client, n)`** (pure):
  - `target` is the window rect with `Top -= n`.
  - `region` is the client rect in target-window coordinates with its top moved down by `n`: `(client.L - window.L, client.T - window.T + n, client.R - window.L, client.B - window.T + n)`.

  The region removes the strip and the invisible borders, so nothing can leak onto a neighboring monitor.
- **Track each window with a `CropTracker`** state machine (pure). For every location change it returns one of three results:
  - `None`: the rect is the one we applied, or this exact attempt was already refused.
  - `RegionOnly(region)`: Windows moved the window back to a rect we produced earlier, for example on restore from maximize, because Windows stored our cropped rect as the normal position. Only the clip is refreshed. This prevents cropping twice.
  - `Apply(target, region)`.

  It keeps the last 8 `(target → slot)` pairs and the last attempt. `Uncrop()` returns the slot to move back to.
- **Move with `SWP_NOSENDCHANGING | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS`.** This bypasses the max-track clamp and Chromium's own maximized-rect enforcement. The flag also skips the app's `WM_WINDOWPOSCHANGING` veto, which is the point.
- **Crop height** = `round(dip * dpi / 96)`.
  - Defaults are a code table of DIP values: msedge/chrome/vivaldi 40, explorer 41, Slack 37, olk 50, Code 35, Obsidian 30, Discord 22, WindowsTerminal 40, Notepad 48. The fallback for other apps is 32.
  - Values saved in `crop.json` override the defaults.
  - Tuning uses ±2 DIP steps with a floor of 0 (`Crop.Tune`).
- **Detect the Win+Alt tap in `KeyEngine`.** A tap is armed when Win and Alt are both held and nothing else is. Any other key, including a third modifier, disarms it. The first release of Win or Alt fires `ToggleReveal` with `MaskFirst`, and the other modifier is marked dirty so its release is masked too.
- **Cover vs crop.** Self-drawn windows skip the change 3 taskbar cover. Chromium already leaves 1–2 px at the bottom for auto-hide taskbar reveal, and running both adjustments would make them fight.
- **Leave app fullscreen alone (found in the live run).** VS Code and Vivaldi in fullscreen measure `0,0 1920x1199`. Chromium stops 1px short of the bottom so an auto-hide taskbar can still be summoned. `Crop.IsAppFullscreen` therefore matches the monitor's left, top and right edges exactly and allows the bottom to be up to 2px short. A real maximized window always overhangs by its frame, so it never matches.
- **Register before stripping (found in the live run).** `SetWindowLong` waits for the target app, and out-of-context WinEvents for that window are dispatched re-entrantly during the wait. `Manage` therefore records the classification before calling `Strip`.
- **Reveal and quit** both call `Uncrop`: remove the region and move the window to its slot with the same flags.
- **Manage UWP frames.** `Chrome.IsManageable` also accepts class `ApplicationFrameWindow` despite `WS_POPUP`.

## Risks / Trade-offs

- [Windows' restore position is stored as our cropped rect] → `CropTracker`'s history recognizes it. The worst case is one extra crop step, which is visible in the log.
- [The default heights are estimates for apps that weren't measured] → The tuning hotkey and the per-app log line (`crop <app> n=<px>`) make them easy to correct.
- [`SWP_NOSENDCHANGING` skips the app's sizing logic, so some app could render incorrectly] → The log shows each crop. Alt+T or the reveal undoes it per window.
