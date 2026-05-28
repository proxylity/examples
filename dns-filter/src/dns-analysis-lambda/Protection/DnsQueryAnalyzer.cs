namespace DnsFilterLambda;

using System.Collections.Concurrent;

public static class DnsQueryAnalyzer
{
    private static readonly ConcurrentDictionary<string, double> _entropyCache = new();

    public static void UpdateDomainStateStatistics(DomainState state, ICollection<string> subdomains, long nxcount)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(subdomains);

        state.TotalQueries += subdomains.Count;

        var entropies = subdomains.Select(subdomain => ComputeEntropy(subdomain)).ToList();
        state.MaxEntropy = Math.Max(state.MaxEntropy, entropies.Max());
        state.AvgEntropy = (state.AvgEntropy * (state.TotalQueries - subdomains.Count) + entropies.Sum()) / state.TotalQueries;

        var relativeEntropies = subdomains.Select(subdomain => RelativeEntropy.CalculateKLDivergence(subdomain)).ToList();
        state.MaxRelativeEntropy = Math.Max(state.MaxRelativeEntropy, relativeEntropies.Max());
        state.AvgRelativeEntropy = (state.AvgRelativeEntropy * (state.TotalQueries - subdomains.Count) + relativeEntropies.Sum()) / state.TotalQueries;

        state.NxDomainCount += nxcount;

        foreach (var subdomain in subdomains)
        {
            state.UniqueSubdomains.Add(subdomain);

            var length = state.Domain.Length + subdomain.Length + 1;
            state.MaxLength = Math.Max(state.MaxLength, length);
            state.AvgLength = (state.AvgLength * (state.TotalQueries - 1) + length) / state.TotalQueries;

            var parts = subdomain.Split('.');
            state.MaxLabelCount = Math.Max(state.MaxLabelCount, parts.Length);
            state.AvgLabelCount = (state.AvgLabelCount * (state.TotalQueries - 1) + parts.Length) / state.TotalQueries;
        }
    }

    public static bool IsSuspicious(DomainState state, out ICollection<string> reasons)
    {
        // TODO: check for the recency of the domain registration. There should be a configurable threshold
        // for how long a domain can be considered suspicious based on its age. Recent domains with even
        // moderately high entropy or unique subdomains should be considered suspicious.
        reasons = [];
        if (HasHighNxDomainRatio(state)) reasons.Add("High NXDOMAIN ratio");
        if (HasHighUniqueSubdomains(state)) reasons.Add("High unique subdomains");
        if (HasLongLabels(state)) reasons.Add("Has long labels");
        if (HasTooManyLabels(state)) reasons.Add("Has too many labels");
        if (HasHighShannonEntropy(state)) reasons.Add("Has high Shannon entropy");
        if (HasHighRelativeEntropy(state)) reasons.Add("Has high relative entropy");
        // Add a catch-all for overall jankiness
        if (HasHighOverallJankiness(state)) reasons.Add("High overall jankiness");
        return reasons.Count > 0;
    }

    private static bool HasHighOverallJankiness(DomainState state)
    {
        // calculate an overall risk scope based on more lax versions of the metrics even if they
        // don't individually trigger a suspicious flag
        var risk_components = new[] {
            HasHighNxDomainRatio(state, 0.1),
            HasHighUniqueSubdomains(state, 1000),
            HasLongLabels(state, 50, 30),
            HasTooManyLabels(state, 8, 6),
            HasHighShannonEntropy(state, 4.5, 4.0),
            HasHighRelativeEntropy(state, 2.5, 2.0)
        };
        double riskScore = risk_components.Count(c => c) / (double)risk_components.Length;
        return riskScore >= 0.6; // if we hit 50% of the risk score, consider it suspicious
    }

    public static bool HasHighNxDomainRatio(DomainState state, double threshold = 0.25)
    {
        if (state.TotalQueries == 0) return false;
        return (double)state.NxDomainCount / state.TotalQueries >= threshold;
    }

    public static bool HasHighUniqueSubdomains(DomainState state, double threshold = 2000)
    {
        return state.UniqueSubdomains.Estimate() >= threshold;
    }

    public static bool HasLongLabels(DomainState state, int maxMaxLabelLength = 54, int maxAvgLabelLength = 32)
    {
        return state.MaxLength > maxMaxLabelLength || state.AvgLength > maxAvgLabelLength;
    }

    public static bool HasTooManyLabels(DomainState state, int maxMaxLabels = 10, int maxAvgLabels = 8)
    {
        return state.MaxLabelCount > maxMaxLabels || state.AvgLabelCount > maxAvgLabels;
    }

    public static bool HasHighShannonEntropy(DomainState state, double maxMaxEntropy = 4.7, double maxAvgEntropy = 4.2)
    {
        return state.AvgEntropy >= maxAvgEntropy || state.MaxEntropy >= maxMaxEntropy;
    }

    public static bool HasHighRelativeEntropy(DomainState state, double maxMaxRelativeEntropy = 3.0, double maxAvgRelativeEntropy = 2.0)
    {
        return state.AvgRelativeEntropy >= maxAvgRelativeEntropy || state.MaxRelativeEntropy >= maxMaxRelativeEntropy;
    }

    private static double ComputeEntropy(string input)
    {
        var frequencies = new Dictionary<char, int>();
        foreach (var c in input)
        {
            if (!frequencies.TryAdd(c, 1))
                frequencies[c]++;
        }

        double entropy = 0.0;
        int len = input.Length;

        foreach (var freq in frequencies.Values)
        {
            double p = (double)freq / len;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }
}
