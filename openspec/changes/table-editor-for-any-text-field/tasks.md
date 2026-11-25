# Tasks

## 1. Build

- [x] 1.1 MdTable.cs: port md-table.js (parse, format, setCell, applyOp, rewrite) with MdTableTests <!-- agent: Claude -->
- [x] 1.2 ClipboardTable.cs: read the selection (pipe, TSV, HTML), write CF_HTML or pipe text, detect the target app <!-- agent: Claude -->
- [x] 1.3 TableEditor.cs: the WinForms grid with Tab/Enter, insert/delete row and column, paste, OK/Esc <!-- agent: Claude -->
- [x] 1.4 Logic.cs: Win+Alt+T hotkey, help row, README hotkey table <!-- agent: Claude -->
- [x] 1.5 Typed trigger instead of the hotkey (2026-11-20): `SlashTrigger` fed from the keyboard hook with `ToUnicodeEx` characters; UIA focused-control check; backspaces remove `/table` (espanso's way), Esc types it back; Win+Alt+T removed. Select-and-copy verification was tried and dropped: a copy from the target blocked for seconds because this thread owned the clipboard (OLE clear) without pumping messages. <!-- agent: Claude -->
- [x] 1.7 The original UX restored (2026-11-20, plan DevResources/plans/table-editor-original-ux-2026-11-20.md): size picker (TablePicker.cs), the grid with the ribbon, headers, selection and in-place editing (TableEditor.cs, no cancel, Tab adds a row), re-edit through the "Edit table" chip (TableReedit.cs), shared palette (TableTheme.cs). 408 tests pass; `--test-table` on Notepad: every step ok, including picker cancel, picker 2×3 + edit, chip on caret-in-table, re-edit with and without a change. Word, Google Docs and VS Code not exercised. <!-- agent: Claude -->
- [x] 1.6 `--test-table` self-test on the real desktop (the sandbox cannot see injected keys), run 2026-11-20 with the user's permission: Notepad, every step ok including the production path end to end (OK replaces `/table` with the table, Esc restores it). Readings in tests/out/table-selftest.md. Word, Google Docs and VS Code not yet exercised. <!-- agent: Claude -->
