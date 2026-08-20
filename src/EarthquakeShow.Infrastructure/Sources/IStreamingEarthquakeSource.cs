using System.Net.WebSockets;

namespace EarthquakeShow.Infrastructure.Sources;

public interface IStreamingEarthquakeSource
{
    string SourceId { get; }

    IAsyncEnumerable<EarthquakeSourceFetchResult> StreamAsync(
        CancellationToken cancellationToken = default);
}

public interface IWebSocketConnection : IAsyncDisposable
{
    Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken);

    Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken);
}
