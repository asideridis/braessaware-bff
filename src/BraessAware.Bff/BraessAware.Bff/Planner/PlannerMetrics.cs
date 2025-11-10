using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace BraessAware.Bff.Planner;

public sealed class PlannerMetrics
{
    private readonly Counter<long> _plans;
    private readonly Counter<long> _detours;
    private readonly Meter _meter;
    private readonly ConcurrentDictionary<string, (double current, double target)> _routeCosts = new();

    public PlannerMetrics(Meter meter)
    {
        _meter = meter;
        _plans = meter.CreateCounter<long>("braess_bff_plans_total", unit: "plans", description: "Total plans evaluated");
        _detours = meter.CreateCounter<long>("braess_bff_detours_applied_total", unit: "requests", description: "Detours applied");
        meter.CreateObservableGauge("braess_bff_endpoint_cost_current", ObserveCurrentCost, unit: "milliseconds", description: "Current endpoint cost");
        meter.CreateObservableGauge("braess_bff_endpoint_cost_target", ObserveTargetCost, unit: "milliseconds", description: "Target endpoint cost");
    }

    public void RecordPlan(string route, PlannerPlan plan)
    {
        _plans.Add(1, KeyValuePair.Create<string, object?>("route", route));
        _routeCosts.AddOrUpdate(route, _ => (plan.CurrentCost, plan.TargetCost), (_, _) => (plan.CurrentCost, plan.TargetCost));
    }

    public void RecordDetour(PlannedNode node, bool success)
    {
        if (node.Selection == EndpointSelection.Alternate)
        {
            _detours.Add(1, KeyValuePair.Create<string, object?>("node", node.Node.Name), KeyValuePair.Create<string, object?>("success", success));
        }
    }

    public Meter Meter => _meter;

    private IEnumerable<Measurement<double>> ObserveCurrentCost()
    {
        foreach (var (route, value) in _routeCosts)
        {
            yield return new Measurement<double>(value.current, new KeyValuePair<string, object?>("route", route));
        }
    }

    private IEnumerable<Measurement<double>> ObserveTargetCost()
    {
        foreach (var (route, value) in _routeCosts)
        {
            yield return new Measurement<double>(value.target, new KeyValuePair<string, object?>("route", route));
        }
    }
}
