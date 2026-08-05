using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.Runtime.Messaging;
using StackExchange.Redis;

namespace Hosting;

public static class HostingExamples
{
    public static async Task LocalSiloAndClient(string[] args)
    {
        // <local_silo_and_client>
        var builder = WebApplication.CreateBuilder(args);

        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.UseLocalhostClustering();
            siloBuilder.AddMemoryGrainStorageAsDefault();
            siloBuilder.UseInMemoryReminderService();
        });

        var app = builder.Build();

        app.MapGet("/hello/{name}", async (string name, IClusterClient client) =>
            await client.GetGrain<IHelloGrain>(name).SayHello());

        await app.RunAsync();
        // </local_silo_and_client>
    }

    public static async Task LocalExternalClient(string[] args)
    {
        // <local_external_client>
        var builder = Host.CreateApplicationBuilder(args);

        builder.UseOrleansClient(clientBuilder =>
        {
            clientBuilder.UseLocalhostClustering();
        });

        await builder.Build().RunAsync();
        // </local_external_client>
    }

    public static async Task RedisSilo(string[] args)
    {
        // <redis_silo>
        var builder = Host.CreateApplicationBuilder(args);

        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.Configure<ClusterOptions>(options =>
            {
                options.ServiceId = "orders";
                options.ClusterId = "orders-production";
            });

            siloBuilder.UseRedisClustering(options =>
            {
                options.ConfigurationOptions =
                    ConfigurationOptions.Parse(
                        builder.Configuration.GetConnectionString("redis")!);
            });
        });

        await builder.Build().RunAsync();
        // </redis_silo>
    }

    public static void ConfigureAdvertisedAndListeningEndpoints(
        ISiloBuilder siloBuilder)
    {
        // <advertised_and_listening_endpoints>
        siloBuilder.Configure<EndpointOptions>(options =>
        {
            // Addresses that other silos and clients use.
            options.AdvertisedIPAddress = IPAddress.Parse("172.16.0.42");
            options.SiloPort = 11_111;
            options.GatewayPort = 30_000;

            // Sockets opened inside this process or container.
            options.SiloListeningEndpoint =
                new IPEndPoint(IPAddress.Any, 40_000);
            options.GatewayListeningEndpoint =
                new IPEndPoint(IPAddress.Any, 50_000);
        });
        // </advertised_and_listening_endpoints>
    }

    public static void ConfigureExternalClient(
        IHostApplicationBuilder builder)
    {
        // <external_client>
        builder.UseOrleansClient(clientBuilder =>
        {
            clientBuilder.Configure<ClusterOptions>(options =>
            {
                options.ServiceId = "orders";
                options.ClusterId = "orders-production";
            });

            clientBuilder.UseRedisClustering(options =>
            {
                options.ConfigurationOptions =
                    ConfigurationOptions.Parse(
                        builder.Configuration.GetConnectionString("redis")!);
            });
        });
        // </external_client>
    }

    public static void ConfigureClientRetry(
        IHostApplicationBuilder builder)
    {
        // <client_retry>
        var retryCount = 0;

        builder.UseOrleansClient(clientBuilder =>
        {
            clientBuilder
                .UseRedisClustering(options => { /* ... */ })
                .UseConnectionRetryFilter(
                    async (exception, cancellationToken) =>
                    {
                        if (exception is not ConnectionFailedException ||
                            Interlocked.Increment(ref retryCount) > 5)
                        {
                            return false;
                        }

                        await Task.Delay(
                            TimeSpan.FromSeconds(5),
                            cancellationToken);
                        return true;
                    });
        });
        // </client_retry>
    }

    public static void ConfigureAdoNetSilo(
        IHostApplicationBuilder builder)
    {
        // <adonet_silo>
        var connectionString =
            builder.Configuration.GetConnectionString("orleans")
            ?? throw new InvalidOperationException(
                "Connection string 'orleans' is required.");

        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.UseAdoNetClustering(options =>
            {
                options.Invariant = "Microsoft.Data.SqlClient";
                options.ConnectionString = connectionString;
            });

            siloBuilder.UseAdoNetReminderService(options =>
            {
                options.Invariant = "Microsoft.Data.SqlClient";
                options.ConnectionString = connectionString;
            });

            siloBuilder.AddAdoNetGrainStorageAsDefault(options =>
            {
                options.Invariant = "Microsoft.Data.SqlClient";
                options.ConnectionString = connectionString;
            });
        });
        // </adonet_silo>
    }

    public static void ConfigureNamedGrainDirectory(
        ISiloBuilder siloBuilder,
        ConfigurationOptions redisConfiguration)
    {
        // <named_grain_directory>
        siloBuilder.AddRedisGrainDirectory(
            "durable-directory",
            options =>
            {
                options.ConfigurationOptions = redisConfiguration;
            });
        // </named_grain_directory>
    }

    public static void ConfigureGrainTypes(ISiloBuilder siloBuilder)
    {
        // <configure_grain_types>
        siloBuilder.Configure<GrainTypeOptions>(options =>
        {
            options.Classes.Clear();
            options.Classes.Add(typeof(RecommendationGrain));
            options.Classes.Add(typeof(ModelRegistryGrain));
        });
        // </configure_grain_types>
    }

    public static void ConfigureActivationCollection(
        ISiloBuilder siloBuilder)
    {
        // <activation_collection>
        siloBuilder.Configure<GrainCollectionOptions>(options =>
        {
            options.CollectionAge = TimeSpan.FromMinutes(20);
            options.ClassSpecificCollectionAge[
                typeof(ShoppingCartGrain).FullName!] = TimeSpan.FromMinutes(5);

            options.EnableActivationSheddingOnMemoryPressure = true;
            options.MemoryUsageLimitPercentage = 80;
            options.MemoryUsageTargetPercentage = 75;
            options.MemoryUsagePollingPeriod = TimeSpan.FromSeconds(5);
        });
        // </activation_collection>
    }

    public static void ReadSiloMetadata(
        ISiloMetadataCache siloMetadataCache,
        SiloAddress siloAddress,
        ILogger logger)
    {
        // <read_silo_metadata>
        var metadata = siloMetadataCache.GetSiloMetadata(siloAddress);

        if (metadata.Metadata.TryGetValue("role", out var role))
        {
            logger.LogInformation(
                "Silo {Silo} has role {Role}",
                siloAddress,
                role);
        }
        // </read_silo_metadata>
    }
}

public sealed class CacheLifecycleParticipant
    : ILifecycleParticipant<ISiloLifecycle>
{
    private readonly IApplicationCache _cache;

    public CacheLifecycleParticipant(IApplicationCache cache)
    {
        _cache = cache;
    }

    // <lifecycle_participant>
    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe<CacheLifecycleParticipant>(
            ServiceLifecycleStage.ApplicationServices,
            cancellationToken => _cache.StartAsync(cancellationToken),
            cancellationToken => _cache.StopAsync(cancellationToken));
    }
    // </lifecycle_participant>
}

public sealed class GrainPingService : BackgroundService
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<GrainPingService> _logger;

    public GrainPingService(
        IGrainFactory grainFactory,
        ILogger<GrainPingService> logger)
    {
        _grainFactory = grainFactory;
        _logger = logger;
    }

    // <background_service>
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var grain = _grainFactory.GetGrain<IHealthGrain>("background-check");

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await grain.Ping();
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Grain ping failed");
            }
        }
    }
    // </background_service>
}

public interface IApplicationCache
{
    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public interface IHelloGrain : IGrainWithStringKey
{
    Task<string> SayHello();
}

public interface IHealthGrain : IGrainWithStringKey
{
    Task Ping();
}

public interface IShoppingCartGrain : IGrainWithStringKey
{
}

[GrainDirectory("durable-directory")]
public sealed class ShoppingCartGrain : Grain, IShoppingCartGrain
{
}

public interface IRecommendationGrain : IGrainWithStringKey
{
}

public sealed class RecommendationGrain : Grain, IRecommendationGrain
{
}

public interface IModelRegistryGrain : IGrainWithStringKey
{
}

public sealed class ModelRegistryGrain : Grain, IModelRegistryGrain
{
}
