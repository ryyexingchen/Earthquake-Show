using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.Core.Tests;

public sealed class JmaTsunamiStationCatalogTests
{
    [Fact]
    public void LoadJson_PreservesLeadingZeroAndBuildsReversePublicationIndex()
    {
        const string json = """
            {
              "sourceVersion": "test",
              "stations": [
                { "stationCode": "10050", "name": "釧路沖１００ｋｍＡ", "nameKana": null, "latitude": 42.0, "longitude": 144.0, "forecastAreaCode": "100" },
                { "stationCode": "10060", "name": "釧路沖８０ｋｍＡ", "nameKana": null, "latitude": 42.0, "longitude": 144.0, "forecastAreaCode": "100" }
              ],
              "offshorePublicationMappings": [
                { "publicationCode": "00410", "name": "釧路沖１００ｋｍ", "nameKana": "くしろおき１００ｋｍ", "stationCodes": ["10050"] },
                { "publicationCode": "00408", "name": "釧路沖８０ｋｍ", "nameKana": "くしろおき８０ｋｍ", "stationCodes": ["10060"] }
              ]
            }
            """;

        JmaTsunamiStationCatalog catalog = JmaTsunamiStationCatalog.LoadJson(json);

        Assert.True(catalog.TryGetPublication("00410", out JmaTsunamiPublicationCatalogEntry publication));
        Assert.Equal("00410", publication.PublicationCode);
        Assert.Equal("10050", Assert.Single(publication.StationCodes));
        Assert.Equal("00410", Assert.Single(catalog.GetPublicationsForStation("10050")).PublicationCode);
        Assert.False(catalog.TryGetPublication("410", out _));
    }
}
