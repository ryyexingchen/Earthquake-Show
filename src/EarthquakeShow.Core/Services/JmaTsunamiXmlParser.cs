using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Core.Services;

public sealed record JmaTsunamiXmlParseOptions(
    string ReportCode,
    SourceReference Source,
    DateTimeOffset? ReceivedAt = null);

public static class JmaTsunamiXmlParser
{
    private static readonly Regex CoordinatePattern = new(
        "^(?<latitude>[+-]?[0-9]+(?:\\.[0-9]+)?)(?<longitude>[+-][0-9]+(?:\\.[0-9]+)?)(?<depth>[+-][0-9]+)?/",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    public static JmaTsunamiReport Parse(string xml, JmaTsunamiXmlParseOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        XDocument document = LoadDocument(xml);
        XElement root = document.Root ?? throw new FormatException("JMAXML 海啸报文缺少根元素。");
        DateTimeOffset issuedAt = ParseDateTime(
            RequiredValue(root, "ReportDateTime"),
            "ReportDateTime");
        DateTimeOffset receivedAt = options.ReceivedAt ??
            ParseDateTime(RequiredValue(root, "DateTime"), "DateTime");
        DateTimeOffset? originTime = ParseOptionalDateTime(FirstValue(root, "OriginTime"), "OriginTime");
        Hypocenter? hypocenter = ParseHypocenter(root);
        Magnitude? magnitude = ParseMagnitude(root);

        XElement? headline = FirstDescendant(root, "Headline");
        var items = ImmutableArray.CreateBuilder<JmaTsunamiInformationItem>();
        foreach (XElement item in headline?.Descendants().Where(element =>
                     element.Name.LocalName == "Item") ?? [])
        {
            XElement? kind = item.Elements().FirstOrDefault(element => element.Name.LocalName == "Kind");
            XElement? lastKind = item.Elements().FirstOrDefault(element => element.Name.LocalName == "LastKind");
            var areas = item
                .Descendants()
                .Where(element => element.Name.LocalName == "Area")
                .Select(area => new JmaTsunamiArea(
                    FirstChildValue(area, "Name") ?? string.Empty,
                    FirstChildValue(area, "Code") ?? string.Empty))
                .Where(area => area.Name.Length > 0 || area.Code.Length > 0)
                .Distinct()
                .ToImmutableArray();
            items.Add(new JmaTsunamiInformationItem(
                FirstChildValue(kind, "Name"),
                FirstChildValue(kind, "Code"),
                FirstChildValue(lastKind, "Name"),
                FirstChildValue(lastKind, "Code"),
                areas));
        }

        XElement? tsunami = FirstDescendant(root, "Tsunami");
        ImmutableArray<JmaTsunamiForecastArea> forecastAreas = ParseForecastAreas(
            tsunami?.Elements().FirstOrDefault(element => element.Name.LocalName == "Forecast"));
        ImmutableArray<JmaTsunamiObservationStation> observationStations = ParseObservationStations(
            tsunami?.Elements().FirstOrDefault(element => element.Name.LocalName == "Observation"));
        ImmutableArray<JmaTsunamiEstimationArea> estimationAreas = ParseEstimationAreas(
            tsunami?.Elements().FirstOrDefault(element => element.Name.LocalName == "Estimation"));

        return new JmaTsunamiReport
        {
            EventId = RequiredValue(root, "EventID"),
            ReportCode = options.ReportCode.Trim(),
            InfoKind = FirstValue(root, "InfoKind"),
            Status = ParseReportStatus(FirstValue(root, "InfoType")),
            Context = ParseReportContext(FirstValue(root, "Status")),
            Serial = ParseOptionalInt(FirstValue(root, "Serial")),
            IssuedAt = issuedAt,
            ReceivedAt = receivedAt,
            OriginTime = originTime,
            Hypocenter = hypocenter,
            Magnitude = magnitude,
            HeadlineText = FirstChildValue(headline, "Text"),
            Items = items.ToImmutable(),
            ForecastAreas = forecastAreas,
            ObservationStations = observationStations,
            EstimationAreas = estimationAreas,
            Source = options.Source with { SourcePayload = options.Source.SourcePayload ?? xml },
        };
    }

    private static ImmutableArray<JmaTsunamiForecastArea> ParseForecastAreas(XElement? forecast)
    {
        if (forecast is null)
        {
            return [];
        }

        var areas = ImmutableArray.CreateBuilder<JmaTsunamiForecastArea>();
        foreach (XElement item in ChildElements(forecast, "Item"))
        {
            XElement? area = FirstChildElement(item, "Area");
            XElement? category = FirstChildElement(item, "Category");
            XElement? firstHeight = FirstChildElement(item, "FirstHeight");
            areas.Add(new JmaTsunamiForecastArea(
                FirstChildValue(area, "Name") ?? string.Empty,
                FirstChildValue(area, "Code") ?? string.Empty,
                FirstChildValue(FirstChildElement(category, "Kind"), "Name"),
                FirstChildValue(FirstChildElement(category, "Kind"), "Code"),
                FirstChildValue(FirstChildElement(category, "LastKind"), "Name"),
                FirstChildValue(FirstChildElement(category, "LastKind"), "Code"),
                ParseOptionalDateTime(FirstChildValue(firstHeight, "ArrivalTime"), "ArrivalTime"),
                FirstChildValue(firstHeight, "Condition"),
                ParseHeight(FirstChildElement(FirstChildElement(item, "MaxHeight"), "TsunamiHeight")),
                ParseForecastStations(item)));
        }

        return areas.ToImmutable();
    }

    private static ImmutableArray<JmaTsunamiStationForecast> ParseForecastStations(XElement item)
    {
        var stations = ImmutableArray.CreateBuilder<JmaTsunamiStationForecast>();
        foreach (XElement station in ChildElements(item, "Station"))
        {
            XElement? firstHeight = FirstChildElement(station, "FirstHeight");
            stations.Add(new JmaTsunamiStationForecast(
                FirstChildValue(station, "Name") ?? string.Empty,
                FirstChildValue(station, "Code") ?? string.Empty,
                ParseOptionalDateTime(FirstChildValue(station, "HighTideDateTime"), "HighTideDateTime"),
                ParseOptionalDateTime(FirstChildValue(firstHeight, "ArrivalTime"), "ArrivalTime"),
                FirstChildValue(firstHeight, "Condition")));
        }

        return stations.ToImmutable();
    }

    private static ImmutableArray<JmaTsunamiObservationStation> ParseObservationStations(XElement? observation)
    {
        if (observation is null)
        {
            return [];
        }

        var stations = ImmutableArray.CreateBuilder<JmaTsunamiObservationStation>();
        foreach (XElement item in ChildElements(observation, "Item"))
        {
            XElement? area = FirstChildElement(item, "Area");
            foreach (XElement station in ChildElements(item, "Station"))
            {
                XElement? firstHeight = FirstChildElement(station, "FirstHeight");
                XElement? maxHeight = FirstChildElement(station, "MaxHeight");
                stations.Add(new JmaTsunamiObservationStation(
                    FirstChildValue(area, "Name") ?? string.Empty,
                    FirstChildValue(area, "Code") ?? string.Empty,
                    FirstChildValue(station, "Name") ?? string.Empty,
                    FirstChildValue(station, "Code") ?? string.Empty,
                    FirstChildValue(station, "Sensor"),
                    ParseOptionalDateTime(FirstChildValue(firstHeight, "ArrivalTime"), "ArrivalTime"),
                    FirstChildValue(firstHeight, "Condition"),
                    FirstChildValue(firstHeight, "Initial"),
                    ParseOptionalDateTime(FirstChildValue(maxHeight, "DateTime"), "DateTime"),
                    FirstChildValue(maxHeight, "Condition"),
                    ParseHeight(FirstChildElement(maxHeight, "TsunamiHeight"))));
            }
        }

        return stations.ToImmutable();
    }

    private static ImmutableArray<JmaTsunamiEstimationArea> ParseEstimationAreas(XElement? estimation)
    {
        if (estimation is null)
        {
            return [];
        }

        var areas = ImmutableArray.CreateBuilder<JmaTsunamiEstimationArea>();
        foreach (XElement item in ChildElements(estimation, "Item"))
        {
            XElement? area = FirstChildElement(item, "Area");
            XElement? firstHeight = FirstChildElement(item, "FirstHeight");
            XElement? maxHeight = FirstChildElement(item, "MaxHeight");
            areas.Add(new JmaTsunamiEstimationArea(
                FirstChildValue(area, "Name") ?? string.Empty,
                FirstChildValue(area, "Code") ?? string.Empty,
                ParseOptionalDateTime(FirstChildValue(firstHeight, "ArrivalTime"), "ArrivalTime"),
                FirstChildValue(firstHeight, "Condition"),
                ParseHeight(FirstChildElement(maxHeight, "TsunamiHeight"))));
        }

        return areas.ToImmutable();
    }

    private static JmaTsunamiHeight? ParseHeight(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        double? meters = null;
        if (double.TryParse(element.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
            double.IsFinite(value))
        {
            meters = value;
        }

        return new JmaTsunamiHeight(
            meters,
            element.Attribute("description")?.Value.Trim(),
            element.Attribute("condition")?.Value.Trim(),
            element.Attribute("unit")?.Value.Trim(),
            element.Attribute("type")?.Value.Trim());
    }

    private static Hypocenter? ParseHypocenter(XElement root)
    {
        XElement? area = FirstDescendant(root, "Hypocenter")?
            .Descendants().FirstOrDefault(item => item.Name.LocalName == "Area");
        if (area is null)
        {
            return null;
        }

        GeoCoordinate? coordinate = null;
        int? depthKm = null;
        string? coordinateText = FirstChildValue(area, "Coordinate");
        Match match = coordinateText is null ? Match.Empty : CoordinatePattern.Match(coordinateText);
        if (match.Success &&
            double.TryParse(match.Groups["latitude"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude) &&
            double.TryParse(match.Groups["longitude"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude))
        {
            coordinate = new GeoCoordinate(latitude, longitude);
            if (int.TryParse(match.Groups["depth"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int depthMeters))
            {
                depthKm = Math.Abs((int)Math.Round(depthMeters / 1000d));
            }
        }

        return new Hypocenter(
            FirstChildValue(area, "Name"),
            FirstChildValue(area, "Code"),
            coordinate,
            depthKm);
    }

    private static Magnitude? ParseMagnitude(XElement root)
    {
        XElement? element = FirstDescendant(root, "Magnitude");
        if (element is null)
        {
            return null;
        }

        double? value = double.TryParse(
            element.Value.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsed) && double.IsFinite(parsed)
                ? parsed
                : null;
        return new Magnitude(
            value,
            element.Attribute("type")?.Value.Trim(),
            element.Attribute("condition")?.Value.Trim());
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

    private static string RequiredValue(XElement root, string localName) =>
        FirstValue(root, localName) is { Length: > 0 } value
            ? value
            : throw new FormatException($"JMAXML 海啸报文缺少必需字段：{localName}。");

    private static string? FirstValue(XElement root, string localName) =>
        FirstDescendant(root, localName)?.Value.Trim();

    private static string? FirstChildValue(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(item => item.Name.LocalName == localName)?.Value.Trim();

    private static XElement? FirstChildElement(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(item => item.Name.LocalName == localName);

    private static IEnumerable<XElement> ChildElements(XElement parent, string localName) =>
        parent.Elements().Where(item => item.Name.LocalName == localName);

    private static XElement? FirstDescendant(XElement root, string localName) =>
        root.DescendantsAndSelf().FirstOrDefault(item => item.Name.LocalName == localName);

    private static DateTimeOffset ParseDateTime(string value, string fieldName)
    {
        if (!DateTimeOffset.TryParse(
                value.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset result))
        {
            throw new FormatException($"JMAXML 海啸报文 {fieldName} 不是有效时间：{value}。");
        }

        return result;
    }

    private static int? ParseOptionalInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : null;

    private static DateTimeOffset? ParseOptionalDateTime(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                value.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset result))
        {
            throw new FormatException($"JMAXML 海啸报文 {fieldName} 不是有效时间：{value}。");
        }

        return result;
    }

    private static ReportStatus ParseReportStatus(string? value) => value?.Trim() switch
    {
        "発表" => ReportStatus.Issued,
        "訂正" => ReportStatus.Correction,
        "取消" or "取り消し" => ReportStatus.Cancelled,
        _ => ReportStatus.Unknown,
    };

    private static ReportContext ParseReportContext(string? value) => value?.Trim() switch
    {
        "通常" => ReportContext.Normal,
        "訓練" => ReportContext.Training,
        "試験" or "テスト" => ReportContext.Test,
        _ => ReportContext.Unknown,
    };

    private static void ValidateOptions(JmaTsunamiXmlParseOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ReportCode) ||
            !options.ReportCode.Trim().StartsWith("VTSE", StringComparison.Ordinal))
        {
            throw new ArgumentException("海啸报文代码必须是 VTSE*。", nameof(options));
        }

        if (options.Source is null || string.IsNullOrWhiteSpace(options.Source.SourceMessageId))
        {
            throw new ArgumentException("海啸报文来源消息 ID 不能为空。", nameof(options));
        }
    }
}
