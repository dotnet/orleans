﻿
using Microsoft.Extensions.Logging;
using Orleans.Hosting;

namespace Orleans.Transactions.Tests
{
    public static class TransactionTestConstants
    {
        /// <summary>
        /// Max number of grains to include in a transaction for test purposes.  Not a hard limit of the transaction system.
        /// </summary>
        public const int MaxCoordinatedTransactions = 8;

        // storage providers
        public const string TransactionStore = "TransactionStore";

        // grain implementations singleton TM
        public const string NoStateTransactionalGrain = "Orleans.Transactions.Tests.NoStateTransactionalGrain";
        public const string SingleStateTransactionalGrain = "Orleans.Transactions.Tests.SingleStateTransactionalGrain";
        public const string DoubleStateTransactionalGrain = "Orleans.Transactions.Tests.DoubleStateTransactionalGrain";
        public const string MaxStateTransactionalGrain = "Orleans.Transactions.Tests.MaxStateTransactionalGrain";


        // control the tracing of the various components of the transaction mechanism
        public static ISiloHostBuilder ConfigureTracingForTransactionTests(this ISiloHostBuilder hostBuilder)
        {
            return hostBuilder
                 .ConfigureLogging(builder => builder.AddFilter("SingleStateTransactionalGrain.data", LogLevel.Trace))
                 .ConfigureLogging(builder => builder.AddFilter("DoubleStateTransactionalGrain.data", LogLevel.Trace))
                 .ConfigureLogging(builder => builder.AddFilter("MaxStateTransactionalGrain.data", LogLevel.Trace))
                 .ConfigureLogging(builder => builder.AddFilter("TransactionAgent", LogLevel.Trace))
                 .ConfigureLogging(builder => builder.AddFilter("Orleans.Transactions.AzureStorage.AzureTableTransactionalStateStorage", LogLevel.Trace));
        }
    }
}
