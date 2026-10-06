namespace ClearMeasure.Bootcamp.UnitTests.UI.Server.LiveTelemetry;

/// <summary>
/// Manually advanced clock; timestamps follow the same clock so elapsed times are deterministic.
/// </summary>
internal sealed class StubTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _now.UtcTicks;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
