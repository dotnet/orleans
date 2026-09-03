using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures transaction processing for transactional state.
    /// </summary>
    public class TransactionalStateOptions
    {
        /// <summary>
        /// Gets or sets the base duration that a transaction group retains the state lock. The effective duration is at least the transaction timeout.
        /// </summary>
        public TimeSpan LockTimeout { get; set; } = DefaultLockTimeout;

        /// <summary>
        /// The default value of <see cref="LockTimeout"/>.
        /// </summary>
        public static TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Gets or sets the maximum duration that a transaction manager waits for the prepare phase to complete.
        /// </summary>
        public TimeSpan PrepareTimeout { get; set; } = DefaultPrepareTimeout;

        /// <summary>
        /// Gets the default value of <see cref="PrepareTimeout"/>.
        /// </summary>
        public static TimeSpan DefaultPrepareTimeout => TimeSpan.FromSeconds(20);

        /// <summary>
        /// Gets or sets the maximum duration that a transaction waits to acquire the state lock.
        /// </summary>
        public TimeSpan LockAcquireTimeout { get; set; } = DefaultLockAcquireTimeout;

        /// <summary>
        /// Gets the default value of <see cref="LockAcquireTimeout"/>.
        /// </summary>
        public static TimeSpan DefaultLockAcquireTimeout => TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets the interval between liveness notifications sent for remote transactions.
        /// </summary>
        public TimeSpan RemoteTransactionPingFrequency { get; set; } = DefaultRemoteTransactionPingFrequency;

        /// <summary>
        /// The default value of <see cref="RemoteTransactionPingFrequency"/>.
        /// </summary>
        public static TimeSpan DefaultRemoteTransactionPingFrequency = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets or sets the delay between attempts to confirm a committed transaction.
        /// </summary>
        public TimeSpan ConfirmationRetryDelay { get; set; } = DefaultConfirmationRetryDelay;
        private static TimeSpan DefaultConfirmationRetryDelay => TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the legacy confirmation retry limit retained for configuration compatibility.
        /// </summary>
        /// <remarks>The runtime retries confirmation until it succeeds or the activation is canceled.</remarks>
        public static int ConfirmationRetryLimit { get; set; } = DefaultConfirmationRetryLimit;

        /// <summary>
        /// The default value of <see cref="ConfirmationRetryLimit"/>.
        /// </summary>
        public const int DefaultConfirmationRetryLimit = 3;

        /// <summary>
        /// Gets or sets the maximum number of transactions which can share a lock group.
        /// </summary>
        public int MaxLockGroupSize { get; set; } = DefaultMaxLockGroupSize;

        /// <summary>
        /// The default value of <see cref="MaxLockGroupSize"/>.
        /// </summary>
        public const int DefaultMaxLockGroupSize = 20;

    }
}
