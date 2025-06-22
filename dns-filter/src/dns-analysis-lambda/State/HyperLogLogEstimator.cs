using System.Security.Cryptography;

namespace DnsFilterLambda;

public class HyperLogLogEstimator
{
    private const int BucketCount = 1024;
    private readonly byte[] _buckets = new byte[BucketCount];

    public void Add(string item)
    {
        var hash = BitConverter.ToUInt32(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item)), 0);
        int bucketIndex = (int)(hash % BucketCount);
        int leadingZeros = CountLeadingZeros((hash >> 10) | 1); // ensure non-zero

        _buckets[bucketIndex] = Math.Max(_buckets[bucketIndex], (byte)leadingZeros);
    }

    public double Estimate()
    {
        double harmonicMean = 0.0;

        foreach (var bucket in _buckets)
            harmonicMean += 1.0 / (1 << bucket);

        harmonicMean = 1.0 / harmonicMean;

        double alpha = 0.7213 / (1 + 1.079 / BucketCount);
        double rawEstimate = alpha * BucketCount * BucketCount * harmonicMean;

        return rawEstimate;
    }

    public void Merge(HyperLogLogEstimator other)
    {
        if (other._buckets.Length != _buckets.Length)
            throw new InvalidOperationException("Cannot merge HLL with different bucket sizes.");

        for (int i = 0; i < _buckets.Length; i++)
            _buckets[i] = Math.Max(_buckets[i], other._buckets[i]);
    }

    public byte[] Serialize() => _buckets;

    public void Deserialize(byte[] buckets)
    {
        if (buckets.Length != BucketCount) throw new ArgumentException("Invalid bucket array size.");
        Array.Copy(buckets, _buckets, BucketCount);
    }

    private static int CountLeadingZeros(uint value)
    {
        if (value == 0) return 32;
        int count = 0;
        while ((value & 0x80000000) == 0)
        {
            count++;
            value <<= 1;
        }
        return count + 1;
    }
}
