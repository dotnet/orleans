using System;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit.Consistency
{
    /// <summary>
    /// Defines randomized transactional operations used to produce consistency histories.
    /// </summary>
    public interface IConsistencyTestGrain : IGrainWithIntegerKey
    {
        /// <summary>
        /// Executes a randomized transactional read, write, or nested grain call.
        /// </summary>
        /// <param name="options">The consistency test configuration.</param>
        /// <param name="depth">The current nested call depth.</param>
        /// <param name="stack">The diagnostic path of the transaction call.</param>
        /// <param name="max">The exclusive upper bound for nested grain selection.</param>
        /// <param name="stopAfter">The deadline for scheduling additional nested calls.</param>
        /// <returns>The state versions observed by the transaction.</returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task<Observation[]> Run(ConsistencyTestOptions options, int depth, string stack, int max, DateTime stopAfter);
    }


    /// <summary>
    /// Represents an intentional user-initiated transaction abort.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class UserAbort : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UserAbort"/> class.
        /// </summary>
        public UserAbort() : base("User aborted transaction") { }

        /// <summary>
        /// Initializes a new instance of the <see cref="UserAbort"/> class from serialized data.
        /// </summary>
        /// <param name="info">The serialized exception data.</param>
        /// <param name="context">The context for the serialization operation.</param>
        [Obsolete]
        protected UserAbort(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

}
