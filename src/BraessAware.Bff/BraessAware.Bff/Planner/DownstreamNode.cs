namespace BraessAware.Bff.Planner;

public sealed class DownstreamEndpoint
{
    public Uri Url { get; set; } = new("http://localhost");
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);
}

public sealed class DownstreamNode
{
    public string Name { get; set; } = string.Empty;
    public DownstreamEndpoint Primary { get; set; } = new();
    public DownstreamEndpoint? Alternate { get; set; }
    public bool Mandatory { get; set; }
    public double MaxDetourShare { get; set; } = 0.2;

    public string MetricsKey => Name.ToLowerInvariant();
}

public enum EndpointSelection
{
    Primary,
    Alternate
}
