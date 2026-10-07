# Design

## Context

This change ports `binds.ahk` lines 65-307. The AHK version gets the Alt state from `GetKeyState("Alt","P")`. Here, the `KeyEngine` from change 1 already tracks which keys are physically held, so its `Mods` value is the single source of truth for Alt.

## Goals / Non-Goals

**Goals:**
- Keep the AHK behavior and tuning constants exactly, and move every decision into pure code: the curve, accumulation, the gesture state machine, and the focus predicates.

**Non-Goals:**
- Making the tuning constants configurable at runtime.

## Decisions

- **Put the gesture logic in a `TrackPointScroller` class in Logic.cs.**
  - `Motion(dx, dy)` returns the wheel deltas to emit `(x, y)`, in wheel units where 120 is one notch, and starts a gesture if none is running.
  - `Stop()` ends the gesture and clears the carried remainders.
  - It keeps the AHK curve (`CurveDelta`) and the reversal rule (`AccumulateAxis`).
- **Parse raw input from bytes.** `RawMouse.Parse(span)` reads the device handle, flags and dx/dy at the x64 offsets (24/36/40). A test feeds it the same buffer as the AHK self-test, which catches layout mistakes without hardware.

### Revised after first use: the port was jittery and broke Alt

The first port copied the AHK mechanics. On each raw sample, about 100 per second, it did three things:
1. sent whole 120-unit wheel notches,
2. lifted Alt around each burst (mask key, Alt up, wheel, Alt down),
3. snapped the cursor back with `SetCursorPos`.

In use, that meant visible cursor shake, step-wise scrolling, and Alt toggling constantly underneath apps. The replacement:

- **Block cursor movement instead of undoing it.** A `WH_MOUSE_LL` hook swallows non-injected `WM_MOUSEMOVE` while a gesture runs and Alt is held, so the cursor never moves.
  - A spike measured this: with the move swallowed, all 10 raw-input reports and the full dx still arrived, and the cursor did not move. Raw input therefore still identifies the TrackPoint and drives the scroll.
  - The sample that starts the gesture has already moved the cursor. It is corrected once with `SetCursorPos` to where the cursor was when Alt went down.
- **Send smooth wheel deltas.** Each sample's curved motion is converted to wheel units: `UnitsPerNotch = 8` keeps the AHK speed. The value is sent as-is, like a touchpad or high-resolution wheel, and the fraction under 1 unit carries to the next sample. The cap is still 3 notches per sample.
- **Lift Alt once per gesture.** When the gesture starts, Alt is marked dirty (via `KeyEngine.MarkChordUsed`) and lifted logically once with `KeyEngine.Lift`. It is never pressed again: the gesture only ends on the physical Alt release, and that release is masked.

The remaining Program.cs work is to send the wheel deltas, run the mouse hook, and decide when Alt is held.
- **Use `SetTimer` on the message-only window** for hover-focus (700 ms), the gesture watchdog (30 ms), and a one-shot 50 ms timer after Alt is released. All of them run on the hook thread, so there are no locks beyond the KeyEngine's own, and the hook callback never sleeps.
- **Activate windows the way AHK's `WinActivate` does.** Call `SetForegroundWindow`. If the foreground lock refuses it, attach to the foreground window's input thread and try again. Log the window if activation still fails.
- **Find the window under the cursor** with `WindowFromPoint` plus `GetAncestor(GA_ROOT)`, which is the top-level window, like `MouseGetPos`.
- **Put the focus predicates in a `Focus` class in Logic.cs:**
  - `IsRealWindow(style)`, which change 3 reuses
  - `ShouldHoverFocus(hwnd, lastHover, altHeld, isReal)`
  - `ShouldCenter(prev, now, mouseInside)`
  - `Rect.Center` and `Rect.Contains`

## Risks / Trade-offs

- [Focus stealing is blocked for some targets, such as elevated windows] → Log the failure and carry on.
- [This TrackPoint's device ID `LEN0325` is specific to this machine] → The log lists every mouse device path, so a mismatch is visible right away.
- [Another program with low-level hooks or its own TrackPoint handler running at the same time] → This was measured in practice. `binds.ahk`, auto-started from the Startup folder, ran alongside the new program. Raw input arrived in bursts after 310/620 ms stalls, with huge coalesced deltas that each hit the cap. That felt jumpy and too sensitive. With AHK stopped there were 304 samples at a steady 10 ms, with an average queue lag of 0.3 ms. The verbose log's per-sample `lag=` field tells the two cases apart: high lag means our thread was busy, while gaps with no lag mean an upstream stall such as another hook.
- [While a gesture runs, every mouse's cursor movement is blocked, not just the TrackPoint's, because the low-level hook can't tell devices apart] → The gesture only lasts while Alt is held after the knob was used.
- [A very old app might treat every wheel message as a whole notch, so it would scroll too fast] → `ponytail:` if one shows up, add a per-app switch that groups deltas into whole notches.
- [Alt is logically up for the rest of the hold after a scroll, so Alt+Tab in the same hold types a Tab] → Accepted. Release Alt and press it again.
