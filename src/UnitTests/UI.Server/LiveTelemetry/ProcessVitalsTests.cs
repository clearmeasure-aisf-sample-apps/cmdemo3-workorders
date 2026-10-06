using ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;
using Shouldly;

namespace ClearMeasure.Bootcamp.UnitTests.UI.Server.LiveTelemetry;

[TestFixture]
public class ProcessVitalsTests
{
    private const long Megabyte = 1024 * 1024;

    private static readonly DateTimeOffset Start = new(2026, 10, 5, 23, 0, 0, 250, TimeSpan.Zero);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(LiveTelemetryCounters.CpuSampleSeconds);

    [Test]
    public void Snapshot_WhenProcessFiguresRead_ShouldReportWholeMegabytesAndThreads()
    {
        var meter = new StubProcessMeter
        {
            WorkingSetBytes = 412 * Megabyte + 300_000,
            GcHeapBytes = 95 * Megabyte + 700_000,
            ThreadPoolThreads = 41
        };
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start), meter);

        var process = counters.Snapshot().Process;

        process.WorkingSetMb.ShouldBe(412);
        process.GcHeapMb.ShouldBe(96);
        process.Threads.ShouldBe(41);
        process.ShouldBe(new ProcessVitals(0, 412, 96, 41, 0, 0, 0));
    }

    [Test]
    public void Snapshot_WhenFirstIntervalHasNotPassed_ShouldReportZeroCpu()
    {
        var clock = new StubTimeProvider(Start);
        var meter = new StubProcessMeter();
        var counters = new LiveTelemetryCounters(clock, meter);

        clock.Advance(SampleInterval - TimeSpan.FromMilliseconds(1));
        meter.CpuTime += TimeSpan.FromSeconds(1);

        counters.Snapshot().Process.CpuPercent.ShouldBe(0);
    }

    [Test]
    public void Snapshot_WhenIntervalPassed_ShouldReportCpuAsShareOfAllProcessors()
    {
        var clock = new StubTimeProvider(Start);
        var meter = new StubProcessMeter { ProcessorCount = 4 };
        var counters = new LiveTelemetryCounters(clock, meter);

        clock.Advance(TimeSpan.FromSeconds(5));
        meter.CpuTime += TimeSpan.FromMilliseconds(640);

        counters.Snapshot().Process.CpuPercent.ShouldBe(3.2);
    }

    [Test]
    public void Snapshot_WhenReadAgainWithinTheInterval_ShouldKeepTheLastSample()
    {
        var clock = new StubTimeProvider(Start);
        var meter = new StubProcessMeter { ProcessorCount = 2 };
        var counters = new LiveTelemetryCounters(clock, meter);
        clock.Advance(SampleInterval);
        meter.CpuTime += TimeSpan.FromMilliseconds(400);
        var first = counters.Snapshot().Process.CpuPercent;

        clock.Advance(SampleInterval / 2);
        meter.CpuTime += TimeSpan.FromSeconds(1);
        var withinInterval = counters.Snapshot().Process.CpuPercent;
        clock.Advance(SampleInterval / 2);
        meter.CpuTime += TimeSpan.FromSeconds(1);
        var afterInterval = counters.Snapshot().Process.CpuPercent;

        first.ShouldBe(10);
        withinInterval.ShouldBe(10);
        afterInterval.ShouldBe(50);
    }

    [Test]
    public void RecordRequest_WhenIntervalPassed_ShouldSampleCpuWithoutARead()
    {
        var clock = new StubTimeProvider(Start);
        var meter = new StubProcessMeter { ProcessorCount = 4 };
        var counters = new LiveTelemetryCounters(clock, meter);
        clock.Advance(SampleInterval);
        meter.CpuTime += TimeSpan.FromMilliseconds(800);

        counters.RecordRequest(RequestKind.Probe, 200, TimeSpan.FromMilliseconds(1));
        clock.Advance(SampleInterval / 2);
        meter.CpuTime += TimeSpan.FromSeconds(4);

        counters.Snapshot().Process.CpuPercent.ShouldBe(10);
    }

    [Test]
    public void Snapshot_WhenCpuTimeExceedsTheInterval_ShouldNotExceedHundred()
    {
        var clock = new StubTimeProvider(Start);
        var meter = new StubProcessMeter { ProcessorCount = 1 };
        var counters = new LiveTelemetryCounters(clock, meter);

        clock.Advance(SampleInterval);
        meter.CpuTime += SampleInterval * 3;

        counters.Snapshot().Process.CpuPercent.ShouldBe(100);
    }

    [Test]
    public void Snapshot_WhenTheClockWentBack_ShouldKeepTheLastSampleAndStartANewInterval()
    {
        var clock = new StubTimeProvider(Start);
        var meter = new StubProcessMeter { ProcessorCount = 1 };
        var counters = new LiveTelemetryCounters(clock, meter);
        clock.Advance(SampleInterval);
        meter.CpuTime += SampleInterval / 4;
        counters.Snapshot().Process.CpuPercent.ShouldBe(25);

        clock.Advance(TimeSpan.FromMinutes(-5));
        var afterStepBack = counters.Snapshot().Process.CpuPercent;
        clock.Advance(SampleInterval);
        meter.CpuTime += SampleInterval / 2;
        var afterNextInterval = counters.Snapshot().Process.CpuPercent;

        afterStepBack.ShouldBe(25);
        afterNextInterval.ShouldBe(50);
    }

    [Test]
    public void Snapshot_WhenRequestsStartAndEnd_ShouldReportThoseStillExecuting()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start), new StubProcessMeter());

        counters.RequestStarted();
        counters.RequestStarted();
        counters.RequestStarted();
        counters.RequestEnded();

        counters.Snapshot().Process.InFlight.ShouldBe(2);
    }

    [Test]
    public void RecordUnhandledException_WhenWindowRolls_ShouldDropThoseOlderThanSixtySeconds()
    {
        var clock = new StubTimeProvider(Start);
        var counters = new LiveTelemetryCounters(clock, new StubProcessMeter());
        counters.RecordUnhandledException();
        clock.Advance(TimeSpan.FromSeconds(30));
        counters.RecordUnhandledException();
        counters.RecordUnhandledException();

        clock.Advance(TimeSpan.FromSeconds(29));
        var beforeRoll = counters.Snapshot().Process.ExceptionsPerMinute;
        clock.Advance(TimeSpan.FromSeconds(1));
        var afterFirstRoll = counters.Snapshot().Process.ExceptionsPerMinute;
        clock.Advance(TimeSpan.FromSeconds(30));
        var afterSecondRoll = counters.Snapshot().Process.ExceptionsPerMinute;

        beforeRoll.ShouldBe(3);
        afterFirstRoll.ShouldBe(2);
        afterSecondRoll.ShouldBe(0);
    }

    [Test]
    public void RecordUnhandledException_WhenRecorded_ShouldNotCountARequest()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start), new StubProcessMeter());

        counters.RecordUnhandledException();

        var snapshot = counters.Snapshot();
        snapshot.Process.ExceptionsPerMinute.ShouldBe(1);
        snapshot.Requests.ShouldBe(new RequestCounts(0, 0, 0, 0, null));
        snapshot.Probes.ShouldBe(new ProbeCounts(0, 0));
    }

    [Test]
    public void Snapshot_WhenTimePasses_ShouldReportWholeSecondsSinceStartedAt()
    {
        var clock = new StubTimeProvider(Start);
        var counters = new LiveTelemetryCounters(clock, new StubProcessMeter());

        clock.Advance(TimeSpan.FromMilliseconds(5_321_700));
        var snapshot = counters.Snapshot();

        snapshot.Process.UptimeSeconds.ShouldBe(5321);
        snapshot.StartedAt.AddSeconds(snapshot.Process.UptimeSeconds)
            .ShouldBe(new DateTime(2026, 10, 6, 0, 28, 41, DateTimeKind.Utc));
    }

    [Test]
    public void Snapshot_WhenCountersUseTheRuntime_ShouldReportTheCurrentProcess()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));

        var process = counters.Snapshot().Process;

        process.WorkingSetMb.ShouldBeGreaterThan(0);
        process.GcHeapMb.ShouldBeGreaterThan(0);
        process.Threads.ShouldBeGreaterThanOrEqualTo(0);
        process.CpuPercent.ShouldBe(0);
    }
}
