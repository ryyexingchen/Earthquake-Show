using System.Net.WebSockets;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Infrastructure.Sources;

ProbeOptions options;
try
{
    options = ProbeOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"参数错误：{exception.Message}");
    Console.Error.WriteLine(ProbeOptions.Usage);
    return 2;
}

using var cancellation = new CancellationTokenSource(options.Duration);
try
{
    await VerifyHandshakeAsync(options.KeepAliveSeconds, cancellation.Token);
}
catch (Exception exception) when (exception is WebSocketException or IOException)
{
    Console.Error.WriteLine($"握手失败：{exception.Message}");
    return 1;
}

var source = new ReconnectingEarthquakeSource(
    new P2pQuakeWebSocketSource(
        keepAliveInterval: TimeSpan.FromSeconds(options.KeepAliveSeconds)),
    new StreamingReconnectPolicy(
        maxConnectionDuration: TimeSpan.FromMinutes(options.RotationMinutes)));

var summary = new ProbeSummary();
Console.WriteLine(
    $"开始 P2PQuake WebSocket 验证：持续 {options.Duration.TotalMinutes:0.#} 分钟，" +
    $"keep-alive {options.KeepAliveSeconds} 秒，轮换 {options.RotationMinutes} 分钟。");

try
{
    await foreach (EarthquakeSourceFetchResult result in
        source.StreamAsync(cancellation.Token))
    {
        summary.Record(result);
        SourceStatus status = result.Status;
        Console.WriteLine(
            $"[{status.CheckedAt:O}] state={status.State} " +
            $"reports={result.Reports.Length} " +
            $"expectedDisconnect={status.IsExpectedDisconnect} " +
            $"exceptions={status.ConnectionExceptionCount ?? 0} " +
            $"detail={status.Detail ?? "--"}");
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("达到验证时长，正在输出汇总。");
}

Console.WriteLine(summary.Format(options));
if (summary.OnlineStates == 0)
{
    Console.Error.WriteLine("警告：验证期间未收到有效 Online 地震报文状态。");
}

if (options.Duration >= TimeSpan.FromMinutes(options.RotationMinutes) &&
    summary.ExpectedDisconnects == 0)
{
    Console.Error.WriteLine("验证失败：达到轮换验证时长但未观察到预期断开。");
    return 1;
}

return 0;

static async Task VerifyHandshakeAsync(int keepAliveSeconds, CancellationToken cancellationToken)
{
    using var client = new ClientWebSocket();
    client.Options.KeepAliveInterval = TimeSpan.FromSeconds(keepAliveSeconds);
    await client.ConnectAsync(
        new Uri(P2pQuakeWebSocketSource.DefaultEndpoint),
        cancellationToken);
    Console.WriteLine($"握手成功：state={client.State} endpoint={P2pQuakeWebSocketSource.DefaultEndpoint}");
    await client.CloseAsync(
        WebSocketCloseStatus.NormalClosure,
        "probe complete",
        CancellationToken.None);
}

internal sealed class ProbeOptions
{
    private const int DefaultDurationMinutes = 1;
    private const int DefaultKeepAliveSeconds = 30;
    private const int DefaultRotationMinutes = 9;

    public static string Usage =>
        "用法：dotnet run --project tools\\P2pQuakeNetworkProbe -- " +
        "[--duration-minutes N] [--keep-alive-seconds N] [--rotation-minutes N]";

    public TimeSpan Duration { get; private init; }

    public int KeepAliveSeconds { get; private init; }

    public int RotationMinutes { get; private init; }

    public static ProbeOptions Parse(string[] args)
    {
        int durationMinutes = DefaultDurationMinutes;
        int keepAliveSeconds = DefaultKeepAliveSeconds;
        int rotationMinutes = DefaultRotationMinutes;
        for (int index = 0; index < args.Length; index++)
        {
            string name = args[index];
            if (index + 1 >= args.Length || !int.TryParse(args[++index], out int value))
            {
                throw new ArgumentException($"{name} 后必须是整数。");
            }

            switch (name)
            {
                case "--duration-minutes":
                    durationMinutes = value;
                    break;
                case "--keep-alive-seconds":
                    keepAliveSeconds = value;
                    break;
                case "--rotation-minutes":
                    rotationMinutes = value;
                    break;
                default:
                    throw new ArgumentException($"不支持的参数：{name}。");
            }
        }

        if (durationMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMinutes), "验证时长必须大于零。");
        }

        _ = new WebSocketConnectionSettingsForProbe(keepAliveSeconds, rotationMinutes);
        return new ProbeOptions
        {
            Duration = TimeSpan.FromMinutes(durationMinutes),
            KeepAliveSeconds = keepAliveSeconds,
            RotationMinutes = rotationMinutes,
        };
    }

    private sealed class WebSocketConnectionSettingsForProbe
    {
        public WebSocketConnectionSettingsForProbe(int keepAliveSeconds, int rotationMinutes)
        {
            if (keepAliveSeconds is < 10 or > 120)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(keepAliveSeconds),
                    "keep-alive 必须在 10 到 120 秒之间。");
            }

            if (rotationMinutes is < 1 or > 9)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rotationMinutes),
                    "轮换时长必须在 1 到 9 分钟之间。");
            }
        }
    }
}

internal sealed class ProbeSummary
{
    public int Statuses { get; private set; }

    public int Reports { get; private set; }

    public int OnlineStates { get; private set; }

    public int ExpectedDisconnects { get; private set; }

    public int ConnectionExceptions { get; private set; }

    public void Record(EarthquakeSourceFetchResult result)
    {
        Statuses++;
        Reports += result.Reports.Length;
        OnlineStates += result.Status.State == SourceConnectionState.Online ? 1 : 0;
        ExpectedDisconnects += result.Status.IsExpectedDisconnect ? 1 : 0;
        ConnectionExceptions = Math.Max(
            ConnectionExceptions,
            result.Status.ConnectionExceptionCount ?? 0);
    }

    public string Format(ProbeOptions options) =>
        $"汇总：statuses={Statuses}, reports={Reports}, online={OnlineStates}, " +
        $"expectedDisconnects={ExpectedDisconnects}, connectionExceptions={ConnectionExceptions}, " +
        $"durationMinutes={options.Duration.TotalMinutes:0.#}";
}
