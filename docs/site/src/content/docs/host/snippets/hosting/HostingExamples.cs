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
    // <local_silo_and_client>
    public static async Task LocalSiloAndClient(string[] args)
    {
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
    }
    // </local_silo_and_client>

    // <local_external_client>
    public static async Task LocalExternalClient(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.UseOrleansClient(clientBuilder =>
        {
            clientBuilder.UseLocalhostClustering();
        });

        await builder.Build().RunAsync();
    }
    // </local_external_client>

    // <redis_silo>
    public static async Task RedisSilo(string[] args)
    {
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
    }
    // </redis_silo>

    // <advertised_and_listening_endpoints>
    public static void ConfigureAdvertisedAndListeningEndpoints(
        ISiloBuilder siloBuilder)
    {
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
    }
    // </advertised_and_listening_endpoints>

    // <external_client>
    public static async Task RunExternalClient(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

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

        await builder.Build().RunAsync();
    }
    // </external_client>

    // <client_retry>
    public static void ConfigureClientRetry(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
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
    }
    // </client_retry>

    // <adonet_silo>
    public static void ConfigureAdoNetSilo(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
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
    }
    // </adonet_silo>

    // <adonet_client>
    public static void ConfigureAdoNetClient(
        IHostApplicationBuilder builder,
        string connectionString)
    {
        builder.UseOrleansClient(clientBuilder =>
        {
            clientBuilder.UseAdoNetClustering(options =>
            {
                options.Invariant = "Microsoft.Data.SqlClient";
                options.ConnectionString = connectionString;
            });
        });
    }
    // </adonet_client>

    // <named_grain_directory>
    public static void ConfigureNamedGrainDirectory(
        ISiloBuilder siloBuilder,
        ConfigurationOptions redisConfiguration)
    {
        siloBuilder.AddRedisGrainDirectory(
            "durable-directory",
            options =>
            {
                options.ConfigurationOptions = redisConfiguration;
            });
    }
    // </named_grain_directory>

    // <configure_grain_types>
    public static void ConfigureGrainTypes(ISiloBuilder siloBuilder)
    {
        siloBuilder.Configure<GrainTypeOptions>(options =>
        {
            options.Classes.Clear();
            options.Classes.Add(typeof(RecommendationGrain));
            options.Classes.Add(typeof(ModelRegistryGrain));
        });
    }
    // </configure_grain_types>

    // <exclude_grain_type>
    public static void ExcludeGrainType(ISiloBuilder siloBuilder)
    {
        siloBuilder.Configure<GrainTypeOptions>(options =>
        {
            options.Classes.Remove(typeof(RecommendationGrain));
        });
    }
    // </exclude_grain_type>

    // <direct_endpoints>
    public static void ConfigureDirectEndpoints(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureEndpoints(
            advertisedIP: IPAddress.Parse("10.0.0.12"),
            siloPort: 11_111,
            gatewayPort: 30_000,
            listenOnAnyHostAddress: true);
    }
    // </direct_endpoints>

    // <named_providers>
    public static void ConfigureNamedProviders(ISiloBuilder siloBuilder)
    {
        siloBuilder
            .AddRedisGrainStorage(
                "hot-state",
                options => options.ConfigurationOptions =
                    ConfigurationOptions.Parse("localhost:6379"))
            .AddAdoNetGrainStorage("archive", options =>
            {
                options.Invariant = "Microsoft.Data.SqlClient";
                options.ConnectionString =
                    "Server=localhost;Database=Orleans;Integrated Security=true";
            })
            .UseRedisReminderService(
                options => options.ConfigurationOptions =
                    ConfigurationOptions.Parse("localhost:6379"));
    }
    // </named_providers>

    // <membership_options>
    public static void ConfigureMembership(ISiloBuilder siloBuilder)
    {
        siloBuilder.Configure<ClusterMembershipOptions>(options =>
        {
            options.ProbeTimeout = TimeSpan.FromSeconds(10);
        });
    }
    // </membership_options>

    // <activation_collection>
    public static void ConfigureActivationCollection(
        ISiloBuilder siloBuilder)
    {
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
    }
    // </activation_collection>

    // <read_silo_metadata>
    public static void ReadSiloMetadata(
        ISiloMetadataCache siloMetadataCache,
        SiloAddress siloAddress,
        ILogger logger)
    {
        var metadata = siloMetadataCache.GetSiloMetadata(siloAddress);

        if (metadata.Metadata.TryGetValue("role", out var role))
        {
            logger.LogInformation(
                "Silo {Silo} has role {Role}",
                siloAddress,
                role);
        }
    }
    // </read_silo_metadata>

    // <configure_silo_metadata>
    public static void ConfigureSiloMetadata(
        ISiloBuilder siloBuilder,
        string region,
        bool hasGpu)
    {
        siloBuilder.UseSiloMetadata(new Dictionary<string, string>
        {
            ["cloud.region"] = region,
            ["hardware.accelerator"] = hasGpu ? "gpu" : "none",
            ["role"] = "recommendations"
        });
    }
    // </configure_silo_metadata>

    // <silo_metadata_from_configuration>
    public static void ConfigureSiloMetadataFromConfiguration(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.UseSiloMetadata();
        });
    }
    // </silo_metadata_from_configuration>

    // <distributed_grain_directory>
    public static void ConfigureDistributedDirectory(ISiloBuilder siloBuilder)
    {
#pragma warning disable ORLEANSEXP003
        siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
    }
    // </distributed_grain_directory>

    // <register_lifecycle_participant>
    public static void RegisterLifecycleParticipant(ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddSingleton<CacheLifecycleParticipant>();
        siloBuilder.Services.AddSingleton<
            ILifecycleParticipant<ISiloLifecycle>>(
            services =>
                services.GetRequiredService<CacheLifecycleParticipant>());
    }
    // </register_lifecycle_participant>

    // <register_startup_task>
    public static void RegisterStartupTask(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddStartupTask(
            async (services, cancellationToken) =>
            {
                var grainFactory =
                    services.GetRequiredService<IGrainFactory>();
                var grain =
                    grainFactory.GetGrain<IInitializerGrain>("application");
                await grain.Initialize(cancellationToken);
            },
            ServiceLifecycleStage.Active);
    }
    // </register_startup_task>

    // <register_validate_dependencies_task>
    public static void RegisterValidateDependenciesTask(
        ISiloBuilder siloBuilder)
    {
        siloBuilder.AddStartupTask<ValidateDependenciesTask>(
            ServiceLifecycleStage.ApplicationServices);
    }
    // </register_validate_dependencies_task>

    // <register_background_service>
    public static async Task RunBackgroundService(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.UseOrleans(siloBuilder =>
        {
            // Configure Orleans.
        });

        builder.Services.AddHostedService<GrainPingService>();

        await builder.Build().RunAsync();
    }
    // </register_background_service>

    // <run_silo>
    public static async Task RunSilo(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.UseOrleans(siloBuilder =>
        {
            // Configure Orleans.
        });

        await builder.Build().RunAsync();
    }
    // </run_silo>

    // <stop_host>
    public static async Task StopHost(
        IHost host,
        CancellationToken cancellationToken)
    {
        await host.StopAsync(cancellationToken);
        host.Dispose();
    }
    // </stop_host>

    // <shutdown_timeout>
    public static void ConfigureShutdownTimeout(
        IHostApplicationBuilder builder)
    {
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(45);
        });
    }
    // </shutdown_timeout>
}

// <lifecycle_participant>
public sealed class CacheLifecycleParticipant
    : ILifecycleParticipant<ISiloLifecycle>
{
    private readonly IApplicationCache _cache;

    public CacheLifecycleParticipant(IApplicationCache cache)
    {
        _cache = cache;
    }

    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe<CacheLifecycleParticipant>(
            ServiceLifecycleStage.ApplicationServices,
            cancellationToken => _cache.StartAsync(cancellationToken),
            cancellationToken => _cache.StopAsync(cancellationToken));
    }
}
// </lifecycle_participant>

// <background_service>
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
}
// </background_service>

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

public interface IInitializerGrain : IGrainWithStringKey
{
    Task Initialize(CancellationToken cancellationToken);
}

public interface IShoppingCartGrain : IGrainWithStringKey
{
}

// <grain_directory_attribute>
[GrainDirectory("durable-directory")]
public sealed class ShoppingCartGrain : Grain, IShoppingCartGrain
{
}
// </grain_directory_attribute>

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

// <deactivate_on_idle>
public sealed class DeactivateOnIdleGrain : Grain
{
    public void RequestDeactivation()
    {
        this.DeactivateOnIdle();
    }
}
// </deactivate_on_idle>

// <delay_deactivation>
public sealed class DelayDeactivationGrain : Grain
{
    public void DelayCollection()
    {
        this.DelayDeactivation(TimeSpan.FromMinutes(30));
    }
}
// </delay_deactivation>

// <keep_alive_grain>
[KeepAlive]
public sealed class ReferenceDataGrain : Grain
{
}
// </keep_alive_grain>

// <validate_dependencies_task>
public sealed class ValidateDependenciesTask : IStartupTask
{
    private readonly IDependencyValidator _validator;

    public ValidateDependenciesTask(IDependencyValidator validator)
    {
        _validator = validator;
    }

    public Task Execute(CancellationToken cancellationToken) =>
        _validator.ValidateAsync(cancellationToken);
}
// </validate_dependencies_task>

public interface IDependencyValidator
{
    Task ValidateAsync(CancellationToken cancellationToken);
}
