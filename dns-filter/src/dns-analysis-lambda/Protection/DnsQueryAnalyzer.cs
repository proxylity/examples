namespace DnsFilterLambda;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

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
        reasons = [];
        if (HasHighNxDomainRatio(state)) reasons.Add("High NXDOMAIN ratio");
        if (HasHighUniqueSubdomains(state)) reasons.Add("High unique subdomains");
        if (HasLongLabels(state)) reasons.Add("Has long labels");
        if (HasTooManyLabels(state)) reasons.Add("Has too many labels");
        if (HasHighShannonEntropy(state)) reasons.Add("Has high Shannon entropy");
        if (HasHighOverallJankiness(state)) reasons.Add("High overall jankiness");
        return reasons.Count > 0;
    }

    private static bool HasHighOverallJankiness(DomainState state)
    {
        // calculate an overall risk scope based on the various metrics even if they
        // don't individually trigger a suspicious flag
        double riskScore = 0.0;
        if (HasHighNxDomainRatio(state, 0.1)) riskScore += 0.2; // moderate threshold
        if (HasHighUniqueSubdomains(state, 1000)) riskScore += 0.2; // moderate threshold
        if (HasLongLabels(state, 60, 40)) riskScore += 0.2; // moderate threshold
        if (HasTooManyLabels(state, 12, 10)) riskScore += 0.2; // moderate threshold
        if (HasHighShannonEntropy(state, 5.0, 4.5)) riskScore += 0.2; // moderate threshold
        return riskScore >= 0.6; // if we hit 60% of the risk score, consider it suspicious
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
