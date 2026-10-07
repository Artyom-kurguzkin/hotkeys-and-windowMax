# Spec Delta

## Purpose

Keyboard replacements for the caption buttons that stripped windows no longer show.

## ADDED Requirements

### Requirement: Close window
The system SHALL ask the active window to close on Alt+Q and on Win+Alt+X, the same way as clicking its close button or pressing Alt+F4.

#### Scenario: AltQ closes active window
- **WHEN** the user presses Alt+Q
- **THEN** the active window receives a close command and Q is not delivered

#### Scenario: WinAltX closes the active window
- **WHEN** the user presses Win+Alt+X
- **THEN** the active window receives the same close command as Alt+F4, and the Win+Alt reveal tap is not triggered

### Requirement: Highlight the active window
The system SHALL flash a border in the Windows accent colour around the active window's visible area for about a second when Alt is tapped three times in quick succession (at most 400 ms between taps, each with no other key). The border SHALL be click-through and SHALL never take focus.

#### Scenario: Triple Alt tap highlights the window
- **WHEN** the user taps Alt three times quickly
- **THEN** an accent-coloured border appears around the active window and disappears again after about a second

#### Scenario: Slow taps dont count
- **WHEN** more than 400 ms pass between two Alt taps
- **THEN** the count starts over

#### Scenario: Alt used in a chord breaks the sequence
- **WHEN** an Alt press is part of a shortcut (for example Alt+Tab) or TrackPoint scrolling
- **THEN** it does not count as a tap

### Requirement: Hotkey list
The system SHALL show a window listing every hotkey it adds when both Alt keys are held at the same time, once per press, without opening a menu bar. The list SHALL be a table of shortcut and action, grouped by category, sorted within each group, and drawn in the Windows light or dark app theme. It SHALL open centred on the active window's monitor and close with Esc. Starting the program with `--hotkeys` SHALL show the same window on its own.

#### Scenario: Both Alts show the hotkey list once
- **WHEN** the user holds both Alt keys
- **THEN** the hotkey table opens, and holding longer does not open more windows

#### Scenario: Table is grouped and sorted
- **WHEN** the hotkey table is shown
- **THEN** rows are grouped by category in a fixed order and sorted by shortcut within each group

#### Scenario: Help lists every binding key
- **WHEN** a key binding exists
- **THEN** it appears in the hotkey list

### Requirement: Toggle maximize
The system SHALL maximize the active window on Alt+M, or restore it if it is already maximized.

#### Scenario: AltM maximizes
- **WHEN** the user presses Alt+M on a window that is not maximized
- **THEN** the window is maximized

#### Scenario: AltM restores
- **WHEN** the user presses Alt+M on a maximized window
- **THEN** the window is restored

### Requirement: Minimize window
The system SHALL minimize the active window on Alt+N.

#### Scenario: AltN minimizes
- **WHEN** the user presses Alt+N
- **THEN** the active window is minimized

### Requirement: Toggle title bar
The system SHALL show or hide the title bar of the active window on Alt+T. A window toggled on SHALL keep its title bar until it is toggled again.

#### Scenario: AltT shows title bar
- **WHEN** the user presses Alt+T on a stripped window
- **THEN** its original title bar is restored and stays until Alt+T is pressed again

#### Scenario: AltT hides title bar again
- **WHEN** the user presses Alt+T on a window whose title bar was toggled on
- **THEN** its title bar is removed again

### Requirement: Snap layouts
The system SHALL open the Windows 11 Snap Layouts menu for the active window (Win+Z) on a tap of Ctrl+Win+Alt: the three keys pressed in any order, then one released, with no other key in between. The tap SHALL NOT open the Start menu or a menu bar.

#### Scenario: CtrlWinAlt tap opens snap layouts
- **WHEN** the user presses Ctrl, Win and Alt in any order and releases one with no other key in between
- **THEN** Win+Z is sent and the releases are masked

#### Scenario: CtrlWinAlt with another key is not a tap
- **WHEN** the user presses another key before releasing
- **THEN** Snap Layouts is not opened

#### Scenario: CtrlShiftWinAlt is not a tap
- **WHEN** Shift is also held (the Office key chord)
- **THEN** Snap Layouts is not opened

### Requirement: Move window to monitor by number
The system SHALL replace the Copilot key's function: Copilot SHALL NOT launch, and releasing it SHALL NOT open the Start menu. While the Copilot key is held, pressing 1–9 SHALL move the active window to the monitor with that Windows display number. The window keeps its maximized state and its relative position and size in the target monitor's work area. The Copilot key sends Win+Shift+F23 and repeats it while held (measured).

#### Scenario: Copilot plus digit moves to that monitor
- **WHEN** the user holds the Copilot key and presses 3
- **THEN** the active window moves to display 3 and the digit is not typed

#### Scenario: Copilot release opens no start menu
- **WHEN** the user presses and releases the Copilot key
- **THEN** neither Copilot nor the Start menu opens

#### Scenario: Maximized window stays maximized
- **WHEN** a maximized window is moved to another monitor
- **THEN** it is maximized on the target monitor

#### Scenario: WinAlt plus digit moves to that monitor
- **WHEN** the user holds Win+Alt and presses a digit 1–9 (for keyboards without a Copilot key; an Fn key never reaches Windows)
- **THEN** the active window moves to that display, the Win+Alt reveal tap is not triggered, and no Start menu or menu bar opens

#### Scenario: Missed F23 release is cleared by Win release
- **WHEN** the Copilot key's F23 release is never seen but its Win key is released
- **THEN** digits are typed normally again

### Requirement: Windows tiled by Snap Layouts have no gaps
The system SHALL keep cropped windows flush with the zone Snap Layouts gives them, even though Windows re-applies each window's position after its animation.

#### Scenario: Snap layout override is retried then given up
- **WHEN** a cropped window is moved back to its zone right after being cropped
- **THEN** it is checked again after 300 ms and cropped again, up to 3 times, after which its clip is removed so no gaps remain


### Requirement: Hotkey list toggles
The system SHALL close the hotkey list when both Alt keys are pressed again while it is showing.

#### Scenario: Hotkey window toggles open and closed
- **WHEN** the hotkey list is showing and the user presses both Alt keys again
- **THEN** the list closes, and the next press of both Alt keys opens it again
