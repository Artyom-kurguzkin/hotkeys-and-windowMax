using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using static WindowManager.Native;

namespace WindowManager;

// `--test-table`: the typed-/table pipeline measured step by step on the real desktop, since injected keys only ever reach the
// input desktop (the hidden-desktop sandbox cannot see them). A running manager is asked to run it in-process (the environment
// that matters); otherwise this instance runs it after its normal startup and exits. It types into a Notepad it starts and kills
// itself; the user keeps their hands off for a few seconds. Readings: tests/out/table-selftest.md and wm.log ("table test:").
static partial class Program
{
    const uint WM_APP_TABLETEST = 0x8019;
    static readonly UIntPtr TimerTableTest = 12;
    static bool tableTestOnStart;

    [DllImport("user32.dll")] static extern short VkKeyScanW(char c);

    // Before the hooks: hand the test to a running instance when there is one.
    static bool HandOffTableTest()
    {
        for (var w = FindWindowEx(HWND_MESSAGE, IntPtr.Zero, MsgClass, null); w != IntPtr.Zero; w = FindWindowEx(HWND_MESSAGE, w, MsgClass, null))
        {
            GetWindowThreadProcessId(w, out var pid);
            if (pid == Environment.ProcessId) continue;
            var path = TableTestPath;
            try { File.Delete(path); } catch { }
            PostMessage(w, WM_APP_TABLETEST, IntPtr.Zero, IntPtr.Zero);
            Console.WriteLine($"asked the running manager (pid {pid}) to run the test; readings go to {path}");
            for (var i = 0; i < 300 && !File.Exists(path); i++) PumpingSleep(100);
            Console.WriteLine(File.Exists(path) ? File.ReadAllText(path) : "no readings after 30 s: see wm.log");
            return true;
        }
        tableTestOnStart = true;
        return false;
    }

    static string TableTestPath => Path.Combine(AppContext.BaseDirectory, "..", "tests", "out", "table-selftest.md");

    static void StartTableTest(bool thenQuit)
    {
        var t = new Thread(() =>
        {
            var readings = new List<string>();
            try { TableSelfTest.Run(readings); }
            catch (Exception e) { readings.Add($"- CRASH: {e}"); }
            var path = TableTestPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = $"# Table self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss} (pid {Environment.ProcessId})\n\n{string.Join("\n", readings)}\n";
            File.WriteAllText(path, text);
            foreach (var r in readings) Log.Info($"table test: {r}");
            if (thenQuit) PostMessage(msgWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }) { IsBackground = true, Name = "table-test" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static class TableSelfTest
    {
        static List<string> R = [];
        static void Note(string name, bool ok, string detail) => R.Add($"- {(ok ? "ok  " : "FAIL")} {name}: {detail}");

        public static void Run(List<string> readings)
        {
            R = readings;
            AppsTest.Run(R); // real apps, real keys through the hook, real mouse (Program.TableE2E.cs)
        }

        // Notepad, read back through UI Automation. (A text box of our own was tried first: UI Automation is unsupported against
        // the client's own process and stalled the form's thread, so every reading failed for reasons production never meets.)
        static void Notepad()
        {
            R.Add("## Test text box (its own process)");
            // Not Notepad: Windows 11 Notepad restores the user's session tabs; a bare start put the test's keys (and its Ctrl+A, Backspace
            // cleanup) into the user's own unsaved note, and opening a file put it in a background tab (2026-11-20). The target is a plain
            // text box in a PowerShell process of the test's own (another process: UI Automation cannot read our own windows).
            Directory.CreateDirectory(Path.GetDirectoryName(TableTestPath)!);
            var script = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(TableTestPath)!, "table-selftest-target.ps1"));
            File.WriteAllText(script, $$"""
                Add-Type -AssemblyName System.Windows.Forms, System.Drawing
                $f = New-Object System.Windows.Forms.Form -Property @{ Text = '{{Marker}}'; Width = 900; Height = 500; StartPosition = 'CenterScreen'; TopMost = $true }
                $t = New-Object System.Windows.Forms.TextBox -Property @{ Multiline = $true; Dock = 'Fill'; Font = New-Object System.Drawing.Font('Consolas', 14); AcceptsTab = $true }
                $f.Controls.Add($t); $f.Add_Shown({ $f.Activate(); $t.Focus() })
                [System.Windows.Forms.Application]::Run($f)
                """);
            Process? targetProc;
            try { targetProc = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"") { UseShellExecute = false, CreateNoWindow = true }); }
            catch (Exception e) { Note("test text box start", false, e.Message); return; }
            IntPtr hwnd = IntPtr.Zero;
            for (var i = 0; i < 80 && hwnd == IntPtr.Zero; i++)
            {
                PumpingSleep(100);
                if (Ours()) hwnd = Desk.Foreground();
            }
            Note("test text box in the foreground", hwnd != IntPtr.Zero, hwnd != IntPtr.Zero ? Describe(hwnd) : $"foreground is {Describe(Desk.Foreground())} \"{Frames.Title(Desk.Foreground())}\"");
            if (hwnd == IntPtr.Zero) return;
            PumpingSleep(300);
            TextPattern? Doc()
            {
                try
                {
                    // the text box itself, never FocusedElement (with the popup focused that is the editor page)
                    var box = AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
                    return box is not null && box.TryGetCurrentPattern(TextPattern.Pattern, out var p) ? (TextPattern)p : null;
                }
                catch { return null; }
            }
            string Text() => Doc()?.DocumentRange.GetText(-1).Replace("\r\n", "\n").Replace('\r', '\n') ?? "<no TextPattern>"; // Notepad reports bare \r line breaks
            testKeyGuard = () => Ours() || IsOurs(Desk.Foreground());
            var start = Text();
            if (start != "") { Note("the test's file is empty before any key", false, $"it holds {start.Length} characters: nothing typed"); return; }
            try
            {
                Pipeline(Text, "| x |");
                EndToEnd(Text);
            }
            finally
            {
                // Only the test's own tab, and closed rather than the process killed (the same Notepad process holds the user's tabs).
                try { targetProc?.Kill(); } catch { }
            }
        }

        static readonly string Marker = $"wm-table-selftest-{DateTime.Now:HHmmss}";
        // The foreground is the test's own Notepad tab (its title carries the file name). Every key that edits text checks this first.
        static bool Ours() => Frames.Title(Desk.Foreground()).Contains(Marker, StringComparison.OrdinalIgnoreCase);

        // The steps of the real flow, each measured. `read`: the control's whole text.
        static void Pipeline(Func<string> read, string table)
        {
            var clip = new WinClipboard();
            TypeText("/table"); PumpingSleep(300);
            var t = read(); Note("typed /table", t == "/table", $"control holds \"{t}\"");
            var control = FocusedTextControl();
            Note("UIA says the focus is in a text control", control is not null, control ?? "no");

            Keys.Backspaces(6); PumpingSleep(150);
            t = read(); Note("Backspace x6 removes it", t == "", $"control holds \"{t}\"");

            var snap = Try("clipboard snapshot", () => clip.Snapshot());
            Try("clipboard set table", () => { clip.SetTable(table, null); return true; });
            Keys.Paste(); PumpingSleep(TableSession.PasteSettleMs);
            t = read(); Note("Ctrl+V pastes it", t.Contains(table), $"control holds \"{t}\"");
            Try("clipboard restore", () => { clip.Restore(snap); return true; });
            var text = Try("clipboard reads back after restore", () => clip.GetText() ?? "<null>");
            var was = snap is DataObject d && d.TryGetData<string>(DataFormats.UnicodeText, out var s0) ? s0 : "<null>";
            Note("clipboard restored", text == was, $"\"{Trunc(text ?? "<null>")}\"");

            Keys.Backspaces(t.Length); PumpingSleep(150);
            Keys.TypeText("/table"); PumpingSleep(150);
            t = read(); Note("typing /table back (cancel path)", t == "/table", $"control holds \"{t}\"");
            Keys.Backspaces(6); PumpingSleep(100);
        }

        // The test plan (DevResources/plans/table-editor-test-plan-2026-11-20.md), live: the real popup (the original md-editor.js in
        // WebView2) driven by real keys. Case ids match the plan. Screenshots of each stage go to tests/out/live-*.png.
        static void EndToEnd(Func<string> read)
        {
            string Flat() => read().Replace("\n", "\n");
            int TableLines(string s) => s.Split('\n').Count(l => l.TrimStart().StartsWith('|'));
            bool Shown(int ms) { for (var i = 0; i < ms / 25; i++) { if (TablePopup.IsShown && IsOurs(Desk.Foreground())) return true; PumpingSleep(25); } return false; }
            void Settle() { for (var i = 0; i < 200 && TablePopup.Writing; i++) PumpingSleep(20); PumpingSleep(LiveQuietMs + 450); for (var i = 0; i < 200 && TablePopup.Writing; i++) PumpingSleep(20); PumpingSleep(150); }
            void Closed() { for (var i = 0; i < 100 && Volatile.Read(ref tableEditorOpen) == 1; i++) PumpingSleep(100); PumpingSleep(300); }
            void Clear() { Keys.Type(KeyEngine.Tap(Vk.LCtrl, Vk.Home)); Keys.Type(KeyEngine.Tap(Vk.LCtrl, Vk.LShift, Vk.End)); Keys.Type(KeyEngine.Tap(Vk.Back)); PumpingSleep(200); } // not Ctrl+A: a plain text box ignores it
            void Key(params int[] vks) { for (var i = 0; i < 100 && TablePopup.Writing; i++) PumpingSleep(20); Keys.Type(KeyEngine.Tap(vks)); PumpingSleep(120); }
            string? Clip() { string? s = null; var t = new Thread(() => { try { s = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { } }); t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join(); return s; }
            var clipBefore = Clip();

            // A1 + B1: /table, the picker at the caret, Esc → /table back
            TypeText("/table"); PumpingSleep(300);
            var caret = Caret.Rect();
            OpenTableEditor(typed: true);
            var shown = Shown(3000);
            GetWindowRect(TablePopup.WindowHandle, out var pr);
            Note("A1 /table: the picker opens, focused", shown, $"shown={shown}, caret {caret}, popup window {pr} (where the grid sits: live-A1-picker.png)");
            PumpingSleep(200); Shot("live-A1-picker");
            Key(Vk.Esc); Closed();
            Note("B1 picker Esc: /table is back", read() == "/table", $"text \"{Flat()}\"");
            Clear();

            // B3: picker, click-away → /table back
            TypeText("/table"); PumpingSleep(300);
            OpenTableEditor(typed: true);
            if (Shown(3000)) { PumpingSleep(200); TablePopup.TestDeactivate(); Closed(); }
            Note("B3 picker, click elsewhere: /table is back", read() == "/table", $"text \"{Flat()}\"");
            Clear();

            // B2 + C1: picker Right, Down, Enter → a 4-column table with 4 body rows, written, and the grid stays up
            TypeText("/table"); PumpingSleep(300);
            OpenTableEditor(typed: true);
            if (!Shown(3000)) { Note("B2 second /table: the popup opens", false, "not shown"); return; }
            PumpingSleep(200);
            Key(Vk.Right); Key(Vk.Down); Key(Vk.Enter); Settle();
            var t = read();
            var cols = t.Split('\n').FirstOrDefault(l => l.StartsWith('|'))?.Count(ch => ch == '|') - 1;
            Note("B2 picker 4x4: the table is written", TableLines(t) == 6 && cols == 4, $"{TableLines(t)} table lines, {cols} columns: \"{Flat()}\"");
            Note("C1 after the insert the grid stays up", TablePopup.IsShown, $"shown={TablePopup.IsShown}");
            Shot("live-C1-grid");

            // C2 + C3: type a word into the active cell straight away, Enter → the text has it, the grid is still up, nothing typed into the app
            TypeText("apple"); Key(Vk.Enter); Settle();
            t = read();
            Note("C2/C3 a cell typed: the text updates live, the grid stays up, no keys lost", t.Contains("apple") && TablePopup.IsShown && !t.Contains("pple|") && TableLines(t) == 6, $"shown={TablePopup.IsShown} text \"{Flat()}\"");
            Shot("live-C2-edited");

            // C4: Ctrl+End (last cell), Tab → a row is added
            Key(Vk.LCtrl, Vk.End); Key(Vk.Tab); Settle();
            t = read();
            Note("C4 Tab in the last cell adds a row", TableLines(t) == 7 && TablePopup.IsShown, $"{TableLines(t)} table lines");

            // C5: back to the word (Ctrl+Home: the header cell it is in), Delete → the cell is cleared
            Key(Vk.LCtrl, Vk.Home); Key(Vk.Del); Settle();
            t = read();
            Note("C5 Delete clears the cell in the text", !t.Contains("apple") && TablePopup.IsShown, $"text \"{Flat()}\"");
            Key(Vk.Down); TypeText("pear"); Key(Vk.Enter); Settle();

            // C6: Esc out of the grid → closed, the text keeps the table
            var beforeEsc = read();
            Key(Vk.Esc); Closed();
            t = read();
            Note("C6 Esc: the popup closes, the text keeps the final table", !TablePopup.IsShown && t.Contains("pear") && TableLines(t) == 7, $"shown={TablePopup.IsShown} text \"{Flat()}\"");

            // D1 + D2: the caret back in the table → the popup opens over it; a cell edited → replaced, not duplicated
            Key(Vk.LCtrl, Vk.Home); Key(Vk.Down); Key(Vk.Down);
            QueueReeditCheck();
            shown = Shown(3000);
            Note("D1 caret back in the table: the popup opens on it, focused", shown, shown ? "shown" : "not shown");
            if (shown)
            {
                PumpingSleep(250); Shot("live-D1-reedit");
                TypeText("plum"); Key(Vk.Enter); Settle();
                t = read();
                Note("D2 re-edit: the table is replaced live, not duplicated", t.Contains("plum") && TableLines(t) == 7, $"{TableLines(t)} table lines, text \"{Flat()}\"");
                Key(Vk.Esc); Closed();
            }

            // D3: re-open, leave with no change → untouched
            var before = read();
            Key(Vk.LCtrl, Vk.Home);
            QueueReeditCheck();
            if (Shown(3000)) { PumpingSleep(250); Key(Vk.Esc); Closed(); Note("D3 re-open, no change: the text is untouched", read() == before, $"text \"{Flat()}\""); }
            else Note("D3 re-open, no change: the popup opened", false, "not shown");

            // E1: the clipboard is the user's again
            var clipAfter = Clip();
            Note("E1 the clipboard is given back", clipAfter == clipBefore, $"before {(clipBefore?.Length ?? -1)} chars, after {(clipAfter?.Length ?? -1)} chars");

            // E4: a later copy by another program is not held up by the editor (it owns the clipboard after giving the user's back)
            var copyMs = -1L;
            var other = new Thread(() => { var s = Stopwatch.StartNew(); try { Clipboard.SetText(clipBefore ?? "x"); } catch { } copyMs = s.ElapsedMilliseconds; });
            other.SetApartmentState(ApartmentState.STA); other.Start(); other.Join();
            Note("E4 another program's copy after a session is not held up", copyMs >= 0 && copyMs < 500, $"{copyMs} ms");
            Clear();
        }

        // The popup's visible cards start within 60 px of the caret (A1).
        static bool TablePopupNear(Rectangle caret)
        {
            if (RgnBox(TablePopup.WindowHandle, out var box) <= 1 || !GetWindowRect(TablePopup.WindowHandle, out var w)) return false; // 1 = NULLREGION
            var left = w.Left + box.Left; var top = w.Top + box.Top;
            return Math.Abs(left - caret.Left) < 60 + 200 && top < caret.Bottom + 60 && w.Top + box.Bottom > caret.Top - 60;
        }
        [DllImport("user32.dll", EntryPoint = "GetWindowRgnBox")] static extern int RgnBox(IntPtr hWnd, out Rect rect);

        // The foreground window (the popup) as it is on screen, to tests/out/<name>.png.
        static void Shot(string name)
        {
            try
            {
                var fg = TablePopup.IsShown ? TablePopup.WindowHandle : Desk.Foreground(); // the popup is cut to its cards: its rect shows them over the app
                if (!GetWindowRect(fg, out var r) || r.Width <= 0) return;
                using var bmp = new Bitmap(r.Width, r.Height);
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
                var dir = Path.GetDirectoryName(TableTestPath)!;
                Directory.CreateDirectory(dir);
                bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                R.Add($"- shot {name}: {Path.GetFullPath(Path.Combine(dir, name + ".png"))}");
            }
            catch (Exception e) { R.Add($"- shot {name} failed: {e.Message}"); }
        }

        static string Trunc(string s) => (s.Length > 60 ? s[..60] + "…" : s).Replace("\n", "\\n");

        static T? Try<T>(string what, Func<T> f)
        {
            try { var r = f(); return r; }
            catch (Exception e) { Note(what, false, $"{e.GetType().Name}: {e.Message}{(e is ExternalException x ? $" (hr 0x{x.ErrorCode:X8})" : "")}"); return default; }
        }

        static void TypeText(string text)
        {
            for (var i = 0; i < 100 && TablePopup.Writing; i++) PumpingSleep(20);
            var strokes = new List<KeyStroke>();
            foreach (var c in text)
            {
                var v = VkKeyScanW(c);
                var vk = v & 0xFF; var shift = (v & 0x100) != 0;
                strokes.AddRange(shift ? KeyEngine.Tap(Vk.LShift, vk) : KeyEngine.Tap(vk));
            }
            Keys.Type([.. strokes]);
        }

        static bool WaitForeground(IntPtr hwnd, int ms)
        {
            Activate(hwnd);
            for (var i = 0; i < ms / 50; i++) { if (Desk.Foreground() == hwnd) return true; PumpingSleep(50); if (i == 10) Activate(hwnd); }
            return Desk.Foreground() == hwnd;
        }

        static IEnumerable<IntPtr> TopWindows()
        {
            var list = new List<IntPtr>();
            EnumWindows((h, _) => { list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }

        // The production key sender, exactly as the editor uses it.
        static readonly TableKeys Keys = new([]);
    }
}
