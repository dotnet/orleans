using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

#nullable enable
namespace Orleans.Internal
{
    /// <summary>
    /// Thread-safe random number generator
    /// </summary>
    public static class ThreadSafeRandom
    {
        [ThreadStatic] private static Random? threadRandom;

        private static Random Instance => threadRandom ?? CreateInstance();

        [MethodImpl(MethodImplOptions.NoInlining)]
#if NETCOREAPP
        private static Random CreateInstance() => threadRandom = new Random();
#else
        private static Random CreateInstance()
        {
            var buf = new byte[4];
            globalRandom.GetBytes(buf);
            return threadRandom = new Random(BitConverter.ToInt32(buf, 0));
        }

        private static readonly RandomNumberGenerator globalRandom = RandomNumberGenerator.Create();
#endif

        public static int Next() => Instance.Next();
        public static int Next(int maxValue) => Instance.Next(maxValue);
        public static int Next(int minValue, int maxValue) => Instance.Next(minValue, maxValue);
        public static void NextBytes(byte[] buffer) => Instance.NextBytes(buffer);
        public static double NextDouble() => Instance.NextDouble();

        public static TimeSpan NextTimeSpan(TimeSpan timeSpan)
        {
            if (timeSpan <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeSpan), timeSpan, "TimeSpan must be positive.");
            return timeSpan.Multiply(NextDouble());
        }

        public static TimeSpan NextTimeSpan(TimeSpan minValue, TimeSpan maxValue)
        {
            if (minValue <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minValue), minValue, "MinValue must be positive.");
            if (minValue >= maxValue) throw new ArgumentOutOfRangeException(nameof(minValue), minValue, "MinValue must be less than maxValue.");
            return minValue + NextTimeSpan(maxValue - minValue);
        }
    }
}
