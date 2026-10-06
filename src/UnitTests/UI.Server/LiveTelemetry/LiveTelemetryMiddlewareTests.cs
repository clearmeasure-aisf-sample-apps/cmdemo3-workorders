using System.Net;
using System.Text.Json;
using ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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

        var snapshot = counters.Snapshot();
        snapshot.Requests.ShouldBe(new RequestCounts(1, 1, 0, 1, 85));
        snapshot.Process.ExceptionsPerMinute.ShouldBe(0);
        snapshot.Process.InFlight.ShouldBe(0);
    }

    [Test]
    public async Task InvokeAsync_WhileTheRequestExecutes_ShouldCountItInFlight()
    {
        var clock = new StubTimeProvider(Start);
        var counters = new LiveTelemetryCounters(clock);
        var inFlightDuringRequest = -1;
        var middleware = new LiveTelemetryMiddleware(_ =>
        {
            inFlightDuringRequest = counters.Snapshot().Process.InFlight;
            return Task.CompletedTask;
        }, counters, clock);

        await middleware.InvokeAsync(new DefaultHttpContext { Request = { Path = "/_healthcheck" } });

        inFlightDuringRequest.ShouldBe(1);
        counters.Snapshot().Process.InFlight.ShouldBe(0);
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

        var snapshot = counters.Snapshot();
        snapshot.Requests.ShouldBe(new RequestCounts(1, 0, 1, 1, 0));
        snapshot.Process.ExceptionsPerMinute.ShouldBe(1);
        snapshot.Process.InFlight.ShouldBe(0);
    }

    [Test]
    public async Task InvokeAsync_WhenTheExceptionHandlerAnswered_ShouldCountTheException()
    {
        var clock = new StubTimeProvider(Start);
        var counters = new LiveTelemetryCounters(clock);
        var middleware = new LiveTelemetryMiddleware(context =>
        {
            context.Features.Set<IExceptionHandlerFeature>(
                new ExceptionHandlerFeature { Error = new InvalidOperationException("boom") });
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }, counters, clock);

        await middleware.InvokeAsync(new DefaultHttpContext { Request = { Path = "/_healthcheck" } });

        var snapshot = counters.Snapshot();
        snapshot.Process.ExceptionsPerMinute.ShouldBe(1);
        snapshot.Probes.PerMinute.ShouldBe(1);
    }

    [Test]
    public async Task InvokeAsync_WhenTheClientAbortedTheRequest_ShouldNotCountAnException()
    {
        var clock = new StubTimeProvider(Start);
        var counters = new LiveTelemetryCounters(clock);
        var middleware = new LiveTelemetryMiddleware(
            context => Task.FromCanceled(context.RequestAborted), counters, clock);
        var httpContext = new DefaultHttpContext { RequestAborted = new CancellationToken(true) };

        await Should.ThrowAsync<OperationCanceledException>(() => middleware.InvokeAsync(httpContext));

        var snapshot = counters.Snapshot();
        snapshot.Process.ExceptionsPerMinute.ShouldBe(0);
        snapshot.Process.InFlight.ShouldBe(0);
        snapshot.Requests.PerMinute.ShouldBe(1);
    }

    [Test]
    public async Task InvokeAsync_WhenTheExceptionHandlerMiddlewareFollows_ShouldCountWhatItHandled()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddLiveTelemetry();
        await using var app = builder.Build();
        app.UseMiddleware<LiveTelemetryMiddleware>();
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandler = context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Task.CompletedTask;
            }
        });
        app.MapGet("/fails", string () => throw new InvalidOperationException("boom"));
        app.MapGet("/works", () => "ok");
        app.MapLiveTelemetry();
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var failed = await client.GetAsync("/fails");
        using var worked = await client.GetAsync("/works");
        using var document = JsonDocument.Parse(await client.GetStringAsync("/_telemetry"));

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        worked.StatusCode.ShouldBe(HttpStatusCode.OK);
        document.RootElement.GetProperty("process").GetProperty("exceptionsPerMinute").GetInt32().ShouldBe(1);
        document.RootElement.GetProperty("requests").GetProperty("perMinute").GetInt32().ShouldBe(2);
        document.RootElement.GetProperty("requests").GetProperty("errors").GetInt32().ShouldBe(1);
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
