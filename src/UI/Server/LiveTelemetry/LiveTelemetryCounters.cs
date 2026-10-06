namespace ClearMeasure.Bootcamp.UI.Server.LiveTelemetry;

/// <summary>
/// In-process counters over a rolling one-minute window: sixty one-second buckets of counts, and the latest
/// latency samples for percentiles; and the vitals of the process (CPU, memory, threads, requests executing).
/// Thread-safe; recording allocates nothing. Time comes from <see cref="TimeProvider"/>, so the window and the
/// CPU sampling can be driven in tests.
/// </summary>
public sealed class LiveTelemetryCounters
{
    // Length of the rolling window, in seconds.
    private const int WindowSeconds = 60;

    /// <summary>Latency samples kept per series; beyond this many in a minute the percentile uses the latest ones.</summary>
    internal const int LatencySampleCapacity = 4096;

    /// <summary>
    /// Shortest interval CPU use is measured over. A sample is taken when a request completes or the counts are
    /// read and the previous sample is at least this old: never on a timer, and not on every request.
    /// </summary>
    internal const int CpuSampleSeconds = 2;

    private const long WindowMilliseconds = WindowSeconds * 1000L;
    private const double BytesPerMegabyte = 1024 * 1024;

    private readonly TimeProvider _timeProvider;
    private readonly IProcessMeter _processMeter;
    private readonly DateTime _startedAt;
    private readonly long _startedSecond;
    private readonly Lock _gate = new();
    private readonly long[] _bucketSecond = new long[WindowSeconds];
    private readonly int[] _counts = new int[WindowSeconds * Counter.Count];
    private readonly LatencySamples _requestLatency = new(LatencySampleCapacity);
    private readonly LatencySamples _sqlLatency = new(LatencySampleCapacity);
    private readonly CpuUsage _cpu;
    private int _inFlight;

    /// <summary>
    /// Starts counting at the current time of <paramref name="timeProvider"/>, for the current process.
    /// </summary>
    public LiveTelemetryCounters(TimeProvider timeProvider) : this(timeProvider, new SystemProcessMeter())
    {
    }

    /// <summary>
    /// Starts counting at the current time of <paramref name="timeProvider"/>, with the process figures of
    /// <paramref name="processMeter"/>.
    /// </summary>
    internal LiveTelemetryCounters(TimeProvider timeProvider, IProcessMeter processMeter)
    {
        _timeProvider = timeProvider;
        _processMeter = processMeter;
        Array.Fill(_bucketSecond, long.MinValue);
        var now = timeProvider.GetUtcNow().UtcTicks;
        _startedAt = new DateTime(now - now % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        _startedSecond = NowMilliseconds() / 1000;
        _cpu = new CpuUsage(NowMilliseconds(), processMeter.CpuTime, processMeter.ProcessorCount);
    }

    /// <summary>
    /// Counts a request as executing, until <see cref="RequestEnded"/>.
    /// </summary>
    public void RequestStarted() => Interlocked.Increment(ref _inFlight);

    /// <summary>
    /// Stops counting a request as executing.
    /// </summary>
    public void RequestEnded() => Interlocked.Decrement(ref _inFlight);

    /// <summary>
    /// Records a completed incoming request. Traffic durations feed the request percentile; pass null for
    /// long-lived requests (WebSockets) whose duration is not a response time.
    /// </summary>
    public void RecordRequest(RequestKind kind, int statusCode, TimeSpan? elapsed)
    {
        var nowMs = NowMilliseconds();
        lock (_gate)
        {
            SampleCpuWhenDue(nowMs);
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

    /// <summary>
    /// Records a completed SQL command and its duration. <paramref name="duringRequest"/> tells a command executed
    /// while an HTTP request was being handled (traffic or probe) from one the process executed on its own.
    /// </summary>
    public void RecordSqlCommand(TimeSpan elapsed, bool duringRequest)
    {
        var nowMs = NowMilliseconds();
        lock (_gate)
        {
            Increment(nowMs, Counter.SqlCommands);
            if (duringRequest)
            {
                Increment(nowMs, Counter.SqlCommandsDuringRequests);
            }

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

    /// <summary>
    /// Records a request that ended in an unhandled exception: one that escaped the pipeline, or one the
    /// exception handler turned into an error response. Traffic and probes alike; a request its client aborted
    /// is not one.
    /// </summary>
    public void RecordUnhandledException()
    {
        var nowMs = NowMilliseconds();
        lock (_gate)
        {
            Increment(nowMs, Counter.UnhandledExceptions);
        }
    }

    /// <summary>Returns the counts of the last 60 seconds and the vitals of the process now.</summary>
    public LiveTelemetrySnapshot Snapshot()
    {
        var nowMs = NowMilliseconds();
        var nowSecond = nowMs / 1000;
        Span<int> totals = stackalloc int[Counter.Count];
        int? requestP95;
        int? sqlP95;
        double cpuPercent;
        lock (_gate)
        {
            SampleCpuWhenDue(nowMs);
            cpuPercent = _cpu.Percent;
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
            new SqlCounts(
                totals[Counter.SqlCommands],
                totals[Counter.SqlCommandsDuringRequests],
                totals[Counter.SqlCommands] - totals[Counter.SqlCommandsDuringRequests],
                sqlP95),
            new HttpClientCounts(totals[Counter.HttpClientCalls]),
            new ProcessVitals(
                cpuPercent,
                ToWholeMegabytes(_processMeter.WorkingSetBytes),
                ToWholeMegabytes(_processMeter.GcHeapBytes),
                _processMeter.ThreadPoolThreads,
                Volatile.Read(ref _inFlight),
                totals[Counter.UnhandledExceptions],
                Math.Max(0, nowSecond - _startedSecond)));
    }

    private long NowMilliseconds() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    // The lock is held. Reading the processor time is one system call, made at most once per sampling interval.
    private void SampleCpuWhenDue(long nowMs)
    {
        if (_cpu.IsDue(nowMs))
        {
            _cpu.Sample(nowMs, _processMeter.CpuTime);
        }
    }

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

    private static long ToWholeMegabytes(long bytes) =>
        (long)Math.Round(bytes / BytesPerMegabyte, MidpointRounding.AwayFromZero);

    private static class Counter
    {
        public const int FrontDoorTraffic = 0;
        public const int DirectTraffic = 1;
        public const int TrafficErrors = 2;
        public const int Probes = 3;
        public const int FrontDoorProbes = 4;
        public const int SqlCommands = 5;
        public const int HttpClientCalls = 6;
        public const int UnhandledExceptions = 7;
        public const int SqlCommandsDuringRequests = 8;
        public const int Count = 9;
    }

    /// <summary>
    /// CPU use between two samples as a percentage of all processors available to the process; not thread-safe
    /// on its own (the owner holds the lock). Zero until the first interval has passed.
    /// </summary>
    private sealed class CpuUsage(long startedMs, TimeSpan cpuTime, int processorCount)
    {
        private const long SampleMilliseconds = CpuSampleSeconds * 1000L;

        private readonly int _processorCount = Math.Max(1, processorCount);
        private long _sampledAtMs = startedMs;
        private TimeSpan _cpuTime = cpuTime;

        /// <summary>Percentage over the last sampled interval: 0 to 100, one decimal.</summary>
        public double Percent { get; private set; }

        /// <summary>True when the previous sample is a full interval old, or the clock went back.</summary>
        public bool IsDue(long nowMs) => nowMs < _sampledAtMs || nowMs - _sampledAtMs >= SampleMilliseconds;

        public void Sample(long nowMs, TimeSpan cpuTime)
        {
            var wallMs = nowMs - _sampledAtMs;
            if (wallMs > 0)
            {
                var usedMs = (cpuTime - _cpuTime).TotalMilliseconds;
                var percent = 100.0 * usedMs / (wallMs * (double)_processorCount);
                Percent = Math.Round(Math.Clamp(percent, 0, 100), 1, MidpointRounding.AwayFromZero);
            }

            _sampledAtMs = nowMs;
            _cpuTime = cpuTime;
        }
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
