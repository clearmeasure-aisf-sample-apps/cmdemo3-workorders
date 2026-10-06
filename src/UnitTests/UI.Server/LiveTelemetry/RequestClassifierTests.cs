using ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace ClearMeasure.Bootcamp.UnitTests.UI.Server.LiveTelemetry;

[TestFixture]
public class RequestClassifierTests
{
    [TestCase("/")]
    [TestCase("/_healthcheck")]
    [TestCase("/api/work-orders/status-counts")]
    public void Classify_WhenFrontDoorHealthProbeHeaderIsOne_ShouldBeFrontDoorProbe(string path)
    {
        var request = CreateRequest(path, ("X-FD-HealthProbe", "1"), ("X-Azure-FDID", "fd-id"));

        RequestClassifier.Classify(request).ShouldBe(RequestKind.FrontDoorProbe);
    }

    [TestCase("/_healthcheck")]
    [TestCase("/_healthcheck/detailed")]
    [TestCase("/_version")]
    [TestCase("/_telemetry")]
    [TestCase("/_lamar/services")]
    [TestCase("/health")]
    [TestCase("/HEALTH")]
    [TestCase("/alive")]
    public void Classify_WhenDiagnosticPath_ShouldBeProbeEvenThroughFrontDoor(string path)
    {
        RequestClassifier.Classify(CreateRequest(path)).ShouldBe(RequestKind.Probe);
        RequestClassifier.Classify(CreateRequest(path, ("X-Azure-FDID", "fd-id"))).ShouldBe(RequestKind.Probe);
    }

    [TestCase("/")]
    [TestCase("/api/work-orders/status-counts")]
    [TestCase("/api/health")]
    [TestCase("/healthcheck")]
    [TestCase("/healthy")]
    [TestCase("/_framework/blazor.webassembly.js")]
    [TestCase("/_content/UI.Shared/app.css")]
    public void Classify_WhenApplicationPathWithFrontDoorId_ShouldBeFrontDoorTraffic(string path)
    {
        var request = CreateRequest(path, ("X-Azure-FDID", "fd-id"));

        RequestClassifier.Classify(request).ShouldBe(RequestKind.FrontDoorTraffic);
    }

    [Test]
    public void Classify_WhenApplicationPathWithoutFrontDoorId_ShouldBeDirectTraffic()
    {
        RequestClassifier.Classify(CreateRequest("/")).ShouldBe(RequestKind.DirectTraffic);
    }

    [Test]
    public void Classify_WhenFrontDoorHealthProbeHeaderIsNotOne_ShouldNotBeFrontDoorProbe()
    {
        var request = CreateRequest("/", ("X-FD-HealthProbe", "0"));

        RequestClassifier.Classify(request).ShouldBe(RequestKind.DirectTraffic);
    }

    private static HttpRequest CreateRequest(string path, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext { Request = { Method = HttpMethods.Get, Path = path } };
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        return context.Request;
    }
}
