namespace DnsFilterLambda;

using System.Collections.Concurrent;

public static class StatelessDnsAnalyzer
{
    private static readonly ConcurrentDictionary<string, double> _entropyCache = new();

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

    public static bool IsSuspicious(DomainState state, out ICollection<string> reasons)
    {
        reasons = [];
        if (HasLongLabel(state.Domain)) reasons.Add("Has long label");
        if (HasTooManyLabels(state.Domain)) reasons.Add("Has too many labels");
        if (HasHighShannonEntropy(state.Domain)) reasons.Add("Has high Shannon entropy");
        return reasons.Count > 0;
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
}
