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
    private const string SourceName = "p2pquake-ws";
    private readonly Func<IWebSocketConnection> _connectionFactory;
    private readonly Uri _endpoint;

    public P2pQuakeWebSocketSource(string endpoint = DefaultEndpoint)
        : this(static () => new ClientWebSocketConnection(), endpoint)
    {
    }

    public P2pQuakeWebSocketSource(
        Func<IWebSocketConnection> connectionFactory,
        string endpoint = DefaultEndpoint)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri) ||
            endpointUri.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException("P2PQuake WebSocket 地址必须是 WS 或 WSS URL。", nameof(endpoint));
        }

        _endpoint = endpointUri;
    }

    public string SourceId => SourceName;

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
            yield return Failure(SourceConnectionState.Disconnected, $"P2PQuake WebSocket 连接失败：{connectException.Message}");
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
                yield return Failure(SourceConnectionState.Disconnected, $"P2PQuake WebSocket 读取失败：{receiveException.Message}");
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
                yield return Failure(SourceConnectionState.ParseFailed, "P2PQuake WebSocket 消息类型发生变化。");
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
                yield return Failure(SourceConnectionState.ParseFailed, "P2PQuake WebSocket 仅支持文本 JSON 消息。");
                continue;
            }

            DateTimeOffset receivedAt = DateTimeOffset.UtcNow;
            EarthquakeSourceFetchResult result;
            try
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("P2PQuake WebSocket 顶层必须是对象。");
                }

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
                        "P2PQuake WebSocket：1 条"));
            }
            catch (JsonException exception)
            {
                result = Failure(SourceConnectionState.ParseFailed, $"P2PQuake WebSocket JSON 格式错误：{exception.Message}");
            }
            catch (FormatException exception)
            {
                result = Failure(SourceConnectionState.ParseFailed, $"P2PQuake WebSocket 字段错误：{exception.Message}");
            }
            catch (ArgumentException exception)
            {
                result = Failure(SourceConnectionState.ParseFailed, $"P2PQuake WebSocket 字段越界：{exception.Message}");
            }

            yield return result;
        }
    }

    private EarthquakeSourceFetchResult Failure(SourceConnectionState state, string detail)
    {
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        return new EarthquakeSourceFetchResult(
            [],
            new SourceStatus(SourceId, state, checkedAt, Detail: detail));
    }

    private sealed class ClientWebSocketConnection : IWebSocketConnection
    {
        private readonly ClientWebSocket _client = new();

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
