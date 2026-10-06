using ChurchBulletin.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Shouldly;

namespace ClearMeasure.Bootcamp.UnitTests.ServiceDefaults;

[TestFixture]
public class OpenTelemetryResourceTests
{
    [Test]
    public void AddServiceDefaults_WhenOtelServiceNameConfigured_ShouldUseItAsServiceName()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["OTEL_SERVICE_NAME"] = "ca-test-ui";
        builder.AddServiceDefaults();
        using var host = builder.Build();

        var resource = host.Services.GetRequiredService<TracerProvider>().GetResource();

        ServiceNameOf(resource.Attributes).ShouldBe("ca-test-ui");
    }

    [Test]
    public void AddServiceDefaults_WhenOtelResourceAttributesConfigured_ShouldUseTheirServiceName()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["OTEL_RESOURCE_ATTRIBUTES"] = "service.name=ca-agent-ui,deployment.environment=tdd";
        builder.AddServiceDefaults();
        using var host = builder.Build();

        var attributes = host.Services.GetRequiredService<TracerProvider>().GetResource().Attributes.ToList();

        ServiceNameOf(attributes).ShouldBe("ca-agent-ui");
        attributes.ShouldContain(a => a.Key == "deployment.environment" && (string)a.Value == "tdd");
    }

    private static string? ServiceNameOf(IEnumerable<KeyValuePair<string, object>> attributes) =>
        attributes.Single(a => a.Key == "service.name").Value as string;
}
