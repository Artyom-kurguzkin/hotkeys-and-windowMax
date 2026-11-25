using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace WindowManager.Tests;

// The app keeps itself up to date: it asks GitHub for the latest release, downloads and stages a newer one, and a PowerShell helper swaps the files after the app quits.
// The network is a fake handler, every folder is a temp folder, and the helper runs against dummy processes: nothing here reaches GitHub or touches a real install.
[Collection("Log")] // calls the static Log.Init and reads the log back; must not run alongside the other Log.Init callers
public sealed class UpdatesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "wm-update-" + Guid.NewGuid().ToString("N")[..8]);
    string Install => Path.Combine(root, "app");
    string ExePath => Path.Combine(Install, "WindowManager.exe");
    string UpdatesDir => Path.Combine(root, "updates");

    public UpdatesTests() => Directory.CreateDirectory(Install);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    sealed class FakeGitHub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            lock (Requests) Requests.Add(request);
            return answer(request, cancel);
        }
    }

    static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static string ReleaseJson(string tag, byte[]? zip = null, string assetName = "WindowManager-win-x64.zip") =>
        $$"""{"tag_name":"{{tag}}","assets":[{"name":"{{assetName}}","browser_download_url":"https://downloads.invalid/{{tag}}/{{assetName}}","digest":{{(zip is null ? "null" : "\"sha256:" + Convert.ToHexStringLower(SHA256.HashData(zip)) + "\"")}}}]}""";

    // The release query is answered with `release`, the asset download with `zip`.
    static FakeGitHub GitHub(string release, byte[]? zip = null) => new((request, _) =>
        Task.FromResult(request.RequestUri!.Host == "api.github.com" ? Json(release) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip ?? []) }));

    static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(text);
        return memory.ToArray();
    }

    static byte[] Release() => Zip(("WindowManager.exe", "new exe"), ("web/editor.html", "new page"), ("monlie.dll", "new hook"));

    UpdateChecker NewChecker(FakeGitHub github, string? version = "0.1.5", bool debug = false, string? exe = null, List<UpdateApplication>? applied = null, TimeSpan? timeout = null) =>
        new(new HttpClient(github),
            new UpdateSetup(version, exe ?? ExePath, debug, UpdatesDir, new Uri("https://api.github.com/repos/Artyom-kurguzkin/hotkeys-and-windowMax/releases/latest"), timeout ?? TimeSpan.FromSeconds(10)),
            apply => { applied?.Add(apply); return true; });

    [Fact]
    public void Versions_compare_by_number_and_a_prerelease_comes_before_its_release()
    {
        SemVer V(string text) => SemVer.Parse(text)!;
        Assert.True(V("0.1.14").CompareTo(V("0.1.9")) > 0);            // numbers, not text
        Assert.True(V("0.2.0").CompareTo(V("0.1.99")) > 0);
        Assert.True(V("v1.0.0").CompareTo(V("0.9.9")) > 0);
        Assert.True(V("0.1.9").CompareTo(V("0.1.9-g3f2a1b9")) > 0);    // a build between releases is older than the release
        Assert.True(V("0.1.9-g3f2a1b9").CompareTo(V("0.1.8")) > 0);
        Assert.Equal(0, V("0.1.9+3f2a1b9").CompareTo(V("v0.1.9")));   // +commit and the leading v are not part of the version
        Assert.Equal("0.1.9", V("v0.1.9+3f2a1b9").ToString());
        Assert.Equal("0.1.9-g3f2a1b9", V("0.1.9-g3f2a1b9").ToString());
        Assert.Null(SemVer.Parse(null));
        Assert.Null(SemVer.Parse("latest"));
        Assert.Null(SemVer.Parse(""));
    }

    [Fact]
    public async Task A_newer_release_is_downloaded_and_staged_with_the_exe_inside()
    {
        var zip = Release();
        var github = GitHub(ReleaseJson("v0.1.9", zip), zip);
        var launched = new List<UpdateApplication>();
        var checker = NewChecker(github, applied: launched);

        await checker.CheckAsync();

        Assert.Equal(UpdateState.Ready, checker.State);
        Assert.Equal("0.1.9", checker.Latest);
        var ask = github.Requests[0];
        Assert.Equal("https://api.github.com/repos/Artyom-kurguzkin/hotkeys-and-windowMax/releases/latest", ask.RequestUri!.ToString());
        Assert.Equal("WindowManager-updater", ask.Headers.UserAgent.ToString());
        var files = Assert.IsType<string>(checker.Staged);
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(files, "WindowManager.exe")));
        Assert.Equal("new page", File.ReadAllText(Path.Combine(files, "web", "editor.html")));
        Assert.Equal(2, github.Requests.Count);   // the query and the download
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(files)!, "*.zip"));   // the zip is gone once unpacked

        // applying starts the helper with the staged folder, this install, and this exe to restart
        Assert.True(checker.Apply());
        var apply = Assert.Single(launched);
        Assert.Equal(files, apply.Staged);
        Assert.Equal(Install, apply.Install);
        Assert.Equal(ExePath, apply.RestartExe);
        Assert.Equal(Environment.ProcessId, apply.ProcessId);
        Assert.False(checker.Apply());   // once
    }

    [Fact]
    public async Task The_same_or_an_older_release_is_left_alone()
    {
        foreach (var tag in new[] { "v0.1.5", "v0.1.2", "v0.1" })
        {
            var github = GitHub(ReleaseJson(tag, Release()), Release());
            var checker = NewChecker(github);
            await checker.CheckAsync();
            Assert.Equal(UpdateState.UpToDate, checker.State);
            Assert.Null(checker.Staged);
            Assert.Null(checker.MenuLine());
            Assert.Single(github.Requests);   // nothing downloaded
        }
        Assert.False(Directory.Exists(UpdatesDir));
    }

    [Fact]
    public async Task A_debug_or_local_build_never_updates_itself()
    {
        var zip = Release();
        var cases = new (string? Version, bool Debug, string? Exe)[]
        {
            ("0.1.5", true, null),                              // a Debug build
            (null, false, null),                                // no version stamped
            ("0.1.5-g3f2a1b9", false, null),                    // a build between releases
            ("0.1.5", false, Path.Combine(root, "x", "bin", "Debug", "net10.0-windows", "WindowManager.exe")),   // run from a project's build folder
            ("0.1.5", false, Path.Combine(Install, "SomethingElse.exe")),
        };
        foreach (var (version, debug, exe) in cases)
        {
            var github = GitHub(ReleaseJson("v9.9.9", zip), zip);
            var checker = NewChecker(github, version, debug, exe);
            Assert.False(checker.CanUpdate);
            Assert.NotNull(checker.WhyNot);
            await checker.CheckAsync();
            Assert.Equal(UpdateState.Available, checker.State);   // it may ask, it never downloads
            Assert.Null(checker.Staged);
            Assert.Null(checker.MenuLine());
            Assert.False(checker.Apply());
            Assert.Single(github.Requests);
        }
        Assert.False(Directory.Exists(UpdatesDir));
        Assert.True(NewChecker(GitHub("{}"), "0.1.5").CanUpdate);   // and a release copy does
    }

    [Fact]
    public async Task A_failed_or_malformed_response_is_logged_and_nothing_is_staged()
    {
        var logDir = Path.Combine(root, "logs");
        Log.Init(logDir, verbose: false);
        var good = Release();
        var offline = new FakeGitHub((_, _) => throw new HttpRequestException("No such host is known."));
        var busy = new FakeGitHub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var garbage = new FakeGitHub((_, _) => Task.FromResult(Json("<html>not json</html>")));
        var noTag = GitHub("""{"assets":[]}""");
        var noAsset = GitHub(ReleaseJson("v0.1.9", good, assetName: "something-else.zip"), good);
        var notAZip = GitHub(ReleaseJson("v0.1.9"), Encoding.UTF8.GetBytes("this is not a zip"));
        var noExe = GitHub(ReleaseJson("v0.1.9"), Zip(("readme.txt", "no exe here")));
        var badSum = GitHub(ReleaseJson("v0.1.9", [1, 2, 3]), good);   // the published checksum is of other bytes
        var hangs = new FakeGitHub(async (_, cancel) => { await Task.Delay(Timeout.InfiniteTimeSpan, cancel); return new HttpResponseMessage(); });

        foreach (var github in new[] { offline, busy, garbage, noTag, noAsset, notAZip, noExe, badSum, hangs })
        {
            var checker = NewChecker(github, timeout: TimeSpan.FromMilliseconds(150));
            await checker.CheckAsync();   // never throws
            Assert.Equal(UpdateState.Failed, checker.State);
            Assert.NotNull(checker.Failure);
            Assert.Null(checker.Staged);
            Assert.Null(checker.MenuLine());
            Assert.False(checker.Apply());
        }

        var log = File.ReadAllText(Path.Combine(logDir, "wm.log"));
        Assert.Contains("update: could not check: No such host is known.", log);
        Assert.Contains("GitHub answered 403", log);
        Assert.Contains("the release has no usable tag", log);
        Assert.Contains("the release has no WindowManager-win-x64.zip", log);
        Assert.Contains("the download is not a zip file", log);
        Assert.Contains("the release does not hold WindowManager.exe", log);
        Assert.Contains("does not match its published checksum", log);
        Assert.Contains("no answer in", log);
        // a refused download leaves no half-staged folder behind
        Assert.Empty(Directory.Exists(UpdatesDir) ? Directory.GetDirectories(UpdatesDir) : []);
    }

    [Fact]
    public async Task The_menu_line_says_what_the_updater_is_doing()
    {
        var zip = Release();
        var gate = new TaskCompletionSource();
        var github = new FakeGitHub(async (request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Json(ReleaseJson("v0.1.14", zip));
            await gate.Task;   // the download waits here
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        });
        var checker = NewChecker(github);
        Assert.Null(checker.MenuLine());   // nothing checked yet

        var check = checker.CheckAsync();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (checker.State != UpdateState.Downloading && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.Equal(("Update 0.1.14 available — downloading…", false), checker.MenuLine());

        gate.SetResult();
        await check;
        Assert.Equal(("Update 0.1.14 ready", true), checker.MenuLine());

        // a later check that fails does not take the offer away
        await checker.CheckAsync();
        Assert.Equal(("Update 0.1.14 ready", true), checker.MenuLine());
        Assert.True(checker.Apply());
        Assert.Null(checker.MenuLine());   // the helper is started: nothing left to click
    }

    [Fact]
    public async Task The_helper_script_swaps_the_files_and_renames_a_locked_hook_dll_aside()
    {
        var staged = Path.Combine(root, "staged");
        Directory.CreateDirectory(Path.Combine(staged, "web"));
        File.WriteAllText(Path.Combine(staged, "WindowManager.exe"), "new exe");
        File.WriteAllText(Path.Combine(staged, "web", "editor.html"), "new page");
        File.WriteAllText(Path.Combine(staged, "monlie.dll"), "new hook");

        Directory.CreateDirectory(Path.Combine(Install, "web"));
        File.WriteAllText(Path.Combine(Install, "WindowManager.exe"), "old exe");
        File.WriteAllText(Path.Combine(Install, "web", "editor.html"), "old page");
        File.WriteAllText(Path.Combine(Install, "web", "kept.txt"), "not in the release");
        File.WriteAllText(Path.Combine(Install, "monlie.dll"), "old hook");
        File.WriteAllText(Path.Combine(Install, "monlie.1.old"), "an earlier leftover, unlocked now");

        // a process that has already ended stands in for the manager
        using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        gone.WaitForExit();

        var backup = Path.Combine(root, "previous");
        var log = Path.Combine(root, "helper", "apply.log");
        // a hook dll another process still maps can be renamed and read, not overwritten: FileShare.Delete stands in for that
        using (var held = new FileStream(Path.Combine(Install, "monlie.dll"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            using var helper = UpdateHelper.Launch(new UpdateApplication(staged, Install, backup, gone.Id, RestartExe: null), log, retries: 1);
            Assert.NotNull(helper);
            Assert.True(await Task.Run(() => helper.WaitForExit(60_000)), "the helper did not finish");
            Assert.True(helper.ExitCode == 0, File.Exists(log) ? File.ReadAllText(log) : "no helper log");
        }

        Assert.Equal("new exe", File.ReadAllText(Path.Combine(Install, "WindowManager.exe")));
        Assert.Equal("new page", File.ReadAllText(Path.Combine(Install, "web", "editor.html")));
        Assert.Equal("not in the release", File.ReadAllText(Path.Combine(Install, "web", "kept.txt")));   // files the release does not hold stay
        Assert.Equal("new hook", File.ReadAllText(Path.Combine(Install, "monlie.dll")));
        var moved = Assert.Single(Directory.GetFiles(Install, "monlie.*.old"));   // the unlocked leftover was swept, the locked one renamed aside
        Assert.NotEqual("monlie.1.old", Path.GetFileName(moved));
        Assert.Equal("old hook", File.ReadAllText(moved, Encoding.UTF8));
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(backup, "WindowManager.exe")));   // one rollback copy
        Assert.Contains("Updated", File.ReadAllText(log));
    }
}
