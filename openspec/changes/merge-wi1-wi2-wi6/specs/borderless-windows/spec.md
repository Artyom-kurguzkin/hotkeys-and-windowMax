# Spec Delta

## Purpose

Makes every application window chrome-free, so its content fills exactly the area it occupies, while tiling, maximize and multi-monitor moves keep working. The mechanism is the "monitor lie": the app is told that the area it occupies (its slot) is its whole monitor, and is then put into its OWN fullscreen, which sizes itself to that slot and hides its own title bar and tab strip. (The earlier approach, stripping the caption and cropping self-drawn bars, is superseded; see chrome-crop.)

## ADDED Requirements

### Requirement: An activated window goes fullscreen into its slot
The system SHALL, when a real application window is activated, tell its process that the window's monitor is the window's slot and put the app into its own fullscreen there. The slot SHALL be the whole monitor for a maximized window, otherwise the window's visible frame clamped to the monitor; a window wholly off every monitor SHALL get the whole monitor. Nothing SHALL be fullscreened at startup; windows go in when the user activates them.

#### Scenario: Activated window fills its slot
- **WHEN** the user activates a capable app's window
- **THEN** it enters its own fullscreen exactly over its slot, with no title bar or tab strip

#### Scenario: Maximized window fills the monitor
- **WHEN** the activated window was maximized
- **THEN** its slot is the whole monitor, taskbar included

#### Scenario: Off-screen window gets an on-screen slot
- **WHEN** the activated window is partly or wholly off the monitor
- **THEN** its slot lies inside the monitor and is never empty

### Requirement: Apps without a usable fullscreen keep their chrome
The system SHALL probe each program once per session. A program whose F11 does nothing SHALL be marked not capable and left with its own chrome, and SHALL NOT be probed again. A window that ends up at a size other than its slot (an app's own minimum size) SHALL be left out without judging the whole program. An app left fullscreen on the real monitor, or at the wrong size, SHALL be taken back out of fullscreen.

#### Scenario: Non-capable app is left alone
- **WHEN** an app's F11 does not change its window
- **THEN** it keeps its chrome and later windows of that program are not probed

#### Scenario: Nothing is left stuck in fullscreen
- **WHEN** an app goes fullscreen somewhere other than its slot and the retries are used up
- **THEN** it is taken back out of fullscreen

### Requirement: Excluded apps are left alone
The system SHALL exclude File Explorer and Notepad from management entirely by process name, case-insensitively.

#### Scenario: Excluded app keeps its title bar
- **WHEN** a File Explorer or Notepad window is activated
- **THEN** it is not fullscreened and keeps its title bar

### Requirement: One slot per monitor per process
The system SHALL keep the lie per process AND per real monitor, so windows of one app on different monitors each fill their own slot, and SHALL report every other monitor truthfully. Two windows of one app on the same monitor in different slots share that monitor's slot; the last one set wins and this SHALL be logged.

#### Scenario: Switching between windows of one app on different monitors moves nothing
- **WHEN** the user Alt+Tabs between windows of one app on different monitors
- **THEN** no window moves to another window's slot

### Requirement: Tiling a fullscreen window moves it in place
The system SHALL tile a fullscreen window (Win+Left, Win+Right, Win+Up) by setting the new slot and moving the window there without leaving fullscreen. Only if the app does not keep the new size SHALL it leave and re-enter its fullscreen.

#### Scenario: No title bar flashes during a tile
- **WHEN** the user presses Win+Left on a fullscreen window
- **THEN** it reaches the left half directly, without showing its title bar in between

### Requirement: The slot follows a window moved to another monitor
The system SHALL, when the user moves a fullscreen window to another monitor (Win+Shift+Arrow, a drag), move its slot to that monitor (the whole monitor stays the whole monitor; a tile keeps its proportions) and release the old monitor's entry.

#### Scenario: Moved window stays on its new monitor
- **WHEN** the user moves a fullscreen window to another monitor and then Alt+Tabs to another window
- **THEN** the moved window stays on its new monitor

### Requirement: An app leaving its own fullscreen is treated as revealed
The system SHALL treat a fullscreen window whose title bar came back (the user pressed the app's own F11) as revealed, and SHALL never treat a window with its title bar showing as being in its own fullscreen, whatever its rectangle.

#### Scenario: User presses the app's own F11
- **WHEN** the user leaves fullscreen with the app's own F11
- **THEN** the manager treats the window as revealed and never toggles it the wrong way

### Requirement: A fullscreen window moved by something else is put back
The system SHALL put a fullscreen window that is still borderless but was moved or resized by something else back into its slot, at most three times in a row.

#### Scenario: Another program resizes a fullscreen window
- **WHEN** another program resizes a fullscreen window
- **THEN** it is returned to its slot

### Requirement: Minimized from outside keeps its state
The system SHALL keep counting a fullscreen window minimized from outside as fullscreen, and SHALL put it back into its slot if it comes back without its fullscreen.

#### Scenario: Minimized and restored from the taskbar
- **WHEN** a fullscreen window is minimized from the taskbar and restored
- **THEN** it comes back filling its slot

### Requirement: Never act on a window the user has left behind
The system SHALL NOT give the focus to, or type F11 at, a window on another virtual desktop, and SHALL type F11 only while the target window has the focus. An operation still running when the user switches desktop SHALL stop without pulling the user back.

#### Scenario: Switching desktops during an operation
- **WHEN** the user switches virtual desktop while a window is entering fullscreen
- **THEN** the user stays on the new desktop

### Requirement: Restore on quit
The system SHALL take every window out of its fullscreen and stop lying about every monitor when it quits cleanly; a window on another virtual desktop SHALL be sent the key directly rather than focused.

#### Scenario: Quit restores windows
- **WHEN** the program quits via Ctrl+Alt+Shift+Q or is replaced by a new instance
- **THEN** every window it fullscreened gets its chrome back

### Requirement: Window dump
The system SHALL, when started with `--dump`, print every visible top-level window with its handle, process, class, title, style and classification, then exit without changing anything.

#### Scenario: Dump changes nothing
- **WHEN** the program runs with --dump
- **THEN** it prints the window list, exits, and leaves every window unchanged

### Requirement: Layout survives sleep and display changes
The system SHALL freeze window tracking while the machine sleeps or the monitor set changes, and once displays have been unchanged for 2 seconds re-assert each fullscreen window's stored slot (a tile stays a tile).

#### Scenario: Tile survives a wake
- **WHEN** the machine wakes with a window tiled to the left half
- **THEN** it still fills the left half

#### Scenario: Wake is detected without a power notification
- **WHEN** the program's 700 ms timer did not tick for more than 30 seconds
- **THEN** it treats this as a wake and re-asserts the slots
