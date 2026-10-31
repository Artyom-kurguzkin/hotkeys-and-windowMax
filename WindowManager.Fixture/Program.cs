using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WmFixture;

// Usage: WmFixture.exe --name A --mode std --x 100 --y 100 --w 800 --h 600
//
// Modes (how the app does its own fullscreen on F11):
//   std        reads the monitor with GetMonitorInfo ONCE, at F11 time, and ignores later monitor changes (Chromium / Electron)
//   redisplay  like std, but re-reads the monitor on WM_DISPLAYCHANGE and resizes in place
//   nof11      ignores F11 entirely (an app with no fullscreen)
//   metrics    sizes fullscreen from GetSystemMetrics, which the monitor lie does not touch (an app that ignores the lie)
//   slow       std, but the UI thread is busy for 350ms handling F11
//   late       std, but applies the fullscreen 250ms after F11
//   anim       std, but glides to the target in six steps over ~150ms
//
// The title is the report channel: "WmFixture:NAME fs=0|1 f11=N clicks=N keys=N".
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        string Arg(string n, string d) { var i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        Application.Run(new Fixture(Arg("--name", "A"), Arg("--mode", "std"),
            new Rectangle(int.Parse(Arg("--x", "100")), int.Parse(Arg("--y", "100")), int.Parse(Arg("--w", "800")), int.Parse(Arg("--h", "600")))));
    }
}

sealed class Fixture : Form
{
    const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_DISPLAYCHANGE = 0x7E, WM_APP_COMMAND = 0x8001, VK_F11 = 0x7A;

    readonly string name, mode;
    bool fs, wasMaximized;
    Rectangle savedBounds;
    nint savedStyle;
    int f11, clicks, keys;
    readonly System.Windows.Forms.Timer timer = new();
    Action? tick;

    public Fixture(string name, string mode, Rectangle at)
    {
        this.name = name; this.mode = mode;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = at;
        var y = 12;
        foreach (var label in new[] { "Apply", "Cancel", "Help" })
        {
            var b = new Button { Text = label, Location = new Point(12, y), Size = new Size(110, 32) };
            b.Click += (_, _) => { clicks++; Title(); };
            Controls.Add(b);
            y += 44;
        }
        Controls.Add(new TextBox { Location = new Point(140, 14), Width = 220 });
        Controls.Add(new CheckBox { Text = "Remember", Location = new Point(140, 56), AutoSize = true });
        timer.Tick += (_, _) => tick?.Invoke();
        Title();
    }

    void Title() => Text = $"WmFixture:{name} fs={(fs ? 1 : 0)} f11={f11} clicks={clicks} keys={keys}";

    // The monitor the way a real app asks for it. The monitor lie hooks GetMonitorInfoW in this process.
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public int l, t, r, b, wl, wt, wr, wb; public uint flags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(IntPtr h, int index, nint value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    const int GWL_STYLE = -16;
    const nint WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
    const uint SWP_FRAMECHANGED = 0x20, SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    // What Chromium does for fullscreen: take off the title bar and sizing frame, keep the rest of the style (the min/max boxes and system menu stay).
    void SetStyle(nint style)
    {
        SetWindowLongPtr(Handle, GWL_STYLE, style);
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    Rectangle MonitorBounds()
    {
        if (mode == "metrics") return new Rectangle(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(Handle, 2), ref mi);
        return Rectangle.FromLTRB(mi.l, mi.t, mi.r, mi.b);
    }

    bool RestoreWasMaximized() => false;   // WinForms does not say; a minimized form comes back normal

    void EnterFs()
    {
        wasMaximized = WindowState == FormWindowState.Maximized;
        var wasMinimized = WindowState == FormWindowState.Minimized;
        savedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;   // a minimized form's Bounds is its taskbar thumbnail, not its size
        if (wasMaximized || wasMinimized) { wasMaximized = wasMaximized || RestoreWasMaximized(); WindowState = FormWindowState.Normal; }
        savedStyle = GetWindowLongPtr(Handle, GWL_STYLE);
        SetStyle(savedStyle & ~(WS_CAPTION | WS_THICKFRAME));
        var target = MonitorBounds();
        fs = true;
        if (mode == "anim") Glide(Bounds, target); else Bounds = target;
        Title();
    }

    void LeaveFs()
    {
        SetStyle(savedStyle);
        if (wasMaximized) WindowState = FormWindowState.Maximized; else Bounds = savedBounds;
        fs = false;
        Title();
    }

    void Glide(Rectangle from, Rectangle to)
    {
        var step = 0;
        timer.Interval = 25;
        tick = () =>
        {
            step++;
            var t = Math.Min(1.0, step / 6.0);
            Bounds = Rectangle.FromLTRB(Lerp(from.Left, to.Left, t), Lerp(from.Top, to.Top, t), Lerp(from.Right, to.Right, t), Lerp(from.Bottom, to.Bottom, t));
            if (step >= 6) { timer.Stop(); tick = null; }
        };
        timer.Start();
    }
    static int Lerp(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);

    void ToggleFs()
    {
        f11++;
        if (WindowState == FormWindowState.Minimized) { Title(); return; }   // nobody can press F11 in a minimized app
        if (mode == "nof11") { Title(); return; }
        void Apply() { if (fs) LeaveFs(); else EnterFs(); }
        switch (mode)
        {
            case "slow": Thread.Sleep(350); Apply(); break;
            case "late":
                timer.Interval = 250;
                tick = () => { timer.Stop(); tick = null; Apply(); };
                timer.Start();
                break;
            default: Apply(); break;
        }
        Title();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_KEYDOWN or WM_SYSKEYDOWN:
                if ((int)m.WParam == VK_F11) { ToggleFs(); return; }
                keys++; Title();
                break;
            case WM_DISPLAYCHANGE when mode == "redisplay" && fs:
                Bounds = MonitorBounds();
                break;
            case WM_APP_COMMAND:
                if ((int)m.WParam == 1) Thread.Sleep((int)m.LParam);          // a hung UI thread
                else if ((int)m.WParam == 2) Environment.Exit(1);              // a crash
                return;
        }
        base.WndProc(ref m);
    }
}
