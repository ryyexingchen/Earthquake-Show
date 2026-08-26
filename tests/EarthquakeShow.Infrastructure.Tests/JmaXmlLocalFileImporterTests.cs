using EarthquakeShow.Core.Models;
using EarthquakeShow.Infrastructure.Sources;
using Xunit;

namespace EarthquakeShow.Infrastructure.Tests;

public sealed class JmaXmlLocalFileImporterTests
{
    [Fact]
    public async Task ImportAsync_ParsesSupportedFilesWithOnlineSourceIdentity()
    {
        using TemporaryDirectory directory = TemporaryDirectory.Create();
        string fixture = OfficialFixture("20260818221432_0_VXSE53_270000.xml");
        string filePath = Path.Combine(directory.Path, Path.GetFileName(fixture));
        File.Copy(fixture, filePath);

        JmaXmlLocalFileImportResult result = await new JmaXmlLocalFileImporter().ImportAsync(directory.Path);

        EarthquakeReport report = Assert.Single(result.Reports);
        Assert.Equal("jma-xml", report.Source.SourceId);
        Assert.Equal(Path.GetFileName(filePath), report.Source.SourceMessageId);
        Assert.Equal(new Uri(filePath), report.Source.RawMessageUri);
        Assert.Contains("<Report", report.Source.SourcePayload);
        Assert.Empty(result.SkippedFiles);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task ImportAsync_SkipsUnsupportedFiles()
    {
        using TemporaryDirectory directory = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "20260818_0_VPWS50_010000.xml"), "not an earthquake report");

        JmaXmlLocalFileImportResult result = await new JmaXmlLocalFileImporter().ImportAsync(directory.Path);

        Assert.Empty(result.Reports);
        Assert.Single(result.SkippedFiles);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task ImportAsync_ContinuesAfterMalformedFile()
    {
        using TemporaryDirectory directory = TemporaryDirectory.Create();
        string validPath = Path.Combine(directory.Path, "20260818221432_0_VXSE53_270000.xml");
        File.Copy(OfficialFixture("20260818221432_0_VXSE53_270000.xml"), validPath);
        string invalidPath = Path.Combine(directory.Path, "20260818221433_0_VXSE53_270000.xml");
        await File.WriteAllTextAsync(invalidPath, "<Report>");

        JmaXmlLocalFileImportResult result = await new JmaXmlLocalFileImporter().ImportAsync(directory.Path);

        Assert.Single(result.Reports);
        JmaXmlLocalFileImportFailure failure = Assert.Single(result.Failures);
        Assert.Equal(invalidPath, failure.FilePath);
    }

    [Fact]
    public async Task ImportAsync_EmptyDirectoryReturnsEmptyResult()
    {
        using TemporaryDirectory directory = TemporaryDirectory.Create();

        JmaXmlLocalFileImportResult result = await new JmaXmlLocalFileImporter().ImportAsync(directory.Path);

        Assert.Empty(result.Reports);
        Assert.Empty(result.SkippedFiles);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task ImportAsync_MissingDirectoryThrowsClearError()
    {
        string path = Path.Combine(Path.GetTempPath(), $"earthquake-show-missing-{Guid.NewGuid():N}");

        DirectoryNotFoundException exception = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => new JmaXmlLocalFileImporter().ImportAsync(path));

        Assert.Contains("本地 JMA XML 目录不存在", exception.Message);
    }

    private static string OfficialFixture(string fileName) => Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "tests", "TestData", "JmaXml", "Official", fileName);

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"earthquake-show-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
