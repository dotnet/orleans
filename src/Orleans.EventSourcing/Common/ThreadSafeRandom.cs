using System;
using System.Runtime.CompilerServices;

#nullable enable
namespace Orleans.Internal.EventSourcing
{
    /// <summary>
    /// Thread-safe random number generator
    /// </summary>
    internal static class ThreadSafeRandom
    {
        [ThreadStatic] private static Random? threadRandom;

        private static Random Instance => threadRandom ?? CreateInstance();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Random CreateInstance() => threadRandom = new Random();

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
