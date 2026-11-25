using System.Reflection;
using System.Text.RegularExpressions;

namespace WindowManager;

/// <summary>
/// This build's version, as Nerdbank.GitVersioning stamped it (version.json: 0.1 plus the git height). AssemblyInformationalVersion is
/// "0.1.12+1a2b3c4d5e" on every branch (only NuGet's package version gets the "-g" suffix off main), so the build also stamps the assembly metadata
/// "PublicRelease" (True on main, False elsewhere; WindowManager.csproj) and that decides what is a release.
/// </summary>
public static class AppVersion
{
    /// <summary>The raw AssemblyInformationalVersion, null when the build stamped none.</summary>
    public static string? Informational { get; } = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

#if DEBUG
    const bool DebugBuild = true;
#else
    const bool DebugBuild = false;
#endif

    // declared before "current": static fields initialise in this order, and Parse uses it
    static readonly Regex Shape = new(@"^v?(?<n>\d+(\.\d+){1,3})(-(?<pre>[0-9A-Za-z.\-]+))?(\+(?<commit>[0-9A-Za-z]+))?$", RegexOptions.CultureInvariant);

    static readonly (string Display, bool IsPublicRelease) current = Parse(Informational, DebugBuild,
        nonPublicBuild: typeof(AppVersion).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "PublicRelease")?.Value == "False");

    /// <summary>What the menu shows: the version without the "+commit" ("-g&lt;commit&gt;" for a build off main), "dev" when it is unknown.</summary>
    public static string Display => current.Display;

    /// <summary>A Release build of a public version (built from main, no prerelease suffix): the only kind that may update itself.</summary>
    public static bool IsPublicRelease => current.IsPublicRelease;

    /// <summary>The pure part: the shown text and whether it is a public release, from the raw attribute, whether this is a Debug build and whether the build was not from main.</summary>
    public static (string Display, bool IsPublicRelease) Parse(string? informational, bool debugBuild, bool nonPublicBuild = false)
    {
        if (informational is null || Shape.Match(informational.Trim()) is not { Success: true } m) return ("dev", false);
        var pre = m.Groups["pre"].Success ? m.Groups["pre"].Value : null;
        if (pre is null && nonPublicBuild && m.Groups["commit"].Success) pre = "g" + m.Groups["commit"].Value[..Math.Min(7, m.Groups["commit"].Value.Length)];
        return (m.Groups["n"].Value + (pre is null ? "" : "-" + pre), !debugBuild && pre is null);
    }
}
