using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Core.Services;

public sealed record JmaTsunamiXmlParseOptions(
    string ReportCode,
    SourceReference Source,
    DateTimeOffset? ReceivedAt = null);

public static class JmaTsunamiXmlParser
{
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

        return new JmaTsunamiReport
        {
            EventId = RequiredValue(root, "EventID"),
            ReportCode = options.ReportCode.Trim(),
            InfoKind = FirstValue(root, "InfoKind"),
            Status = ParseReportStatus(FirstValue(root, "InfoType")),
            Serial = ParseOptionalInt(FirstValue(root, "Serial")),
            IssuedAt = issuedAt,
            ReceivedAt = receivedAt,
            HeadlineText = FirstChildValue(headline, "Text"),
            Items = items.ToImmutable(),
            Source = options.Source with { SourcePayload = options.Source.SourcePayload ?? xml },
        };
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

    private static ReportStatus ParseReportStatus(string? value) => value?.Trim() switch
    {
        "発表" => ReportStatus.Issued,
        "訂正" => ReportStatus.Correction,
        "取消" or "取り消し" => ReportStatus.Cancelled,
        _ => ReportStatus.Unknown,
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
