# Tasks

## 1. Tasks

- [x] 1.1 Updates.cs (port of repo-task-manager's AppUpdates): GitHub latest release of Artyom-kurguzkin/hotkeys-and-windowMax, SemVer compare, download + sha256 digest check + stage to %LOCALAPPDATA%\WindowManager\updates; 60 s after start, then every 24 h; Debug, local and off-main builds never update. WM_UPDATE_URL overrides the endpoint. <!-- agent: Claude chunk B -->
- [x] 1.2 UpdateHelper.cs: PowerShell swap after the manager exits (backup, robocopy, roll back on failure, restart); a locked monlie.dll is renamed aside. <!-- agent: Claude chunk B -->
- [x] 1.3 Wired: the menu shows the updater's line and an Update button (HelpPopup.UpdateLine / UpdateClicked); Update launches the helper and quits the clean way (WM_CLOSE, as Ctrl+Alt+Shift+Q). The version is AppVersion.Display so builds off main count as prereleases. <!-- agent: Claude -->
- [ ] 1.4 First real update end to end after the first two releases exist (needs a push to main). <!-- agent: Claude -->
