namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Injects faults before or after transactional state is stored.
    /// </summary>
    public interface ITransactionFaultInjector
    {
        /// <summary>
        /// Injects a fault before transactional state is stored.
        /// </summary>
        void BeforeStore();

        /// <summary>
        /// Injects a fault after transactional state is stored.
        /// </summary>
        void AfterStore();
    }
}
