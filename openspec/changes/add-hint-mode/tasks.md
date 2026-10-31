# Tasks

## 1. Pure logic
- [x] 1.1 `HintEngine` (Logic.cs): label scheme (single letters then pairs, no f), Show/Hide, Type → Narrowed / Pick / PickKeep / Reset, Dim.
- [x] 1.2 `HintTests`: labels, no-f, narrowing dims, pick hides, Shift-pick keeps, nonsense resets.
- [x] 1.3 Help entry for Alt+F in the both-Alts list.

## 2. Win32
- [x] 2.1 `Hints.Scan`: UI Automation over the windows on screen, foreground first, visibility check per badge spot, capped.
- [x] 2.2 `Hints.FocusIsTextInput`: Edit / writable Document or ComboBox / terminal classes; unknown = typing.
- [x] 2.3 Overlay: layered per-pixel-alpha window, GDI+ badges in the web app's `.hint-badge` style (10px), dim at 25%, light/dark palette, DPI scaled.
- [x] 2.4 `UseWPF` for `System.Windows.Automation`; native bindings (UpdateLayeredWindow, DCs).

## 3. Wiring (Program.cs)
- [x] 3.1 `RouteHint` on the hook thread before `KeyEngine.Feed`; same path from the test channel (`SimulateKey`).
- [x] 3.2 ~~`EVENT_OBJECT_FOCUS` → `focusIsText`~~ removed in 4.7 (the toggle is Alt+F, nothing to guess about focus).
- [x] 3.3 Toggle / letter / Esc handlers; real click on the input-sender thread (Shift held for Shift-pick).
- [x] 3.4 Hide on foreground change and at shutdown.

## 4. Verify
- [x] 4.1 `dotnet test` green: 215 (7 HintTests).
- [x] 4.2 Live (2026-10-30, via the test channel = real key path): F on Edge (PDF) + Word showed 44 badges across 6 windows in the
      web app's style (dark palette, two-letter labels); typing `aa` clicked Edge's TOC toggle (pane opened, badges gone); Esc hides.
      Still to try by hand: Shift-pick keeps badges; plain F types in Word (State shows
      `hints` state in the dump if it ever misfires).
- [x] 4.3 Scenario harness: H1 (F -> hints shown with targets) and H2 (Esc -> hidden) in tests/scenarios.ps1.
- [x] 4.4 `openspec validate add-hint-mode --strict`: valid.
- [x] 4.5 Published to dist and restarted (17:28, after the performance pass).
- [x] 4.7 Plain F -> Alt+F (2026-10-30, user: "i cant type f in word as the hints appear instead"). The focus-is-text
      machinery is gone; Alt+F is a KeyEngine binding (unit test AltF_toggles_hints_and_masks_the_alt_release); H1 uses Alt+F.
- [x] 4.6 Performance (2026-10-30): F -> badges 300-500ms (was 2-6s), loop round trip 25ms during a scan, scan 200-300ms over
      the 3 visible windows (covered ones skipped). The "3s loop stall" seen first was the harness (State matched on file
      time); it now matches a sequence number. Suite 17/17, 216 unit tests.
- [x] 4.8 Prefetch (2026-10-31, user: "why aren't you figuring them out ahead of time"): see design.md "Prefetch". HintCacheTests (13),
      tests/macros/hints.macro H9-H12 (Alt+F from a finished scan < 80ms; straight after a switch < 700ms; a moved window invalidates it;
      two presses in a row), all passing on the hidden desktop; smoke 20/21 clean, 0 defects.
- [x] 4.9 Only the focused window is labelled (user request, 2026-10-31): fewer badges, shorter labels, cheaper scan. H13 checks it.
- [x] 4.10 An Alt+Tab switch flashes the window it landed on for 1.5s (the triple-Alt border; Tab while Alt is held, shown on the Alt
      release). G3 checks it fires on a switch, G4 that a plain Alt chord does not.
