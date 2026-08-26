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

    public required string WebSocketErrorText { get; init; }

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

    public static EarthquakePageDisplayState Create(
        EarthquakePageState state,
        DateTimeOffset? displayAt = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        DateTimeOffset now = displayAt ?? DateTimeOffset.UtcNow;

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
            WebSocketStatusText = GetWebSocketStatusText(state, now),
            WebSocketErrorText = GetWebSocketErrorText(state),
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

        if (state.SourceStatuses.Any(status =>
                status.SourceId == "jma-xml" &&
                status.State == SourceConnectionState.Delayed))
        {
            return "网络：XML覆盖延迟";
        }

        if (state.SourceStatuses.Any(status =>
                status.State == SourceConnectionState.RateLimited))
        {
            return "网络：来源限流";
        }

        if (state.SourceStatuses.Any(status =>
                status.State == SourceConnectionState.ParseFailed))
        {
            return "网络：来源解析失败";
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

    private static string GetWebSocketStatusText(
        EarthquakePageState state,
        DateTimeOffset now)
    {
        SourceStatus? status = FindWebSocketStatus(state);
        if (status is null)
        {
            return "WebSocket：未启动";
        }

        string? duration = GetConnectionDurationText(status, now);
        if (status.State == SourceConnectionState.Delayed &&
            status.RetryAttempt is int attempt &&
            status.NextRetryAt is DateTimeOffset nextRetryAt)
        {
            DateTimeOffset japanTime = TimeZoneInfo.ConvertTime(nextRetryAt, JapanTimeZone);
            string previousDuration = duration is null ? string.Empty : $" · 上次连接 {duration}";
            return AppendWebSocketActivity(
                $"WebSocket：第 {attempt} 次重连 · {japanTime:HH:mm:ss} JST{previousDuration}",
                status,
                now);
        }

        string statusText = status.State switch
        {
            SourceConnectionState.Online => duration is null
                ? "WebSocket：已连接"
                : $"WebSocket：已连接 · 持续 {duration}",
            SourceConnectionState.Delayed => "WebSocket：等待重连",
            SourceConnectionState.Disconnected => duration is null
                ? "WebSocket：已断开"
                : $"WebSocket：已断开 · 上次连接 {duration}",
            SourceConnectionState.ParseFailed => "WebSocket：消息解析失败",
            SourceConnectionState.Disabled => "WebSocket：未启用",
            _ => "WebSocket：状态未知",
        };
        return AppendWebSocketActivity(statusText, status, now);
    }

    private static string GetWebSocketErrorText(EarthquakePageState state)
    {
        SourceStatus? status = FindWebSocketStatus(state);
        if (status is null)
        {
            return "最近错误：--";
        }

        string? error = status.LastError;
        if (string.IsNullOrWhiteSpace(error) &&
            status.State is SourceConnectionState.Disconnected or SourceConnectionState.ParseFailed)
        {
            error = status.Detail;
        }

        return string.IsNullOrWhiteSpace(error)
            ? "最近错误：无"
            : $"最近错误：{error}";
    }

    private static string AppendWebSocketActivity(
        string statusText,
        SourceStatus status,
        DateTimeOffset now)
    {
        List<string> details = [];
        if (status.LastMessageAt is DateTimeOffset lastMessageAt)
        {
            TimeSpan age = now - lastMessageAt;
            if (age < TimeSpan.Zero)
            {
                age = TimeSpan.Zero;
            }

            details.Add($"活性：最近消息 {FormatDuration(age)} 前");
        }
        else if (status.State == SourceConnectionState.Online)
        {
            details.Add("活性：等待首条消息");
        }

        if (status.ConnectionExceptionCount is int exceptionCount && exceptionCount > 0)
        {
            details.Add($"异常 {exceptionCount} 次");
        }

        return details.Count == 0
            ? statusText
            : $"{statusText} · {string.Join(" · ", details)}";
    }

    private static SourceStatus? FindWebSocketStatus(EarthquakePageState state)
    {
        return state.SourceStatuses.FirstOrDefault(item =>
            string.Equals(item.SourceId, "p2pquake-ws", StringComparison.Ordinal));
    }

    private static string? GetConnectionDurationText(
        SourceStatus status,
        DateTimeOffset now)
    {
        if (status.ConnectedAt is not DateTimeOffset connectedAt)
        {
            return null;
        }

        DateTimeOffset end = status.ConnectionEndedAt ?? now;
        TimeSpan duration = end - connectedAt;
        if (duration < TimeSpan.Zero)
        {
            return null;
        }

        return FormatDuration(duration);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        int totalSeconds = (int)Math.Min(int.MaxValue, duration.TotalSeconds);
        int hours = totalSeconds / 3600;
        int minutes = totalSeconds / 60 % 60;
        int seconds = totalSeconds % 60;
        return $"{hours:00}:{minutes:00}:{seconds:00}";
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

        EarthquakeReport? report = selectedEvent.PreferredReport;
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
