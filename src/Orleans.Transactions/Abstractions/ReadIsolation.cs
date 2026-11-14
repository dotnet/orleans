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
        /// Reads the latest locally committed state in an empty ambient transaction context, independently
        /// copying the result while pending writers retain their locks and tentative state.
        /// Remotely coordinated writes become locally visible after the participant receives and persists
        /// commit confirmation, which can occur after the write transaction completes.
        /// </summary>
        ReadCommitted,
    }
}
