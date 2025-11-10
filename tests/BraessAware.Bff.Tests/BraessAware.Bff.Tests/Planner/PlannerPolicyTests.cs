using BraessAware.Bff.Planner;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace BraessAware.Bff.Tests.Planner;

public class PlannerPolicyTests
{
    [Fact]
    public void UsesAlternateWhenPrimaryP95Degrades()
    {
        var node = new DownstreamNode
        {
            Name = "user",
            Primary = new DownstreamEndpoint { Url = new Uri("http://primary"), Timeout = TimeSpan.FromMilliseconds(100) },
            Alternate = new DownstreamEndpoint { Url = new Uri("http://alternate"), Timeout = TimeSpan.FromMilliseconds(100) },
            Mandatory = true
        };

        var policy = new CallPlannerPolicy(Options.Create(new PlannerPolicyOptions
        {
            DegradedP95Threshold = 200
        }), NullLogger<CallPlannerPolicy>.Instance);

        var plan = policy.Plan("dashboard", new[] { node }, _ => new NodeStatsSnapshot(50, 400, 450, 0, 0, 0, 100, 0), new RouteCostSnapshot(200, 200), enabled: true);

        plan.Nodes.Single().Selection.ShouldBe(EndpointSelection.Alternate);
        plan.Nodes.Single().IsDegraded.ShouldBeTrue();
    }

    [Fact]
    public void RespectsHysteresisBeforeRecovery()
    {
        var node = new DownstreamNode
        {
            Name = "accounts",
            Primary = new DownstreamEndpoint { Url = new Uri("http://primary"), Timeout = TimeSpan.FromMilliseconds(100) },
            Alternate = new DownstreamEndpoint { Url = new Uri("http://alternate"), Timeout = TimeSpan.FromMilliseconds(100) },
            Mandatory = true
        };

        var options = Options.Create(new PlannerPolicyOptions
        {
            DegradedP95Threshold = 200,
            RecoveryP95Threshold = 150,
            HysteresisDuration = TimeSpan.FromSeconds(10)
        });
        var policy = new CallPlannerPolicy(options, NullLogger<CallPlannerPolicy>.Instance);

        // first degrade
        var degradedPlan = policy.Plan("dashboard", new[] { node }, _ => new NodeStatsSnapshot(50, 400, 450, 0, 0, 0, 100, 0), new RouteCostSnapshot(200, 200), enabled: true);
        degradedPlan.Nodes.Single().Selection.ShouldBe(EndpointSelection.Alternate);

        // soon after degrade metrics improve but hysteresis prevents immediate recovery
        var recoveryAttempt = policy.Plan("dashboard", new[] { node }, _ => new NodeStatsSnapshot(50, 140, 150, 0, 0, 0, 100, 0), new RouteCostSnapshot(200, 200), enabled: true);
        recoveryAttempt.Nodes.Single().Selection.ShouldBe(EndpointSelection.Alternate);
    }

    [Fact]
    public void PrimaryAlwaysSelectedWhenPlannerDisabled()
    {
        var node = new DownstreamNode
        {
            Name = "recommendations",
            Primary = new DownstreamEndpoint { Url = new Uri("http://primary"), Timeout = TimeSpan.FromMilliseconds(100) },
            Alternate = new DownstreamEndpoint { Url = new Uri("http://alternate"), Timeout = TimeSpan.FromMilliseconds(100) },
            Mandatory = false
        };

        var policy = new CallPlannerPolicy(Options.Create(new PlannerPolicyOptions()), NullLogger<CallPlannerPolicy>.Instance);
        var plan = policy.Plan("dashboard", new[] { node }, _ => new NodeStatsSnapshot(50, 500, 600, 1, 0, 0.2, 80, 20), new RouteCostSnapshot(200, 200), enabled: false);
        plan.Nodes.Single().Selection.ShouldBe(EndpointSelection.Primary);
    }
}
