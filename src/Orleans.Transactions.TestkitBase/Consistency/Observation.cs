using System;

namespace Orleans.Transactions.TestKit.Consistency
{
    [Serializable]
    public struct Observation
    {
        public int Grain { get; set; }
        public int SeqNo { get; set; }
        public string WriterTx { get; set; }
        public string ExecutingTx { get; set; }
    }
}
