using System;

namespace Orleans.Transactions.TestKit.Consistency
{
    [Serializable]
    [GenerateSerializer]
    public struct Observation
    {
        [Id(0)]
        public int Grain { get; set; }
        [Id(1)]
        public int SeqNo { get; set; }
        [Id(2)]
        public string WriterTx { get; set; }
        [Id(3)]
        public string ExecutingTx { get; set; }
    }
}
