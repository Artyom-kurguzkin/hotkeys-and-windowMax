# Design

## Measured on this machine (2026-10-31, Windows 11 Home, one monitor)
On a hidden desktop made with `CreateDesktop` and never switched to:
- windows are created, rendered (`PrintWindow` works; the screen DC reads blank) and hit-tested (`WindowFromPoint`);
- a low-level keyboard hook installs and sees nothing, and nothing leaks in;
- `SendInput` delivers 0 events, `GetForegroundWindow` is always null, `SetForegroundWindow` fails;
- Edge started there with its own profile appears in 0.6s; posting `WM_KEYDOWN`/`WM_KEYUP` F11 to it makes it
  fullscreen (1920x1200).
The user's foreground window was identical before and after.

## Decisions
- **One seam, `Desk`.** `Foreground`, `SetForeground`, cursor, key and mouse injection and F11 all go through it. Outside
  the sandbox each method is a direct call to the same Win32 function as before. In the sandbox they hit a virtual
  foreground / cursor and an emission log, and key messages are also posted to the target window. A virtual foreground
  raises the foreground event Windows would have, so activation-driven entry runs as in production.
- **The key path is the real one.** The runner posts `WM_APP_TESTKEY`, which runs `KeyEngine.Feed` and `Run`. In the sandbox
  `SimulateKey` also records what the app would see: the physical key if passed through, or the mask + release the real hook
  injects. `AppView` replays that stream: Alt/Win releases masked, nothing typed under the user's Alt, nothing stuck.
- **Window events.** Windows may raise none on the hidden desktop, so a 12ms pump synthesises show / move / destroy; real ones
  are counted and the environment check reports which source worked.
- **One environment check**, at the start, with one throwaway window: lie engaged, F11 delivered, foreground chain, events,
  frame bounds, UI Automation. Groups needing something missing are skipped with the reason; nothing is probed again.
- **Capability is cached by process name**, so each fixture mode runs as its own renamed copy of the exe.
- **Judging.** After every action the runner waits for the manager to report idle and for 300ms of no movement, then checks:
  invariants (fills slot, slot on screen, manager belief equals app belief, tiles meet exactly, right window has focus, no
  leaked state), the documented tiling postconditions, bystanders unchanged, the app's key stream, and the log. Quality bar:
  in-between states on screen for 12ms or more, style flicker versus the ideal, first response over 150ms, settle over 900ms,
  loop stalls of 100ms or more, key queueing, focus bounce. Defects (hard) and quality misses (ux) are reported separately.
- **Generators.** `singles`, `pairs` (every ordered pair of actions from a canonical start) and seeded `walk`s, so sequences
  nobody thought of are still tried, and a failing walk prints its replayable sequence.
- **Safety.** The sandbox uses its own message-window class, log folder and state folder; skips `ReplaceRunningInstances`,
  `RecoverFromDeadInstance`, the keyboard and mouse hooks, TrackPoint registration and all cursor-scheme calls (system cursors
  are shared by every desktop of the window station, and `Restore` would reset the real manager's pointers).

## Risks
- Fidelity: real `SendInput` delivery, foreground rules and the physical hook are not exercised.
- Whether UI Automation hint scans work on the hidden desktop is found out by the environment check, not assumed.
