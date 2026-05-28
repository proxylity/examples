namespace DnsFilterLambda;

using System;
using System.Collections.Generic;
using System.Linq;

public class RelativeEntropy
{
    /// <summary>
    /// Calculates the Kullback-Leibler divergence (KL divergence) between the character distribution of a domain name
    /// and the expected distribution of characters in English.
    /// </summary>
    /// <param name="domainName">The domain name to analyze.</param>
    /// <returns>The KL divergence value.</returns>

    // English letter frequencies (approximate) - adjust as needed
    private static readonly Dictionary<char, double> EnglishLetterFrequencies = new()
    {
        {'a', 0.082}, {'b', 0.015}, {'c', 0.028}, {'d', 0.043}, {'e', 0.127},
        {'f', 0.022}, {'g', 0.020}, {'h', 0.061}, {'i', 0.070}, {'j', 0.0015},
        {'k', 0.0077}, {'l', 0.040}, {'m', 0.024}, {'n', 0.067}, {'o', 0.075},
        {'p', 0.019}, {'q', 0.00095}, {'r', 0.060}, {'s', 0.063}, {'t', 0.091},
        {'u', 0.028}, {'v', 0.0098}, {'w', 0.024}, {'x', 0.0015}, {'y', 0.020},
        {'z', 0.00074}
    };

    public static double CalculateKLDivergence(string label)
    {
        // 1. Calculate the probability distribution of the domain name (P)
        Dictionary<char, double> domainFrequencies = CalculateDomainFrequencies(label);

        // 2. Initialize KL divergence
        double klDivergence = 0.0;

        // 3. Iterate over the characters in the domain name
        foreach (var entry in domainFrequencies)
        {
            char character = entry.Key;
            double p_x = entry.Value; // Probability of the character in the domain

            // Get the probability of the character in English (Q)
            if (EnglishLetterFrequencies.TryGetValue(character, out double q_x))
            {
                // Handle cases where q_x is zero or very small to avoid log(0) issues
                if (q_x < Double.Epsilon) // Use a small epsilon to check for near-zero values
                {
                    // If the character is present in the domain but extremely rare or absent in English,
                    // this suggests high divergence.  Assign a large penalty.
                    // A common approach is to use a small smoothing constant or a large finite value.
                    // For this example, we'll use a large constant. In a real system, you might
                    // consider smoothing or Laplace correction for more robust handling.
                    klDivergence += p_x * Math.Log2(p_x / Double.Epsilon); // Or a fixed large value
                }
                else
                {
                    klDivergence += p_x * Math.Log2(p_x / q_x);
                }
            }
            else
            {
                // If a character in the domain is not in our English frequency list,
                // it's highly divergent, so assign a large penalty.
                // Again, in a real-world scenario, you might have a broader character set
                // or a different way to handle unknown characters.
                klDivergence += p_x * Math.Log2(p_x / Double.Epsilon); // Large penalty for unknown char
            }
        }

        return klDivergence;
    }

    private static Dictionary<char, double> CalculateDomainFrequencies(string label)
    {
        Dictionary<char, int> counts = [];
        foreach (char c in label.ToLower()) // Convert to lowercase for consistent comparison
        {
            if (char.IsLetter(c)) // Only consider letters
            {
                if (counts.TryGetValue(c, out int value))
                {
                    counts[c] = ++value;
                }
                else
                {
                    counts[c] = 1;
                }
            }
        }

        Dictionary<char, double> probabilities = [];
        int totalLetters = counts.Values.Sum();
        foreach (var entry in counts)
        {
            probabilities[entry.Key] = (double)entry.Value / totalLetters;
        }
        return probabilities;
    }

    public static void Examples()
    {
        string randomDomain = "kxulsrwcq";
        string legitimateDomain = "google";
        string anotherRandomDomain = "zxcyxqwerrty";
        string commonDomain = "exampledomain";

        Console.WriteLine($"KL Divergence for '{randomDomain}': {CalculateKLDivergence(randomDomain):F3}");
        Console.WriteLine($"KL Divergence for '{legitimateDomain}': {CalculateKLDivergence(legitimateDomain):F3}");
        Console.WriteLine($"KL Divergence for '{anotherRandomDomain}': {CalculateKLDivergence(anotherRandomDomain):F3}");
        Console.WriteLine($"KL Divergence for '{commonDomain}': {CalculateKLDivergence(commonDomain):F3}");

        // Example threshold (adjust based on your testing)
        double threshold = 2.0; 
        Console.WriteLine($"\nUsing threshold of {threshold}:");
        Console.WriteLine($"'{randomDomain}' is {(CalculateKLDivergence(randomDomain) > threshold ? "random" : "legitimate")}");
        Console.WriteLine($"'{legitimateDomain}' is {(CalculateKLDivergence(legitimateDomain) > threshold ? "random" : "legitimate")}");
        Console.WriteLine($"'{anotherRandomDomain}' is {(CalculateKLDivergence(anotherRandomDomain) > threshold ? "random" : "legitimate")}");
        Console.WriteLine($"'{commonDomain}' is {(CalculateKLDivergence(commonDomain) > threshold ? "random" : "legitimate")}");
    }
}