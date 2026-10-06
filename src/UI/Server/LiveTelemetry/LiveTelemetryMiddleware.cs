namespace ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;

/// <summary>
/// Classifies each request with <see cref="RequestClassifier"/> and records it, with its status code and duration,
/// in <see cref="LiveTelemetryCounters"/> once the rest of the pipeline has run.
/// </summary>
public sealed class LiveTelemetryMiddleware(RequestDelegate next, LiveTelemetryCounters counters, TimeProvider timeProvider)
{
    /// <summary>
    /// Invokes the next middleware and records the request.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var kind = RequestClassifier.Classify(context.Request);
        var isWebSocket = context.WebSockets.IsWebSocketRequest;
        var started = timeProvider.GetTimestamp();
        var statusCode = StatusCodes.Status500InternalServerError;
        try
        {
            await next(context);
            statusCode = context.Response.StatusCode;
        }
        catch when (context.RequestAborted.IsCancellationRequested)
        {
            statusCode = context.Response.StatusCode;
            throw;
        }
        finally
        {
            counters.RecordRequest(kind, statusCode, isWebSocket ? null : timeProvider.GetElapsedTime(started));
        }
    }
}
