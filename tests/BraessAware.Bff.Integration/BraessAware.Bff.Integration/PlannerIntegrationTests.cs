using System.Net.Http.Json;
using System.Net.Sockets;
using BraessAware.Bff.Planner;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BraessAware.Bff.Integration;

public class PlannerIntegrationTests : IAsyncLifetime
{
    private readonly TestcontainersContainer _user;
    private readonly TestcontainersContainer _accounts;
    private readonly TestcontainersContainer _recs;
    private readonly List<TestcontainersContainer> _containers;

    private bool _dockerUnavailable;

    public PlannerIntegrationTests()
    {
        _user = BuildWiremockContainer();
        _accounts = BuildWiremockContainer();
        _recs = BuildWiremockContainer();
        _containers = new List<TestcontainersContainer> { _user, _accounts, _recs };
    }

    [SkippableFact]
    public async Task MandatoryNodeFailurePropagates()
    {
        Skip.If(_dockerUnavailable, "Docker is unavailable");
        await ConfigureDefaultsAsync();
        await ConfigureStubAsync(_accounts, "/accounts-primary", 100, 503);
        await ConfigureStubAsync(_accounts, "/accounts-alternate", 100, 503);

        await using var app = CreateFactory();
        var client = app.CreateClient();
        var response = await client.GetAsync("/api/dashboard");
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.ServiceUnavailable);
    }

    [SkippableFact]
    public async Task PlannerReducesP95Latency()
    {
        Skip.If(_dockerUnavailable, "Docker is unavailable");
        await ConfigureDefaultsAsync(primaryDelay: 400, alternateDelay: 60);

        // Planner off baseline
        await using var plannerOff = CreateFactory(enabled: false);
        var offTimings = await CollectLatenciesAsync(plannerOff.CreateClient(), 10);
        var baselineP95 = Percentile(offTimings, 95);

        // Planner on - allow warmup requests to capture degraded stats
        await using var plannerOn = CreateFactory(enabled: true);
        var client = plannerOn.CreateClient();
        await CollectLatenciesAsync(client, 3); // warm up to gather stats
        var improvedTimings = await CollectLatenciesAsync(client, 10);
        var improvedP95 = Percentile(improvedTimings, 95);

        improvedP95.ShouldBeLessThan(baselineP95);
    }

    public async Task InitializeAsync()
    {
        try
        {
            foreach (var container in _containers)
            {
                await container.StartAsync();
            }
        }
        catch (Exception ex) when (IsDockerUnavailable(ex))
        {
            Console.WriteLine($"Skipping Testcontainers integration tests: {ex.Message}");
            _dockerUnavailable = true;
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var container in _containers)
        {
            await container.DisposeAsync();
        }
    }

    private static bool IsDockerUnavailable(Exception ex)
    {
        return ex is HttpRequestException || ex is InvalidOperationException || ex.InnerException is SocketException;
    }

    private static TestcontainersContainer BuildWiremockContainer() => new TestcontainersBuilder<TestcontainersContainer>()
        .WithImage("wiremock/wiremock:3.3.1")
        .WithPortBinding(8080, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(8080))
        .Build();

    private async Task ConfigureDefaultsAsync(int primaryDelay = 200, int alternateDelay = 80)
    {
        await ResetAsync(_user);
        await ResetAsync(_accounts);
        await ResetAsync(_recs);

        await ConfigureStubAsync(_user, "/user-primary", primaryDelay);
        await ConfigureStubAsync(_user, "/user-alternate", alternateDelay);

        await ConfigureStubAsync(_accounts, "/accounts-primary", primaryDelay);
        await ConfigureStubAsync(_accounts, "/accounts-alternate", alternateDelay);

        await ConfigureStubAsync(_recs, "/recommendations-primary", primaryDelay);
        await ConfigureStubAsync(_recs, "/recommendations-alternate", alternateDelay);
    }

    private async Task ResetAsync(TestcontainersContainer container)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{container.GetMappedPublicPort(8080)}") };
        await client.DeleteAsync("/__admin/mappings");
    }

    private async Task ConfigureStubAsync(TestcontainersContainer container, string path, int delay, int status = 200)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{container.GetMappedPublicPort(8080)}") };
        var mapping = new
        {
            request = new { method = "GET", url = path },
            response = new
            {
                status,
                fixedDelayMilliseconds = delay,
                jsonBody = new { path, delay }
            }
        };
        await client.PostAsJsonAsync("/__admin/mappings", mapping);
    }

    private BraessAwareApplicationFactory CreateFactory(bool enabled = true)
    {
        return new BraessAwareApplicationFactory(this, enabled);
    }

    private async Task<List<double>> CollectLatenciesAsync(HttpClient client, int count)
    {
        var timings = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var response = await client.GetAsync("/api/dashboard");
            response.EnsureSuccessStatusCode();
            watch.Stop();
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }

        return timings;
    }

    private static double Percentile(List<double> samples, int percentile)
    {
        var ordered = samples.OrderBy(x => x).ToArray();
        var rank = (int)Math.Ceiling(percentile / 100.0 * ordered.Length) - 1;
        rank = Math.Clamp(rank, 0, ordered.Length - 1);
        return ordered[rank];
    }

    private sealed class BraessAwareApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly PlannerIntegrationTests _parent;
        private readonly bool _enabled;

        public BraessAwareApplicationFactory(PlannerIntegrationTests parent, bool enabled)
        {
            _parent = parent;
            _enabled = enabled;
        }

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((context, config) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["BRAESS_ENABLED"] = _enabled.ToString(),
                    ["BraessPlanner:Enabled"] = _enabled.ToString(),
                    ["BraessPlanner:Routes:dashboard:Enabled"] = "true",
                    ["BraessPlanner:Routes:dashboard:Nodes:user:Name"] = "user",
                    ["BraessPlanner:Routes:dashboard:Nodes:user:Mandatory"] = "true",
                    ["BraessPlanner:Routes:dashboard:Nodes:user:Primary:Url"] = $"http://localhost:{_parent._user.GetMappedPublicPort(8080)}/user-primary",
                    ["BraessPlanner:Routes:dashboard:Nodes:user:Primary:Timeout"] = "00:00:02",
                    ["BraessPlanner:Routes:dashboard:Nodes:user:Alternate:Url"] = $"http://localhost:{_parent._user.GetMappedPublicPort(8080)}/user-alternate",
                    ["BraessPlanner:Routes:dashboard:Nodes:user:Alternate:Timeout"] = "00:00:02",
                    ["BraessPlanner:Routes:dashboard:Nodes:accounts:Name"] = "accounts",
                    ["BraessPlanner:Routes:dashboard:Nodes:accounts:Mandatory"] = "true",
                    ["BraessPlanner:Routes:dashboard:Nodes:accounts:Primary:Url"] = $"http://localhost:{_parent._accounts.GetMappedPublicPort(8080)}/accounts-primary",
                    ["BraessPlanner:Routes:dashboard:Nodes:accounts:Primary:Timeout"] = "00:00:02",
                    ["BraessPlanner:Routes:dashboard:Nodes:accounts:Alternate:Url"] = $"http://localhost:{_parent._accounts.GetMappedPublicPort(8080)}/accounts-alternate",
                    ["BraessPlanner:Routes:dashboard:Nodes:accounts:Alternate:Timeout"] = "00:00:02",
                    ["BraessPlanner:Routes:dashboard:Nodes:recommendations:Name"] = "recommendations",
                    ["BraessPlanner:Routes:dashboard:Nodes:recommendations:Mandatory"] = "false",
                    ["BraessPlanner:Routes:dashboard:Nodes:recommendations:Primary:Url"] = $"http://localhost:{_parent._recs.GetMappedPublicPort(8080)}/recommendations-primary",
                    ["BraessPlanner:Routes:dashboard:Nodes:recommendations:Primary:Timeout"] = "00:00:02",
                    ["BraessPlanner:Routes:dashboard:Nodes:recommendations:Alternate:Url"] = $"http://localhost:{_parent._recs.GetMappedPublicPort(8080)}/recommendations-alternate",
                    ["BraessPlanner:Routes:dashboard:Nodes:recommendations:Alternate:Timeout"] = "00:00:02"
                };

                config.AddInMemoryCollection(settings!);
            });

            builder.ConfigureServices(services =>
            {
                services.PostConfigure<PlannerPolicyOptions>(options =>
                {
                    options.DegradedP95Threshold = 200;
                    options.RecoveryP95Threshold = 120;
                    options.DegradedLatencySlopeThreshold = 10;
                });
            });
        }
    }
}
