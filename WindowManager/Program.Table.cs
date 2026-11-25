using System.Diagnostics;
using static WindowManager.Native;

namespace WindowManager;

// Typing `/table` in any text control opens the table editor (TableEditor.cs) over it. The decisions are in ClipboardTable.cs
// (TableSession); this file supplies the keystroke watcher, the real keys and the thread, like HelpWindow.Toggle does for the hotkey list.
static partial class Program
{
    static int tableEditorOpen; // one at a time
    static readonly SlashTrigger slash = new();
    static readonly byte[] keyState = new byte[256];
    static readonly System.Text.StringBuilder keyChars = new(8);

    // Hook thread, for a key-down the apps will see: what it types, into the watcher. True when it completed `/table`.
    static bool SlashTyped(KBDLLHOOKSTRUCT k)
    {
        var vk = (int)k.vkCode;
        if (Vk.IsModifier(vk)) return false;
        if (keys.Mods is not (Mod.None or Mod.Shift)) { slash.Reset(); return false; } // a chord, not text
        if (vk == Vk.Back) { slash.Backspace(); return false; }
        if (SlashTrigger.IsBoundary(vk)) { slash.Reset(); return false; }
        // The character this key makes under the focused window's layout (ToUnicode with the hook thread's own layout
        // would be wrong for a window on another layout; `/` is Shift+7 on a German one).
        var layout = GetKeyboardLayout(GetWindowThreadProcessId(Desk.Foreground(), out _));
        if (!GetKeyboardState(keyState)) return false;
        keyChars.Clear();
        var n = ToUnicodeEx(k.vkCode, k.scanCode, keyState, keyChars, keyChars.Capacity, TOUNICODE_NO_STATE_CHANGE, layout);
        if (n <= 0) { if (n < 0) slash.Reset(); return false; } // n < 0: a dead key - the next character is composed, not typed
        var fired = false;
        for (var i = 0; i < n; i++) fired = slash.Type(keyChars[i]);
        return fired;
    }

    // Hook thread: take the target now (the popup is about to take the focus), then do the rest on its own STA thread.
    // typed: `/table` was just typed; else `existing`: the table the caret was put into.
    static void OpenTableEditor(bool typed, TableAtCaret? existing = null)
    {
        if (Desk.Sandbox) { Log.Info("table editor: not available in the sandbox"); return; }
        var fg = Desk.Foreground();
        if (fg == IntPtr.Zero) { Log.Info("table editor: no foreground window"); return; }
        if (Interlocked.Exchange(ref tableEditorOpen, 1) == 1) { Log.Info("table editor: already open"); return; }
        var target = new TableTarget(fg, ProcessName(fg), Frames.Title(fg));
        var held = keys.Held; // a held Shift has to be lifted for the keys we send
        EditorThread().Add(() =>
        {
            try { RunTableEditor(target, held, typed, existing); }
            catch (Exception e) { Log.Info($"table editor error: {e}"); }
            finally { Interlocked.Exchange(ref tableEditorOpen, 0); }
        });
    }

    // One long-lived STA thread for every editor session: a new thread paid ~5 s on its first clipboard and virtual-desktop (COM) calls,
    // which made the first live write of each session take 5 s (measured). Warmed at startup (WarmTableEditor).
    static System.Collections.Concurrent.BlockingCollection<Action>? editorWork;
    static System.Collections.Concurrent.BlockingCollection<Action> EditorThread()
    {
        if (editorWork is { } q) return q;
        var work = new System.Collections.Concurrent.BlockingCollection<Action>();
        if (Interlocked.CompareExchange(ref editorWork, work, null) is { } other) return other;
        // It pumps messages while idle: after a session it owns the clipboard (the user's clipboard was given back from here), and an app
        // copying later sends WM_DESTROYCLIPBOARD to this thread; a thread blocked without pumping held every Ctrl+C for 5 s (measured).
        var t = new Thread(() => { while (true) { if (work.TryTake(out var a)) a(); else PumpingSleep(15); } }) { IsBackground = true, Name = "table-editor" };
        t.SetApartmentState(ApartmentState.STA); // the clipboard needs it
        t.Start();
        return work;
    }

    static void WarmTableEditor() => EditorThread().Add(() =>
    {
        var sw = Stopwatch.StartNew();
        try { _ = new WinClipboard().Sequence; _ = Clipboard.ContainsText(); _ = VirtualDesktops.IsOnCurrent(GetForegroundWindow()); } catch { }
        Log.Info($"table editor: thread warmed in {sw.ElapsedMilliseconds}ms");
    });

    // The original editor (repo-task-manager's md-editor.js, unchanged, in the WebView2 popup, TablePopup.cs) runs the whole
    // interaction: `/table` in its textarea opens its size picker, the table gets its grid and ribbon. Here: take the text out of the
    // app, hand it to the popup at the caret, and put what comes back where it came from. No cancel, as in the original: whatever the
    // textarea holds when the popup closes is written back if it changed (the app's own Ctrl+Z undoes it).
    static void RunTableEditor(TableTarget target, int[] heldAtPress, bool typed, TableAtCaret? existing)
    {
        var tkeys = new TableKeys(heldAtPress);
        var session = new TableSession(new WinClipboard(), tkeys, PumpingSleep);
        Log.Info($"table editor: target {target.Process} \"{target.Title}\" ({target.Kind})");
        string text; int caretAt; Rectangle caret;
        if (typed)
        {
            PumpingSleep(TypedSettleMs); // the last key is still on its way to the app
            var control = FocusedTextControl();
            if (control is null) { Log.Info("table editor: the focus is not in a text control, left alone"); return; }
            Log.Info($"table editor: /table typed in {control}: removing it");
            session.RemoveTyped(slash.Command);
            PumpingSleep(TypedSettleMs);
            var at = Caret.Rect(); // where the slash was: the popup's textarea goes there
            Log.Info($"table editor: caret {(at is { } a ? a.ToString() : "unknown")}");
            Desk.GetCursor(out var mouse);
            (text, caretAt, caret) = (slash.Command, slash.Command.Length, at ?? new Rectangle(mouse.X, mouse.Y, 1, 20));
        }
        else if (existing is { } e)
        {
            (text, caretAt, caret) = (e.Text, e.CaretOffset, e.Top); // the popup's textarea over the table's first line
            if (!NoLiveWrites(target))
            {
                // The table selected the way every live write re-selects it (verified, measured to hold): the caret to the table's end, then back.
                // A direct UIA selection of the whole range was off by a few characters in Notepad and Edge (measured).
                if (!TableReedit.PlaceCaretAtEnd(e)) { Log.Info("table editor: could not put the caret at the table's end, left alone"); return; }
                PumpingSleep(30);
                Log.Info($"table editor: table selected ({SelectBack(tkeys, e.Text)})");
            }
        }
        else return;

        // Written as it changes (the owner's choice): every committed change replaces the table in the app at once, the focus goes there for
        // the paste and comes straight back to the grid. The written table stays selected in the app, so the next change replaces it again.
        // Word / Google Docs get the table once, on close, as a real table: a pasted Word table cannot be selected back by keys.
        // Editors whose text cannot be read back (VS Code and the like) also get it once, on close: no way to keep the written table
        // selected between writes held there (measured), and duplicated tables are worse than writing on close.
        var live = !(typed && target.Kind == TargetKind.Html) && !NoLiveWrites(target);
        var written = text;                       // what the app holds where the table goes
        var changes = new System.Collections.Concurrent.BlockingCollection<string>();
        var userClipboard = live ? session.SaveClipboard() : null;   // given back once, when the editor closes
        var sw = Stopwatch.StartNew();
        long lastKey = 0; var cellEditing = false;   // the user is typing in the editor: no write now (it moves the focus)
        var open = TablePopup.Open(text, caretAt, caret, t => { if (live) changes.Add(t); }, editing => { cellEditing = editing; Interlocked.Exchange(ref lastKey, Environment.TickCount64); });
        while (!open.IsCompleted)
        {
            if (!changes.TryTake(out var next, 30)) { PumpingSleep(1); continue; }
            for (var quiet = Environment.TickCount64 + LiveQuietMs; !open.IsCompleted && (Environment.TickCount64 < quiet || cellEditing || Environment.TickCount64 - Interlocked.Read(ref lastKey) < LiveQuietMs); )
                if (changes.TryTake(out var newer, 20)) { next = newer; quiet = Environment.TickCount64 + LiveQuietMs; }   // write once the user pauses, never mid-cell: a write moves the focus
            written = WriteLive(session, tkeys, target, next.TrimEnd('\n'), written, typed);
        }
        var result = open.GetAwaiter().GetResult();
        if (written != text) session.RestoreClipboard(userClipboard);
        Log.Info($"table editor: popup closed after {sw.ElapsedMilliseconds}ms ({result.Reason}{(result.AsText ? ", edit as text" : "")})");
        reeditMuted = TableReedit.MuteAfterClose(result);
        if (!IsWindow(target.Hwnd)) { Log.Info("table editor: the target window is gone"); return; }

        var final = result.Text.TrimEnd('\n');
        RefocusTarget(tkeys, target);
        if (existing is { } ex && NoLiveWrites(target) && final != written)
        {
            // Select the re-opened table by lines from the caret (still where the user put it): to its first line, then down to its end.
            var row = ex.Text[..Math.Min(ex.CaretOffset, ex.Text.Length)].Count(c => c == '\n');
            var lines = ex.Text.Split('\n').Length;
            tkeys.Type([.. KeyEngine.Tap(Vk.Home), .. Enumerable.Range(0, row).SelectMany(_ => KeyEngine.Tap(Vk.Up)), .. KeyEngine.Tap(Vk.Home),
                new KeyStroke(Vk.LShift, false), .. Enumerable.Range(0, lines - 1).SelectMany(_ => KeyEngine.Tap(Vk.Down)), .. KeyEngine.Tap(Vk.End), new KeyStroke(Vk.LShift, true)]);
            WaitInputDone(target.Hwnd, lines * 4);
        }
        if (final == written)
        {
            if (written == text && typed) session.WriteText(slash.Command, target, TargetKind.Text);   // nothing written: /table goes back, pasted (typed back as keys, Notepad's own "/" handling ate it mid-line: measured)
            else tkeys.Type(KeyEngine.Tap(result.AsText ? Vk.Left : Vk.Right));    // drop the table's selection (Edit as text: caret at its start)
            Log.Info(written == text ? "table editor: nothing changed" : "table editor: done, everything already written");
            return;
        }
        // A new table in Word / Google Docs goes in as a real table when the text is just that table; a re-edited pipe table stays text.
        var kind = typed && MdTable.Find(final, 0) is { } f && f.Start == 0 && f.End == final.Length ? (TargetKind?)null : TargetKind.Text;
        session.WriteText(final, target, kind);
        Log.Info($"table editor: written on close ({final.Split('\n').Length} lines)");
    }

    // One live write: paste `text` over what the app holds for the table (selected), select it again, give the focus back to the popup.
    static string WriteLive(TableSession session, TableKeys tkeys, TableTarget target, string text, string written, bool typed)
    {
        var sw = Stopwatch.StartNew();
        TablePopup.Writing = true;
        try
        {
            session.WriteText(text, target, TargetKind.Text, LiveFocusSettleMs, LivePasteSettleMs, restoreClipboard: false);
            var pasted = sw.ElapsedMilliseconds;
            if (!tkeys.IsForeground(target.Hwnd)) { Log.Info("table editor: live write skipped, the app did not take the focus"); return written; }
            // The caret is at the end of the pasted text: select it back, so the next change replaces it. By characters, not lines: Shift+Up
            // moves by screen line, and in an app that wraps long lines (Notepad) the selection came up short and the next write duplicated
            // the table (measured). UI Automation first (exact, checked against the text); Shift+Left once per character where it is missing.
            var how = SelectBack(tkeys, text);
            WaitInputDone(target.Hwnd, text.Length);   // the app must have taken every key before the focus goes back to the grid
            Log.Info($"table editor: selection after select-back: {SelectedLength()} of {text.Length} chars");
            Log.Info($"table editor: live write steps: pasted at {pasted}ms, selected back ({how}) at {sw.ElapsedMilliseconds}ms");
            return text;
        }
        finally
        {
            TablePopup.Refocus();
            TablePopup.Writing = false;
            ReplayHeldBack(); // keys pressed during the write, now that the editor has the focus again
            Log.Info($"table editor: live write {text.Split('\n').Length} lines in {sw.ElapsedMilliseconds}ms");
        }
    }

    // Selects the text just pasted (the caret is at its end), so the next change replaces it. Measured on real apps:
    //  - Shift+Up per line: apps that wrap long lines (Notepad) move by screen line; the selection came up short.
    //  - ~230 Shift+Left in one burst: Notepad took about 20 of them.
    //  - UI Automation's Select: VS Code's accessible text is a partial copy; the selection never reached its editor.
    // So: UI Automation where the control exposes the whole document, checked by reading the selection back; else Shift+Left in small
    // batches, checked the same way where the app allows it.
    // ponytail: known editors whose text cannot be read back through UI Automation (Monaco / Electron); add others as they turn up.
    static readonly string[] NoLiveWriteApps = ["Code", "Code - Insiders", "Cursor", "VSCodium", "Windsurf"];
    static bool NoLiveWrites(TableTarget t) => NoLiveWriteApps.Contains(t.Process, StringComparer.OrdinalIgnoreCase);

    // A re-opened table whose UIA selection could not be made exact (Edge's textarea, measured): the caret is at the table's start; Shift+Right
    // in small batches, checked by reading the selection back, topped up while short.
    static string SelectForward(TableKeys tkeys, string text)
    {
        System.Windows.Automation.TextPattern? tp = null;
        try { if (System.Windows.Automation.AutomationElement.FocusedElement?.GetCurrentPattern(System.Windows.Automation.TextPattern.Pattern) is System.Windows.Automation.TextPattern p) tp = p; } catch { }
        string? Sel() { try { return tp?.GetSelection() is { Length: > 0 } s ? s[0].GetText(-1).Replace("\r\n", "\n").Replace('\r', '\n') : null; } catch { return null; } }
        void Rights(int count)
        {
            for (var done = 0; done < count; done += 16)
            {
                var n = Math.Min(16, count - done);
                tkeys.Type([new KeyStroke(Vk.LShift, false), .. Enumerable.Range(0, n).SelectMany(_ => KeyEngine.Tap(Vk.Right)), new KeyStroke(Vk.LShift, true)]);
                PumpingSleep(12);
            }
        }
        Rights(text.Length);
        for (var round = 0; round < 3 && tp is not null; round++)
        {
            PumpingSleep(60);
            var got = Sel();
            if (got == text) return round == 0 ? "checked" : $"checked after {round} top-ups";
            if ((got?.Length ?? 0) >= text.Length) break;
            Rights(text.Length - (got?.Length ?? 0));
        }
        return tp is null ? "unchecked" : $"{Sel()?.Length ?? -1} of {text.Length} chars";
    }

    static string SelectBack(TableKeys tkeys, string text)
    {
        System.Windows.Automation.TextPattern? tp = null;
        try
        {
            var el = System.Windows.Automation.AutomationElement.FocusedElement;
            if (el is not null && el.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var p)) tp = (System.Windows.Automation.TextPattern)p;
            for (var attempt = 0; tp is not null && attempt < 5; attempt++, PumpingSleep(40))   // right after a paste the app's accessible text may lag (measured)
            {
                if (!Norm(tp.DocumentRange.GetText(-1)).Contains(text) || tp.GetSelection() is not { Length: > 0 } sel) continue;
                var lines = text.Split('\n').Length;
                foreach (var n in new[] { text.Length, text.Length + lines - 1 })   // a line break is one character, or two (\r\n)
                {
                    var r = sel[0].Clone();
                    r.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start, r, System.Windows.Automation.Text.TextPatternRangeEndpoint.End);
                    r.MoveEndpointByUnit(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start, System.Windows.Automation.Text.TextUnit.Character, -n);
                    if (Norm(r.GetText(-1)) != text) continue;
                    r.Select();
                    if (Selected(tp) == text) return attempt == 0 ? "uia" : $"uia, try {attempt + 1}";
                }
            }
        }
        catch (Exception e) { Log.Info($"table editor: select back through UIA failed: {e.Message}"); tp = null; }
        if (tp is null || !CanCheck(tp, text))
        {
            // Nothing to check the selection against (VS Code: its accessible text is a partial copy). Per line, which is exact in an app that
            // does not wrap (VS Code's default): Shift+Up once per line, then Shift+Home. Shift+Left per character did not hold there (measured).
            var lines = text.Split('\n').Length;
            tkeys.Type([new KeyStroke(Vk.LShift, false), .. Enumerable.Range(0, lines - 1).SelectMany(_ => KeyEngine.Tap(Vk.Up)), .. KeyEngine.Tap(Vk.Home), new KeyStroke(Vk.LShift, true)]);
            return "lines";
        }
        Lefts(tkeys, text.Length);
        for (var round = 0; round < 4; round++)   // apps drop or delay keys under a burst: wait until the selection settles, then correct it either way
        {
            var got = Settled(tp);
            if (got == text.Length && Selected(tp) == text) return round == 0 ? "keys (checked)" : $"keys (checked, {round} corrections)";
            if (got < text.Length) Lefts(tkeys, text.Length - got);
            else Rights(tkeys, got - text.Length);
        }
        return $"keys, but {Selected(tp)?.Length ?? -1} of {text.Length} chars selected";

        static bool CanCheck(System.Windows.Automation.TextPattern tp, string text) { try { return Norm(tp.DocumentRange.GetText(-1)).Contains(text); } catch { return false; } }
        static int Settled(System.Windows.Automation.TextPattern tp)
        {
            var last = -1;
            for (var i = 0; i < 20; i++)
            {
                PumpingSleep(50);
                var now = Selected(tp)?.Length ?? 0;
                if (now == last) return now;
                last = now;
            }
            return last;
        }
        static void Rights(TableKeys tkeys, int count)
        {
            for (var done = 0; done < count; done += 16)
            {
                var n = Math.Min(16, count - done);
                tkeys.Type([new KeyStroke(Vk.LShift, false), .. Enumerable.Range(0, n).SelectMany(_ => KeyEngine.Tap(Vk.Right)), new KeyStroke(Vk.LShift, true)]);
                PumpingSleep(12);
            }
        }
        static void Lefts(TableKeys tkeys, int count)
        {
            for (var done = 0; done < count; done += 16)
            {
                var n = Math.Min(16, count - done);
                tkeys.Type([new KeyStroke(Vk.LShift, false), .. Enumerable.Range(0, n).SelectMany(_ => KeyEngine.Tap(Vk.Left)), new KeyStroke(Vk.LShift, true)]);
                PumpingSleep(12);
            }
        }
        static string Norm(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');
        static string? Selected(System.Windows.Automation.TextPattern tp) { try { return tp.GetSelection() is { Length: > 0 } s ? Norm(s[0].GetText(-1)) : null; } catch { return null; } }
    }

    // Waits until the app has processed the keys sent to it. SendInput returns when the keys are queued, not when the app has handled them; the
    // focus went back to the editor while Notepad, Edge and VS Code were still working through the ~300 Shift+Left of a select-back, the
    // selection came up short (the next write pasted beside the table) and the rest of the keys landed in the grid (measured).
    static void WaitInputDone(IntPtr hwnd, int keys)
    {
        var sw = Stopwatch.StartNew();
        GetWindowThreadProcessId(hwnd, out var pid);
        try { using var p = Process.GetProcessById((int)pid); p.WaitForInputIdle(1500); } catch { }
        PumpingSleep(40 + keys / 8);   // multi-process apps (Chromium, Electron) pass input on to a renderer after the UI thread is idle
        if (sw.ElapsedMilliseconds > 400) Log.Info($"table editor: the app took {sw.ElapsedMilliseconds} ms to take {keys} keys");
    }

    static string SelectedLength()
    {
        try
        {
            var el = System.Windows.Automation.AutomationElement.FocusedElement;
            if (el is not null && el.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var p) && ((System.Windows.Automation.TextPattern)p).GetSelection() is { Length: > 0 } s)
                return s[0].GetText(-1).Length.ToString();
        }
        catch { }
        return "?";
    }

    static string FocusedClass() { try { var e = System.Windows.Automation.AutomationElement.FocusedElement; return $"{e.Current.ControlType.ProgrammaticName} {e.Current.ClassName}"; } catch { return "?"; } }

    static void RefocusTarget(TableKeys tkeys, TableTarget target)
    {
        tkeys.Focus(target.Hwnd);
        PumpingSleep(TableSession.FocusSettleMs);
    }

    const int TypedSettleMs = 60;
    const int LiveQuietMs = 300, LiveFocusSettleMs = 40, LivePasteSettleMs = 120;
    static readonly System.Collections.Concurrent.ConcurrentQueue<KeyStroke> heldBack = new(); // keys the hook held back during a live write

    static void ReplayHeldBack()
    {
        var keys = new List<KeyStroke>();
        while (heldBack.TryDequeue(out var k)) keys.Add(k);
        if (keys.Count == 0) return;
        Inject(() => Send([.. keys]));
        Log.Info($"table editor: replayed {keys.Count(k => !k.Up)} keys typed during the write");
    }
    static Func<bool>? testKeyGuard; // --test-table: keys only to the test's own Notepad tab or the popup

    // ── editing a table again ──
    // As in the original, where putting the caret into a table brought its grid up: after a click or a caret key in another app, the
    // caret's line is read through UI Automation (TableReedit.Read, cheap when the line is no table); in a pipe table the popup opens on
    // it, focused. "Edit as text" mutes that table until the caret has been outside it (the user asked for the raw text).

    static int reeditPending;          // a check is queued: later triggers fold into it
    static long reeditAsked;           // when the last caret key / click asked for one
    static string? reeditMuted;        // the table "Edit as text" was chosen for
    const int ReeditDelayMs = 150;     // after the click / key: the app has moved its caret

    // Hook thread (a click or a caret key the app will see): check the caret's line a moment later. Never blocks.
    static void QueueReeditCheck()
    {
        if (Desk.Sandbox || Volatile.Read(ref tableEditorOpen) == 1) return;
        Interlocked.Exchange(ref reeditAsked, Environment.TickCount64);
        if (Interlocked.Exchange(ref reeditPending, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            // until the caret keys pause (Ctrl+End then Up: the check after the Up, not after the End; measured)
            do Thread.Sleep(ReeditDelayMs); while (Environment.TickCount64 - Interlocked.Read(ref reeditAsked) < ReeditDelayMs);
            Interlocked.Exchange(ref reeditPending, 0);
            var fg = Desk.Foreground();
            if (fg == IntPtr.Zero || IsOurs(fg) || Volatile.Read(ref tableEditorOpen) == 1) return;
            var sw = Stopwatch.StartNew();
            GetWindowThreadProcessId(fg, out var fgPid);
            for (var i = 0; i < 8; i++)   // right after the editor closed, UIA can still report its page as focused (measured: a check 0.5 s after a close read nothing)
            {
                try { if (System.Windows.Automation.AutomationElement.FocusedElement?.Current.ProcessId == (int)fgPid) break; } catch { }
                Thread.Sleep(60);
            }
            var at = TableReedit.Read();
            if (at is null) { reeditMuted = null; if (e2eRealInput) Log.Info($"table reedit: nothing at the caret in {Describe(fg)} (focused: {FocusedClass()})"); return; }
            if (reeditMuted == at.Text) return;
            Log.Info($"table reedit: caret in a {at.Text.Split('\n').Length}-line table in {Describe(fg)} ({sw.ElapsedMilliseconds}ms): opening");
            OpenTableEditor(typed: false, at);
        });
    }

    // Hook thread: caret keys passed through to an app re-check whether the caret is now in a table.
    static void ReeditKey(int vk)
    {
        if (vk is Vk.Left or Vk.Right or Vk.Up or Vk.Down or Vk.Home or Vk.End) QueueReeditCheck();
    }

    // Waits on the editor thread pump messages: it owns the clipboard between the set and the restore, and a target that empties
    // the clipboard sends WM_DESTROYCLIPBOARD to the owner's thread - a sleeping thread held the target's copy for seconds (measured).
    static void PumpingSleep(int ms)
    {
        var end = Environment.TickCount64 + ms;
        do { while (PeekMessage(out var m, IntPtr.Zero, 0, 0, PM_REMOVE)) { TranslateMessage(ref m); DispatchMessage(ref m); } Thread.Sleep(5); } while (Environment.TickCount64 < end);
    }

    // The UIA control that has the focus, when it takes text: its type and name for the log; null when it does not.
    // UIA failing counts as text (the hook's characters were typed somewhere): the feature must not depend on UIA working.
    static string? FocusedTextControl()
    {
        try
        {
            var el = System.Windows.Automation.AutomationElement.FocusedElement;
            if (el is null) return null;
            var ct = el.Current.ControlType;
            var text = ct == System.Windows.Automation.ControlType.Edit || ct == System.Windows.Automation.ControlType.Document
                || el.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out _)
                || el.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out _);
            var what = $"{ct.ProgrammaticName.Replace("ControlType.", "")} \"{el.Current.Name}\" ({el.Current.ClassName})";
            if (!text) Log.Info($"table editor: focused control is {what}");
            return text ? what : null;
        }
        catch (Exception e) { Log.Info($"table editor: UIA focus check failed ({e.Message}), assuming a text control"); return "unknown"; }
    }

    // The real key output: Ctrl+C / Ctrl+V through the input-sender thread (never SendInput from here), lifting the modifiers the user holds.
    sealed class TableKeys(int[] heldAtPress) : ITableKeys
    {
        int[] held = heldAtPress;

        internal void Type(KeyStroke[] strokes)
        {
            if (testKeyGuard is { } guard && !guard()) { Log.Info($"table keys: skipped, the foreground {Describe(Desk.Foreground())} is not the test's"); return; }
            var lifted = held;
            using var done = new ManualResetEventSlim();
            var sw = Stopwatch.StartNew();
            Inject(() =>
            {
                try { Send([.. KeyEngine.Lift(lifted), .. strokes, .. KeyEngine.Restore(lifted, keys.Held)]); }
                finally { done.Set(); }
            });
            var sent = done.Wait(2000);
            Log.Info($"table keys: {Show(strokes)}{(lifted.Length > 0 ? $" (lifted {Show(lifted.Select(v => new KeyStroke(v, true)).ToArray())})" : "")} -> {(sent ? $"sent after {sw.ElapsedMilliseconds}ms" : "NOT SENT in 2s: the input sender is stuck")} to {Describe(Desk.Foreground())}");
            held = []; // only the first output has the keys held at the trigger to lift
        }

        static string Show(KeyStroke[] strokes) => string.Join(" ", strokes.Select(s => $"{Name(s.Vk)}{(s.Up ? "↑" : "↓")}"));
        static string Name(int vk) => vk switch
        {
            Vk.LShift => "Shift", Vk.LCtrl => "Ctrl", Vk.Left => "Left", Vk.Right => "Right", Vk.Back => "Back", Vk.Mask => "Mask",
            >= 'A' and <= 'Z' => ((char)vk).ToString(), _ => $"0x{vk:X2}",
        };

        public void Copy() => Type(KeyEngine.Tap(Vk.LCtrl, 'C'));
        public void Paste() { held = keys.Held; Type(KeyEngine.Tap(Vk.LCtrl, 'V')); }
        public bool Focus(nint hwnd) => Activate(hwnd);
        public bool IsForeground(nint hwnd) => Desk.Foreground() == hwnd;
        public void Backspaces(int n) => Type([.. Enumerable.Range(0, n).SelectMany(_ => KeyEngine.Tap(Vk.Back))]);

        public void TypeText(string s)
        {
            if (testKeyGuard is { } guard && !guard()) return;
            using var done = new ManualResetEventSlim();
            Inject(() => { try { Desk.SendText(s); } finally { done.Set(); } });
            var sent = done.Wait(2000);
            Log.Info($"table keys: text \"{s}\" -> {(sent ? "sent" : "NOT SENT in 2s")} to {Describe(Desk.Foreground())}");
        }
    }
}
