using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;

/// <summary>
/// Feeds <see cref="LiveTelemetryCounters"/> with the process's outgoing calls, whether or not a telemetry
/// exporter is configured: SQL commands from Microsoft.Data.SqlClient's diagnostic events (every command, and
/// whether it started in the flow of an HTTP request: <see cref="RequestFlow"/>), and outgoing HTTP calls from the runtime's <c>System.Net.Http</c> meter. HTTP calls that export
/// telemetry (run under OpenTelemetry's suppression scope, or sent to Azure Monitor ingestion) are not counted.
/// </summary>
public sealed class DependencyCallListener(LiveTelemetryCounters counters) :
    IHostedService, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    /// <summary>Name of Microsoft.Data.SqlClient's <see cref="DiagnosticListener"/>.</summary>
    public const string SqlClientListenerName = "SqlClientDiagnosticListener";

    internal const string SqlCommandBefore = "Microsoft.Data.SqlClient.WriteCommandBefore";
    internal const string SqlCommandAfter = "Microsoft.Data.SqlClient.WriteCommandAfter";
    internal const string SqlCommandError = "Microsoft.Data.SqlClient.WriteCommandError";

    private const string HttpClientMeterName = "System.Net.Http";
    internal const string HttpClientDurationInstrument = "http.client.request.duration";

    // Hosts the Azure Monitor exporter sends traces, metrics, logs, Live Metrics and statsbeat to.
    private static readonly string[] TelemetryIngestionHostSuffixes =
    [
        ".applicationinsights.azure.com",
        ".monitor.azure.com",
        "dc.services.visualstudio.com"
    ];

    private readonly ConcurrentDictionary<Guid, SqlCommandStart> _sqlCommandsInFlight = new();
    private readonly Lock _subscriptionGate = new();
    private readonly List<IDisposable> _subscriptions = [];
    private MeterListener? _meterListener;

    /// <summary>Subscribes to SqlClient's diagnostic events and the HTTP client meter.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <summary>Unsubscribes.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Subscribes to SqlClient's diagnostic events and the HTTP client meter.</summary>
    internal void Start()
    {
        lock (_subscriptionGate)
        {
            _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
        }

        var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument is { Meter.Name: HttpClientMeterName, Name: HttpClientDurationInstrument })
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<double>(OnHttpClientMeasurement);
        meterListener.Start();
        _meterListener = meterListener;
    }

    /// <summary>Unsubscribes from every source.</summary>
    public void Dispose()
    {
        lock (_subscriptionGate)
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }

        _meterListener?.Dispose();
        _meterListener = null;
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener value)
    {
        if (value.Name != SqlClientListenerName)
        {
            return;
        }

        lock (_subscriptionGate)
        {
            _subscriptions.Add(value.Subscribe(this, IsSqlCommandEvent));
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value) =>
        OnSqlClientEvent(value.Key, value.Value);

    void IObserver<DiagnosticListener>.OnCompleted()
    {
    }

    void IObserver<DiagnosticListener>.OnError(Exception error)
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnCompleted()
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error)
    {
    }

    /// <summary>
    /// Pairs SqlClient's before and after/error events by operation ID and records the command's duration.
    /// The payloads expose their fields as key/value pairs (<c>OperationId</c>, <c>Timestamp</c> in
    /// <see cref="Stopwatch"/> ticks). SqlClient writes the before event on the flow that executes the command,
    /// so that is where the command is attributed to a request or to the process itself; the after event may
    /// arrive on another flow.
    /// </summary>
    internal void OnSqlClientEvent(string name, object? payload)
    {
        if (!TryReadOperation(payload, out var operationId, out var timestamp))
        {
            return;
        }

        switch (name)
        {
            case SqlCommandBefore:
                _sqlCommandsInFlight[operationId] = new SqlCommandStart(timestamp, RequestFlow.IsActive);
                break;
            case SqlCommandAfter:
            case SqlCommandError:
                if (_sqlCommandsInFlight.TryRemove(operationId, out var started))
                {
                    counters.RecordSqlCommand(
                        Stopwatch.GetElapsedTime(started.Timestamp, timestamp), started.DuringRequest);
                }

                break;
        }
    }

    /// <summary>
    /// Counts a completed outgoing HTTP call unless it is telemetry export.
    /// </summary>
    internal void OnHttpClientMeasurement(
        Instrument instrument,
        double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        if (OpenTelemetry.Sdk.SuppressInstrumentation)
        {
            return;
        }

        foreach (var tag in tags)
        {
            if (tag is { Key: "server.address", Value: string host } && IsTelemetryIngestionHost(host))
            {
                return;
            }
        }

        counters.RecordHttpClientCall();
    }

    private static bool IsTelemetryIngestionHost(string host)
    {
        foreach (var suffix in TelemetryIngestionHostSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSqlCommandEvent(string name) =>
        name is SqlCommandBefore or SqlCommandAfter or SqlCommandError;

    private readonly record struct SqlCommandStart(long Timestamp, bool DuringRequest);

    private static bool TryReadOperation(object? payload, out Guid operationId, out long timestamp)
    {
        operationId = Guid.Empty;
        timestamp = 0;
        if (payload is not IEnumerable<KeyValuePair<string, object>> fields)
        {
            return false;
        }

        var found = 0;
        foreach (var field in fields)
        {
            switch (field)
            {
                case { Key: "OperationId", Value: Guid id }:
                    operationId = id;
                    found++;
                    break;
                case { Key: "Timestamp", Value: long ticks }:
                    timestamp = ticks;
                    found++;
                    break;
            }
        }

        return found == 2;
    }
}
