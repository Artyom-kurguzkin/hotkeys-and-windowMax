# Proposal

## Why

The mouse half of `binds.ahk` still has to move to the C# program before the AHK script can be retired: hover-to-focus, cursor centering after Alt+Tab, and Alt+TrackPoint drag-scrolling.

## What Changes

- Hovering over a real application window focuses it. Hover-focus pauses while Alt is held.
- After Alt is released, if a different window became active and the cursor is outside it, the cursor jumps to the window's center.
- While Alt is held, TrackPoint motion turns into wheel scrolling (both axes) with an acceleration curve, and the cursor stays still. Scrolling stops immediately when Alt is released.
- The log lists the pointing devices found at startup and which one was picked as the TrackPoint, which makes a wrong device ID easy to diagnose.

## Capabilities

### New Capabilities
- `focus`: focus-follows-mouse and cursor centering after window switches.
- `trackpoint-scroll`: Alt+TrackPoint scrolling, its acceleration curve and its stop conditions.

### Modified Capabilities

## Impact

- Adds Raw Input registration and timers on the existing message-only window. These are new P/Invokes.
- Depends on `add-core-and-hotkeys`, which provides the physical Alt state and the message loop.
- After this change, `binds.ahk` is fully replaced.
