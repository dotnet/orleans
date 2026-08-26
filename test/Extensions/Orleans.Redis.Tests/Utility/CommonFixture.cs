using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Persistence;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using StackExchange.Redis;
using Tester;
using TestExtensions;
using Xunit;

public class CommonFixture : TestEnvironmentFixture
{
    /// <summary>
    /// Caches DefaultProviderRuntime for multiple uses.
    /// </summary>
    private IProviderRuntime DefaultProviderRuntime { get; }

    /// <summary>
    /// Constructor.
    /// </summary>
    public CommonFixture()
    {
        _ = this.Services.GetRequiredService<IOptions<ClusterOptions>>();
        DefaultProviderRuntime = new ClientProviderRuntime(
            this.InternalGrainFactory,
            this.Services,
            this.Services.GetRequiredService<ClientGrainContext>());
    }

    /// <summary>
    /// Returns a correct implementation of the persistence provider according to environment variables.
    /// </summary>
    /// <remarks>If the environment invariants have failed to hold upon creation of the storage provider,
    /// a <em>null</em> value will be provided.</remarks>
    public async Task<IGrainStorage> CreateRedisGrainStorage(
        bool useOrleansSerializer = false,
        bool deleteStateOnClear = false,
        CancellationToken cancellationToken = default)
    {
        TestUtils.CheckForRedis();
        IGrainStorageSerializer grainStorageSerializer = useOrleansSerializer ? new OrleansGrainStorageSerializer(this.DefaultProviderRuntime.ServiceProvider.GetService<Serializer>()!)
                                                                              : new JsonGrainStorageSerializer(this.DefaultProviderRuntime.ServiceProvider.GetService<OrleansJsonSerializer>()!);
        var options = new RedisStorageOptions
        {
            ConfigurationOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString!),
            GrainStorageSerializer = grainStorageSerializer,
            DeleteStateOnClear = deleteStateOnClear,
        };
        var connectTask = ConnectionMultiplexer.ConnectAsync(options.ConfigurationOptions);
        ConnectionMultiplexer connection;
        try
        {
            connection = await connectTask.WaitAsync(cancellationToken);
        }
        catch
        {
            _ = connectTask.ContinueWith(
                static task =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        task.Result.Dispose();
                    }
                    else
                    {
                        _ = task.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }

        options.CreateMultiplexer = _ => Task.FromResult((
            Multiplexer: (IConnectionMultiplexer)connection,
            IsShared: false));

        var clusterOptions = new ClusterOptions()
        {
            ServiceId = Guid.NewGuid().ToString()
        };

        var serviceProvider = DefaultProviderRuntime.ServiceProvider;
        var storageProvider = new RedisGrainStorage(
            string.Empty,
            options,
            grainStorageSerializer,
            Options.Create(clusterOptions),
            serviceProvider.GetRequiredService<IActivatorProvider>(),
            serviceProvider.GetRequiredService<ILogger<RedisGrainStorage>>());
        ISiloLifecycleSubject siloLifeCycle = new SiloLifecycleSubject(NullLoggerFactory.Instance.CreateLogger<SiloLifecycleSubject>());
        storageProvider.Participate(siloLifeCycle);
        try
        {
            await siloLifeCycle.OnStart(cancellationToken);
            return storageProvider;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}