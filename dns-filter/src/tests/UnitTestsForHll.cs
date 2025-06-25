using DnsFilterLambda;
using Xunit;

namespace test;

public class UnitTestsForHll
{
    const int SUSPICION_THRESHOLD = 2000;

    static string RandomString(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random();
        return new string([.. Enumerable.Range(0, length).Select(i => chars[random.Next(chars.Length)])]);
    }

    [Fact]
    public void EmptyHllDoesntTriggerSuspicionThreshold()
    {
        // Arrange
        var hll = new HyperLogLogEstimator();

        // Assert
        Assert.True(1000 > hll.Estimate());
    }

    [Fact]
    public void SmallHllDoesntTriggerSuspicionThreshold()
    {
        // Arrange
        var hll = new HyperLogLogEstimator();

        // Act
        for (int i = 0; i < 100; i++)
        {
            hll.Add($"test-{RandomString(6)}.example.com");
        }

        // Assert
        Assert.True(1000 > hll.Estimate());
    }

    [Fact]
    public void HllTriggersSuspicionThresholdAtReasonableCount()
    {
        // Arrange
        var hll = new HyperLogLogEstimator();

        // Act
        var count = 0;
        while (SUSPICION_THRESHOLD > hll.Estimate())
        {
            hll.Add($"test-{RandomString(6)}.example.com");
            count++;
        }

        // Assert
        Assert.InRange(count, 990, int.MaxValue); // heuristic.  improvements in the HLL would allow moving this closer to SUSPICION_THRESHOLD
    }
    
    [Fact]
    public void LargeHllDoesTriggerSuspicionThreshold()
    {
        // Arrange
        var hll = new HyperLogLogEstimator();

        // Act
        for (int i = 0; i < SUSPICION_THRESHOLD; i++)
        {
            hll.Add($"test-{RandomString(6)}.example.com");
        }

        // Assert
        Assert.True(SUSPICION_THRESHOLD < hll.Estimate());
    }    
}