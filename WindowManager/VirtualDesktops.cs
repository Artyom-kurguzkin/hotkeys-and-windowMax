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
}
