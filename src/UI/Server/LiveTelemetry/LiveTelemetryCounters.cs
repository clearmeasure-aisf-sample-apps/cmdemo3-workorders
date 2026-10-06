namespace ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;

/// <summary>
/// In-process counters over a rolling one-minute window: sixty one-second buckets of counts, and the latest
/// latency samples for percentiles. Thread-safe; recording allocates nothing. Time comes from
/// <see cref="TimeProvider"/>, so the window can be driven in tests.
/// </summary>
public sealed class LiveTelemetryCounters
{
    // Length of the rolling window, in seconds.
    private const int WindowSeconds = 60;

    /// <summary>Latency samples kept per series; beyond this many in a minute the percentile uses the latest ones.</summary>
    internal const int LatencySampleCapacity = 4096;

    private const long WindowMilliseconds = WindowSeconds * 1000L;

    private readonly TimeProvider _timeProvider;
    private readonly DateTime _startedAt;
    private readonly Lock _gate = new();
    private readonly long[] _bucketSecond = new long[WindowSeconds];
    private readonly int[] _counts = new int[WindowSeconds * Counter.Count];
    private readonly LatencySamples _requestLatency = new(LatencySampleCapacity);
    private readonly LatencySamples _sqlLatency = new(LatencySampleCapacity);

    /// <summary>
    /// Starts counting at the current time of <paramref name="timeProvider"/>.
    /// </summary>
    public LiveTelemetryCounters(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        Array.Fill(_bucketSecond, long.MinValue);
        var now = timeProvider.GetUtcNow().UtcTicks;
        _startedAt = new DateTime(now - now % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    /// <summary>
    /// Records a completed incoming request. Traffic durations feed the request percentile; pass null for
    /// long-lived requests (WebSockets) whose duration is not a response time.
    /// </summary>
    public void RecordRequest(RequestKind kind, int statusCode, TimeSpan? elapsed)
    {
        var nowMs = NowMilliseconds();
        lock (_gate)
        {
            switch (kind)
            {
                case RequestKind.FrontDoorProbe:
                    Increment(nowMs, Counter.FrontDoorProbes);
                    return;
                case RequestKind.Probe:
                    Increment(nowMs, Counter.Probes);
                    return;
                case RequestKind.FrontDoorTraffic:
                    Increment(nowMs, Counter.FrontDoorTraffic);
                    break;
                default:
                    Increment(nowMs, Counter.DirectTraffic);
                    break;
            }

            if (statusCode >= StatusCodes.Status500InternalServerError)
            {
                Increment(nowMs, Counter.TrafficErrors);
            }

            if (elapsed is { } duration)
            {
                _requestLatency.Add(nowMs, ToWholeMilliseconds(duration));
            }
        }
    }

    /// <summary>Records a completed SQL command and its duration.</summary>
    public void RecordSqlCommand(TimeSpan elapsed)
    {
        var nowMs = NowMilliseconds();
        lock (_gate)
        {
            Increment(nowMs, Counter.SqlCommands);
            _sqlLatency.Add(nowMs, ToWholeMilliseconds(elapsed));
        }
    }

    /// <summary>Records a completed outgoing HTTP call.</summary>
    public void RecordHttpClientCall()
    {
        var nowMs = NowMilliseconds();
        lock (_gate)
        {
            Increment(nowMs, Counter.HttpClientCalls);
        }
    }

    /// <summary>Returns the counts of the last 60 seconds.</summary>
    public LiveTelemetrySnapshot Snapshot()
    {
        var nowMs = NowMilliseconds();
        var nowSecond = nowMs / 1000;
        Span<int> totals = stackalloc int[Counter.Count];
        int? requestP95;
        int? sqlP95;
        lock (_gate)
        {
            for (var bucket = 0; bucket < WindowSeconds; bucket++)
            {
                var second = _bucketSecond[bucket];
                if (second <= nowSecond - WindowSeconds || second > nowSecond)
                {
                    continue;
                }

                for (var counter = 0; counter < Counter.Count; counter++)
                {
                    totals[counter] += _counts[bucket * Counter.Count + counter];
                }
            }

            requestP95 = _requestLatency.Percentile95(nowMs - WindowMilliseconds);
            sqlP95 = _sqlLatency.Percentile95(nowMs - WindowMilliseconds);
        }

        var frontDoor = totals[Counter.FrontDoorTraffic];
        var direct = totals[Counter.DirectTraffic];
        return new LiveTelemetrySnapshot(
            WindowSeconds,
            _startedAt,
            new RequestCounts(frontDoor + direct, frontDoor, direct, totals[Counter.TrafficErrors], requestP95),
            new ProbeCounts(totals[Counter.Probes], totals[Counter.FrontDoorProbes]),
            new SqlCounts(totals[Counter.SqlCommands], sqlP95),
            new HttpClientCounts(totals[Counter.HttpClientCalls]));
    }

    private long NowMilliseconds() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private void Increment(long nowMs, int counter)
    {
        var second = nowMs / 1000;
        var bucket = (int)(second % WindowSeconds);
        if (_bucketSecond[bucket] != second)
        {
            _bucketSecond[bucket] = second;
            Array.Clear(_counts, bucket * Counter.Count, Counter.Count);
        }

        _counts[bucket * Counter.Count + counter]++;
    }

    private static int ToWholeMilliseconds(TimeSpan elapsed) =>
        (int)Math.Clamp(Math.Round(elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero), 0, int.MaxValue);

    private static class Counter
    {
        public const int FrontDoorTraffic = 0;
        public const int DirectTraffic = 1;
        public const int TrafficErrors = 2;
        public const int Probes = 3;
        public const int FrontDoorProbes = 4;
        public const int SqlCommands = 5;
        public const int HttpClientCalls = 6;
        public const int Count = 7;
    }

    /// <summary>
    /// Ring buffer of (time, milliseconds) samples; not thread-safe on its own (the owner holds the lock).
    /// </summary>
    private sealed class LatencySamples(int capacity)
    {
        private readonly long[] _times = new long[capacity];
        private readonly int[] _values = new int[capacity];
        private readonly int[] _scratch = new int[capacity];
        private int _next;
        private int _count;

        public void Add(long timeMs, int valueMs)
        {
            _times[_next] = timeMs;
            _values[_next] = valueMs;
            _next = (_next + 1) % _times.Length;
            _count = Math.Min(_count + 1, _times.Length);
        }

        /// <summary>Nearest-rank 95th percentile of the samples newer than <paramref name="afterMs"/>.</summary>
        public int? Percentile95(long afterMs)
        {
            var selected = 0;
            for (var i = 0; i < _count; i++)
            {
                if (_times[i] > afterMs)
                {
                    _scratch[selected++] = _values[i];
                }
            }

            if (selected == 0)
            {
                return null;
            }

            var window = _scratch.AsSpan(0, selected);
            window.Sort();
            var rank = (int)Math.Ceiling(0.95 * selected);
            return window[rank - 1];
        }
    }
}
