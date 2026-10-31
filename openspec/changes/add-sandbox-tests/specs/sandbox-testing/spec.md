# Spec Delta

## Purpose

Run the window manager through prepared sequences without touching the user's desktop, and report what happened.

## ADDED Requirements

### Requirement: A sandbox run never reaches the user's desktop
In sandbox mode the system SHALL NOT inject input into, read the foreground window of, move the cursor of, or change the
cursor scheme of the user's desktop, and SHALL NOT replace, recover or write the state of any other instance.

#### Scenario: Input has nowhere to go but the record
- **WHEN** the manager in sandbox mode would type F11, a key or a click
- **THEN** it is recorded and posted to the target window on the hidden desktop only

#### Scenario: The real manager is untouched
- **WHEN** a sandbox manager starts and stops while the real manager runs
- **THEN** the real manager keeps running with its cursors, log and state files unchanged

### Requirement: One environment check
The runner SHALL examine the environment once, at the start, and skip with a stated reason any group that needs a
capability it found missing.

#### Scenario: Missing capability
- **WHEN** UI Automation scans do not work on the hidden desktop
- **THEN** the hint-mode tests are skipped and the report says why, and nothing else is affected

### Requirement: Readings, not pass or fail only
After each action the runner SHALL record how long the first change and the settle took, every state the window passed
through, the loop's worst latency, the keys the application would have seen, and the manager's log lines, and SHALL
report defects and quality-bar misses separately.

#### Scenario: A visible flash
- **WHEN** a fullscreen window leaves fullscreen and re-enters during a tile
- **THEN** the report names the in-between rectangle and how long it was on screen
