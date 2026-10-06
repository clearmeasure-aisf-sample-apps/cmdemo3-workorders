using System.Diagnostics;
using System.Diagnostics.Metrics;
using ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;
using OpenTelemetry;
using Shouldly;

namespace ClearMeasure.Bootcamp.UnitTests.UI.Server.LiveTelemetry;

[TestFixture]
public class DependencyCallListenerTests
{
    private static readonly HttpClient Client = new();

    private static readonly DateTimeOffset Start = new(2026, 10, 5, 23, 0, 0, TimeSpan.Zero);

    [Test]
    public void OnSqlClientEvent_WhenBeforeAndAfterArrive_ShouldRecordCommandWithItsDuration()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));
        using var listener = new DependencyCallListener(counters);
        var operationId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();

        listener.OnSqlClientEvent(DependencyCallListener.SqlCommandBefore, SqlPayload(operationId, started));
        listener.OnSqlClientEvent(
            DependencyCallListener.SqlCommandAfter,
            SqlPayload(operationId, started + Stopwatch.Frequency * 12 / 1000));

        counters.Snapshot().Sql.ShouldBe(new SqlCounts(1, 12));
    }

    [Test]
    public void OnSqlClientEvent_WhenCommandFails_ShouldStillCountIt()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));
        using var listener = new DependencyCallListener(counters);
        var operationId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();

        listener.OnSqlClientEvent(DependencyCallListener.SqlCommandBefore, SqlPayload(operationId, started));
        listener.OnSqlClientEvent(DependencyCallListener.SqlCommandError, SqlPayload(operationId, started));

        counters.Snapshot().Sql.PerMinute.ShouldBe(1);
    }

    [Test]
    public void OnSqlClientEvent_WhenAfterHasNoMatchingBeforeOrPayloadIsUnknown_ShouldIgnoreIt()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));
        using var listener = new DependencyCallListener(counters);

        listener.OnSqlClientEvent(DependencyCallListener.SqlCommandAfter, SqlPayload(Guid.NewGuid(), 1));
        listener.OnSqlClientEvent(DependencyCallListener.SqlCommandBefore, new { OperationId = Guid.NewGuid() });
        listener.OnSqlClientEvent(DependencyCallListener.SqlCommandAfter, null);

        counters.Snapshot().Sql.PerMinute.ShouldBe(0);
    }

    [Test]
    public void Start_WhenSqlClientListenerWritesCommandEvents_ShouldCountThem()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));
        using var listener = new DependencyCallListener(counters);
        listener.Start();
        using var sqlClient = new DiagnosticListener(DependencyCallListener.SqlClientListenerName);
        var operationId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();

        sqlClient.IsEnabled(DependencyCallListener.SqlCommandBefore).ShouldBeTrue();
        sqlClient.Write(DependencyCallListener.SqlCommandBefore, SqlPayload(operationId, started));
        sqlClient.Write(DependencyCallListener.SqlCommandAfter, SqlPayload(operationId, started + 1));

        counters.Snapshot().Sql.PerMinute.ShouldBe(1);
    }

    [Test]
    public void OnHttpClientMeasurement_WhenApplicationCall_ShouldCountIt()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));
        using var listener = new DependencyCallListener(counters);
        using var meter = new Meter("LiveTelemetryTests.Http");
        var instrument = meter.CreateHistogram<double>(DependencyCallListener.HttpClientDurationInstrument);

        listener.OnHttpClientMeasurement(instrument, 0.05, [new("server.address", "api.example.com")], null);

        counters.Snapshot().Http.PerMinute.ShouldBe(1);
    }

    [TestCase("centralus-2.in.applicationinsights.azure.com")]
    [TestCase("centralus.livediagnostics.monitor.azure.com")]
    [TestCase("rt.services.visualstudio.com.monitor.azure.com")]
    [TestCase("dc.services.visualstudio.com")]
    public void OnHttpClientMeasurement_WhenCallGoesToAzureMonitorIngestion_ShouldNotCountIt(string host)
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));
        using var listener = new DependencyCallListener(counters);
        using var meter = new Meter("LiveTelemetryTests.Http");
        var instrument = meter.CreateHistogram<double>(DependencyCallListener.HttpClientDurationInstrument);

        listener.OnHttpClientMeasurement(instrument, 0.05, [new("server.address", host)], null);

        counters.Snapshot().Http.PerMinute.ShouldBe(0);
    }

    [Test]
    public void OnHttpClientMeasurement_WhenInstrumentationSuppressed_ShouldNotCountIt()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));
        using var listener = new DependencyCallListener(counters);
        using var meter = new Meter("LiveTelemetryTests.Http");
        var instrument = meter.CreateHistogram<double>(DependencyCallListener.HttpClientDurationInstrument);

        using (SuppressInstrumentationScope.Begin())
        {
            listener.OnHttpClientMeasurement(instrument, 0.05, [new("server.address", "localhost")], null);
        }

        counters.Snapshot().Http.PerMinute.ShouldBe(0);
    }

    [Test]
    public async Task Start_WhenHttpClientCallsAServer_ShouldCountTheCall()
    {
        var counters = new LiveTelemetryCounters(new StubTimeProvider(Start));
        using var listener = new DependencyCallListener(counters);
        listener.Start();
        using var server = new System.Net.HttpListener();
        var port = FreeTcpPort();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var serve = AnswerOneRequestWithNoContentAsync(server);

        using var response = await Client.GetAsync($"http://127.0.0.1:{port}/");
        await serve;

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NoContent);
        counters.Snapshot().Http.PerMinute.ShouldBeGreaterThanOrEqualTo(1);
    }

    private static async Task AnswerOneRequestWithNoContentAsync(System.Net.HttpListener server)
    {
        var context = await server.GetContextAsync();
        context.Response.StatusCode = 204;
        context.Response.Close();
    }

    private static int FreeTcpPort()
    {
        using var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        socket.Start();
        return ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
    }

    private static List<KeyValuePair<string, object>> SqlPayload(Guid operationId, long timestamp) =>
    [
        new("OperationId", operationId),
        new("Operation", "ExecuteReader"),
        new("Timestamp", timestamp)
    ];
}
