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
        if (HasLongLabel(state.Domain)) reasons.Add("Has long label");
        if (HasTooManyLabels(state.Domain)) reasons.Add("Has too many labels");
        if (HasHighShannonEntropy(state.Domain)) reasons.Add("Has high Shannon entropy");
        return reasons.Count > 0;
    }

    public static bool HasHighNxDomainRatio(DomainState state, double threshold = 0.25)
    {
        if (state.TotalQueries == 0) return false;
        return (double)state.NxDomainCount / state.TotalQueries >= threshold;
    }

    public static bool HasHighUniqueSubdomains(DomainState state, double threshold = 1000)
    {
        return state.UniqueSubdomains.Estimate() >= threshold;
    }

    public static bool HasLongLabel(string domain, int maxLabelLength = 54)
    {
        return domain.Split('.').Any(label => label.Length > maxLabelLength);
    }

    public static bool HasTooManyLabels(string domain, int maxLabels = 10)
    {
        return domain.Split('.').Length > maxLabels;
    }

    public static bool HasHighShannonEntropy(string domain, double maxEntropy = 4.2)
    {
        if (string.IsNullOrEmpty(domain)) return false;

        return _entropyCache.GetOrAdd(domain, ComputeEntropy) > maxEntropy;
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
