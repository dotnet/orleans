using System;

namespace FasterSample.Core.RandomGenerators
{
    internal class RandomGenerator : IRandomGenerator
    {
        private static Random _global = new Random();

        [ThreadStatic]
        private static Random _local;

        private static Random GetLocalGenerator()
        {
            if (_local is null)
            {
                lock (_global)
                {
                    if (_local is null)
                    {
                        _local = new Random(_global.Next());
                    }
                }
            }

            return _local;
        }

        public int Next(int minValue, int maxValue) => GetLocalGenerator().Next(minValue, maxValue);

        public int Next(int maxValue) => GetLocalGenerator().Next(maxValue);
    }
}