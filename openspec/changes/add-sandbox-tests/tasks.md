# Tasks

## 1. Seam
- [x] 1.1 `Desk`: foreground, cursor, keys, mouse, F11; virtual in the sandbox, recorded.
- [x] 1.2 Program.cs and MonitorLie.cs call `Desk` instead of Windows directly; `CursorTheme` does nothing in the sandbox.
- [x] 1.3 Sandbox start-up: own message class, own folders, no hooks, no TrackPoint, no instance replacement.

## 2. Runner
- [x] 2.1 Pure rules (`SandboxLogic.cs`) with unit tests: chords, macro parsing and `for`, statistics, trajectories, invariants, tiling postconditions, key-stream checks.
- [x] 2.2 Runner: fixtures, sampler, loop probe, settle, analysis, generators, single environment check, report.
- [x] 2.3 Fixture app with seven fullscreen behaviours.
- [x] 2.4 `tests/sandbox.ps1`, `tests/macros/smoke.macro`, `tests/macros/full.macro`.

## 3. Verify
- [x] 3.1 Builds; 57 new unit tests pass.
- [x] 3.2 Live runs on the hidden desktop (2026-10-31): see add-monitor-lie/PLAN.md "SANDBOX-DRIVEN FIXES". Run it again with `tests\sandbox.ps1`; readings in `tests\out\sandbox\report.md`.
