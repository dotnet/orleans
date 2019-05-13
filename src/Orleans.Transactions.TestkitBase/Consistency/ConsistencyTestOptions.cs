using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit.Consistency
{
    [Serializable]
    public class ConsistencyTestOptions
    {
        public int RandomSeed { get; set; } = 0;
        public int NumGrains { get; set; } = 50;
        public int MaxDepth { get; set; } = 5;
        public bool AvoidDeadlocks { get; set; } = true;
        public bool AvoidTimeouts { get; set; } = true;
        public ReadWriteDetermination ReadWrite { get; set; } = ReadWriteDetermination.PerGrain;
        public long GrainOffset { get; set; }

        public const int MaxGrains = 100000;
    }

    public enum ReadWriteDetermination
    {
        PerTransaction, PerGrain, PerAccess
    }
}
