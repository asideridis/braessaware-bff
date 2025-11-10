using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace BraessAware.Bff.Planner;

public sealed class InMemoryNodeStatsStore : INodeStatsStore
{
    private readonly ConcurrentDictionary<(string Route, string Node), SlidingWindow> _latencies = new();
    private readonly ConcurrentDictionary<(string Route, string Node), RequestCounters> _counters = new();
    private readonly Meter _meter;
    private readonly Histogram<double> _latencyHistogram;
    private readonly Counter<long> _errorCounter;

    public InMemoryNodeStatsStore(Meter meter)
    {
        _meter = meter;
        _latencyHistogram = meter.CreateHistogram<double>("braess_bff_node_latency", "milliseconds", "Latency per downstream node");
        _errorCounter = meter.CreateCounter<long>("braess_bff_node_errors_total", "errors", "Errors per downstream node");
    }

    public NodeExecutionScope BeginExecution(string route, string node, EndpointSelection selection)
    {
        var counters = _counters.GetOrAdd((route, node), _ => new RequestCounters());
        counters.IncrementInFlight(selection);
        return new NodeExecutionScope(() => counters.DecrementInFlight(selection));
    }

    public void Record(string route, string node, EndpointSelection selection, TimeSpan duration, bool success)
    {
        var window = _latencies.GetOrAdd((route, node), _ => new SlidingWindow(TimeSpan.FromMinutes(2)));
        window.AddSample(duration, success, selection);

        var counters = _counters.GetOrAdd((route, node), _ => new RequestCounters());
        counters.Record(selection, success);

        _latencyHistogram.Record(duration.TotalMilliseconds, new KeyValuePair<string, object?>("route", route), new KeyValuePair<string, object?>("node", node), new KeyValuePair<string, object?>("selection", selection.ToString()));
        if (!success)
        {
            _errorCounter.Add(1, new KeyValuePair<string, object?>("route", route), new KeyValuePair<string, object?>("node", node), new KeyValuePair<string, object?>("selection", selection.ToString()));
        }
    }

    public NodeStatsSnapshot GetSnapshot(string route, string node)
    {
        var window = _latencies.GetOrAdd((route, node), _ => new SlidingWindow(TimeSpan.FromMinutes(2)));
        var counters = _counters.GetOrAdd((route, node), _ => new RequestCounters());
        var snapshot = window.BuildSnapshot();
        var counts = counters.BuildSnapshot();

        return new NodeStatsSnapshot(
            snapshot.P50,
            snapshot.P95,
            snapshot.P99,
            counts.ErrorRate,
            counts.InFlight,
            counts.DetourShare,
            counts.PrimaryShare,
            counts.AlternateShare);
    }

    public RouteCostSnapshot GetRouteSnapshot(string route, IEnumerable<DownstreamNode> nodes)
    {
        var worst = 0d;
        foreach (var node in nodes)
        {
            var snapshot = GetSnapshot(route, node.Name);
            worst = Math.Max(worst, snapshot.P95);
        }

        return new RouteCostSnapshot(worst, worst);
    }

    private sealed class SlidingWindow
    {
        private readonly TimeSpan _window;
        private readonly LinkedList<Sample> _samples = new();
        private readonly object _sync = new();

        public SlidingWindow(TimeSpan window)
        {
            _window = window;
        }

        public void AddSample(TimeSpan duration, bool success, EndpointSelection selection)
        {
            var now = DateTime.UtcNow;
            lock (_sync)
            {
                _samples.AddLast(new Sample(now, duration.TotalMilliseconds, success, selection));
                Trim(now);
            }
        }

        public SampleSnapshot BuildSnapshot()
        {
            lock (_sync)
            {
                Trim(DateTime.UtcNow);
                if (_samples.Count == 0)
                {
                    return new SampleSnapshot(0, 0, 0, 0, 0, 0, 0);
                }

                var ordered = _samples.Select(s => s.LatencyMs).OrderBy(v => v).ToArray();
                double Percentile(double p)
                {
                    if (ordered.Length == 0)
                    {
                        return 0;
                    }

                    var rank = (int)Math.Ceiling(p / 100.0 * ordered.Length) - 1;
                    rank = Math.Clamp(rank, 0, ordered.Length - 1);
                    return ordered[rank];
                }

                return new SampleSnapshot(
                    Percentile(50),
                    Percentile(95),
                    Percentile(99),
                    _samples.Count,
                    _samples.Count(s => !s.Success),
                    _samples.Count(s => s.Selection == EndpointSelection.Primary),
                    _samples.Count(s => s.Selection == EndpointSelection.Alternate));
            }
        }

        private void Trim(DateTime now)
        {
            while (_samples.First is { } head && now - head.Value.Timestamp > _window)
            {
                _samples.RemoveFirst();
            }
        }

        private sealed record Sample(DateTime Timestamp, double LatencyMs, bool Success, EndpointSelection Selection);
    }

    private sealed class RequestCounters
    {
        private int _primaryInFlight;
        private int _alternateInFlight;
        private long _primarySuccess;
        private long _alternateSuccess;
        private long _primaryErrors;
        private long _alternateErrors;

        public void IncrementInFlight(EndpointSelection selection)
        {
            if (selection == EndpointSelection.Primary)
            {
                Interlocked.Increment(ref _primaryInFlight);
            }
            else
            {
                Interlocked.Increment(ref _alternateInFlight);
            }
        }

        public void DecrementInFlight(EndpointSelection selection)
        {
            if (selection == EndpointSelection.Primary)
            {
                Interlocked.Decrement(ref _primaryInFlight);
            }
            else
            {
                Interlocked.Decrement(ref _alternateInFlight);
            }
        }

        public void Record(EndpointSelection selection, bool success)
        {
            if (selection == EndpointSelection.Primary)
            {
                if (success)
                {
                    Interlocked.Increment(ref _primarySuccess);
                }
                else
                {
                    Interlocked.Increment(ref _primaryErrors);
                }
            }
            else
            {
                if (success)
                {
                    Interlocked.Increment(ref _alternateSuccess);
                }
                else
                {
                    Interlocked.Increment(ref _alternateErrors);
                }
            }
        }

        public RequestCounterSnapshot BuildSnapshot()
        {
            var primarySuccess = Volatile.Read(ref _primarySuccess);
            var alternateSuccess = Volatile.Read(ref _alternateSuccess);
            var primaryErrors = Volatile.Read(ref _primaryErrors);
            var alternateErrors = Volatile.Read(ref _alternateErrors);
            var primaryInFlight = Volatile.Read(ref _primaryInFlight);
            var alternateInFlight = Volatile.Read(ref _alternateInFlight);

            var primaryTotal = primarySuccess + primaryErrors;
            var alternateTotal = alternateSuccess + alternateErrors;
            var total = primaryTotal + alternateTotal;

            var errorRate = total == 0 ? 0 : (double)(primaryErrors + alternateErrors) / total;
            var detourShare = total == 0 ? 0 : (double)alternateTotal / total;

            return new RequestCounterSnapshot(
                primaryInFlight + alternateInFlight,
                errorRate,
                detourShare,
                primaryTotal,
                alternateTotal);
        }
    }

    private sealed record SampleSnapshot(
        double P50,
        double P95,
        double P99,
        int Total,
        int Errors,
        int PrimaryCount,
        int AlternateCount);

    private sealed record RequestCounterSnapshot(
        int InFlight,
        double ErrorRate,
        double DetourShare,
        long PrimaryTotal,
        long AlternateTotal)
    {
        public double PrimaryShare => PrimaryTotal;
        public double AlternateShare => AlternateTotal;
    }
}
