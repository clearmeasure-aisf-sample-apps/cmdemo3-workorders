using System.Net;
using System.Text.Json;
using ClearMeasure.Bootcamp.Core;
using ClearMeasure.Bootcamp.Core.Model;
using ClearMeasure.Bootcamp.Core.Queries;
using ClearMeasure.Bootcamp.UI.Api.Controllers;
using ClearMeasure.Bootcamp.UI.Server;
using ClearMeasure.Bootcamp.UnitTests.UI.Server;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Shouldly;

namespace ClearMeasure.Bootcamp.UnitTests.UI.Api;

[TestFixture]
public class WorkOrderStatusCountsControllerTests
{
    [Test]
    public async Task Get_WhenCalled_ShouldReturnCountsFromWorkOrderCountByStatusQuery()
    {
        var counts = WorkOrderStatus.GetAllItems().ToDictionary(s => s.Key, _ => 0);
        counts[WorkOrderStatus.Assigned.Key] = 3;
        var bus = new StubBus(counts);
        var controller = new WorkOrderStatusCountsController(bus);

        var result = await controller.Get();

        result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeSameAs(counts);
        bus.Requests.ShouldHaveSingleItem().ShouldBeOfType<WorkOrderCountByStatusQuery>();
    }

    [Test]
    public void Controller_ShouldNotUseTheApiSlidingWindowRateLimit()
    {
        typeof(WorkOrderStatusCountsController)
            .GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
            .ShouldBeEmpty();
    }

    [Test]
    public async Task Get_WhenOneClientCallsTwiceASecondForAMinute_ShouldAnswerEveryCallFromTheDatabase()
    {
        // The shared in-memory SQLite database lives while a connection is open; hold one so the schema the
        // Testing host creates at startup stays for the requests.
        await using var keepDatabase = new SqliteConnection(WebApplicationTestingDatabase.SqliteSharedMemoryConnectionString);
        await keepDatabase.OpenAsync();
        await using var factory = new ApiVersioningRoutingWebApplicationFactory();
        using var client = factory.CreateClient();

        for (var i = 0; i < 120; i++)
        {
            using var response = await client.GetAsync("/api/work-orders/status-counts");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        }

        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/v1.0/work-orders/status-counts"));
        document.RootElement.EnumerateObject().Select(p => p.Name)
            .ShouldBe(WorkOrderStatus.GetAllItems().Select(s => s.Key), ignoreOrder: true);
    }

    private sealed class StubBus(Dictionary<string, int> counts) : IBus
    {
        public List<object> Requests { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request)
        {
            Requests.Add(request);
            return Task.FromResult((TResponse)(object)counts);
        }

        public Task<object?> Send(object request) => throw new NotSupportedException();

        public Task Publish(INotification notification) => Task.CompletedTask;
    }
}
