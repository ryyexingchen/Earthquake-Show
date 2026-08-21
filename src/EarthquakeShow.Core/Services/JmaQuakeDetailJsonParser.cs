using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Core.Services;

public sealed record JmaQuakeDetailJsonParseOptions(
    string ReportCode,
    SourceReference Source,
    DateTimeOffset? ReceivedAt = null,
    JmaStationCoordinateCatalog? StationCatalog = null);

public static partial class JmaQuakeDetailJsonParser
{
    [GeneratedRegex(
        "^(?<latitude>[+-]?[0-9]+(?:\\.[0-9]+)?)(?<longitude>[+-][0-9]+(?:\\.[0-9]+)?)(?<depth>[+-][0-9]+)?/",
        RegexOptions.CultureInvariant)]
    private static partial Regex DecimalCoordinatePattern();

    [GeneratedRegex(
        "^(?<latitudeSign>[+-])(?<latitudeDegrees>[0-9]{2})(?<latitudeMinutes>[0-9]{2}(?:\\.[0-9]+)?)(?<longitudeSign>[+-])(?<longitudeDegrees>[0-9]{3})(?<longitudeMinutes>[0-9]{2}(?:\\.[0-9]+)?)(?<depth>[+-][0-9]+)?/",
        RegexOptions.CultureInvariant)]
    private static partial Regex WgsCoordinatePattern();

    public static EarthquakeReport Parse(
        string json,
        JmaQuakeDetailJsonParseOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ReportCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Source.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Source.SourceMessageId);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement control = RequiredObject(root, "Control");
        JsonElement head = RequiredObject(root, "Head");
        JsonElement body = RequiredObject(root, "Body");
        string eventId = RequiredString(head, "EventID");
        DateTimeOffset issuedAt = ParseDateTime(
            RequiredString(head, "ReportDateTime"),
            "Head.ReportDateTime");
        DateTimeOffset receivedAt = options.ReceivedAt ?? ParseDateTime(
            RequiredString(control, "DateTime"),
            "Control.DateTime");
        Hypocenter? hypocenter = ParseHypocenter(body);
        Magnitude? magnitude = ParseMagnitude(body);
        (ImmutableArray<IntensityArea> Areas,
            ImmutableArray<IntensityMunicipality> Municipalities,
            ImmutableArray<IntensityStation> Stations,
            JmaIntensity MaxIntensity) = ParseIntensity(body, options.StationCatalog);
        string reportCode = options.ReportCode.Trim();

        return new EarthquakeReport
        {
            EventId = eventId,
            ReportCode = reportCode,
            ReportType = ParseReportType(reportCode),
            Status = ParseReportStatus(OptionalString(head, "InfoType")),
            Context = ParseReportContext(OptionalString(control, "Status")),
            Serial = ParseOptionalInt(OptionalString(head, "Serial")),
            OriginTime = ParseOptionalDateTime(body, "Earthquake", "OriginTime"),
            IssuedAt = issuedAt,
            ReceivedAt = receivedAt,
            Hypocenter = hypocenter,
            Magnitude = magnitude,
            MaxIntensity = MaxIntensity,
            IntensityAreas = Areas,
            IntensityMunicipalities = Municipalities,
            IntensityStations = Stations,
            TsunamiComment = ParseTsunamiComment(body),
            Source = options.Source with
            {
                SourcePayload = options.Source.SourcePayload ?? json,
            },
        };
    }

    private static Hypocenter? ParseHypocenter(JsonElement body)
    {
        if (!TryGetObject(body, "Earthquake", out JsonElement earthquake) ||
            !TryGetObject(earthquake, "Hypocenter", out JsonElement hypocenter) ||
            !TryGetObject(hypocenter, "Area", out JsonElement area))
        {
            return null;
        }

        string? coordinateText = OptionalString(area, "Coordinate_WGS")
            ?? OptionalString(area, "Coordinate");
        GeoCoordinate? coordinate = null;
        int? depthKm = null;
        if (!string.IsNullOrWhiteSpace(coordinateText))
        {
            (coordinate, depthKm) = ParseCoordinate(coordinateText);
        }

        return new Hypocenter(
            OptionalString(area, "Name"),
            OptionalString(area, "Code"),
            coordinate,
            depthKm);
    }

    private static Magnitude? ParseMagnitude(JsonElement body)
    {
        if (!TryGetObject(body, "Earthquake", out JsonElement earthquake) ||
            OptionalString(earthquake, "Magnitude") is not { } value)
        {
            return null;
        }

        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double magnitude) && double.IsFinite(magnitude)
                ? new Magnitude(magnitude)
                : new Magnitude(null);
    }

    private static (
        ImmutableArray<IntensityArea> Areas,
        ImmutableArray<IntensityMunicipality> Municipalities,
        ImmutableArray<IntensityStation> Stations,
        JmaIntensity MaxIntensity) ParseIntensity(
        JsonElement body,
        JmaStationCoordinateCatalog? stationCatalog)
    {
        if (!TryGetObject(body, "Intensity", out JsonElement intensity) ||
            !TryGetObject(intensity, "Observation", out JsonElement observation))
        {
            return ([], [], [], JmaIntensity.Unknown);
        }

        var areas = ImmutableArray.CreateBuilder<IntensityArea>();
        var municipalities = ImmutableArray.CreateBuilder<IntensityMunicipality>();
        var stations = ImmutableArray.CreateBuilder<IntensityStation>();
        foreach (JsonElement prefecture in Objects(observation, "Pref"))
        {
            string prefectureCode = OptionalString(prefecture, "Code") ?? string.Empty;
            string prefectureName = OptionalString(prefecture, "Name") ?? prefectureCode;
            foreach (JsonElement area in Objects(prefecture, "Area"))
            {
                string areaCode = OptionalString(area, "Code") ?? string.Empty;
                areas.Add(new IntensityArea(
                    areaCode,
                    OptionalString(area, "Name") ?? areaCode,
                    prefectureCode,
                    prefectureName,
                    ParseIntensity(OptionalString(area, "MaxInt"))));

                foreach (JsonElement city in Objects(area, "City"))
                {
                    string cityCode = OptionalString(city, "Code") ?? string.Empty;
                    municipalities.Add(new IntensityMunicipality(
                        cityCode,
                        OptionalString(city, "Name") ?? cityCode,
                        areaCode,
                        ParseIntensity(OptionalString(city, "MaxInt"))));

                    foreach (JsonElement station in Objects(city, "IntensityStation"))
                    {
                        string stationCode = OptionalString(station, "Code") ?? string.Empty;
                        string stationName = OptionalString(station, "Name") ?? stationCode;
                        GeoCoordinate? coordinate = ParseStationCoordinate(station);
                        if (coordinate is null && stationCatalog is not null &&
                            stationCatalog.TryResolve(
                                stationCode,
                                stationName,
                                out GeoCoordinate resolved,
                                out _))
                        {
                            coordinate = resolved;
                        }

                        stations.Add(new IntensityStation(
                            stationCode,
                            stationName,
                            cityCode,
                            ParseIntensity(OptionalString(station, "Int")),
                            coordinate));
                    }
                }
            }
        }

        return (
            areas.ToImmutable(),
            municipalities.ToImmutable(),
            stations.ToImmutable(),
            ParseIntensity(OptionalString(observation, "MaxInt")));
    }

    private static GeoCoordinate? ParseStationCoordinate(JsonElement station)
    {
        if (!TryGetObject(station, "latlon", out JsonElement latlon) ||
            !TryGetDouble(latlon, "lat", out double latitude) ||
            !TryGetDouble(latlon, "lon", out double longitude))
        {
            return null;
        }

        return new GeoCoordinate(latitude, longitude);
    }

    private static (GeoCoordinate Coordinate, int? DepthKm) ParseCoordinate(string text)
    {
        Match wgsMatch = WgsCoordinatePattern().Match(text.Trim());
        if (wgsMatch.Success)
        {
            double latitude = ParseDegreesMinutes(
                wgsMatch.Groups["latitudeSign"].Value,
                wgsMatch.Groups["latitudeDegrees"].Value,
                wgsMatch.Groups["latitudeMinutes"].Value);
            double longitude = ParseDegreesMinutes(
                wgsMatch.Groups["longitudeSign"].Value,
                wgsMatch.Groups["longitudeDegrees"].Value,
                wgsMatch.Groups["longitudeMinutes"].Value);
            return (
                new GeoCoordinate(latitude, longitude),
                ParseDepthKm(wgsMatch.Groups["depth"]));
        }

        Match decimalMatch = DecimalCoordinatePattern().Match(text.Trim());
        if (!decimalMatch.Success)
        {
            throw new FormatException($"无法解析 JMA 详细 JSON 坐标：{text}。");
        }

        return (
            new GeoCoordinate(
                double.Parse(decimalMatch.Groups["latitude"].Value, CultureInfo.InvariantCulture),
                double.Parse(decimalMatch.Groups["longitude"].Value, CultureInfo.InvariantCulture)),
            ParseDepthKm(decimalMatch.Groups["depth"]));
    }

    private static double ParseDegreesMinutes(string sign, string degrees, string minutes)
    {
        double value = double.Parse(degrees, CultureInfo.InvariantCulture) +
            (double.Parse(minutes, CultureInfo.InvariantCulture) / 60d);
        return sign == "-" ? -value : value;
    }

    private static int? ParseDepthKm(Group depthGroup)
    {
        return depthGroup.Success && int.TryParse(
            depthGroup.Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int depthMeters)
                ? (int)Math.Round(
                    Math.Abs(depthMeters) / 1000d,
                    MidpointRounding.AwayFromZero)
                : null;
    }

    private static JmaIntensity ParseIntensity(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        normalized = normalized switch
        {
            "5-" or "5弱" or "５弱" => "5-lower",
            "5+" or "5強" or "５強" => "5-upper",
            "6-" or "6弱" or "６弱" => "6-lower",
            "6+" or "6強" or "６強" => "6-upper",
            "１" => "1",
            "２" => "2",
            "３" => "3",
            "４" => "4",
            "７" => "7",
            _ => normalized,
        };
        return JmaIntensityExtensions.TryParseCode(normalized, out JmaIntensity result)
            ? result
            : JmaIntensity.Unknown;
    }

    private static EarthquakeReportType ParseReportType(string reportCode)
    {
        return reportCode switch
        {
            "VXSE51" => EarthquakeReportType.SeismicIntensity,
            "VXSE52" or "VXSE61" => EarthquakeReportType.Hypocenter,
            "VXSE53" => EarthquakeReportType.HypocenterAndIntensity,
            _ => EarthquakeReportType.Unknown,
        };
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

    private static string? ParseTsunamiComment(JsonElement body)
    {
        if (!TryGetObject(body, "Comments", out JsonElement comments) ||
            !TryGetObject(comments, "ForecastComment", out JsonElement forecastComment))
        {
            return null;
        }

        string? text = OptionalString(forecastComment, "Text");
        return text?.Contains("津波", StringComparison.Ordinal) == true ? text.Trim() : null;
    }

    private static DateTimeOffset? ParseOptionalDateTime(
        JsonElement root,
        string objectName,
        string propertyName)
    {
        return TryGetObject(root, objectName, out JsonElement child) &&
            OptionalString(child, propertyName) is { } value
                ? ParseDateTime(value, $"{objectName}.{propertyName}")
                : null;
    }

    private static DateTimeOffset ParseDateTime(string value, string fieldName)
    {
        if (!DateTimeOffset.TryParse(
            value.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset result))
        {
            throw new FormatException($"JMA 详细 JSON {fieldName} 不是有效时间：{value}。");
        }

        return result;
    }

    private static int? ParseOptionalInt(string? value)
    {
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int result)
                ? result
                : null;
    }

    private static JsonElement RequiredObject(JsonElement parent, string propertyName)
    {
        return TryGetObject(parent, propertyName, out JsonElement result)
            ? result
            : throw new FormatException($"JMA 详细 JSON 缺少对象：{propertyName}。");
    }

    private static string RequiredString(JsonElement parent, string propertyName)
    {
        return OptionalString(parent, propertyName) is { Length: > 0 } value
            ? value
            : throw new FormatException($"JMA 详细 JSON 缺少字段：{propertyName}。");
    }

    private static string? OptionalString(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString())
                ? null
                : value.GetString()!.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static bool TryGetObject(
        JsonElement parent,
        string propertyName,
        out JsonElement result)
    {
        result = default;
        return parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(propertyName, out result) &&
            result.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetDouble(
        JsonElement parent,
        string propertyName,
        out double value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(propertyName, out JsonElement element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out value) &&
            double.IsFinite(value);
    }

    private static IEnumerable<JsonElement> Objects(
        JsonElement parent,
        string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(propertyName, out JsonElement value))
        {
            yield break;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            yield return value;
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                yield return item;
            }
        }
    }
}
