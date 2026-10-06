using System.Text.Json;
using Microsoft.Net.Http.Headers;

// ReSharper disable UnusedMethodReturnValue.Global -- Qodana C6 (#9039): fluent
// IServiceCollection extension-method pattern; chained return value is by design, not always used.
namespace ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;

/// <summary>
/// Registers the live counters and maps <c>GET /_telemetry</c>, which a health dashboard in another origin polls.
/// </summary>
public static class LiveTelemetryExtensions
{
    private const string EndpointPath = "/_telemetry";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Adds <see cref="LiveTelemetryCounters"/> and the <see cref="DependencyCallListener"/> that feeds it.
    /// </summary>
    public static IServiceCollection AddLiveTelemetry(this IServiceCollection services)
    {
        services.AddSingleton<LiveTelemetryCounters>();
        services.AddSingleton<DependencyCallListener>();
        services.AddHostedService(sp => sp.GetRequiredService<DependencyCallListener>());
        return services;
    }

    /// <summary>
    /// Maps <c>GET /_telemetry</c>: anonymous, aggregate numbers only, readable from any origin, never cached.
    /// It is outside <c>/api</c>, so neither the API key nor the API rate limiter applies.
    /// </summary>
    public static RouteHandlerBuilder MapLiveTelemetry(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(EndpointPath, WriteSnapshot)
            .AllowAnonymous()
            .CacheOutput(policy => policy.NoCache());

    private static IResult WriteSnapshot(HttpContext context, LiveTelemetryCounters counters)
    {
        context.Response.Headers[HeaderNames.AccessControlAllowOrigin] = "*";
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return Results.Json(counters.Snapshot(), JsonOptions);
    }
}
