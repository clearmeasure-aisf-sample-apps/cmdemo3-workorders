using ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace ClearMeasure.Bootcamp.UnitTests.UI.Server.LiveTelemetry;

[TestFixture]
public class RequestFlowTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 23, 0, 0, TimeSpan.Zero);

    [Test]
    public void IsActive_WhenNoRequestIsHandled_ShouldBeFalse()
    {
        RequestFlow.IsActive.ShouldBeFalse();
    }

    [Test]
    public async Task IsActive_WhenBegun_ShouldBeTrueInThatFlowAndTheFlowsItStartsUntilItEnds()
    {
        var beforeEnd = false;
        var inStartedFlowBeforeEnd = false;
        var afterEnd = true;
        var inStartedFlowAfterEnd = true;

        await Task.Run(async () =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var flow = RequestFlow.Begin();
            beforeEnd = RequestFlow.IsActive;
            inStartedFlowBeforeEnd = await Task.Run(() => RequestFlow.IsActive);
            var leftRunning = Task.Run(async () =>
            {
                await release.Task;
                return RequestFlow.IsActive;
            });

            flow.End();
            release.SetResult();
            afterEnd = RequestFlow.IsActive;
            inStartedFlowAfterEnd = await leftRunning;
        });

        beforeEnd.ShouldBeTrue();
        inStartedFlowBeforeEnd.ShouldBeTrue();
        afterEnd.ShouldBeFalse();
        inStartedFlowAfterEnd.ShouldBeFalse();
    }

    [Test]
    public async Task IsActive_WhenAnotherFlowHandlesARequest_ShouldStayFalseHere()
    {
        var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = Task.Run(async () =>
        {
            var flow = RequestFlow.Begin();
            begun.SetResult();
            await release.Task;
            flow.End();
        });
        await begun.Task;

        var here = RequestFlow.IsActive;
        release.SetResult();
        await request;

        here.ShouldBeFalse();
        RequestFlow.IsActive.ShouldBeFalse();
    }

    [Test]
    public async Task InvokeAsync_WhileTheRequestExecutes_ShouldMarkItsFlowAndNotTheCallers()
    {
        var clock = new StubTimeProvider(Start);
        var duringRequest = false;
        var middleware = new LiveTelemetryMiddleware(
            async _ =>
            {
                await Task.Yield();
                duringRequest = RequestFlow.IsActive;
            },
            new LiveTelemetryCounters(clock),
            clock);

        await middleware.InvokeAsync(new DefaultHttpContext());

        duringRequest.ShouldBeTrue();
        RequestFlow.IsActive.ShouldBeFalse();
    }

    [Test]
    public async Task InvokeAsync_WhenTheRequestFails_ShouldEndTheMark()
    {
        var clock = new StubTimeProvider(Start);
        Task<bool>? leftRunning = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new LiveTelemetryMiddleware(
            _ =>
            {
                leftRunning = Task.Run(async () =>
                {
                    await release.Task;
                    return RequestFlow.IsActive;
                });
                throw new InvalidOperationException("boom");
            },
            new LiveTelemetryCounters(clock),
            clock);

        await Should.ThrowAsync<InvalidOperationException>(() => middleware.InvokeAsync(new DefaultHttpContext()));
        release.SetResult();

        (await leftRunning!).ShouldBeFalse();
    }
}
