using System;

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Counts read and write accesses on a transaction participant.
    /// </summary>
    [GenerateSerializer]
    [Serializable]
    public struct AccessCounter
    {
        [Id(0)]
        public int Reads;
        [Id(1)]
        public int Writes;

        public static AccessCounter operator +(AccessCounter c1, AccessCounter c2)
        {
            return new AccessCounter { Reads = c1.Reads + c2.Reads, Writes = c1.Writes + c2.Writes };
        }
    }
}
