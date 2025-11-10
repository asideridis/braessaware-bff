using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

var settings = DownstreamSettings.FromConfiguration(builder.Configuration, "accounts");
var state = new DownstreamState(settings);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.MapGet("/accounts", async (CancellationToken cancellationToken) =>
{
    var result = await state.ExecuteAsync(cancellationToken);
    if (!result.Success)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Json(result.Payload);
});

app.MapPost("/control/degrade/{mode}", (string mode) =>
{
    state.SetDegraded(string.Equals(mode, "on", StringComparison.OrdinalIgnoreCase));
    return Results.Ok(new { degraded = state.IsDegraded });
});

app.MapPost("/control/error/{percent}", (int percent) =>
{
    state.SetErrorRate(percent);
    return Results.Ok(new { state.ErrorRate });
});

app.Run();

internal sealed record DownstreamSettings(
    string ServiceName,
    string Role,
    int PrimaryDelayMs,
    int AlternateDelayMs,
    int DegradedPenaltyMs,
    int JitterMs,
    double BaseErrorRate)
{
    public static DownstreamSettings FromConfiguration(IConfiguration configuration, string serviceName)
    {
        var role = configuration["SERVICE_ROLE"] ?? "primary";
        var primaryDelay = configuration.GetValue("PRIMARY_DELAY_MS", 80);
        var alternateDelay = configuration.GetValue("ALTERNATE_DELAY_MS", 40);
        var degradedPenalty = configuration.GetValue("DEGRADED_PENALTY_MS", 450);
        var jitter = configuration.GetValue("JITTER_MS", 30);
        var errorRate = configuration.GetValue("ERROR_RATE", 0.0);
        return new DownstreamSettings(serviceName, role, primaryDelay, alternateDelay, degradedPenalty, jitter, errorRate);
    }

    public int GetBaseDelay() => Role.Equals("alternate", StringComparison.OrdinalIgnoreCase) ? AlternateDelayMs : PrimaryDelayMs;
}

internal sealed class DownstreamState
{
    private readonly DownstreamSettings _settings;
    private readonly object _sync = new();
    private readonly Random _random = new();
    private double _errorRate;
    public bool IsDegraded { get; private set; }

    public DownstreamState(DownstreamSettings settings)
    {
        _settings = settings;
        _errorRate = settings.BaseErrorRate;
    }

    public double ErrorRate => _errorRate;

    public void SetDegraded(bool degraded)
    {
        lock (_sync)
        {
            IsDegraded = degraded;
        }
    }

    public void SetErrorRate(int percent)
    {
        lock (_sync)
        {
            _errorRate = Math.Clamp(percent / 100.0, 0, 1);
        }
    }

    public async Task<(bool Success, object Payload)> ExecuteAsync(CancellationToken cancellationToken)
    {
        int delay;
        double errorRate;
        bool degraded;
        lock (_sync)
        {
            degraded = IsDegraded;
            errorRate = _errorRate;
            delay = _settings.GetBaseDelay() + _random.Next(_settings.JitterMs);
            if (degraded)
            {
                delay += _settings.DegradedPenaltyMs;
            }
        }

        if (delay > 0)
        {
            await Task.Delay(delay, cancellationToken);
        }

        var fail = _random.NextDouble() < errorRate;
        var payload = new
        {
            service = _settings.ServiceName,
            role = _settings.Role,
            degraded,
            delayMs = delay,
            timestamp = DateTimeOffset.UtcNow
        };
        return (!fail, payload);
    }
}
