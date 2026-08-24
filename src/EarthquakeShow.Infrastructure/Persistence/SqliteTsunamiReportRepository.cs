using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using EarthquakeShow.Core.Abstractions;
using EarthquakeShow.Core.Models;
using Microsoft.Data.Sqlite;

namespace EarthquakeShow.Infrastructure.Persistence;

/// <summary>
/// 使用独立 SQLite 表保存 JMA VTSE 海啸报文。
/// </summary>
public sealed class SqliteTsunamiReportRepository : ITsunamiReportRepository
{
    private const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _databasePath;

    public SqliteTsunamiReportRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ImmutableArray<JmaTsunamiReport>> ListReportsAsync(
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenInitializedConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadReportsAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ImmutableArray<JmaTsunamiReport>> ListReportsForEventAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        await using SqliteConnection connection = await OpenInitializedConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadReportsAsync(connection, eventId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveReportsAsync(
        IEnumerable<JmaTsunamiReport> reports,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ImmutableArray<JmaTsunamiReport> incomingReports = reports.ToImmutableArray();
        cancellationToken.ThrowIfCancellationRequested();

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            await using SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (JmaTsunamiReport report in incomingReports)
            {
                TsunamiReportPayloadDto payload = TsunamiReportPayloadDto.FromDomain(report);
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO tsunami_reports(
                        event_id, source_id, source_message_id, report_code,
                        issued_at, received_at, payload_json)
                    VALUES ($event_id, $source_id, $source_message_id, $report_code,
                        $issued_at, $received_at, $payload_json)
                    ON CONFLICT(event_id, source_id, source_message_id) DO UPDATE SET
                        report_code = excluded.report_code,
                        issued_at = excluded.issued_at,
                        received_at = excluded.received_at,
                        payload_json = excluded.payload_json;
                    """;
                command.Parameters.AddWithValue("$event_id", report.EventId);
                command.Parameters.AddWithValue("$source_id", report.Source.SourceId);
                command.Parameters.AddWithValue("$source_message_id", report.Source.SourceMessageId);
                command.Parameters.AddWithValue("$report_code", report.ReportCode);
                command.Parameters.AddWithValue("$issued_at", FormatDateTime(report.IssuedAt));
                command.Parameters.AddWithValue("$received_at", FormatDateTime(report.ReceivedAt));
                command.Parameters.AddWithValue(
                    "$payload_json",
                    JsonSerializer.Serialize(payload, JsonOptions));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    private async Task<SqliteConnection> OpenInitializedConnectionAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task EnsureSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS schema_info (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tsunami_reports (
                event_id TEXT NOT NULL,
                source_id TEXT NOT NULL,
                source_message_id TEXT NOT NULL,
                report_code TEXT NOT NULL,
                issued_at TEXT NOT NULL,
                received_at TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                PRIMARY KEY (event_id, source_id, source_message_id)
            );
            CREATE INDEX IF NOT EXISTS idx_tsunami_reports_event_issued
                ON tsunami_reports(event_id, issued_at, received_at);
            INSERT OR IGNORE INTO schema_info(key, value)
            VALUES ('schema_version', '1');
            """;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version';";
            object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!int.TryParse(
                    value?.ToString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int version))
            {
                throw new InvalidDataException($"不支持的 SQLite schema 版本：{value ?? "缺失"}。");
            }

            if (version == 1)
            {
                await using SqliteCommand migration = connection.CreateCommand();
                migration.CommandText = "UPDATE schema_info SET value = '2' WHERE key = 'schema_version';";
                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                version = 2;
            }

            if (version != CurrentSchemaVersion)
            {
                throw new InvalidDataException($"不支持的 SQLite schema 版本：{value ?? "缺失"}。");
            }
        }
    }

    private static async Task<ImmutableArray<JmaTsunamiReport>> ReadReportsAsync(
        SqliteConnection connection,
        string? eventId,
        CancellationToken cancellationToken)
    {
        var reports = ImmutableArray.CreateBuilder<JmaTsunamiReport>();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = eventId is null
            ? """
                SELECT payload_json
                FROM tsunami_reports
                ORDER BY issued_at, received_at, source_id, source_message_id;
                """
            : """
                SELECT payload_json
                FROM tsunami_reports
                WHERE event_id = $event_id
                ORDER BY issued_at, received_at, source_id, source_message_id;
                """;
        if (eventId is not null)
        {
            command.Parameters.AddWithValue("$event_id", eventId);
        }

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string payloadJson = reader.GetString(0);
            TsunamiReportPayloadDto? payload = JsonSerializer.Deserialize<TsunamiReportPayloadDto>(
                payloadJson,
                JsonOptions);
            if (payload is null)
            {
                throw new InvalidDataException("SQLite 海啸报文负载为空。");
            }

            reports.Add(payload.ToDomain());
        }

        return reports.ToImmutable();
    }

    private static string FormatDateTime(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDateTime(string value)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset result))
        {
            throw new FormatException($"无法解析 SQLite 海啸时间：{value}。");
        }

        return result;
    }

    private sealed class TsunamiReportPayloadDto
    {
        public string EventId { get; set; } = string.Empty;
        public string ReportCode { get; set; } = string.Empty;
        public string? InfoKind { get; set; }
        public string Status { get; set; } = nameof(ReportStatus.Unknown);
        public string Context { get; set; } = nameof(ReportContext.Unknown);
        public int? Serial { get; set; }
        public string IssuedAt { get; set; } = string.Empty;
        public string ReceivedAt { get; set; } = string.Empty;
        public string? HeadlineText { get; set; }
        public List<TsunamiInformationItemDto> Items { get; set; } = [];
        public List<TsunamiForecastAreaDto> ForecastAreas { get; set; } = [];
        public List<TsunamiObservationStationDto> ObservationStations { get; set; } = [];
        public List<TsunamiEstimationAreaDto> EstimationAreas { get; set; } = [];
        public SourceReferenceDto Source { get; set; } = new();

        public static TsunamiReportPayloadDto FromDomain(JmaTsunamiReport report) => new()
        {
            EventId = report.EventId,
            ReportCode = report.ReportCode,
            InfoKind = report.InfoKind,
            Status = report.Status.ToString(),
            Context = report.Context.ToString(),
            Serial = report.Serial,
            IssuedAt = FormatDateTime(report.IssuedAt),
            ReceivedAt = FormatDateTime(report.ReceivedAt),
            HeadlineText = report.HeadlineText,
            Items = report.Items.Select(TsunamiInformationItemDto.FromDomain).ToList(),
            ForecastAreas = report.ForecastAreas.Select(TsunamiForecastAreaDto.FromDomain).ToList(),
            ObservationStations = report.ObservationStations.Select(TsunamiObservationStationDto.FromDomain).ToList(),
            EstimationAreas = report.EstimationAreas.Select(TsunamiEstimationAreaDto.FromDomain).ToList(),
            Source = SourceReferenceDto.FromDomain(report.Source),
        };

        public JmaTsunamiReport ToDomain() => new()
        {
            EventId = EventId,
            ReportCode = ReportCode,
            InfoKind = InfoKind,
            Status = ParseEnum<ReportStatus>(Status),
            Context = ParseEnum<ReportContext>(Context),
            Serial = Serial,
            IssuedAt = ParseDateTime(IssuedAt),
            ReceivedAt = ParseDateTime(ReceivedAt),
            HeadlineText = HeadlineText,
            Items = Items.Select(item => item.ToDomain()).ToImmutableArray(),
            ForecastAreas = ForecastAreas.Select(item => item.ToDomain()).ToImmutableArray(),
            ObservationStations = ObservationStations.Select(item => item.ToDomain()).ToImmutableArray(),
            EstimationAreas = EstimationAreas.Select(item => item.ToDomain()).ToImmutableArray(),
            Source = Source.ToDomain(),
        };

        private static T ParseEnum<T>(string value) where T : struct, Enum =>
            Enum.TryParse(value, ignoreCase: false, out T result)
                ? result
                : throw new InvalidDataException($"无法解析 SQLite 海啸枚举值：{value}。");
    }

    private sealed class TsunamiInformationItemDto
    {
        public string? KindName { get; set; }
        public string? KindCode { get; set; }
        public string? LastKindName { get; set; }
        public string? LastKindCode { get; set; }
        public List<TsunamiAreaDto> Areas { get; set; } = [];

        public static TsunamiInformationItemDto FromDomain(JmaTsunamiInformationItem value) => new()
        {
            KindName = value.KindName,
            KindCode = value.KindCode,
            LastKindName = value.LastKindName,
            LastKindCode = value.LastKindCode,
            Areas = value.Areas.Select(TsunamiAreaDto.FromDomain).ToList(),
        };

        public JmaTsunamiInformationItem ToDomain() => new(
            KindName,
            KindCode,
            LastKindName,
            LastKindCode,
            Areas.Select(item => item.ToDomain()).ToImmutableArray());
    }

    private sealed class TsunamiAreaDto
    {
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;

        public static TsunamiAreaDto FromDomain(JmaTsunamiArea value) => new()
        {
            Name = value.Name,
            Code = value.Code,
        };

        public JmaTsunamiArea ToDomain() => new(Name, Code);
    }

    private sealed class TsunamiHeightDto
    {
        public double? Meters { get; set; }
        public string? Description { get; set; }
        public string? Condition { get; set; }
        public string? Unit { get; set; }
        public string? Type { get; set; }

        public static TsunamiHeightDto? FromDomain(JmaTsunamiHeight? value) => value is null ? null : new()
        {
            Meters = value.Meters,
            Description = value.Description,
            Condition = value.Condition,
            Unit = value.Unit,
            Type = value.Type,
        };

        public JmaTsunamiHeight ToDomain() => new(Meters, Description, Condition, Unit, Type);
    }

    private sealed class TsunamiForecastAreaDto
    {
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string? KindName { get; set; }
        public string? KindCode { get; set; }
        public string? LastKindName { get; set; }
        public string? LastKindCode { get; set; }
        public string? FirstArrivalTime { get; set; }
        public string? FirstArrivalCondition { get; set; }
        public TsunamiHeightDto? MaximumHeight { get; set; }
        public List<TsunamiStationForecastDto> Stations { get; set; } = [];

        public static TsunamiForecastAreaDto FromDomain(JmaTsunamiForecastArea value) => new()
        {
            Name = value.Name,
            Code = value.Code,
            KindName = value.KindName,
            KindCode = value.KindCode,
            LastKindName = value.LastKindName,
            LastKindCode = value.LastKindCode,
            FirstArrivalTime = value.FirstArrivalTime is null ? null : FormatDateTime(value.FirstArrivalTime.Value),
            FirstArrivalCondition = value.FirstArrivalCondition,
            MaximumHeight = TsunamiHeightDto.FromDomain(value.MaximumHeight),
            Stations = value.Stations.Select(TsunamiStationForecastDto.FromDomain).ToList(),
        };

        public JmaTsunamiForecastArea ToDomain() => new(
            Name,
            Code,
            KindName,
            KindCode,
            LastKindName,
            LastKindCode,
            FirstArrivalTime is null ? null : ParseDateTime(FirstArrivalTime),
            FirstArrivalCondition,
            MaximumHeight?.ToDomain(),
            Stations.Select(item => item.ToDomain()).ToImmutableArray());
    }

    private sealed class TsunamiStationForecastDto
    {
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string? HighTideTime { get; set; }
        public string? FirstArrivalTime { get; set; }
        public string? FirstArrivalCondition { get; set; }

        public static TsunamiStationForecastDto FromDomain(JmaTsunamiStationForecast value) => new()
        {
            Name = value.Name,
            Code = value.Code,
            HighTideTime = value.HighTideTime is null ? null : FormatDateTime(value.HighTideTime.Value),
            FirstArrivalTime = value.FirstArrivalTime is null ? null : FormatDateTime(value.FirstArrivalTime.Value),
            FirstArrivalCondition = value.FirstArrivalCondition,
        };

        public JmaTsunamiStationForecast ToDomain() => new(
            Name,
            Code,
            HighTideTime is null ? null : ParseDateTime(HighTideTime),
            FirstArrivalTime is null ? null : ParseDateTime(FirstArrivalTime),
            FirstArrivalCondition);
    }

    private sealed class TsunamiObservationStationDto
    {
        public string AreaName { get; set; } = string.Empty;
        public string AreaCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string? Sensor { get; set; }
        public string? FirstArrivalTime { get; set; }
        public string? FirstArrivalCondition { get; set; }
        public string? Initial { get; set; }
        public string? MaximumHeightTime { get; set; }
        public string? MaximumHeightCondition { get; set; }
        public TsunamiHeightDto? MaximumHeight { get; set; }

        public static TsunamiObservationStationDto FromDomain(JmaTsunamiObservationStation value) => new()
        {
            AreaName = value.AreaName,
            AreaCode = value.AreaCode,
            Name = value.Name,
            Code = value.Code,
            Sensor = value.Sensor,
            FirstArrivalTime = value.FirstArrivalTime is null ? null : FormatDateTime(value.FirstArrivalTime.Value),
            FirstArrivalCondition = value.FirstArrivalCondition,
            Initial = value.Initial,
            MaximumHeightTime = value.MaximumHeightTime is null ? null : FormatDateTime(value.MaximumHeightTime.Value),
            MaximumHeightCondition = value.MaximumHeightCondition,
            MaximumHeight = TsunamiHeightDto.FromDomain(value.MaximumHeight),
        };

        public JmaTsunamiObservationStation ToDomain() => new(
            AreaName,
            AreaCode,
            Name,
            Code,
            Sensor,
            FirstArrivalTime is null ? null : ParseDateTime(FirstArrivalTime),
            FirstArrivalCondition,
            Initial,
            MaximumHeightTime is null ? null : ParseDateTime(MaximumHeightTime),
            MaximumHeightCondition,
            MaximumHeight?.ToDomain());
    }

    private sealed class TsunamiEstimationAreaDto
    {
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string? FirstArrivalTime { get; set; }
        public string? FirstArrivalCondition { get; set; }
        public TsunamiHeightDto? MaximumHeight { get; set; }

        public static TsunamiEstimationAreaDto FromDomain(JmaTsunamiEstimationArea value) => new()
        {
            Name = value.Name,
            Code = value.Code,
            FirstArrivalTime = value.FirstArrivalTime is null ? null : FormatDateTime(value.FirstArrivalTime.Value),
            FirstArrivalCondition = value.FirstArrivalCondition,
            MaximumHeight = TsunamiHeightDto.FromDomain(value.MaximumHeight),
        };

        public JmaTsunamiEstimationArea ToDomain() => new(
            Name,
            Code,
            FirstArrivalTime is null ? null : ParseDateTime(FirstArrivalTime),
            FirstArrivalCondition,
            MaximumHeight?.ToDomain());
    }

    private sealed class SourceReferenceDto
    {
        public string SourceId { get; set; } = string.Empty;
        public string SourceMessageId { get; set; } = string.Empty;
        public string? RawMessageUri { get; set; }
        public string? SourcePayload { get; set; }

        public static SourceReferenceDto FromDomain(SourceReference value) => new()
        {
            SourceId = value.SourceId,
            SourceMessageId = value.SourceMessageId,
            RawMessageUri = value.RawMessageUri?.ToString(),
            SourcePayload = value.SourcePayload,
        };

        public SourceReference ToDomain() => new(
            SourceId,
            SourceMessageId,
            string.IsNullOrWhiteSpace(RawMessageUri) ? null : new Uri(RawMessageUri, UriKind.RelativeOrAbsolute),
            SourcePayload);
    }
}
