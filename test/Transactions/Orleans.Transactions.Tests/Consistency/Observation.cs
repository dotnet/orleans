using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Transactions.Tests.Consistency
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
