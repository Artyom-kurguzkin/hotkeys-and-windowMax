# Proposal

## Why

The portfolio website (`../portfolio`) uses a hand-drawn yellow arrow over cards and a matching pointing hand over clickable elements. The same look should apply across Windows while this program runs, without breaking programs that set their own cursor.

## What Changes

- While the program runs, Windows' standard arrow and link hand become the website's hand-drawn cursors (same paths, colours `#ffc94d` fill and `#17171c` outline, hotspots, and 40 px size scaled by the display DPI), and the other 12 standard pointers (text I-beam, busy and working spinners, help, precision cross, alternate, unavailable, move, four resize directions) are drawn to match. The busy pointers animate.
- Only the standard pointers are replaced. Any program that sets its own cursor keeps it: websites with CSS cursors, games, drawing tools.
- Quitting (or a crash) restores the user's cursor scheme. Nothing is written to the user's settings.

## Capabilities

### New Capabilities
- `cursor-theme`: replacing all of Windows' standard pointers with hand-drawn ones in the portfolio's style, leaving custom cursors alone, and restoring the user's scheme.

### Modified Capabilities

## Impact

- New `CursorTheme.cs` (GDI+ rendering, `SetSystemCursor`, `SPI_SETCURSORS` restore) and `CursorArt` in Logic.cs (the SVG paths and an M/L/Q/Z path parser).
- No new packages: System.Drawing comes with the WinForms reference already used by the hotkey table.
