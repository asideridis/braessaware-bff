namespace BraessAware.Bff.Planner;

public interface ICallPlannerPolicy
{
    PlannerPlan Plan(string route, IEnumerable<DownstreamNode> nodes, Func<string, NodeStatsSnapshot> snapshotFactory, RouteCostSnapshot costSnapshot, bool enabled);
}

public sealed class PlannerPolicyOptions
{
    public double DegradedLatencySlopeThreshold { get; set; } = 50;
    public double DegradedP95Threshold { get; set; } = 450;
    public double RecoveryP95Threshold { get; set; } = 300;
    public double ErrorRateThreshold { get; set; } = 0.2;
    public double MaxDetourShare { get; set; } = 0.2;
    public TimeSpan HysteresisDuration { get; set; } = TimeSpan.FromSeconds(30);
}
