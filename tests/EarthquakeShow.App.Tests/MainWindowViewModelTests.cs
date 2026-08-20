using EarthquakeShow.App.ViewModels;
using EarthquakeShow.Core.Models;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task Initialize_FixedJmaXml_LoadsMergedCorrectionEvent()
    {
        using var viewModel = new MainWindowViewModel();

        await viewModel.InitializeAsync();

        EarthquakeEvent earthquakeEvent = Assert.Single(viewModel.EarthquakePage.State.Events);
        Assert.Equal(4, earthquakeEvent.Reports.Length);
        Assert.Equal(ReportStatus.Correction, earthquakeEvent.Summary?.Status);
        Assert.Equal(3.9, earthquakeEvent.Summary?.Magnitude?.Value);
        Assert.Equal(75, earthquakeEvent.LatestReport?.IntensityStations.Length);
        Assert.All(
            earthquakeEvent.LatestReport!.IntensityStations,
            station => Assert.NotNull(station.Coordinate));
        Assert.Single(viewModel.EventList.Items);
        Assert.Equal(76, viewModel.Map.Markers.Count);
    }
}
