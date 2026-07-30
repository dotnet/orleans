using Azure.Identity;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Timers;

// Reminder configuration examples for documentation

public static class ReminderConfiguration
{
    // <configure_azure_table>
    public static async Task ConfigureAzureTableAsync(string[] args)
    {
        // TODO replace with your connection string
        const string connectionString = "YOUR_CONNECTION_STRING_HERE";

        var builder = Host.CreateApplicationBuilder(args);
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.UseAzureTableReminderService(connectionString);
        });

        using var host = builder.Build();
        await host.RunAsync();
    }
    // </configure_azure_table>

    // <configure_adonet>
    public static async Task ConfigureAdoNetAsync(string[] args)
    {
        const string connectionString = "YOUR_CONNECTION_STRING_HERE";
        const string invariant = "YOUR_INVARIANT";

        var builder = Host.CreateApplicationBuilder(args);
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.UseAdoNetReminderService(options =>
            {
                options.ConnectionString = connectionString; // Redacted
                options.Invariant = invariant;
            });
        });

        using var host = builder.Build();
        await host.RunAsync();
    }
    // </configure_adonet>

    // <configure_inmemory>
    public static async Task ConfigureInMemoryAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.UseInMemoryReminderService();
        });

        using var host = builder.Build();
        await host.RunAsync();
    }
    // </configure_inmemory>

    // <configure_redis>
    public static async Task ConfigureRedisAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.UseRedisReminderService(options =>
            {
                options.ConfigurationOptions = new ConfigurationOptions
                {
                    EndPoints = { "localhost:6379" },
                    AbortOnConnectFail = false
                };
            });
        });

        using var host = builder.Build();
        await host.RunAsync();
    }
    // </configure_redis>

    // <configure_cosmos>
    public static async Task ConfigureCosmosAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.UseCosmosReminderService(options =>
            {
                options.ConfigureCosmosClient(
                    "https://myaccount.documents.azure.com:443/",
                    new DefaultAzureCredential());
                options.DatabaseName = "Orleans";
                options.ContainerName = "OrleansReminders";
                options.IsResourceCreationEnabled = true;
            });
        });

        using var host = builder.Build();
        await host.RunAsync();
    }
    // </configure_cosmos>
}
