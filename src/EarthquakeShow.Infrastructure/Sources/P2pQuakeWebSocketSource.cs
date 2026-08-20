using System.Collections.Immutable;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Infrastructure.Sources;

public sealed class P2pQuakeWebSocketSource : IStreamingEarthquakeSource
{
    public const string DefaultEndpoint = "wss://api.p2pquake.net/v2/ws";
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    private const string SourceName = "p2pquake-ws";
    private readonly Func<IWebSocketConnection> _connectionFactory;
    private readonly Uri _endpoint;
    private readonly TimeSpan _keepAliveInterval;
    private readonly Action<string>? _rawMessageObserver;

    public P2pQuakeWebSocketSource(
        string endpoint = DefaultEndpoint,
        TimeSpan? keepAliveInterval = null,
        Action<string>? rawMessageObserver = null)
    {
        _connectionFactory = () => new ClientWebSocketConnection(_keepAliveInterval);
        _endpoint = ParseEndpoint(endpoint);
        _keepAliveInterval = ValidateKeepAliveInterval(
            keepAliveInterval ?? KeepAliveInterval);
        _rawMessageObserver = rawMessageObserver;
    }

    public P2pQuakeWebSocketSource(
        Func<IWebSocketConnection> connectionFactory,
        string endpoint = DefaultEndpoint,
        TimeSpan? keepAliveInterval = null,
        Action<string>? rawMessageObserver = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _endpoint = ParseEndpoint(endpoint);
        _keepAliveInterval = ValidateKeepAliveInterval(
            keepAliveInterval ?? KeepAliveInterval);
        _rawMessageObserver = rawMessageObserver;
    }

    public string SourceId => SourceName;

    public TimeSpan ConfiguredKeepAliveInterval => _keepAliveInterval;

    public async IAsyncEnumerable<EarthquakeSourceFetchResult> StreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using IWebSocketConnection connection = _connectionFactory();
        Exception? connectException = null;
        try
        {
            await connection.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is WebSocketException or IOException)
        {
            connectException = exception;
        }

        if (connectException is not null)
        {
            bool rateLimited = IsRateLimitedHandshake(connectException);
            yield return Failure(
                rateLimited
                    ? SourceConnectionState.RateLimited
                    : SourceConnectionState.Disconnected,
                $"P2PQuake WebSocket 连接失败：{connectException.Message}",
                connectionException: !rateLimited);
            yield break;
        }

        byte[] buffer = new byte[8192];
        using var message = new MemoryStream();
        WebSocketMessageType? messageType = null;

        while (true)
        {
            WebSocketReceiveResult? received = null;
            Exception? receiveException = null;
            try
            {
                received = await connection
                    .ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is WebSocketException or IOException)
            {
                receiveException = exception;
            }

            if (receiveException is not null)
            {
                yield return Failure(
                    SourceConnectionState.Disconnected,
                    $"P2PQuake WebSocket 读取失败：{receiveException.Message}",
                    connectionException: true);
                yield break;
            }

            if (received!.MessageType == WebSocketMessageType.Close)
            {
                string detail = received.CloseStatusDescription is { Length: > 0 } description
                    ? $"P2PQuake WebSocket 已关闭：{description}"
                    : "P2PQuake WebSocket 已关闭。";
                yield return Failure(SourceConnectionState.Disconnected, detail);
                yield break;
            }

            messageType ??= received.MessageType;
            if (messageType != received.MessageType)
            {
                message.SetLength(0);
                messageType = received.MessageType;
                yield return Failure(
                    SourceConnectionState.ParseFailed,
                    "P2PQuake WebSocket 消息类型发生变化。",
                    messageReceived: true);
            }

            message.Write(buffer, 0, received.Count);
            if (!received.EndOfMessage)
            {
                continue;
            }

            string payload = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
            message.SetLength(0);
            WebSocketMessageType completedType = messageType.Value;
            messageType = null;
            if (completedType != WebSocketMessageType.Text)
            {
                yield return Failure(
                    SourceConnectionState.ParseFailed,
                    "P2PQuake WebSocket 仅支持文本 JSON 消息。",
                    messageReceived: true);
                continue;
            }

            _rawMessageObserver?.Invoke(payload);

            DateTimeOffset receivedAt = DateTimeOffset.UtcNow;
            EarthquakeSourceFetchResult result;
            try
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("P2PQuake WebSocket 顶层必须是对象。");
                }

                if (IsPeerStatisticsMessage(document.RootElement))
                {
                    result = NonEventResult(
                        receivedAt,
                        "P2PQuake WebSocket：网络节点统计（code 555）");
                }
                else if (IsNonEventMessage(document.RootElement))
                {
                    result = NonEventResult(
                        receivedAt,
                        "P2PQuake WebSocket：忽略非事件消息");
                }
                else
                {
                    EarthquakeReport report = P2pQuakeEarthquakeSource.ParseReport(
                        document.RootElement,
                        receivedAt,
                        _endpoint);
                    result = new EarthquakeSourceFetchResult(
                        ImmutableArray.Create(report),
                        new SourceStatus(
                            SourceId,
                            SourceConnectionState.Online,
                            receivedAt,
                            report.ReceivedAt,
                            "P2PQuake WebSocket：1 条",
                            LastMessageAt: receivedAt));
                }
            }
            catch (JsonException exception)
            {
                result = Failure(
                    SourceConnectionState.ParseFailed,
                    $"P2PQuake WebSocket JSON 格式错误：{exception.Message}",
                    messageReceived: true);
            }
            catch (FormatException exception)
            {
                result = Failure(
                    SourceConnectionState.ParseFailed,
                    $"P2PQuake WebSocket 字段错误：{exception.Message}",
                    messageReceived: true);
            }
            catch (ArgumentException exception)
            {
                result = Failure(
                    SourceConnectionState.ParseFailed,
                    $"P2PQuake WebSocket 字段越界：{exception.Message}",
                    messageReceived: true);
            }

            yield return result;
        }
    }

    private EarthquakeSourceFetchResult NonEventResult(
        DateTimeOffset receivedAt,
        string detail) =>
        new(
            [],
            new SourceStatus(
                SourceId,
                SourceConnectionState.Online,
                receivedAt,
                Detail: detail,
                LastMessageAt: receivedAt));

    private static bool IsPeerStatisticsMessage(JsonElement element) =>
        element.TryGetProperty("code", out JsonElement code) &&
        code.ValueKind == JsonValueKind.Number &&
        code.TryGetInt32(out int codeValue) &&
        codeValue == 555 &&
        element.TryGetProperty("areas", out JsonElement areas) &&
        areas.ValueKind == JsonValueKind.Array;

    private static bool IsNonEventMessage(JsonElement element)
    {
        // 控制/状态对象没有地震报文的事件结构；带事件结构但缺少 id 仍按格式错误处理。
        return !element.TryGetProperty("id", out _) &&
            !element.TryGetProperty("issue", out _) &&
            !element.TryGetProperty("earthquake", out _);
    }

    private static bool IsRateLimitedHandshake(Exception exception) =>
        exception is WebSocketException &&
        exception.Message.Contains("status code '429'", StringComparison.OrdinalIgnoreCase);

    private EarthquakeSourceFetchResult Failure(
        SourceConnectionState state,
        string detail,
        bool messageReceived = false,
        bool connectionException = false)
    {
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        return new EarthquakeSourceFetchResult(
            [],
            new SourceStatus(
                SourceId,
                state,
                checkedAt,
                Detail: detail,
                LastMessageAt: messageReceived ? checkedAt : null,
                ConnectionExceptionCount: connectionException ? 1 : null,
                LastConnectionExceptionAt: connectionException ? checkedAt : null));
    }

    private static Uri ParseEndpoint(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri) ||
            endpointUri.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException("P2PQuake WebSocket 地址必须是 WS 或 WSS URL。", nameof(endpoint));
        }

        return endpointUri;
    }

    private static TimeSpan ValidateKeepAliveInterval(TimeSpan interval)
    {
        if (interval < TimeSpan.FromSeconds(10) || interval > TimeSpan.FromSeconds(120))
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                "WebSocket keep-alive 必须在 10 到 120 秒之间。");
        }

        return interval;
    }

    private sealed class ClientWebSocketConnection : IWebSocketConnection
    {
        private readonly ClientWebSocket _client;

        public ClientWebSocketConnection(TimeSpan keepAliveInterval)
        {
            _client = new ClientWebSocket();
            _client.Options.KeepAliveInterval = keepAliveInterval;
        }

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken) =>
            _client.ConnectAsync(endpoint, cancellationToken);

        public Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            _client.ReceiveAsync(buffer, cancellationToken);

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
