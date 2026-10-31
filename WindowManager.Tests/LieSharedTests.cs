using System.Runtime.InteropServices;

namespace WindowManager.Tests;

// The shared-memory table the injected DLL reads (monlie.c) and the manager writes (MonitorLie.cs) must agree byte for byte.
// These tests read the mapping back raw, at the offsets the C struct dictates, so a drift on the C# side fails here instead of
// as "lie missed" on the user's machine. (The C side pins its layout with _Static_assert.)
public class LieSharedTests
{
    const uint Pid = 0x7E57_0001; // a mapping name nothing real uses

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint hi, uint lo, UIntPtr size);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static byte[] Raw()
    {
        var map = OpenFileMapping(4 /*FILE_MAP_READ*/, false, $"Local\\monlie_{Pid}");
        Assert.NotEqual(IntPtr.Zero, map);
        var view = MapViewOfFile(map, 4, 0, 0, (UIntPtr)MonitorLie.ShmSize);
        var bytes = new byte[MonitorLie.ShmSize];
        Marshal.Copy(view, bytes, 0, bytes.Length);
        UnmapViewOfFile(view); CloseHandle(map);
        return bytes;
    }

    static (long Mon, int Enabled, int L, int T, int R, int B) Entry(byte[] raw, int i)
    {
        var o = i * MonitorLie.EntrySize;
        return (BitConverter.ToInt64(raw, o), BitConverter.ToInt32(raw, o + 8),
            BitConverter.ToInt32(raw, o + 16), BitConverter.ToInt32(raw, o + 20), BitConverter.ToInt32(raw, o + 24), BitConverter.ToInt32(raw, o + 28));
    }

    [Fact]
    public void Layout_matches_the_dll_struct()
    {
        Assert.Equal(32, MonitorLie.EntrySize);
        Assert.Equal(256, MonitorLie.ShmSize);
    }

    [Fact]
    public void Two_monitors_of_one_process_get_their_own_slots_and_are_disabled_one_at_a_time()
    {
        var m1 = (IntPtr)0x10001; var m2 = (IntPtr)0x20002;
        MonitorLie.SetSlot(Pid, m1, new Rect(0, 0, 1920, 1200));
        MonitorLie.SetSlot(Pid, m2, new Rect(940, -2172, 4780, -12));
        var raw = Raw();
        Assert.Equal((0x10001L, 1, 0, 0, 1920, 1200), Entry(raw, 0));
        Assert.Equal((0x20002L, 1, 940, -2172, 4780, -12), Entry(raw, 1));

        MonitorLie.SetSlot(Pid, m1, new Rect(0, 0, 960, 1200)); // re-slot: same entry, new rect
        raw = Raw();
        Assert.Equal((0x10001L, 1, 0, 0, 960, 1200), Entry(raw, 0));
        Assert.Equal((0x20002L, 1, 940, -2172, 4780, -12), Entry(raw, 1));

        MonitorLie.Disable(Pid, m1);
        raw = Raw();
        Assert.Equal(0, Entry(raw, 0).Enabled);
        Assert.Equal(1, Entry(raw, 1).Enabled);
        Assert.Equal(2, MonitorLie.Entries(Pid).Length);

        MonitorLie.DisableAll(Pid);
        Assert.All(MonitorLie.Entries(Pid), e => Assert.False(e.Enabled));
    }
}
