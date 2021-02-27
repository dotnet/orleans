
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit.Correctnesss
{
    [Serializable]
    [GenerateSerializer]
    public class BitArrayState
    {
        protected bool Equals(BitArrayState other)
        {
            if (ReferenceEquals(null, this.value)) return false;
            if (ReferenceEquals(null, other.value)) return false;
            if (this.value.Length != other.value.Length) return false;
            for (var i = 0; i < this.value.Length; i++)
            {
                if (this.value[i] != other.value[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((BitArrayState) obj);
        }

        public override int GetHashCode()
        {
            return (value != null ? value.GetHashCode() : 0);
        }

        private static readonly int BitsInInt = sizeof(int) * 8;

        [JsonProperty("v")]
        [Id(0)]
        private int[] value = { 0 };

        [JsonIgnore]
        public int[] Value => value;

        [JsonIgnore]
        public int Length => this.value.Length;

        public BitArrayState()
        {
        }

        public BitArrayState(BitArrayState other)
        {
            this.value = new int[other.value.Length];
            for (var i = 0; i < other.value.Length; i++)
            {
                this.value[i] = other.value[i];
            }
        }

        public void Set(int index, bool value)
        {
            int idx = index / BitsInInt;
            if (idx >= this.value.Length)
            {
                Array.Resize(ref this.value, idx+1);
            }
            int shift = 1 << (index % BitsInInt);
            if (value)
            {
                this.value[idx] |= shift;
            } else
                this.value[idx] &= ~shift;
        }

        public IEnumerator<int> GetEnumerator()
        {
            foreach (var v in this.value) yield return v;
        }

        public override string ToString()
        {
            // Write the values from least significant bit to most significant bit
            var builder = new StringBuilder();
            foreach (var v in this.value)
            {
                builder.Append(Reverse(Convert.ToString(v, 2)).PadRight(BitsInInt, '0'));

                string Reverse(string s)
                {
                    char[] charArray = s.ToCharArray();
                    Array.Reverse(charArray);
                    return new string(charArray);
                }
            }
            return builder.ToString();
        }

        public int this[int index]
        {
            get => this.value[index];
            set => this.value[index] = value;
        }

        public static bool operator ==(BitArrayState left, BitArrayState right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (ReferenceEquals(left, null)) return false;
            if (ReferenceEquals(right, null)) return false;
            return left.Equals(right);
        }

        public static bool operator !=(BitArrayState left, BitArrayState right)
        {
            return !(left == right);
        }

        public static BitArrayState operator ^(BitArrayState left, BitArrayState right)
        {
            return Apply(left, right, (l, r) => l ^ r);
        }

        public static BitArrayState operator |(BitArrayState left, BitArrayState right)
        {
            return Apply(left, right, (l, r) => l | r);
        }

        public static BitArrayState operator &(BitArrayState left, BitArrayState right)
        {
            return Apply(left, right, (l, r) => l & r);
        }

        public static BitArrayState Apply(BitArrayState left, BitArrayState right, Func<int, int, int> op)
        {
            var result = new BitArrayState(left.value.Length > right.value.Length ? left : right);
            var overlappingLength = Math.Min(left.value.Length, right.value.Length);
            var i = 0;
            for (; i < overlappingLength; i++)
            {
                result.value[i] = op(left.value[i], right.value[i]);
            }

            // Continue with the non-overlapping portion.
            for (; i < result.value.Length; i++)
            {
                var leftVal = left.value.Length > i ? left.value[i] : 0;
                var rightVal = right.value.Length > i ? right.value[i] : 0;
                result.value[i] = op(leftVal, rightVal);
            }

            return result;
        }
    }

    [GrainType("txn-correctness-MaxStateTransactionalGrain")]
    public class MaxStateTransactionalGrain : MultiStateTransactionalBitArrayGrain
    {
        public MaxStateTransactionalGrain(ITransactionalStateFactory stateFactory,
            ILoggerFactory loggerFactory)
            : base(Enumerable.Range(0, TransactionTestConstants.MaxCoordinatedTransactions)
                .Select(i => stateFactory.Create<BitArrayState>(new TransactionalStateConfiguration(new TransactionalStateAttribute($"data{i}", TransactionTestConstants.TransactionStore))))
                .ToArray(),
                  loggerFactory)
        {
        }
    }

    [GrainType("txn-correctness-DoubleStateTransactionalGrain")]
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

    [GrainType("txn-correctness-SingleStateTransactionalGrain")]
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

    [GrainType("txn-correctness-MultiStateTransactionalBitArrayGrain")]
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
            this.logger = this.loggerFactory.CreateLogger(this.GetGrainId().ToString());
            this.logger.LogTrace($"GrainId : {this.GetPrimaryKey()}.");

            return base.OnActivateAsync();
        }

        public Task Ping()
        {
            return Task.CompletedTask;
        }

        public Task SetBit(int index)
        {
            return Task.WhenAll(this.dataArray
                .Select(data => data.PerformUpdate(state =>
                {
                    this.logger.LogTrace($"Setting bit {index} in state {state}. Transaction {TransactionContext.CurrentTransactionId}");
                    state.Set(index, true);
                    this.logger.LogTrace($"Set bit {index} in state {state}.");
                })));
        }

        public async Task<List<BitArrayState>> Get()
        {
            return (await Task.WhenAll(this.dataArray
                .Select(state => state.PerformRead(s =>
                {
                    this.logger.LogTrace($"Get state {s}.");
                    return s;
                })))).ToList();
        }
    }
}
