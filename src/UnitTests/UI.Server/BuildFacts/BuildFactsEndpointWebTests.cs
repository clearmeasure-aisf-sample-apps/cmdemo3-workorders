using System.Net;
using System.Text.Json;
using Shouldly;

namespace ClearMeasure.Bootcamp.UnitTests.UI.Server.BuildFacts;

[TestFixture]
public class BuildFactsEndpointWebTests
{
    [Test]
    public async Task GetBuild_WhenCalledFromAnotherOrigin_ShouldAllowAnyOriginAndBeCacheable()
    {
        await using var factory = new ApiVersioningRoutingWebApplicationFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_build");
        request.Headers.Add("Origin", "https://dashboard.example.com");

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe(["*"]);
        response.Headers.CacheControl!.Public.ShouldBeTrue();
        response.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromMinutes(5));
        response.Headers.CacheControl.NoStore.ShouldBeFalse();
    }

    [Test]
    public async Task GetBuild_WhenNoFactsWereStamped_ShouldAnswerTheContractWithTheAssemblyVersion()
    {
        await using var factory = new ApiVersioningRoutingWebApplicationFactory();
        using var client = factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/_build"));

        var root = document.RootElement;
        root.EnumerateObject().Select(p => p.Name).ShouldBe(
        [
            "version", "commit", "commitUrl", "builtAt", "buildUrl", "code", "tests", "coverage", "complexity",
            "crap", "analysis"
        ]);
        root.GetProperty("version").GetString()!.ShouldMatch(@"^\d+\.\d+\.\d+");
        root.GetProperty("version").GetString()!.ShouldNotContain("+");
        root.GetProperty("commit").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("analysis").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Test]
    public async Task GetBuild_WhenApiKeyIsRequired_ShouldStillAnswerWithoutOne()
    {
        await using var factory = new ApiKeyProtectedWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/_build");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
