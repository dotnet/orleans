using System;

namespace Orleans.Internal;

public static class ThreadSafeRandom
{
    public static int Next() => Random.Shared.Next();
    public static int Next(int maxValue) => Random.Shared.Next(maxValue);
    public static int Next(int minValue, int maxValue) => Random.Shared.Next(minValue, maxValue);
    public static void NextBytes(byte[] buffer) => Random.Shared.NextBytes(buffer);
    public static double NextDouble() => Random.Shared.NextDouble();
}