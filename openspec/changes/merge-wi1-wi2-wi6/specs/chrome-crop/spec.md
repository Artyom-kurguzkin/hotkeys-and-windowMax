# Spec Delta

## Purpose

Shows an app's own chrome (title bar, tab strip) again on demand, and hides it again. With the monitor lie (see borderless-windows) nothing is cropped any more: a window shows its chrome by leaving its own fullscreen, and hides it by going back in. The crop code remains compiled as a fallback but is not used.

## ADDED Requirements

### Requirement: Reveal and hide the active window's chrome
The system SHALL, on a Win+Alt tap or Alt+T, take the active fullscreen window out of its own fullscreen so its chrome shows, and on the next press put it back into its slot. Only the active window SHALL change.

#### Scenario: Reveal
- **WHEN** the user taps Win+Alt on a fullscreen window
- **THEN** the window shows its title bar and tab strip and the Start menu does not open

#### Scenario: Hide again
- **WHEN** the user taps Win+Alt again
- **THEN** the window goes back into its own fullscreen in its slot

#### Scenario: Other windows are untouched
- **WHEN** one window is revealed and the user switches to another
- **THEN** only the revealed window shows its chrome

### Requirement: Rapid presses collapse
The system SHALL, when reveal is pressed again while the window's previous toggle is still running, remember only whether an odd number of presses arrived and apply that once when the window is free.

#### Scenario: Ten quick presses
- **WHEN** the user presses Win+Alt ten times quickly
- **THEN** the window ends in a consistent state within about a second, not after ten full round trips

### Requirement: Nothing to reveal on a minimized window
The system SHALL ignore reveal for a minimized window.

#### Scenario: Reveal on a minimized window
- **WHEN** the user presses Alt+T while the window is minimized
- **THEN** nothing is toggled

### Requirement: Revealed windows are restored on quit
The system SHALL leave revealed windows with their chrome on quit and return every fullscreen one to its chrome.

#### Scenario: Quit with a revealed window
- **WHEN** the program quits while one window is revealed and another is fullscreen
- **THEN** both show their chrome afterwards
