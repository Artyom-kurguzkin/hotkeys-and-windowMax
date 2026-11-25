# Table editor: restore the original repo-task-manager experience

Status: done 2026-11-20 (merged; 408 tests; --test-table on Notepad all ok; Word, Google Docs, VS Code not exercised)

## Why

The system-wide `/table` editor ported from repo-task-manager (md-editor.js / md-table.js, last at repo-task-manager commit 2ee9a45) kept the table logic but replaced its UI with a plain WinForms dialog, and lost the way back into a table once it was written. The owner wants the old experience: the size picker, the grid with its ribbon, headers, selection and in-place editing, and editing a generated table again.

## What the industry does

- Text expanders (espanso) never read the target back: they trust their own keystroke buffer, delete the trigger with backspaces and inject the replacement by key events or clipboard + Ctrl+V ([espanso injection](https://deepwiki.com/espanso/espanso/5.4-text-injection-mechanisms)). The trigger already works this way (verified 2026-11-20).
- Reading the text around the caret system-wide is UI Automation's TextPattern (`GetSelection`, `ExpandToEnclosingUnit(Line)`, `Move`), which Notepad, Word, WordPad and browser text fields support; the caret's screen position is `GetGUIThreadInfo().rcCaret`, with UIA `GetBoundingRectangles` as a fallback for apps that draw their own caret.

## Decisions

1. `/table` (as now: typed at a word start in a text control, removed with backspaces) opens the original 8×8 size picker at the caret: hover or arrows size it, Enter or click inserts, Esc types `/table` back. The picked table is pasted, and the grid editor opens on it at once (in the original the caret landed in the new table, which brought the grid up).
2. The editor looks and behaves like the original overlay + ribbon: grouped ribbon (Row above, Row below, Column left, Column right, Delete row, Delete column | align left / centre / right icons with the active one lit | Sort A–Z, Sort Z–A | Edit as text), A/B/C column headers, 1/2/3 row headers, corner select-all, click/drag/Shift selection of cells, rows and columns, translucent yellow selection, active-cell outline, bold header row with a heavier rule, in-place edit on typing / Enter / F2 / double-click, Ctrl+A, Ctrl+C/X/V as TSV, Delete/Backspace clear, Alt+arrows move rows/columns, Tab past the last cell adds a ROW (the original; the port had changed it to a column). There is no Cancel: as in the original, every change is the document's; closing (Esc, an arrow or Enter past the first/last row, clicking elsewhere) writes the table back if it changed, and the app's own Ctrl+Z undoes it. "Edit as text" closes the grid and leaves the markdown. A borderless popup placed over/near the caret, in the original palette (light and dark).
3. Editing a table again: when the caret moves into a pipe table in a text control (UIA reads the caret's line after a click or a caret key), an "Edit table" chip appears next to the caret, like the original ribbon appearing; clicking it reads the whole table through UIA, selects it through UIA, opens the editor, and pastes the result over the selection. Apps without TextPattern get no chip (logged).

## Chunks

| Chunk | Owns | Test |
|---|---|---|
| A size picker | `WindowManager/TablePicker.cs`, `WindowManager.Tests/TablePickerTests.cs` | size state, caret rect helper, render light/dark |
| B editor | `WindowManager/TableEditor.cs`, `WindowManager.Tests/TableEditorTests.cs` | each behaviour driven through public methods; render light/dark |
| C re-edit | `WindowManager/TableReedit.cs`, `WindowManager.Tests/TableReeditTests.cs` | table-at-caret extraction from lines; chip render; UIA read against a Notepad |
| integration | `Program.Table.cs`, `Program.TableTest.cs`, `ClipboardTable.cs`, README, OpenSpec | `--test-table` on Notepad (real desktop, authorized by the owner for this test) |

## Verification

- Unit tests per chunk, then the whole suite once after integration.
- `WindowManager.exe --test-table`: Notepad, `/table` → picker → 3×3 inserted → editor → edit a cell → closed → table written; caret back into it → chip → editor → change → written again.
- Renders of the picker, editor and chip in light and dark to `tests/out/`.
