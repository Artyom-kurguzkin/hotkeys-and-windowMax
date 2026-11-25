# Tasks

## 1. Tasks

- [x] 1.1 The hotkey menu (both Alts) as a WebView2 page in the table editor's style (web/hotkeys.html, web/js/hotkeys.js, app.css unchanged, light), HelpPopup.cs; WinForms HelpWindow.cs removed; `--hotkeys` still works. Screenshot from the off-screen test: tests/out/hotkeys-menu.png. <!-- agent: Claude chunk A -->
- [x] 1.2 Semantic version: Nerdbank.GitVersioning 3.9.50, version.json 0.1 (public on main), AppVersion.cs; a build off main shows 0.1.x-g<commit>. Shown in the menu's header. <!-- agent: Claude chunk A -->
- [x] 1.3 .github/workflows/release.yml: every push to main publishes v{SemVer2} with WindowManager-win-x64.zip (exe, web/, WebView2Loader.dll, monlie.dll). Local Release publish checked; the workflow itself not yet run. <!-- agent: Claude chunk A -->
- [x] 1.4 Integrated; full suite 394 passed (5 runs). HelpPopupTests, UpdatesTests and TablePopupTests serialized with the other Log.Init callers. <!-- agent: Claude -->
- [ ] 1.5 Open the menu on the real desktop (both Alts): focus, Esc, click-away. Not run (no live input). <!-- agent: Claude -->
