using EarthquakeShow.Core.Abstractions;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.Core.Tests;

public sealed class InMemoryEarthquakeEventRepositoryTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 8, 19, 7, 10, 0, TimeSpan.FromHours(9));

    [Fact]
    public async Task ListAndGet_InitialReports_ReturnMergedEvents()
    {
        EarthquakeReport older = CreateReport("event-old", "old", 1);
        EarthquakeReport newer = CreateReport("event-new", "new", 2);
        var repository = new InMemoryEarthquakeEventRepository([older, newer]);

        var events = await repository.ListEventsAsync();
        EarthquakeEvent? selected = await repository.GetEventAsync("event-old");

        Assert.Equal(["event-new", "event-old"],
            events.Select(earthquakeEvent => earthquakeEvent.EventId));
        Assert.Equal("event-old", selected?.EventId);
        Assert.Null(await repository.GetEventAsync("missing"));
    }

    [Fact]
    public async Task ApplyReports_MergesTimelineAndPublishesSnapshot()
    {
        EarthquakeReport first = CreateReport("event", "message-1", 1);
        EarthquakeReport second = CreateReport("event", "message-2", 2);
        var repository = new InMemoryEarthquakeEventRepository([first]);
        EarthquakeEventsChangedEventArgs? update = null;
        repository.EventsChanged += (_, eventArgs) => update = eventArgs;

        repository.ApplyReports([second, second]);

        EarthquakeEvent earthquakeEvent = Assert.Single(await repository.ListEventsAsync());
        Assert.Equal(2, earthquakeEvent.Reports.Length);
        Assert.NotNull(update);
        Assert.Equal(earthquakeEvent, Assert.Single(update.Events));
    }

    [Fact]
    public async Task Operations_CancelledToken_AreCancelled()
    {
        var repository = new InMemoryEarthquakeEventRepository();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await repository.ListEventsAsync(cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await repository.GetEventAsync("event", cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await repository.RefreshAsync(cancellation.Token));
    }

    [Fact]
    public async Task GetEvent_BlankEventId_IsRejected()
    {
        var repository = new InMemoryEarthquakeEventRepository();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.GetEventAsync(" "));
    }

    private static EarthquakeReport CreateReport(
        string eventId,
        string sourceMessageId,
        int issuedMinute)
    {
        DateTimeOffset issuedAt = BaseTime.AddMinutes(issuedMinute);
        return new EarthquakeReport
        {
            EventId = eventId,
            ReportCode = "VXSE53",
            Status = ReportStatus.Issued,
            Context = ReportContext.Normal,
            IssuedAt = issuedAt,
            ReceivedAt = issuedAt.AddSeconds(1),
            Source = new SourceReference("jma-xml", sourceMessageId),
        };
    }
}
