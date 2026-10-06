using System.Net.Mime;
using System.Text;
using Microsoft.Net.Http.Headers;

// ReSharper disable UnusedMethodReturnValue.Global -- Qodana C6 (#9039): fluent
// IServiceCollection extension-method pattern; chained return value is by design, not always used.
namespace ClearMeasure.Bootcamp.UI.Server.BuildFacts;

/// <summary>
/// Registers the build facts and maps <c>GET /_build</c>, which a health dashboard in another origin reads to show
/// what each environment runs: version, commit, size of the code, tests, coverage, complexity and analysis.
/// </summary>
public static class BuildFactsExtensions
{
    private const string EndpointPath = "/_build";

    // The facts of a deployed build never change: a browser may keep them for five minutes.
    private const string CacheControl = "public, max-age=300";

    /// <summary>
    /// Adds <see cref="BuildFactsProvider"/>.
    /// </summary>
    public static IServiceCollection AddBuildFacts(this IServiceCollection services)
    {
        services.AddSingleton<BuildFactsProvider>();
        return services;
    }

    /// <summary>
    /// Maps <c>GET /_build</c>: anonymous, facts about the build only, readable from any origin, cacheable.
    /// It is outside <c>/api</c>, so neither the API key nor the API rate limiter applies.
    /// </summary>
    public static RouteHandlerBuilder MapBuildFacts(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(EndpointPath, WriteFacts)
            .AllowAnonymous();

    private static IResult WriteFacts(HttpContext context, BuildFactsProvider facts)
    {
        context.Response.Headers[HeaderNames.AccessControlAllowOrigin] = "*";
        context.Response.Headers[HeaderNames.CacheControl] = CacheControl;
        return Results.Text(facts.Json, MediaTypeNames.Application.Json, Encoding.UTF8);
    }
}
