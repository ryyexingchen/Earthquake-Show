using System.Collections.Immutable;
using EarthquakeShow.App.ViewModels;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class EarthquakePageDisplayStateTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 8, 19, 7, 10, 0, TimeSpan.FromHours(9));

    [Fact]
    public void Create_EmptyReadyState_ShowsConfiguredEmptyText()
    {
        var state = new EarthquakePageState
        {
            LoadState = EarthquakePageLoadState.Ready,
        };

        EarthquakePageDisplayState display = EarthquakePageDisplayState.Create(state);

        Assert.Equal("0 条", display.EventCountText);
        Assert.Equal("未配置", display.DataStatusText);
        Assert.Equal("最后接收：--", display.LastReceivedText);
        Assert.Equal("网络：未配置", display.NetworkStatusText);
        Assert.True(display.ShowEmptyState);
        Assert.Equal("暂无地震事件", display.EventListTitle);
    }

    [Fact]
    public void Create_LoadingState_ShowsOnlyLoadingState()
    {
        var state = new EarthquakePageState
        {
            LoadState = EarthquakePageLoadState.Loading,
        };

        EarthquakePageDisplayState display = EarthquakePageDisplayState.Create(state);

        Assert.True(display.IsLoading);
        Assert.False(display.ShowEmptyState);
        Assert.False(display.ShowErrorState);
        Assert.Equal("加载中", display.DataStatusText);
        Assert.Equal("正在读取事件", display.EventListTitle);
    }

    [Fact]
    public void Create_ErrorWithoutEvents_ShowsErrorMessage()
    {
        var state = new EarthquakePageState
        {
            LoadState = EarthquakePageLoadState.Error,
            ErrorMessage = "测试读取失败",
        };

        EarthquakePageDisplayState display = EarthquakePageDisplayState.Create(state);

        Assert.True(display.HasError);
        Assert.True(display.ShowErrorState);
        Assert.Equal("数据异常", display.DataStatusText);
        Assert.Equal("事件读取失败", display.EventListTitle);
        Assert.Equal("测试读取失败", display.EventListMessage);
    }

    [Fact]
    public void Create_OnlineSelectedEvent_FormatsSourceTimeAndDetails()
    {
        EarthquakeReport report = CreateReport();
        EarthquakeEvent earthquakeEvent = Assert.Single(
            EarthquakeEventMerger.Merge([report]));
        var state = new EarthquakePageState
        {
            Events = ImmutableArray.Create(earthquakeEvent),
            SelectedEvent = earthquakeEvent,
            ViewedReport = report,
            SourceStatuses = ImmutableArray.Create(new SourceStatus(
                "jma-xml",
                SourceConnectionState.Online,
                BaseTime.AddMinutes(11),
                BaseTime.AddMinutes(10))),
            LoadState = EarthquakePageLoadState.Ready,
        };

        EarthquakePageDisplayState display = EarthquakePageDisplayState.Create(state);

        Assert.Equal("在线", display.DataStatusText);
        Assert.True(display.HasOnlineSource);
        Assert.Equal("网络：已连接", display.NetworkStatusText);
        Assert.Equal("来源：jma-xml", display.SourceText);
        Assert.Equal("最后接收：08-19 07:20:00 JST", display.LastReceivedText);
        Assert.Equal("event", display.DetailsTitle);
        Assert.Equal("VXSE53 · 发布", display.DetailsMessage);
    }

    [Fact]
    public void Create_LateArrivingOldReport_UsesLatestReceivedTime()
    {
        EarthquakeReport lateOldReport = CreateReport() with
        {
            IssuedAt = BaseTime,
            ReceivedAt = BaseTime.AddMinutes(30),
            Source = new SourceReference("jma-xml", "late-old"),
        };
        EarthquakeReport newerReport = CreateReport() with
        {
            IssuedAt = BaseTime.AddMinutes(10),
            ReceivedAt = BaseTime.AddMinutes(11),
            Source = new SourceReference("jma-xml", "newer"),
        };
        EarthquakeEvent earthquakeEvent = Assert.Single(
            EarthquakeEventMerger.Merge([newerReport, lateOldReport]));
        var state = new EarthquakePageState
        {
            Events = ImmutableArray.Create(earthquakeEvent),
            LoadState = EarthquakePageLoadState.Ready,
        };

        EarthquakePageDisplayState display = EarthquakePageDisplayState.Create(state);

        Assert.Equal("最后接收：08-19 07:40:00 JST", display.LastReceivedText);
    }

    private static EarthquakeReport CreateReport()
    {
        return new EarthquakeReport
        {
            EventId = "event",
            ReportCode = "VXSE53",
            Status = ReportStatus.Issued,
            Context = ReportContext.Normal,
            IssuedAt = BaseTime.AddMinutes(1),
            ReceivedAt = BaseTime.AddMinutes(2),
            Source = new SourceReference("jma-xml", "message"),
        };
    }
}
