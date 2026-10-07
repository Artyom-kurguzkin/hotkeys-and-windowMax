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
The system SHALL ask an already running instance to exit cleanly when a new instance starts. The new instance SHALL continue once the old one has exited.

#### Scenario: Second instance replaces first
- **WHEN** a second instance starts while one is running
- **THEN** the first instance exits cleanly and the second keeps running
