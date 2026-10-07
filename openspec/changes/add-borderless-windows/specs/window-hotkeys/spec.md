# Spec Delta

## Purpose

Keyboard replacements for the caption buttons that stripped windows no longer show.

## ADDED Requirements

### Requirement: Close window
The system SHALL ask the active window to close on Alt+Q, the same way as clicking its close button.

#### Scenario: AltQ closes active window
- **WHEN** the user presses Alt+Q
- **THEN** the active window receives a close command and Q is not delivered

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

### Requirement: Windows tiled by Snap Layouts have no gaps
The system SHALL keep cropped windows flush with the zone Snap Layouts gives them, even though Windows re-applies each window's position after its animation.

#### Scenario: Snap layout override is retried then given up
- **WHEN** a cropped window is moved back to its zone right after being cropped
- **THEN** it is checked again after 300 ms and cropped again, up to 3 times, after which its clip is removed so no gaps remain
