# Spec Delta

## Purpose

Lets the TrackPoint scroll in any direction while Alt is held. This replaces the middle-button drag-scroll, which is implemented in the touchpad driver and can't be triggered synthetically.

## ADDED Requirements

### Requirement: Alt turns TrackPoint motion into scrolling
The system SHALL convert relative motion from the TrackPoint into wheel events while Alt is physically held: vertical motion becomes vertical wheel events and horizontal motion becomes horizontal wheel events. Motion from other pointing devices SHALL NOT be converted.

#### Scenario: Push down scrolls down
- **WHEN** Alt is held and the TrackPoint reports downward motion
- **THEN** wheel-down movement proportional to the motion is emitted

#### Scenario: Other mouse is ignored
- **WHEN** Alt is held and a different mouse moves
- **THEN** no wheel movement is emitted

### Requirement: Scrolling is smooth
The system SHALL emit wheel movement in fine-grained deltas proportional to each motion report, like a touchpad or high-resolution wheel, not in whole 120-unit notches. Fractions too small to send SHALL carry over to the next report.

#### Scenario: Scrolling is smooth not notched
- **WHEN** a motion report worth about two notches arrives
- **THEN** the emitted wheel delta is not a multiple of one notch

### Requirement: Cursor holds still while scrolling
The system SHALL keep the cursor from moving for as long as the gesture lasts, by blocking cursor movement rather than moving the cursor back afterwards. The cursor SHALL stay where it was when Alt was pressed.

#### Scenario: Cursor does not move during gesture
- **WHEN** the TrackPoint moves while Alt is held
- **THEN** cursor movement is blocked and the cursor stays at the position it had when Alt was pressed

### Requirement: Scroll is not seen as Alt+wheel
The system SHALL release Alt logically once when a gesture starts, so applications receive plain wheel input, and SHALL mask the later physical Alt release so no menu bar opens.

#### Scenario: Alt lifted once per gesture
- **WHEN** a gesture starts while Alt is held
- **THEN** Alt is released logically once, not around every wheel event, and its physical release is masked

### Requirement: Acceleration curve
The system SHALL scale motion with a power curve, so small deflections scroll slowly and larger ones scroll faster. A single motion report SHALL produce at most 3 notches of wheel movement.

#### Scenario: Hard push is capped
- **WHEN** a single motion report has a very large deflection
- **THEN** at most 3 notches of wheel movement are emitted for it

#### Scenario: Small motion accumulates
- **WHEN** several small motion reports arrive
- **THEN** their total emitted wheel movement equals their summed scaled motion, with nothing lost to rounding

### Requirement: Direction reversal drops momentum
The system SHALL discard motion accumulated in one direction when motion arrives in the opposite direction on the same axis.

#### Scenario: Reversal starts fresh
- **WHEN** motion has accumulated downward and an upward report arrives
- **THEN** the accumulated downward motion is discarded

### Requirement: Scrolling stops with Alt
The system SHALL end the scroll gesture and discard accumulated motion as soon as Alt is released. A periodic check SHALL also end it if the Alt release was missed.

#### Scenario: Alt release stops scrolling
- **WHEN** Alt is released during a gesture
- **THEN** no further wheel movement is emitted from motion that was already accumulated

### Requirement: Device discovery is logged
The system SHALL log every pointing device it finds at startup and which one it uses as the TrackPoint. If none matches, the system SHALL log that scrolling is disabled and keep running.

#### Scenario: Missing TrackPoint disables feature
- **WHEN** no device matches the TrackPoint identifier at startup
- **THEN** a log line says TrackPoint scrolling is disabled and the rest of the program works normally
