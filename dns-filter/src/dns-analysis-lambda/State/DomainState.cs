namespace DnsFilterLambda;

public class DomainState
{
    public string Domain { get; set; } = string.Empty;
    public long TotalQueries { get; set; } = 0;
    public long NxDomainCount { get; set; } = 0;
    public HyperLogLogEstimator UniqueSubdomains { get; set; } = new();
    public bool IsSuspicious { get; set; } = false;
    public long Version { get; set; } = 0;
    public DateTimeOffset Expires { get; set; } = DateTimeOffset.UtcNow.AddDays(7); // Default to 7 days if no expiry set
}