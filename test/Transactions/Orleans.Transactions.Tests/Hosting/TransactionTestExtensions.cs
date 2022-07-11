using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;

namespace Orleans.Transactions.Tests
{
    public static class TransactionTestExtensions
    {
        public static ISiloBuilder ConfigureTracingForTransactionTests(this ISiloBuilder clientBuilder)
        {
            clientBuilder.Services.ConfiguretracingForTransactionTests();
            return clientBuilder;
        }

        public static IClientBuilder ConfigureTracingForTransactionTests(this IClientBuilder clientBuilder)
        {
            clientBuilder.Services.ConfiguretracingForTransactionTests();
            return clientBuilder;
        }

        // control the tracing of the various components of the transaction mechanism
        public static IServiceCollection ConfiguretracingForTransactionTests(this IServiceCollection services)
        {
            return services.AddLogging(loggingBuilder =>
            {
                loggingBuilder
                    .AddFilter("SingleStateTransactionalGrain.data", LogLevel.Trace)
                    .AddFilter("DoubleStateTransactionalGrain.data", LogLevel.Trace)
                    .AddFilter("MaxStateTransactionalGrain.data", LogLevel.Trace)
                    .AddFilter("SingleStateFaultInjectionTransactionalGrain.data", LogLevel.Trace)
                    .AddFilter("ConsistencyTestGrain.data", LogLevel.Trace)
                    .AddFilter("ConsistencyTestGrain.graincall", LogLevel.Trace)
                    .AddFilter("Orleans.Transactions.TransactionAgent", LogLevel.Trace)
                    .AddFilter("Orleans.Transactions.AzureStorage.AzureTableTransactionalStateStorage", LogLevel.Trace)
                    .AddFilter("TransactionAgent", LogLevel.Trace);
            });
        }
    }
}

