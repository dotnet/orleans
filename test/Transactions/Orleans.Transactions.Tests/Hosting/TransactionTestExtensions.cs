using Microsoft.Extensions.Logging;
using Orleans.Hosting;

namespace Orleans.Transactions.Tests
{
    public static class TransactionTestExtensions
    {
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
