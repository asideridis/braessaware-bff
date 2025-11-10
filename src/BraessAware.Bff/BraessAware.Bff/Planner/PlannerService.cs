using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BraessAware.Bff.Planner;

public sealed class PlannerService
{
    private readonly INodeStatsStore _statsStore;
    private readonly ICallPlannerPolicy _policy;
    private readonly ILogger<PlannerService> _logger;
    private readonly PlannerOptions _options;
    private readonly bool _globalEnabled;
    private readonly PlannerMetrics _metrics;

    public PlannerService(
        INodeStatsStore statsStore,
        ICallPlannerPolicy policy,
        IOptions<PlannerOptions> options,
        PlannerMetrics metrics,
        ILogger<PlannerService> logger)
    {
        _statsStore = statsStore;
        _policy = policy;
        _logger = logger;
        _options = options.Value;
        _globalEnabled = _options.Enabled;
        _metrics = metrics;
    }

    public PlannerPlan Plan(string routeKey)
    {
        if (!_options.Routes.TryGetValue(routeKey, out var route))
        {
            throw new InvalidOperationException($"Unknown route {routeKey}");
        }

        var nodes = route.Nodes.Select(n => n.Value).ToArray();
        var costSnapshot = _statsStore.GetRouteSnapshot(routeKey, nodes);
        var enabled = _globalEnabled && route.Enabled;
        var plan = _policy.Plan(routeKey, nodes, node => _statsStore.GetSnapshot(routeKey, node), costSnapshot, enabled);
        _metrics.RecordPlan(routeKey, plan);
        return plan;
    }

    public async Task<HttpResponseMessage> ExecuteAsync(string routeKey, PlannedNode plannedNode, Func<Uri, TimeSpan, Task<HttpResponseMessage>> action, CancellationToken cancellationToken)
    {
        using var scope = _statsStore.BeginExecution(routeKey, plannedNode.Node.Name, plannedNode.Selection);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await action(plannedNode.SelectedUri, plannedNode.SelectedTimeout).ConfigureAwait(false);
        }
        catch
        {
            stopwatch.Stop();
            _statsStore.Record(routeKey, plannedNode.Node.Name, plannedNode.Selection, stopwatch.Elapsed, false);
            _metrics.RecordDetour(plannedNode, false);
            throw;
        }

        stopwatch.Stop();
        var success = response.IsSuccessStatusCode;
        _statsStore.Record(routeKey, plannedNode.Node.Name, plannedNode.Selection, stopwatch.Elapsed, success);
        _metrics.RecordDetour(plannedNode, success);
        return response;
    }
}

public sealed class PlannerOptions
{
    public bool Enabled { get; set; } = true;
    public Dictionary<string, RoutePlanOptions> Routes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RoutePlanOptions
{
    public bool Enabled { get; set; } = true;
    public Dictionary<string, DownstreamNode> Nodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
