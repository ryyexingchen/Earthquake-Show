using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.ViewModels;

public sealed record EarthquakeEventListItemViewModel
{
    private static readonly TimeZoneInfo JapanTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    public required string EventId { get; init; }

    public required string Title { get; init; }

    public required string OriginTimeText { get; init; }

    public required string UpdatedAtText { get; init; }

    public required string MagnitudeText { get; init; }

    public required string IntensityText { get; init; }

    public required string ReportText { get; init; }

    public required string TsunamiText { get; init; }

    public required JmaIntensity MaxIntensity { get; init; }

    public required bool IsNew { get; init; }

    public static EarthquakeEventListItemViewModel Create(
        EarthquakeEvent earthquakeEvent,
        bool isNew)
    {
        ArgumentNullException.ThrowIfNull(earthquakeEvent);
        EarthquakeEventSummary? summary = earthquakeEvent.Summary;

        return new EarthquakeEventListItemViewModel
        {
            EventId = earthquakeEvent.EventId,
            Title = summary?.Hypocenter?.Name ?? "震源不明",
            OriginTimeText = summary?.OriginTime is DateTimeOffset originTime
                ? $"发生 {FormatJapanTime(originTime)}"
                : "发生时间不明",
            UpdatedAtText = summary is null
                ? "发布时间不明"
                : $"发布 {FormatJapanTime(summary.UpdatedAt)}",
            MagnitudeText = summary?.Magnitude?.Value is double magnitude
                ? $"M {magnitude:0.0}"
                : "M 不明",
            IntensityText = summary?.MaxIntensity.ToCode() switch
            {
                null or "unknown" => "--",
                "5-lower" => "5弱",
                "5-upper" => "5强",
                "6-lower" => "6弱",
                "6-upper" => "6强",
                string code => code,
            },
            ReportText = GetReportText(earthquakeEvent.PreferredReport),
            TsunamiText = string.IsNullOrWhiteSpace(earthquakeEvent.PreferredReport?.TsunamiComment)
                ? "不明"
                : $"{earthquakeEvent.PreferredReport.TsunamiComment}",
            MaxIntensity = summary?.MaxIntensity ?? JmaIntensity.Unknown,
            IsNew = isNew,
        };
    }

    private static string FormatJapanTime(DateTimeOffset value)
    {
        return TimeZoneInfo.ConvertTime(value, JapanTimeZone).ToString("MM-dd HH:mm");
    }

    private static string GetStatusText(ReportStatus status)
    {
        return status switch
        {
            ReportStatus.Issued => "发布",
            ReportStatus.Correction => "订正",
            ReportStatus.Cancelled => "取消",
            _ => "状态不明",
        };
    }

    private static string GetReportText(EarthquakeReport? report)
    {
        if (report is null)
        {
            return "报文状态不明";
        }

        string reportType = report.ReportType switch
        {
            EarthquakeReportType.SeismicIntensity => "震度速報",
            EarthquakeReportType.Hypocenter => "震源情报",
            EarthquakeReportType.HypocenterAndIntensity => "震源・震度情报",
            EarthquakeReportType.DistantEarthquake => report.DistantEarthquakeKind ==
                DistantEarthquakeKind.VolcanicEruption
                    ? "远地火山喷发"
                    : "远地地震情报",
            _ => report.ReportCode,
        };
        return $"{reportType} · {GetStatusText(report.Status)}";
    }
}
