namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Specifies the isolation of a read performed through <see cref="ITransactionalState{TState}"/>.
    /// </summary>
    public enum ReadIsolation
    {
        /// <summary>
        /// Reads inside the ambient transaction under a shared lock. This is the default.
        /// </summary>
        Serializable = 0,

        /// <summary>
        /// Reads the most recently committed state without a transaction, lock, or storage write.
        /// Rejected when an ambient transaction exists.
        /// </summary>
        ReadCommitted,
    }
}
