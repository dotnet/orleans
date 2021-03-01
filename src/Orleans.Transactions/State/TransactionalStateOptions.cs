using System;

namespace Orleans.Configuration
{
    public class TransactionalStateOptions
    {
        // max time a group can occupy the lock
        public TimeSpan LockTimeout { get; set; } = DefaultLockTimeout;
        public static TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(8);

        // max time the TM will wait for prepare phase to complete
        public TimeSpan PrepareTimeout { get; set; } = DefaultPrepareTimeout;
        public static TimeSpan DefaultPrepareTimeout => TimeSpan.FromSeconds(20);

        // max time a transaction will wait for the lock to become available
        public TimeSpan LockAcquireTimeout { get; set; } = DefaultLockAcquireTimeout;
        public static TimeSpan DefaultLockAcquireTimeout => TimeSpan.FromSeconds(10);

        public TimeSpan RemoteTransactionPingFrequency { get; set; } = DefaultRemoteTransactionPingFrequency;
        public static TimeSpan DefaultRemoteTransactionPingFrequency = TimeSpan.FromSeconds(60);

        public TimeSpan ConfirmationRetryDelay { get; set; } = DefaultConfirmationRetryDelay;
        private static TimeSpan DefaultConfirmationRetryDelay => TimeSpan.FromSeconds(30);

        public static int ConfirmationRetryLimit { get; set; } = DefaultConfirmationRetryLimit;
        public const int DefaultConfirmationRetryLimit = 3;

        public int MaxLockGroupSize { get; set; } = DefaultMaxLockGroupSize;
        public const int DefaultMaxLockGroupSize = 20;

    }
}
