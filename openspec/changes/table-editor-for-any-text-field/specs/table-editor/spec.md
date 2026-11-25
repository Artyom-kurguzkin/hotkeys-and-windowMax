## ADDED Requirements

### Requirement: Typing /table opens the size picker
When the user types `/table` at a word start (after a boundary key, click, focus change or chord, or after whitespace) and UI Automation reports a text control with the focus, the program SHALL delete those six characters with backspaces and show the original 8×8 size picker at the caret; otherwise it SHALL change nothing. There is no hotkey.

#### Scenario: /table at a line start
- **WHEN** the user presses Enter and types `/table` in a text control
- **THEN** `/table` is removed and the size picker opens at the caret, showing 3 × 3

#### Scenario: Picking a size
- **WHEN** the user sizes the picker with the arrows or the mouse and presses Enter or clicks
- **THEN** the grid editor opens on a new table with that many body rows and columns

#### Scenario: Picker cancelled
- **WHEN** the user presses Esc in the picker or clicks elsewhere
- **THEN** the target is focused again and `/table` is typed back

#### Scenario: /table inside a word
- **WHEN** the user types `x/table` without a boundary in between
- **THEN** nothing happens

#### Scenario: The focus is not in a text control
- **WHEN** `/table` was typed where UI Automation sees no text control
- **THEN** nothing is deleted and the picker does not open

### Requirement: The grid editor of the original
The editor SHALL be a popup at the caret with the original ribbon (rows, columns, alignment, sort, Edit as text), column letters, row numbers, click / drag / Shift selection, in-place editing, Ctrl+C/X/V as tab-separated text and Alt+arrow moves. Tab in the last cell SHALL add a row. There is no cancel: every way of closing it keeps the changes.

#### Scenario: Tab in the last cell
- **WHEN** the active cell is the last one and Tab is pressed
- **THEN** a row is added and the first cell of it is active

#### Scenario: Esc keeps the changes
- **WHEN** a cell was changed and Esc is pressed outside an in-place edit
- **THEN** the editor closes and the table with the change is written

### Requirement: Editing a written table again
When the caret moves into a pipe table in a text control (after a click or a caret key), the program SHALL read the table through UI Automation and show an "Edit table" chip above the caret without taking the focus; clicking it SHALL select the table in the app and open the grid editor on it. Any other key SHALL hide the chip. On close the table SHALL be written over the selection only if it changed (as pipe text, whatever the app); otherwise the selection is collapsed.

#### Scenario: Caret back in a table
- **WHEN** the caret is put into a pipe table in Notepad
- **THEN** the chip shows above the caret, and a click opens the grid on that table

#### Scenario: Re-edit with a change
- **WHEN** a cell is changed in the re-opened grid and it is closed
- **THEN** the old table is replaced by the changed one

#### Scenario: Re-edit with no change
- **WHEN** the re-opened grid is closed without a change
- **THEN** the text is untouched

### Requirement: The table is written in the target's format
On close the program SHALL put the table on the clipboard and paste it where `/table` was, or over the selected table. A new table in Word (WINWORD) or a browser whose title contains "Google Docs" SHALL be an HTML table in CF_HTML framing (UTF-8, byte-exact offsets) with the pipe table as text fallback; anywhere else, and for every re-edited table, the formatted pipe table as text. The user's clipboard SHALL be restored afterwards.

#### Scenario: Word target
- **WHEN** a new table is closed and the target process is WINWORD
- **THEN** the clipboard holds an HTML table and the pipe text, Ctrl+V is sent to Word, and the previous clipboard is restored

#### Scenario: Markdown or plain text target
- **WHEN** a table is closed and the target is a code editor or Notepad
- **THEN** only the formatted pipe table is pasted
