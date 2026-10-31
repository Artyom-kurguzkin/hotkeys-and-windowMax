# Proposal: sandbox test runs on a hidden desktop

## Why
Live testing took over the user's laptop: scenarios activated, fullscreened and tiled their real windows and stole
the foreground while they worked. The user needs to keep using the machine, wants a prepared sequence ("macro") that
runs without them, and wants readings to hand back instead of watching it. The sequence has to cover responsiveness,
every custom hotkey, combinations of them, and window resizing and movement comprehensively, and judge them against
"no glitches", not only "ends in the right place".

## What changes
- A `--sandbox` mode for the manager that runs on a second Windows desktop that is never shown. Injected input,
  the foreground window and the cursor go through one seam (`Desk`) that is virtual in the sandbox, so nothing can
  reach the real input stream; system cursors, other instances and the real state files are left alone.
- A macro runner inside the manager (`Sandbox/`) that executes a text macro against the live manager through the real
  key path, samples every window every ~2ms, probes the loop's responsiveness every 20ms, and writes
  `report.md` and `readings.json`.
- A fixture app (`WindowManager.Fixture`, `WmFixture.exe`) whose own-fullscreen behaviour is selectable
  (Chromium-like, re-reads on display change, no fullscreen, ignores the lie, slow, late, animated).
- `tests/sandbox.ps1` to build, create the desktop, run, and clean up; `tests/macros/smoke.macro` and `full.macro`.

## Out of scope
- The physical keyboard hook, real `SendInput` delivery, the Game Bar / Start menu interplay and multi-monitor: these
  need the real desktop and stay in `tests/scenarios.ps1` and by hand.
