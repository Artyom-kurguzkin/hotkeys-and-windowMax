using System.Runtime.InteropServices;

namespace WindowManager;

// Which virtual desktop a window is on. Giving the focus to a window on ANOTHER desktop makes Windows switch to that desktop, so a
// fullscreen operation still running when the user moved on would drag them back (seen three times in the real log: "op done:
// re-asserting foreground on X" within a few hundred ms of a desktop-switch shortcut). Every step that grabs the focus or types F11 asks first.
static class VirtualDesktops
{
    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    static readonly Guid ClassId = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");

    [ThreadStatic] static IVirtualDesktopManager? manager;   // one per thread: the loop is STA, the workers are MTA

    // Unknown counts as "yes": a failure of this check must never stop the manager from doing its job.
    public static bool IsOnCurrent(IntPtr hwnd)
    {
        try
        {
            manager ??= (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(ClassId)!)!;
            return manager.IsWindowOnCurrentVirtualDesktop(hwnd, out var on) != 0 || on;
        }
        catch { return true; }
    }

    // ---- switching ----
    // PgUp/PgDn used to inject Ctrl+Win+Left/Right. An app with its own low-level keyboard hook (re-installed after a wake, so
    // ahead of ours) ate the injected chord: the log showed "action DesktopRight" and no switch. Ask the shell directly instead.
    // ponytail: undocumented shell interface, GUID + vtable for Windows 11 24H2+ (build 26100+). On another build QueryService
    // fails and the caller falls back to the keystroke; update the GUID + vtable if a future build breaks it.
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr obj);
    }

    [ComImport, Guid("53F5CA0B-158F-4124-900C-057158060B27"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManagerInternal
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int MoveViewToDesktop(IntPtr view, IntPtr desktop);
        [PreserveSig] int CanViewMoveDesktops(IntPtr view, out int can);
        [PreserveSig] int GetCurrentDesktop(out IntPtr desktop);
        [PreserveSig] int GetDesktops(out IntPtr desktops);
        [PreserveSig] int GetAdjacentDesktop(IntPtr from, int direction, out IntPtr desktop); // 3 = left, 4 = right
        [PreserveSig] int SwitchDesktop(IntPtr desktop);
    }

    static readonly Guid ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    static readonly Guid ManagerInternalService = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");

    // True if the shell handled it (including "no desktop that way"). False = caller types the keystroke.
    // Fetched fresh each call: an Explorer restart would leave a cached proxy dead, and a switch is rare.
    public static bool Switch(bool right)
    {
        IntPtr unk = IntPtr.Zero, cur = IntPtr.Zero, next = IntPtr.Zero;
        try
        {
            var sp = (IServiceProvider)Activator.CreateInstance(Type.GetTypeFromCLSID(ImmersiveShell)!)!;
            var service = ManagerInternalService; var iid = typeof(IVirtualDesktopManagerInternal).GUID;
            if (sp.QueryService(ref service, ref iid, out unk) != 0 || unk == IntPtr.Zero) { Log.Info("virtual desktop service unavailable, using the keystroke"); return false; }
            var m = (IVirtualDesktopManagerInternal)Marshal.GetObjectForIUnknown(unk);
            if (m.GetCurrentDesktop(out cur) != 0) return false;
            if (m.GetAdjacentDesktop(cur, right ? 4 : 3, out next) != 0) return true; // already the first/last desktop
            return m.SwitchDesktop(next) == 0;
        }
        catch (Exception e) { Log.Info($"virtual desktop switch failed, using the keystroke: {e.Message}"); return false; }
        finally { foreach (var p in new[] { unk, cur, next }) if (p != IntPtr.Zero) Marshal.Release(p); }
    }
}
