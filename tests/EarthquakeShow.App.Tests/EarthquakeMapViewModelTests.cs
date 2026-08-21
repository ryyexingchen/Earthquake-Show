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
    public async Task SelectedEvent_BuildsBoundaryLayersFromAdjacentAreaIntensity()
    {
        EarthquakeReport report = CreateReport() with
        {
            IntensityAreas =
            [
                new IntensityArea("A", "区域 A", "01", "都道府県 A", JmaIntensity.Four),
                new IntensityArea("A", "区域 A（未知）", "01", "都道府県 A", JmaIntensity.Unknown),
                new IntensityArea("B", "区域 B", "02", "都道府県 B", JmaIntensity.SixUpper),
            ],
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson),
            boundaryGeometry: OfflineMapBoundaryGeometry.LoadFromJson(BoundaryGeometryJson));

        Assert.Equal(
            [JmaIntensity.Unknown, JmaIntensity.Four, JmaIntensity.SixUpper],
            map.BoundaryLayers.Select(layer => layer.Intensity));
        Assert.Single(map.BoundaryLayers[0].Boundaries);
        Assert.Equal("D", map.BoundaryLayers[0].Boundaries[0].AreaCode1);
        Assert.Single(map.BoundaryLayers[1].Boundaries);
        Assert.Equal("A", map.BoundaryLayers[1].Boundaries[0].AreaCode1);
        Assert.Equal("", map.BoundaryLayers[1].Boundaries[0].AreaCode2);
        Assert.Equal(2, map.BoundaryLayers[2].Boundaries.Length);
        Assert.Contains(map.BoundaryLayers[2].Boundaries, boundary =>
            boundary.AreaCode1 == "A" && boundary.AreaCode2 == "B");
        Assert.Contains(map.BoundaryLayers[2].Boundaries, boundary =>
            boundary.AreaCode1 == "C" && boundary.AreaCode2 == "B");
    }

    [Fact]
    public async Task SelectedEvent_UsesUnknownBoundaryLayerWhenNoAreaHasValidIntensity()
    {
        EarthquakeReport report = CreateReport() with
        {
            IntensityAreas = [],
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson),
            boundaryGeometry: OfflineMapBoundaryGeometry.LoadFromJson(BoundaryGeometryJson));

        EarthquakeMapBoundaryLayer layer = Assert.Single(map.BoundaryLayers);
        Assert.Equal(JmaIntensity.Unknown, layer.Intensity);
        Assert.Equal(4, layer.Boundaries.Length);
    }

    [Fact]
    public async Task BoundaryLayerCountsAsDrawableWhenNoAreaMunicipalityOrMarkerExists()
    {
        EarthquakeReport report = CreateReport() with
        {
            Hypocenter = null,
            IntensityAreas = [],
            IntensityMunicipalities = [],
            IntensityStations = [],
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson),
            boundaryGeometry: OfflineMapBoundaryGeometry.LoadFromJson(BoundaryGeometryJson));

        Assert.Empty(map.Areas);
        Assert.Empty(map.Municipalities);
        Assert.Empty(map.Markers);
        Assert.NotEmpty(map.BoundaryLayers);
        Assert.True(map.HasDrawableLayers);
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
    public async Task SelectedEvent_BuildsMunicipalityLayerAndFocusesMunicipality()
    {
        EarthquakeReport report = CreateReport() with
        {
            IntensityMunicipalities =
            [
                new IntensityMunicipality(
                    "C741",
                    "熊本市",
                    "741",
                    JmaIntensity.Four),
                new IntensityMunicipality(
                    "C999",
                    "不存在市町村",
                    "741",
                    JmaIntensity.Two),
            ],
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson),
            OfflineMapGeometry.LoadFromJson(MunicipalityGeometryJson));

        MapPolygonGeometry polygon = Assert.Single(
            OfflineMapGeometry.LoadFromJson(MunicipalityGeometryJson).Polygons);
        Assert.Equal("C741", polygon.Code);
        EarthquakeMapMunicipality municipality = Assert.Single(map.Municipalities);
        Assert.Equal("C741", municipality.Code);
        Assert.Equal(JmaIntensity.Four, municipality.Intensity);
        Assert.Equal(1, map.UnmappedMunicipalityCount);
        Assert.True(map.TryGetMunicipalityFocusCoordinate(
            "C741",
            out GeoCoordinate coordinate));
        Assert.Equal(32.70, coordinate.Latitude, precision: 2);
        Assert.Equal(130.70, coordinate.Longitude, precision: 2);
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
    public async Task MapCommands_AllowExpandedZoomAndClampAtMaximum()
    {
        var report = CreateReport();
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));
        GeoCoordinate focus = new(32.75, 130.70);
        map.FocusLocation(focus);

        for (int index = 0; index < 20; index++)
        {
            map.ZoomIn();
        }

        Assert.Equal(EarthquakeMapViewModel.MaximumZoomLevel, map.ZoomLevel);
        Assert.Equal(focus, map.FocusedCoordinate);

        for (int index = 0; index < 20; index++)
        {
            map.ZoomOut();
        }

        Assert.Equal(1, map.ZoomLevel);
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
            IntensityMunicipalities =
            [
                new IntensityMunicipality("C741", "熊本市", "741", JmaIntensity.Four),
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

    private const string MunicipalityGeometryJson = """
        {
          "type": "FeatureCollection",
          "metadata": { "source": "测试市町村轮廓", "officialBoundary": true },
          "features": [
            {
              "type": "Feature",
              "properties": { "municipalityCode": "C741", "name": "熊本市" },
              "geometry": {
                "type": "Polygon",
                "coordinates": [[[130.5,32.5],[130.9,32.5],[130.9,32.9],[130.5,32.9],[130.5,32.5]]]
              }
            }
          ]
        }
        """;

    private const string BoundaryGeometryJson = """
        {
          "type": "FeatureCollection",
          "metadata": { "source": "测试区域边界", "officialBoundary": true },
          "features": [
            {
              "type": "Feature",
              "properties": { "areaCode1": "A", "areaCode2": "B" },
              "geometry": { "type": "LineString", "coordinates": [[130,32],[131,32]] }
            },
            {
              "type": "Feature",
              "properties": { "areaCode1": "A", "areaCode2": "" },
              "geometry": { "type": "LineString", "coordinates": [[130,33],[131,33]] }
            },
            {
              "type": "Feature",
              "properties": { "areaCode1": "C", "areaCode2": "B" },
              "geometry": { "type": "LineString", "coordinates": [[130,34],[131,34]] }
            },
            {
              "type": "Feature",
              "properties": { "areaCode1": "D", "areaCode2": "E" },
              "geometry": { "type": "LineString", "coordinates": [[130,35],[131,35]] }
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
