using System.Diagnostics.Metrics;
using BraessAware.Bff.Planner;
using Shouldly;

namespace BraessAware.Bff.Tests.Planner;

public class InMemoryNodeStatsStoreTests
{
    [Fact]
    public void RecordsLatencyAndDetourShare()
    {
        var meter = new Meter("test");
        var store = new InMemoryNodeStatsStore(meter);

        using (store.BeginExecution("dashboard", "user", EndpointSelection.Primary))
        {
        }

        store.Record("dashboard", "user", EndpointSelection.Primary, TimeSpan.FromMilliseconds(100), true);
        store.Record("dashboard", "user", EndpointSelection.Alternate, TimeSpan.FromMilliseconds(200), false);

        var snapshot = store.GetSnapshot("dashboard", "user");
        snapshot.P95.ShouldBeGreaterThanOrEqualTo(100);
        snapshot.DetourShare.ShouldBeGreaterThan(0);
        snapshot.ErrorRate.ShouldBeGreaterThan(0);
    }
}
