# Spec Delta

## Purpose

Hides the title bars and tab strips that apps draw inside their own windows, so every window shows only content. The bars can be shown again on demand.

## ADDED Requirements

### Requirement: Detect self-drawn title bars
The system SHALL treat a managed window as having a self-drawn title bar if the window has no system title bar area above its content when it is first managed. That means the top of its client area is within the frame thickness of the top of the window.

#### Scenario: Chromium window is detected
- **WHEN** a window whose client area starts at the window's top edge is managed
- **THEN** it is classified as having a self-drawn title bar

#### Scenario: Classic window is not cropped
- **WHEN** a window whose client area starts below a system caption is managed
- **THEN** it is not classified as having a self-drawn title bar and only its caption style is removed

### Requirement: Crop the top bar
The system SHALL stretch each window with a self-drawn title bar upward by its crop height and clip the stretched part, so its visible content fills exactly the area Windows assigned it. This SHALL hold whether the window is maximized, snapped or floating, and the window SHALL stay in that state.

#### Scenario: Window placed by Windows is cropped
- **WHEN** Windows places a self-drawn-bar window in a new area
- **THEN** the window is extended upward by the crop height and the extra strip is clipped away

#### Scenario: Own move is not cropped again
- **WHEN** the window reports the position the system just gave it
- **THEN** no further crop is applied

#### Scenario: Restore to a previously cropped rect is not double cropped
- **WHEN** Windows moves the window back to a rect that the system had already produced by cropping
- **THEN** only the clip is refreshed and the window is not extended again

#### Scenario: Refused crop is not retried
- **WHEN** an app snaps back to the same rect after a crop attempt
- **THEN** the same crop is not attempted again until the window moves elsewhere

### Requirement: App fullscreen is left alone
The system SHALL NOT crop a window that exactly fills its monitor with no frame overhang. That is app-level fullscreen, such as F11 or VS Code full screen, where the app has already hidden its bar. If a cropped window enters app fullscreen, its clip SHALL be removed without moving it.

#### Scenario: App fullscreen is not cropped
- **WHEN** a self-drawn-bar window's rect equals its monitor rect
- **THEN** no crop is applied

#### Scenario: Entering fullscreen drops the clip
- **WHEN** a cropped window switches itself to app fullscreen
- **THEN** its clip is removed and the window is not moved

### Requirement: Cropped strip never leaks
The system SHALL clip the cropped strip so that it is invisible even where it extends onto another monitor.

#### Scenario: Clip covers only content
- **WHEN** a crop is applied
- **THEN** the visible part of the window is exactly its content area below the crop line

### Requirement: Crop height per app
The system SHALL use a crop height per process name, in DPI-independent units, scaled by the window's DPI. Apps not listed SHALL use a default height.

#### Scenario: Height scales with DPI
- **WHEN** an app with a 40-unit crop is on a 144 DPI monitor
- **THEN** 60 pixels are cropped

#### Scenario: Unknown app uses default
- **WHEN** a self-drawn-bar app has no configured height
- **THEN** the default height is used

### Requirement: Tune crop height live
The system SHALL increase the active app's crop height by 2 units on Win+Alt+PageDown and decrease it by 2 units on Win+Alt+PageUp, never below 0. It SHALL re-crop that app's windows immediately and persist the value across restarts.

#### Scenario: Crop more
- **WHEN** the user presses Win+Alt+PageDown over a cropped app
- **THEN** that app's crop height grows by 2 units and its windows are re-cropped

#### Scenario: Crop never negative
- **WHEN** the crop height is 0 and the user presses Win+Alt+PageUp
- **THEN** the crop height stays 0

#### Scenario: Tuned value persists
- **WHEN** the program restarts after a crop height was tuned
- **THEN** the tuned value is used

### Requirement: Reveal hidden bars
The system SHALL toggle a global reveal on a tap of Win+Alt: both keys pressed, then released, with no other key in between. While revealed, cropped windows SHALL show their full title bars in their assigned areas. The tap SHALL NOT open the Start menu or a menu bar.

#### Scenario: WinAlt tap reveals
- **WHEN** the user presses Win, presses Alt, and releases them with no other key in between
- **THEN** the reveal is toggled and the release is masked so Start does not open

#### Scenario: WinAlt with another key is not a tap
- **WHEN** the user presses Win+Alt and then another key before releasing
- **THEN** the reveal is not toggled

#### Scenario: Second tap hides again
- **WHEN** the reveal is on and the user taps Win+Alt again
- **THEN** the bars are cropped again

### Requirement: Per-window reveal
The system SHALL show the full title bar of the active cropped window on Alt+T, and crop it again on the next Alt+T.

#### Scenario: AltT reveals one window
- **WHEN** the user presses Alt+T on a cropped window
- **THEN** that window's bar is shown and other windows stay cropped

### Requirement: UWP frames are managed
The system SHALL manage UWP app frame windows like other application windows, even though they carry the popup style.

#### Scenario: ApplicationFrameWindow is manageable
- **WHEN** a visible ApplicationFrameWindow is shown
- **THEN** it is managed and cropped like other self-drawn-bar windows

### Requirement: Crops removed on quit
The system SHALL remove every clip and return every cropped window to its assigned area when it quits.

#### Scenario: Quit uncrops
- **WHEN** the program quits cleanly
- **THEN** every cropped window has its clip removed and fills its assigned area with its bar visible
