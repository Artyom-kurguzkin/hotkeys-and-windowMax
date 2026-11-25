using WindowManager;

namespace WindowManager.Tests;

// HintEngine mirrors repo-task-manager's app.js `_hints`; these pin the parts that are easy to get subtly wrong.
public class HintTests
{
    [Fact]
    public void Labels_are_single_letters_while_they_last()
    {
        Assert.Equal(["a", "s", "d"], HintEngine.LabelsFor(3));
        Assert.Equal(25, HintEngine.LabelsFor(25).Length);
        Assert.All(HintEngine.LabelsFor(25), l => Assert.Equal(1, l.Length));
    }

    [Fact]
    public void Beyond_the_alphabet_single_letters_come_first_then_pairs()
    {
        var l = HintEngine.LabelsFor(27);   // 24 singles, then pairs on the 25th letter
        Assert.Equal("a", l[0]);
        Assert.Equal("n", l[23]);
        Assert.Equal("ma", l[24]);
        Assert.Equal("ms", l[25]);
        foreach (var n in new[] { 26, 27, 50, 100, 200, 400, 625 })
        {
            var ls = HintEngine.LabelsFor(n);
            Assert.Equal(n, ls.Distinct().Count());
            Assert.True(ls.Count(x => x.Length == 1) > 0 || n > 600, $"{n}: no single letters");
            Assert.DoesNotContain(ls, a => ls.Any(b => b != a && b.StartsWith(a))); // no label is a prefix of another
            Assert.Equal(ls.OrderBy(x => x.Length).ToArray(), ls);                 // singles first
        }
    }

    [Fact]
    public void No_label_ever_contains_f() // F toggles hint mode
    {
        Assert.DoesNotContain(HintEngine.LabelsFor(200), l => l.Contains('f'));
    }

    [Fact]
    public void Typing_narrows_and_dims_the_rest()
    {
        var h = new HintEngine(); h.Show(30);          // 24 singles, then ma ms md ... (m is a group)
        Assert.Equal(HintEngine.Step.Narrowed, h.Type('m', false).Step);
        Assert.True(h.Active);
        Assert.False(h.Dim("ms"));                      // still possible
        Assert.True(h.Dim("a"));                        // ruled out
    }

    [Fact]
    public void A_full_label_picks_and_hides()
    {
        var h = new HintEngine(); h.Show(3);
        var (step, i) = h.Type('S', false);             // case-insensitive, like e.key.toLowerCase()
        Assert.Equal(HintEngine.Step.Pick, step);
        Assert.Equal(1, i);
        Assert.False(h.Active);
    }

    [Fact]
    public void Shift_pick_keeps_the_badges_up_for_the_next_one()
    {
        var h = new HintEngine(); h.Show(3);
        var (step, i) = h.Type('d', shift: true);
        Assert.Equal(HintEngine.Step.PickKeep, step);
        Assert.Equal(2, i);
        Assert.True(h.Active);
        Assert.Equal("", h.Typed);
        Assert.Equal((HintEngine.Step.PickKeep, 0), h.Type('a', true)); // a second pick straight away
    }

    [Fact]
    public void Nonsense_resets_instead_of_getting_stuck()
    {
        var h = new HintEngine(); h.Show(3);            // a s d
        Assert.Equal(HintEngine.Step.Reset, h.Type('x', false).Step);
        Assert.Equal("", h.Typed);
        Assert.True(h.Active);
        Assert.Equal(HintEngine.Step.Pick, h.Type('a', false).Step); // works again after the reset
    }
}
