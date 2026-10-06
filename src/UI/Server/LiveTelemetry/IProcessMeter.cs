namespace ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;

/// <summary>
/// Raw figures of the current process, read when <see cref="LiveTelemetryCounters"/> needs them.
/// </summary>
internal interface IProcessMeter
{
    /// <summary>Processors available to the process (a container's CPU limit counts).</summary>
    int ProcessorCount { get; }

    /// <summary>Processor time the process has used since it started, user and kernel, summed over all cores.</summary>
    TimeSpan CpuTime { get; }

    /// <summary>Physical memory mapped to the process.</summary>
    long WorkingSetBytes { get; }

    /// <summary>Bytes the garbage collector thinks are allocated on the managed heap.</summary>
    long GcHeapBytes { get; }

    /// <summary>Threads that exist in the thread pool.</summary>
    int ThreadPoolThreads { get; }
}
