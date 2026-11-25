using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using static WindowManager.Native;

namespace WindowManager;

// The table editor tested the way it is used: real apps (a plain text box, a textarea in Edge, VS Code, Word), real keys through the
// keyboard hook (tagged TestInputTag, so the hook treats them as the user's: the /table trigger and the arrow-key reopen are the real ones),
// real mouse clicks on the picker, the grid, the ribbon, the app's own text and away from the editor. Every key and click first checks that
// it goes to the test's own window or the editor. Scenario ids: DevResources/plans/table-editor-test-plan-2026-11-20.md ("Apps" section).
static partial class Program
{
    static readonly IntPtr TestInputTag = 0x574D5459;   // "WMTY"
    static volatile bool e2eRealInput;

    static class AppsTest
    {
        sealed class Target
        {
            public string Name = "";
            public IntPtr Hwnd;
            public Process? Proc;
            public Func<string?> Read = () => null;     // the whole text, "\n" line breaks; null when unknown
            public bool LiveRead = true;                 // Read works while the editor is open (VS Code: only from the saved file)
            public bool Pipe = true;                     // a new table is pipe text (Word: a real table)
            public Func<string, Rectangle?>? Find;       // where a piece of text is on screen (to click it)
            public Action Close = () => { };
        }

        static List<string> R = [];
        static void Note(string name, bool ok, string detail) => R.Add($"- {(ok ? "ok  " : "FAIL")} {name}: {Short(detail)}");
        static void Info(string text) => R.Add($"- info {text}");
        static string Short(string s) { s = s.Replace("\r", "").Replace("\n", "\\n"); return s.Length > 260 ? s[..260] + "…" : s; }
        static readonly string Dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tests", "out", "e2e"));
        static readonly string Marker = $"wm-e2e-{DateTime.Now:HHmmss}";

        public static void Run(List<string> readings)
        {
            R = readings;
            Directory.CreateDirectory(Dir);
            e2eRealInput = true;
            try
            {
                // No Word: it opened the user's own cloud document (AutoSave on) instead of a blank one, and the test typed into it (2026-11-20).
                var only = File.Exists(Path.Combine(Dir, "only.txt")) ? File.ReadAllText(Path.Combine(Dir, "only.txt")).Trim() : "";   // e.g. "notepad": one app
            var all = new (string Name, Func<Target?> Make)[] { ("textbox", TextBox), ("notepad", Notepad), ("edge", EdgeTextarea), ("vscode", VsCode) };
            foreach (var make in all.Where(a => only == "" || only.Contains(a.Name)).Select(a => a.Make))
                {
                    Target? t = null;
                    try
                    {
                        t = make();
                        if (t is null) continue;
                        R.Add($"## {t.Name}");
                        Scenarios(t);
                    }
                    catch (Exception e) { Note("crash", false, e.ToString()); }
                    finally
                    {
                        if (TablePopup.IsShown) { TablePopup.TestDeactivate(); Closed(); }
                        try { t?.Close(); } catch { }
                        Thread.Sleep(500);
                    }
                }
            }
            finally { e2eRealInput = false; }
        }

        // ── the scenarios ──

        static void Scenarios(Target t)
        {
            string Text() => (t.Read() ?? "<unreadable>").Replace("\r\n", "\n").Replace('\r', '\n');
            int Lines(string s) => s.Split('\n').Count(l => l.TrimStart().StartsWith('|'));

            // K2 first (a clean document): "x/table" must not open the editor
            Fresh(t);
            Type(t, "x/table");
            Note("K2 x/table does not open the editor", !WaitShown(900), $"shown={TablePopup.IsShown}");
            Fresh(t);

            // K1: /table typed for real, Right (4 columns), Enter, "alpha" Tab "beta" Enter, Esc
            Type(t, "/table");
            var shown = WaitShown(2500);
            Note("K1 typed /table opens the picker, focused", shown, Describe(Desk.Foreground()));
            if (!shown) return;
            Shot($"e2e-{t.Name}-picker");
            Tap(t, Vk.Right); Tap(t, Vk.Enter); Settle();
            Shot($"e2e-{t.Name}-grid");
            Type(t, "alpha"); Tap(t, Vk.Tab); Type(t, "beta"); Tap(t, Vk.Enter); Settle();
            if (t.LiveRead && t.Pipe)
            {
                var live = Text();
                Note("K1 edits are in the app while the grid is up", live.Contains("alpha") && live.Contains("beta") && TablePopup.IsShown, live);
            }
            Tap(t, Vk.Esc); Closed();
            var k1 = Text();
            Note("K1 Esc closes; the app has the table with both cells", !TablePopup.IsShown && k1.Contains("alpha") && k1.Contains("beta") && (!t.Pipe || Lines(k1) == 5), k1);
            if (!t.Pipe) { Fresh(t); return; }   // Word: a real table; the pipe-table cases below do not apply

            // K3: a table in the middle of a document keeps the text around it
            Fresh(t);
            Type(t, "Intro line"); Tap(t, Vk.Enter);
            Type(t, "/table");
            if (WaitShown(2500))
            {
                Tap(t, Vk.Enter); Settle(); Type(t, "mid"); Tap(t, Vk.Enter); Settle(); Tap(t, Vk.Esc); Closed();
                SendRaw(KeyEngine.Tap(Vk.LCtrl, Vk.End)); Thread.Sleep(120); SendRaw(KeyEngine.Tap(Vk.Enter)); Thread.Sleep(120); Type(t, "Outro line"); Thread.Sleep(300);   // out of the table with keys the hook ignores (a real Ctrl+End stays in a table that ends the text, and reopens it)
                var k3 = Text();
                var ok = k3.StartsWith("Intro line\n") && k3.TrimEnd().EndsWith("Outro line") && k3.Contains("mid") && Lines(k3) == 5;
                Note("K3 table between two lines: both kept, table intact", ok, k3);
            }
            else Note("K3 /table after a line opens the picker", false, "not shown");

            // F1: type straight on after the insert, through the write
            Fresh(t);
            Type(t, "/table");
            if (WaitShown(2500))
            {
                Tap(t, Vk.Enter);
                Type(t, "quickbrownfox", 15); Tap(t, Vk.Tab); Type(t, "jumps", 15); Tap(t, Vk.Enter); Settle();
                Tap(t, Vk.Esc); Closed();
                var f1 = Text();
                Note("F1 fast typing through a write: nothing lost or leaked", f1.Contains("quickbrownfox") && f1.Contains("jumps") && Lines(f1) == 5, f1);
            }
            else Note("F1 /table opens", false, "not shown");

            // M1: the mouse: a picker cell, a grid cell, a ribbon button, a click away
            Fresh(t);
            Type(t, "/table");
            if (WaitShown(2500))
            {
                var cell = PageCenter(".md-gc[data-r='2'][data-c='2']");
                if (cell is { } pc) { Move(pc); Thread.Sleep(120); Click(pc); }
                Settle();
                var g = PageCenter("td.md-cell[data-r='1'][data-c='1']");
                if (g is { } gc) { Click(gc); Thread.Sleep(150); Type(t, "mouse"); Tap(t, Vk.Enter); Settle(); }
                var rowBelow = PageCenter(".md-b[data-op='insertRowBelow']");
                if (rowBelow is { } rb) { Click(rb); Settle(); }
                Shot($"e2e-{t.Name}-mouse");
                ClickAway(t); Closed();
                var m1 = Text();
                Note("M1 picker click 2x2, cell click + type, Row below, click away", cell is not null && g is not null && rowBelow is not null && m1.Contains("mouse") && Lines(m1) == 5 && !TablePopup.IsShown,
                    $"picker={cell is not null} cell={g is not null} ribbon={rowBelow is not null} text {m1}");

                // R1: click into the table's text in the app → the editor reopens there
                if (t.Find?.Invoke("mouse") is { } at)
                {
                    Click(new Point(at.Left + 2, at.Top + at.Height / 2));
                    var re = WaitShown(2500);
                    Note("R1 click into the table reopens the editor", re, re ? "shown" : "not shown");
                    if (re)
                    {
                        Shot($"e2e-{t.Name}-reopen");
                        Type(t, "again"); Tap(t, Vk.Enter); Settle(); Tap(t, Vk.Esc); Closed();
                        var r1 = Text();
                        Note("R1 the edit replaces the table, nothing duplicated", r1.Contains("again") && Lines(r1) == 5, r1);
                    }
                }
                else Info($"R1 skipped: the app does not say where its text is ({t.Name})");

                // R2: an arrow key into the table reopens it; Esc leaves it unchanged
                var before = Text();
                Tap(t, Vk.LCtrl, Vk.Home);
                var r2 = WaitShown(2500);
                Note("R2 Ctrl+Home into the table reopens the editor", r2, r2 ? "shown" : "not shown");
                if (r2) { Tap(t, Vk.Esc); Closed(); Note("R2 Esc with no change leaves the text as it was", Text() == before, Text()); }

                // A1: "Edit as text" closes it and keeps it closed while the caret stays in the table
                Fresh(t); Type(t, "/table"); if (WaitShown(2500)) { Tap(t, Vk.Enter); Settle(); Tap(t, Vk.Esc); Closed(); }
                SendRaw(KeyEngine.Tap(Vk.LCtrl, Vk.End)); Thread.Sleep(120); SendRaw(KeyEngine.Tap(Vk.Enter)); Thread.Sleep(120); Type(t, "after"); Thread.Sleep(200);   // a line after the table to leave to (untagged keys: no reopen)
                SendRaw(KeyEngine.Tap(Vk.LCtrl, Vk.Home)); Thread.Sleep(150);
                Tap(t, Vk.Home);
                if (WaitShown(2500))
                {
                    var mode = PageCenter(".md-b[data-op='mode']");
                    if (mode is { } md) Click(md);
                    Closed();
                    Tap(t, Vk.Down);
                    var muted = !WaitShown(900);
                    Tap(t, Vk.LCtrl, Vk.End); Thread.Sleep(500); Tap(t, Vk.LCtrl, Vk.Home);   // out to the line after the table, and back in
                    var back = WaitShown(2500);
                    Note("A1 Edit as text: closed, stays closed in the table, back after leaving it", mode is not null && muted && back, $"button={mode is not null} muted={muted} back={back}");
                    if (back) { Tap(t, Vk.Esc); Closed(); }
                }
                else Note("A1 the editor reopens for Edit as text", false, "not shown");
            }
            else Note("M1 /table opens", false, "not shown");

            // P1: the picker sized by hovering (4 body rows x 5 columns), then a click
            Fresh(t);
            Type(t, "/table");
            if (WaitShown(2500))
            {
                foreach (var (r, c) in new[] { (1, 1), (2, 3), (4, 5) })
                    if (PageCenter($".md-gc[data-r='{r}'][data-c='{c}']") is { } h) { Move(h); Thread.Sleep(90); }
                var label = TablePopup.EvalForTest("document.querySelector('.md-gl')?.textContent") ?? "";
                if (PageCenter(".md-gc[data-r='4'][data-c='5']") is { } p45) Click(p45);
                Settle(); Tap(t, Vk.Esc); Closed();
                var p1 = Text();
                var cols = p1.Split('\n').FirstOrDefault(l => l.StartsWith('|'))?.Count(ch => ch == '|') - 1;
                Note("P1 hover sizes the picker (label follows), click inserts 4x5", label.Contains('4') && label.Contains('5') && Lines(p1) == 6 && cols == 5, $"label {label}, {Lines(p1)} lines, {cols} cols");
            }
            else Note("P1 /table opens", false, "not shown");

            // I1: /table after text on the same line (after a space) opens; Esc gives the line back as typed
            Fresh(t);
            Type(t, "notes /table");
            var i1 = WaitShown(2500);
            if (i1) { Tap(t, Vk.Esc); Closed(); }
            Note("I1 'notes /table' opens the picker; Esc gives 'notes /table' back", i1 && Text() == "notes /table", Text());

            // T2: two tables; the second is reopened and changed; the first is untouched
            Fresh(t);
            Type(t, "/table");
            if (WaitShown(2500))
            {
                Tap(t, Vk.Enter); Settle(); Type(t, "first"); Tap(t, Vk.Enter); Settle(); Tap(t, Vk.Esc); Closed();
                Tap(t, Vk.LCtrl, Vk.End); Tap(t, Vk.Enter); Tap(t, Vk.Enter);
                Type(t, "/table");
                if (WaitShown(2500))
                {
                    Tap(t, Vk.Enter); Settle(); Type(t, "second"); Tap(t, Vk.Enter); Settle(); Tap(t, Vk.Esc); Closed();
                    var before2 = Text();
                    // Esc keeps the second table closed while the caret stays in it (the original's G.off). Ctrl+Home reopens the first;
                    // Esc out of it, then Ctrl+End comes back into the second, which now reopens.
                    Tap(t, Vk.LCtrl, Vk.End);
                    var stayedClosed = !WaitShown(800);
                    Tap(t, Vk.LCtrl, Vk.Home);
                    if (WaitShown(2500)) { Tap(t, Vk.Esc); Closed(); }
                    Tap(t, Vk.LCtrl, Vk.End);
                    Note("T2 Esc keeps the table closed until the caret leaves it", stayedClosed, stayedClosed ? "closed" : "reopened at once");
                    if (WaitShown(2500))
                    {
                        Tap(t, Vk.LCtrl, Vk.Home); Type(t, "changed"); Tap(t, Vk.Enter); Settle(); Tap(t, Vk.Esc); Closed();
                        var t2 = Text();
                        Note("T2 second table edited; the first untouched; nothing duplicated",
                            t2.Contains("first") && t2.Contains("changed") && !t2.Contains("second") && Lines(t2) == Lines(before2), $"{Lines(before2)}->{Lines(t2)} lines: {t2}");
                    }
                    else Note("T2 the second table reopens", false, "not shown");
                }
                else Note("T2 second /table opens", false, "not shown");
            }
            else Note("T2 /table opens", false, "not shown");

            // G1: grid operations: fill a row, Shift+Right select + Ctrl+C, Down + Ctrl+V, Alt+Down moves a row, ribbon Delete column and Sort
            Fresh(t);
            Type(t, "/table");
            if (WaitShown(2500))
            {
                Tap(t, Vk.Enter); Settle();
                Tap(t, Vk.Down); Type(t, "b2"); Tap(t, Vk.Tab); Type(t, "a2"); Tap(t, Vk.Tab); Type(t, "c2"); Tap(t, Vk.Enter); Settle();   // row 1: b2 a2 c2
                Tap(t, Vk.LCtrl, Vk.Home); Tap(t, Vk.Down); Tap(t, Vk.LShift, Vk.Right); Tap(t, Vk.LCtrl, 'C');
                Tap(t, Vk.Down); Tap(t, Vk.LCtrl, 'V'); Settle();                                                                 // row 2: b2 a2
                var pasted = Text();
                Tap(t, Vk.LAlt, Vk.Down); Settle();                                                                               // row 2 moves to row 3
                var moved = Text();
                var delCol = PageCenter(".md-b[data-op='deleteColumn']");
                Tap(t, Vk.LCtrl, Vk.Home); Tap(t, Vk.Right); Tap(t, Vk.Right);                                                    // column C
                if (delCol is { } dc) { Click(dc); Settle(); }
                var deleted = Text();
                Tap(t, Vk.LCtrl, Vk.Home); Tap(t, Vk.Down); Type(t, "pear"); Tap(t, Vk.Enter); Type(t, "apple"); Tap(t, Vk.Enter); Settle();   // column A: pear, apple
                var sort = PageCenter(".md-b[data-op='sortAsc']");
                Tap(t, Vk.LCtrl, Vk.Home);
                if (sort is { } so) { Click(so); Settle(); }
                Shot($"e2e-{t.Name}-ops");
                Tap(t, Vk.Esc); Closed();
                var g1 = Text();
                var colsAfter = g1.Split('\n').FirstOrDefault(l => l.StartsWith('|'))?.Count(ch => ch == '|') - 1;
                if (t.LiveRead)
                {
                    Note("G1 Shift+Right + Ctrl+C / Ctrl+V copies two cells", pasted.Split('\n').Count(l => l.Contains("b2") && l.Contains("a2")) == 2, pasted);
                    Note("G1 Alt+Down moves the row", moved != pasted && Lines(moved) == Lines(pasted), moved);
                }
                else Note("G1 copy + paste + Alt+Down, checked after close: two rows hold b2 and a2", g1.Split('\n').Count(l => l.Contains("b2") && l.Contains("a2")) >= 2, g1);   // the app can be read only after the editor closes
                Note("G1 ribbon Delete column leaves 2 columns", delCol is not null && colsAfter == 2 && !g1.Contains("c2"), $"{colsAfter} cols: {deleted}");
                Note("G1 ribbon Sort A-Z sorts the body by column A", sort is not null && IsSorted(g1), g1);
            }
            else Note("G1 /table opens", false, "not shown");

            // U1: the app's own Ctrl+Z after an edit (information: what undo does with a live-written table)
            var beforeUndo = Text();
            Tap(t, Vk.LCtrl, 'Z'); Thread.Sleep(400);
            var afterUndo = Text();
            Info($"U1 Ctrl+Z in {t.Name} after a session: {(afterUndo == beforeUndo ? "no change" : $"{Lines(beforeUndo)} -> {Lines(afterUndo)} table lines")}");
            if (TablePopup.IsShown) { Tap(t, Vk.Esc); Closed(); }
            Fresh(t);
        }

        // The table's body rows in ascending order of their first cell (empty cells last is fine either way).
        static bool IsSorted(string text)
        {
            var body = text.Split('\n').Where(l => l.TrimStart().StartsWith('|')).Skip(2).Select(l => l.Split('|')[1].Trim()).Where(s => s != "").ToList();
            return body.Count >= 2 && body.SequenceEqual(body.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        }

        // ── the targets ──

        static Target? TextBox()
        {
            var script = Path.Combine(Dir, "textbox.ps1");
            File.WriteAllText(script, $$"""
                Add-Type -AssemblyName System.Windows.Forms, System.Drawing
                $f = New-Object System.Windows.Forms.Form -Property @{ Text = '{{Marker}}-textbox'; Width = 1000; Height = 650; StartPosition = 'CenterScreen'; TopMost = $true }
                $t = New-Object System.Windows.Forms.TextBox -Property @{ Multiline = $true; Dock = 'Fill'; Font = New-Object System.Drawing.Font('Segoe UI', 12); AcceptsTab = $true }
                $f.Controls.Add($t); $f.Add_Shown({ $f.Activate(); $t.Focus() })
                [System.Windows.Forms.Application]::Run($f)
                """);
            var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"") { UseShellExecute = false, CreateNoWindow = true });
            var hwnd = WaitWindow($"{Marker}-textbox", 10000);
            if (hwnd == IntPtr.Zero) { R.Add("## textbox"); Note("textbox starts", false, "no window"); p?.Kill(); return null; }
            return new Target
            {
                Name = "textbox", Hwnd = hwnd, Proc = p,
                Read = () => TextOf(hwnd), Find = s => TextRect(hwnd, s),
                Close = () => { try { p?.Kill(); } catch { } },
            };
        }

        // Notepad restores the user's tabs: the test opens a file of its own and works only while the window's title is that file's (the active
        // tab). It switches tabs with Ctrl+Tab to get there, and closes only its own tab at the end.
        static Target? Notepad()
        {
            var file = Path.Combine(Dir, $"{Marker}-notepad.txt");
            File.WriteAllText(file, "");
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true });
            IntPtr hwnd = IntPtr.Zero;
            for (var i = 0; i < 60 && hwnd == IntPtr.Zero; i++)
            {
                Thread.Sleep(250);
                var f = Desk.Foreground();
                if (ProcessName(f).Equals("Notepad", StringComparison.OrdinalIgnoreCase)) hwnd = f;
            }
            if (hwnd == IntPtr.Zero) { R.Add("## notepad"); Note("notepad starts", false, "no window"); return null; }
            var t = new Target { Name = "notepad", Hwnd = hwnd };
            for (var i = 0; i < 30 && !Frames.Title(hwnd).Contains(Marker); i++) { SendRaw(KeyEngine.Tap(Vk.LCtrl, Vk.Tab)); Thread.Sleep(250); }   // to the test's tab
            if (!Frames.Title(hwnd).Contains(Marker)) { R.Add("## notepad"); Note("notepad: the test's own tab is active", false, Frames.Title(hwnd)); return null; }
            Thread.Sleep(500);
            t.Read = () => TextOf(hwnd);
            t.Find = s => TextRect(hwnd, s);
            t.Close = () => { if (Frames.Title(hwnd).Contains(Marker) && Desk.Foreground() == hwnd) { Tap(t, Vk.LCtrl, 'S'); Thread.Sleep(300); Tap(t, Vk.LCtrl, 'W'); } };
            return t;
        }

        static Target? EdgeTextarea()
        {
            var page = Path.Combine(Dir, "textarea.html");
            File.WriteAllText(page, $"<!doctype html><title>{Marker}-edge</title><textarea id=t style='width:96vw;height:90vh;font:16px Consolas'></textarea><script>t.focus()</script>");
            var profile = Path.Combine(Dir, "edge-profile");
            var p = Process.Start(new ProcessStartInfo(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                $"--app=\"file:///{page.Replace('\\', '/')}\" --user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --window-size=1100,750 --window-position=260,160") { UseShellExecute = false });
            var hwnd = WaitWindow($"{Marker}-edge", 15000);
            if (hwnd == IntPtr.Zero) { R.Add("## edge"); Note("edge starts", false, "no window"); KillProfile(profile); return null; }
            Thread.Sleep(800);
            Desk.SendKeys(KeyEngine.Tap(Vk.Mask)); Activate(hwnd);
            return new Target
            {
                Name = "edge", Hwnd = hwnd, Proc = p,
                Read = () => TextOf(hwnd, "t"), Find = s => TextRect(hwnd, s, "t"),
                Close = () => { try { p?.Kill(entireProcessTree: true); } catch { } KillProfile(profile); },
            };
        }

        static Target? VsCode()
        {
            var file = Path.Combine(Dir, $"{Marker}-vscode.md");
            File.WriteAllText(file, "");
            var code = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe");
            if (!File.Exists(code)) { Info("VS Code not installed"); return null; }
            var cli = Path.Combine(Path.GetDirectoryName(code)!, "bin", "code.cmd");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{cli}\" -n \"{file}\"\"") { UseShellExecute = false, CreateNoWindow = true });
            var hwnd = WaitWindow($"{Marker}-vscode", 40000);
            if (hwnd == IntPtr.Zero) { R.Add("## vscode"); Note("vscode starts", false, "no window"); return null; }
            Thread.Sleep(2500); // the editor gets the focus a moment after the window
            var t = new Target { Name = "vscode", Hwnd = hwnd, LiveRead = false };
            t.Read = () =>
            {
                if (TablePopup.IsShown || !Ours(t)) return null;
                Tap(t, Vk.LCtrl, 'S'); Thread.Sleep(500);
                try { return File.ReadAllText(file); } catch { return null; }
            };
            t.Close = () => { if (Ours(t)) { Tap(t, Vk.LCtrl, 'S'); Thread.Sleep(300); } PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero); };
            return t;
        }

        static Target? Word()
        {
            var word = @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE";
            if (!File.Exists(word)) { Info("Word not installed"); return null; }
            if (Process.GetProcessesByName("WINWORD").Length > 0) { Info("Word skipped: it is already running (the user's documents)"); return null; }
            var p = Process.Start(new ProcessStartInfo(word, "/q /w") { UseShellExecute = false });
            IntPtr hwnd = IntPtr.Zero;
            for (var i = 0; i < 120 && hwnd == IntPtr.Zero; i++) { Thread.Sleep(250); var f = Desk.Foreground(); if (ProcessName(f).Equals("WINWORD", StringComparison.OrdinalIgnoreCase)) hwnd = f; }
            if (hwnd == IntPtr.Zero) { R.Add("## word"); Note("word starts", false, "no window"); try { p?.Kill(); } catch { } return null; }
            Thread.Sleep(2000);
            return new Target
            {
                Name = "word", Hwnd = hwnd, Proc = p, Pipe = false,
                Read = () => TextOf(hwnd),
                Close = () => { foreach (var w in Process.GetProcessesByName("WINWORD")) try { w.Kill(); } catch { } },
            };
        }

        // ── input: tagged keys (the hook takes them as the user's), real mouse ──

        static bool Ours(Target t) { var f = Desk.Foreground(); if (t.Name == "edge") return (ProcessName(f).Equals("msedge", StringComparison.OrdinalIgnoreCase) && Frames.Title(f).Contains(Marker)) || (f != IntPtr.Zero && IsOurs(f)); if (t.Name == "notepad") return (f == t.Hwnd && Frames.Title(f).Contains(Marker)) || (f != IntPtr.Zero && IsOurs(f)); return f == t.Hwnd || (f != IntPtr.Zero && IsOurs(f)) || (t.Proc is { } p && ProcessOf(f) == p.Id) || (t.Name == "word" && ProcessName(f).Equals("WINWORD", StringComparison.OrdinalIgnoreCase)); }
        static int ProcessOf(IntPtr h) { GetWindowThreadProcessId(h, out var pid); return (int)pid; }

        static void Send(Target t, KeyStroke[] strokes)
        {
            for (var i = 0; i < 100 && TablePopup.Writing; i++) Thread.Sleep(10);   // a person does not type into the gap of a write either... the hook holds keys back anyway
            if (!Ours(t)) { Info($"key skipped: the foreground {Describe(Desk.Foreground())} is not the test's"); return; }
            var inputs = strokes.Select(s => new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)s.Vk, wScan = (ushort)MapVirtualKeyW((uint)s.Vk, 0), dwFlags = (s.Up ? KEYEVENTF_KEYUP : 0) | (Vk.IsExtended(s.Vk) ? KEYEVENTF_EXTENDEDKEY : 0), dwExtraInfo = TestInputTag } },
            }).ToArray();
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        static void SendRaw(KeyStroke[] strokes)
        {
            var inputs = strokes.Select(s => new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)s.Vk, wScan = (ushort)MapVirtualKeyW((uint)s.Vk, 0), dwFlags = (s.Up ? KEYEVENTF_KEYUP : 0) | (Vk.IsExtended(s.Vk) ? KEYEVENTF_EXTENDEDKEY : 0) } } }).ToArray();
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        static void Tap(Target t, params int[] vks) { Send(t, KeyEngine.Tap(vks)); Thread.Sleep(110); }

        static void Type(Target t, string text, int perCharMs = 35)
        {
            foreach (var c in text)
            {
                var v = VkKeyScanW(c);
                Send(t, (v & 0x100) != 0 ? KeyEngine.Tap(Vk.LShift, v & 0xFF) : KeyEngine.Tap(v & 0xFF));
                Thread.Sleep(perCharMs);
            }
            Thread.Sleep(100);
        }

        // Clears the document with keys the hook ignores (untagged): a tagged Ctrl+Home into a table would reopen the editor.
        static void Fresh(Target t)
        {
            if (TablePopup.IsShown) { Tap(t, Vk.Esc); Closed(); }
            Focus(t);
            if (!Ours(t)) { Info($"clear skipped: the foreground {Describe(Desk.Foreground())} is not the test's"); return; }
            foreach (var k in new[] { KeyEngine.Tap(Vk.LCtrl, Vk.Home), KeyEngine.Tap(Vk.LCtrl, Vk.LShift, Vk.End), KeyEngine.Tap(Vk.Del) }) { SendRaw(k); Thread.Sleep(120); }
            slash.Reset();   // the hook did not see these keys: a person's Ctrl+Home would have reset the /table watcher
            Thread.Sleep(250);
        }

        static void Focus(Target t)
        {
            for (var i = 0; i < 10 && !Ours(t); i++) { Desk.SendKeys(KeyEngine.Tap(Vk.Mask)); Activate(t.Hwnd); Thread.Sleep(150); } }

        static void Move(Point p) => Mouse(p, 0);
        static void Click(Point p) { Mouse(p, 0); Thread.Sleep(40); Mouse(p, MOUSEEVENTF_LEFTDOWN); Thread.Sleep(30); Mouse(p, MOUSEEVENTF_LEFTUP); Thread.Sleep(150); }
        static void Mouse(Point p, uint button)
        {
            int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
            var input = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT
            {
                dx = (int)Math.Round((p.X - vx) * 65535.0 / (vw - 1)), dy = (int)Math.Round((p.Y - vy) * 65535.0 / (vh - 1)),
                dwFlags = 0x0001 /*MOVE*/ | 0x8000 /*ABSOLUTE*/ | 0x4000 /*VIRTUALDESK*/ | button,
            } } };
            SendInput(1, [input], Marshal.SizeOf<INPUT>());
        }

        // A click into the app, away from the editor's cards (the bottom-right of the app's window).
        static void ClickAway(Target t)
        {
            if (!GetWindowRect(t.Hwnd, out var r)) return;
            Click(new Point(r.Right - 60, r.Bottom - 60));
        }

        // ── the editor ──

        static bool WaitShown(int ms) { for (var i = 0; i < ms / 25; i++) { if (TablePopup.IsShown && IsOurs(Desk.Foreground())) { Thread.Sleep(150); return true; } Thread.Sleep(25); } return false; }
        static void Settle() { Thread.Sleep(80); for (var i = 0; i < 300 && TablePopup.Writing; i++) Thread.Sleep(10); Thread.Sleep(LiveQuietMs + 400); for (var i = 0; i < 300 && TablePopup.Writing; i++) Thread.Sleep(10); Thread.Sleep(150); }
        static void Closed() { for (var i = 0; i < 100 && (Volatile.Read(ref tableEditorOpen) == 1 || TablePopup.IsShown); i++) Thread.Sleep(50); Thread.Sleep(300); }

        // The screen centre of an element of the editor page.
        static Point? PageCenter(string selector)
        {
            var js = "(()=>{const e=document.querySelector(" + JsonSerializer.Serialize(selector) + ");if(!e)return null;const b=e.getBoundingClientRect();return [b.left+b.width/2,b.top+b.height/2];})()";
            var json = TablePopup.EvalForTest(js);
            if (json is null or "null" || !GetWindowRect(TablePopup.WindowHandle, out var w)) return null;
            try
            {
                var a = JsonSerializer.Deserialize<double[]>(json);
                if (a is not { Length: 2 }) return null;
                var s = TablePopup.LastScale <= 0 ? 1 : TablePopup.LastScale;
                return new Point(w.Left + (int)Math.Round(a[0] * s), w.Top + (int)Math.Round(a[1] * s));
            }
            catch { return null; }
        }

        // ── reading an app through UI Automation ──

        static string? TextOf(IntPtr hwnd, string? automationId = null)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                AutomationElement? el = automationId is null ? null : root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));   // Edge: the textarea, not the address field
                foreach (var type in new[] { ControlType.Document, ControlType.Edit })   // the document first: Word has a search box (an Edit) before it
                    el ??= root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true), new PropertyCondition(AutomationElement.ControlTypeProperty, type)));
                if (el is null) return null;
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var v) && ((ValuePattern)v).Current.Value is { Length: > 0 } value) return value;
                return el.TryGetCurrentPattern(TextPattern.Pattern, out var tp) ? ((TextPattern)tp).DocumentRange.GetText(-1) : null;
            }
            catch { return null; }
        }

        static Rectangle? TextRect(IntPtr hwnd, string text, string? automationId = null)
        {
            try
            {
                var el = AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants, automationId is null ? new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true) : new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
                if (el is null || !el.TryGetCurrentPattern(TextPattern.Pattern, out var tp)) return null;
                var range = ((TextPattern)tp).DocumentRange.FindText(text, false, false);
                if (range is null) return null;
                foreach (var r in range.GetBoundingRectangles())
                    if (!r.IsEmpty && r.Height > 0) return new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
            }
            catch { }
            return null;
        }

        static IntPtr WaitWindow(string title, int ms)
        {
            for (var i = 0; i < ms / 200; i++)
            {
                Thread.Sleep(200);
                IntPtr found = IntPtr.Zero;
                EnumWindows((h, _) => { if (IsWindowVisible(h) && Frames.Title(h).Contains(title, StringComparison.OrdinalIgnoreCase)) { found = h; return false; } return true; }, IntPtr.Zero);
                if (found != IntPtr.Zero)
                {
                    for (var k = 0; k < 20 && Desk.Foreground() != found; k++) { Desk.SendKeys(KeyEngine.Tap(Vk.Mask)); Activate(found); Thread.Sleep(100); }
                    return found;
                }
            }
            return IntPtr.Zero;
        }

        // The test's own Edge (its own profile folder): its browser process tree, by the profile path on the command line.
        static void KillProfile(string profile)
        {
            var ps = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"Get-CimInstance Win32_Process -Filter \\\"Name='msedge.exe'\\\" | Where-Object {{ $_.CommandLine -like '*{profile}*' }} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }}\"") { UseShellExecute = false, CreateNoWindow = true });
            ps?.WaitForExit(8000);
        }

        static void Shot(string name)
        {
            try
            {
                var screen = System.Windows.Forms.Screen.FromHandle(Desk.Foreground()).Bounds;
                using var bmp = new Bitmap(screen.Width, screen.Height);
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(screen.Left, screen.Top, 0, 0, bmp.Size);
                var path = Path.Combine(Dir, name + ".png");
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                R.Add($"- shot {path}");
            }
            catch (Exception e) { R.Add($"- shot {name} failed: {e.Message}"); }
        }

        [DllImport("user32.dll")] static extern short VkKeyScanW(char c);
        [DllImport("user32.dll")] static extern uint MapVirtualKeyW(uint code, uint type);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    }
}
