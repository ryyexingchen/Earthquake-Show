using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Core.Services;

public sealed record JmaXmlParseOptions(
    string ReportCode,
    SourceReference Source,
    DateTimeOffset? ReceivedAt = null,
    IReadOnlyDictionary<string, GeoCoordinate>? StationCoordinates = null,
    JmaStationCoordinateCatalog? StationCatalog = null,
    JmaIntensityRegionCatalog? RegionCatalog = null);

public sealed record JmaXmlFixture(
    string FilePath,
    string ReportCode,
    string SourceMessageId);

public static class JmaXmlParser
{
    private static readonly Regex CoordinatePattern = new(
        "^(?<latitude>[+-]?[0-9]+(?:\\.[0-9]+)?)(?<longitude>[+-][0-9]+(?:\\.[0-9]+)?)(?<depth>[+-][0-9]+)?/",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static EarthquakeReport Parse(string xml, JmaXmlParseOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        XDocument document = LoadDocument(xml);
        XElement root = document.Root ?? throw new FormatException("JMAXML 缺少根元素。");
        string eventId = RequiredValue(root, "EventID");
        DateTimeOffset issuedAt = ParseDateTime(RequiredValue(root, "ReportDateTime"), "ReportDateTime");
        DateTimeOffset receivedAt = options.ReceivedAt ??
            ParseDateTime(RequiredValue(root, "DateTime"), "DateTime");
        DateTimeOffset? originTime = ParseOptionalDateTime(root, "OriginTime");
        Hypocenter? hypocenter = ParseHypocenter(root);
        Magnitude? magnitude = ParseMagnitude(root);
        (ImmutableArray<IntensityArea> Areas,
            ImmutableArray<IntensityMunicipality> Municipalities,
            ImmutableArray<IntensityStation> Stations,
            JmaIntensity MaxIntensity) = ParseIntensity(
                root,
                options.StationCoordinates,
                options.StationCatalog,
                options.RegionCatalog);

        string reportCode = options.ReportCode.Trim();
        SourceReference source = options.Source with
        {
            SourcePayload = options.Source.SourcePayload ?? xml,
        };
        return new EarthquakeReport
        {
            EventId = eventId,
            ReportCode = reportCode,
            ReportType = GetReportType(reportCode, root),
            DistantEarthquakeKind = ParseDistantEarthquakeKind(root),
            Status = ParseReportStatus(FirstValue(root, "InfoType")),
            Context = ParseReportContext(FirstValue(root, "Status")),
            Serial = ParseOptionalInt(FirstValue(root, "Serial")),
            OriginTime = originTime,
            IssuedAt = issuedAt,
            ReceivedAt = receivedAt,
            Hypocenter = hypocenter,
            Magnitude = magnitude,
            MaxIntensity = MaxIntensity,
            IntensityAreas = Areas,
            IntensityMunicipalities = Municipalities,
            IntensityStations = Stations,
            TsunamiComment = ParseTsunamiComment(root),
            TsunamiCommentCode = ParseTsunamiCommentCode(root),
            Source = source,
        };
    }

    public static ImmutableArray<EarthquakeReport> LoadFixtures(
        IEnumerable<JmaXmlFixture> fixtures,
        IReadOnlyDictionary<string, GeoCoordinate>? stationCoordinates = null)
    {
        return LoadFixtures(fixtures, stationCoordinates, null);
    }

    public static ImmutableArray<EarthquakeReport> LoadFixtures(
        IEnumerable<JmaXmlFixture> fixtures,
        JmaStationCoordinateCatalog stationCatalog)
    {
        ArgumentNullException.ThrowIfNull(stationCatalog);
        return LoadFixtures(fixtures, null, stationCatalog);
    }

    private static ImmutableArray<EarthquakeReport> LoadFixtures(
        IEnumerable<JmaXmlFixture> fixtures,
        IReadOnlyDictionary<string, GeoCoordinate>? stationCoordinates,
        JmaStationCoordinateCatalog? stationCatalog)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        var reports = ImmutableArray.CreateBuilder<EarthquakeReport>();
        foreach (JmaXmlFixture fixture in fixtures)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fixture.FilePath);
            string xml = File.ReadAllText(fixture.FilePath, Encoding.UTF8);
            reports.Add(Parse(
                xml,
                new JmaXmlParseOptions(
                    fixture.ReportCode,
                    new SourceReference("jma-xml", fixture.SourceMessageId),
                    StationCoordinates: stationCoordinates,
                    StationCatalog: stationCatalog)));
        }

        return reports.ToImmutable();
    }

    private static XDocument LoadDocument(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 10_000_000,
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static Hypocenter? ParseHypocenter(XElement root)
    {
        XElement? area = FirstDescendant(root, "Hypocenter")?
            .Descendants()
            .FirstOrDefault(item => item.Name.LocalName == "Area");
        if (area is null)
        {
            return null;
        }

        string? name = FirstChildValue(area, "Name");
        string? code = FirstChildValue(area, "Code");
        string? coordinateText = FirstChildValue(area, "Coordinate");
        GeoCoordinate? coordinate = null;
        int? depthKm = null;
        if (!string.IsNullOrWhiteSpace(coordinateText))
        {
            (coordinate, depthKm) = ParseCoordinate(coordinateText);
        }

        return new Hypocenter(name, code, coordinate, depthKm);
    }

    private static Magnitude? ParseMagnitude(XElement root)
    {
        XElement? element = FirstDescendant(root, "Magnitude");
        if (element is null)
        {
            return null;
        }

        double? value = null;
        if (double.TryParse(
                element.Value.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed) &&
            double.IsFinite(parsed))
        {
            value = parsed;
        }

        string? type = element.Attribute("type")?.Value;
        string? condition = element.Attribute("condition")?.Value;
        return new Magnitude(value, type, condition);
    }

    private static (
        ImmutableArray<IntensityArea> Areas,
        ImmutableArray<IntensityMunicipality> Municipalities,
        ImmutableArray<IntensityStation> Stations,
        JmaIntensity MaxIntensity) ParseIntensity(
        XElement root,
        IReadOnlyDictionary<string, GeoCoordinate>? stationCoordinates,
        JmaStationCoordinateCatalog? stationCatalog,
        JmaIntensityRegionCatalog? regionCatalog)
    {
        XElement? observation = FirstDescendant(root, "Observation");
        if (observation is null)
        {
            return ([], [], [], JmaIntensity.Unknown);
        }

        JmaIntensity maxIntensity = ParseIntensityValue(FirstChildValue(observation, "MaxInt"));
        var areas = ImmutableArray.CreateBuilder<IntensityArea>();
        var municipalities = ImmutableArray.CreateBuilder<IntensityMunicipality>();
        var stations = ImmutableArray.CreateBuilder<IntensityStation>();

        foreach (XElement prefecture in ChildElements(observation, "Pref"))
        {
            string prefectureName = FirstChildValue(prefecture, "Name") ?? string.Empty;
            string prefectureCode = FirstChildValue(prefecture, "Code") ?? string.Empty;
            foreach (XElement area in ChildElements(prefecture, "Area"))
            {
                string areaCode = FirstChildValue(area, "Code") ?? string.Empty;
                string areaName = FirstChildValue(area, "Name") ?? areaCode;
                string resolvedPrefectureCode = prefectureCode;
                string resolvedPrefectureName = prefectureName;
                if (regionCatalog is not null &&
                    ((regionCatalog.TryGetArea(areaCode, out JmaIntensityAreaDefinition definition) &&
                        string.Equals(definition.Name, areaName, StringComparison.Ordinal)) ||
                     (regionCatalog.TryResolveAreaName(areaName, out definition))))
                {
                    resolvedPrefectureCode = definition.PrefectureCode;
                    resolvedPrefectureName = definition.PrefectureName;
                }
                areas.Add(new IntensityArea(
                    areaCode,
                    areaName,
                    resolvedPrefectureCode,
                    resolvedPrefectureName,
                    ParseIntensityValue(FirstChildValue(area, "MaxInt"))));

                foreach (XElement city in ChildElements(area, "City"))
                {
                    string cityCode = FirstChildValue(city, "Code") ?? string.Empty;
                    municipalities.Add(new IntensityMunicipality(
                        cityCode,
                        FirstChildValue(city, "Name") ?? cityCode,
                        areaCode,
                        ParseIntensityValue(FirstChildValue(city, "MaxInt"))));

                    foreach (XElement station in ChildElements(city, "IntensityStation"))
                    {
                        string stationCode = FirstChildValue(station, "Code") ?? string.Empty;
                        string stationName = FirstChildValue(station, "Name") ?? stationCode;
                        GeoCoordinate? coordinate = stationCoordinates is not null &&
                            stationCoordinates.TryGetValue(stationCode, out GeoCoordinate value)
                            ? value
                            : null;
                        if (coordinate is null && stationCatalog is not null &&
                            stationCatalog.TryResolve(stationCode, stationName, out value, out _))
                        {
                            coordinate = value;
                        }

                        stations.Add(new IntensityStation(
                            stationCode,
                            stationName,
                            cityCode,
                            ParseIntensityValue(FirstChildValue(station, "Int")),
                            coordinate));
                    }
                }
            }
        }

        return (areas.ToImmutable(), municipalities.ToImmutable(), stations.ToImmutable(), maxIntensity);
    }

    private static (GeoCoordinate Coordinate, int? DepthKm) ParseCoordinate(string text)
    {
        Match match = CoordinatePattern.Match(text.Trim());
        if (!match.Success)
        {
            throw new FormatException($"无法解析 JMAXML 坐标：{text}。");
        }

        double latitude = double.Parse(match.Groups["latitude"].Value, CultureInfo.InvariantCulture);
        double longitude = double.Parse(match.Groups["longitude"].Value, CultureInfo.InvariantCulture);
        int? depthKm = null;
        if (match.Groups["depth"].Success &&
            int.TryParse(match.Groups["depth"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int depthMeters))
        {
            depthKm = (int)Math.Round(Math.Abs(depthMeters) / 1000d, MidpointRounding.AwayFromZero);
        }

        return (new GeoCoordinate(latitude, longitude), depthKm);
    }

    private static JmaIntensity ParseIntensityValue(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (JmaIntensityExtensions.TryParseCode(normalized, out JmaIntensity intensity))
        {
            return intensity;
        }

        return normalized switch
        {
            "不明" or "-" or "－" or "" => JmaIntensity.Unknown,
            "５弱" or "5弱" or "5-" => JmaIntensity.FiveLower,
            "５強" or "5強" or "5+" => JmaIntensity.FiveUpper,
            "６弱" or "6弱" or "6-" => JmaIntensity.SixLower,
            "６強" or "6強" or "6+" => JmaIntensity.SixUpper,
            "７" => JmaIntensity.Seven,
            "１" => JmaIntensity.One,
            "２" => JmaIntensity.Two,
            "３" => JmaIntensity.Three,
            "４" => JmaIntensity.Four,
            _ => JmaIntensity.Unknown,
        };
    }

    private static string? ParseTsunamiComment(XElement root)
    {
        string[] comments = root
            .Descendants()
            .Where(element => element.Name.LocalName == "Text" &&
                element.Parent?.Name.LocalName is "ForecastComment" or "VarComment" &&
                element.Value.Contains("津波", StringComparison.Ordinal))
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return comments.Length == 0 ? null : string.Join("；", comments);
    }

    private static string? ParseTsunamiCommentCode(XElement root)
    {
        return root
            .Descendants()
            .Where(element => element.Name.LocalName == "ForecastComment" ||
                element.Name.LocalName == "VarComment")
            .Select(element =>
            {
                string? text = FirstChildValue(element, "Text");
                string? code = FirstChildValue(element, "Code");
                return text?.Contains("津波", StringComparison.Ordinal) == true
                    ? code
                    : null;
            })
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
    }

    private static EarthquakeReportType GetReportType(string reportCode, XElement root)
    {
        if (IsDistantEarthquake(root))
        {
            return EarthquakeReportType.DistantEarthquake;
        }

        return reportCode switch
        {
            "VXSE51" => EarthquakeReportType.SeismicIntensity,
            "VXSE52" => EarthquakeReportType.Hypocenter,
            "VXSE53" => EarthquakeReportType.HypocenterAndIntensity,
            _ => FirstValue(root, "InfoKind") switch
            {
                "震度速報" => EarthquakeReportType.SeismicIntensity,
                "震源速報" => EarthquakeReportType.Hypocenter,
                "地震情報" => EarthquakeReportType.HypocenterAndIntensity,
                _ => EarthquakeReportType.Unknown,
            },
        };
    }

    private static DistantEarthquakeKind? ParseDistantEarthquakeKind(XElement root)
    {
        if (!IsDistantEarthquake(root))
        {
            return null;
        }

        string? comment = FirstValue(root, "FreeFormComment");
        return comment?.Contains("噴火", StringComparison.Ordinal) == true
            ? DistantEarthquakeKind.VolcanicEruption
            : DistantEarthquakeKind.Earthquake;
    }

    private static bool IsDistantEarthquake(XElement root)
    {
        XElement? head = FirstDescendant(root, "Head");
        return head is not null && string.Equals(
            FirstChildValue(head, "Title"),
            "遠地地震に関する情報",
            StringComparison.Ordinal);
    }

    private static ReportStatus ParseReportStatus(string? value)
    {
        return value?.Trim() switch
        {
            "発表" => ReportStatus.Issued,
            "訂正" => ReportStatus.Correction,
            "取消" or "取り消し" => ReportStatus.Cancelled,
            _ => ReportStatus.Unknown,
        };
    }

    private static ReportContext ParseReportContext(string? value)
    {
        return value?.Trim() switch
        {
            "通常" => ReportContext.Normal,
            "訓練" => ReportContext.Training,
            "試験" or "テスト" => ReportContext.Test,
            _ => ReportContext.Unknown,
        };
    }

    private static int? ParseOptionalInt(string? value)
    {
        return int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : null;
    }

    private static DateTimeOffset? ParseOptionalDateTime(XElement root, string localName)
    {
        string? value = FirstValue(root, localName);
        return string.IsNullOrWhiteSpace(value) ? null : ParseDateTime(value, localName);
    }

    private static DateTimeOffset ParseDateTime(string value, string fieldName)
    {
        if (!DateTimeOffset.TryParse(
                value.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset result))
        {
            throw new FormatException($"JMAXML {fieldName} 不是有效时间：{value}。");
        }

        return result;
    }

    private static string RequiredValue(XElement root, string localName)
    {
        return FirstValue(root, localName) is { Length: > 0 } value
            ? value
            : throw new FormatException($"JMAXML 缺少必需字段：{localName}。");
    }

    private static string? FirstValue(XElement root, string localName)
    {
        return FirstDescendant(root, localName)?.Value.Trim();
    }

    private static string? FirstChildValue(XElement parent, string localName)
    {
        return parent.Elements().FirstOrDefault(item => item.Name.LocalName == localName)?.Value.Trim();
    }

    private static XElement? FirstDescendant(XElement root, string localName)
    {
        return root.DescendantsAndSelf().FirstOrDefault(item => item.Name.LocalName == localName);
    }

    private static IEnumerable<XElement> ChildElements(XElement parent, string localName)
    {
        return parent.Elements().Where(item => item.Name.LocalName == localName);
    }

    private static void ValidateOptions(JmaXmlParseOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ReportCode))
        {
            throw new ArgumentException("报文代码不能为空。", nameof(options));
        }

        if (options.Source is null ||
            string.IsNullOrWhiteSpace(options.Source.SourceId) ||
            string.IsNullOrWhiteSpace(options.Source.SourceMessageId))
        {
            throw new ArgumentException("来源 ID 和来源消息 ID 不能为空。", nameof(options));
        }
    }
}

public static class JmaStationCatalog
{
    public static ImmutableDictionary<string, GeoCoordinate> LoadCsv(string csv)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csv);
        string[] lines = csv.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            throw new FormatException("JMA 观测点 CSV 缺少数据行。");
        }

        var result = ImmutableDictionary.CreateBuilder<string, GeoCoordinate>(StringComparer.Ordinal);
        for (int index = 1; index < lines.Length; index++)
        {
            string[] fields = ParseCsvLine(lines[index]);
            if (fields.Length < 7)
            {
                throw new FormatException($"JMA 观测点 CSV 第 {index + 1} 行字段不足。");
            }

            if (!double.TryParse(fields[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude) ||
                !double.TryParse(fields[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude))
            {
                throw new FormatException($"JMA 观测点 CSV 第 {index + 1} 行坐标无效。");
            }

            string code = fields[0].Trim();
            if (code.Length == 0 || !result.TryAdd(code, new GeoCoordinate(latitude, longitude)))
            {
                throw new FormatException($"JMA 观测点 CSV 存在重复或空编码：第 {index + 1} 行。");
            }
        }

        return result.ToImmutable();
    }

    public static ImmutableDictionary<string, GeoCoordinate> LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadCsv(File.ReadAllText(path, Encoding.UTF8));
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char current = line[index];
            if (current == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (current == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(current);
            }
        }

        fields.Add(field.ToString());
        return fields.ToArray();
    }
}
