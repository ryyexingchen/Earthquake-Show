using EarthquakeShow.App.Services;
using EarthquakeShow.App.ViewModels;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task ApplyAsync_PassesDraftToCallbackAndClearsDirtyState()
    {
        WebSocketConnectionSettings? applied = null;
        var viewModel = new SettingsViewModel(
            new ApplicationSettingsLoadResult(ApplicationSettings.Default, null),
            settings =>
            {
                applied = settings;
                return Task.CompletedTask;
            });

        viewModel.KeepAliveSeconds = 60;
        viewModel.MaxConnectionDurationMinutes = 8;
        Assert.True(viewModel.IsDirty);

        await viewModel.ApplyAsync();

        Assert.Equal(new WebSocketConnectionSettings(60, 8), applied);
        Assert.False(viewModel.IsDirty);
        Assert.Equal("已保存连接策略设置", viewModel.StatusText);
    }

    [Fact]
    public void RestoreDefaults_MarksDraftAsDirtyUntilApplied()
    {
        var viewModel = new SettingsViewModel(
            new ApplicationSettingsLoadResult(
                new ApplicationSettings(
                    WebSocket: new WebSocketConnectionSettings(60, 8)),
                null),
            _ => Task.CompletedTask);

        viewModel.RestoreDefaults();

        Assert.Equal(30, viewModel.KeepAliveSeconds);
        Assert.Equal(9, viewModel.MaxConnectionDurationMinutes);
        Assert.True(viewModel.IsDirty);
        Assert.Contains("恢复默认值", viewModel.StatusText);
    }

    [Fact]
    public void KeepAliveOptions_CoverEntireContractRange()
    {
        var viewModel = new SettingsViewModel(
            new ApplicationSettingsLoadResult(ApplicationSettings.Default, null),
            _ => Task.CompletedTask);

        Assert.Equal(111, viewModel.KeepAliveOptions.Count);
        Assert.Contains(15, viewModel.KeepAliveOptions);
        Assert.Contains(120, viewModel.KeepAliveOptions);
    }
}
