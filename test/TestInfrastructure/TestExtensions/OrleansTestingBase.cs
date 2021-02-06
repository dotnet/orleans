using System;
using Orleans.Internal;
using Orleans.Runtime;

namespace TestExtensions
{
    public abstract class OrleansTestingBase
    {
        protected static class random
        {
            public static int Next() => ThreadSafeRandom.Next();
            public static int Next(int maxValue) => ThreadSafeRandom.Next(maxValue);
            public static double NextDouble() => ThreadSafeRandom.NextDouble();
        }

        public static long GetRandomGrainId()
        {
            return ThreadSafeRandom.Next();
        }
    }
}