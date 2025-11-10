namespace BraessAware.Bff.Planner;

public interface INodeStatsStore
{
    NodeExecutionScope BeginExecution(string route, string node, EndpointSelection selection);
    void Record(string route, string node, EndpointSelection selection, TimeSpan duration, bool success);
    NodeStatsSnapshot GetSnapshot(string route, string node);
    RouteCostSnapshot GetRouteSnapshot(string route, IEnumerable<DownstreamNode> nodes);
}

public sealed record NodeStatsSnapshot(
    double P50,
    double P95,
    double P99,
    double ErrorRate,
    int InFlight,
    double DetourShare,
    double PrimaryShare,
    double AlternateShare)
{
    public double Requests => PrimaryShare + AlternateShare;
}

public sealed record RouteCostSnapshot(double CurrentCost, double TargetCost);

public sealed class NodeExecutionScope : IDisposable
{
    private readonly Action _onDispose;

    public NodeExecutionScope(Action onDispose)
    {
        _onDispose = onDispose;
    }

    public void Dispose() => _onDispose();
}
