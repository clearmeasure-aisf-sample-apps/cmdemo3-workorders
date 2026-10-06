using ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace ClearMeasure.Bootcamp.UnitTests.UI.Server.LiveTelemetry;

[TestFixture]
public class LiveTelemetryMiddlewareTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 23, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task InvokeAsync_WhenRequestCompletes_ShouldRecordItsKindStatusAndDuration()
    {
        var clock = new StubTimeProvider(Start);
        var counters = new LiveTelemetryCounters(clock);
        var middleware = new LiveTelemetryMiddleware(context =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(85));
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        }, counters, clock);
        var httpContext = new DefaultHttpContext { Request = { Path = "/", Headers = { ["X-Azure-FDID"] = "fd-id" } } };

        await middleware.InvokeAsync(httpContext);

        counters.Snapshot().Requests.ShouldBe(new RequestCounts(1, 1, 0, 1, 85));
    }

    [Test]
    public async Task InvokeAsync_WhenNextThrows_ShouldRecordServerErrorAndRethrow()
    {
        var clock = new StubTimeProvider(Start);
        var counters = new LiveTelemetryCounters(clock);
        var middleware = new LiveTelemetryMiddleware(
            _ => throw new InvalidOperationException("boom"), counters, clock);
        var httpContext = new DefaultHttpContext { Request = { Path = "/api/work-orders/status-counts" } };

        await Should.ThrowAsync<InvalidOperationException>(() => middleware.InvokeAsync(httpContext));

        counters.Snapshot().Requests.ShouldBe(new RequestCounts(1, 0, 1, 1, 0));
    }

    [Test]
    public async Task InvokeAsync_WhenDiagnosticPath_ShouldCountProbeNotTraffic()
    {
        var clock = new StubTimeProvider(Start);
        var counters = new LiveTelemetryCounters(clock);
        var middleware = new LiveTelemetryMiddleware(_ => Task.CompletedTask, counters, clock);
        var httpContext = new DefaultHttpContext { Request = { Path = "/_telemetry" } };

        await middleware.InvokeAsync(httpContext);

        var snapshot = counters.Snapshot();
        snapshot.Probes.PerMinute.ShouldBe(1);
        snapshot.Requests.PerMinute.ShouldBe(0);
    }
}
