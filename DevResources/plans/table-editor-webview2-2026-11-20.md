# Table editor: the original page in WebView2

Status: in progress (host and page with agents; wiring, removals and the real-desktop check by the supervisor)

## Why

The WinForms imitation (table-editor-original-ux-2026-11-20.md) was rejected: "it still looks ugly, I need it to look and feel EXACTLY like the old thing, one to one". A side-by-side render (headless Edge running the original code vs ours) showed wrong structure (one boxed dialog instead of a floating ribbon over an inline table), sizing, weights and selection. Only running the original page reproduces it.

## Research and measurements

- Options considered: Rust GPUI, WinUI 3, the original page in WebView2 (owner's shortlist; no WinForms). Only WebView2 is one-to-one (same Chromium engine as the old Photino window); GPUI and WinUI 3 are rewrites. Sources: WebView2 performance guide (https://learn.microsoft.com/microsoft-edge/webview2/concepts/performance), controller creation on build 26200 ~420 ms default profile vs ~5.4 s named profile (https://github.com/MicrosoftEdge/WebView2Feedback/issues/5557), GPUI has no hide/show of a window on Windows (zed crates/gpui_windows/src/window.rs).
- Measured on this machine (WebView2 157.0.4341.60, DPI 1.5, the original page): cold 0.55–0.70 s to the overlay painted (environment 14 ms, controller 270–290 ms, navigation 120–210 ms); pre-warmed hidden → shown 17.6 ms median, 18.8 ms p95; in-page edit → frame 13.7 ms; hidden idle 134 MB private across 6 child processes.

## Decisions

1. WebView2 (Microsoft.Web.WebView2, Core API only) on a raw Win32 popup (WS_POPUP, WS_EX_TOOLWINDOW | WS_EX_TOPMOST), created and navigated once at startup, kept hidden; default profile; user data in %LOCALAPPDATA%\WindowManager\WebView2.
2. The page is the original app.css, md-table.js, md-editor.js and the Plus Jakarta Sans woff2 files, byte for byte, light theme (`data-theme="light"`, the old app's setting), plus editor.html (a note-editor tile with the `textarea.desc-in`) and host.js (the bridge). Served by a virtual host mapping.
3. The window is large and transparent outside the cards: the page reports the cards' rectangles (tile, ribbon, picker, with the 4px hard shadow) and the host clips the window to them with a region, so the popup floats over the user's app like the old cards floated over the page.
4. `/table`: the popup opens with `/table` in its textarea and the caret after it, so the original picker opens; the original flow follows. Clicking (or moving the caret) into a pipe table in any text control opens the popup at once, focused, with that table and the caret where it was (replaces the chip). Closing (Esc with nothing open, or clicking back into the app) writes the textarea back if it changed; `/table` left alone is typed back. "Edit as text" closes it and does not reopen until the caret leaves that table.
5. Idle: TrySuspendAsync after 30 s hidden.

## Protocol (page ↔ host, JSON web messages)

- page → host: `{type:"ready"}`; `{type:"shape", rects:[{x,y,w,h,r}], anchor:{x,y}}` (CSS px; anchor = where the textarea's first text line starts); `{type:"close", text, reason:"escape"|"collect", asText}`.
- host → page: `{type:"open", text, caret}`; `{type:"collect"}`.

## Chunks

| Chunk | Owns | Test |
|---|---|---|
| host | WindowManager/TablePopup.cs, WindowManager.Tests/TablePopupTests.cs, WindowManager/WindowManager.csproj | message parsing and region maths unit tests; an off-screen, never-activated run: prewarm, open, shape, collect |
| page | WindowManager/web/** | headless Edge with a chrome.webview shim: screenshots of picker, grid and text states vs the old renders; shape and close messages |
| wiring | Program.Table.cs, Program.TableTest.cs, TableReedit.cs, ClipboardTable.cs, removals | full suite; `--test-table` on Notepad |

## Verification

Full suite once after merging; `--test-table` on Notepad (typed → picker → table written; click into the table → popup → change → written); a screenshot of the live popup over Notepad next to the old render.
