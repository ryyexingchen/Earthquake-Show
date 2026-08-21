using System.Collections.Immutable;
using EarthquakeShow.App.ViewModels;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class EarthquakeMapViewModelTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 8, 20, 9, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void LoadFromJson_PreservesSchematicMetadataAndPolygon()
    {
        OfflineMapGeometry geometry = OfflineMapGeometry.LoadFromJson(GeometryJson);

        Assert.False(geometry.IsOfficialBoundary);
        Assert.Equal("测试离线轮廓", geometry.Source);
        MapPolygonGeometry polygon = Assert.Single(geometry.Polygons);
        Assert.Equal("741", polygon.Code);
        Assert.Equal("熊本県熊本", polygon.Name);
        Assert.Equal(5, polygon.Coordinates.Length);
    }

    [Fact]
    public void LoadFromJson_RejectsUnsupportedGeometry()
    {
        const string json = """
            {"type":"FeatureCollection","features":[{"type":"Feature","properties":{},"geometry":{"type":"Point","coordinates":[130,33]}}]}
            """;

        Assert.Throws<FormatException>(() => OfflineMapGeometry.LoadFromJson(json));
    }

    [Fact]
    public void LoadFromJson_PreservesInteriorRingsAndMultiPolygonParts()
    {
        OfflineMapGeometry geometry = OfflineMapGeometry.LoadFromJson(MultiRingGeometryJson);

        Assert.Equal(2, geometry.Polygons.Length);
        Assert.Equal(2, geometry.Polygons[0].Rings.Length);
        Assert.Equal(2, geometry.Polygons[1].Rings.Length);
        Assert.Equal(5, geometry.Polygons[1].Coordinates.Length);
        Assert.Equal(0, geometry.InvalidGeometryCount);
        Assert.Equal(129, geometry.Bounds.MinLongitude, precision: 3);
        Assert.Equal(132, geometry.Bounds.MaxLongitude, precision: 3);
    }

    [Fact]
    public void LoadFromJson_ReportsInvalidPolygonWithoutDiscardingValidFeature()
    {
        OfflineMapGeometry geometry = OfflineMapGeometry.LoadFromJson(InvalidGeometryJson);

        Assert.Single(geometry.Polygons);
        Assert.Equal(1, geometry.InvalidGeometryCount);
    }

    [Fact]
    public async Task SelectedEvent_BuildsAreaHypocenterAndCoordinateStationLayers()
    {
        var report = CreateReport();
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        Assert.Single(map.Areas);
        Assert.Equal("741", map.Areas[0].Code);
        Assert.Equal(2, map.Markers.Count);
        Assert.Contains(map.Markers, marker =>
            marker.Kind == EarthquakeMapMarkerKind.Hypocenter);
        Assert.Contains(map.Markers, marker =>
            marker.Kind == EarthquakeMapMarkerKind.Station && marker.Label == "熊本観測点");
        Assert.DoesNotContain(map.Markers, marker => marker.Label == "座標不明観測点");
        Assert.True(map.HasDrawableLayers);
        Assert.Equal("离线示意底图 · 当前事件图层", map.StatusText);
        Assert.Equal(EarthquakeMapFocusMode.SelectedEvent, map.EffectiveFocusMode);
    }

    [Fact]
    public async Task SelectedEvent_DrawsHigherIntensityStationsAfterLowerIntensityStations()
    {
        EarthquakeReport report = CreateReport() with
        {
            IntensityStations =
            [
                new IntensityStation(
                    "KMM001",
                    "低震度观测点",
                    "741",
                    JmaIntensity.Two,
                    new GeoCoordinate(32.81, 130.71)),
                new IntensityStation(
                    "KMM003",
                    "高震度观测点",
                    "741",
                    JmaIntensity.SixUpper,
                    new GeoCoordinate(32.82, 130.72)),
            ],
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        EarthquakeMapMarker[] stations = map.Markers
            .Where(marker => marker.Kind == EarthquakeMapMarkerKind.Station)
            .ToArray();

        Assert.Equal([JmaIntensity.Two, JmaIntensity.SixUpper],
            stations.Select(marker => marker.Intensity));
    }

    [Fact]
    public async Task SelectedEvent_ProvidesFocusCoordinateForMappedArea()
    {
        var report = CreateReport();
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        Assert.True(map.TryGetAreaFocusCoordinate("741", out GeoCoordinate coordinate));
        Assert.Equal(32.75, coordinate.Latitude, precision: 2);
        Assert.Equal(130.70, coordinate.Longitude, precision: 2);
        Assert.False(map.TryGetAreaFocusCoordinate("999", out _));
    }

    [Fact]
    public async Task SelectedEventFocus_UsesAreaWhenIntensityAlertHasNoMarkers()
    {
        EarthquakeReport report = CreateReport() with
        {
            Hypocenter = null,
            IntensityStations = [],
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        Assert.True(map.TryGetSelectedEventFocusCoordinate(out GeoCoordinate coordinate));
        Assert.Equal(32.75, coordinate.Latitude, precision: 2);
        Assert.Equal(130.70, coordinate.Longitude, precision: 2);
    }

    [Fact]
    public async Task MapCommands_UpdateZoomAndPageMapState()
    {
        var report = CreateReport();
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        map.ZoomIn();
        Assert.True(map.ZoomLevel > 1);
        map.ZoomOut();
        Assert.Equal(1, map.ZoomLevel, precision: 3);

        map.FocusSelectedEvent();
        Assert.Equal(EarthquakeMapFocusMode.SelectedEvent, page.State.Map.FocusMode);
        map.SetFollowSelection(false);
        Assert.False(page.State.Map.FollowSelection);
        map.ResetView();
        Assert.Equal(EarthquakeMapFocusMode.JapanOverview, page.State.Map.FocusMode);
        Assert.True(map.FollowSelection == false);
        Assert.Equal(EarthquakeMapFocusMode.JapanOverview, map.EffectiveFocusMode);
    }

    [Fact]
    public async Task EmptyPage_StillExposesOfflineOutlineAndNoEventLayers()
    {
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository());
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        Assert.Single(map.Outline);
        Assert.Empty(map.Areas);
        Assert.Empty(map.Markers);
        Assert.False(map.HasSelectedEvent);
        Assert.False(map.HasDrawableLayers);
        Assert.Equal("离线示意底图 · 未选择事件", map.StatusText);
    }

    private static EarthquakeReport CreateReport()
    {
        return new EarthquakeReport
        {
            EventId = "event-map",
            ReportCode = "VXSE53",
            ReportType = EarthquakeReportType.HypocenterAndIntensity,
            Status = ReportStatus.Issued,
            Context = ReportContext.Normal,
            IssuedAt = BaseTime,
            ReceivedAt = BaseTime.AddSeconds(1),
            Hypocenter = new Hypocenter(
                "熊本県熊本",
                "741",
                new GeoCoordinate(32.8, 130.7),
                20),
            MaxIntensity = JmaIntensity.Four,
            IntensityAreas =
            [
                new IntensityArea("741", "熊本県熊本", "43", "熊本県", JmaIntensity.Four),
                new IntensityArea("999", "不存在区域", "99", "不存在", JmaIntensity.Seven),
            ],
            IntensityStations =
            [
                new IntensityStation(
                    "KMM001",
                    "熊本観測点",
                    "741",
                    JmaIntensity.Four,
                    new GeoCoordinate(32.81, 130.71)),
                new IntensityStation(
                    "KMM002",
                    "座標不明观测点",
                    "741",
                    JmaIntensity.Three,
                    null),
            ],
            Source = new SourceReference("jma-xml", "map-message"),
        };
    }

    private const string GeometryJson = """
        {
          "type": "FeatureCollection",
          "metadata": { "source": "测试离线轮廓", "officialBoundary": false },
          "features": [
            {
              "type": "Feature",
              "properties": { "areaCode": "741", "name": "熊本県熊本", "officialBoundary": false },
              "geometry": {
                "type": "Polygon",
                "coordinates": [[[130.4,32.4],[131.0,32.4],[131.0,33.1],[130.4,33.1],[130.4,32.4]]]
              }
            }
          ]
        }
        """;

    private const string MultiRingGeometryJson = """
        {
          "type": "FeatureCollection",
          "metadata": { "source": "测试多环轮廓", "officialBoundary": true },
          "features": [
            {
              "type": "Feature",
              "properties": { "areaCode": "100", "name": "主岛" },
              "geometry": {
                "type": "Polygon",
                "coordinates": [
                  [[130,32],[131,32],[131,33],[130,33],[130,32]],
                  [[130.2,32.2],[130.4,32.2],[130.4,32.4],[130.2,32.4],[130.2,32.2]]
                ]
              }
            },
            {
              "type": "Feature",
              "properties": { "areaCode": "100", "name": "离岛" },
              "geometry": {
                "type": "MultiPolygon",
                "coordinates": [
                  [[[129,31],[129.5,31],[129.5,31.5],[129,31.5],[129,31]]],
                  [[[131.5,34],[132,34],[132,34.5],[131.5,34.5],[131.5,34]]]
                ]
              }
            }
          ]
        }
        """;

    private const string InvalidGeometryJson = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "properties": { "areaCode": "bad" },
              "geometry": { "type": "Polygon", "coordinates": [[]] }
            },
            {
              "type": "Feature",
              "properties": { "areaCode": "good" },
              "geometry": { "type": "Polygon", "coordinates": [[[130,32],[131,32],[131,33]]] }
            }
          ]
        }
        """;
}
