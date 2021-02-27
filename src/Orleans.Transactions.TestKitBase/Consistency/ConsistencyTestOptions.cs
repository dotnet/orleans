using System;

namespace Orleans.Transactions.TestKit.Consistency
{
    [Serializable]
    [GenerateSerializer]
    public class ConsistencyTestOptions
    {
        [Id(0)]
        public int RandomSeed { get; set; } = 0;
        [Id(1)]
        public int NumGrains { get; set; } = 50;
        [Id(2)]
        public int MaxDepth { get; set; } = 5;
        [Id(3)]
        public bool AvoidDeadlocks { get; set; } = true;
        [Id(4)]
        public bool AvoidTimeouts { get; set; } = true;
        [Id(5)]
        public ReadWriteDetermination ReadWrite { get; set; } = ReadWriteDetermination.PerGrain;
        [Id(6)]
        public long GrainOffset { get; set; }

        public const int MaxGrains = 100000;
    }

    public enum ReadWriteDetermination
    {
        PerTransaction, PerGrain, PerAccess
    }
}
