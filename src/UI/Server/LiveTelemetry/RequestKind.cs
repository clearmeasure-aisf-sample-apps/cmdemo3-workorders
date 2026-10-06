namespace ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;

/// <summary>
/// How an incoming HTTP request is counted by <see cref="LiveTelemetryCounters"/>.
/// </summary>
public enum RequestKind
{
    /// <summary>Application traffic that reached the origin through Azure Front Door.</summary>
    FrontDoorTraffic,

    /// <summary>Application traffic that reached the origin directly.</summary>
    DirectTraffic,

    /// <summary>Health checks, version checks and other diagnostics (the dashboard's own checks).</summary>
    Probe,

    /// <summary>Azure Front Door's health probe to the origin.</summary>
    FrontDoorProbe
}
