using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using EarthquakeShow.Core.Abstractions;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using EarthquakeShow.Infrastructure.Sources;
using Microsoft.Data.Sqlite;

namespace EarthquakeShow.Infrastructure.Persistence;

/// <summary>
/// 使用 SQLite 保存规范化报文，并在读取时重新派生事件时间线。
/// </summary>
public sealed class SqliteEarthquakeEventRepository :
    IEarthquakeEventRepository,
    IEarthquakeSourceStatusProvider
{
    private const int CurrentSchemaVersion = 1;
    private const string DefaultSourceId = "jma-xml";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _databasePath;
    private readonly ImmutableArray<IRealtimeEarthquakeSource> _realtimeSources;
    private readonly JmaStationCoordinateCatalog? _stationCatalog;
    private ImmutableArray<EarthquakeReport> _reports = [];
    private ImmutableArray<EarthquakeEvent> _events = [];
    private ImmutableArray<SourceStatus> _sourceStatuses = [];
    private string _cacheStatus = "缓存：未初始化";

    public SqliteEarthquakeEventRepository(
        string databasePath,
        IRealtimeEarthquakeSource? realtimeSource = null,
        JmaStationCoordinateCatalog? stationCatalog = null)
        : this(
            databasePath,
            realtimeSource is null ? [] : [realtimeSource],
            stationCatalog)
    {
    }

    public SqliteEarthquakeEventRepository(
        string databasePath,
        IEnumerable<IRealtimeEarthquakeSource> realtimeSources,
        JmaStationCoordinateCatalog? stationCatalog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(realtimeSources);
        _databasePath = Path.GetFullPath(databasePath);
        _realtimeSources = realtimeSources
            .Where(source => source is not null)
            .ToImmutableArray();
        _stationCatalog = stationCatalog;
    }

    public event EventHandler<EarthquakeEventsChangedEventArgs>? EventsChanged;

    public string CacheStatus
    {
        get
        {
            lock (_syncRoot)
            {
                return _cacheStatus;
            }
        }
    }

    public ImmutableArray<SourceStatus> SourceStatuses
    {
        get
        {
            lock (_syncRoot)
            {
                return _sourceStatuses;
            }
        }
    }

    public async Task InitializeAsync(
        IEnumerable<EarthquakeReport> seedReports,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seedReports);
        ImmutableArray<EarthquakeReport> fallbackReports = seedReports.ToImmutableArray();
        ImmutableArray<EarthquakeReport> loadedReports = [];
        bool hasLoadedReports = false;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            string? directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

            ImmutableArray<EarthquakeReport> cachedReports =
                await ReadReportsAsync(connection, cancellationToken).ConfigureAwait(false);
            loadedReports = cachedReports;
            hasLoadedReports = true;
            bool seeded = cachedReports.IsDefaultOrEmpty;
            int addedSeedReportCount = 0;
            if (seeded)
            {
                await SaveReportsToConnectionAsync(
                    connection,
                    fallbackReports,
                    cancellationToken).ConfigureAwait(false);
                cachedReports = fallbackReports;
                loadedReports = cachedReports;
            }
            else
            {
                ImmutableArray<EarthquakeReport> missingSeedReports =
                    GetMissingSeedReports(cachedReports, fallbackReports);
                if (!missingSeedReports.IsDefaultOrEmpty)
                {
                    loadedReports = cachedReports
                        .AddRange(missingSeedReports);
                    await SaveReportsToConnectionAsync(
                        connection,
                        missingSeedReports,
                        cancellationToken).ConfigureAwait(false);
                    cachedReports = await ReadReportsAsync(
                        connection,
                        cancellationToken).ConfigureAwait(false);
                    addedSeedReportCount = missingSeedReports.Length;
                }
            }

            ImmutableArray<SourceStatus> statuses =
                await ReadSourceStatusesAsync(connection, cancellationToken).ConfigureAwait(false);
            statuses = BuildOfflineStatuses(cachedReports, statuses);
            await SaveSourceStatusesToConnectionAsync(
                connection,
                statuses,
                cancellationToken).ConfigureAwait(false);

            SetSnapshot(
                cachedReports,
                statuses,
                seeded
                    ? $"缓存：已写入固定样本 {cachedReports.Length} 条报文"
                    : addedSeedReportCount > 0
                        ? $"缓存：已补充固定样本 {addedSeedReportCount} 条报文，已读取 {cachedReports.Length} 条报文"
                    : $"缓存：已读取 {cachedReports.Length} 条报文");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SqliteException or
            InvalidDataException or JsonException or FormatException or ArgumentException or
            InvalidOperationException)
        {
            if (!hasLoadedReports)
            {
                (ImmutableArray<EarthquakeReport> Reports,
                    ImmutableArray<SourceStatus> Statuses)? readOnlySnapshot =
                    await TryReadReadOnlySnapshotAsync(cancellationToken).ConfigureAwait(false);
                if (readOnlySnapshot is not null)
                {
                    ImmutableArray<EarthquakeReport> readOnlyReports = readOnlySnapshot.Value.Reports;
                    ImmutableArray<EarthquakeReport> missingSeedReports =
                        GetMissingSeedReports(readOnlyReports, fallbackReports);
                    ImmutableArray<EarthquakeReport> readOnlySnapshotReports = readOnlyReports
                        .AddRange(missingSeedReports);
                    ImmutableArray<SourceStatus> readOnlyStatuses = BuildOfflineStatuses(
                        readOnlySnapshotReports,
                        readOnlySnapshot.Value.Statuses);
                    SetSnapshot(
                        readOnlySnapshotReports,
                        readOnlyStatuses,
                        $"缓存：只读模式，已读取 {readOnlySnapshotReports.Length} 条报文（无法写回：{exception.Message}）");
                    return;
                }
            }

            ImmutableArray<EarthquakeReport> snapshotReports =
                hasLoadedReports ? loadedReports : fallbackReports;
            ImmutableArray<SourceStatus> statuses = BuildOfflineStatuses(snapshotReports, []);
            SetSnapshot(
                snapshotReports,
                statuses,
                hasLoadedReports
                    ? $"缓存：已读取 {snapshotReports.Length} 条报文，但无法写回（{exception.Message}）"
                    : $"缓存：不可用，已回退固定样本（{exception.Message}）");
        }
    }

    public ValueTask<ImmutableArray<EarthquakeEvent>> ListEventsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            return ValueTask.FromResult(_events);
        }
    }

    public ValueTask<EarthquakeEvent?> GetEventAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            return ValueTask.FromResult(_events.FirstOrDefault(
                item => string.Equals(item.EventId, eventId, StringComparison.Ordinal)));
        }
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_realtimeSources.IsDefaultOrEmpty)
        {
            return;
        }

        var reports = ImmutableArray.CreateBuilder<EarthquakeReport>();
        var statuses = ImmutableArray.CreateBuilder<SourceStatus>();
        foreach (IRealtimeEarthquakeSource source in _realtimeSources)
        {
            EarthquakeSourceFetchResult result;
            try
            {
                result = await source.FetchAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                result = new EarthquakeSourceFetchResult(
                    [],
                    new SourceStatus(
                        source.SourceId,
                        SourceConnectionState.Disconnected,
                        DateTimeOffset.UtcNow,
                        Detail: $"数据源网络错误：{exception.Message}"));
            }

            reports.AddRange(result.Reports);
            statuses.Add(result.Status);
        }

        ImmutableArray<SourceStatus> sourceStatuses = statuses.ToImmutable();
        bool allOnline = sourceStatuses.All(
            status => status.State == SourceConnectionState.Online);
        await SaveReportsAndStatusAsync(
            reports.ToImmutable(),
            sourceStatuses,
            allOnline
                ? $"缓存：实时源已更新 {reports.Count} 条报文"
                : "缓存：保留已有数据，至少一个来源不可用",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveReportsAsync(
        IEnumerable<EarthquakeReport> reports,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reports);
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<EarthquakeReport> incomingReports = reports.ToImmutableArray();
        await SaveReportsAndStatusAsync(
            incomingReports,
            [],
            $"缓存：已保存 {incomingReports.Length} 条报文",
            cancellationToken).ConfigureAwait(false);
    }

    public Task ApplyStreamingResultAsync(
        EarthquakeSourceFetchResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        string cacheStatus = result.Status.State == SourceConnectionState.Online
            ? $"缓存：{result.Status.SourceId} 已更新 {result.Reports.Length} 条报文"
            : $"缓存：{result.Status.SourceId} 状态已更新，保留已有数据";
        return SaveReportsAndStatusAsync(
            result.Reports,
            [result.Status],
            cacheStatus,
            cancellationToken);
    }

    private async Task SaveReportsAndStatusAsync(
        ImmutableArray<EarthquakeReport> incomingReports,
        ImmutableArray<SourceStatus> sourceStatuses,
        string cacheStatus,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            await SaveReportsToConnectionAsync(connection, incomingReports, cancellationToken)
                .ConfigureAwait(false);
            ImmutableArray<EarthquakeReport> allReports =
                await ReadReportsAsync(connection, cancellationToken).ConfigureAwait(false);
            ImmutableArray<SourceStatus> existingStatuses =
                await ReadSourceStatusesAsync(connection, cancellationToken).ConfigureAwait(false);
            existingStatuses = MergeTransientSourceStatuses(
                existingStatuses,
                GetSourceStatusesSnapshot());
            ImmutableArray<SourceStatus> statuses = EnsureSourceStatuses(
                allReports,
                existingStatuses);
            foreach (SourceStatus sourceStatus in sourceStatuses)
            {
                statuses = ReplaceSourceStatus(statuses, sourceStatus);
            }

            await SaveSourceStatusesToConnectionAsync(connection, statuses, cancellationToken)
                .ConfigureAwait(false);
            SetSnapshot(allReports, statuses, cacheStatus);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<(
        ImmutableArray<EarthquakeReport> Reports,
        ImmutableArray<SourceStatus> Statuses)?> TryReadReadOnlySnapshotAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = CreateConnection(readOnly: true);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            ImmutableArray<EarthquakeReport> reports =
                await ReadReportsAsync(connection, cancellationToken).ConfigureAwait(false);
            ImmutableArray<SourceStatus> statuses =
                await ReadSourceStatusesAsync(connection, cancellationToken).ConfigureAwait(false);
            return (reports, statuses);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SqliteException or
            InvalidDataException or JsonException or FormatException or ArgumentException or
            InvalidOperationException)
        {
            return null;
        }
    }

    private static ImmutableArray<EarthquakeReport> GetMissingSeedReports(
        ImmutableArray<EarthquakeReport> cachedReports,
        ImmutableArray<EarthquakeReport> fallbackReports)
    {
        return fallbackReports
            .Where(seedReport => !cachedReports.Any(cachedReport =>
                string.Equals(
                    cachedReport.EventId,
                    seedReport.EventId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    cachedReport.Source.SourceId,
                    seedReport.Source.SourceId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    cachedReport.Source.SourceMessageId,
                    seedReport.Source.SourceMessageId,
                    StringComparison.Ordinal)))
            .ToImmutableArray();
    }

    private SqliteConnection CreateConnection(bool readOnly = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        return new SqliteConnection(builder.ConnectionString);
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
            CREATE TABLE IF NOT EXISTS earthquake_events (
                event_id TEXT PRIMARY KEY,
                updated_at TEXT NOT NULL,
                report_count INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS earthquake_reports (
                event_id TEXT NOT NULL,
                source_id TEXT NOT NULL,
                source_message_id TEXT NOT NULL,
                report_code TEXT NOT NULL,
                issued_at TEXT NOT NULL,
                received_at TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                PRIMARY KEY (event_id, source_id, source_message_id)
            );
            CREATE TABLE IF NOT EXISTS source_status (
                source_id TEXT PRIMARY KEY,
                state TEXT NOT NULL,
                checked_at TEXT NOT NULL,
                last_received_at TEXT NULL,
                detail TEXT NULL
            );
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
            if (!int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int version) ||
                version != CurrentSchemaVersion)
            {
                throw new InvalidDataException($"不支持的 SQLite schema 版本：{value ?? "缺失"}。");
            }
        }
    }

    private static async Task SaveReportsToConnectionAsync(
        SqliteConnection connection,
        ImmutableArray<EarthquakeReport> reports,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (EarthquakeReport report in reports)
        {
            ReportPayloadDto payload = ReportPayloadDto.FromDomain(report);
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO earthquake_reports(
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
            command.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(payload, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO earthquake_events(event_id, updated_at, report_count)
                SELECT event_id, MAX(issued_at), COUNT(*)
                FROM earthquake_reports
                GROUP BY event_id
                ON CONFLICT(event_id) DO UPDATE SET
                    updated_at = excluded.updated_at,
                    report_count = excluded.report_count;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImmutableArray<EarthquakeReport>> ReadReportsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var reports = ImmutableArray.CreateBuilder<EarthquakeReport>();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json
            FROM earthquake_reports
            ORDER BY issued_at, received_at, source_id, source_message_id;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string payloadJson = reader.GetString(0);
            ReportPayloadDto? payload = JsonSerializer.Deserialize<ReportPayloadDto>(payloadJson, JsonOptions);
            if (payload is null)
            {
                throw new InvalidDataException("SQLite 报文负载为空。");
            }

            reports.Add(FillMissingJmaStationCoordinates(payload.ToDomain()));
        }

        return reports.ToImmutable();
    }

    private EarthquakeReport FillMissingJmaStationCoordinates(EarthquakeReport report)
    {
        if (_stationCatalog is null ||
            !string.Equals(report.Source.SourceId, DefaultSourceId, StringComparison.Ordinal) ||
            report.IntensityStations.All(station => station.Coordinate is not null))
        {
            return report;
        }

        bool changed = false;
        ImmutableArray<IntensityStation> stations = report.IntensityStations
            .Select(station =>
            {
                if (station.Coordinate is not null ||
                    !_stationCatalog.TryResolve(station.Code, station.Name, out GeoCoordinate coordinate, out _))
                {
                    return station;
                }

                changed = true;
                return station with { Coordinate = coordinate };
            })
            .ToImmutableArray();
        return changed ? report with { IntensityStations = stations } : report;
    }

    private static async Task<ImmutableArray<SourceStatus>> ReadSourceStatusesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var statuses = ImmutableArray.CreateBuilder<SourceStatus>();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_id, state, checked_at, last_received_at, detail
            FROM source_status
            ORDER BY source_id;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            statuses.Add(new SourceStatus(
                reader.GetString(0),
                ParseConnectionState(reader.GetString(1)),
                ParseDateTime(reader.GetString(2)),
                reader.IsDBNull(3) ? null : ParseDateTime(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return statuses.ToImmutable();
    }

    private static async Task SaveSourceStatusesToConnectionAsync(
        SqliteConnection connection,
        ImmutableArray<SourceStatus> statuses,
        CancellationToken cancellationToken)
    {
        foreach (SourceStatus status in statuses)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO source_status(
                    source_id, state, checked_at, last_received_at, detail)
                VALUES ($source_id, $state, $checked_at, $last_received_at, $detail)
                ON CONFLICT(source_id) DO UPDATE SET
                    state = excluded.state,
                    checked_at = excluded.checked_at,
                    last_received_at = excluded.last_received_at,
                    detail = excluded.detail;
                """;
            command.Parameters.AddWithValue("$source_id", status.SourceId);
            command.Parameters.AddWithValue("$state", status.State.ToString());
            command.Parameters.AddWithValue("$checked_at", FormatDateTime(status.CheckedAt));
            command.Parameters.AddWithValue(
                "$last_received_at",
                status.LastReceivedAt is null ? DBNull.Value : FormatDateTime(status.LastReceivedAt.Value));
            command.Parameters.AddWithValue("$detail", status.Detail is null ? DBNull.Value : status.Detail);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static ImmutableArray<SourceStatus> BuildOfflineStatuses(
        ImmutableArray<EarthquakeReport> reports,
        ImmutableArray<SourceStatus> existing)
    {
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        var sourceIds = reports
            .Select(report => report.Source.SourceId)
            .Append(DefaultSourceId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<SourceStatus>();
        foreach (string sourceId in sourceIds)
        {
            DateTimeOffset? lastReceived = reports
                .Where(report => string.Equals(report.Source.SourceId, sourceId, StringComparison.Ordinal))
                .Select(report => (DateTimeOffset?)report.ReceivedAt)
                .Max();
            SourceStatus? prior = existing.FirstOrDefault(
                status => string.Equals(status.SourceId, sourceId, StringComparison.Ordinal));
            builder.Add(new SourceStatus(
                sourceId,
                SourceConnectionState.Disabled,
                checkedAt,
                lastReceived ?? prior?.LastReceivedAt,
                "离线缓存，尚未连接实时数据源"));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<SourceStatus> EnsureSourceStatuses(
        ImmutableArray<EarthquakeReport> reports,
        ImmutableArray<SourceStatus> existing)
    {
        ImmutableArray<SourceStatus> statuses = existing;
        IEnumerable<string> sourceIds = reports
            .Select(report => report.Source.SourceId)
            .Append(DefaultSourceId)
            .Distinct(StringComparer.Ordinal);
        foreach (string sourceId in sourceIds)
        {
            if (statuses.Any(status =>
                    string.Equals(status.SourceId, sourceId, StringComparison.Ordinal)))
            {
                continue;
            }

            DateTimeOffset? lastReceived = reports
                .Where(report => string.Equals(
                    report.Source.SourceId,
                    sourceId,
                    StringComparison.Ordinal))
                .Select(report => (DateTimeOffset?)report.ReceivedAt)
                .Max();
            statuses = ReplaceSourceStatus(
                statuses,
                new SourceStatus(
                    sourceId,
                    SourceConnectionState.Disabled,
                    DateTimeOffset.UtcNow,
                    lastReceived,
                    "离线缓存，尚未连接实时数据源"));
        }

        return statuses;
    }

    private ImmutableArray<SourceStatus> GetSourceStatusesSnapshot()
    {
        lock (_syncRoot)
        {
            return _sourceStatuses;
        }
    }

    private static ImmutableArray<SourceStatus> MergeTransientSourceStatuses(
        ImmutableArray<SourceStatus> persisted,
        ImmutableArray<SourceStatus> current)
    {
        ImmutableArray<SourceStatus> result = persisted;
        foreach (SourceStatus status in current)
        {
            if (string.Equals(status.SourceId, "p2pquake-ws", StringComparison.Ordinal) &&
                (status.RetryAttempt is not null ||
                    status.NextRetryAt is not null ||
                    status.ConnectedAt is not null ||
                    status.ConnectionEndedAt is not null ||
                    status.LastError is not null ||
                    status.LastMessageAt is not null ||
                    status.ConnectionExceptionCount is not null ||
                    status.LastConnectionExceptionAt is not null ||
                    status.IsExpectedDisconnect))
            {
                result = ReplaceSourceStatus(result, status);
            }
        }

        return result;
    }

    private static ImmutableArray<SourceStatus> ReplaceSourceStatus(
        ImmutableArray<SourceStatus> statuses,
        SourceStatus updatedStatus)
    {
        var builder = statuses
            .Where(status => !string.Equals(status.SourceId, updatedStatus.SourceId, StringComparison.Ordinal))
            .ToImmutableArray()
            .ToBuilder();
        builder.Add(updatedStatus);
        return builder
            .OrderBy(status => status.SourceId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private void SetSnapshot(
        ImmutableArray<EarthquakeReport> reports,
        ImmutableArray<SourceStatus> statuses,
        string cacheStatus)
    {
        ImmutableArray<EarthquakeEvent> events = EarthquakeEventMerger.Merge(reports);
        lock (_syncRoot)
        {
            _reports = reports;
            _events = events;
            _sourceStatuses = statuses;
            _cacheStatus = cacheStatus;
        }

        EventsChanged?.Invoke(this, new EarthquakeEventsChangedEventArgs(events));
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
            throw new FormatException($"无法解析 SQLite 时间：{value}。");
        }

        return result;
    }

    private static SourceConnectionState ParseConnectionState(string value) =>
        Enum.TryParse(value, ignoreCase: false, out SourceConnectionState state)
            ? state
            : throw new InvalidDataException($"无法解析数据源状态：{value}。");

    private sealed class ReportPayloadDto
    {
        public string EventId { get; set; } = string.Empty;
        public string ReportCode { get; set; } = string.Empty;
        public string ReportType { get; set; } = nameof(EarthquakeReportType.Unknown);
        public string Status { get; set; } = nameof(ReportStatus.Unknown);
        public string Context { get; set; } = nameof(ReportContext.Unknown);
        public int? Serial { get; set; }
        public string? OriginTime { get; set; }
        public string IssuedAt { get; set; } = string.Empty;
        public string ReceivedAt { get; set; } = string.Empty;
        public HypocenterDto? Hypocenter { get; set; }
        public MagnitudeDto? Magnitude { get; set; }
        public string MaxIntensity { get; set; } = "unknown";
        public List<IntensityAreaDto> IntensityAreas { get; set; } = [];
        public List<IntensityMunicipalityDto> IntensityMunicipalities { get; set; } = [];
        public List<IntensityStationDto> IntensityStations { get; set; } = [];
        public string? TsunamiComment { get; set; }
        public SourceReferenceDto Source { get; set; } = new();

        public static ReportPayloadDto FromDomain(EarthquakeReport report) => new()
        {
            EventId = report.EventId,
            ReportCode = report.ReportCode,
            ReportType = report.ReportType.ToString(),
            Status = report.Status.ToString(),
            Context = report.Context.ToString(),
            Serial = report.Serial,
            OriginTime = report.OriginTime is null ? null : FormatDateTime(report.OriginTime.Value),
            IssuedAt = FormatDateTime(report.IssuedAt),
            ReceivedAt = FormatDateTime(report.ReceivedAt),
            Hypocenter = HypocenterDto.FromDomain(report.Hypocenter),
            Magnitude = MagnitudeDto.FromDomain(report.Magnitude),
            MaxIntensity = report.MaxIntensity.ToCode(),
            IntensityAreas = report.IntensityAreas.Select(IntensityAreaDto.FromDomain).ToList(),
            IntensityMunicipalities = report.IntensityMunicipalities.Select(IntensityMunicipalityDto.FromDomain).ToList(),
            IntensityStations = report.IntensityStations.Select(IntensityStationDto.FromDomain).ToList(),
            TsunamiComment = report.TsunamiComment,
            Source = SourceReferenceDto.FromDomain(report.Source),
        };

        public EarthquakeReport ToDomain()
        {
            if (!JmaIntensityExtensions.TryParseCode(MaxIntensity, out JmaIntensity intensity))
            {
                throw new InvalidDataException($"无法解析震度代码：{MaxIntensity}。");
            }

            return new EarthquakeReport
            {
                EventId = EventId,
                ReportCode = ReportCode,
                ReportType = ParseEnum<EarthquakeReportType>(ReportType),
                Status = ParseEnum<ReportStatus>(Status),
                Context = ParseEnum<ReportContext>(Context),
                Serial = Serial,
                OriginTime = OriginTime is null ? null : ParseDateTime(OriginTime),
                IssuedAt = ParseDateTime(IssuedAt),
                ReceivedAt = ParseDateTime(ReceivedAt),
                Hypocenter = Hypocenter?.ToDomain(),
                Magnitude = Magnitude?.ToDomain(),
                MaxIntensity = intensity,
                IntensityAreas = IntensityAreas.Select(item => item.ToDomain()).ToImmutableArray(),
                IntensityMunicipalities = IntensityMunicipalities.Select(item => item.ToDomain()).ToImmutableArray(),
                IntensityStations = IntensityStations.Select(item => item.ToDomain()).ToImmutableArray(),
                TsunamiComment = TsunamiComment,
                Source = Source.ToDomain(),
            };
        }

        private static T ParseEnum<T>(string value) where T : struct, Enum =>
            Enum.TryParse(value, ignoreCase: false, out T result)
                ? result
                : throw new InvalidDataException($"无法解析枚举值：{value}。");
    }

    private sealed class SourceReferenceDto
    {
        public string SourceId { get; set; } = string.Empty;
        public string SourceMessageId { get; set; } = string.Empty;
        public string? RawMessageUri { get; set; }
        public string? SourcePayload { get; set; }

        public static SourceReferenceDto FromDomain(SourceReference source) => new()
        {
            SourceId = source.SourceId,
            SourceMessageId = source.SourceMessageId,
            RawMessageUri = source.RawMessageUri?.ToString(),
            SourcePayload = source.SourcePayload,
        };

        public SourceReference ToDomain() => new(
            SourceId,
            SourceMessageId,
            string.IsNullOrWhiteSpace(RawMessageUri) ? null : new Uri(RawMessageUri, UriKind.RelativeOrAbsolute),
            SourcePayload);
    }

    private sealed class GeoCoordinateDto
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }

        public static GeoCoordinateDto FromDomain(GeoCoordinate value) => new()
        {
            Latitude = value.Latitude,
            Longitude = value.Longitude,
        };

        public GeoCoordinate ToDomain() => new(Latitude, Longitude);
    }

    private sealed class HypocenterDto
    {
        public string? Name { get; set; }
        public string? Code { get; set; }
        public GeoCoordinateDto? Coordinate { get; set; }
        public int? DepthKm { get; set; }

        public static HypocenterDto? FromDomain(Hypocenter? value) => value is null ? null : new()
        {
            Name = value.Name,
            Code = value.Code,
            Coordinate = value.Coordinate is GeoCoordinate coordinate
                ? GeoCoordinateDto.FromDomain(coordinate)
                : null,
            DepthKm = value.DepthKm,
        };

        public Hypocenter ToDomain() => new(Name, Code, Coordinate?.ToDomain(), DepthKm);
    }

    private sealed class MagnitudeDto
    {
        public double? Value { get; set; }
        public string? Type { get; set; }
        public string? Condition { get; set; }

        public static MagnitudeDto? FromDomain(Magnitude? value) => value is null ? null : new()
        {
            Value = value.Value,
            Type = value.Type,
            Condition = value.Condition,
        };

        public Magnitude ToDomain() => new(Value, Type, Condition);
    }

    private sealed class IntensityAreaDto
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string PrefectureCode { get; set; } = string.Empty;
        public string PrefectureName { get; set; } = string.Empty;
        public string MaxIntensity { get; set; } = "unknown";

        public static IntensityAreaDto FromDomain(IntensityArea value) => new()
        {
            Code = value.Code,
            Name = value.Name,
            PrefectureCode = value.PrefectureCode,
            PrefectureName = value.PrefectureName,
            MaxIntensity = value.MaxIntensity.ToCode(),
        };

        public IntensityArea ToDomain() => new(Code, Name, PrefectureCode, PrefectureName, ParseIntensity(MaxIntensity));
    }

    private sealed class IntensityMunicipalityDto
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string AreaCode { get; set; } = string.Empty;
        public string MaxIntensity { get; set; } = "unknown";

        public static IntensityMunicipalityDto FromDomain(IntensityMunicipality value) => new()
        {
            Code = value.Code,
            Name = value.Name,
            AreaCode = value.AreaCode,
            MaxIntensity = value.MaxIntensity.ToCode(),
        };

        public IntensityMunicipality ToDomain() => new(Code, Name, AreaCode, ParseIntensity(MaxIntensity));
    }

    private sealed class IntensityStationDto
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string MunicipalityCode { get; set; } = string.Empty;
        public string Intensity { get; set; } = "unknown";
        public GeoCoordinateDto? Coordinate { get; set; }

        public static IntensityStationDto FromDomain(IntensityStation value) => new()
        {
            Code = value.Code,
            Name = value.Name,
            MunicipalityCode = value.MunicipalityCode,
            Intensity = value.Intensity.ToCode(),
            Coordinate = value.Coordinate is GeoCoordinate coordinate
                ? GeoCoordinateDto.FromDomain(coordinate)
                : null,
        };

        public IntensityStation ToDomain() => new(
            Code,
            Name,
            MunicipalityCode,
            ParseIntensity(Intensity),
            Coordinate?.ToDomain());
    }

    private static JmaIntensity ParseIntensity(string value) =>
        JmaIntensityExtensions.TryParseCode(value, out JmaIntensity result)
            ? result
            : throw new InvalidDataException($"无法解析震度代码：{value}。");
}
