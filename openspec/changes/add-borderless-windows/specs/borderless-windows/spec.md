# Spec Delta

## Purpose

Makes every application window chrome-free, so its content fills exactly the area Windows' own layout gives it, while Snap, maximize and multi-monitor moves keep working.

## ADDED Requirements

### Requirement: Real windows lose their title bar
The system SHALL remove the title bar from every real application window. That covers windows already open at startup and windows shown later. A real application window is visible, framed, top-level, not a popup and not a tool window. Other windows SHALL be left unchanged.

#### Scenario: Framed window is stripped
- **WHEN** a real application window with a title bar is shown
- **THEN** its title bar is removed

#### Scenario: Tool window is left alone
- **WHEN** a tool window or popup is shown
- **THEN** its style is not changed

#### Scenario: Window stays resizable
- **WHEN** a window's title bar is removed
- **THEN** it keeps its resizable frame and minimize/maximize abilities, so Snap, Win+Arrow and maximize keep working

### Requirement: Excluded apps are left alone
The system SHALL exclude File Explorer and Notepad from management entirely by process name, case-insensitively. An excluded window SHALL NOT be stripped, cropped or covered, and keeps its native title bar.

#### Scenario: Excluded app keeps its title bar
- **WHEN** a File Explorer or Notepad window is shown
- **THEN** its style is not changed and it keeps its title bar

#### Scenario: Other apps are unaffected by the exclusion
- **WHEN** a window belongs to a process that is not on the excluded list
- **THEN** it is managed normally

### Requirement: No border, accent or rounded corners
The system SHALL turn off the visible accent border and corner rounding of each stripped window.

#### Scenario: Accent border removed
- **WHEN** a window is stripped
- **THEN** its border color is set to none and its corners to square

### Requirement: Maximized window covers the taskbar
The system SHALL resize a maximized stripped window so that its content area covers the full monitor, not just the work area.

#### Scenario: Maximize covers monitor
- **WHEN** a stripped window is maximized and its content area does not match the monitor
- **THEN** the window is resized so its content area equals the monitor rectangle

#### Scenario: Already covering does nothing
- **WHEN** a maximized stripped window's content area already equals the monitor
- **THEN** the window is not moved again

#### Scenario: Normal window is not resized
- **WHEN** a stripped window is not maximized
- **THEN** its position and size are left to Windows

### Requirement: Title bar stays removed
The system SHALL remove the title bar again if an application restores it on a managed window, unless the user turned the title bar back on with the toggle hotkey.

#### Scenario: Reappearing caption is stripped
- **WHEN** a managed window gets its title bar style back and the user has not toggled it on
- **THEN** the title bar is removed again

### Requirement: Restore on quit
The system SHALL restore the original title bar, border color and corners of every window it changed when it quits cleanly.

#### Scenario: Quit restores frames
- **WHEN** the program quits via Ctrl+Alt+Shift+Q or is replaced by a new instance
- **THEN** every window it stripped gets its original style back

### Requirement: Window dump
The system SHALL, when started with `--dump`, print every visible top-level window with its handle, process, class, title, style and classification, then exit without changing anything.

#### Scenario: Dump changes nothing
- **WHEN** the program runs with --dump
- **THEN** it prints the window list, exits, and leaves every window's style unchanged

### Requirement: Layout survives sleep and display changes
The system SHALL track the last known-good place of every managed window (monitor, laid-out rect, maximized state, restore rect) while the displays are stable. It SHALL freeze that tracking when the machine suspends or the monitor set changes, and once the displays have been unchanged for 2 seconds it SHALL put windows back where they were and re-apply crops and borders from scratch.

#### Scenario: Window evacuated during sleep is put back
- **WHEN** a window is moved elsewhere and loses its crop while the machine sleeps, and its monitor is back unchanged after waking
- **THEN** it returns to its exact pre-sleep place and crop

#### Scenario: Window stays put when its monitor is gone
- **WHEN** a window's monitor is missing or came back with a different position or size
- **THEN** the window stays where Windows put it and is re-cropped there

#### Scenario: Moves while displays change are not recorded
- **WHEN** Windows moves windows while monitors are dropping out or coming back
- **THEN** those moves neither crop nor overwrite the recorded layout

#### Scenario: Wake is detected without a power notification
- **WHEN** the program's 700 ms timer did not tick for more than 30 seconds
- **THEN** it treats this as a wake and repairs the layout
