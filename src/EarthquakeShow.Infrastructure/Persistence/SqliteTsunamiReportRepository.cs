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
/// 使用独立 SQLite 表保存 JMA VTSE 海啸报文。
/// </summary>
public sealed class SqliteTsunamiReportRepository : ITsunamiReportRepository, ITsunamiStationCatalogRepository, IDisposable
{
    private const int CurrentSchemaVersion = 2;
    private const string SchemaKey = "tsunami_schema_version";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _databasePath;
    private readonly ImmutableArray<IRealtimeTsunamiSource> _realtimeSources;
    private ImmutableArray<SourceStatus> _sourceStatuses = [];
    private bool _initialized;
    private bool _disposed;

    public SqliteTsunamiReportRepository(string databasePath)
        : this(databasePath, Array.Empty<IRealtimeTsunamiSource>())
    {
    }

    public SqliteTsunamiReportRepository(
        string databasePath,
        IRealtimeTsunamiSource? realtimeSource,
        params IRealtimeTsunamiSource[] additionalSources)
        : this(
            databasePath,
            (realtimeSource is null ? Array.Empty<IRealtimeTsunamiSource>() : [realtimeSource])
                .Concat(additionalSources ?? Array.Empty<IRealtimeTsunamiSource>()))
    {
    }

    public SqliteTsunamiReportRepository(
        string databasePath,
        IEnumerable<IRealtimeTsunamiSource> realtimeSources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(realtimeSources);
        _databasePath = Path.GetFullPath(databasePath);
        _realtimeSources = realtimeSources
            .Where(source => source is not null)
            .ToImmutableArray();
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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_initialized)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

        ImmutableArray<SourceStatus> existing = await ReadSourceStatusesAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
        ImmutableArray<SourceStatus> statuses = BuildOfflineStatuses(existing);
        if (!statuses.IsDefaultOrEmpty)
        {
            await SaveSourceStatusesToConnectionAsync(
                connection,
                statuses,
                cancellationToken).ConfigureAwait(false);
        }

        SetSourceStatuses(statuses);
        _initialized = true;
    }

    public async Task SaveStationCatalogAsync(
        JmaTsunamiStationCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(catalog);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (SqliteCommand clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM tsunami_offshore_station_map; DELETE FROM tsunami_offshore_publication; DELETE FROM tsunami_station_catalog;";
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (JmaTsunamiStationCatalogEntry station in catalog.Stations)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO tsunami_station_catalog
                        (station_code, name, name_kana, latitude, longitude, forecast_area_code, source_version)
                    VALUES ($code, $name, $kana, $latitude, $longitude, $area, $source);
                    """;
                command.Parameters.AddWithValue("$code", station.StationCode);
                command.Parameters.AddWithValue("$name", station.Name);
                command.Parameters.AddWithValue("$kana", (object?)station.NameKana ?? DBNull.Value);
                command.Parameters.AddWithValue("$latitude", (object?)station.Latitude ?? DBNull.Value);
                command.Parameters.AddWithValue("$longitude", (object?)station.Longitude ?? DBNull.Value);
                command.Parameters.AddWithValue("$area", (object?)station.ForecastAreaCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$source", catalog.SourceVersion);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (JmaTsunamiPublicationCatalogEntry publication in catalog.Publications)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO tsunami_offshore_publication
                        (publication_code, name, name_kana, source_version)
                    VALUES ($code, $name, $kana, $source);
                    """;
                command.Parameters.AddWithValue("$code", publication.PublicationCode);
                command.Parameters.AddWithValue("$name", publication.Name);
                command.Parameters.AddWithValue("$kana", (object?)publication.NameKana ?? DBNull.Value);
                command.Parameters.AddWithValue("$source", catalog.SourceVersion);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                foreach (string stationCode in publication.StationCodes)
                {
                    await using SqliteCommand map = connection.CreateCommand();
                    map.Transaction = transaction;
                    map.CommandText = "INSERT INTO tsunami_offshore_station_map(publication_code, station_code) VALUES ($publication, $station);";
                    map.Parameters.AddWithValue("$publication", publication.PublicationCode);
                    map.Parameters.AddWithValue("$station", stationCode);
                    await map.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<JmaTsunamiStationCatalog> LoadStationCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var stations = new List<JmaTsunamiStationCatalogEntry>();
        string sourceVersion = string.Empty;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT station_code, name, name_kana, latitude, longitude, forecast_area_code, source_version
                FROM tsunami_station_catalog
                ORDER BY station_code;
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sourceVersion = reader.GetString(6);
                stations.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        var publicationRows = new List<(string Code, string Name, string? Kana, string Source)>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT publication_code, name, name_kana, source_version
                FROM tsunami_offshore_publication
                ORDER BY publication_code;
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string publicationSource = reader.GetString(3);
                sourceVersion = string.IsNullOrWhiteSpace(sourceVersion) ? publicationSource : sourceVersion;
                publicationRows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    publicationSource));
            }
        }

        var stationCodesByPublication = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT publication_code, station_code
                FROM tsunami_offshore_station_map
                ORDER BY publication_code, station_code;
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                stationCodesByPublication
                    .TryGetValue(reader.GetString(0), out List<string>? codes);
                codes ??= stationCodesByPublication[reader.GetString(0)] = [];
                codes.Add(reader.GetString(1));
            }
        }

        var publications = publicationRows.Select(row => new JmaTsunamiPublicationCatalogEntry(
            row.Code,
            row.Name,
            row.Kana,
            stationCodesByPublication.TryGetValue(row.Code, out List<string>? codes)
                ? codes.ToImmutableArray()
                : []));
        return JmaTsunamiStationCatalog.Create(sourceVersion, stations, publications);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_realtimeSources.IsDefaultOrEmpty)
        {
            return;
        }

        var reports = ImmutableArray.CreateBuilder<JmaTsunamiReport>();
        var statuses = ImmutableArray.CreateBuilder<SourceStatus>();
        foreach (IRealtimeTsunamiSource source in _realtimeSources)
        {
            TsunamiSourceFetchResult result;
            try
            {
                DateTimeOffset? since = await GetLatestIssuedAtAsync(
                    source.SourceId,
                    cancellationToken).ConfigureAwait(false);
                result = source is IIncrementalTsunamiSource incrementalSource
                    ? await incrementalSource.FetchSinceAsync(since, cancellationToken).ConfigureAwait(false)
                    : await source.FetchAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                result = new TsunamiSourceFetchResult(
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
        ImmutableArray<SourceStatus> persistedStatuses = await SaveReportsAndStatusesAsync(
            reports.ToImmutable(),
            sourceStatuses,
            cancellationToken).ConfigureAwait(false);
        SetSourceStatuses(persistedStatuses);
    }

    public async ValueTask<ImmutableArray<JmaTsunamiReport>> ListReportsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await using SqliteConnection connection = await OpenInitializedConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadReportsAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ImmutableArray<JmaTsunamiReport>> ListReportsForEventAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        await using SqliteConnection connection = await OpenInitializedConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadReportsAsync(connection, eventId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveReportsAsync(
        IEnumerable<JmaTsunamiReport> reports,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
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
            await SaveReportsAndStatusesToConnectionAsync(
                connection,
                incomingReports,
                [],
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<ImmutableArray<SourceStatus>> SaveReportsAndStatusesAsync(
        ImmutableArray<JmaTsunamiReport> reports,
        ImmutableArray<SourceStatus> statuses,
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

            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            ImmutableArray<SourceStatus> existingStatuses = await ReadSourceStatusesAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            ImmutableArray<SourceStatus> persistedStatuses = statuses
                .Select(status =>
                {
                    SourceStatus? prior = existingStatuses.FirstOrDefault(existing =>
                        string.Equals(existing.SourceId, status.SourceId, StringComparison.Ordinal));
                    return status with
                    {
                        LastReceivedAt = status.LastReceivedAt ?? prior?.LastReceivedAt,
                    };
                })
                .ToImmutableArray();
            await SaveReportsAndStatusesToConnectionAsync(
                connection,
                reports,
                persistedStatuses,
                cancellationToken).ConfigureAwait(false);
            return persistedStatuses;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task SaveReportsAndStatusesToConnectionAsync(
        SqliteConnection connection,
        ImmutableArray<JmaTsunamiReport> reports,
        ImmutableArray<SourceStatus> statuses,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (JmaTsunamiReport report in reports)
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

        foreach (SourceStatus status in statuses)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
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
                status.LastReceivedAt is null
                    ? DBNull.Value
                    : FormatDateTime(status.LastReceivedAt.Value));
            command.Parameters.AddWithValue(
                "$detail",
                status.Detail is null ? DBNull.Value : status.Detail);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeGate.Dispose();
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
            CREATE TABLE IF NOT EXISTS source_status (
                source_id TEXT PRIMARY KEY,
                state TEXT NOT NULL,
                checked_at TEXT NOT NULL,
                last_received_at TEXT NULL,
                detail TEXT NULL
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
            CREATE TABLE IF NOT EXISTS tsunami_station_catalog (
                station_code TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                name_kana TEXT NULL,
                latitude REAL NULL,
                longitude REAL NULL,
                forecast_area_code TEXT NULL,
                source_version TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tsunami_offshore_publication (
                publication_code TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                name_kana TEXT NULL,
                source_version TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tsunami_offshore_station_map (
                publication_code TEXT NOT NULL,
                station_code TEXT NOT NULL,
                PRIMARY KEY (publication_code, station_code),
                FOREIGN KEY (publication_code) REFERENCES tsunami_offshore_publication(publication_code),
                FOREIGN KEY (station_code) REFERENCES tsunami_station_catalog(station_code)
            );
            """;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COALESCE(
                    (SELECT value FROM schema_info WHERE key = 'tsunami_schema_version'),
                    (SELECT value FROM schema_info WHERE key = 'schema_version'),
                    '1');
                """;
            object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!int.TryParse(
                    value?.ToString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int version))
            {
                throw new InvalidDataException($"不支持的 SQLite schema 版本：{value ?? "缺失"}。");
            }

            await using (SqliteCommand initializeVersion = connection.CreateCommand())
            {
                initializeVersion.CommandText = "INSERT OR IGNORE INTO schema_info(key, value) VALUES ($key, $value);";
                initializeVersion.Parameters.AddWithValue("$key", SchemaKey);
                initializeVersion.Parameters.AddWithValue("$value", version.ToString(CultureInfo.InvariantCulture));
                await initializeVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (version == 1)
            {
                await using SqliteCommand migration = connection.CreateCommand();
                migration.CommandText = "UPDATE schema_info SET value = '2' WHERE key IN ('tsunami_schema_version', 'schema_version');";
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

    private async Task<DateTimeOffset?> GetLatestIssuedAtAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(issued_at)
            FROM tsunami_reports
            WHERE source_id = $source_id;
            """;
        command.Parameters.AddWithValue("$source_id", sourceId);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? null
            : ParseDateTime(value.ToString()!);
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
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
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

    private ImmutableArray<SourceStatus> BuildOfflineStatuses(
        ImmutableArray<SourceStatus> existing)
    {
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        return _realtimeSources
            .Select(source =>
            {
                SourceStatus? prior = existing.FirstOrDefault(status =>
                    string.Equals(status.SourceId, source.SourceId, StringComparison.Ordinal));
                return new SourceStatus(
                    source.SourceId,
                    SourceConnectionState.Disabled,
                    checkedAt,
                    prior?.LastReceivedAt,
                    "离线缓存，尚未连接实时数据源");
            })
            .OrderBy(status => status.SourceId, StringComparer.Ordinal)
            .ToImmutableArray();
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
                status.LastReceivedAt is null
                    ? DBNull.Value
                    : FormatDateTime(status.LastReceivedAt.Value));
            command.Parameters.AddWithValue(
                "$detail",
                status.Detail is null ? DBNull.Value : status.Detail);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void SetSourceStatuses(ImmutableArray<SourceStatus> statuses)
    {
        lock (_syncRoot)
        {
            _sourceStatuses = statuses;
        }
    }

    private static SourceConnectionState ParseConnectionState(string value) =>
        Enum.TryParse(value, ignoreCase: false, out SourceConnectionState state)
            ? state
            : throw new InvalidDataException($"无法解析海啸来源状态：{value}。");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

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
        public string? OriginTime { get; set; }
        public HypocenterDto? Hypocenter { get; set; }
        public MagnitudeDto? Magnitude { get; set; }
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
            OriginTime = report.OriginTime is null ? null : FormatDateTime(report.OriginTime.Value),
            Hypocenter = HypocenterDto.FromDomain(report.Hypocenter),
            Magnitude = MagnitudeDto.FromDomain(report.Magnitude),
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
            OriginTime = OriginTime is null ? null : ParseDateTime(OriginTime),
            Hypocenter = Hypocenter?.ToDomain(),
            Magnitude = Magnitude?.ToDomain(),
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

    private sealed class HypocenterDto
    {
        public string? Name { get; set; }
        public string? Code { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public int? DepthKm { get; set; }

        public static HypocenterDto? FromDomain(Hypocenter? value) => value is null ? null : new()
        {
            Name = value.Name,
            Code = value.Code,
            Latitude = value.Coordinate?.Latitude,
            Longitude = value.Coordinate?.Longitude,
            DepthKm = value.DepthKm,
        };

        public Hypocenter ToDomain() => new(
            Name,
            Code,
            Latitude is double latitude && Longitude is double longitude
                ? new GeoCoordinate(latitude, longitude)
                : null,
            DepthKm);
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
