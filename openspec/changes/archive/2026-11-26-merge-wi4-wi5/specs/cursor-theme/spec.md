# Spec Delta

## Purpose

Gives Windows the same hand-drawn cursors as the portfolio website while the program runs, without overriding programs that choose their own cursor.

## ADDED Requirements

### Requirement: Hand-drawn standard cursors
The system SHALL replace Windows' standard arrow with the portfolio's hand-drawn yellow arrow and the standard link hand with its matching pointing hand while it runs. Both SHALL use the website's paths, colours and hotspots, at 40 px scaled by the system DPI.

#### Scenario: Standard arrow is hand drawn
- **WHEN** the program is running and an app shows the standard arrow cursor
- **THEN** the hand-drawn yellow arrow is shown, with its hotspot at the tip

#### Scenario: Standard hand is hand drawn
- **WHEN** the program is running and an app shows the standard link hand
- **THEN** the hand-drawn pointing hand is shown, with its hotspot at the fingertip

#### Scenario: Size and hotspot scale with DPI
- **WHEN** the system DPI is 144 (150%)
- **THEN** the cursors are 60 px and the hand's hotspot is at (24, 5)

### Requirement: Custom cursors are left alone
The system SHALL replace only the 14 standard pointers. A cursor that a program supplies itself (a website's CSS cursor, a game's cursor) SHALL be unchanged.

#### Scenario: Program with its own cursor keeps it
- **WHEN** a program sets a cursor of its own
- **THEN** that cursor is shown, not the hand-drawn one

### Requirement: User's cursors restored
The system SHALL restore the user's cursor scheme when it quits or crashes, and SHALL NOT write the hand-drawn cursors to the user's settings.

#### Scenario: Quit restores standard cursors
- **WHEN** the program quits
- **THEN** all standard pointers are the user's own again

### Requirement: Cursors stay applied
The system SHALL re-apply the hand-drawn cursors when Windows reloads the user's cursor scheme on its own (after waking from sleep, on display, DPI or theme changes). It SHALL check after every wake or display repair and every 3.5 seconds.

#### Scenario: Reset arrow is detected
- **WHEN** Windows has replaced the standard arrow with the scheme's own
- **THEN** the hand-drawn cursors are applied again within 3.5 seconds

#### Scenario: Our arrow is recognised
- **WHEN** the standard arrow is still the hand-drawn one
- **THEN** nothing is re-applied

### Requirement: Every standard pointer is hand-drawn
The system SHALL replace all 14 standard Windows pointers with hand-drawn versions in the website's style (yellow fill, dark outline, faint detail strokes): arrow, link hand, text I-beam, busy, working in background, help, precision cross, alternate (up arrow), unavailable, move, and the four resize directions. The busy pointers SHALL animate.

#### Scenario: Every standard pointer is covered once
- **WHEN** the program applies the cursors
- **THEN** each of the 14 standard pointer IDs gets exactly one hand-drawn pointer

#### Scenario: Busy pointer spins
- **WHEN** an app shows the busy or working-in-background pointer
- **THEN** its dark arc goes round (12 frames, 0.8 s a turn)

#### Scenario: Rotated pointers click at the centre
- **WHEN** a resize pointer is one drawing turned by 90 or 45 degrees
- **THEN** its hotspot is the centre, where rotation leaves it
