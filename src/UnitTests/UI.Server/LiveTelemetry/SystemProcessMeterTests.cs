using ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;
using Shouldly;

namespace ClearMeasure.Bootcamp.UnitTests.UI.Server.LiveTelemetry;

[TestFixture]
public class SystemProcessMeterTests
{
    [Test]
    public async Task CpuTime_WhenTheProcessWorks_ShouldAdvance()
    {
        var meter = new SystemProcessMeter();
        var before = meter.CpuTime;

        var advanced = await Task.Run(
            () => SpinWait.SpinUntil(() => meter.CpuTime > before, TimeSpan.FromSeconds(30)));

        advanced.ShouldBeTrue();
        before.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Test]
    public async Task Read_WhenCalledOnAThreadPoolThread_ShouldReportTheFiguresOfTheProcess()
    {
        var meter = new SystemProcessMeter();

        var threads = await Task.Run(() => meter.ThreadPoolThreads);

        threads.ShouldBeGreaterThan(0);
        meter.ProcessorCount.ShouldBe(Environment.ProcessorCount);
        meter.WorkingSetBytes.ShouldBeGreaterThan(0);
        meter.GcHeapBytes.ShouldBeGreaterThan(0);
    }
}
