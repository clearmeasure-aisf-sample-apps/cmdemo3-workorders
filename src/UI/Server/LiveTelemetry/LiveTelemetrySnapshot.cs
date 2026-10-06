namespace ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;

/// <summary>
/// Aggregate counts over the last <see cref="WindowSeconds"/> seconds, served by <c>GET /_telemetry</c>.
/// Counts are per minute (the window is one minute); latencies are whole milliseconds, null without a sample.
/// </summary>
/// <param name="WindowSeconds">Length of the rolling window.</param>
/// <param name="StartedAt">When this process started counting (UTC, whole seconds).</param>
/// <param name="Requests">Application traffic (probes excluded).</param>
/// <param name="Probes">Diagnostic probes and Azure Front Door health probes.</param>
/// <param name="Sql">SQL commands executed by the process.</param>
/// <param name="Http">Outgoing HTTP client calls (telemetry export excluded).</param>
public sealed record LiveTelemetrySnapshot(
    int WindowSeconds,
    DateTime StartedAt,
    RequestCounts Requests,
    ProbeCounts Probes,
    SqlCounts Sql,
    HttpClientCounts Http);

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
/// <param name="PerMinute">SQL commands executed, whatever caused them.</param>
/// <param name="P95Ms">95th percentile of command duration.</param>
public sealed record SqlCounts(int PerMinute, int? P95Ms);

/// <summary>Outgoing HTTP client calls in the window.</summary>
/// <param name="PerMinute">Completed outgoing HTTP calls.</param>
public sealed record HttpClientCounts(int PerMinute);
