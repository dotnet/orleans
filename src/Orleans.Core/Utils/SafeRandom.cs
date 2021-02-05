using System;

namespace Orleans.Internal
{
    [Obsolete("Use " + nameof(ThreadSafeRandom) + " static methods instead.")]
    public class SafeRandom
    {
        public int Next() => ThreadSafeRandom.Next();
        public int Next(int maxValue) => ThreadSafeRandom.Next(maxValue);
        public int Next(int minValue, int maxValue) => ThreadSafeRandom.Next(minValue, maxValue);
        public void NextBytes(byte[] buffer) => ThreadSafeRandom.NextBytes(buffer);
        public double NextDouble() => ThreadSafeRandom.NextDouble();
    }
}
