namespace ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;

/// <summary>
/// Aggregate counts over the last <see cref="WindowSeconds"/> seconds and the vitals of the process, served by
/// <c>GET /_telemetry</c>. Counts are per minute (the window is one minute); latencies are whole milliseconds,
/// null without a sample.
/// </summary>
/// <param name="WindowSeconds">Length of the rolling window.</param>
/// <param name="StartedAt">When this process started counting (UTC, whole seconds).</param>
/// <param name="Requests">Application traffic (probes excluded).</param>
/// <param name="Probes">Diagnostic probes and Azure Front Door health probes.</param>
/// <param name="Sql">SQL commands executed by the process.</param>
/// <param name="Http">Outgoing HTTP client calls (telemetry export excluded).</param>
/// <param name="Process">Vitals of the process that answers.</param>
public sealed record LiveTelemetrySnapshot(
    int WindowSeconds,
    DateTime StartedAt,
    RequestCounts Requests,
    ProbeCounts Probes,
    SqlCounts Sql,
    HttpClientCounts Http,
    ProcessVitals Process);

/// <summary>Application traffic in the window.</summary>
/// <param name="PerMinute">Traffic requests: <paramref name="FrontDoor"/> plus <paramref name="Direct"/>.</param>
/// <param name="FrontDoor">Traffic requests forwarded by Azure Front Door.</param>
/// <param name="Direct">Traffic requests that reached the origin directly.</param>
/// <param name="Errors">Traffic responses with status 500 or above.</param>
/// <param name="P95Ms">95th percentile of traffic request duration.</param>
public sealed record RequestCounts(int PerMinute, int FrontDoor, int Direct, int Errors, int? P95Ms);

/// <summary>Probes in the window.</summary>
/// <param name="PerMinute">Diagnostic probes (<c>/_*</c>, <c>/health</c>, <c>/alive</c>).</param>
/// <param name="FrontDoor">Azure Front Door health probes.</param>
public sealed record ProbeCounts(int PerMinute, int FrontDoor);

/// <summary>SQL commands in the window.</summary>
/// <param name="PerMinute">
/// SQL commands executed, whatever caused them: <paramref name="Requests"/> plus <paramref name="Background"/>.
/// </param>
/// <param name="Requests">Commands executed while an HTTP request was being handled, traffic or probe.</param>
/// <param name="Background">
/// Commands the process executed on its own: message transport polling, hosted services, startup.
/// </param>
/// <param name="P95Ms">95th percentile of command duration, of all commands.</param>
public sealed record SqlCounts(int PerMinute, int Requests, int Background, int? P95Ms);

/// <summary>Outgoing HTTP client calls in the window.</summary>
/// <param name="PerMinute">Completed outgoing HTTP calls.</param>
public sealed record HttpClientCounts(int PerMinute);

/// <summary>Vitals of the process at the time of the snapshot.</summary>
/// <param name="CpuPercent">
/// CPU the process used over the last sampling interval, as a percentage of all processors available to it
/// (0 to 100, one decimal). The interval ends when a request completes or the counts are read, at least
/// <see cref="LiveTelemetryCounters.CpuSampleSeconds"/> seconds after the previous one; 0 until the first one ends.
/// </param>
/// <param name="WorkingSetMb">Physical memory mapped to the process, in whole megabytes.</param>
/// <param name="GcHeapMb">Managed heap the garbage collector reports as allocated, in whole megabytes.</param>
/// <param name="Threads">Threads in the thread pool.</param>
/// <param name="InFlight">
/// Requests executing now, traffic and probes: the request that reads this is one of them, and so is every open
/// WebSocket.
/// </param>
/// <param name="ExceptionsPerMinute">
/// Requests in the window that ended in an unhandled exception: it escaped the pipeline, or the exception handler
/// turned it into an error response. First-chance exceptions that the code caught are not counted.
/// </param>
/// <param name="UptimeSeconds">Whole seconds since <see cref="LiveTelemetrySnapshot.StartedAt"/>.</param>
public sealed record ProcessVitals(
    double CpuPercent,
    long WorkingSetMb,
    long GcHeapMb,
    int Threads,
    int InFlight,
    int ExceptionsPerMinute,
    long UptimeSeconds);
