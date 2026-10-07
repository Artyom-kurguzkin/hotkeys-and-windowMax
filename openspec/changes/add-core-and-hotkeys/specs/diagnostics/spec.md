# Spec Delta

## Purpose

Lets the user see what the program decided and why, so problems can be diagnosed from a log instead of by trial and error. Also controls the program's lifecycle: quitting and single-instance behavior.

## ADDED Requirements

### Requirement: Log file
The system SHALL append timestamped lines to `%LOCALAPPDATA%\WindowManager\wm.log`. The log SHALL record startup, shutdown, every shortcut action fired, and every failed Win32 call with its error code.

#### Scenario: Action is logged
- **WHEN** a shortcut fires
- **THEN** a line naming the action is appended to the log

### Requirement: Log rotation
The system SHALL, at startup, rename a log larger than 5 MB to `wm.log.old`, replacing any previous `.old` file.

#### Scenario: Large log is rotated
- **WHEN** the program starts and wm.log is larger than 5 MB
- **THEN** wm.log is renamed to wm.log.old and a fresh wm.log is started

### Requirement: Verbose mode
The system SHALL log every raw keyboard event only when started with `--verbose`.

#### Scenario: Raw keys hidden by default
- **WHEN** the program runs without --verbose
- **THEN** raw key events are not written to the log

### Requirement: Quit shortcut
The system SHALL exit cleanly on Ctrl+Alt+Shift+Q.

#### Scenario: CtrlAltShiftQ quits
- **WHEN** the user presses Ctrl+Alt+Shift+Q
- **THEN** the program logs shutdown and exits

### Requirement: Single instance
The system SHALL, on start, ask every already running instance to exit cleanly. Any instance that has not exited within 3 seconds SHALL be terminated. Only the new instance SHALL keep running.

#### Scenario: Second instance replaces first
- **WHEN** a second instance starts while one is running
- **THEN** the first instance exits cleanly, restoring its windows, and the second keeps running

#### Scenario: Unresponsive instance is terminated
- **WHEN** a new instance starts while an old instance is running but not responding
- **THEN** the old instance is terminated after 3 seconds and the new one keeps running

### Requirement: Recover after a terminated instance
The system SHALL persist which windows it changed, with their original style and crop slot, whenever it changes them. On start, it SHALL undo any changes left behind by an instance that was terminated or crashed. After a clean exit there SHALL be nothing to undo.

#### Scenario: Recovery undoes a dead instance's changes
- **WHEN** an instance is terminated while windows are stripped and cropped
- **THEN** the next instance first restores those windows' title bars, removes their clips and returns them to their slots

#### Scenario: Clean exit leaves nothing to recover
- **WHEN** an instance exits cleanly
- **THEN** no recovery record remains

### Requirement: Input is never injected from the hook thread
The system SHALL send all synthetic input from a dedicated thread, in order, and never from the thread that runs its keyboard and mouse hooks.

#### Scenario: Masked release sends mask before the release
- **WHEN** a modifier release must be masked
- **THEN** the physical release is withheld and the mask key followed by the release is sent in that order
