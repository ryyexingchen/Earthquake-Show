using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.ViewModels;

public sealed record EarthquakePageDisplayState
{
    private static readonly TimeZoneInfo JapanTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    public required string EventCountText { get; init; }

    public required string DataStatusText { get; init; }

    public required string LastReceivedText { get; init; }

    public required string NetworkStatusText { get; init; }

    public required string SourceText { get; init; }

    public required string WebSocketStatusText { get; init; }

    public required string EventListTitle { get; init; }

    public required string EventListMessage { get; init; }

    public required string DetailsTitle { get; init; }

    public required string DetailsMessage { get; init; }

    public bool IsLoading { get; init; }

    public bool HasError { get; init; }

    public bool ShowErrorState { get; init; }

    public bool ShowEmptyState { get; init; }

    public bool HasEvents { get; init; }

    public bool HasSelectedEvent { get; init; }

    public bool IsOffline { get; init; }

    public bool HasOnlineSource { get; init; }

    public static EarthquakePageDisplayState Create(EarthquakePageState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        bool hasEvents = !state.Events.IsDefaultOrEmpty;
        bool isLoading = state.LoadState == EarthquakePageLoadState.Loading && !hasEvents;
        bool hasError = state.LoadState == EarthquakePageLoadState.Error;
        bool hasOnlineSource = state.SourceStatuses.Any(
            status => status.State == SourceConnectionState.Online);

        (string listTitle, string listMessage) = GetEventListText(
            state,
            isLoading,
            hasError,
            hasEvents);
        (string detailsTitle, string detailsMessage) = GetDetailsText(state.SelectedEvent);

        return new EarthquakePageDisplayState
        {
            EventCountText = $"{state.Events.Length} 条",
            DataStatusText = GetDataStatusText(state, isLoading, hasError, hasOnlineSource),
            LastReceivedText = GetLastReceivedText(state),
            NetworkStatusText = GetNetworkStatusText(state, hasOnlineSource),
            SourceText = GetSourceText(state),
            WebSocketStatusText = GetWebSocketStatusText(state),
            EventListTitle = listTitle,
            EventListMessage = listMessage,
            DetailsTitle = detailsTitle,
            DetailsMessage = detailsMessage,
            IsLoading = isLoading,
            HasError = hasError,
            ShowErrorState = hasError && !hasEvents,
            ShowEmptyState = !isLoading && !hasError && !hasEvents,
            HasEvents = hasEvents,
            HasSelectedEvent = state.SelectedEvent is not null,
            IsOffline = state.IsOffline,
            HasOnlineSource = hasOnlineSource,
        };
    }

    private static string GetDataStatusText(
        EarthquakePageState state,
        bool isLoading,
        bool hasError,
        bool hasOnlineSource)
    {
        if (hasError)
        {
            return "数据异常";
        }

        if (isLoading)
        {
            return "加载中";
        }

        if (state.IsOffline)
        {
            return "离线";
        }

        if (state.SourceStatuses.IsDefaultOrEmpty)
        {
            return "未配置";
        }

        return state.SourceStatuses.All(status => status.State == SourceConnectionState.Online)
            ? "在线"
            : hasOnlineSource ? "部分可用" : "不可用";
    }

    private static string GetNetworkStatusText(
        EarthquakePageState state,
        bool hasOnlineSource)
    {
        if (state.IsOffline)
        {
            return "网络：离线";
        }

        if (state.SourceStatuses.IsDefaultOrEmpty)
        {
            return "网络：未配置";
        }

        return hasOnlineSource ? "网络：已连接" : "网络：不可用";
    }

    private static string GetLastReceivedText(EarthquakePageState state)
    {
        DateTimeOffset? latest = null;
        foreach (SourceStatus status in state.SourceStatuses)
        {
            if (status.LastReceivedAt is DateTimeOffset receivedAt &&
                (latest is null || receivedAt > latest.Value))
            {
                latest = receivedAt;
            }
        }

        foreach (EarthquakeEvent earthquakeEvent in state.Events)
        {
            foreach (EarthquakeReport report in earthquakeEvent.Reports)
            {
                if (latest is null || report.ReceivedAt > latest.Value)
                {
                    latest = report.ReceivedAt;
                }
            }
        }

        if (latest is null)
        {
            return "最后接收：--";
        }

        DateTimeOffset japanTime = TimeZoneInfo.ConvertTime(latest.Value, JapanTimeZone);
        return $"最后接收：{japanTime:MM-dd HH:mm:ss} JST";
    }

    private static string GetSourceText(EarthquakePageState state)
    {
        string? selectedSource = state.ViewedReport?.Source.SourceId;
        if (!string.IsNullOrWhiteSpace(selectedSource))
        {
            return $"来源：{selectedSource}";
        }

        string[] sourceIds = state.SourceStatuses
            .Select(status => status.SourceId)
            .Where(sourceId => !string.IsNullOrWhiteSpace(sourceId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return sourceIds.Length == 0
            ? "来源：未选择"
            : $"来源：{string.Join(", ", sourceIds)}";
    }

    private static string GetWebSocketStatusText(EarthquakePageState state)
    {
        SourceStatus? status = state.SourceStatuses.FirstOrDefault(item =>
            string.Equals(item.SourceId, "p2pquake-ws", StringComparison.Ordinal));
        if (status is null)
        {
            return "WebSocket：未启动";
        }

        if (status.State == SourceConnectionState.Delayed &&
            status.RetryAttempt is int attempt &&
            status.NextRetryAt is DateTimeOffset nextRetryAt)
        {
            DateTimeOffset japanTime = TimeZoneInfo.ConvertTime(nextRetryAt, JapanTimeZone);
            return $"WebSocket：第 {attempt} 次重连 · {japanTime:HH:mm:ss} JST";
        }

        return status.State switch
        {
            SourceConnectionState.Online => "WebSocket：已连接",
            SourceConnectionState.Delayed => "WebSocket：等待重连",
            SourceConnectionState.Disconnected => "WebSocket：已断开",
            SourceConnectionState.ParseFailed => "WebSocket：消息解析失败",
            SourceConnectionState.Disabled => "WebSocket：未启用",
            _ => "WebSocket：状态未知",
        };
    }

    private static (string Title, string Message) GetEventListText(
        EarthquakePageState state,
        bool isLoading,
        bool hasError,
        bool hasEvents)
    {
        if (isLoading)
        {
            return ("正在读取事件", "请稍候");
        }

        if (hasError && !hasEvents)
        {
            return ("事件读取失败", state.ErrorMessage ?? "未知错误");
        }

        return hasEvents
            ? ("事件数据已就绪", $"已加载 {state.Events.Length} 条事件")
            : ("暂无地震事件", "本地缓存中没有事件");
    }

    private static (string Title, string Message) GetDetailsText(
        EarthquakeEvent? selectedEvent)
    {
        if (selectedEvent is null)
        {
            return ("未选择事件", "从左侧事件列表选择一条地震情报");
        }

        EarthquakeReport? report = selectedEvent.LatestReport;
        string title = selectedEvent.Summary?.Hypocenter?.Name ?? selectedEvent.EventId;
        string reportText = report is null
            ? "报文状态不明"
            : $"{report.ReportCode} · {GetReportStatusText(report.Status)}";
        return (title, reportText);
    }

    private static string GetReportStatusText(ReportStatus status)
    {
        return status switch
        {
            ReportStatus.Issued => "发布",
            ReportStatus.Correction => "订正",
            ReportStatus.Cancelled => "取消",
            _ => "状态不明",
        };
    }
}
