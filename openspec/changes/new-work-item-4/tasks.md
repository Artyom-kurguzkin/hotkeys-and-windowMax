# Tasks

## 1. Tasks

- [x] 1.1 Cause: PgUp/PgDn switch desktops through the shell's internal API (VirtualDesktops.Switch). With Task View open that moves the desktop behind it, not Task View's selection, so Enter goes back to the desktop Task View had selected. <!-- agent: Claude -->
- [x] 1.2 Fix: with Task View in the foreground (class XamlExplorerHostIslandWindow*, Windows 10: MultitaskingViewFrame) PgUp/PgDn send Ctrl+Win+Left/Right, which Task View handles itself; elsewhere the API as before. Program.cs Run, VirtualDesktops.IsTaskView. 384 tests pass. <!-- agent: Claude -->
- [ ] 1.3 Verify on the real desktop: Win+Tab, PgDn, Enter lands on the next desktop; wm.log shows "task view open: desktop switch by keystroke". If that line is missing, Task View's foreground class differs: note it from the log. <!-- agent: Claude -->
