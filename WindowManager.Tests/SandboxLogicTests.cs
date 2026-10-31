namespace WindowManager.Tests;

// The rules the sandbox runner judges with. A wrong rule here would send the troubleshooting after phantom defects,
// so each one is pinned with a case that must pass and one that must be reported.
public class SandboxLogicTests
{
    static readonly Rect Mon = new(0, 0, 1920, 1200);
    static readonly Rect Work = new(0, 0, 1920, 1160);
    static Rect Left => new(0, 0, 960, 1200);
    static Rect Right => new(960, 0, 1920, 1200);

    static WinFacts Win(string name, long h, Rect rect, bool lie = true, Rect? slot = null, bool? appFs = true, bool iconic = false,
        bool revealed = false, bool awaiting = false, Rect? frame = null, bool zoomed = false) =>
        new(h, name, "std", rect, frame, true, iconic, zoomed, true, lie, lie ? slot ?? rect : null, revealed, awaiting, false, appFs);

    static WorldFacts World(long fg, long actor, params WinFacts[] w) => new(Mon, Work, fg, actor, w, []);
    static string[] Ids(IEnumerable<Violation> v) => [.. v.Select(x => x.Id)];

    // ---- chords ----

    [Theory]
    [InlineData("Win+Left", new[] { Vk.LWin, Vk.Left })]
    [InlineData("Alt+]", new[] { Vk.LAlt, Vk.OemClose })]
    [InlineData("Ctrl+Alt+Shift+Q", new[] { Vk.LCtrl, Vk.LAlt, Vk.LShift, 'Q' })]
    [InlineData("RAlt+X", new[] { Vk.RAlt, 'X' })]
    [InlineData("PgUp", new[] { Vk.PgUp })]
    [InlineData("F23", new[] { Vk.F23 })]
    public void Chord_parses_in_press_order(string chord, int[] vks) => Assert.Equal(vks, Chord.Parse(chord));

    [Fact] public void Chord_rejects_unknown_names() => Assert.Throws<FormatException>(() => Chord.Parse("Win+Bogus"));
    [Fact] public void Chord_names_come_back_readable() => Assert.Equal("Home", Chord.NameOf(Vk.Home));
    [Fact] public void Chord_function_keys() { Assert.Equal(0x7A, Chord.One("F11")); Assert.Equal("F11", Chord.NameOf(0x7A)); }

    // ---- macro text ----

    [Fact]
    public void Macro_parses_verbs_and_drops_comments_and_blank_lines()
    {
        var cmds = MacroParser.Parse("# top\n\nkey Win+Left   # tile\n  expect A at left\n");
        Assert.Equal(2, cmds.Count);
        Assert.Equal("key", cmds[0].Verb);
        Assert.Equal(["Win+Left"], cmds[0].Args);
        Assert.Equal(4, cmds[1].Line);
        Assert.Equal(["A", "at", "left"], cmds[1].Args);
    }

    [Fact]
    public void Macro_for_repeats_its_body_and_substitutes()
    {
        var cmds = MacroParser.Expand(MacroParser.Parse("for k in J,K\nkey Alt+$k\nendfor\nsettle"));
        Assert.Equal(["key Alt+J", "key Alt+K", "settle"], cmds.Select(c => c.Raw));
    }

    [Fact]
    public void Macro_for_nests()
    {
        var cmds = MacroParser.Expand(MacroParser.Parse("for m in a,b\nfor s in 1,2\nspawn $m$s\nendfor\nendfor"));
        Assert.Equal(["spawn a1", "spawn a2", "spawn b1", "spawn b2"], cmds.Select(c => c.Raw));
    }

    [Fact] public void Macro_for_without_endfor_is_an_error() => Assert.Throws<FormatException>(() => MacroParser.Expand(MacroParser.Parse("for x in a,b\nkey A")));

    [Fact]
    public void Macro_substitution_prefers_the_longer_name()
    {
        var cmds = MacroParser.Expand(MacroParser.Parse("for m in x\nfor mode in y\nnote $mode $m\nendfor\nendfor"));
        Assert.Equal("note y x", cmds.Single().Raw);
    }

    // ---- statistics ----

    [Fact]
    public void Percentiles_interpolate()
    {
        var s = Stats.Summarize([10, 20, 30, 40, 50]);
        Assert.Equal(5, s.Count);
        Assert.Equal(30, s.P50);
        Assert.Equal(48, s.P95, 3);
        Assert.Equal(50, s.Max);
    }

    [Fact] public void Percentiles_of_nothing_are_zero() => Assert.Equal(new Summary(0, 0, 0, 0, 0), Stats.Summarize([]));

    // ---- trajectories ----

    const double Tpm = 1000;   // 1000 ticks per ms keeps the numbers readable
    static Sample S(long ms, Rect r, WinFlag f = WinFlag.Visible) => new((long)(ms * Tpm), 1, r, f);

    [Fact]
    public void A_window_that_never_moves_has_one_state()
    {
        var tr = Trajectories.Analyze(1, [S(-50, Left)], 0, 500_000, Mon, Tpm);
        Assert.False(tr.Changed);
        Assert.Equal(1, tr.Distinct);
    }

    [Fact]
    public void A_direct_move_has_two_states_and_no_transient()
    {
        var tr = Trajectories.Analyze(1, [S(-50, Mon), S(40, Left)], 0, 500_000, Mon, Tpm);
        Assert.Equal(2, tr.Distinct);
        Assert.Empty(tr.Transients);
        Assert.Equal(40, tr.FirstChangeMs, 3);
        Assert.Equal(40, tr.LastChangeMs, 3);
    }

    [Fact]
    public void Leaving_fullscreen_and_coming_back_shows_a_visible_in_between_state()
    {
        var normal = new Rect(120, 90, 940, 670);
        var tr = Trajectories.Analyze(1,
            [S(-50, Mon, WinFlag.Visible), S(60, normal, WinFlag.Visible | WinFlag.Caption | WinFlag.Frame), S(260, Left, WinFlag.Visible)],
            0, 500_000, Mon, Tpm);
        Assert.Equal(3, tr.Distinct);
        var t = Assert.Single(tr.Transients);
        Assert.Equal(normal, t.Rect);
        Assert.Equal(200, t.DurMs, 3);
        Assert.Equal(200, tr.MaxTransientMs, 3);
        Assert.Equal(2, tr.StyleFlips);   // the chrome came back for the in-between state, then went away again at the end
    }

    [Fact]
    public void Style_flips_count_each_chrome_change()
    {
        var chrome = WinFlag.Visible | WinFlag.Caption | WinFlag.Frame;
        var tr = Trajectories.Analyze(1, [S(-1, Mon, WinFlag.Visible), S(10, Mon, chrome), S(20, Left, WinFlag.Visible)], 0, 100_000, Mon, Tpm);
        Assert.Equal(2, tr.StyleFlips);
    }

    [Fact]
    public void Samples_after_the_window_are_ignored_and_before_it_set_the_starting_state()
    {
        var tr = Trajectories.Analyze(1, [S(-500, Mon), S(-100, Left), S(900, Right)], 0, 500_000, Mon, Tpm);
        Assert.Equal(1, tr.Distinct);
        Assert.Equal(Left, tr.States[0].Rect);
    }

    [Fact]
    public void Overshoot_is_how_far_outside_the_monitor_it_went()
    {
        var tr = Trajectories.Analyze(1, [S(-1, Mon), S(10, new Rect(-30, 0, 1920, 1200)), S(20, Mon)], 0, 100_000, Mon, Tpm);
        Assert.Equal(30, tr.MaxOvershootPx);
    }

    [Fact]
    public void A_window_that_started_outside_the_monitor_is_not_blamed_for_it()
    {
        var tr = Trajectories.Analyze(1, [S(-1, new Rect(-200, -150, 1900, 1200)), S(10, Mon)], 0, 100_000, Mon, Tpm);
        Assert.Equal(0, tr.MaxOvershootPx);
    }

    [Fact]
    public void An_unbound_chord_reaches_the_app_as_the_real_chord_with_its_release()
    {
        var stream = new List<AppKey> { P(Vk.LAlt, false), P(0x09, false), P(0x09, true), P(Vk.LAlt, true) };
        Assert.Empty(AppView.Check(stream));
    }

    [Fact]
    public void A_swallowed_key_in_one_Alt_tap_does_not_condemn_the_taps_around_it()
    {
        List<AppKey> stream = [P(Vk.LAlt, false), P(Vk.LAlt, true), P(Vk.LAlt, false), S(Vk.LAlt), .. Inj(KeyEngine.MaskedRelease(Vk.LAlt))];
        Assert.Empty(AppView.Check(stream));
    }

    [Fact]
    public void A_minimized_state_is_not_an_overshoot()
    {
        var tr = Trajectories.Analyze(1, [S(-1, Mon), S(10, new Rect(-32000, -32000, -31840, -31970), WinFlag.Visible | WinFlag.Iconic)], 0, 100_000, Mon, Tpm);
        Assert.Equal(0, tr.MaxOvershootPx);
        Assert.Equal(1, tr.VisibilityFlips);
    }

    // ---- invariants ----

    [Fact] public void Fullscreen_window_filling_its_slot_is_clean() =>
        Assert.Empty(Invariants.Check(World(1, 1, Win("A", 1, Left))));

    [Fact] public void Fullscreen_window_off_its_slot_is_reported() =>
        Assert.Contains("lie-fills-slot", Ids(Invariants.Check(World(1, 1, Win("A", 1, new Rect(0, 0, 900, 1200), slot: Left)))));

    [Fact] public void Slot_outside_the_monitor_is_reported() =>
        Assert.Contains("slot-on-screen", Ids(Invariants.Check(World(1, 1, Win("A", 1, new Rect(1800, 0, 2400, 1200))))));

    [Fact] public void Empty_slot_is_reported() =>
        Assert.Contains("slot-on-screen", Ids(Invariants.Check(World(1, 1, Win("A", 1, new Rect(3000, 0, 3000, 100))))));

    [Fact] public void Manager_and_app_disagreeing_about_fullscreen_is_reported() =>
        Assert.Contains("app-fullscreen-matches-lie", Ids(Invariants.Check(World(1, 1, Win("A", 1, Left, appFs: false)))));

    [Fact] public void App_fullscreen_but_unmanaged_is_reported() =>
        Assert.Contains("app-fullscreen-matches-lie", Ids(Invariants.Check(World(1, 1, Win("A", 1, Mon, lie: false, appFs: true)))));

    [Fact] public void Unknown_app_state_is_not_judged() =>
        Assert.Empty(Invariants.Check(World(1, 1, Win("A", 1, Left, appFs: null))));

    [Fact] public void A_pending_snap_layouts_pick_excuses_the_disagreement() =>
        Assert.Empty(Invariants.Check(World(1, 1, Win("A", 1, new Rect(0, 0, 800, 600), lie: false, appFs: true, awaiting: true))));

    [Fact] public void Minimized_while_the_app_is_not_fullscreen_but_counted_fullscreen_is_reported() =>
        Assert.Contains("iconic-lie-mismatch", Ids(Invariants.Check(World(0, 1, Win("A", 1, Left, iconic: true, appFs: false)))));

    [Fact] public void Minimized_from_outside_while_the_app_stays_fullscreen_is_consistent() =>
        Assert.DoesNotContain("iconic-lie-mismatch", Ids(Invariants.Check(World(0, 1, Win("A", 1, Left, iconic: true, appFs: true)))));

    [Fact] public void Both_fullscreen_and_revealed_is_reported() =>
        Assert.Contains("lie-and-revealed", Ids(Invariants.Check(World(1, 1, Win("A", 1, Left, revealed: true)))));

    [Fact]
    public void Left_and_right_tiles_that_meet_are_clean() =>
        Assert.Empty(Invariants.Check(World(2, 2, Win("A", 1, Left), Win("B", 2, Right))));

    [Fact]
    public void A_gap_between_tiles_is_reported()
    {
        var v = Invariants.Check(World(2, 2, Win("A", 1, new Rect(0, 0, 940, 1200), slot: Left), Win("B", 2, Right)));
        Assert.Contains("tiles-adjoin", Ids(v));
        Assert.Contains("gap", v.First(x => x.Id == "tiles-adjoin").Message);
    }

    [Fact]
    public void An_overlap_between_tiles_is_reported()
    {
        var v = Invariants.Check(World(2, 2, Win("A", 1, new Rect(0, 0, 1000, 1200), slot: Left), Win("B", 2, Right)));
        Assert.Contains("overlap", v.First(x => x.Id == "tiles-adjoin").Message);
    }

    [Fact] public void Two_windows_stacked_in_the_same_half_are_not_a_tile_pair() =>
        Assert.Empty(Invariants.Check(World(2, 2, Win("A", 1, Left), Win("B", 2, Left))));

    [Fact] public void The_wrong_window_holding_the_focus_is_reported() =>
        Assert.Contains("foreground-is-actor", Ids(Invariants.Check(World(2, 1, Win("A", 1, Left), Win("B", 2, Right)))));

    [Fact] public void A_minimized_actor_does_not_need_the_focus() =>
        Assert.Empty(Invariants.Check(World(0, 1, Win("A", 1, new Rect(-32000, -32000, -31840, -31970), lie: false, appFs: false, iconic: true))));

    [Fact]
    public void Leaked_state_is_reported() =>
        Assert.Contains("no-orphan-state", Ids(Invariants.Check(new WorldFacts(Mon, Work, 0, 0, [], ["0x10 is gone but still has a slot"]))));

    // ---- documented tiling behaviour ----

    [Theory]
    [InlineData("Win+Left", 0, 0, 960, 1200)]
    [InlineData("Win+Right", 960, 0, 1920, 1200)]
    [InlineData("Win+Up", 0, 0, 1920, 1200)]
    public void A_fullscreen_window_tiles_within_the_monitor(string chord, int l, int t, int r, int b)
    {
        var before = Win("A", 1, Mon);
        var good = Win("A", 1, new Rect(l, t, r, b));
        Assert.Empty(TilingSpec.AfterKey(chord, before, good, Mon, Work));
        var wrong = Win("A", 1, Mon, slot: new Rect(100, 0, 800, 1200));
        Assert.NotEmpty(TilingSpec.AfterKey(chord, before, wrong with { Rect = new Rect(100, 0, 800, 1200) }, Mon, Work));
    }

    [Fact]
    public void A_tile_key_that_drops_fullscreen_is_reported()
    {
        var v = TilingSpec.AfterKey("Win+Left", Win("A", 1, Mon), Win("A", 1, Left, lie: false, appFs: false), Mon, Work);
        Assert.Contains("tile-keeps-fullscreen", Ids(v));
    }

    [Fact]
    public void A_normal_window_tiles_within_the_work_area_judged_by_its_visible_frame()
    {
        var before = Win("A", 1, new Rect(100, 100, 900, 700), lie: false, appFs: false);
        var halfWork = new Rect(0, 0, 960, 1160);
        var after = Win("A", 1, new Rect(-7, 0, 967, 1167), lie: false, appFs: false, frame: halfWork);
        Assert.Empty(TilingSpec.AfterKey("Win+Left", before, after, Mon, Work));
        var gap = after with { Frame = new Rect(0, 0, 940, 1160) };
        Assert.Contains("tile-rect", Ids(TilingSpec.AfterKey("Win+Left", before, gap, Mon, Work)));
    }

    [Fact] public void Win_Down_must_minimize() =>
        Assert.Contains("minimize", Ids(TilingSpec.AfterKey("Win+Down", Win("A", 1, Mon), Win("A", 1, Mon), Mon, Work)));

    [Fact] public void Win_Down_that_minimized_is_clean() =>
        Assert.Empty(TilingSpec.AfterKey("Win+Down", Win("A", 1, Mon), Win("A", 1, new Rect(-32000, -32000, -31840, -31970), lie: false, appFs: false, iconic: true), Mon, Work));

    [Fact] public void Win_Up_on_a_normal_window_must_maximize() =>
        Assert.Contains("maximize", Ids(TilingSpec.AfterKey("Win+Up", Win("A", 1, new Rect(100, 100, 900, 700), lie: false, appFs: false), Win("A", 1, new Rect(100, 100, 900, 700), lie: false, appFs: false), Mon, Work)));

    // ---- what the application sees on its keyboard ----

    static AppKey P(int vk, bool up) => new(0, vk, up, Src.Pass);
    static AppKey I(int vk, bool up) => new(0, vk, up, Src.Injected);
    static AppKey S(int vk) => new(0, vk, false, Src.Swallowed);   // a key the manager took for itself
    static AppKey[] Inj(params KeyStroke[] s) => [.. s.Select(k => I(k.Vk, k.Up))];

    // Exactly what the manager emits for Alt+] with Alt still held then released: lift (mask, Alt up), End, then masked release.
    static List<AppKey> AltBracket() =>
    [
        P(Vk.LAlt, false), S(Vk.OemClose),
        .. Inj(KeyEngine.Lift([Vk.LAlt])), .. Inj(KeyEngine.Output(Act.End)),
        .. Inj(KeyEngine.MaskedRelease(Vk.LAlt)),
    ];

    [Fact] public void The_managers_Alt_chord_output_is_clean() => Assert.Empty(AppView.Check(AltBracket()));

    [Fact]
    public void A_chord_whose_Alt_release_is_not_masked_would_open_the_menu()
    {
        var stream = new List<AppKey> { P(Vk.LAlt, false), S(Vk.OemClose), I(Vk.End, false), I(Vk.End, true), P(Vk.LAlt, true) };
        Assert.Contains("unmasked-release", Ids(AppView.Check(stream)));
    }

    [Fact]
    public void A_plain_Alt_tap_is_not_judged_when_nothing_was_chorded()
    {
        var stream = new List<AppKey> { P(Vk.LAlt, false), P(Vk.LAlt, true) };
        Assert.Empty(AppView.Check(stream));
    }

    [Fact]
    public void Output_typed_while_the_users_Alt_is_still_down_is_reported()
    {
        List<AppKey> stream = [P(Vk.LAlt, false), I(Vk.Home, false), I(Vk.Home, true), .. Inj(KeyEngine.MaskedRelease(Vk.LAlt))];
        Assert.Contains("output-under-modifier", Ids(AppView.Check(stream)));
    }

    [Fact]
    public void The_managers_own_Win_chord_for_desktop_switching_is_not_output_under_a_modifier()
    {
        var stream = new List<AppKey>(Inj(KeyEngine.Output(Act.DesktopLeft)));
        Assert.Empty(AppView.Check(stream));
    }

    [Fact]
    public void A_key_left_down_is_reported()
    {
        var stream = new List<AppKey> { I(Vk.LShift, false), I(Vk.Home, false), I(Vk.Home, true) };
        Assert.Contains("stuck-key", Ids(AppView.Check(stream)));
    }

    [Fact]
    public void A_key_still_physically_held_is_not_stuck()
    {
        var stream = new List<AppKey> { P(Vk.LShift, false) };
        Assert.Empty(AppView.Check(stream, physicalDownAtEnd: [Vk.LShift]));
    }

    [Fact]
    public void A_non_modifier_release_without_a_press_is_reported_but_a_repeated_modifier_release_is_not()
    {
        Assert.Contains("release-without-press", Ids(AppView.Check([I(Vk.End, true)])));
        Assert.Empty(AppView.Check([P(Vk.LAlt, false), I(Vk.LAlt, true), I(Vk.LAlt, true)]));
    }

    [Fact]
    public void Counting_taps_only_counts_injected_down_up_pairs()
    {
        var stream = new List<AppKey> { I(Vk.End, false), I(Vk.End, true), P(Vk.End, false), P(Vk.End, true), I(Vk.End, false), I(Vk.End, true) };
        Assert.Equal(2, Emissions.CountTaps(stream, Vk.End));
        Assert.Equal(0, Emissions.CountTaps(stream, Vk.Home));
    }
}
