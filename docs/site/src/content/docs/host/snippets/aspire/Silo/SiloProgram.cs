using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Orleans.Docs.Snippets.Aspire.Silo;

// This class contains example code for Orleans silo configuration with Aspire
public static class SiloProgram
{
    // <silo_basic_config>
    public static void BasicSiloConfiguration(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Add Aspire service defaults (OpenTelemetry, health checks, etc.)
        builder.AddServiceDefaults();

        // Add the Aspire Redis client for Orleans
        builder.AddKeyedRedisClient("orleans-redis");

        // Configure Orleans - Aspire injects all configuration automatically
        builder.UseOrleans();

        builder.Build().Run();
    }
    // </silo_basic_config>

    // <silo_explicit_connection>
    public static void ExplicitConnectionConfiguration(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.AddServiceDefaults();
        builder.AddKeyedRedisClient("orleans-redis");

        builder.UseOrleans(siloBuilder =>
        {
            var redisConnectionString = builder.Configuration.GetConnectionString("orleans-redis");

            siloBuilder.UseRedisClustering(options =>
            {
                options.ConfigurationOptions =
                    ConfigurationOptions.Parse(redisConnectionString!);
            });

            siloBuilder.AddRedisGrainStorageAsDefault(options =>
            {
                options.ConfigurationOptions =
                    ConfigurationOptions.Parse(redisConnectionString!);
            });
        });

        builder.Build().Run();
    }
    // </silo_explicit_connection>

    // <silo_azure_config>
    public static void AzureStorageConfiguration(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.AddServiceDefaults();
        builder.AddKeyedAzureTableServiceClient("orleans-tables");
        builder.UseOrleans();

        builder.Build().Run();
    }
    // </silo_azure_config>

    // <health_checks>
    public static void ConfigureHealthChecks(IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks()
            .AddCheck<GrainHealthCheck>("orleans-grains")
            .AddCheck<SiloHealthCheck>("orleans-silo");
    }
    // </health_checks>

    // <reminders_redis_silo>
    public static void RemindersRedisSilo(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.AddServiceDefaults();
        builder.AddKeyedRedisClient("redis");
        builder.UseOrleans();

        builder.Build().Run();
    }
    // </reminders_redis_silo>

    // <reminders_azure_table_silo>
    public static void RemindersAzureTableSilo(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.AddServiceDefaults();
        builder.AddKeyedAzureTableServiceClient("reminders");
        builder.UseOrleans();

        builder.Build().Run();
    }
    // </reminders_azure_table_silo>

    // <reminders_inmemory_silo>
    public static void RemindersInMemorySilo(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.AddServiceDefaults();
        builder.UseOrleans();

        builder.Build().Run();
    }
    // </reminders_inmemory_silo>

    // <adonet_silo>
    public static void AdoNetSilo(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.AddServiceDefaults();

        // Configure Orleans manually because Aspire cannot automatically wire ADO.NET
        // providers — provider type inference produces "SqlServerDatabase" instead of
        // the "AdoNet" provider name Orleans expects.
        builder.UseOrleans(siloBuilder =>
        {
            var connectionString = builder.Configuration.GetConnectionString("orleans-db")!;

            siloBuilder.UseAdoNetClustering(options =>
            {
                options.Invariant = "Microsoft.Data.SqlClient";
                options.ConnectionString = connectionString;
            });

            siloBuilder.AddAdoNetGrainStorageAsDefault(options =>
            {
                options.Invariant = "Microsoft.Data.SqlClient";
                options.ConnectionString = connectionString;
            });

            siloBuilder.UseAdoNetReminderService(options =>
            {
                options.Invariant = "Microsoft.Data.SqlClient";
                options.ConnectionString = connectionString;
            });
        });

        builder.Build().Run();
    }
    // </adonet_silo>

    // <grain_directory_silo>
    public static void GrainDirectorySilo(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.AddServiceDefaults();
        builder.AddKeyedRedisClient("orleans-redis");
        builder.UseOrleans();

        builder.Build().Run();
    }
    // </grain_directory_silo>
}

// Stub health check classes for documentation examples
// In a real application, you would implement actual health check logic

internal class GrainHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(HealthCheckResult.Healthy());
}

internal class SiloHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(HealthCheckResult.Healthy());
}
