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
- Detecting the bar height automatically. Per-app default values are used, overridable only by hand-editing `crop.json`.
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
  - Defaults are a code table of DIP values: msedge/chrome 40, vivaldi 30 (measured with a red test page: title row only, with tabs on the side), explorer 41, Slack 37, olk 50, Code 35, Obsidian 30, Discord 22, WindowsTerminal 40, Notepad 48. The fallback for other apps is 32.
  - Values saved in `crop.json` override the defaults.
  - Removed at the user's request: a Win+Alt+PageUp/PageDown tuning hotkey that wrote `crop.json`. A stray key press could have changed a good setting, so heights now change only by deliberate edit.
- **Detect the Win+Alt tap in `KeyEngine`.** A tap is armed when Win and Alt are both held and nothing else is. Any other key, including a third modifier, disarms it. The first release of Win or Alt fires `ToggleReveal` with `MaskFirst`, and the other modifier is marked dirty so its release is masked too. The reveal applies to the foreground window only (changed at the user's request from an all-windows toggle): `Program.revealed` is a set of windows.
- **Cover vs crop.** Self-drawn windows skip the change 3 taskbar cover. Chromium already leaves 1–2 px at the bottom for auto-hide taskbar reveal, and running both adjustments would make them fight.
- **Snap Layouts: retry instead of trusting the first refusal (found in use).** With Win+Z, Windows re-applies each window's zone position after its animation, *after* our crop move. The tracker treated that as a permanent app refusal and never re-cropped. The clip, made for the stretched rect, then cut a 30 px gap at the top and a few px at the side and bottom.
  - `CropKind.Refused` now triggers a re-check after 300 ms, up to `MaxRetries` (3) per slot. The tracker ignores further events on that slot until `Retry()`.
  - Removing the clip immediately caused an event storm: SetWindowRgn reports a location change, which was refused again, hundreds of times per ms. So the clip is only removed after the retries are exhausted.
  - The re-check also resolves stale refusals, events from before our async move landed, by refreshing the clip.
- **Handle location changes outside the WinEvent callback.** SetWindowRgn and SetWindowLong dispatch nested WinEvents synchronously, which re-entered the crop logic mid-step. Symptoms: impossible refusal counts, a missed retry, and "strip" logged after "crop". `EVENT_OBJECT_LOCATIONCHANGE` now only records the window and posts `WM_APP+4`. The crop runs from the message loop, once per window per burst.
- **Close Chromium's bottom sliver (found in use).** When the taskbar auto-hides, maximized Chromium/Electron windows stop their content 2 px short of the monitor bottom, so the mouse can still reach the taskbar. Measured: VS Code and Discord client bottom at 1198 on a 1200 px panel, which shows a thin line of wallpaper. `Crop.MaximizedVisible` makes `FillVisible` stretch the window down by that gap, if it is at most 4 px. Verified: every maximized window on that monitor now covers it exactly. Trade-off: summoning the auto-hidden taskbar relies on Windows' own edge handling rather than those 2 px.
- **Fill the snap area (found in use).** Snap aligns a window's DWM frame bounds to the screen. Disabling the DWM frame for a cropped window (the leak fix) makes the frame bounds equal the window rect. Snap then put Vivaldi's *window rect* exactly on the left third, `(-640,-1440 853x1440)`, while its content sits 7 px inside on the left, right and bottom. The result was 7 px gaps on the left and at the bottom.
  - For non-maximized windows, `Crop.FillVisible` grows the window by its own client insets so the content fills the frame-bounds rect.
  - A window back on a slot the tracker computed itself (after reveal or Alt+T) is not widened again. Without that check it overshot by 7 px.
  - Verified live with the measurement tool: Win+Left, Win+Right, Win+Left, then reveal and hide all left 0 px gaps on the left, top and bottom.
- **Subtract the off-screen part of the bar (found in use).** A maximized Vivaldi's content started 8 px above its monitor (client top -1448, monitor top -1440). Cropping the full 30 px bar from the content top therefore cut 8 px into the page, and through the top edge of the side tabs. `Crop.VisibleBar` reduces the crop by `monitor.Top - slotClient.Top` when positive, and `CropTracker.OnLocation` takes the monitor top. Verified on the real window: the page starts on the monitor's first line and the side tab's top edge is visible.
- **Turn off the DWM backdrop and frame while a window is cropped (found in use).** On a multi-monitor layout with a 2560x1440 monitor directly above the laptop panel, every maximized cropped window leaked a band onto the bottom of the upper monitor. The window region was intact: hit-testing in the band reached the desktop, so Windows treated the area as outside the window, yet DWM still painted there.
  - First, the Windows 11 system backdrop (`DWMWA_SYSTEMBACKDROP_TYPE`, type 2/Mica on VS Code) filled the whole window rect in #202020. Measured: 63 rows with it on, 0 with `DWMSBT_NONE`.
  - With the backdrop off, the DWM frame was left as a 9 px #2B2B2B line. `DWMWA_NCRENDERING_POLICY = DWMNCRP_DISABLED` removed it, to 0.
  - So each crop step records the original backdrop (also in `state.json`), sets it to none and disables frame rendering. Every uncrop path restores both: reveal, Alt+T, app fullscreen, quit and recovery.
  - The visible area of a cropped window is opaque app content, so losing the backdrop there is not visible.
- **Handle sideways resizes (found in use).** A snapped or floating Vivaldi was resized by dragging its width. Each step reported our cropped top (-60) with the height clamped back to the max track size (1226). The tracker treated each step as a new slot and cropped again, to -120. That left a 44 px gap at the bottom and hid 60 px of content. Now, a rect whose top equals the applied top keeps the current slot's top and bottom and takes only the new left and right.
- **Leave app fullscreen alone (found in the live run).** VS Code and Vivaldi in fullscreen measure `0,0 1920x1199`. Chromium stops 1px short of the bottom so an auto-hide taskbar can still be summoned. `Crop.IsAppFullscreen` therefore matches the monitor's left, top and right edges exactly and allows the bottom to be up to 2px short. A real maximized window always overhangs by its frame, so it never matches.
- **Register before stripping (found in the live run).** `SetWindowLong` waits for the target app, and out-of-context WinEvents for that window are dispatched re-entrantly during the wait. `Manage` therefore records the classification before calling `Strip`.
- **Reveal and quit** both call `Uncrop`: remove the region and move the window to its slot with the same flags.
- **Manage UWP frames.** `Chrome.IsManageable` also accepts class `ApplicationFrameWindow` despite `WS_POPUP`.

## Risks / Trade-offs

- [Windows' restore position is stored as our cropped rect] → `CropTracker`'s history recognizes it. The worst case is one extra crop step, which is visible in the log.
- [The default heights are estimates for apps that weren't measured] → The per-app log line (`crop <app> n=<px>`) shows the value in use. Correct it in `Crop.Defaults`, or locally in `crop.json`.
- [`SWP_NOSENDCHANGING` skips the app's sizing logic, so some app could render incorrectly] → The log shows each crop. Alt+T or the reveal undoes it per window.
