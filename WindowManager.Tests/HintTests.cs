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
    public void Labels_become_pairs_beyond_the_alphabet() // first letter = group, like the web version
    {
        var l = HintEngine.LabelsFor(27);
        Assert.Equal("aa", l[0]);
        Assert.Equal("as", l[1]);
        Assert.Equal("sa", l[25]);
        Assert.Equal(27, l.Distinct().Count());
    }

    [Fact]
    public void No_label_ever_contains_f() // F toggles hint mode
    {
        Assert.DoesNotContain(HintEngine.LabelsFor(200), l => l.Contains('f'));
    }

    [Fact]
    public void Typing_narrows_and_dims_the_rest()
    {
        var h = new HintEngine(); h.Show(30);          // pairs: aa as ad ...
        Assert.Equal(HintEngine.Step.Narrowed, h.Type('a', false).Step);
        Assert.True(h.Active);
        Assert.False(h.Dim("as"));                      // still possible
        Assert.True(h.Dim("sa"));                       // ruled out
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
