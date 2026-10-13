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
The system SHALL replace only the standard arrow and hand. A cursor that a program supplies itself (a website's CSS cursor, a game's cursor) and every other standard cursor SHALL be unchanged.

#### Scenario: Program with its own cursor keeps it
- **WHEN** a program sets a cursor of its own
- **THEN** that cursor is shown, not the hand-drawn one

### Requirement: User's cursors restored
The system SHALL restore the user's cursor scheme when it quits or crashes, and SHALL NOT write the hand-drawn cursors to the user's settings.

#### Scenario: Quit restores standard cursors
- **WHEN** the program quits
- **THEN** the standard arrow and hand are the user's own again
