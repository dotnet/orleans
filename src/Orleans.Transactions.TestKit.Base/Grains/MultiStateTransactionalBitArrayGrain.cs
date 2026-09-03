
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit.Correctnesss
{
    /// <summary>
    /// Represents a growable bit array stored as packed 32-bit integer values.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class BitArrayState
    {
        /// <summary>
        /// Determines whether this instance contains the same packed bit values as another instance.
        /// </summary>
        /// <param name="other">The instance to compare with this instance.</param>
        /// <returns><see langword="true"/> when the packed values are equal; otherwise, <see langword="false"/>.</returns>
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

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((BitArrayState)obj);
        }

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(value);

        private static readonly int BitsInInt = sizeof(int) * 8;

        [JsonProperty("v")]
        [Id(0)]
        private int[] value = { 0 };

        /// <summary>
        /// Gets the packed 32-bit values which store the bits.
        /// </summary>
        [JsonIgnore]
        public int[] Value => value;

        /// <summary>
        /// Gets the number of packed 32-bit values in the state.
        /// </summary>
        [JsonIgnore]
        public int Length => this.value.Length;

        /// <summary>
        /// Initializes a new instance of the <see cref="BitArrayState"/> class with all bits cleared.
        /// </summary>
        public BitArrayState()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BitArrayState"/> class by copying another instance.
        /// </summary>
        /// <param name="other">The state to copy.</param>
        public BitArrayState(BitArrayState other)
        {
            this.value = new int[other.value.Length];
            for (var i = 0; i < other.value.Length; i++)
            {
                this.value[i] = other.value[i];
            }
        }

        /// <summary>
        /// Sets or clears a bit, growing the backing storage when necessary.
        /// </summary>
        /// <param name="index">The zero-based bit index.</param>
        /// <param name="value"><see langword="true"/> to set the bit; <see langword="false"/> to clear it.</param>
        public void Set(int index, bool value)
        {
            int idx = index / BitsInInt;
            if (idx >= this.value.Length)
            {
                Array.Resize(ref this.value, idx + 1);
            }
            int shift = 1 << (index % BitsInInt);
            if (value)
            {
                this.value[idx] |= shift;
            }
            else
                this.value[idx] &= ~shift;
        }

        /// <summary>
        /// Returns an enumerator over the packed 32-bit values.
        /// </summary>
        /// <returns>An enumerator over the backing values.</returns>
        public IEnumerator<int> GetEnumerator()
        {
            foreach (var v in this.value) yield return v;
        }

        /// <summary>
        /// Returns the bits as a string ordered from least significant to most significant within each packed value.
        /// </summary>
        /// <returns>A binary representation of the state.</returns>
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

        /// <summary>
        /// Gets or sets a packed 32-bit value by storage index.
        /// </summary>
        /// <param name="index">The zero-based storage index.</param>
        /// <value>The packed value at <paramref name="index"/>.</value>
        public int this[int index]
        {
            get => this.value[index];
            set => this.value[index] = value;
        }

        /// <summary>
        /// Determines whether two instances contain the same packed bit values.
        /// </summary>
        /// <param name="left">The first instance to compare.</param>
        /// <param name="right">The second instance to compare.</param>
        /// <returns><see langword="true"/> when the instances are equal; otherwise, <see langword="false"/>.</returns>
        public static bool operator ==(BitArrayState? left, BitArrayState? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (ReferenceEquals(left, null)) return false;
            if (ReferenceEquals(right, null)) return false;
            return left.Equals(right);
        }

        /// <summary>
        /// Determines whether two instances contain different packed bit values.
        /// </summary>
        /// <param name="left">The first instance to compare.</param>
        /// <param name="right">The second instance to compare.</param>
        /// <returns><see langword="true"/> when the instances differ; otherwise, <see langword="false"/>.</returns>
        public static bool operator !=(BitArrayState? left, BitArrayState? right)
        {
            return !(left == right);
        }

        /// <summary>
        /// Computes the bitwise exclusive OR of two states.
        /// </summary>
        /// <param name="left">The first state.</param>
        /// <param name="right">The second state.</param>
        /// <returns>A new state containing the bitwise exclusive OR.</returns>
        public static BitArrayState operator ^(BitArrayState left, BitArrayState right)
        {
            return Apply(left, right, (l, r) => l ^ r);
        }

        /// <summary>
        /// Computes the bitwise OR of two states.
        /// </summary>
        /// <param name="left">The first state.</param>
        /// <param name="right">The second state.</param>
        /// <returns>A new state containing the bitwise OR.</returns>
        public static BitArrayState operator |(BitArrayState left, BitArrayState right)
        {
            return Apply(left, right, (l, r) => l | r);
        }

        /// <summary>
        /// Computes the bitwise AND of two states.
        /// </summary>
        /// <param name="left">The first state.</param>
        /// <param name="right">The second state.</param>
        /// <returns>A new state containing the bitwise AND.</returns>
        public static BitArrayState operator &(BitArrayState left, BitArrayState right)
        {
            return Apply(left, right, (l, r) => l & r);
        }

        /// <summary>
        /// Applies a binary operation to the corresponding packed values of two states.
        /// </summary>
        /// <param name="left">The first state.</param>
        /// <param name="right">The second state.</param>
        /// <param name="op">The operation applied to each pair of packed values.</param>
        /// <returns>A new state containing the operation results.</returns>
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

    /// <summary>
    /// Implements bit-array transaction test operations over the maximum supported number of coordinated states.
    /// </summary>
    [GrainType("txn-correctness-MaxStateTransactionalGrain")]
    public class MaxStateTransactionalGrain : MultiStateTransactionalBitArrayGrain
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MaxStateTransactionalGrain"/> class.
        /// </summary>
        /// <param name="stateFactory">The factory used to create transactional states.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public MaxStateTransactionalGrain(ITransactionalStateFactory stateFactory,
            ILoggerFactory loggerFactory)
            : base(Enumerable.Range(0, TransactionTestConstants.MaxCoordinatedTransactions)
                .Select(i => stateFactory.Create<BitArrayState>(new TransactionalStateConfiguration(new TransactionalStateAttribute($"data{i}", TransactionTestConstants.TransactionStore))))
                .ToArray(),
                  loggerFactory)
        {
        }
    }

    /// <summary>
    /// Implements bit-array transaction test operations over two transactional states.
    /// </summary>
    [GrainType("txn-correctness-DoubleStateTransactionalGrain")]
    public class DoubleStateTransactionalGrain : MultiStateTransactionalBitArrayGrain
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DoubleStateTransactionalGrain"/> class.
        /// </summary>
        /// <param name="data1">The first transactional state.</param>
        /// <param name="data2">The second transactional state.</param>
        /// <param name="loggerFactory">The logger factory.</param>
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

    /// <summary>
    /// Implements bit-array transaction test operations over one transactional state.
    /// </summary>
    [GrainType("txn-correctness-SingleStateTransactionalGrain")]
    public class SingleStateTransactionalGrain : MultiStateTransactionalBitArrayGrain
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SingleStateTransactionalGrain"/> class.
        /// </summary>
        /// <param name="data">The transactional state.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public SingleStateTransactionalGrain(
            [TransactionalState("data", TransactionTestConstants.TransactionStore)]
            ITransactionalState<BitArrayState> data,
            ILoggerFactory loggerFactory)
            : base(new ITransactionalState<BitArrayState>[1] { data }, loggerFactory)
        {
        }
    }

    /// <summary>
    /// Provides transaction test operations over an ordered collection of bit-array states.
    /// </summary>
    [GrainType("txn-correctness-MultiStateTransactionalBitArrayGrain")]
    public partial class MultiStateTransactionalBitArrayGrain : Grain, ITransactionalBitArrayGrain
    {
        /// <summary>
        /// The transactional states operated on by this grain.
        /// </summary>
        protected ITransactionalState<BitArrayState>[] dataArray;
        private readonly ILoggerFactory loggerFactory;

        /// <summary>
        /// The logger for this grain activation.
        /// </summary>
        protected ILogger logger = null!;

        /// <summary>
        /// Initializes a new instance of the <see cref="MultiStateTransactionalBitArrayGrain"/> class.
        /// </summary>
        /// <param name="dataArray">The ordered transactional states.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public MultiStateTransactionalBitArrayGrain(
            ITransactionalState<BitArrayState>[] dataArray,
            ILoggerFactory loggerFactory)
        {
            this.dataArray = dataArray;
            this.loggerFactory = loggerFactory;
        }

<<<<<<< HEAD

        /// <inheritdoc/>
||||||| parent of 82a763ec4 (style: format solution whitespace)
        
=======
>>>>>>> 82a763ec4 (style: format solution whitespace)
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.logger = this.loggerFactory.CreateLogger(this.GetGrainId().ToString());
            LogTraceGrainId(this.logger, this.GetPrimaryKey());

            return base.OnActivateAsync(cancellationToken);
        }

        /// <inheritdoc/>
        public Task Ping()
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task SetBit(int index)
        {
            return Task.WhenAll(this.dataArray
                .Select(data => data.PerformUpdate(state =>
                {
                    LogTraceSettingBit(this.logger, index, state, TransactionContext.CurrentTransactionId);
                    state.Set(index, true);
                    LogTraceSetBit(this.logger, index, state);
                })));
        }

        /// <inheritdoc/>
        public async Task<List<BitArrayState>> Get()
        {
            return (await Task.WhenAll(this.dataArray
                .Select(state => state.PerformRead(s =>
                {
                    LogTraceGetState(this.logger, s);
                    return s;
                })))).ToList();
        }

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "GrainId: {GrainId}."
        )]
        private static partial void LogTraceGrainId(ILogger logger, Guid grainId);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Setting bit {Index} in state {State}. Transaction {CurrentTransactionId}"
        )]
        private static partial void LogTraceSettingBit(ILogger logger, int index, BitArrayState state, object currentTransactionId);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Set bit {Index} in state {State}."
        )]
        private static partial void LogTraceSetBit(ILogger logger, int index, BitArrayState state);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Get state {State}."
        )]
        private static partial void LogTraceGetState(ILogger logger, BitArrayState state);
    }
}
