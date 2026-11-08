using AlUmmahNow.App.Navigation;
using Xunit;

namespace AlUmmahNow.App.Tests;

public class TvFocusGridTests
{
    [Fact]
    public void Rtl_left_moves_forward_and_ltr_left_is_blocked_at_start()
    {
        var ltr = new TvFocusGrid(new[] { 3 });
        Assert.False(ltr.Move(DpadKey.Left, false));
        var rtl = new TvFocusGrid(new[] { 3 });
        Assert.True(rtl.Move(DpadKey.Left, true));
        Assert.Equal(1, rtl.Column);
        Assert.False(rtl.Move(DpadKey.Right, true) && rtl.Move(DpadKey.Right, true));
        Assert.Equal(0, rtl.Column);
    }

    [Fact]
    public void Rows_remember_their_column_and_stop_at_edges()
    {
        var g = new TvFocusGrid(new[] { 3, 2 });
        g.Move(DpadKey.Right, false);
        g.Move(DpadKey.Right, false);
        Assert.False(g.Move(DpadKey.Right, false));
        Assert.False(g.Move(DpadKey.Up, false));
        Assert.True(g.Move(DpadKey.Down, false));
        Assert.Equal(0, g.Column);
        g.Move(DpadKey.Up, false);
        Assert.Equal(2, g.Column);
    }

    [Fact]
    public void Empty_grid_never_moves() => Assert.False(new TvFocusGrid(Array.Empty<int>()).Move(DpadKey.Down, false));
}
