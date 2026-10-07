using System.Runtime.InteropServices;
using System.Text;

namespace WindowManager;

public static class Log
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public static bool Verbose { get; private set; }
    static string? path;
    static readonly object gate = new();

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowManager");

    public static void Init(string dir, bool verbose)
    {
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "wm.log");
        var info = new FileInfo(path);
        try { if (info.Exists && info.Length > MaxBytes) File.Move(path, path + ".old", overwrite: true); }
        catch (IOException) { } // ponytail: another instance is mid-write; rotates on the next start
        Verbose = verbose;
    }

    // Open-append-close per line: a replacing instance and the old one can both log without clobbering lines.
    // ponytail: one open per line; fine at human input rates, buffer it if --verbose ever gets too slow.
    public static void Info(string msg)
    {
        if (path is null) return;
        var line = Encoding.UTF8.GetBytes($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}:{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}");
        lock (gate)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.Write(line);
            }
            catch (IOException) { } // never let logging take down the hook
        }
    }

    public static void Debug(string msg) { if (Verbose) Info(msg); }

    // Call right after a failed P/Invoke declared with SetLastError = true.
    public static void Win32(string what) => Info($"WIN32 FAIL {what}: error {Marshal.GetLastWin32Error()}");
}
