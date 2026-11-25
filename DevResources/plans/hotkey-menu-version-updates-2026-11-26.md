# Hotkey menu restyle, semantic version, self-update (2026-11-26)

Status: done 2026-11-26 (integrated, 394 tests pass); the on-screen menu and the first real update not yet exercised

## Why
- Owner: "style double alt menu just like you did the table editor. It should also show current semantic version (look how semantic version and release build automation is done in task manager proj)".
- Owner: "i want this proj to auto-check for updates similar to task manager proj".

## What the industry does / the reference
- repo-task-manager (DevResources/plans/releases-and-self-update-2026-11-24.md there): Nerdbank.GitVersioning (`version.json` 0.1 + git height, `publicReleaseRefSpec` main) https://github.com/dotnet/Nerdbank.GitVersioning ; a GitHub Actions workflow publishes a release `v{SemVer2}` with one zip on every push to main https://cli.github.com/manual/gh_release_create ; the app asks `GET /repos/{owner}/{repo}/releases/latest` https://docs.github.com/en/rest/releases/releases#get-the-latest-release , compares SemVer, downloads and stages the zip, and a PowerShell helper waits for the app to exit, backs up, swaps the files and restarts it (the pattern Velopack / Squirrel automate https://docs.velopack.io/).

## Decisions
1. The hotkey list (both Alts) becomes a WebView2 page styled with the table editor's own `web/css/app.css` and Plus Jakarta Sans, light theme only, in a borderless popup like the table editor's. The WinForms HelpWindow is removed. `--hotkeys` still shows it on its own.
2. The version comes from Nerdbank.GitVersioning (`version.json` `0.1`, public on `main`), read from AssemblyInformationalVersion with `+commit` cut off. The menu shows it.
3. Release: `.github/workflows/release.yml`, on every push to main: `dotnet publish WindowManager -r win-x64 -o dist`, zip `WindowManager-win-x64.zip`, tag `v{SemVer2}`. Repository `Artyom-kurguzkin/hotkeys-and-windowMax`.
4. Self-update ported from repo-task-manager's AppUpdates.cs and UpdateHelper.cs: checked a minute after start and every 24 h (the manager runs for days), downloaded and staged in the background, applied when the user clicks Update in the menu (the manager quits cleanly, restoring windows, and the helper swaps the files and starts it again). A Debug or local (non-public) build never updates itself. `monlie.dll` stays mapped in other processes after quitting: the helper renames a locked one aside, as the build does.
5. The menu and the updater meet through two hooks on the menu (`HelpPopup.UpdateLine`, `HelpPopup.UpdateClicked`), so neither chunk compiles against the other.

## Chunks
| Chunk | Owns | Tests |
|---|---|---|
| A menu + version | HelpPopup.cs (new), web/hotkeys.html, web/js/hotkeys.js, HelpWindow.cs (deleted), Program.cs call sites of HelpWindow, AppVersion.cs, version.json, csproj (NB.GV), .github/workflows/release.yml, README | HelpPopupTests (message building, version text); an off-screen render screenshot |
| B self-update | Updates.cs, UpdateHelper.cs (new), UpdatesTests.cs | fake HTTP handler + temp folders: SemVer order, newer / same / older / debug, download + stage, helper script launched with the right arguments |

## Verification
Full suite after merging; `--hotkeys` screenshot compared with the table editor's look; the update check run against a local fake endpoint (`WM_UPDATE_URL`), never a real swap on the owner's desktop without their go-ahead.
