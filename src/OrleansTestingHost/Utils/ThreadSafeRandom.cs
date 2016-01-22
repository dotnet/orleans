using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Orleans.TestingHost.Utils
{
    /// <summary>
    /// Thread-safe random number generator.
    /// Similar to the implementation by Steven Toub: http://blogs.msdn.com/b/pfxteam/archive/2014/10/20/9434171.aspx
    /// </summary>
    internal static class ThreadSafeRandom
    {
        private static readonly RandomNumberGenerator globalCryptoProvider = RandomNumberGenerator.Create();

        [ThreadStatic] private static Random random;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Random GetRandom()
        {
            if (random == null)
            {
                byte[] buffer = new byte[4];
                globalCryptoProvider.GetBytes(buffer);
                random = new Random(BitConverter.ToInt32(buffer, 0));
            }

            return random;
        }

        public static int Next()
        {
            return GetRandom().Next();
        }

        public static int Next(int maxValue)
        {
            return GetRandom().Next(maxValue);
        }

        public static int Next(int minValue, int maxValue)
        {
            return GetRandom().Next(minValue, maxValue);
        }
    }
}
