using System.Text.Json;
using static WindowManager.Native;

namespace WindowManager;

// What this instance changed on other apps' windows, persisted so a successor can undo it if this instance is
// killed or crashes (a killed process can't run its own restore). Deleted again after a clean shutdown.
public static class State
{
    // Slot = the rect Windows had given a cropped window before we stretched it (null if not cropped).
    // Backdrop = its system backdrop type before we turned it off for the crop (null if untouched).
    public record Entry(long Hwnd, uint OriginalStyle, int[]? Slot, uint? Backdrop = null);

    public static string DefaultPath => Path.Combine(Log.DefaultDir, "state.json");

    public static void Save(string path, IEnumerable<Entry> entries)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(entries.ToArray())); }
        catch (IOException e) { Log.Info($"state not saved: {e.Message}"); }
    }

    public static Entry[] Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(path)) ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException) { Log.Info($"state unreadable, skipped: {e.Message}"); return []; }
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    public static int[] ToArray(Rect r) => [r.Left, r.Top, r.Right, r.Bottom];

    // Undo a dead instance's changes: drop the clip, put cropped windows back in their slot, give the caption back.
    public static int Recover(IEnumerable<Entry> entries)
    {
        var n = 0;
        foreach (var e in entries)
        {
            var hwnd = (IntPtr)e.Hwnd;
            if (!IsWindow(hwnd)) continue;
            Frames.SetRegion(hwnd, null);
            if (e.Slot is [var l, var t, var r, var b])
            {
                Frames.MoveUnclamped(hwnd, new Rect(l, t, r, b));
                Frames.SetFrameRendering(hwnd, true);
            }
            if (e.Backdrop is { } bd) Frames.SetBackdrop(hwnd, bd);
            Frames.Unstrip(hwnd, e.OriginalStyle);
            n++;
        }
        return n;
    }
}
