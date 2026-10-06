using ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;

namespace ClearMeasure.Bootcamp.UnitTests.UI.Server.LiveTelemetry;

/// <summary>
/// Process figures a test sets by hand.
/// </summary>
internal sealed class StubProcessMeter : IProcessMeter
{
    public int ProcessorCount { get; init; } = 4;

    public TimeSpan CpuTime { get; set; } = TimeSpan.FromSeconds(30);

    public long WorkingSetBytes { get; init; }

    public long GcHeapBytes { get; init; }

    public int ThreadPoolThreads { get; init; }
}
