# Tasks

## 1. Scaffolding

- [x] 1.1 Create `WindowManager.slnx`, the `WindowManager` project (net10.0-windows WinExe) and the `WindowManager.Tests` project (xUnit); verify `dotnet build` succeeds
- [x] 1.2 Add `Native.cs` with the hook, message-loop, SendInput and window P/Invokes; add a test that asserts `INPUT` marshals to 40 bytes and `KBDLLHOOKSTRUCT` to 24 bytes, the x64 Win32 sizes

## 2. Logging and lifecycle

- [x] 2.1 Add `Log.cs` (file under %LOCALAPPDATA%, 5 MB rotation, verbose gate, Win32 error helper); unit-test rotation against a temp directory
- [x] 2.2 Add the message-only window, the message loop, single-instance takeover and the Ctrl+Alt+Shift+Q quit; verify by starting two instances and checking that the log shows the first one shutting down
- [x] 2.3 Make the takeover robust: close every running instance, terminate any that doesn't exit within 3 s, and repair windows it left modified from `state.json` (`State.cs`); unit-test the state round-trip and recovery against a test-owned window, and verify live by suspending an instance and starting a new one

## 3. Key engine

- [x] 3.1 Implement `KeyEngine` modifier tracking, the binding table and passthrough of injected input; add unit tests for each bind scenario in specs/hotkeys
- [x] 3.2 Implement `Lift`/`Restore` and dirty-modifier masking; add unit tests for the three "Shortcuts don't leak modifiers" scenarios
- [x] 3.3 Wire the engine to the low-level keyboard hook in Program.cs, sending all synthetic input from a dedicated sender thread (a masked release swallows the key-up and re-injects mask + key-up); add a unit test for `MaskedRelease`
- [ ] 3.4 Verify manually in Notepad that each bind works and that releasing Alt opens no menu

## Workflow follow-up

- Archive the change with `openspec archive add-core-and-hotkeys` once 3.4 passes.
