using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Transactions;

namespace Documentation.Grains.Transactions
{
    internal static class TransactionConfiguration
    {
        internal static void ConfigureSilo(string[] args)
        {
            // <enable_silo_transactions>
var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.UseTransactions();
});
            // </enable_silo_transactions>
        }

        internal static void ConfigureClient(
            HostApplicationBuilder builder)
        {
            // <enable_client_transactions>
builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.UseTransactions();
});
            // </enable_client_transactions>
        }

        internal static void ConfigureAzureStorage(
            ISiloBuilder siloBuilder,
            HostApplicationBuilder builder)
        {
            // <configure_transaction_storage>
siloBuilder
    .AddAzureTableTransactionalStateStorage(
        "TransactionStore",
        options =>
        {
            options.TableServiceClient = new TableServiceClient(
                builder.Configuration.GetConnectionString("transactions")
                ?? throw new InvalidOperationException(
                    "The transactions connection string isn't configured."));
        })
    .UseTransactions();
            // </configure_transaction_storage>
        }

        internal static void ConfigureTimeouts(ISiloBuilder siloBuilder)
        {
            // <configure_transaction_timeouts>
siloBuilder.Configure<TransactionalStateOptions>(options =>
{
    options.LockAcquireTimeout = TimeSpan.FromSeconds(5);
    options.LockTimeout = TimeSpan.FromSeconds(8);
    options.PrepareTimeout = TimeSpan.FromSeconds(20);
});
            // </configure_transaction_timeouts>
        }
    }

    public interface IAccountGrain : IGrainWithStringKey
    {
        // <transactional_read>
[ReadOnly]
[Transaction(TransactionOption.CreateOrJoin)]
Task<uint> GetBalance();
        // </transactional_read>

        // <exclusive_transactional_read>
[UseExclusiveLock]
[Transaction(TransactionOption.CreateOrJoin)]
Task<uint> ReserveAndGetBalance();
        // </exclusive_transactional_read>
    }
}
