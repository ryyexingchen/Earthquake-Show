using System.Collections.Immutable;
using System.Text;
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
    public void LoadFromJson_FiltersPolygonsOutsideViewportBounds()
    {
        OfflineMapGeometry geometry = OfflineMapGeometry.LoadFromJson(
            GeometryJson,
            new MapGeometryBounds(130.2, 130.8, 32.2, 32.8));

        Assert.Single(geometry.Polygons);
        Assert.Equal("741", geometry.Polygons[0].Code);

        OfflineMapGeometry emptyGeometry = OfflineMapGeometry.LoadFromJson(
            GeometryJson,
            new MapGeometryBounds(140, 141, 40, 41));
        Assert.Empty(emptyGeometry.Polygons);
        Assert.Equal(140, emptyGeometry.Bounds.MinLongitude);
        Assert.Equal(141, emptyGeometry.Bounds.MaxLongitude);
    }

    [Fact]
    public void LoadFromFile_UsesFeatureIndexForViewportFiltering()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "EarthquakeShowTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string selectedFeature =
                "{\"type\":\"Feature\",\"properties\":{\"code\":\"selected\",\"name\":\"命中\"},\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[[130,32],[131,32],[131,33],[130,32]]]}}";
            const string outsideFeature =
                "{\"type\":\"Feature\",\"properties\":{\"code\":\"outside\"},\"geometry\":{\"type\":\"Point\",\"coordinates\":[140,40]}}";
            string json =
                $"{{\"type\":\"FeatureCollection\",\"metadata\":{{\"source\":\"索引测试\"}},\"features\":[{selectedFeature},{outsideFeature}]}}";
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            string path = Path.Combine(directory, "areas.geojson");
            File.WriteAllBytes(path, bytes);

            long selectedOffset = Encoding.UTF8.GetByteCount(
                json[..json.IndexOf(selectedFeature, StringComparison.Ordinal)]);
            long outsideOffset = Encoding.UTF8.GetByteCount(
                json[..json.IndexOf(outsideFeature, StringComparison.Ordinal)]);
            string indexJson = $$"""
                {"version":1,"sourceLength":{{bytes.Length}},"source":"索引测试","officialBoundary":false,"features":[{"offset":{{selectedOffset}},"length":{{Encoding.UTF8.GetByteCount(selectedFeature)}},"minLongitude":130,"maxLongitude":131,"minLatitude":32,"maxLatitude":33},{"offset":{{outsideOffset}},"length":{{Encoding.UTF8.GetByteCount(outsideFeature)}},"minLongitude":140,"maxLongitude":140,"minLatitude":40,"maxLatitude":40}]}
                """;
            File.WriteAllText(path + ".index.json", indexJson, Encoding.UTF8);

            OfflineMapGeometry geometry = OfflineMapGeometry.LoadFromFile(
                path,
                new MapGeometryBounds(129.5, 131.5, 31.5, 33.5));

            MapPolygonGeometry polygon = Assert.Single(geometry.Polygons);
            Assert.Equal("selected", polygon.Code);
            Assert.Equal("索引测试", geometry.Source);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
    public async Task HypocenterReport_InheritsPreviousIntensityAreaLayer()
    {
        EarthquakeReport intensity = CreateReport() with
        {
            ReportCode = "VXSE51",
            ReportType = EarthquakeReportType.SeismicIntensity,
            IssuedAt = BaseTime,
            ReceivedAt = BaseTime.AddSeconds(1),
            Hypocenter = null,
            Magnitude = null,
            IntensityMunicipalities = [],
            IntensityStations = [],
            Source = new SourceReference("jma-xml", "intensity-area"),
        };
        EarthquakeReport hypocenter = CreateReport() with
        {
            ReportCode = "VXSE52",
            ReportType = EarthquakeReportType.Hypocenter,
            IssuedAt = BaseTime.AddMinutes(1),
            ReceivedAt = BaseTime.AddMinutes(1).AddSeconds(1),
            IntensityAreas = [],
            IntensityMunicipalities = [],
            IntensityStations = [],
            Source = new SourceReference("jma-xml", "hypocenter"),
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([intensity, hypocenter]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        Assert.Equal("hypocenter", page.State.ViewedReport?.Source.SourceMessageId);
        EarthquakeMapArea area = Assert.Single(map.Areas);
        Assert.Equal("741", area.Code);
        Assert.Equal(JmaIntensity.Four, area.Intensity);
        Assert.Empty(map.Municipalities);
        Assert.DoesNotContain(map.Markers, marker =>
            marker.Kind == EarthquakeMapMarkerKind.Station);
        Assert.Equal(EarthquakeReportType.SeismicIntensity, map.ViewedReportType);
    }

    [Fact]
    public async Task HypocenterReportAfterDetailedObservations_UsesDetailedIntensityOutline()
    {
        EarthquakeReport intensity = CreateReport() with
        {
            ReportCode = "VXSE51",
            ReportType = EarthquakeReportType.SeismicIntensity,
            IssuedAt = BaseTime,
            ReceivedAt = BaseTime.AddSeconds(1),
            Hypocenter = null,
            Magnitude = null,
            IntensityMunicipalities = [],
            IntensityStations = [],
            Source = new SourceReference("jma-xml", "intensity-area"),
        };
        EarthquakeReport detailed = CreateReport() with
        {
            ReportCode = "VXSE53",
            ReportType = EarthquakeReportType.HypocenterAndIntensity,
            IssuedAt = BaseTime.AddMinutes(1),
            ReceivedAt = BaseTime.AddMinutes(1).AddSeconds(1),
            Source = new SourceReference("jma-xml", "detailed-intensity"),
        };
        EarthquakeReport hypocenter = CreateReport() with
        {
            ReportCode = "VXSE52",
            ReportType = EarthquakeReportType.Hypocenter,
            IssuedAt = BaseTime.AddMinutes(2),
            ReceivedAt = BaseTime.AddMinutes(2).AddSeconds(1),
            IntensityAreas = [],
            IntensityMunicipalities = [],
            IntensityStations = [],
            Source = new SourceReference("jma-xml", "hypocenter"),
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([intensity, detailed, hypocenter]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        Assert.Equal("hypocenter", page.State.ViewedReport?.Source.SourceMessageId);
        Assert.Equal(EarthquakeReportType.HypocenterAndIntensity, map.ViewedReportType);
    }

    [Fact]
    public async Task HypocenterReportAfterDetailedObservationsFromAnotherSource_UsesDetailedIntensityOutline()
    {
        EarthquakeReport detailed = CreateReport() with
        {
            IssuedAt = BaseTime,
            ReceivedAt = BaseTime.AddSeconds(1),
            Source = new SourceReference("p2pquake", "detailed-intensity"),
        };
        EarthquakeReport hypocenter = CreateReport() with
        {
            ReportCode = "VXSE52",
            ReportType = EarthquakeReportType.Hypocenter,
            IssuedAt = BaseTime.AddMinutes(1),
            ReceivedAt = BaseTime.AddMinutes(1).AddSeconds(1),
            IntensityAreas = [],
            IntensityMunicipalities = [],
            IntensityStations = [],
            Source = new SourceReference("jma-xml", "hypocenter"),
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([detailed, hypocenter]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        Assert.Equal("hypocenter", page.State.ViewedReport?.Source.SourceMessageId);
        Assert.Equal(EarthquakeReportType.HypocenterAndIntensity, map.ViewedReportType);
    }

    [Fact]
    public async Task DistantEvent_DrawsOnlyHypocenterMarker()
    {
        EarthquakeReport report = CreateReport() with
        {
            ReportType = EarthquakeReportType.DistantEarthquake,
            DistantEarthquakeKind = DistantEarthquakeKind.Earthquake,
            Hypocenter = new Hypocenter(
                "南太平洋",
                "950",
                new GeoCoordinate(-15.4, 167.8),
                null),
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        Assert.Empty(map.Areas);
        Assert.Empty(map.Municipalities);
        Assert.Empty(map.BoundaryLayers);
        EarthquakeMapMarker marker = Assert.Single(map.Markers);
        Assert.Equal(EarthquakeMapMarkerKind.Hypocenter, marker.Kind);
        Assert.True(map.IsDistantEvent);
    }

    [Fact]
    public async Task DistantEvent_DoesNotRequestJapaneseDetailGeometry()
    {
        EarthquakeReport report = CreateReport() with
        {
            ReportType = EarthquakeReportType.DistantEarthquake,
            DistantEarthquakeKind = DistantEarthquakeKind.Earthquake,
            Hypocenter = new Hypocenter(
                "南太平洋",
                "950",
                new GeoCoordinate(-15.4, 167.8),
                null),
            IntensityAreas = [],
            IntensityMunicipalities = [],
            IntensityStations = [],
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        map.AutoScale(EarthquakeMapViewModel.MaxBigZoomLevel);

        Assert.False(map.WillChangeDetailLevel(
            new MapGeometryBounds(160, 175, -25, -5)));
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
    public async Task AreaFocus_UsesOverviewGeometryWhenIntensityLayerIsMissing()
    {
        EarthquakeReport report = CreateReport() with
        {
            IntensityAreas = [],
            IntensityMunicipalities = [],
            IntensityStations = [],
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        Assert.Empty(map.Areas);
        Assert.True(map.TryGetAreaFocusCoordinate("741", out GeoCoordinate coordinate));
        Assert.Equal(32.75, coordinate.Latitude, precision: 2);
        Assert.Equal(130.70, coordinate.Longitude, precision: 2);
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
    public async Task SelectedP2pEvent_UsesOfficialMunicipalityCodeForMapLayer()
    {
        EarthquakeReport report = CreateReport() with
        {
            Source = new SourceReference("p2pquake", "p2p-map"),
            IntensityMunicipalities =
            [new IntensityMunicipality("4320200", "八代市", "741", JmaIntensity.Four)],
            IntensityStations = [],
        };
        const string municipalityGeometry = """
            {"type":"FeatureCollection","features":[{"type":"Feature","properties":{"municipalityCode":"4320200","name":"八代市"},"geometry":{"type":"Polygon","coordinates":[[[130.4,32.4],[131,32.4],[131,33.1],[130.4,33.1],[130.4,32.4]]]}}]}
            """;
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson),
            OfflineMapGeometry.LoadFromJson(municipalityGeometry));

        EarthquakeMapMunicipality municipality = Assert.Single(map.Municipalities);
        Assert.Equal("4320200", municipality.Code);
        Assert.Equal(JmaIntensity.Four, municipality.Intensity);
        Assert.Equal(0, map.UnmappedMunicipalityCount);
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
    public async Task SelectedEventFocus_PrefersHypocenterOverEventBoundsCenter()
    {
        EarthquakeReport report = CreateReport() with
        {
            Hypocenter = new Hypocenter(
                "熊本県熊本",
                "741",
                new GeoCoordinate(32.95, 130.95),
                20),
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        Assert.True(map.TryGetSelectedEventFocusCoordinate(out GeoCoordinate coordinate));
        Assert.Equal(32.95, coordinate.Latitude, precision: 3);
        Assert.Equal(130.95, coordinate.Longitude, precision: 3);
        Assert.True(map.TryGetSelectedEventBounds(out MapGeometryBounds bounds));
        Assert.True(bounds.LongitudeSpan >= 0.5);
        Assert.True(bounds.LatitudeSpan >= 0.5);
    }

    [Fact]
    public void CenterEventBounds_ContainsAllEventGeometryAroundHypocenter()
    {
        MapGeometryBounds eventBounds = new(130.4, 131.0, 32.4, 33.1);
        MapGeometryBounds centered = EarthquakeMapViewModel.CenterEventBounds(
            eventBounds,
            new GeoCoordinate(32.95, 130.95));

        Assert.True(centered.MinLongitude <= eventBounds.MinLongitude);
        Assert.True(centered.MaxLongitude >= eventBounds.MaxLongitude);
        Assert.True(centered.MinLatitude <= eventBounds.MinLatitude);
        Assert.True(centered.MaxLatitude >= eventBounds.MaxLatitude);
        Assert.Equal(130.95, (centered.MinLongitude + centered.MaxLongitude) / 2, precision: 6);
        Assert.Equal(32.95, (centered.MinLatitude + centered.MaxLatitude) / 2, precision: 6);
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

        Assert.True(map.FollowSelection);
        map.BeginManualInteraction();
        Assert.False(map.FollowSelection);
        Assert.True(map.FocusedCoordinate.HasValue);
        Assert.Equal(EarthquakeMapFocusMode.SelectedEvent, map.EffectiveFocusMode);
        Assert.Equal(EarthquakeMapFocusMode.SelectedEvent, page.State.Map.FocusMode);
        GeoCoordinate manualFocus = map.FocusedCoordinate.Value;
        map.BeginManualInteraction();
        Assert.Equal(manualFocus, map.FocusedCoordinate);
        map.SetFollowSelection(true);
        Assert.True(map.FollowSelection);
        Assert.Null(map.FocusedCoordinate);

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

        for (int index = 0; index < 30; index++)
        {
            map.ZoomIn();
        }

        Assert.Equal(EarthquakeMapViewModel.MaxBigZoomLevel, map.ZoomLevel);
        Assert.Equal(focus, map.FocusedCoordinate);

        for (int index = 0; index < 30; index++)
        {
            map.ZoomOut();
        }

        Assert.Equal(EarthquakeMapViewModel.MaxSmallZoomLevel, map.ZoomLevel);
    }

    [Fact]
    public async Task AutoScale_RestoresAutomaticBaselineAfterManualZoomAndFocus()
    {
        var report = CreateReport();
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        map.FocusLocation(new GeoCoordinate(32.75, 130.70));
        map.ZoomIn();
        Assert.True(map.ZoomLevel > 2);
        Assert.NotNull(map.FocusedCoordinate);

        map.AutoScale();

        Assert.Equal(1, map.ZoomLevel, precision: 3);
        Assert.Null(map.FocusedCoordinate);
    }

    [Fact]
    public async Task AutoScale_RestoresSelectedEventFollowStateAfterManualOverview()
    {
        var report = CreateReport();
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        map.BeginManualInteraction();
        map.ResetView();
        map.AutoScale();

        Assert.True(map.FollowSelection);
        Assert.Equal(EarthquakeMapFocusMode.SelectedEvent, map.EffectiveFocusMode);
        Assert.Equal(EarthquakeMapFocusMode.SelectedEvent, page.State.Map.FocusMode);
        Assert.Equal(1, map.ZoomLevel, precision: 3);
    }

    [Fact]
    public async Task SelectingAnotherReportAutomaticallyRestoresEventView()
    {
        EarthquakeReport first = CreateReport();
        EarthquakeReport second = CreateReport() with
        {
            IssuedAt = BaseTime.AddMinutes(1),
            ReceivedAt = BaseTime.AddMinutes(1).AddSeconds(1),
            Source = new SourceReference("p2pquake", "p2p-message"),
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([first, second]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));

        map.ZoomIn();
        map.ZoomIn();
        Assert.True(map.ZoomLevel > 1);

        Assert.True(page.SelectReport("p2pquake", "p2p-message"));

        Assert.Equal(1, map.ZoomLevel, precision: 3);
        Assert.True(map.FollowSelection);
        Assert.Equal(EarthquakeMapFocusMode.SelectedEvent, map.EffectiveFocusMode);
    }

    [Fact]
    public async Task ZoomDetailSwitchesToHighGeometryAfterHighThreshold()
    {
        var report = CreateReport();
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();

        string directory = Path.Combine(
            Path.GetTempPath(),
            "EarthquakeShowTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string mediumAreasPath = Path.Combine(directory, "areas-medium.geojson");
            string mediumMunicipalitiesPath = Path.Combine(directory, "municipalities-medium.geojson");
            string mediumBoundariesPath = Path.Combine(directory, "boundaries-medium.geojson");
            string highAreasPath = Path.Combine(directory, "areas-high.geojson");
            string highMunicipalitiesPath = Path.Combine(directory, "municipalities-high.geojson");
            await File.WriteAllTextAsync(mediumAreasPath, GeometryJson.Replace("测试离线轮廓", "中精度区域"));
            await File.WriteAllTextAsync(mediumMunicipalitiesPath, MunicipalityGeometryJson);
            await File.WriteAllTextAsync(mediumBoundariesPath, BoundaryGeometryJson);
            await File.WriteAllTextAsync(highAreasPath, GeometryJson.Replace("测试离线轮廓", "高精度区域"));
            await File.WriteAllTextAsync(highMunicipalitiesPath, MunicipalityGeometryJson);

            using var map = new EarthquakeMapViewModel(
                page,
                OfflineMapGeometry.LoadFromJson(GeometryJson),
                OfflineMapGeometry.LoadFromJson(MunicipalityGeometryJson),
                OfflineMapBoundaryGeometry.LoadFromJson(BoundaryGeometryJson),
                new MapLodResourceProvider(
                    mediumAreasPath,
                    mediumMunicipalitiesPath,
                    mediumBoundariesPath,
                    highAreasPath,
                    highMunicipalitiesPath));
            string? sourceBeforeGeometryChange = null;
            map.GeometryChanging += (_, _) => sourceBeforeGeometryChange ??= map.GeometrySource;

            for (int index = 0; index < 12; index++)
            {
                map.ZoomIn();
            }

            await map.EnsureDetailLevelForZoomAsync();

            Assert.Equal(MapDetailLevel.High, map.DetailLevel);
            Assert.Equal("高精度区域", map.GeometrySource);
            Assert.Contains("区域轮廓", map.BoundaryGeometry?.Source);
            Assert.Equal("测试离线轮廓", sourceBeforeGeometryChange);

            map.AutoScale(3);
            await map.EnsureDetailLevelForZoomAsync();

            Assert.Equal(MapDetailLevel.Medium, map.DetailLevel);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HighDetailReloadRetainsPreviousViewportForReturnNavigation()
    {
        var report = CreateReport();
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();

        string directory = Path.Combine(
            Path.GetTempPath(),
            "EarthquakeShowTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string mediumAreasPath = Path.Combine(directory, "areas-medium.geojson");
            string mediumMunicipalitiesPath = Path.Combine(directory, "municipalities-medium.geojson");
            string mediumBoundariesPath = Path.Combine(directory, "boundaries-medium.geojson");
            string highAreasPath = Path.Combine(directory, "areas-high.geojson");
            string highMunicipalitiesPath = Path.Combine(directory, "municipalities-high.geojson");
            await File.WriteAllTextAsync(mediumAreasPath, GeometryJson);
            await File.WriteAllTextAsync(mediumMunicipalitiesPath, MunicipalityGeometryJson);
            await File.WriteAllTextAsync(mediumBoundariesPath, BoundaryGeometryJson);
            await File.WriteAllTextAsync(highAreasPath, GeometryJson.Replace("测试离线轮廓", "高精度区域"));
            await File.WriteAllTextAsync(highMunicipalitiesPath, MunicipalityGeometryJson);

            using var map = new EarthquakeMapViewModel(
                page,
                OfflineMapGeometry.LoadFromJson(GeometryJson),
                OfflineMapGeometry.LoadFromJson(MunicipalityGeometryJson),
                OfflineMapBoundaryGeometry.LoadFromJson(BoundaryGeometryJson),
                new MapLodResourceProvider(
                    mediumAreasPath,
                    mediumMunicipalitiesPath,
                    mediumBoundariesPath,
                    highAreasPath,
                    highMunicipalitiesPath));
            map.AutoScale(13);
            List<MapDetailLevel> detailLevelsBeforeChange = [];
            map.GeometryChanging += (_, _) => detailLevelsBeforeChange.Add(map.DetailLevel);
            MapGeometryBounds firstBounds = new(129, 132, 31, 35);
            MapGeometryBounds secondBounds = new(140, 143, 35, 39);

            await map.EnsureDetailLevelForZoomAsync(viewportBounds: firstBounds);
            Assert.Equal(firstBounds, map.HighLoadedViewportBounds);
            Assert.False(map.LastHighLoadUsedCache);

            detailLevelsBeforeChange.Clear();
            await map.EnsureDetailLevelForZoomAsync(viewportBounds: secondBounds);
            Assert.Equal(MapDetailLevel.High, map.DetailLevel);
            Assert.Equal(secondBounds, map.HighLoadedViewportBounds);
            Assert.Contains(MapDetailLevel.High, detailLevelsBeforeChange);
            Assert.Contains(MapDetailLevel.Medium, detailLevelsBeforeChange);
            Assert.False(map.LastHighLoadUsedCache);

            await map.EnsureDetailLevelForZoomAsync(viewportBounds: firstBounds);
            Assert.Equal(MapDetailLevel.High, map.DetailLevel);
            Assert.Equal(firstBounds, map.HighLoadedViewportBounds);
            Assert.True(map.LastHighLoadUsedCache);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ZoomDetailSwitchesAllGeometryLayersAndRestoresOverview()
    {
        var report = CreateReport();
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();

        string directory = Path.Combine(
            Path.GetTempPath(),
            "EarthquakeShowTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string areasPath = Path.Combine(directory, "areas.geojson");
            string municipalitiesPath = Path.Combine(directory, "municipalities.geojson");
            string boundariesPath = Path.Combine(directory, "boundaries.geojson");
            await File.WriteAllTextAsync(areasPath, GeometryJson.Replace("测试离线轮廓", "中精度区域"));
            await File.WriteAllTextAsync(
                municipalitiesPath,
                MunicipalityGeometryJson
                    .Replace("测试市町村轮廓", "中精度市町村")
                    .Replace("130.5", "129.5"));
            await File.WriteAllTextAsync(boundariesPath, BoundaryGeometryJson.Replace("测试区域边界", "中精度边界"));

            using var map = new EarthquakeMapViewModel(
                page,
                OfflineMapGeometry.LoadFromJson(GeometryJson),
                OfflineMapGeometry.LoadFromJson(MunicipalityGeometryJson),
                OfflineMapBoundaryGeometry.LoadFromJson(BoundaryGeometryJson),
                new MapLodResourceProvider(areasPath, municipalitiesPath, boundariesPath));

            map.AutoScale(3);
            await map.EnsureDetailLevelForZoomAsync();

            Assert.Equal(MapDetailLevel.Medium, map.DetailLevel);
            map.ResetView();
            await map.EnsureDetailLevelForZoomAsync();
            Assert.Equal(MapDetailLevel.Overview, map.DetailLevel);

            for (int index = 0; index < 5; index++)
            {
                map.ZoomIn();
            }

            await map.EnsureDetailLevelForZoomAsync();

            Assert.Equal(MapDetailLevel.Medium, map.DetailLevel);
            Assert.Equal("中精度区域", map.GeometrySource);
            Assert.Equal(129.5, map.Municipalities[0].Coordinates[0].Longitude, precision: 3);
            Assert.Equal("中精度边界", map.BoundaryGeometry?.Source);
            Assert.NotEmpty(map.Areas);
            Assert.NotEmpty(map.Municipalities);
            Assert.NotEmpty(map.BoundaryLayers);

            while (map.ZoomLevel > 2)
            {
                map.ZoomOut();
            }

            await map.EnsureDetailLevelForZoomAsync();

            Assert.Equal(MapDetailLevel.Overview, map.DetailLevel);
            Assert.Equal("测试离线轮廓", map.GeometrySource);
            Assert.Equal(130.5, map.Municipalities[0].Coordinates[0].Longitude, precision: 3);
            Assert.Equal("测试区域边界", map.BoundaryGeometry?.Source);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DetailLoadFailureKeepsCurrentGeometryAndReportsError()
    {
        var report = CreateReport();
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();

        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson),
            OfflineMapGeometry.LoadFromJson(MunicipalityGeometryJson),
            OfflineMapBoundaryGeometry.LoadFromJson(BoundaryGeometryJson),
            new MapLodResourceProvider(
                "missing-areas.geojson",
                "missing-municipalities.geojson",
                "missing-boundaries.geojson"));
        string source = map.GeometrySource;
        int areaCount = map.Areas.Count;
        int municipalityCount = map.Municipalities.Count;
        int boundaryCount = map.BoundaryLayers.Count;

        for (int index = 0; index < 5; index++)
        {
            map.ZoomIn();
        }

        await map.EnsureDetailLevelForZoomAsync();

        Assert.Equal(MapDetailLevel.Overview, map.DetailLevel);
        Assert.Equal(source, map.GeometrySource);
        Assert.Equal(areaCount, map.Areas.Count);
        Assert.Equal(municipalityCount, map.Municipalities.Count);
        Assert.Equal(boundaryCount, map.BoundaryLayers.Count);
        Assert.False(string.IsNullOrWhiteSpace(map.DetailLoadError));
        Assert.Contains("中精度加载失败", map.StatusText);
        Assert.False(map.IsLoadingDetail);
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
