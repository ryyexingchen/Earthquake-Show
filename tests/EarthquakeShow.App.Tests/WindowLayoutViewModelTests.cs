using EarthquakeShow.App.ViewModels;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class WindowLayoutViewModelTests
{
    [Fact]
    public void UpdateWidth_CompactLayout_UsesClosableDetailsDrawer()
    {
        var layout = new WindowLayoutViewModel();

        layout.UpdateWidth(WindowLayoutViewModel.CompactWidthThreshold);
        Assert.False(layout.IsCompactLayout);
        Assert.True(layout.IsDetailsPaneVisible);

        layout.UpdateWidth(WindowLayoutViewModel.CompactWidthThreshold - 1);
        Assert.True(layout.IsCompactLayout);
        Assert.False(layout.IsDetailsPaneVisible);

        layout.OpenDetailsPane();
        Assert.True(layout.IsDetailsPaneVisible);

        layout.CloseDetailsPane();
        Assert.False(layout.IsDetailsPaneVisible);

        layout.UpdateWidth(1440);
        Assert.False(layout.IsCompactLayout);
        Assert.True(layout.IsDetailsPaneVisible);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UpdateWidth_InvalidWidth_IsRejected(double width)
    {
        var layout = new WindowLayoutViewModel();

        Assert.Throws<ArgumentOutOfRangeException>(() => layout.UpdateWidth(width));
    }
}
