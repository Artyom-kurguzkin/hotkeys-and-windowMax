using System.Text;
using System.Windows.Automation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static WindowManager.Native;

namespace WindowManager;

// `--test-taskview`: which way of moving to the next desktop inside Task View (Win+Tab) makes Enter land there. Each strategy opens Task View on
// the start desktop, tries to move one desktop right, presses Enter (only while Task View is the foreground: no key reaches anything else),
// and reads the desktop it landed on; then goes back to the start desktop. Readings: tests/out/taskview-test.md and wm.log ("taskview test:").
// Hands off the keyboard for about a minute. Needs at least two desktops, and the start desktop not the last one.
static partial class Program
{
    const uint WM_APP_TASKVIEWTEST = 0x801A;
    static string TaskViewTestPath => Path.Combine(Log.DefaultDir, "taskview-test.md");

    static bool HandOffTaskViewTest()
    {
        for (var w = FindWindowEx(HWND_MESSAGE, IntPtr.Zero, MsgClass, null); w != IntPtr.Zero; w = FindWindowEx(HWND_MESSAGE, w, MsgClass, null))
        {
            GetWindowThreadProcessId(w, out var pid);
            if (pid == Environment.ProcessId) continue;
            try { File.Delete(TaskViewTestPath); } catch { }
            PostMessage(w, WM_APP_TASKVIEWTEST, IntPtr.Zero, IntPtr.Zero);
            for (var i = 0; i < 1200 && !File.Exists(TaskViewTestPath); i++) Thread.Sleep(100);
            return true;
        }
        return false;
    }

    // Keys the manager's own hook takes as the user's (TestInputTag while e2eRealInput): the real PgUp/PgDn and Enter path.
    static void SendAsUser(params KeyStroke[] strokes)
    {
        var inputs = strokes.Select(s => new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)s.Vk, dwFlags = (s.Up ? KEYEVENTF_KEYUP : 0) | (Vk.IsExtended(s.Vk) ? KEYEVENTF_EXTENDEDKEY : 0), dwExtraInfo = TestInputTag } } }).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    static void TaskViewTest()
    {
        var report = new StringBuilder($"# Task View test {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n");
        void Say(string s) { Log.Info("taskview test: " + s); report.AppendLine(s); }
        string Fg() => Frames.ClassName(Desk.Foreground());
        bool InTaskView() => VirtualDesktops.IsTaskView(Fg());
        void Tap(params int[] vks) => Desk.SendKeys(KeyEngine.Tap(vks));
        void TapInTaskView(params int[] vks) { if (InTaskView()) Tap(vks); else Say($"  (not sent: foreground is {Fg()})"); }
        bool WaitFor(Func<bool> cond, int ms) { var until = Environment.TickCount64 + ms; while (!cond()) { if (Environment.TickCount64 > until) return false; Thread.Sleep(20); } return true; }
        string Focused() { try { var f = AutomationElement.FocusedElement; return $"[{f.Current.ControlType.ProgrammaticName}] '{f.Current.Name}' id='{f.Current.AutomationId}'"; } catch (Exception e) { return "focus: " + e.Message; } }
        bool Open() { Tap(Vk.LWin, Vk.Tab); var ok = WaitFor(InTaskView, 3000); Thread.Sleep(700); return ok; }
        void CloseIfOpen() { if (InTaskView()) { Tap(Vk.Esc); WaitFor(() => !InTaskView(), 3000); } Thread.Sleep(700); }
        void GoTo(int index)
        {
            CloseIfOpen();
            for (var i = 0; i < 10 && CurrentDesktop() is var c && c != index && c >= 0; i++) { VirtualDesktops.Switch(index > c); Thread.Sleep(500); }
        }

        var start = CurrentDesktop(); var count = DesktopIds().Count;
        Say($"desktops {count}, start {start}, Windows {Environment.OSVersion.Version}");
        if (count < 2 || start < 0 || start >= count - 1) { Say("needs a desktop to the right of the current one"); Write(); return; }
        var target = start + 1;

        // the tree once, so the Task View's own controls are on record
        if (Open())
        {
            Say("open: focus " + Focused());
            try
            {
                var tv = AutomationElement.FromHandle(Desk.Foreground());
                var all = tv.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                report.AppendLine("\n```");
                foreach (AutomationElement e in all)
                {
                    var c = e.Current;
                    var pats = string.Join(",", e.GetSupportedPatterns().Select(p => p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", "")));
                    report.AppendLine($"[{c.ControlType.ProgrammaticName}] '{c.Name}' id='{c.AutomationId}' class='{c.ClassName}' focus={c.HasKeyboardFocus} {pats}");
                }
                report.AppendLine("```\n");
            }
            catch (Exception e) { Say("tree: " + e.Message); }
        }

        var manager = msgWnd != IntPtr.Zero;   // running inside the manager: its hook handles the keys below
        void UserKey(int vk) { if (InTaskView()) SendAsUser(KeyEngine.Tap(vk)); else Say($"  (not sent: foreground is {Fg()})"); }
        var real = new List<(string Name, int Steps)> { ("the manager: PgDn, Enter", 1), ("the manager: PgDn, PgDn, PgUp, PgDn, Enter", 2) };
        var strategies = new List<(string Name, Action Move, int Finish)>
        {
            ("Ctrl+Win+Right inside Task View", () => TapInTaskView(Vk.LCtrl, Vk.LWin, Vk.Right), Vk.Enter),
            ("SwitchDesktop API inside Task View, Enter", () => VirtualDesktops.Switch(true), Vk.Enter),
            ("SwitchDesktop API inside Task View, Esc", () => VirtualDesktops.Switch(true), Vk.Esc),
            ("Esc, SwitchDesktop API, Win+Tab", () => { CloseIfOpen(); VirtualDesktops.Switch(true); Thread.Sleep(600); Open(); }, Vk.Enter),
            ("Esc, Ctrl+Win+Right, Win+Tab", () => { CloseIfOpen(); Tap(Vk.LCtrl, Vk.LWin, Vk.Right); Thread.Sleep(900); Open(); }, Vk.Enter),
            ("UIA: select/invoke the target desktop's item", () => SelectDesktopItem(target, Say), Vk.Enter),
        };
        if (!manager) Say("not inside the manager: its PgUp/PgDn path is not tested (start the manager, then run --test-taskview again)");
        foreach (var (name, move, finish) in strategies)
        {
            GoTo(start);
            if (!Open()) { Say($"{name}: Task View did not open (foreground {Fg()})"); continue; }
            move();
            Thread.Sleep(900);
            var during = CurrentDesktop(); var focus = Focused();
            TapInTaskView(finish);
            WaitFor(() => !InTaskView(), 3000);
            Thread.Sleep(900);
            var landed = CurrentDesktop();
            Say($"{(landed == target ? "PASS" : "FAIL")} {name}: during {during}, focus {focus}, landed {landed} (want {target})");
        }
        if (manager)
        {
            e2eRealInput = true;
            try
            {
                foreach (var (name, steps) in real)
                {
                    var want = start + steps;
                    if (want >= count) { Say($"{name}: skipped, no desktop {want}"); continue; }
                    GoTo(start);
                    if (!Open()) { Say($"{name}: Task View did not open (foreground {Fg()})"); continue; }
                    var keys = steps == 1 ? new[] { Vk.PgDn } : [Vk.PgDn, Vk.PgDn, Vk.PgUp, Vk.PgDn];
                    foreach (var k in keys) { UserKey(k); Thread.Sleep(700); }
                    var during = CurrentDesktop(); var focus = Focused();
                    UserKey(Vk.Enter);
                    WaitFor(() => !InTaskView(), 3000);
                    Thread.Sleep(900);
                    var landed = CurrentDesktop();
                    Say($"{(landed == want && !InTaskView() ? "PASS" : "FAIL")} {name}: during {during}, focus {focus}, landed {landed} (want {want}), foreground {Fg()}");
                }
            }
            finally { e2eRealInput = false; }
        }
        GoTo(start);
        Write();

        void Write()
        {
            try { File.WriteAllText(TaskViewTestPath, report.ToString()); Log.Info("taskview test: written to " + TaskViewTestPath); }
            catch (Exception e) { Log.Info("taskview test: could not write the report: " + e.Message); }
        }
    }

    // In Task View's desktop strip: the item whose name is the target desktop's ("Desktop 3" unless renamed); selected or invoked through UI Automation.
    static void SelectDesktopItem(int index, Action<string> say)
    {
        try
        {
            var tv = AutomationElement.FromHandle(Desk.Foreground());
            var name = DesktopName(index);
            var items = tv.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>()
                .Where(e => e.Current.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            say($"  UIA: {items.Count} list item(s) named like '{name}'");
            foreach (var e in items)
            {
                if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var s)) { ((SelectionItemPattern)s).Select(); say("  UIA: selected"); return; }
                if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var i)) { ((InvokePattern)i).Invoke(); say("  UIA: invoked"); return; }
            }
        }
        catch (Exception e) { say("  UIA: " + e.Message); }
    }

    const string VdKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    static List<Guid> DesktopIds()
    {
        using var k = Registry.CurrentUser.OpenSubKey(VdKey);
        var raw = k?.GetValue("VirtualDesktopIDs") as byte[] ?? [];
        return Enumerable.Range(0, raw.Length / 16).Select(i => new Guid(raw.AsSpan(i * 16, 16))).ToList();
    }
    static int CurrentDesktop()
    {
        using var k = Registry.CurrentUser.OpenSubKey(VdKey);
        return k?.GetValue("CurrentVirtualDesktop") is byte[] { Length: 16 } b ? DesktopIds().IndexOf(new Guid(b)) : -1;
    }
    static string DesktopName(int index)
    {
        var id = DesktopIds()[index];
        using var k = Registry.CurrentUser.OpenSubKey($@"{VdKey}\Desktops\{id:B}");
        return k?.GetValue("Name") as string is { Length: > 0 } n ? n : $"Desktop {index + 1}";
    }
}
