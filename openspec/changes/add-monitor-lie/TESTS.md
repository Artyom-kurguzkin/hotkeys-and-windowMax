# Monitor-lie: exhaustive window-operation test list

Every operation a user can perform on managed windows, what PASS looks like, and whether
it can be driven programmatically (via the manager's WM_APP_* messages) or needs real keys.

Legend: [AUTO] I can drive it + screenshot. [KEYS] needs real key presses (the LL keyboard
hook ignores injected keys). [SYS] needs a system event (sleep, display change).

## A. Becoming borderless
A1 [AUTO] Activate a capable app (VS Code/Edge/Vivaldi/Discord) -> borderless fullscreen, fills the monitor, no title bar/tabs.
A2 [AUTO] Activate a NON-capable app (if any) -> keeps its chrome, no stuck state, probed once only.
A3 [AUTO] Open a brand-new window of a capable app -> fullscreens on first activation.
A4 [KEYS] App's own F11 pressed by the user while managed -> no fight, no double-toggle.
A5 [AUTO] A dialog / popup / owned window (Save As, settings popup) -> NOT forced fullscreen, usable.

## B. Tiling (Win+Arrow, now intercepted)
B1 [AUTO] Win+Left -> left half, borderless, no gap, no Game Bar.
B2 [AUTO] Win+Right -> right half.
B3 [AUTO] Win+Up -> full monitor.
B4 [AUTO] Win+Down -> minimize.
B5 [AUTO] Left then Right then Up (sequential) -> each lands exactly, no drift.
B6 [AUTO] Two windows: A Win+Left, B Win+Right -> side by side, both borderless, no overlap/gap.
B7 [KEYS] Snap Layouts (Ctrl+Win+Alt) -> Windows' zone picker, pick a zone, window fills it.
B8 [KEYS] Drag a window's (invisible) edge / drag to a screen edge -> KNOWN GAP: a fullscreen window has no frame to drag; document behavior.

## C. Reveal / chrome
C1 [AUTO] Win+Alt tap -> active window shows its real chrome (exits fullscreen); others stay fullscreen.
C2 [AUTO] Win+Alt again -> back to borderless fullscreen in its slot.
C3 [AUTO] Alt+T -> same toggle, persistent.
C4 [AUTO] Reveal one window, switch to another -> only the revealed one shows chrome.

## D. Window commands
D1 [KEYS] Alt+Q / Win+Alt+X -> closes the active window (WM_SYSCOMMAND SC_CLOSE).
D2 [KEYS] Alt+M -> maximize/restore toggle.
D3 [KEYS] Alt+N -> minimize.
D4 [AUTO] Close a fullscreen window -> no leftover lie state, neighbors unaffected.
D5 [AUTO] Minimize then restore (click taskbar) -> comes back fullscreen in its slot.

## E. Multi-monitor  [needs a second monitor connected]
E1 [AUTO] Win+Alt+N / Copilot+N -> move active window to monitor N, re-fills that monitor.
E2 [SYS]  A window on each monitor -> each fullscreens into its own monitor independently.
E3 [SYS]  Monitor with a different DPI -> fills the right monitor, not the wrong size.
E4 [SYS]  Monitor ABOVE the laptop panel -> NO leak/band on it (nothing drawn off-slot).

## F. Focus
F1 [AUTO] Alt+Tab / click to another window -> it becomes foreground and fullscreens (first time).
F2 [AUTO] Hover-to-focus (mouse over a window) -> activates it (binds.ahk parity).
F3 [AUTO] Rapid switching between 3+ windows -> each fullscreens once, no focus-storm, no flicker loop.

## G. Overlays
G1 [AUTO] Triple-Alt -> themed highlight border around the active window, fades after ~1.2s.
G2 [KEYS] Both Alts -> sorted hotkey list window; both Alts again -> closes.

## H. System events
H1 [SYS] Sleep then wake -> windows back in place, still borderless in their slots, no stuck fullscreen, no broken layout.
H2 [SYS] Display sleep / unplug-replug a monitor -> windows recover to correct slots.
H3 [SYS] Resolution or scale (DPI) change -> windows re-fill correctly.
H4 [SYS] Lock (Win+L) then unlock -> no disruption.

## I. Manager lifecycle
I1 [AUTO] Quit (Ctrl+Alt+Shift+Q) -> every app leaves fullscreen, chrome back, hook removed.
I2 [AUTO] Start a second instance -> old one quits cleanly (apps un-fullscreen), new one takes over.
I3 [SYS]  Kill the manager (Task Manager) -> DLL detaches without crashing apps; next start recovers.

## J. App variety
J1 [AUTO] UWP app (Settings, Calculator) -> managed; if its F11 does nothing, backs off gracefully, stays usable.
J2 [AUTO] Explorer + Notepad -> excluded, completely untouched.
J3 [AUTO] Terminal, Slack, Obsidian, Outlook, Word -> each probed; capable ones fullscreen, others keep chrome.
J4 [KEYS] A real fullscreen video / game -> not interfered with.

## K. Stress / races
K1 [AUTO] Rapid Win+Left/Right/Up repeatedly -> no desync, ends in the last-pressed slot.
K2 [AUTO] Snap while a probe is still running on another window -> no deadlock, both resolve.
K3 [AUTO] Reveal during a tile / tile during a reveal -> consistent end state.
K4 [AUTO] Close a window mid-tile -> no crash, no orphan lie/slot entry.
