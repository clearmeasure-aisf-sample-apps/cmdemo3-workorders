using System.Net;
using System.Text.Json;
using Shouldly;

namespace ClearMeasure.Bootcamp.UnitTests.UI.Server.LiveTelemetry;

[TestFixture]
public class LiveTelemetryEndpointWebTests
{
    [Test]
    public async Task GetTelemetry_WhenCalledFromAnotherOrigin_ShouldAllowAnyOriginAndForbidCaching()
    {
        await using var factory = new ApiVersioningRoutingWebApplicationFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_telemetry");
        request.Headers.Add("Origin", "https://dashboard.example.com");

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe(["*"]);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Test]
    public async Task GetTelemetry_WhenCalled_ShouldReturnTheDashboardContract()
    {
        await using var factory = new ApiVersioningRoutingWebApplicationFactory();
        using var client = factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/_telemetry"));

        var root = document.RootElement;
        PropertyNames(root).ShouldBe(["windowSeconds", "startedAt", "requests", "probes", "sql", "http"]);
        root.GetProperty("windowSeconds").GetInt32().ShouldBe(60);
        root.GetProperty("startedAt").GetString()!.ShouldMatch(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$");
        PropertyNames(root.GetProperty("requests")).ShouldBe(["perMinute", "frontDoor", "direct", "errors", "p95Ms"]);
        PropertyNames(root.GetProperty("probes")).ShouldBe(["perMinute", "frontDoor"]);
        PropertyNames(root.GetProperty("sql")).ShouldBe(["perMinute", "p95Ms"]);
        PropertyNames(root.GetProperty("http")).ShouldBe(["perMinute"]);
    }

    [Test]
    public async Task GetTelemetry_WhenTrafficAndProbesPrecedeIt_ShouldCountThemApartAndNotBeOutputCached()
    {
        await using var factory = new ApiVersioningRoutingWebApplicationFactory();
        using var client = factory.CreateClient();
        using var throughFrontDoor = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        throughFrontDoor.Headers.Add("X-Azure-FDID", "00000000-0000-0000-0000-000000000000");
        using var frontDoorProbe = new HttpRequestMessage(HttpMethod.Get, "/_healthcheck");
        frontDoorProbe.Headers.Add("X-FD-HealthProbe", "1");
        (await client.SendAsync(throughFrontDoor)).Dispose();
        (await client.SendAsync(frontDoorProbe)).Dispose();
        (await client.GetAsync("/api/ping")).Dispose();
        (await client.GetAsync("/_version")).Dispose();

        using var first = await client.GetAsync("/_telemetry");
        using var second = await client.GetAsync("/_telemetry");

        second.Headers.Age.ShouldBeNull();
        using var before = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var after = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var requests = before.RootElement.GetProperty("requests");
        requests.GetProperty("perMinute").GetInt32().ShouldBe(2);
        requests.GetProperty("frontDoor").GetInt32().ShouldBe(1);
        requests.GetProperty("direct").GetInt32().ShouldBe(1);
        requests.GetProperty("errors").GetInt32().ShouldBe(0);
        requests.GetProperty("p95Ms").ValueKind.ShouldBe(JsonValueKind.Number);
        before.RootElement.GetProperty("probes").GetProperty("perMinute").GetInt32().ShouldBe(1);
        before.RootElement.GetProperty("probes").GetProperty("frontDoor").GetInt32().ShouldBe(1);
        after.RootElement.GetProperty("probes").GetProperty("perMinute").GetInt32().ShouldBe(2);
    }

    private static string[] PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).ToArray();
}
