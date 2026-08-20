using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EarthquakeShow.App.Services;

public sealed record WebSocketConnectionSettings(
    int KeepAliveSeconds = 30,
    int MaxConnectionDurationMinutes = 9)
{
    public static WebSocketConnectionSettings Default { get; } = new();

    public TimeSpan KeepAliveInterval => TimeSpan.FromSeconds(KeepAliveSeconds);

    public TimeSpan MaxConnectionDuration =>
        TimeSpan.FromMinutes(MaxConnectionDurationMinutes);

    public void Validate()
    {
        if (KeepAliveSeconds is < 10 or > 120)
        {
            throw new ArgumentOutOfRangeException(
                nameof(KeepAliveSeconds),
                "WebSocket keep-alive 必须在 10 到 120 秒之间。");
        }

        if (MaxConnectionDurationMinutes is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConnectionDurationMinutes),
                "单连接最大持续时间必须在 1 到 9 分钟之间。");
        }
    }
}

public sealed record ApplicationSettings(
    int SchemaVersion = 1,
    WebSocketConnectionSettings? WebSocket = null)
{
    public const int CurrentSchemaVersion = 1;

    public WebSocketConnectionSettings WebSocketSettings =>
        WebSocket ?? WebSocketConnectionSettings.Default;

    public static ApplicationSettings Default { get; } = new(
        CurrentSchemaVersion,
        WebSocketConnectionSettings.Default);

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"不支持的设置版本：{SchemaVersion}。");
        }

        WebSocketSettings.Validate();
    }
}

public sealed record ApplicationSettingsLoadResult(
    ApplicationSettings Settings,
    string? Warning);

public sealed class ApplicationSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ApplicationSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    public string Path { get; }

    public ApplicationSettingsLoadResult Load()
    {
        if (!File.Exists(Path))
        {
            return new(ApplicationSettings.Default, null);
        }

        try
        {
            string json = File.ReadAllText(Path);
            ApplicationSettings? settings =
                JsonSerializer.Deserialize<ApplicationSettings>(json, JsonOptions);
            if (settings is null)
            {
                throw new InvalidDataException("设置文件为空。");
            }

            settings.Validate();
            return new(settings, null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
            NotSupportedException or InvalidDataException or ArgumentOutOfRangeException)
        {
            return new(
                ApplicationSettings.Default,
                $"设置已回退默认值：{exception.Message}");
        }
    }

    public async Task SaveAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(settings, JsonOptions);
        await File.WriteAllTextAsync(Path, json, cancellationToken)
            .ConfigureAwait(false);
    }
}
