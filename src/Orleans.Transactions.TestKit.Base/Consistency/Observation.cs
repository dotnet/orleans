using System;

namespace Orleans.Transactions.TestKit.Consistency
{
    /// <summary>
    /// Describes a transactional read observation of a grain state version.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public struct Observation
    {
        /// <summary>
        /// Gets or sets the logical number of the observed grain.
        /// </summary>
        [Id(0)]
        public int Grain { get; set; }

        /// <summary>
        /// Gets or sets the observed state version sequence number.
        /// </summary>
        [Id(1)]
        public int SeqNo { get; set; }

        /// <summary>
        /// Gets or sets the identifier of the transaction which wrote the observed version.
        /// </summary>
        [Id(2)]
        public string WriterTx { get; set; }

        /// <summary>
        /// Gets or sets the identifier of the transaction which made the observation.
        /// </summary>
        [Id(3)]
        public string ExecutingTx { get; set; }
    }
}
