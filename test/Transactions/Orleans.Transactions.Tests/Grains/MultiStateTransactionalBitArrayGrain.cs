
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.Tests.Correctness
{
    [Serializable]
    public class BitArrayState
    {
        private static readonly int BitsInInt = sizeof(int) * 8;

        private int[] value = new int[] { 0 };
        public int[] Value => value;

        public void Set(int index, bool value)
        {
            if (index >= this.value.Length * BitsInInt)
            {
                Array.Resize(ref this.value, this.value.Length + 1);
            }
            int idx = this.value.Length / BitsInInt;
            int shift = 1 >> (this.value.Length % BitsInInt);
            if (value)
            {
                this.value[idx] |= shift;
            } else
                this.value[idx] &= ~shift;
        }
    }

    public class MaxStateTransactionalGrain : MultiStateTransactionalBitArrayGrain
    {
        public MaxStateTransactionalGrain(ITransactionalStateFactory stateFactory,
            ILoggerFactory loggerFactory)
            : base(Enumerable.Range(0, TransactionTestConstants.MaxCoordinatedTransactions)
                .Select(i => stateFactory.Create<BitArrayState>(new TransactionalStateAttribute($"data{i}", TransactionTestConstants.TransactionStore)))
                .ToArray(),
                  loggerFactory)
        {
        }
    }

    public class DoubleStateTransactionalGrain : MultiStateTransactionalBitArrayGrain
    {
        public DoubleStateTransactionalGrain(
            [TransactionalState("data1", TransactionTestConstants.TransactionStore)]
            ITransactionalState<BitArrayState> data1,
            [TransactionalState("data2", TransactionTestConstants.TransactionStore)]
            ITransactionalState<BitArrayState> data2,
            ILoggerFactory loggerFactory)
            : base(new ITransactionalState<BitArrayState>[2] { data1, data2 }, loggerFactory)
        {
        }
    }

    public class SingleStateTransactionalGrain : MultiStateTransactionalBitArrayGrain
    {
        public SingleStateTransactionalGrain(
            [TransactionalState("data", TransactionTestConstants.TransactionStore)]
            ITransactionalState<BitArrayState> data,
            ILoggerFactory loggerFactory)
            : base(new ITransactionalState<BitArrayState>[1] { data }, loggerFactory)
        {
        }
    }

    public class MultiStateTransactionalBitArrayGrain : Grain, ITransactionalBitArrayGrain
    {
        protected ITransactionalState<BitArrayState>[] dataArray;
        private readonly ILoggerFactory loggerFactory;
        protected ILogger logger;

        public MultiStateTransactionalBitArrayGrain(
            ITransactionalState<BitArrayState>[] dataArray,
            ILoggerFactory loggerFactory)
        {
            this.dataArray = dataArray;
            this.loggerFactory = loggerFactory;
        }

        public override Task OnActivateAsync()
        {
            this.logger = this.loggerFactory.CreateLogger(this.GetGrainIdentity().ToString());
            return base.OnActivateAsync();
        }

        public async Task SetBit(int index)
        {
            foreach (var data in this.dataArray)
            {
                await data.PerformUpdate(state =>
                {
                    state.Set(index, true);
                });
            }
        }

        public async Task<int[][]> Get()
        {
            var result = new int[dataArray.Length][];
            for (int i = 0; i < dataArray.Length; i++)
            {
                result[i] = await dataArray[i].PerformRead(state =>
                {
                    return state.Value;
                });
            }
            return result;
        }
    }
}
