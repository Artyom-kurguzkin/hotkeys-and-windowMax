using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowManager;

// The hotkey list (both Alt keys): a native ListView table, grouped and sorted, following the Windows light/dark
// app theme (dark title bar, dark list/header/scrollbars via the system's DarkMode_* control themes).
public sealed class HelpWindow : Form
{
    static int open; // one at a time
    static volatile HelpWindow? current;

    public static bool IsOpen => Volatile.Read(ref open) == 1;
    public static bool IsShowing => current is { IsHandleCreated: true, Visible: true };

    // Both Alts: open the list, or close it if it is already showing. Returns true if it opened.
    public static bool Toggle(IntPtr anchor)
    {
        if (Interlocked.Exchange(ref open, 1) == 1)
        {
            // Already open (or still opening, then current is null and the press is ignored).
            if (current is { IsHandleCreated: true } c) c.BeginInvoke(c.Close); // Close on its own UI thread
            return false;
        }
        var t = new Thread(() =>
        {
            try
            {
                EnableStyles();
                var form = new HelpWindow(anchor);
                current = form;
                Application.Run(form);
            }
            catch (Exception e) { Log.Info($"hotkey list error: {e}"); }
            finally { current = null; Interlocked.Exchange(ref open, 0); }
        }) { IsBackground = true, Name = "hotkey-list" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return true;
    }

    public static void ShowAndWait()
    {
        EnableStyles();
        Application.Run(new HelpWindow(IntPtr.Zero));
    }

    static int stylesEnabled;
    // Modern control styles (comctl32 v6): without them the ListView has no groups and an unthemed header.
    static void EnableStyles()
    {
        if (Interlocked.Exchange(ref stylesEnabled, 1) == 1) return;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
    }

    readonly IntPtr anchor;
    readonly bool dark = IsDarkMode();
    readonly ListView list = new();

    HelpWindow(IntPtr anchor)
    {
        this.anchor = anchor;
        Text = "WindowManager hotkeys";
        Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        // A tool window: slim themed title bar, and the window manager deliberately leaves tool windows unstripped.
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        list.BorderStyle = BorderStyle.None;
        list.Columns.Add("Shortcut");
        list.Columns.Add("Action");
        foreach (var category in KeyEngine.HelpCategories) list.Groups.Add(new ListViewGroup(category, category));
        foreach (var (category, keys, what) in KeyEngine.SortedHelp())
            list.Items.Add(new ListViewItem([keys, what], list.Groups[category]));
        Controls.Add(list);

        var back = dark ? Color.FromArgb(32, 32, 32) : SystemColors.Window;
        var fore = dark ? Color.FromArgb(240, 240, 240) : SystemColors.WindowText;
        BackColor = list.BackColor = back;
        ForeColor = list.ForeColor = fore;

        // The dark header theme draws its labels in a dim grey; draw just the header, rows stay native.
        var headerBack = dark ? Color.FromArgb(43, 43, 43) : SystemColors.Control;
        var headerFont = new Font(Font, FontStyle.Bold);
        list.OwnerDraw = true;
        list.DrawItem += (_, _) => { }; // cells are drawn per sub-item below; skipping the row draw drops the focus box
        list.DrawSubItem += (_, e) =>
        {
            // Plain cells: themed colours, no selection or focus box (nothing to select in this list).
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
            var text = e.Bounds with { X = e.Bounds.X + LogicalToDeviceUnits(6), Width = e.Bounds.Width - LogicalToDeviceUnits(6) };
            TextRenderer.DrawText(e.Graphics, e.SubItem!.Text, list.Font, text, fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
        list.DrawColumnHeader += (_, e) =>
        {
            using (var b = new SolidBrush(headerBack)) e.Graphics.FillRectangle(b, e.Bounds);
            var text = e.Bounds with { X = e.Bounds.X + LogicalToDeviceUnits(6) };
            TextRenderer.DrawText(e.Graphics, e.Header!.Text, headerFont, text, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var on = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref on, sizeof(int));
        var theme = dark ? "DarkMode_ItemsView" : "ItemsView";
        SetWindowTheme(list.Handle, theme, null);
        SetWindowTheme(SendMessage(list.Handle, 0x101F /*LVM_GETHEADER*/, 0, 0), theme, null);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Size the columns to their content, then the window to the table, centred on the active window's monitor.
        list.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
        foreach (ColumnHeader c in list.Columns) c.Width += LogicalToDeviceUnits(24);
        // Room for every row plus a margin, so no scrollbar appears (they are all visible at once).
        var rowsHeight = list.Items.Count > 0 ? list.GetItemRect(list.Items.Count - 1).Bottom + LogicalToDeviceUnits(36) : 400;
        ClientSize = new Size(list.Columns.Cast<ColumnHeader>().Sum(c => c.Width) + LogicalToDeviceUnits(8), rowsHeight);
        var screen = anchor != IntPtr.Zero ? Screen.FromHandle(anchor) : Screen.PrimaryScreen!;
        var area = screen.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        Activate();
    }

    static bool IsDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hWnd, string? app, string? idList);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
