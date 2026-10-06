namespace ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;

/// <summary>
/// Reads the figures from the runtime. Every read is one cheap call; none of them forces a garbage collection.
/// </summary>
internal sealed class SystemProcessMeter : IProcessMeter
{
    /// <inheritdoc />
    public int ProcessorCount => Environment.ProcessorCount;

    /// <inheritdoc />
    public TimeSpan CpuTime => Environment.CpuUsage.TotalTime;

    /// <inheritdoc />
    public long WorkingSetBytes => Environment.WorkingSet;

    /// <inheritdoc />
    public long GcHeapBytes => GC.GetTotalMemory(false);

    /// <inheritdoc />
    public int ThreadPoolThreads => ThreadPool.ThreadCount;
}
