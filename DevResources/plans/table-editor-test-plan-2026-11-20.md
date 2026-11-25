# Table editor: test plan

What the owner expects is the old repo-task-manager experience, in any text field: `/table` → size picker at the caret → the table appears and its grid is over it → every edit lands in the text at once → leaving the grid returns to the text → putting the caret back in the table brings the grid back.

How it is run: `dist\WindowManager.exe --test-table` with the manager running (the running instance runs it in-process). The target is a plain text box in a PowerShell process the test starts and kills (never the user's apps: Notepad restores the user's tabs). Real keys (SendInput), real popup, real clipboard. Readings in `tests\out\table-selftest.md`, screenshots `tests\out\live-*.png`, timings from `wm.log`. Unit tests cover the pure parts (`dotnet test WindowManager.Tests`).

| # | Case | Expected | How it is checked |
|---|---|---|---|
| A1 | `/table` at a line start | the picker opens at the caret, the popup has the focus | live: popup shown + foreground; popup within 60 px of the caret |
| A2 | `/table` after a space; `x/table`; Backspace inside it; a chord or a click in between | opens / does not open / edits the buffer / resets | unit: SlashTriggerTests |
| A3 | the focus is not in a text control | nothing is deleted, nothing opens | unit + log ("not in a text control") |
| B1 | picker, Esc | the popup closes, `/table` is typed back | live: text == `/table` |
| B2 | picker, Right, Down, Enter | a table with 4 columns and 4 body rows is written | live: 6 table lines, 4 columns |
| B3 | picker, click elsewhere (deactivate) | `/table` typed back | live: TestDeactivate path, text == `/table` |
| C1 | picker Enter | the table is in the text AND the grid stays up | live: text has the table; popup still shown 1 s later |
| C2 | type in a cell, Enter | the text updates while the grid stays up | live: text contains the word; popup still shown |
| C3 | type right after a write starts | no character lost or sent to the app | live: whole word present, nothing extra in the text |
| C4 | Tab in the last cell | a row is added in the text | live: one more table line |
| C5 | Delete on a cell | the cell is cleared in the text | live: word gone |
| C6 | Esc out of the grid | the popup closes, the text keeps the final table | live: popup hidden; text == last table |
| D1 | caret back into the table | the popup opens over the table, focused | live: popup shown + foreground |
| D2 | edit a cell in the re-opened grid | the old table is replaced, not duplicated | live: same number of table lines, new word present |
| D3 | re-open and leave with no change | the text is untouched | live: text identical |
| E1 | after a session | the user's clipboard is what it was | live: clipboard text equal before / after |
| E2 | speed | popup shown < 150 ms after the trigger; a live write < 400 ms | log: open->shown, live write ms |
| E4 | another program copies after a session | not held up (< 0.5 s) | live: Clipboard.SetText from another thread, timed |
| E3 | no window chrome | no open animation, no title bar, only the cards | screenshot `live-*.png` |
