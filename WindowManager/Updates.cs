using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowManager;

public enum UpdateState { NotChecked, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <summary>
/// What the update check needs to know about this run. <paramref name="Version"/> is the app's own version (null = unknown), <paramref name="ExePath"/> the exe that is running,
/// <paramref name="IsDebug"/> a Debug build, <paramref name="UpdatesFolder"/> where releases are staged, <paramref name="Endpoint"/> the "latest release" address and <paramref name="Timeout"/> how long one request may take.
/// </summary>
public sealed record UpdateSetup(string? Version, string ExePath, bool IsDebug, string UpdatesFolder, Uri Endpoint, TimeSpan Timeout)
{
    public const string Repository = "Artyom-kurguzkin/hotkeys-and-windowMax";
    public const string AssetName = "WindowManager-win-x64.zip";
    public const string ExeName = "WindowManager.exe";

    /// <summary>WM_UPDATE_URL points the check at another address (a demo or a test against a local fake); otherwise the repository's latest release on GitHub.</summary>
    public static Uri DefaultEndpoint => Uri.TryCreate(Environment.GetEnvironmentVariable("WM_UPDATE_URL"), UriKind.Absolute, out var own) ? own : new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>This run: the version the build stamped on the assembly (AssemblyInformationalVersion), the running exe, the configuration, and the updates folder beside the log (so sandbox mode's redirect applies).</summary>
    public static UpdateSetup ForThisRun()
    {
        // AppVersion.Display, not the raw attribute: the attribute is 0.1.x+commit on every branch; Display marks a build off main as a
        // prerelease (0.1.x-g<commit>), which never updates itself.
        var version = AppVersion.Informational is null ? null : AppVersion.Display;
#if DEBUG
        const bool debug = true;
#else
        const bool debug = false;
#endif
        return new(version, Environment.ProcessPath ?? "", debug, Path.Combine(Log.DefaultDir, "updates"), DefaultEndpoint, TimeSpan.FromSeconds(10));
    }
}

/// <summary>A version as the check compares it: the numbers, and the prerelease suffix (a build between releases has one: 0.1.5-g3f2a1b9 comes before 0.1.5).</summary>
public sealed record SemVer(Version Numbers, string? Prerelease) : IComparable<SemVer>
{
    static readonly Regex Shape = new(@"^v?(?<n>\d+(\.\d+){0,3})(-(?<pre>[0-9A-Za-z.\-]+))?(\+.*)?$", RegexOptions.CultureInvariant);

    public static SemVer? Parse(string? text)
    {
        if (text is null || Shape.Match(text.Trim()) is not { Success: true } m) return null;
        var parts = m.Groups["n"].Value.Split('.');
        var n = new int[4];
        for (var i = 0; i < parts.Length; i++) if (!int.TryParse(parts[i], out n[i])) return null;
        return new(new Version(n[0], n[1], n[2], n[3]), m.Groups["pre"].Success ? m.Groups["pre"].Value : null);
    }

    /// <summary>The version as shown: no leading v, no +commit.</summary>
    public override string ToString()
    {
        var numbers = Numbers.Revision > 0 ? Numbers.ToString(4) : Numbers.ToString(3);
        return Prerelease is null ? numbers : numbers + "-" + Prerelease;
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        if (Numbers.CompareTo(other.Numbers) is var c and not 0) return c;
        if (Prerelease is null || other.Prerelease is null) return (Prerelease is null ? 1 : 0) - (other.Prerelease is null ? 1 : 0); // the release is after its own prereleases
        return string.CompareOrdinal(Prerelease, other.Prerelease);
    }
}

/// <summary>
/// Asks GitHub for the repository's latest release; when it is newer and this copy is a public release build, downloads the zip, checks it (the published checksum, and that it holds WindowManager.exe),
/// stages it under the updates folder and waits for <see cref="Apply"/>. Nothing here throws: a check that cannot be made is a state and a line in the log.
/// </summary>
public sealed class UpdateChecker
{
    readonly HttpClient http;
    readonly UpdateSetup setup;
    readonly Func<UpdateApplication, bool> launch;
    readonly SemaphoreSlim busy = new(1, 1);
    readonly SemVer? own;
    string? staged, stagedVersion;
    bool applied;

    public UpdateChecker(HttpClient http, UpdateSetup setup, Func<UpdateApplication, bool>? launch = null)
    {
        (this.http, this.setup) = (http, setup);
        this.launch = launch ?? UpdateHelper.Apply;
        own = SemVer.Parse(setup.Version);
        WhyNot = Decide(own, setup);
        Log.Info($"update: version {Current}; " + (WhyNot is null ? "this copy updates itself" : "it does not update itself: " + WhyNot));
    }

    public UpdateState State { get; private set; }
    /// <summary>The newest release found (without its v), or null before a check finds one.</summary>
    public string? Latest { get; private set; }
    public string? Failure { get; private set; }
    public string Current => own?.ToString() ?? "unknown";
    public bool CanUpdate => WhyNot is null;
    /// <summary>Why this copy never updates itself, or null when it does.</summary>
    public string? WhyNot { get; }
    /// <summary>The folder with the staged release, or null before one is downloaded.</summary>
    public string? Staged => staged;

    /// <summary>A copy that updates itself is a release one: not Debug, a public version (no prerelease suffix), WindowManager.exe, and not under a project's bin/ or obj/.</summary>
    static string? Decide(SemVer? version, UpdateSetup setup)
    {
        if (setup.IsDebug) return "this is a Debug build";
        if (version is null) return "its version is unknown";
        if (version.Prerelease is not null) return "its version is not a public release";
        if (!string.Equals(Path.GetFileName(setup.ExePath), UpdateSetup.ExeName, StringComparison.OrdinalIgnoreCase)) return "it is not running from " + UpdateSetup.ExeName;
        if (Regex.IsMatch(setup.ExePath, @"[\\/](bin[\\/](Debug|Release)|obj)[\\/]", RegexOptions.IgnoreCase)) return "it runs from a project's build folder, not a release folder";
        return null;
    }

    /// <summary>What the hotkey menu shows about the updater: null when there is nothing to say, a line while a release downloads, and a clickable one once it is staged.</summary>
    public (string Text, bool CanApply)? MenuLine()
    {
        if (staged is not null && !applied) return ($"Update {stagedVersion} ready", true);
        if (State == UpdateState.Downloading) return ($"Update {Latest} available — downloading…", false);
        return null;
    }

    // A staged release stays offered: a later check that fails or finds nothing newer must not take the line away.
    void Set(UpdateState state, string? why = null)
    {
        if (staged is not null && state is UpdateState.Checking or UpdateState.UpToDate or UpdateState.Failed) return;
        (State, Failure) = (state, why);
    }

    /// <summary>Checks for a newer release and, when this copy updates itself, downloads and stages it. A second call while one runs returns at once.</summary>
    public async Task CheckAsync(CancellationToken cancel = default)
    {
        if (!busy.Wait(0)) return;
        try { await CheckAndStageAsync(cancel); }
        catch (Exception e)
        {
            Log.Info($"update: the check failed unexpectedly: {e}");
            Set(UpdateState.Failed, e.Message);
        }
        finally { busy.Release(); }
    }

    async Task CheckAndStageAsync(CancellationToken cancel)
    {
        Set(UpdateState.Checking);
        Log.Info($"update: checking {setup.Endpoint} for a release newer than {Current}");
        JsonElement release;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(setup.Timeout);
            using var ask = new HttpRequestMessage(HttpMethod.Get, setup.Endpoint);
            ask.Headers.UserAgent.ParseAdd("WindowManager-updater");
            ask.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var answer = await http.SendAsync(ask, limit.Token);
            if (!answer.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)answer.StatusCode} {answer.ReasonPhrase}");
            release = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(limit.Token)).RootElement.Clone();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            var why = e is OperationCanceledException ? $"no answer in {setup.Timeout.TotalSeconds:0.#} seconds" : e.Message;
            Log.Info($"update: could not check: {why}");
            Set(UpdateState.Failed, why);
            return;
        }

        var latest = SemVer.Parse(release.ValueKind == JsonValueKind.Object && release.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() : null);
        if (latest is null) { Log.Info("update: could not check: the release has no usable tag"); Set(UpdateState.Failed, "the latest release has no version tag"); return; }
        Latest = latest.ToString();
        if (own is not null && latest.CompareTo(own) <= 0)
        {
            Log.Info($"update: up to date: running {Current}, the latest release is {Latest}");
            Set(UpdateState.UpToDate);
            return;
        }
        Log.Info($"update: a newer release is available: {Latest} (running {Current})");
        if (!CanUpdate) { Log.Info("update: not updating: " + WhyNot); Set(UpdateState.Available); return; }

        Set(UpdateState.Downloading);
        try { (staged, stagedVersion, applied) = (await StageAsync(release, Latest, cancel), Latest, false); }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            Log.Info($"update: could not download {Latest}: {e.Message}");
            Set(staged is null ? UpdateState.Failed : UpdateState.Ready, e.Message);
            return;
        }
        Set(UpdateState.Ready);
    }

    /// <summary>Downloads the release's zip into updates/v{version}/, checks and unpacks it into files/ there. A folder already unpacked and checked is reused.</summary>
    async Task<string> StageAsync(JsonElement release, string version, CancellationToken cancel)
    {
        string folder = Path.Combine(setup.UpdatesFolder, "v" + version), files = Path.Combine(folder, "files"), ready = Path.Combine(folder, "ready");
        if (File.Exists(ready) && File.Exists(Path.Combine(files, UpdateSetup.ExeName))) { Log.Info($"update: {version} is already downloaded in {folder}"); return files; }

        var asset = release.TryGetProperty("assets", out var all) && all.ValueKind == JsonValueKind.Array
            ? all.EnumerateArray().FirstOrDefault(a => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out var n) && n.GetString() == UpdateSetup.AssetName) : default;
        if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("browser_download_url", out var url) || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var address))
            throw new InvalidOperationException($"the release has no {UpdateSetup.AssetName}");

        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            var zip = Path.Combine(folder, UpdateSetup.AssetName);
            Log.Info($"update: downloading {address} to {zip}");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromMinutes(10));
            using (var ask = new HttpRequestMessage(HttpMethod.Get, address))
            {
                ask.Headers.UserAgent.ParseAdd("WindowManager-updater");
                using var answer = await http.SendAsync(ask, HttpCompletionOption.ResponseHeadersRead, limit.Token);
                if (!answer.IsSuccessStatusCode) throw new HttpRequestException($"the download answered {(int)answer.StatusCode} {answer.ReasonPhrase}");
                await using var target = File.Create(zip);
                await answer.Content.CopyToAsync(target, limit.Token);
            }

            if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String && digest.GetString() is { } published && published.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                await using var read = File.OpenRead(zip);
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, limit.Token));
                if (!string.Equals(actual, published[7..], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("the download does not match its published checksum");
                Log.Info("update: the download matches its published checksum");
            }
            try { ZipFile.ExtractToDirectory(zip, files); }
            catch (InvalidDataException) { throw new InvalidDataException("the download is not a zip file"); }
            if (!File.Exists(Path.Combine(files, UpdateSetup.ExeName))) throw new InvalidDataException($"the release does not hold {UpdateSetup.ExeName}");
            File.WriteAllText(ready, version);
            File.Delete(zip);
            foreach (var old in Directory.GetDirectories(setup.UpdatesFolder, "v*").Where(d => d != folder)) TryDelete(old); // earlier downloads
            Log.Info($"update: {version} is downloaded, checked and unpacked in {files}");
            return files;
        }
        catch
        {
            TryDelete(folder); // a refused download leaves nothing behind
            throw;
        }
    }

    static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Info($"update: could not remove {folder}: {e.Message}"); }
    }

    /// <summary>Swaps the staged files in and starts the new exe. True when it did (the caller then quits); false when nothing is staged or the swap failed.</summary>
    public bool Apply()
    {
        if (staged is null || applied) return false;
        applied = launch(new UpdateApplication(staged, Path.GetDirectoryName(setup.ExePath)!, setup.ExePath));
        Log.Info(applied ? $"update: installing {stagedVersion}" : $"update: could not install {stagedVersion}");
        return applied;
    }
}

/// <summary>
/// The update as the rest of the app sees it: <see cref="Start"/> from startup, <see cref="MenuLine"/> and <see cref="ApplyNow"/> for the hotkey menu. The check runs a minute after start and then
/// every 24 hours, off every thread the app's loop uses.
/// </summary>
public static class Updates
{
    public static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(60), Every = TimeSpan.FromHours(24);
    static UpdateChecker? checker;
    static Action? quit;

    /// <summary>Begins the schedule. <paramref name="quitCleanly"/> must end the app the way Ctrl+Alt+Shift+Q does and may be called from any thread.</summary>
    public static void Start(Action quitCleanly)
    {
        if (checker is not null) return;
        quit = quitCleanly;
        try
        {
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // each request has its own limit
            checker = new UpdateChecker(client, UpdateSetup.ForThisRun());
            if (!checker.CanUpdate) return;
            UpdateHelper.Sweep(Path.GetDirectoryName(Environment.ProcessPath!)!); // the files the last update renamed aside
            var run = checker;
            _ = Task.Run(async () =>
            {
                await Task.Delay(FirstCheck);
                while (true)
                {
                    await run.CheckAsync();
                    await Task.Delay(Every);
                }
            });
        }
        catch (Exception e) { Log.Info($"update: could not start: {e.Message}"); }
    }

    public static (string Text, bool CanApply)? MenuLine() => checker?.MenuLine();

    /// <summary>The menu's Update click: swap the files in and start the new exe, then quit cleanly so the windows are restored.</summary>
    public static void ApplyNow()
    {
        try { if (checker?.Apply() == true) quit?.Invoke(); }
        catch (Exception e) { Log.Info($"update: apply failed: {e.Message}"); }
    }
}
