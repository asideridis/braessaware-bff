using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using BraessAware.Bff.Planner;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<PlannerOptions>(builder.Configuration.GetSection("BraessPlanner"));
builder.Services.Configure<PlannerPolicyOptions>(builder.Configuration.GetSection("BraessPlanner:Policy"));

builder.Services.AddSingleton<Meter>(_ => new Meter("BraessAware.Bff"));
builder.Services.AddSingleton<PlannerMetrics>();
builder.Services.AddSingleton<INodeStatsStore>(sp => new InMemoryNodeStatsStore(sp.GetRequiredService<PlannerMetrics>().Meter));
builder.Services.AddSingleton<ICallPlannerPolicy, CallPlannerPolicy>();
builder.Services.AddSingleton<PlannerService>();

builder.Services.AddHttpClient("planner")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All
    });

var envFlag = builder.Configuration["BRAESS_ENABLED"];
if (bool.TryParse(envFlag, out var enabledFromEnv))
{
    builder.Services.PostConfigure<PlannerOptions>(options => options.Enabled = enabledFromEnv);
}

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("BraessAware.Bff"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddMeter("BraessAware.Bff")
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.MapPrometheusScrapingEndpoint();

app.MapGet("/api/dashboard", async ([FromServices] PlannerService planner, [FromServices] IHttpClientFactory clientFactory, HttpContext httpContext, CancellationToken cancellationToken) =>
{
    var plan = planner.Plan("dashboard");
    var httpClient = clientFactory.CreateClient("planner");

    var results = new Dictionary<string, object?>();
    var degraded = plan.DegradedNodes.ToArray();

    foreach (var node in plan.Nodes)
    {
        var response = await planner.ExecuteAsync("dashboard", node, async (uri, timeout) =>
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            return await httpClient.GetAsync(uri, timeoutCts.Token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if (node.Node.Mandatory)
            {
                return Results.Problem($"Mandatory node {node.Node.Name} failed with {(int)response.StatusCode}", statusCode: (int)response.StatusCode);
            }

            continue;
        }

        var payload = await response.Content.ReadFromJsonAsync<object?>(cancellationToken: cancellationToken).ConfigureAwait(false);
        results[node.Node.Name] = payload;
    }

    if (degraded.Length > 0)
    {
        httpContext.Response.Headers["x-braess-degraded"] = string.Join(',', degraded);
    }

    var envelope = new
    {
        nodes = results,
        braess = new
        {
            degraded,
            plan.CurrentCost,
            plan.TargetCost
        }
    };

    return Results.Ok(envelope);
})
.WithName("GetDashboard")
.WithOpenApi();

app.Run();

public partial class Program;
