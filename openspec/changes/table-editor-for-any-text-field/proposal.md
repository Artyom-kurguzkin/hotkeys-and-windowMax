# Proposal

## Why

The repo-task-manager app had a markdown table editor (type /table, edit a pipe table in a grid). The owner wants it out of that app and here instead, as a system-wide helper that works in Markdown and txt editors, Word and Google Docs (repo-task-manager #wi208).

## What Changes

- Win+Alt+T opens a table editor for the focused control's selection: the selection is copied, parsed as a markdown pipe table, a tab-separated block (what Word and Google Docs give when a table is copied) or, with nothing selected, a new 3x3 table.
- A grid window with Word-like editing: Tab/Enter move between cells, insert/delete rows and columns, paste, Esc cancels.
- OK writes the table back: a markdown pipe table for markdown and plain-text targets; an HTML table (CF_HTML with a text fallback) for Word and Google Docs; then pastes it over the selection.
- The pure table logic is a C# port of repo-task-manager's md-table.js with its tests ported.
