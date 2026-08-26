using System.Collections.Immutable;
using System.Text.RegularExpressions;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;

namespace EarthquakeShow.Infrastructure.Sources;

/// <summary>
/// 将本地保存的 JMA 地震 XML 转换为统一报文模型。
/// </summary>
public sealed class JmaXmlLocalFileImporter
{
    private const string SourceName = "jma-xml";
    private static readonly Regex ReportCodePattern = new(
        "_(?<code>VXSE51|VXSE52|VXSE53)_",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IReadOnlyDictionary<string, GeoCoordinate>? _stationCoordinates;
    private readonly JmaStationCoordinateCatalog? _stationCatalog;
    private readonly JmaIntensityRegionCatalog? _regionCatalog;

    public JmaXmlLocalFileImporter(
        IReadOnlyDictionary<string, GeoCoordinate>? stationCoordinates = null,
        JmaStationCoordinateCatalog? stationCatalog = null,
        JmaIntensityRegionCatalog? regionCatalog = null)
    {
        _stationCoordinates = stationCoordinates;
        _stationCatalog = stationCatalog;
        _regionCatalog = regionCatalog;
    }

    public async Task<JmaXmlLocalFileImportResult> ImportAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        string fullDirectoryPath = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullDirectoryPath))
        {
            throw new DirectoryNotFoundException($"本地 JMA XML 目录不存在：{fullDirectoryPath}。");
        }

        var reports = ImmutableArray.CreateBuilder<EarthquakeReport>();
        var skippedFiles = ImmutableArray.CreateBuilder<string>();
        var failures = ImmutableArray.CreateBuilder<JmaXmlLocalFileImportFailure>();

        foreach (string filePath in Directory.EnumerateFiles(fullDirectoryPath, "*.xml", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(filePath);
            Match match = ReportCodePattern.Match(fileName);
            if (!match.Success)
            {
                skippedFiles.Add(filePath);
                continue;
            }

            string reportCode = match.Groups["code"].Value.ToUpperInvariant();
            try
            {
                string xml = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                reports.Add(JmaXmlParser.Parse(
                    xml,
                    new JmaXmlParseOptions(
                        reportCode,
                        new SourceReference(
                            SourceName,
                            fileName,
                            new Uri(filePath),
                            xml),
                        StationCoordinates: _stationCoordinates,
                        StationCatalog: _stationCatalog,
                        RegionCatalog: _regionCatalog)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or ArgumentException or System.Xml.XmlException)
            {
                failures.Add(new JmaXmlLocalFileImportFailure(filePath, exception.Message));
            }
        }

        return new JmaXmlLocalFileImportResult(
            reports.ToImmutable(),
            skippedFiles.ToImmutable(),
            failures.ToImmutable());
    }
}

public sealed record JmaXmlLocalFileImportFailure(
    string FilePath,
    string Error);

public sealed record JmaXmlLocalFileImportResult(
    ImmutableArray<EarthquakeReport> Reports,
    ImmutableArray<string> SkippedFiles,
    ImmutableArray<JmaXmlLocalFileImportFailure> Failures,
    int SavedReportCount = 0);
