namespace BraessAware.Bff.Planner;

public sealed record PlannedNode(
    DownstreamNode Node,
    EndpointSelection Selection,
    bool IsDegraded,
    double PrimaryP95,
    double AlternateP95)
{
    public Uri SelectedUri => Selection == EndpointSelection.Primary
        ? Node.Primary.Url
        : Node.Alternate?.Url ?? Node.Primary.Url;

    public TimeSpan SelectedTimeout => Selection == EndpointSelection.Primary
        ? Node.Primary.Timeout
        : Node.Alternate?.Timeout ?? Node.Primary.Timeout;
}

public sealed record PlannerPlan(
    string Route,
    IReadOnlyList<PlannedNode> Nodes,
    double CurrentCost,
    double TargetCost)
{
    public IEnumerable<string> DegradedNodes => Nodes.Where(n => n.IsDegraded).Select(n => n.Node.Name);
}
