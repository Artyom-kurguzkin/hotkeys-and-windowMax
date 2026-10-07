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
