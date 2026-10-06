using ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;
using Shouldly;

namespace ClearMeasure.Bootcamp.UnitTests.UI.Server.LiveTelemetry;

[TestFixture]
public class LiveTelemetryCountersTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 23, 0, 0, 250, TimeSpan.Zero);

    [Test]
    public void Snapshot_WhenNothingRecorded_ShouldReturnZerosAndNullLatencies()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));

        var snapshot = counters.Snapshot();

        snapshot.WindowSeconds.ShouldBe(60);
        snapshot.StartedAt.ShouldBe(new DateTime(2026, 10, 5, 23, 0, 0, DateTimeKind.Utc));
        snapshot.Requests.ShouldBe(new RequestCounts(0, 0, 0, 0, null));
        snapshot.Probes.ShouldBe(new ProbeCounts(0, 0));
        snapshot.Sql.ShouldBe(new SqlCounts(0, null));
        snapshot.Http.ShouldBe(new HttpClientCounts(0));
    }

    [Test]
    public void RecordRequest_WhenEachKindRecorded_ShouldCountTrafficAndProbesApart()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));

        counters.RecordRequest(RequestKind.FrontDoorTraffic, 200, TimeSpan.FromMilliseconds(10));
        counters.RecordRequest(RequestKind.FrontDoorTraffic, 503, TimeSpan.FromMilliseconds(10));
        counters.RecordRequest(RequestKind.DirectTraffic, 500, TimeSpan.FromMilliseconds(10));
        counters.RecordRequest(RequestKind.DirectTraffic, 404, TimeSpan.FromMilliseconds(10));
        counters.RecordRequest(RequestKind.DirectTraffic, 200, TimeSpan.FromMilliseconds(10));
        counters.RecordRequest(RequestKind.Probe, 500, TimeSpan.FromMilliseconds(10));
        counters.RecordRequest(RequestKind.FrontDoorProbe, 200, TimeSpan.FromMilliseconds(10));
        counters.RecordRequest(RequestKind.FrontDoorProbe, 200, TimeSpan.FromMilliseconds(10));
        counters.RecordSqlCommand(TimeSpan.FromMilliseconds(3));
        counters.RecordHttpClientCall();

        var snapshot = counters.Snapshot();

        snapshot.Requests.PerMinute.ShouldBe(5);
        snapshot.Requests.FrontDoor.ShouldBe(2);
        snapshot.Requests.Direct.ShouldBe(3);
        snapshot.Requests.Errors.ShouldBe(2);
        snapshot.Requests.P95Ms.ShouldBe(10);
        snapshot.Probes.PerMinute.ShouldBe(1);
        snapshot.Probes.FrontDoor.ShouldBe(2);
        snapshot.Sql.ShouldBe(new SqlCounts(1, 3));
        snapshot.Http.ShouldBe(new HttpClientCounts(1));
    }

    [Test]
    public void Snapshot_WhenOnlyProbesRecorded_ShouldHaveNoRequestLatency()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));

        counters.RecordRequest(RequestKind.Probe, 200, TimeSpan.FromMilliseconds(40));
        counters.RecordRequest(RequestKind.FrontDoorProbe, 200, TimeSpan.FromMilliseconds(40));

        counters.Snapshot().Requests.P95Ms.ShouldBeNull();
    }

    [Test]
    public void Snapshot_WhenWindowRolls_ShouldDropCountsOlderThanSixtySeconds()
    {
        var clock = new StubTimeProvider(Start);
        var counters = new LiveTelemetryCounters(clock);
        counters.RecordRequest(RequestKind.DirectTraffic, 200, TimeSpan.FromMilliseconds(100));
        counters.RecordSqlCommand(TimeSpan.FromMilliseconds(100));
        clock.Advance(TimeSpan.FromSeconds(30));
        counters.RecordRequest(RequestKind.DirectTraffic, 200, TimeSpan.FromMilliseconds(20));
        counters.RecordSqlCommand(TimeSpan.FromMilliseconds(20));

        clock.Advance(TimeSpan.FromSeconds(29));
        var beforeRoll = counters.Snapshot();
        clock.Advance(TimeSpan.FromSeconds(1));
        var afterFirstRoll = counters.Snapshot();
        clock.Advance(TimeSpan.FromSeconds(30));
        var afterSecondRoll = counters.Snapshot();

        beforeRoll.Requests.PerMinute.ShouldBe(2);
        beforeRoll.Requests.P95Ms.ShouldBe(100);
        afterFirstRoll.Requests.PerMinute.ShouldBe(1);
        afterFirstRoll.Requests.P95Ms.ShouldBe(20);
        afterFirstRoll.Sql.ShouldBe(new SqlCounts(1, 20));
        afterSecondRoll.Requests.ShouldBe(new RequestCounts(0, 0, 0, 0, null));
        afterSecondRoll.Sql.ShouldBe(new SqlCounts(0, null));
    }

    [Test]
    public void RecordRequest_WhenBucketIsReusedAMinuteLater_ShouldStartItFromZero()
    {
        var clock = new StubTimeProvider(Start);
        var counters = new LiveTelemetryCounters(clock);
        counters.RecordRequest(RequestKind.FrontDoorTraffic, 200, null);
        counters.RecordRequest(RequestKind.FrontDoorTraffic, 200, null);

        clock.Advance(TimeSpan.FromSeconds(60));
        counters.RecordRequest(RequestKind.FrontDoorTraffic, 200, null);

        counters.Snapshot().Requests.FrontDoor.ShouldBe(1);
    }

    [Test]
    public void Snapshot_WhenTrafficSpreadOverTheMinute_ShouldCountEverySecond()
    {
        var clock = new StubTimeProvider(Start);
        var counters = new LiveTelemetryCounters(clock);

        for (var second = 0; second < 60; second++)
        {
            counters.RecordRequest(RequestKind.FrontDoorTraffic, 200, TimeSpan.FromMilliseconds(5));
            counters.RecordRequest(RequestKind.FrontDoorTraffic, 200, TimeSpan.FromMilliseconds(5));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        clock.Advance(TimeSpan.FromSeconds(-1));

        counters.Snapshot().Requests.PerMinute.ShouldBe(120);
    }

    [Test]
    public void Snapshot_WhenTwentyLatencies_ShouldReportNearestRankNinetyFifthPercentile()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));

        foreach (var ms in Enumerable.Range(1, 20).Reverse())
        {
            counters.RecordRequest(RequestKind.DirectTraffic, 200, TimeSpan.FromMilliseconds(ms));
            counters.RecordSqlCommand(TimeSpan.FromMilliseconds(ms * 10));
        }

        var snapshot = counters.Snapshot();

        snapshot.Requests.P95Ms.ShouldBe(19);
        snapshot.Sql.P95Ms.ShouldBe(190);
    }

    [Test]
    public void Snapshot_WhenOneSlowOutlierInHundred_ShouldNotReportIt()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));

        for (var i = 0; i < 99; i++)
        {
            counters.RecordRequest(RequestKind.DirectTraffic, 200, TimeSpan.FromMilliseconds(12.4));
        }

        counters.RecordRequest(RequestKind.DirectTraffic, 200, TimeSpan.FromSeconds(5));

        counters.Snapshot().Requests.P95Ms.ShouldBe(12);
    }

    [Test]
    public void Snapshot_WhenMoreSamplesThanCapacity_ShouldUseTheLatest()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));

        for (var i = 0; i < LiveTelemetryCounters.LatencySampleCapacity; i++)
        {
            counters.RecordSqlCommand(TimeSpan.FromMilliseconds(500));
        }

        for (var i = 0; i < LiveTelemetryCounters.LatencySampleCapacity; i++)
        {
            counters.RecordSqlCommand(TimeSpan.FromMilliseconds(2));
        }

        counters.Snapshot().Sql.ShouldBe(new SqlCounts(2 * LiveTelemetryCounters.LatencySampleCapacity, 2));
    }

    [Test]
    public async Task RecordRequest_WhenCalledFromManyThreads_ShouldCountEveryCall()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                counters.RecordRequest(RequestKind.DirectTraffic, 200, TimeSpan.FromMilliseconds(1));
                counters.RecordSqlCommand(TimeSpan.FromMilliseconds(1));
                counters.RecordHttpClientCall();
            }
        })));

        var snapshot = counters.Snapshot();
        snapshot.Requests.Direct.ShouldBe(8000);
        snapshot.Sql.PerMinute.ShouldBe(8000);
        snapshot.Http.PerMinute.ShouldBe(8000);
    }
}
