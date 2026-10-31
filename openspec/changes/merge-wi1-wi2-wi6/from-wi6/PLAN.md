# Plan: universal monitor-boundary lie (replace per-app cropping)

## Goal
Make every app think its monitor is the slot Windows gave the window. The app's
own fullscreen (F11) then fills that slot exactly, with its own chrome hidden by
its own code. No per-app bar heights, no clipping. One universal mechanism.

## KNOWN SIDE EFFECT — Xbox / gaming overlays keep popping up (2026-10-19)
Symptom: Xbox-branded overlays appear repeatedly during normal use - the Xbox Game Bar,
and (found later) an Xbox-app overlay that launches specifically on Win+Up.

### Root cause (confirmed by process trace, NOT our key injection)
The lie model drives apps into their OWN fullscreen with F11 constantly - on activation,
on every Win+Arrow / Win+Z tile, and on reveal. Windows' game-detection treats "an app
just went fullscreen" as "a game launched" and launches whatever gaming overlays are
installed. Our code never sends a Game Bar hotkey (grep: we send F11, Win+Z for Snap
Layouts, Ctrl+Win+Arrow for desktops - never Win+G). The trigger is Windows itself,
inherent to app-fullscreen. The old crop approach never fired it (the window stayed
un-fullscreen).

### THERE ARE THREE+ SEPARATE OVERLAYS (this is why removing one didn't fix it)
1. `Microsoft.XboxGamingOverlay`  -> GameBar.exe, GameBarFTServer, XboxGameBarWidgets (the classic Game Bar)
2. `Microsoft.Edge.GameAssist`    -> EdgeGameAssist.exe (Edge's own overlay; fires when Edge goes fullscreen)
3. `Microsoft.GamingApp`          -> XboxPcAppCE (the Xbox APP's compact overlay; this is the one that launched on Win+Up)
   (also `Microsoft.XboxSpeechToTextOverlay` - another overlay)

### How to diagnose which one it is (don't guess - trace it)
Settings toggles and killing processes DON'T help: Windows relaunches them on detection.
Run a process monitor and press the triggering key for real (the manager's keyboard hook
ignores injected keys, so a scripted WM_APP_SNAP does NOT reproduce it - you must press it):
  scripts/procmon.ps1 logs every NEW process to %LOCALAPPDATA%\Temp\procmon.log.
  Press Win+Up etc.; the Xbox exe that appears names the package to remove.
(Win32 game processes seen: GameBar*, EdgeGameAssist*, XboxPcApp*, GamingServices.)

### PERMANENT FIX (applied 2026-10-19, verified; scripts: openspec/changes/add-monitor-lie/scripts/)
### kill-xbox.ps1 (run elevated) does the admin steps; procmon.ps1 is the diagnostic monitor.
Per-user (no admin) stops it now; admin makes it survive Windows/Edge updates.
- No-op protocol handlers in HKCU\Software\Classes for `ms-gamingoverlay`, `ms-gamebar`,
  `ms-gamebarservices` (shell\open\command -> systray.exe) so nothing raises the
  "you'll need a new app to open this ms-gamingoverlay link" popup.
- Per-user: `Get-AppxPackage <name> | Remove-AppxPackage` for the four packages above.
- Admin (kill-xbox.ps1, run elevated): machine policies
  `HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR\AllowGameDVR=0`,
  `HKLM\SOFTWARE\Policies\Microsoft\Edge\GameAssistEnabled=0`,
  plus `Remove-AppxProvisionedPackage -Online` for all four so they don't reinstall.
  (Removing GamingApp while it is running crashes the remover with 0xC0000006 but the
  removal still completes - kill XboxPcApp* first, or re-run the per-user Remove-AppxPackage.)
Verify: `Get-AppxPackage Microsoft.GamingApp` -> nothing; press Win+Up -> no Xbox process.
Revert: reinstall "Xbox"/"Xbox Game Bar" from the Microsoft Store, delete the HKLM policy
keys and the HKCU\Software\Classes ms-gamebar* keys.

### Implication
This is a standing cost of the app-fullscreen approach and lives on the MACHINE, not in
the window manager - a fresh machine running this build re-hits it until kill-xbox.ps1 is
run there. If the fullscreen approach is kept, ship kill-xbox.ps1 as a documented setup
step. The hybrid (fullscreen only maximized windows, crop the rest) avoids it entirely.

## Mechanism (the only one that works from outside the app's UI)
The app learns its monitor size by calling `GetMonitorInfo` / `MonitorFromWindow`
inside its own process. To change the answer we run a small native DLL inside each
app process and redirect those calls. Standard documented technique (same family
as AutoHotkey, input remappers, accessibility tools).

- `SetWindowsHookEx(WH_CBT, ..., 0)` with a system-wide hook loads `monlie.dll`
  into every process that has windows.
- Inside each process the DLL patches `GetMonitorInfoW`, `GetMonitorInfoA`,
  `MonitorFromWindow`, `MonitorFromRect`, `EnumDisplayMonitors` via MinHook.
- The manager writes each managed window's slot onto the window with
  `SetProp(hwnd, "MonLieSlot", packed-rect)`. The hooked functions read that prop
  for the asking window and return the slot as `rcMonitor` + `rcWork`. Windows with
  no prop get the real answer (untouched).
- Windows itself is never lied to, so Snap / maximize / taskbar / multi-monitor
  keep working. Only the app's self-query is changed.

## Risks to settle BEFORE building the real thing (spike order)
1. **Does Chromium even load a third-party DLL?** Chrome/Edge have a policy that can
   block non-allowlisted DLLs in the browser process. If blocked, the browsers (the
   main apps) can't be lied to and this approach is dead for them. TEST FIRST.
2. **Does the lie actually produce a gap-free fullscreen** when the app does its own
   F11 into the fake monitor?
3. Bitness: a hook DLL only loads into processes of its own bitness. Need x64 and
   x86 copies + a tiny x86 broker. Most modern apps are x64; defer x86 until needed.

## Exclusions (safety)
- Games with anti-cheat: a foreign DLL in their process can get the account banned.
  Exclude by process name; this is the one unavoidable list. `monlie.dll` also
  no-ops in any process whose main module is in an excluded set.
- Elevated apps: skipped unless the manager runs elevated.

## Step 0 — toolchain (no compiler present)
- Install LLVM (clang-cl) via winget: smaller and faster than full VS Build Tools,
  and enough to build a plain Win32 DLL. `winget install LLVM.LLVM`.
- Vendor MinHook (single .c + headers, MIT) under `native/minhook/`.
- If winget install needs elevation/consent, STOP and report — do not force it.

## Step 1 — SPIKE (throwaway, no changes to the running manager)
Build `native/monlie.dll` (x64) that hooks only `GetMonitorInfoW` + `MonitorFromWindow`
and returns a hard-coded slot for windows carrying the `MonLieSlot` prop.
Build a throwaway `spike.exe` that:
  - takes an hwnd + slot rect on the command line,
  - installs the WH_CBT hook, SetProp on the hwnd,
  - waits, then removes the hook.
Manual: set the slot on one Vivaldi window and one VS Code window, press F11,
measure the content rect.
GO / NO-GO:
  - Chromium loaded the DLL? (check with a debug log line the DLL writes on attach)
  - F11 content == slot, zero gaps?
If NO-GO for Chromium → report, keep cropping, stop.

## STABILISATION + TEST HARNESS (2026-10-30) — 15/15 live scenarios green, 208 unit tests
The instability was real and had FIVE root causes, every one found from evidence the new harness
produced (tests/wm.ps1 + tests/scenarios.ps1), not from guessing:
1. F11 is a TOGGLE and the manager assumed "F11 = enter". An app left fullscreen by a killed
   manager got flipped OUT on every probe -> "fullscreened to (-12,0 1944x754)" -> falsely
   not-capable -> every later tile/reveal "not lie-managed". Fix: LieLogic.IsAppFullscreen +
   InAppFullscreen decide how many F11s to send at EVERY site (enter, re-tile, reveal, Win+Z,
   shutdown). Shutdown's blind F11 had been the thing leaving apps fullscreen in the first place.
2. False "not-capable" from a race: the injected DLL opened its mapping lazily with a 2s retry,
   the probe sent F11 ~400ms after SetSlot -> app fullscreened to the REAL monitor -> marked
   not-capable for the session. Fix: DLL retries every 300ms, mapping pre-created at Manage
   (MonitorLie.Touch), and a 3-way ProbeVerdict (Capable / LieMissed->retry / NotCapable).
3. Garbage slots: a non-maximized frame overhanging the monitor became an off-screen slot.
   Fix: LieLogic.Clamp; SlotOf uses the VISIBLE frame clamped to the monitor.
4. Bystander takeover: leaving fullscreen bounces focus; activation-driven entry probed the
   bystander (Word, the Claude app) and stole the foreground, so every later key hit it.
   Fix: Op(target, ...) wraps every lie sequence; activation entry is suspended while an op is
   in flight and debounced (ActivateSettleMs); on completion the op's TARGET is re-asserted
   as foreground (a fullscreen transition can leave a bystander foreground).
5. Fixed sleeps raced the app's own transition (same tile passed one run, failed the next).
   Fix: MonitorLie.WaitRect/LeaveFullscreen/EnterFullscreen wait for OBSERVED state; rapid
   Win+Arrow is coalesced (pendingSnap) so queued ops never act on stale state.
Also: the hook DLL stays mapped in every GUI process until each next pumps a message, so the
build could not overwrite bin\monlie.dll; the csproj now moves the in-use copy aside first.

LAG ("press anything, wait ~1s", even a desktop switch) - 2026-10-30, two causes:
a) The user was running a STALE dist build (dist\WindowManager.exe started 10:56 "replacing
   running instance" of the day's Debug build): old lie code with probe-on-hover and 1.5s sleeps.
   dist is only what the user runs if it is republished; it now is (dotnet publish -o dist).
b) Structural: lie operations (probes waiting ~1.5s, re-tiles) ran on the SAME input-sender
   thread as every injected keystroke, so a desktop switch / scroll pressed during a probe
   queued behind it (log: "action DesktopRight", then a probe, then the switch). Fix: Op() runs
   on its own "lie-worker" thread (opbox); SendInput is fine from any non-hook thread, and user
   output never waits behind a fullscreen transition. Rule: NOTHING slow goes on the sender.
Found by the harness in the same pass: minimizing a window still in its own fullscreen restores
it into an unpredictable state (then the re-enter toggles wrong) - Win+Down now leaves fullscreen
(waited) before minimizing; and the non-probe enter claimed "lie ok" blind - it is now verified
by the same ProbeVerdict as a probe and retried (bounded), never marked not-capable.
Result after all of it: 15/15 live, 0 MISMATCH, 208 unit tests; dist published + restarted.

HOW TO TEST / TROUBLESHOOT FROM NOW ON (do not go back to screenshots and guessing):
- `. .\tests\wm.ps1` then `.\tests\scenarios.ps1 -Proc msedge` : Cycle (close/build/relaunch),
  drive every scriptable case through the REAL key path (WM_APP_TESTKEY -> KeyEngine.Feed ->
  Run, same as a physical key), assert on `State` (JSON the manager writes: what it THINKS vs
  what Windows SAYS), PASS/FAIL table. A failure is diagnosed from the log, not re-guessed:
  `LogSince`, `Mismatches` (self-check: a lie window whose rect != slot for 2 ticks logs
  `lie MISMATCH`, excluding windows mid-probe/mid-op), and `Diff { action }` (new processes +
  windows = what an action launched). `Activate` throws if the target can't be made foreground,
  so a scenario fails fast instead of 14 misleading FAILs; `Prepare` un-minimizes the target.
- Still needs real hands (hook ignores injected keys only for the real-key path, which the
  channel bypasses; these need a human or a second monitor): Win+Z zone pick, Snap Layouts,
  multi-monitor, sleep/wake, drag-snap.

HINT MODE LAG + PHANTOM STALLS (2026-10-30 afternoon) - "it is stupidly slow and laggy":
a) The UIA scan ran on the loop (2s on a browser tab + a ribbon) and every focus event ran a UIA
   query on the loop. Fix: scan + focus query on pool threads (Task.Run; NOT the lie-worker: F
   right after an activation sat behind that window's fullscreen transition), CacheRequest
   (one round trip for all BoundingRectangles instead of one per element), result posted back
   (WM_APP_HINTSREADY / WM_APP_FOCUSRESULT). Root element is fetched OUTSIDE the cache scope
   (inside it, AutomationElementMode.None gives an element with no native ref -> "0 targets").
b) Covered windows were scanned anyway (~200ms each for 0 badges): Hints.AnyVisible samples
   3x3 points of the rect against WindowFromPoint and skips a fully covered window.
   Scan: 1121ms/6 windows -> 200-300ms/3 windows; F -> badges 300-500ms; loop round trip 25ms.
c) THE HARNESS LIED: `State` waited for the dump file's LastWriteTime >= request time; a dump
   landing in the same millisecond read as older, so State sat out its 3s deadline - measured
   as a phantom "3s loop stall" that no instrumentation in the manager could find. Now every
   dump echoes a sequence number (DUMPSTATE wParam -> state.seq) and State matches on it, and
   THROWS if the manager never answers (a real stall fails loud).
d) Found by the suite once State told the truth: hover-focus fired on a RESTING pointer when a
   window slid out from under it (Win+Right on Edge -> VS Code behind is now under the mouse ->
   VS Code foreground 700ms later, every later assertion hit the wrong window; a user loses the
   window they just tiled the same way). Focus.ShouldHoverFocus now requires the pointer to
   have moved since the last tick (unit test Hover_ignored_when_pointer_did_not_move).
STALL FINDER (permanent): anything on the loop over 100ms is logged `SLOW ...ms on the loop:
   msg 0x.. wParam=..` (DispatchMessage, WinEvent callback, keyboard hook); worker items are
   named and logged over 1s; hint scans log per-window `0xHWND:targets/ms`. Read the log for
   SLOW before theorising about lag.
Result: 17/17 live, 0 MISMATCH, 216 unit tests; dist published + restarted (17:28).

## IMPLEMENTATION STATUS (2026-10-16)
DONE & building (195 tests pass), running live as the Debug build:
- MonitorLie engine + Native P/Invokes + monlie.dll shipped. UseLie switch gates the new path.
- Manage -> lie path (no strip/crop). Foreground-only entry + EVENT_SYSTEM_FOREGROUND lazy entry
  (no startup focus-storm). Probe on first window per process; capability cached; graceful back-off.
- Verified live: VS Code, Edge, Discord each go borderless-fullscreen into their slot on activation.
- HandleMovedWindows -> UpdateLieSlot (re-size in place via WM_DISPLAYCHANGE, skip-if-unchanged).
- Reveal (Win+Alt) / Alt+T -> ToggleRevealLie (exit/enter the app's fullscreen).
- Sleep/wake RepairRecrop -> ReassertLie. MoveToMonitor -> re-assert slot. Quit/RestoreAll ->
  ExitAllLie (synchronous F11-exit, verified "restored 9 windows"). Shutdown clean.
- Win+Arrow (Win+Left/Right/Up/Down) intercepted -> SnapTile: exit fullscreen, Windows snaps,
  re-enter fullscreen into the new slot. (Needs real-key interactive test: keyboard-hook-driven,
  and the re-enter needs the window foreground, which a synthetic test can't reliably arrange.)

NEEDS INTERACTIVE TESTING BY USER (can't synthesize: the LL keyboard hook ignores injected keys,
and foreground can't be set from a background script):
- Win+Arrow snap/tile round-trip on a fullscreen window (the just-built feature).
- Win+Alt reveal / second tap hide; Alt+T; triple-Alt highlight; both-Alts help list.
- Snap Layouts (Ctrl+Win+Alt), Win+Alt+number monitor move on fullscreen windows.
- Opening/closing apps, multi-monitor, sleep/wake in real use.

NOT YET DONE:
- Delete crop subsystem (still compiled behind UseLie as the fallback).
- Drag-to-edge snapping (mouse) still won't re-tile a fullscreen window (only Win+Arrow is intercepted).
- Win32Tests SetSlot/mapping round-trip; the capability-cache + FS-state unit tests from the plan.

## ====================================================================================
## STEP 2 — MAIN-FLOW INTEGRATION (detailed plan + tests)
## ====================================================================================
## Mechanism and native layer are DONE and proven (see sections below). This is the plan
## to wire MonitorLie into Program.cs, remove the crop subsystem, and test it thoroughly.

### New model vs old
- OLD: Strip WS_CAPTION on every managed window; for self-drawn bars, stretch-and-clip
  (CropTracker). Cover the taskbar for maximized classic windows.
- NEW: Do NOT strip or clip. For each managed window: give its process a slot via
  MonitorLie.SetSlot + trigger the app's own fullscreen (F11). The app sizes itself to
  the slot and hides its own chrome. Move/snap = update slot + WM_DISPLAYCHANGE. Reveal =
  disable lie + exit fullscreen. The crop subsystem is deleted.

### Core design decisions to lock in code (each gets a unit test on the pure part)
- D1 SLOT = the exact rect Windows laid the window out at, in PHYSICAL px (manager is
  PMv2). Maximized -> the monitor rect (Frames.MonitorRect). Snapped/floating -> the
  window rect Windows gave it. `LieLogic.SlotFor(windowRect, monitorRect, isZoomed)` pure.
- D2 PROBE (dynamic table, user's call): on first manage of a process, set slot+enabled,
  SendF11, wait, FilledSlot? YES -> mark process fullscreen-capable, keep managing.
  NO -> mark process NOT capable (cache by process name), Disable, leave the app alone
  (keeps its own chrome). Never probe that process again this session.
  `LieLogic.FilledSlot` already done + tested.
- D3 FS STATE per window: track {Managed, FullscreenOn}. Entering once via F11; later
  layout changes DON'T re-F11 — update slot + post WM_DISPLAYCHANGE so the app re-sizes
  in place (verified a display change keeps it in-slot).
- D4 REVEAL (Win+Alt): Disable(pid or window) + SendF11 to exit fullscreen -> chrome back.
  Hide: enable + SendF11 to re-enter. Alt+T = same, persistent per window.
- D5 MULTI-WINDOW-PER-PROCESS ceiling: one slot per process. If a second managed window
  of the SAME process needs a different slot, set the slot to whichever window was last
  placed; a display-change could then snap both to one slot. Documented; detect and log.
- D6 EXCLUSIONS unchanged: explorer, notepad excluded (also in the DLL). UWP frames:
  probe like anything else - if Settings' F11 does nothing, D2 backs off automatically
  (no special-casing needed; confirm live).
- D7 SHUTDOWN/CRASH: Disable every process + SendF11-exit each fullscreen + Uninstall
  (UnhookWindowsHookEx -> DLL detach restores user32 bytes). Crash handler + ProcessExit.

### Code changes (Program.cs unless noted)
1. Startup: `MonitorLie.Install()` after the other hooks; log if it fails (degrade: no lie).
2. `Manage`: replace Strip/ApplyCrop/CoverIfMaximized with: compute slot (D1), skip if the
   process is marked not-capable, else SetSlot+enable, probe (D2) on first time per process.
3. `HandleMovedWindows` (WM_APP_MOVED): for a managed+fullscreen window, recompute slot;
   if changed, SetSlot + PostMessage(hwnd, WM_DISPLAYCHANGE). No strip/crop.
4. `ToggleReveal` / `ToggleChrome`: D4.
5. Sleep/wake (`BeginSettle`/`RepairMoveBack`/`RepairRecrop`): drop crop reset; instead
   re-assert each managed window's slot + WM_DISPLAYCHANGE after displays settle. Keep the
   layout-record/put-back logic (still needed to restore positions).
6. `MoveToMonitor`: after the window lands on the new monitor, recompute + SetSlot +
   WM_DISPLAYCHANGE (and re-probe only if not yet fullscreen).
7. `RestoreAll` / quit / `State`: D7. state.json no longer needs crop/backdrop fields;
   simplify to {Hwnd, WasFullscreen} or drop (nothing to undo but exiting fullscreen).
8. DELETE from Logic.cs: Crop, CropTracker, CropStep/CropKind, Crop.Defaults, VisibleBar,
   MaximizedVisible, FillVisible, IsAppFullscreen, DipFor/Pixels. Keep Chrome.IsManageable,
   Monitors, Layout, Focus, Frames.* (Strip/SetRegion etc. become unused -> remove too).
9. DELETE CropTests.cs; move any still-relevant Chrome/Layout tests out first.
10. Remove crop.json handling; remove the self-drawn classification path.

### UNIT TESTS (pure, in WindowManager.Tests) — add/keep
- LieLogic.FilledSlot: exact, few-px tolerance, full-monitor-not-filled. (DONE, 3 pass.)
- LieLogic.SlotFor: maximized -> monitor rect; snapped -> the given rect; floating -> given
  rect; multi-monitor rect passed through unchanged.
- Process-capability cache: capable stays capable; not-capable short-circuits (table logic).
- FS-state transitions: Manage->on; move->stay on (no re-enter); reveal->off; hide->on.
- Shared-struct layout: SetSlot writes enabled at 0, rect at 4/8/12/16 (guards the C ABI).
- Keep LogicTests/AltGestureTests/LayoutTests/MonitorTests/CursorTests/InstanceTests green.
- Win32Tests: drop the crop-region round-trip; add a MonitorLie.SetSlot/CreateFileMapping
  round-trip (manager writes, a second mapping view reads back the same bytes).

### LIVE REGRESSION (rebuild dist + restart authorized) — every case this project hit
Measure with the existing rect tool; PASS = content meets the slot edges, no wallpaper.
 1. VS Code maximized: fills the monitor exactly, taskbar covered, no left/right/bottom gap.
 2. Vivaldi maximized: no gap; no bar; side tabs behave.
 3. Snap Win+Left / Win+Right / Win+Up: content fills the slot, no gaps any edge.
 4. Snap Layouts (Win+Z) zones: fills each zone, no gaps after the animation.
 5. Sideways drag-resize of a snapped/floating window: tracks, no gap, no double-apply.
 6. Second monitor ABOVE: NO band/leak on it (the lie draws nothing off-slot by design).
 7. Win+Alt+<n> move to monitor N: window follows, re-fills new slot, DPI rescale correct.
 8. Monitor with different DPI: fills the right slot, not the whole monitor.
 9. Win+Alt reveal active window only -> chrome shows; again -> back to fullscreen slot.
10. Alt+T per-window reveal persists until toggled.
11. UWP (Settings): probe backs off gracefully if no F11; app usable, not fighting.
12. Explorer + Notepad: untouched (excluded).
13. Sleep/wake: windows back in place, still slot-filled, no broken layout, no stuck FS.
14. Display add/remove (Win+P duplicate/extend): apps re-fill correct slots, recover.
15. Minimize then restore: comes back in its slot, still fullscreen.
16. Single-instance restart (new build replaces old): old hook removed + apps exit FS
    cleanly, new hook installs, apps re-enter; no crash, no double-FS.
17. Discord/Outlook (no F11): keep their chrome, no flicker, no repeated probe.
18. Hand-drawn cursors still applied (unrelated subsystem untouched).
19. Quit / Ctrl+Alt+Shift+Q: every app exits fullscreen, chrome back, hook removed.
20. Crash path: kill the manager -> DLL detaches cleanly, no app crashes; next start +
    state recovery exits any left-over fullscreen.
21. NEW: two windows of one app (two VS Code) side by side -> document/verify the D5
    ceiling behavior (both fill until a display change; log when the ceiling is hit).
22. NEW: F11-probe focus race -> probing must not leave focus stolen or an app toggled
    into FS that we then abandon; verify foreground restored after each probe.

### SEQUENCING (do not leave the tool broken)
- Build the integration with crop still compiled but bypassed by a single internal switch
  `UseLie` (default on). Prove cases 1-10 live. Flip off once to confirm crop still works
  as a fallback. THEN delete crop (step 8-10) and re-run the full matrix. (This is a build
  safety net, not a shipped flag - removed before done.)

## SPIKE RESULTS (2026-10-16) — approach is GO
Toolchain: no compiler was present; installed LLVM + WinLibs mingw-w64 gcc (gcc builds
the Win32 DLL directly, no Windows SDK needed). MinHook vendored from upstream.

Phase 1a (does the DLL even load?): injected a logging DLL via WH_GETMESSAGE into
vivaldi, msedge, Code, Discord, Obsidian, olk. **All six loaded it**, including
Edge — its third-party-DLL blocklist did NOT stop a SetWindowsHookEx injection into
the browser UI thread. Risk #1 is dead.

Phase 1b (does the lie reach F11?): hooked GetMonitorInfoW/A to report a left-half
slot (physical 0,0-960,1200), posted WM_DISPLAYCHANGE to rebuild the app's display
cache, synthesized F11, measured. Results (logical 1280x800 space; 640 logical = 960 px):
  - Vivaldi: F11 -> win (0,0)-(640,800) = exact slot. Chromium obeys.
  - Edge:    F11 -> win (0,-40)-(640,800) = exact slot width. Chromium obeys.
  - VS Code: F11 -> win (0,-35)-(640,800) = exact slot; restored clean. Electron obeys.
So the app hides its OWN chrome and sizes to the fake monitor. No crop, no bar-height
table. This is the universal mechanism.

CRITICAL FINDING (edge case #20, hit for real): the first DLL had no DLL_PROCESS_DETACH
cleanup. UnhookWindowsHookEx unmapped it from Vivaldi while MinHook's inline JMP in
user32.GetMonitorInfoW still pointed into the freed DLL -> **Vivaldi crashed/closed on
its next monitor query.** Fix: DETACH must MH_DisableHook(ALL)+MH_Uninitialize to
restore user32's bytes before unmap. After the fix, Edge and VS Code survived a full
attach->lie->unhook cycle. PRODUCT IMPLICATION: prefer PINNING the DLL
(GET_MODULE_HANDLE_EX_FLAG_PIN) + a global enabled-flag the hooks check, instead of
unloading on shutdown — avoids MinHook's thread-suspension under loader lock entirely.

OPEN DESIGN FORK for Step 2 (per-window vs per-process lie):
The spike lied GLOBALLY inside each process and injected one process per window. In
production a single process (Chrome, VS Code) can own SEVERAL managed windows, each
needing a DIFFERENT slot. GetMonitorInfo is keyed by HMONITOR, not hwnd, so a
per-window lie needs MonitorFromWindow(hwnd)->synthetic HMONITOR and
GetMonitorInfo(synthetic)->slot, AND Chromium's ScreenWin must accept an HMONITOR
that isn't in its enumerated cache. UNTESTED. Next spike must settle this before any
rewrite: two windows in one process, two slots.

Phase 1c (per-window gate): hooked MonitorFromWindow/Point/Rect to return a synthetic
HMONITOR (0x7A7A0001) not in the enumerated cache, and GetMonitorInfo(sentinel)->slot.
Edge F11 -> win (0,-40)-(640,800) = exact slot, survived, restored clean. **Chromium
tolerates a non-enumerated HMONITOR**, so per-window lying is feasible: one distinct
synthetic HMONITOR per managed window, each mapped to that window's slot. Step 2
unblocked.

## NATIVE LAYER DONE (2026-10-16) — WindowManager/native/monlie.c (+ vendored minhook)
Per-window synthetic HMONITOR was tried and FAILED: an app's fullscreen sizing reads
the monitor through the REAL HMONITOR; a synthetic per-window handle is accepted but
ignored for sizing (debug log: tagged window got the slot, yet Edge sized to the real
monitor the GetMonitorInfo(realHandle) returned). The global GetMonitorInfo-overwrite
is the only reliable mechanism, so the lie is per PROCESS.

FINAL DESIGN (validated on Edge, incl. right-half/non-origin slot and a display change
while fullscreen; app survived; clean disable+restore):
- Per-process file mapping "Local\monlie_<pid>" = { volatile LONG enabled; RECT slot }.
- monlie.dll (injected everywhere via WH_GETMESSAGE) opens its own mapping on attach;
  if none exists it does NOTHING (unmanaged process untouched). When enabled, it
  overwrites every GetMonitorInfo result to slot.
- Detach restores user32 bytes (MH_DisableHook+Uninitialize) - no host crash.
- Exclusions (explorer.exe, WindowManager.exe) are enforced in the DLL too.
CEILING: one slot per process. Several windows of ONE app in DIFFERENT slots at once
share the process slot. Common case (one managed window per app) is exact.

## Step 2 remaining — C# integration (the big piece, NOT built yet)
- Ship monlie.dll beside the exe (csproj Content, CopyToOutputDirectory).
- Startup: LoadLibrary monlie.dll, SetWindowsHookEx(WH_GETMESSAGE, GetMsgProc, hDll, 0).
- Per managed window: create/open its process mapping, write slot, enabled=1, then make
  the app ENTER ITS OWN FULLSCREEN. There is NO universal "go fullscreen" API - this is
  the one unavoidable per-app bit: F11 for Chromium/Electron/Explorer/Terminal, other
  keys for a few apps, none for Discord/Outlook (those just keep their chrome). Small
  key table, far smaller/safer than the crop bar-height table.
- Layout change (snap/move/maximize): update slot in the mapping, post WM_DISPLAYCHANGE
  to the window so the app re-reads and re-sizes to the new slot WITHOUT leaving
  fullscreen (verified a display change keeps it in-slot). AVOIDS F11 toggling per move.
- Reveal: enabled=0 + exit fullscreen (show chrome). Hide: enabled=1 + re-enter.
- Shutdown/quit: enabled=0 for every process, exit fullscreen, UnhookWindowsHookEx.
- THEN retire the crop subsystem (behind a build flag first, then delete) and run Step 3.
- Live Step-3 regression needs the running manager restarted -> requires the user's ask.

## C# ENGINE DONE + PROVEN LIVE (2026-10-16)
Built into the project: WindowManager/MonitorLie.cs (Install/Uninstall/SetSlot/Disable +
LieLogic.FilledSlot pure decision), Native.cs P/Invokes (WH_GETMESSAGE raw install, file
mapping, LoadLibrary/GetProcAddress), csproj Target copies native/monlie.dll to output and
publish, LieTests (3 pass). A standalone `WindowManager.exe --lie-test <hwnd> L T R B` path
exercises the engine without touching the managing flow.
Live, with the old crop manager CLOSED (so nothing fought it): Edge right-half slot ->
FS=(960,0 960x1200) = exactly the slot, filled=True, zero gaps. (With the crop manager still
running it pulled the top up 60px - the two fight, which is why full-replace must remove crop.)
Fullscreen trigger decision (user): DYNAMIC PROBE, not a hardcoded table - send F11, check
FilledSlot; if it did not fill, the app has no usable fullscreen, so back off and remember
that process. Builds the table at runtime.

(Detailed integration plan + full test matrix are in the STEP 2 section above.)

## Verify
- `dotnet test` green, `openspec validate --all --strict`.
- The spike's measurements logged.
- NOTHING committed, published to dist, or restarted without an explicit ask.

SANDBOX-DRIVEN FIXES (2026-10-31) - found by tests/sandbox.ps1 (see add-sandbox-tests), each measured, none guessed.
Full macro, 388 tests: hard defects 1797 -> 386 -> 44 -> 36 -> (see TESTS.md for the latest). Tiling first change 100+ms -> 13ms.
- Tile flash: Win+Left/Right/Up on a fullscreen window left fullscreen and re-entered (title bar on screen ~100ms, 2 F11s). Now the slot is
  set and the window is moved in place (SWP_NOSENDCHANGING, the user's own original design); the leave/re-enter toggle is only the fallback
  if the move does not hold.
- Alt+N minimized without leaving fullscreen (manager counted a minimized window as fullscreen). Now the Win+Down path.
- Alt+M on a fullscreen window posted a maximize to the app and left the slot stale. Now: fill the monitor; again = back to the previous tile.
- Reconcile (ReconcileLie, on every move event): app left its own fullscreen (title bar back) -> dropped, revealed; moved/resized while still
  borderless -> healed (max 3); minimized from outside -> belief kept (the app is still fullscreen), re-entered if it returns without it.
  Adopting a revealed window that went fullscreen on its own was tried and removed: it adopted transient states.
- Snap Layouts: the 10s wait timer could fire a few ms early, find nothing expired and never run again (window out of fullscreen for good);
  and a pick was accepted while the leave-fullscreen op was still running (the leave itself looked like a pick).
- opWindows was a SET: two overlapping ops on one window, the first to finish marked it idle. Now counted (OpBegin/OpEnd/OpActive).
- Describe() looked the process up from scratch on every log line (enumerates processes): 80-500ms loop stalls. Cached per window (locked: workers log too).
- Non-fullscreen window commands (minimize/maximize/restore+place) ran on the loop and blocked it ~85ms on a busy app: now ops.
- Rapid Win+Alt: ten presses queued ten F11 round trips (12s). Presses arriving while the window is busy now collapse to their parity.
- InAppFullscreen: a window with its title bar showing is not in fullscreen whatever its rectangle (a maximized-looking window equals the monitor);
  the manager toggled F11 the wrong way three times.
- SlotOf: a window wholly off every monitor produced an empty slot -> the monitor. An app that ignores the lie, or cannot be as small as the slot,
  is no longer left stuck in a fullscreen the manager does not know about (it is taken back out).
- VIRTUAL DESKTOPS (user report: Alt+Tab, desktop-switch shortcut, Enter -> back on desktop 1). Real log, three times: "op done: re-asserting
  foreground on X" 55-100ms after a desktop shortcut. A fullscreen operation still running when the user moved on re-asserted the focus on a window
  on the OLD desktop and Windows switched back. Now every step that focuses or types F11 asks IVirtualDesktopManager first (VirtualDesktops.cs):
  Desk.SetForeground refuses a window on another desktop, Desk.SendF11(target) types only if that window is on this desktop AND focused (F11 goes
  to the focused window, so anything else toggled a stranger), MonitorLie.Focus replaces the fixed 80ms sleeps, entry that never started is
  abandoned cleanly (AbandonEnter), the op-done re-assert is skipped, shutdown posts F11 as key messages to windows on other desktops.
- Latency: activation entry 780ms -> ~75ms (debounce 600 -> 60ms, no fixed sleeps, 10ms polls); reveal/minimize first change 74-190ms -> 13-24ms.
- Remaining by design: up to two frames of the app's OWN title bar inside its own F11 (reported as app-side, not a manager defect).

WINDOWS JUMPING BETWEEN DISPLAYS ON ALT+TAB (2026-10-31, user report; three monitors connected) - root cause and fix.
Real log: `lie enter 0xB40344(Code) slot=(104,0 1807x1191)` ... `snap 0xB40344 -> (940,-2172 3840x2160)` ... 7s later
`lie MISMATCH 0x600C14(Code) rect=(940,-2172 3840x2160) slot=(0,0 1920x1200)`: a DIFFERENT VS Code window, on the laptop panel,
landed in the rectangle just set for the first one. The lie was one slot per PROCESS, told for EVERY monitor (and every monitor
flagged primary): four Code windows on three monitors shared it, so each activation re-slotted them all and Chromium's display
model was three identical displays. Design decision D5 called this a ceiling; with several monitors it is the common case.
Fix: the shared table is now keyed by the REAL monitor handle - 8 entries of {HMONITOR, enabled, slot} per process (monlie.c,
MonitorLie.cs; LieSharedTests pins the byte layout, _Static_assert pins the C side). GetMonitorInfo lies only for the monitor
with an enabled entry and leaves its flags real; other monitors are truthful. Program.cs owns entries per window (LieSet /
LieClear / LieDisableProcess): a window that moves monitors releases its old entry; two windows of one process on the SAME
monitor with different slots still share that monitor's entry (logged as "lie conflict", last one wins) - the remaining ceiling.
Also found in the same log: "center cursor on 0x540BA8(Code) at -31882,-31981" - centring the cursor on a MINIMIZED window
threw it off screen; CenterAfterSwitch now skips iconic windows.
Not verifiable in the sandbox (one monitor): the check is the real log after the new build runs - no `lie MISMATCH` after Alt+Tab
between windows of one app on different monitors, and `lie conflict` only when two such windows share a monitor.
