using System.Net;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Infrastructure.Sources;
using Xunit;

namespace EarthquakeShow.Infrastructure.Tests;

public sealed class JmaJsonEarthquakeSourceTests
{
    [Fact]
    public async Task Fetch_ValidList_MapsSummaryReport()
    {
        const string json = """
            [
              {
                "ctt": "20260820010130",
                "eid": "20260820010000",
                "rdt": "2026-08-20T10:01:00+09:00",
                "ttl": "震源・震度情報",
                "ift": "発表",
                "ser": "1",
                "at": "2026/08/20 10:00:00",
                "anm": "相模湾",
                "acd": "493",
                "cod": "+35.1+139.2-10000/",
                "mag": "4.2",
                "maxi": "5弱",
                "json": "20260820010130_20260820010000_VXSE53_1.json"
              }
            ]
            """;
        using var httpClient = new HttpClient(new StaticResponseHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            }));
        var source = new JmaJsonEarthquakeSource(httpClient);

        EarthquakeSourceFetchResult result = await source.FetchAsync();

        EarthquakeReport report = Assert.Single(result.Reports);
        Assert.Equal(SourceConnectionState.Online, result.Status.State);
        Assert.Equal("20260820010000", report.EventId);
        Assert.Equal("20260820010130_20260820010000_VXSE53_1.json", report.Source.SourceMessageId);
        Assert.Equal(TimeSpan.FromHours(9), report.OriginTime?.Offset);
        Assert.Equal(EarthquakeReportType.HypocenterAndIntensity, report.ReportType);
        Assert.Equal(ReportStatus.Issued, report.Status);
        Assert.Equal(1, report.Serial);
        Assert.Equal(new GeoCoordinate(35.1, 139.2), report.Hypocenter?.Coordinate);
        Assert.Equal("493", report.Hypocenter?.Code);
        Assert.Equal(10, report.Hypocenter?.DepthKm);
        Assert.Equal(4.2, report.Magnitude?.Value);
        Assert.Equal(JmaIntensity.FiveLower, report.MaxIntensity);
        Assert.Contains("20260820010000", report.Source.SourcePayload);
    }

    [Fact]
    public async Task Fetch_MixedNumericAndStringSerial_ParsesWholeList()
    {
        const string json = """
            [
              {
                "ctt": "20260824125723",
                "eid": "20260824125441",
                "rdt": "2026-08-24T12:57:00+09:00",
                "ttl": "震源・震度情報",
                "ift": "発表",
                "ser": "1",
                "at": "2026-08-24T12:54:00+09:00",
                "anm": "釧路地方中南部",
                "acd": "161",
                "cod": "+43.2+145.0-80000/",
                "mag": "3.3",
                "maxi": "1"
              },
              {
                "ctt": "20260824120000",
                "eid": "20260824115900",
                "rdt": "2026-08-24T12:00:00+09:00",
                "ttl": "震源に関する情報",
                "ift": "発表",
                "ser": 0,
                "at": "2026-08-24T11:59:00+09:00",
                "anm": "相模湾",
                "acd": "493",
                "cod": "+35.0+139.0-10000/",
                "mag": "3.0",
                "maxi": ""
              }
            ]
            """;
        using var httpClient = new HttpClient(new StaticResponseHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            }));
        var source = new JmaJsonEarthquakeSource(httpClient);

        EarthquakeSourceFetchResult result = await source.FetchAsync();

        Assert.Equal(2, result.Reports.Length);
        Assert.Equal(SourceConnectionState.Online, result.Status.State);
        Assert.Contains(result.Reports, report =>
            report.EventId == "20260824125441" && report.Serial == 1);
        Assert.Contains(result.Reports, report =>
            report.EventId == "20260824115900" && report.Serial == 0);
    }

    [Fact]
    public async Task Fetch_RateLimited_ReturnsStatusWithoutReports()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var source = new JmaJsonEarthquakeSource(httpClient);

        EarthquakeSourceFetchResult result = await source.FetchAsync();

        Assert.Empty(result.Reports);
        Assert.Equal(SourceConnectionState.RateLimited, result.Status.State);
        Assert.Contains("429", result.Status.Detail);
    }

    [Fact]
    public async Task Fetch_MalformedJson_ReturnsParseFailedStatus()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ not-json }"),
            }));
        var source = new JmaJsonEarthquakeSource(httpClient);

        EarthquakeSourceFetchResult result = await source.FetchAsync();

        Assert.Empty(result.Reports);
        Assert.Equal(SourceConnectionState.ParseFailed, result.Status.State);
        Assert.Contains("格式错误", result.Status.Detail);
    }

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response);
        }
    }
}
