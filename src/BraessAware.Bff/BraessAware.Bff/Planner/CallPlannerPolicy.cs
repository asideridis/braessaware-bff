using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BraessAware.Bff.Planner;

public sealed class CallPlannerPolicy : ICallPlannerPolicy
{
    private readonly PlannerPolicyOptions _options;
    private readonly ILogger<CallPlannerPolicy> _logger;
    private readonly Dictionary<string, HysteresisState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public CallPlannerPolicy(IOptions<PlannerPolicyOptions> options, ILogger<CallPlannerPolicy> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public PlannerPlan Plan(string route, IEnumerable<DownstreamNode> nodes, Func<string, NodeStatsSnapshot> snapshotFactory, RouteCostSnapshot costSnapshot, bool enabled)
    {
        var plannedNodes = new List<PlannedNode>();
        var degradedNodes = new List<string>();
        var adjustedTarget = costSnapshot.TargetCost;

        foreach (var node in nodes)
        {
            var snapshot = snapshotFactory(node.Name);
            var selection = EndpointSelection.Primary;
            var isDegraded = false;

            if (!enabled)
            {
                plannedNodes.Add(new PlannedNode(node, selection, false, snapshot.P95, 0));
                continue;
            }

            var state = GetState(route, node.Name);
            var shouldDetour = ShouldDetour(node, snapshot, state);

            if (shouldDetour && node.Alternate is not null)
            {
                selection = EndpointSelection.Alternate;
                isDegraded = true;
                degradedNodes.Add(node.Name);
                state.LastDetour = DateTime.UtcNow;
                state.IsDetouring = true;
            }
            else if (state.IsDetouring && CanRecover(snapshot, state))
            {
                state.IsDetouring = false;
            }

            plannedNodes.Add(new PlannedNode(node, selection, isDegraded, snapshot.P95, snapshot.P95));
        }

        if (degradedNodes.Count > 0)
        {
            adjustedTarget = Math.Max(costSnapshot.TargetCost * 0.9, costSnapshot.TargetCost - _options.DegradedLatencySlopeThreshold);
        }

        _logger.LogDebug("Planner route {Route} selection {Selection}", route, string.Join(',', plannedNodes.Select(p => $"{p.Node.Name}:{p.Selection}")));
        return new PlannerPlan(route, plannedNodes, costSnapshot.CurrentCost, adjustedTarget);
    }

    private bool ShouldDetour(DownstreamNode node, NodeStatsSnapshot snapshot, HysteresisState state)
    {
        var now = DateTime.UtcNow;
        if (state.IsDetouring)
        {
            if (now - state.LastDetour < _options.HysteresisDuration)
            {
                return true;
            }

            if (snapshot.P95 < _options.RecoveryP95Threshold && snapshot.ErrorRate < _options.ErrorRateThreshold)
            {
                return false;
            }

            return true;
        }

        if (snapshot.P95 > _options.DegradedP95Threshold)
        {
            return node.Alternate is not null && snapshot.DetourShare < Math.Min(node.MaxDetourShare, _options.MaxDetourShare);
        }

        if (snapshot.ErrorRate > _options.ErrorRateThreshold)
        {
            return node.Alternate is not null && snapshot.DetourShare < Math.Min(node.MaxDetourShare, _options.MaxDetourShare);
        }

        if (snapshot.InFlight > 0 && snapshot.P99 - snapshot.P50 > _options.DegradedLatencySlopeThreshold)
        {
            return node.Alternate is not null && snapshot.DetourShare < Math.Min(node.MaxDetourShare, _options.MaxDetourShare);
        }

        return false;
    }

    private bool CanRecover(NodeStatsSnapshot snapshot, HysteresisState state)
    {
        var now = DateTime.UtcNow;
        if (now - state.LastDetour < _options.HysteresisDuration)
        {
            return false;
        }

        return snapshot.P95 < _options.RecoveryP95Threshold && snapshot.ErrorRate < _options.ErrorRateThreshold;
    }

    private HysteresisState GetState(string route, string node)
    {
        lock (_sync)
        {
            var key = $"{route}:{node}";
            if (!_states.TryGetValue(key, out var state))
            {
                state = new HysteresisState();
                _states[key] = state;
            }

            return state;
        }
    }

    private sealed class HysteresisState
    {
        public bool IsDetouring { get; set; }
        public DateTime LastDetour { get; set; } = DateTime.MinValue;
    }
}
