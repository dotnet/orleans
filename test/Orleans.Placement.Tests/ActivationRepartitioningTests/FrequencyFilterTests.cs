using System.Text;
using Orleans.Runtime.Placement.Repartitioning;
using Xunit;

namespace UnitTests.ActivationRepartitioningTests;

public class FrequencyFilterTests
{
    [Fact]
    public void GetExpectedTopK()
    {
        const int NumSamples = 10_000;
        var sink = new UlongFrequentItemCollection(100);
        var random = new Random();
        var distribution = new ZipfRejectionSampler(random, 1000, 0.5);
        for (var i = 0; i < NumSamples; i++)
        {
            var sample = (ulong)distribution.Sample();
            sink.Add(new TestKey(sample));

            if (i == 4 * NumSamples / 5)
            {
                sink.Remove(new TestKey(3));
            }
        }

        var allCounters = sink.Elements.ToList();
        allCounters.Sort((left, right) => right.Count.CompareTo(left.Count));
        var sb = new StringBuilder();
        foreach (var (key, count, error) in allCounters)
        {
            if (error == 0)
            {
                sb.AppendLine($"{key.Key,3}: {count}");
            }
            else
            {
                sb.AppendLine($"{key.Key,3}: {count} ε{error}");
            }
        }

        var result = sb.ToString();
        Assert.NotEmpty(result);
    }

    public readonly struct TestKey(ulong key) 
    {
        private static ulong _nextKey;
        public static TestKey GetNext() => new(_nextKey++);
        public readonly ulong Key = key;

        public override string ToString() => $"[{Key}]";
    }

    private sealed class UlongFrequentItemCollection(int capacity) : FrequentItemCollection<ulong, TestKey>(capacity)
    {
        protected override ulong GetKey(in TestKey element) => element.Key;
        public void Remove(in TestKey element) => RemoveCore(GetKey(element));
    }

    /// <summary>
    /// Generates an approximate Zipf distribution. Previous method was 20x faster than MathNet.Numerics, but could only generate 250 samples/sec.
    /// This approximate method can generate > 1,000,000 samples/sec.
    /// </summary>
    public class FastZipf
    {
        private static readonly Random SeededPrng = new(42);

        /// <summary>
        /// Generate a zipf distribution.
        /// </summary>
        /// <param name="random">The random number generator to use.</param>
        /// <param name="sampleCount">The number of samples.</param>
        /// <param name="skew">The skew. s=0 is a uniform distribution. As s increases, high-rank items become rapidly more likely than the rare low-ranked items.</param>
        /// <param name="cardinality">N: the cardinality. The total number of items.</param>
        /// <returns>A zipf distribution.</returns>
        public static long[] Generate(Random random, int sampleCount, double skew, int cardinality)
        {
            var sampler = new ZipfRejectionSampler(random, cardinality, skew);

            var samples = new long[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                samples[i] = sampler.Sample();
            }

            return samples;
        }

        /// <summary>
        /// Generate a zipf distribution.
        /// </summary>
        /// <param name="sampleCount">The number of samples.</param>
        /// <param name="skew">The skew. s=0 is a uniform distribution. As s increases, high-rank items become rapidly more likely than the rare low-ranked items.</param>
        /// <param name="cardinality">N: the cardinality. The total number of items.</param>
        /// <returns>A zipf distribution.</returns>
        public static long[] Generate(int sampleCount, double skew, int cardinality) => Generate(SeededPrng, sampleCount, skew, cardinality);
    }

    // https://jasoncrease.medium.com/rejection-sampling-the-zipf-distribution-6b359792cffa
    public class ZipfRejectionSampler
    {
        private readonly Random _rand;
        private readonly double _skew;
        private readonly double _t;

        public ZipfRejectionSampler(Random random, long cardinality, double skew)
        {
            _rand = random;
            _skew = skew;
            _t = (Math.Pow(cardinality, 1 - skew) - skew) / (1 - skew);
        }

        public long Sample()
        {
            while (true)
            {
                var invB = bInvCdf(_rand.NextDouble());
                var sampleX = (long)(invB + 1);
                var yRand = _rand.NextDouble();
                var ratioTop = Math.Pow(sampleX, -_skew);
                var ratioBottom = sampleX <= 1 ? 1 / _t : Math.Pow(invB, -_skew) / _t;
                var rat = ratioTop / (ratioBottom * _t);

                if (yRand < rat)
                {
                    return sampleX;
                }
            }
        }
        private double bInvCdf(double p)
        {
            return p * _t switch
            {
                <= 1 => p * _t,
                _ => Math.Pow(p * _t * (1 - _skew) + _skew, 1 / (1 - _skew))
            };
        }
    }
}