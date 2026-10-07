# Spec Delta

## Purpose

Focus follows the mouse, and the mouse follows keyboard focus changes. This keeps the pointer and the active window together without clicking.

## ADDED Requirements

### Requirement: Hover to focus
The system SHALL check about every 700 ms which top-level window is under the cursor. It SHALL activate that window if it is a real application window that differs from the window last activated this way. A real application window is visible, has an overlapped frame, and is neither a child nor a popup.

#### Scenario: Hovering a new window focuses it
- **WHEN** the cursor rests over a real application window that was not the last hover target
- **THEN** that window is activated

#### Scenario: Same window is not reactivated
- **WHEN** the cursor stays over the window it already hover-activated
- **THEN** no activation happens

#### Scenario: Popup is not focused
- **WHEN** the cursor rests over a popup or child window
- **THEN** no activation happens

### Requirement: Hover pauses during Alt
The system SHALL NOT hover-activate windows while Alt is physically held, so Alt+Tab and Alt+TrackPoint scrolling are not disturbed.

#### Scenario: Hover ignored while Alt held
- **WHEN** the cursor moves onto a new window while Alt is held
- **THEN** no activation happens

### Requirement: Center cursor after window switch
The system SHALL move the cursor to the center of the active window when Alt is released, if the active window changed since the last Alt release and the cursor is outside it.

#### Scenario: Switching window centers cursor
- **WHEN** Alt is released and the active window is different and the cursor is outside it
- **THEN** the cursor moves to the center of the active window

#### Scenario: Cursor already inside stays put
- **WHEN** Alt is released and the active window changed but the cursor is already inside it
- **THEN** the cursor is not moved

#### Scenario: Same window does not move cursor
- **WHEN** Alt is released and the active window is unchanged
- **THEN** the cursor is not moved
