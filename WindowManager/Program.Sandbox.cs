using System.Collections.Concurrent;
using System.Diagnostics;
using static WindowManager.Native;

namespace WindowManager;

// Sandbox mode (--sandbox): the manager runs on a hidden desktop and a macro (Sandbox/SandboxRunner.cs) drives it.
// Everything that would reach the real input stream goes through Desk instead, and this file supplies what the
// hidden desktop lacks: a way to run code on the loop thread, window events when Windows raises none, and a
// snapshot of the manager's internal state for the runner to judge.
static partial class Program
{
    static SandboxRunner? sandboxRunner;

    static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // Called first thing in Main. Moves every file this instance writes into the sandbox folder and gives its
    // message window a name the real manager never matches (and so never replaces or recovers).
    static void EnterSandbox(string[] args)
    {
        Desk.EnterSandbox();
        var dir = Arg(args, "--sandbox-dir") ?? Path.Combine(Path.GetTempPath(), "wm-sandbox");
        Directory.CreateDirectory(dir);
        Log.DefaultDir = dir;
        MsgClass = "WindowManagerMsg.sandbox";
    }

    static void StartSandbox(string[] args)
    {
        // Windows raises no foreground event on a desktop with no input: raise the one it would have.
        Desk.ForegroundChanged = h => PostLoop(() => OnWinEventCore(EVENT_SYSTEM_FOREGROUND, h, OBJID_WINDOW, 0));
        new Thread(WinEventPump) { IsBackground = true, Name = "sandbox-winevents" }.Start();

        var macro = Arg(args, "--macro");
        var fixture = Arg(args, "--fixture");
        if (macro is null || fixture is null) { Log.Info("sandbox: --macro and --fixture are required"); PostMessage(msgWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); return; }
        sandboxRunner = new SandboxRunner(macro, Log.DefaultDir, fixture, msgWnd);
        new Thread(sandboxRunner.Run) { IsBackground = true, Name = "sandbox-macro" }.Start();
    }

    // After the loop ended and the manager restored every window: judge what it left behind, finish the report.
    static void FinishSandbox() => sandboxRunner?.Finish();

    // ---- running things on the loop thread ----

    static readonly ConcurrentQueue<Action> loopQueue = new();

    internal static void PostLoop(Action a)
    {
        loopQueue.Enqueue(a);
        PostMessage(msgWnd, WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
    }

    static void DrainLoopQueue()
    {
        while (loopQueue.TryDequeue(out var a))
            try { a(); } catch (Exception e) { Log.Info($"loop invoke error: {e}"); }
    }

    // Run f on the loop thread and wait for it: how the runner reads manager state safely. The time it took to get
    // there IS the loop's responsiveness, so callers that care measure around it.
    internal static T OnLoop<T>(Func<T> f, int timeoutMs = 20000)
    {
        T result = default!;
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        PostLoop(() => { try { result = f(); } catch (Exception e) { error = e; } finally { done.Set(); } });
        if (!done.Wait(timeoutMs)) throw new TimeoutException($"the manager's loop did not answer within {timeoutMs}ms");
        if (error is not null) throw new InvalidOperationException(error.Message, error);
        return result;
    }

    internal static bool LoopIdle => loopQueue.IsEmpty;

    // ---- keys ----

    static readonly ConcurrentQueue<long> keyStamps = new();
    internal static readonly ConcurrentQueue<(long Post, long Dispatch)> KeyDispatches = new();

    // A physical key event through the real path (SimulateKey -> KeyEngine.Feed -> Run), stamped so the time it
    // waited for the loop is measured.
    internal static void SandboxKey(int vk, bool down)
    {
        keyStamps.Enqueue(Stopwatch.GetTimestamp());
        PostMessage(msgWnd, WM_APP_TESTKEY, vk, down ? 1 : 0);
    }

    static void NoteKeyDispatched()
    {
        if (keyStamps.TryDequeue(out var posted)) KeyDispatches.Enqueue((posted, Stopwatch.GetTimestamp()));
    }

    // The real hook swallows a key the engine decided on (and injects mask + release for a masked modifier); otherwise
    // the physical key reaches the application. Recording which, in order, lets the runner replay what the app saw.
    static void SandboxKeyOutcome(int vk, bool down, Decision d)
    {
        if (d.Suppress || d.MaskFirst) Desk.RecordSwallow(vk);
        if (d.MaskFirst) Inject(() => Send(KeyEngine.MaskedRelease(vk)));
        else if (!d.Suppress) Desk.RecordPass(vk, !down);
    }

    // ---- window events ----

    static readonly ConcurrentDictionary<uint, int> realEvents = new();
    static int syntheticEvents;
    static void CountRealEvent(uint evt) => realEvents.AddOrUpdate(evt, 1, (_, n) => n + 1);
    internal static (int Show, int Location, int Destroy, int Foreground, int Synthetic) EventCounts =>
        (realEvents.GetValueOrDefault(EVENT_OBJECT_SHOW), realEvents.GetValueOrDefault(EVENT_OBJECT_LOCATIONCHANGE),
         realEvents.GetValueOrDefault(EVENT_OBJECT_DESTROY), realEvents.GetValueOrDefault(EVENT_SYSTEM_FOREGROUND), Volatile.Read(ref syntheticEvents));

    // Polls the desktop's windows and raises show / move / destroy for changes. Duplicates of real events are
    // harmless (managing is idempotent and moves coalesce); if Windows raises none on this desktop, this is all there is.
    static void WinEventPump()
    {
        var seen = new Dictionary<IntPtr, Rect>();
        while (true)
        {
            Thread.Sleep(12);
            var now = new Dictionary<IntPtr, Rect>();
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || IsOurs(h)) return true;
                if (GetWindowRect(h, out var r)) now[h] = r;
                return true;
            }, IntPtr.Zero);
            foreach (var (h, r) in now)
            {
                if (!seen.TryGetValue(h, out var old)) Raise(EVENT_OBJECT_SHOW, h);
                else if (old != r) Raise(EVENT_OBJECT_LOCATIONCHANGE, h);
            }
            foreach (var h in seen.Keys) if (!now.ContainsKey(h) && !IsWindow(h)) Raise(EVENT_OBJECT_DESTROY, h);
            seen = now;
        }
    }

    static void Raise(uint evt, IntPtr h)
    {
        Interlocked.Increment(ref syntheticEvents);
        PostLoop(() => OnWinEventCore(evt, h, OBJID_WINDOW, 0));
    }

    // ---- snapshot ----

    internal sealed record MgrWin(long Hwnd, string Proc, bool Managed, bool Lie, Rect? Slot, bool Revealed, bool AwaitingZone, bool NotCapable, bool Probing, bool InOp);
    internal sealed record MgrSnap(long Foreground, MgrWin[] Windows, string[] NotQuiet, string[] Leaks, bool HintsActive, string HintsTyped, int HintTargets,
        string[] CapableProcs, string[] NotCapableProcs, bool LieOn);

    // Loop thread only. NotQuiet names every reason the manager is still busy; empty = idle.
    internal static MgrSnap TakeSnapshot()
    {
        var reasons = new List<string>();
        if (Volatile.Read(ref opsInFlight) > 0) reasons.Add($"ops in flight ({opsInFlight})");
        if (outbox.Count > 0) reasons.Add($"input-sender queue ({outbox.Count})");
        if (opbox.Count > 0) reasons.Add($"worker queue ({opbox.Count})");
        if (!lieResults.IsEmpty) reasons.Add("lie results waiting");
        if (probing.Count > 0) reasons.Add($"probe running ({probing.Count})");
        if (pendingSnap.Count > 0) reasons.Add("snap coalesced behind an op");
        if (pendingToggle.Count > 0) reasons.Add("reveal press remembered");
        if (pendingFg != IntPtr.Zero) reasons.Add("activation debounce");
        if (movedWindows.Count > 0) reasons.Add("window moves not handled yet");
        if (hintScanning) reasons.Add("hint scan running");
        if (Volatile.Read(ref hintPrefetching) == 1 || hintWanted) reasons.Add("hint prefetch running");
        if (settling) reasons.Add("settling (display change)");
        if (awaitingZone.Count > 0) reasons.Add("waiting for a Snap Layouts pick");
        if (cropRetries.Count > 0) reasons.Add("crop retry pending");
        if (loopQueue.Count > 0) reasons.Add("loop queue");
        var leaks = new List<string>();
        foreach (var h in lie.Where(h => !IsWindow(h))) leaks.Add($"0x{h:X} is gone but still in the fullscreen set");
        foreach (var h in lieSlot.Keys.Where(h => !IsWindow(h))) leaks.Add($"0x{h:X} is gone but still has a slot");
        foreach (var h in managed.Keys.Where(h => !IsWindow(h))) leaks.Add($"0x{h:X} is gone but still managed");
        foreach (var h in lie.Where(h => !managed.ContainsKey(h))) leaks.Add($"0x{h:X} is fullscreen-in-slot but not managed");
        var wins = managed.Keys.Where(IsWindow).Select(h => new MgrWin(h, ProcessName(h), true, lie.Contains(h),
            lieSlot.TryGetValue(h, out var s) ? s : null, revealed.Contains(h), awaitingZone.ContainsKey(h),
            notCapable.Contains(ProcessName(h)), probing.Contains(h), OpActive(h))).ToArray();
        return new MgrSnap(Desk.Foreground(), wins, [.. reasons], [.. leaks], hints.Active, hints.Typed, hintTargets.Count,
            [.. capable], [.. notCapable], lieOn);
    }
}
