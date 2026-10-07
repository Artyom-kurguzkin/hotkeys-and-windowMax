# Spec Delta

## Purpose

Global keyboard shortcuts that replace the binds previously provided by binds.ahk. They emit keys, clicks and wheel events system-wide.

## ADDED Requirements

### Requirement: Mouse emulation
The system SHALL emit the context-menu key on Alt+Z and a left click at the cursor on Alt+C. Either Alt key SHALL work.

#### Scenario: AltZ sends AppsKey
- **WHEN** the user presses Alt+Z
- **THEN** the AppsKey is emitted and Z is not delivered

#### Scenario: AltC clicks
- **WHEN** the user presses Alt+C
- **THEN** a left click is emitted at the cursor position and C is not delivered

### Requirement: Escape on left Alt+X
The system SHALL emit Escape on LeftAlt+X only. RightAlt+X SHALL pass through unchanged.

#### Scenario: LeftAltX sends Escape
- **WHEN** the user presses LeftAlt+X
- **THEN** Escape is emitted and X is not delivered

#### Scenario: RightAltX passes through
- **WHEN** the user presses RightAlt+X
- **THEN** the keystroke is delivered unchanged

### Requirement: Line navigation
The system SHALL map Alt+] to End, Alt+[ to Home, Shift+Alt+] to Shift+End and Shift+Alt+[ to Shift+Home.

#### Scenario: AltBracket jumps to line edges
- **WHEN** the user presses Alt+] or Alt+[
- **THEN** End or Home is emitted, respectively, without Alt applied

#### Scenario: ShiftAltBracket selects to line edges
- **WHEN** the user presses Shift+Alt+] or Shift+Alt+[
- **THEN** Shift+End or Shift+Home is emitted, respectively

### Requirement: New line below
The system SHALL emit End followed by Shift+Enter on Alt+O or Shift+Alt+O. Shift+Enter is used so that chat inputs insert a newline instead of sending the message.

#### Scenario: AltO opens line below
- **WHEN** the user presses Alt+O
- **THEN** End then Shift+Enter are emitted

#### Scenario: ShiftAltO opens line below
- **WHEN** the user presses Shift+Alt+O
- **THEN** End then Shift+Enter are emitted

### Requirement: Desktop switching
The system SHALL map unmodified PageUp to Ctrl+Win+Left and unmodified PageDown to Ctrl+Win+Right.

#### Scenario: PageUp switches desktop left
- **WHEN** the user presses PageUp with no modifiers
- **THEN** Ctrl+Win+Left is emitted

#### Scenario: PageDown switches desktop right
- **WHEN** the user presses PageDown with no modifiers
- **THEN** Ctrl+Win+Right is emitted

#### Scenario: Modified PageUp passes through
- **WHEN** the user presses Shift+PageUp
- **THEN** the keystroke is delivered unchanged

### Requirement: Keyboard scrolling
The system SHALL emit 3 wheel steps about 10 ms apart: down on Alt+J, up on Alt+K, left on Alt+H, and right on Alt+L.

#### Scenario: AltJ scrolls down
- **WHEN** the user presses Alt+J
- **THEN** three wheel-down steps are emitted

#### Scenario: AltH scrolls left
- **WHEN** the user presses Alt+H
- **THEN** three wheel-left steps are emitted

### Requirement: Shortcuts don't leak modifiers
The system SHALL emit each shortcut's output without the user's held modifiers applied. Releasing a held Alt or Win key afterwards SHALL NOT open a menu bar or the Start menu.

#### Scenario: Held modifiers are lifted around output
- **WHEN** a shortcut fires while Alt is physically held
- **THEN** a neutral mask key is emitted, Alt is released, the output is emitted, and Alt is pressed again

#### Scenario: Alt release after shortcut is masked
- **WHEN** the user releases Alt after one or more shortcuts fired while it was held
- **THEN** a neutral mask key is emitted before the release so no menu activates

#### Scenario: Plain Alt tap is untouched
- **WHEN** the user taps Alt alone
- **THEN** no mask key is emitted and the tap behaves normally

### Requirement: Synthetic input is ignored
The system SHALL NOT treat injected (synthetic) keystrokes as shortcut triggers. This includes keystrokes the system emits itself.

#### Scenario: Injected key is passed through
- **WHEN** an injected Alt+Z arrives
- **THEN** it is delivered unchanged and no action fires
