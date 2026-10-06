using Microsoft.AspNetCore.Diagnostics;

namespace ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;

/// <summary>
/// Classifies each request with <see cref="RequestClassifier"/> and records it, with its status code and duration,
/// in <see cref="LiveTelemetryCounters"/> once the rest of the pipeline has run. While the pipeline runs, the
/// request counts as executing and its flow is marked (<see cref="RequestFlow"/>).
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
        var endedInException = false;
        counters.RequestStarted();
        var flow = RequestFlow.Begin();
        try
        {
            await next(context);
            statusCode = context.Response.StatusCode;

            // The exception handler (it runs after this middleware) leaves this feature behind when it turned an
            // exception into the response.
            endedInException = context.Features.Get<IExceptionHandlerFeature>() is not null;
        }
        catch when (context.RequestAborted.IsCancellationRequested)
        {
            statusCode = context.Response.StatusCode;
            throw;
        }
        catch
        {
            endedInException = true;
            throw;
        }
        finally
        {
            flow.End();
            counters.RequestEnded();
            if (endedInException)
            {
                counters.RecordUnhandledException();
            }

            counters.RecordRequest(kind, statusCode, isWebSocket ? null : timeProvider.GetElapsedTime(started));
        }
    }
}
