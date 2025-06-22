namespace DnsFilterLambda;

public class StatefulDnsAnalyzer(
    IDomainStateStore stateStore,
    double entropyThreshold = 4.2,
    double nxDomainThreshold = 0.25,
    double uniqueSubdomainThreshold = 100)
{
    private readonly IDomainStateStore _stateStore = stateStore;
    private readonly double _entropyThreshold = entropyThreshold;
    private readonly double _nxDomainThreshold = nxDomainThreshold;
    private readonly double _uniqueSubdomainThreshold = uniqueSubdomainThreshold;

    public bool IsSuspicious(DomainState state, out ICollection<string> reasons)
    {
        var (_, subdomain) = DomainUtils.GetDomainParts(state.Domain);
        double entropy = !string.IsNullOrEmpty(subdomain)
            ? CalculateShannonEntropy(subdomain)
            : 0.0;

        double uniqueEstimate = state.UniqueSubdomains.Estimate();
        double nxDomainRatio = state.TotalQueries > 0
            ? (double)state.NxDomainCount / state.TotalQueries
            : 0.0;

        bool highEntropy = entropy >= _entropyThreshold && subdomain?.Length >= 10;
        bool highNxDomain = nxDomainRatio >= _nxDomainThreshold;
        bool highUnique = uniqueEstimate >= _uniqueSubdomainThreshold;

        reasons = [];
        if (highEntropy)
            reasons.Add($"High entropy: {entropy:F2} (threshold: {_entropyThreshold})");
        if (highNxDomain)
            reasons.Add($"High NXDOMAIN ratio: {nxDomainRatio:P2} (threshold: {_nxDomainThreshold:P2})");
        if (highUnique)
            reasons.Add($"High unique subdomains: {uniqueEstimate:F0} (threshold: {_uniqueSubdomainThreshold})");

        return reasons.Count > 0;
    }

    private static double CalculateShannonEntropy(string input)
    {
        if (string.IsNullOrEmpty(input))
            return 0.0;

        var frequencies = input.GroupBy(c => c).Select(g => (double)g.Count() / input.Length);
        return -frequencies.Sum(p => p * Math.Log2(p));
    }
}

