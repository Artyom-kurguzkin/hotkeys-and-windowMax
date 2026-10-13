# Proposal

## Why

The portfolio website (`../portfolio`) uses a hand-drawn yellow arrow over cards and a matching pointing hand over clickable elements. The same look should apply across Windows while this program runs, without breaking programs that set their own cursor.

## What Changes

- While the program runs, Windows' standard arrow and link hand become the website's hand-drawn cursors: same paths, colours (`#ffc94d` fill, `#17171c` outline), hotspots and 40 px size scaled by the display DPI, as the browser draws them.
- Only those two standard cursors are replaced. Any program that sets its own cursor keeps it: websites with CSS cursors, games, drawing tools. Every other standard cursor (text I-beam, resize, busy) is unchanged, as on the website.
- Quitting (or a crash) restores the user's cursor scheme. Nothing is written to the user's settings.

## Capabilities

### New Capabilities
- `cursor-theme`: replacing Windows' standard arrow and hand with the portfolio's hand-drawn cursors, leaving custom cursors alone, and restoring the user's scheme.

### Modified Capabilities

## Impact

- New `CursorTheme.cs` (GDI+ rendering, `SetSystemCursor`, `SPI_SETCURSORS` restore) and `CursorArt` in Logic.cs (the SVG paths and an M/L/Q/Z path parser).
- No new packages: System.Drawing comes with the WinForms reference already used by the hotkey table.
