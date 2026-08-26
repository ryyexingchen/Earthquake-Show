using System.Collections.Immutable;
using EarthquakeShow.App.Services;
using EarthquakeShow.App.ViewModels;
using EarthquakeShow.Infrastructure.Sources;
using EarthquakeShow.Core.Models;
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

    [Fact]
    public async Task ImportLocalXmlAsync_ReportsPersistedAndSkippedCounts()
    {
        string? importedPath = null;
        var viewModel = new SettingsViewModel(
            new ApplicationSettingsLoadResult(ApplicationSettings.Default, null),
            _ => Task.CompletedTask,
            (path, _) =>
            {
                importedPath = path;
                return Task.FromResult(new JmaXmlLocalFileImportResult(
                    ImmutableArray<EarthquakeReport>.Empty,
                    ["ignored.xml"],
                    [new JmaXmlLocalFileImportFailure("broken.xml", "格式错误")],
                    2,
                    new JmaXmlLocalFileImportHistory(
                        "batch",
                        path,
                        DateTimeOffset.UtcNow,
                        2,
                        [
                            new("ignored.xml", true, null),
                            new("broken.xml", false, "格式错误"),
                        ])));
            });

        await viewModel.ImportLocalXmlAsync("E:\\history");

        Assert.Equal("E:\\history", importedPath);
        Assert.False(viewModel.IsImporting);
        Assert.True(viewModel.CanImportLocalXml);
        Assert.Equal("本地 XML 导入完成：写入/更新 2 条，跳过 1 个，失败 1 个", viewModel.StatusText);
        Assert.Contains("跳过：ignored.xml", viewModel.LatestImportDetails);
        Assert.Contains(viewModel.LatestImportDetails, item => item.StartsWith("失败：broken.xml", StringComparison.Ordinal));
    }

    [Fact]
    public void SetLatestImport_ProjectsSummaryAndDetails()
    {
        var viewModel = new SettingsViewModel(
            new ApplicationSettingsLoadResult(ApplicationSettings.Default, null),
            _ => Task.CompletedTask);
        viewModel.SetLatestImport(new JmaXmlLocalFileImportHistory(
            "batch",
            "E:\\history",
            DateTimeOffset.Parse("2026-08-26T12:00:00+09:00"),
            3,
            [
                new("ignored.xml", true, null),
                new("broken.xml", false, "XML 格式错误"),
            ]));

        Assert.Contains("写入/更新 3 条", viewModel.LatestImportSummary);
        Assert.Equal(2, viewModel.LatestImportDetails.Count);
    }
}
