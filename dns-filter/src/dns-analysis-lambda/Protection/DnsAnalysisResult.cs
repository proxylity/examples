namespace DnsFilterLambda;

public class DnsAnalysisResult
{
    public string Domain { get; set; } = string.Empty;
    public long TotalQueries { get; set; }
    public long NxDomainCount { get; set; }
    public double EstimatedUniqueSubdomains { get; set; }
    public double LastEntropy { get; set; }
    public bool IsSuspicious { get; set; }
    public List<string> SuspicionReasons { get; set; } = [];
}
