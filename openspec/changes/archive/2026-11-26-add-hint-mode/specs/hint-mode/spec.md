# Spec Delta

## Purpose

Click anything on screen from the keyboard: Alt+F labels every clickable control with a letter badge, typing the
letters clicks it. Ported from repo-task-manager's hint mode, with the same badge styling, a bit smaller.

## ADDED Requirements

### Requirement: Alt+F toggles hint mode
The system SHALL show hint badges when Alt+F is pressed and hide them when Alt+F or Esc is pressed while hints are
shown. Alt+F SHALL NOT reach the application and its Alt release SHALL be masked, so no menu bar or KeyTips open.
A plain F SHALL always reach the application unchanged, whatever has focus.

#### Scenario: Alt+F shows hints
- **WHEN** the user presses Alt+F in any application, Word included
- **THEN** every clickable control on screen gets a letter badge and neither the F nor the Alt release reaches the application

#### Scenario: plain F types
- **WHEN** the user presses F with no modifier, in a text field or anywhere else
- **THEN** no hints are shown and the F is typed as usual

#### Scenario: Alt+F or Esc hides
- **WHEN** hints are shown and the user presses Alt+F or Esc
- **THEN** the badges disappear and no control is clicked

### Requirement: Clickable controls get stable, f-free labels
The system SHALL label the enabled, on-screen, visible clickable controls (buttons, links, inputs, check boxes, radio
buttons, combo boxes, list / tree / tab / menu items) of the focused window only. Labels SHALL be single letters from
`asdghjklqwertyuiopzxcvbnm` while they last and two-letter labels beyond that, and SHALL never contain f.

#### Scenario: Single letters first
- **WHEN** 3 controls are clickable
- **THEN** their labels are a, s and d

#### Scenario: Pairs beyond the alphabet
- **WHEN** 27 controls are clickable
- **THEN** labels are two letters, all distinct, the first letter being the group (aa, as, …, sa)

#### Scenario: Covered controls are not labelled
- **WHEN** a control's badge spot is covered by another window
- **THEN** it gets no badge

### Requirement: Labels are ready before they are asked for
The system SHALL scan the focused window ahead of time and SHALL show the badges from that scan on Alt+F, provided the same window has the focus at the same rectangle and the scan is under 6 seconds old; otherwise it SHALL scan again. Triggers, the in-flight wait, the redraw rule and `--no-prefetch` are in design.md, "Prefetch".

#### Scenario: Badges at once
- **WHEN** the user presses Alt+F after the focused window has been still for a moment
- **THEN** the badges appear within about 80 ms

#### Scenario: A scan of an old layout is never shown
- **WHEN** the focused window moved since the last scan
- **THEN** the badges come from a fresh scan

#### Scenario: Letters do not change under the user's fingers
- **WHEN** the page changes while the badges are up and the user has typed a letter
- **THEN** the badges are not redrawn

### Requirement: Typing a label clicks the control
The system SHALL treat letters typed while hints are shown as label input: badges that can no longer match SHALL dim
to 25% opacity; completing a label SHALL hide the hints and click that control; completing it with Shift held SHALL
Shift-click it and keep the hints shown for the next pick; input that can match nothing SHALL reset the typed
prefix. Letters typed while hints are shown SHALL NOT reach the application.

#### Scenario: Narrowing dims
- **WHEN** labels aa, as and sa are shown and the user types a
- **THEN** sa is dimmed and aa, as stay full

#### Scenario: Pick clicks and hides
- **WHEN** labels a, s, d are shown and the user types s
- **THEN** the hints hide and the control labelled s is clicked

#### Scenario: Shift-pick keeps picking
- **WHEN** labels a, s, d are shown and the user types Shift+d
- **THEN** the control labelled d is Shift-clicked and the badges stay shown with no prefix typed

#### Scenario: Nonsense resets
- **WHEN** labels a, s, d are shown and the user types x
- **THEN** nothing is clicked, the prefix is cleared and typing a then clicks a

### Requirement: Badge styling matches the web app, a bit smaller
Badges SHALL be drawn on a click-through overlay above everything, never taking focus, centred on the control's
bottom-right corner inset 6px: bold 10px text, 1px 4px padding, 3.5px radius, sun-yellow fill, dark text, 1.5px dark
outline, a soft 1px shadow, 25% opacity when dimmed, the dark palette when Windows' app theme is dark, scaled with
the monitor's DPI.

#### Scenario: Badges never take the click
- **WHEN** hints are shown and the user clicks with the mouse
- **THEN** the click reaches whatever is under the badge

### Requirement: Stale hints are dismissed
The system SHALL hide the hints when the foreground window changes.

#### Scenario: Switching windows hides
- **WHEN** hints are shown and another window becomes foreground
- **THEN** the badges disappear
