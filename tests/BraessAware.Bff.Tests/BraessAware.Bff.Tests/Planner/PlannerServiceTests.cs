using BraessAware.Bff.Planner;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace BraessAware.Bff.Tests.Planner;

public class PlannerServiceTests
{
    [Fact]
    public void HonorsPlannerEnableFlag()
    {
        var store = new FakeStore();
        var policy = new TestPolicy();
        var options = Options.Create(new PlannerOptions
        {
            Enabled = false,
            Routes =
            {
                ["dashboard"] = new RoutePlanOptions
                {
                    Nodes =
                    {
                        ["user"] = new DownstreamNode
                        {
                            Name = "user",
                            Mandatory = true,
                            Primary = new DownstreamEndpoint { Url = new Uri("http://primary"), Timeout = TimeSpan.FromMilliseconds(100) },
                            Alternate = new DownstreamEndpoint { Url = new Uri("http://alternate"), Timeout = TimeSpan.FromMilliseconds(100) }
                        }
                    }
                }
            }
        });

        var service = new PlannerService(store, policy, options, new PlannerMetrics(new System.Diagnostics.Metrics.Meter("test")), NullLogger<PlannerService>.Instance);
        var plan = service.Plan("dashboard");
        policy.LastEnabled.ShouldBeFalse();
        plan.Route.ShouldBe("dashboard");
    }

    private sealed class FakeStore : INodeStatsStore
    {
        public NodeExecutionScope BeginExecution(string route, string node, EndpointSelection selection) => new(() => { });
        public NodeStatsSnapshot GetSnapshot(string route, string node) => new(50, 60, 70, 0, 0, 0, 1, 0);
        public RouteCostSnapshot GetRouteSnapshot(string route, IEnumerable<DownstreamNode> nodes) => new(10, 10);
        public void Record(string route, string node, EndpointSelection selection, TimeSpan duration, bool success) { }
    }

    private sealed class TestPolicy : ICallPlannerPolicy
    {
        public bool LastEnabled { get; private set; }

        public PlannerPlan Plan(string route, IEnumerable<DownstreamNode> nodes, Func<string, NodeStatsSnapshot> snapshotFactory, RouteCostSnapshot costSnapshot, bool enabled)
        {
            LastEnabled = enabled;
            return new PlannerPlan(route, nodes.Select(n => new PlannedNode(n, EndpointSelection.Primary, false, 0, 0)).ToArray(), costSnapshot.CurrentCost, costSnapshot.TargetCost);
        }
    }
}
