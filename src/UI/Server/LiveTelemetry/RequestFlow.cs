namespace ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;

/// <summary>
/// Marks the asynchronous flow of an HTTP request, from <see cref="LiveTelemetryMiddleware"/> down through the
/// pipeline, so that work done there (a SQL command) can be told from work the process does on its own (message
/// transport polling, hosted services). The mark ends with the request: work the request started and left running
/// is the process's own from then on.
/// </summary>
internal sealed class RequestFlow
{
    private static readonly AsyncLocal<RequestFlow?> Current = new();

    private volatile bool _ended;

    private RequestFlow()
    {
    }

    /// <summary>True when the caller runs in the flow of a request that is still being handled.</summary>
    public static bool IsActive => Current.Value is { _ended: false };

    /// <summary>
    /// Marks the caller's flow, and every flow it starts, until <see cref="End"/>.
    /// </summary>
    public static RequestFlow Begin()
    {
        var flow = new RequestFlow();
        Current.Value = flow;
        return flow;
    }

    /// <summary>
    /// Ends the mark everywhere it flowed to.
    /// </summary>
    public void End() => _ended = true;
}
