using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Orleans.Hosting;
using Orleans.Streaming.NATS;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace NATS.Tests;

[TestSuite("BVT")]
[TestArea("Streaming")]
[TestProvider("NATS")]
[TestCategory("NATS"), TestCategory("Streaming")]
public sealed class NatsAspireAppModelTests
{
    [Fact]
    public async Task AspireAppModel_ActivatesSiloAndClientProvidersUsingKeyedConnections()
    {
        await using var model = await NatsAspireTestModel.CreateAsync("orders-stream");

        Assert.Equal("NatsServer", model.SiloEnvironment["Orleans:Streaming:orders:ProviderType"]);
        Assert.Equal("NatsServer", model.ClientEnvironment["Orleans:Streaming:orders:ProviderType"]);
        Assert.Equal("nats", model.SiloEnvironment["Orleans:Streaming:orders:ServiceKey"]);
        Assert.Equal("nats", model.ClientEnvironment["Orleans:Streaming:orders:ServiceKey"]);

        using var siloHost = BuildHost(model.SiloEnvironment, isSilo: true);
        using var clientHost = BuildHost(model.ClientEnvironment, isSilo: false);

        AssertProviderActivation(siloHost.Services);
        AssertProviderActivation(clientHost.Services);
    }

    private static IHost BuildHost(IReadOnlyDictionary<string, string?> environment, bool isSilo)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(environment);
        builder.Services.AddKeyedSingleton<INatsConnection>(
            "nats",
            new NatsConnection(NatsTestConstants.NatsClientOptions));
        if (isSilo)
        {
            builder.UseOrleans();
        }
        else
        {
            builder.UseOrleansClient();
        }

        return builder.Build();
    }

    private static void AssertProviderActivation(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptionsMonitor<NatsOptions>>().Get("orders");
        var connection = services.GetRequiredKeyedService<INatsConnection>("nats");

        Assert.Equal("orders-stream", options.StreamName);
        Assert.Same(connection, options.Connection);
    }
}

[TestSuite("Functional")]
[TestArea("Streaming")]
[TestProvider("NATS")]
[TestCategory("NATS"), TestCategory("Streaming")]
public sealed class NatsAspireJetStreamTests
{
    [Fact]
    public async Task AspireConfiguration_StreamsThroughLiveJetStream()
    {
        if (!NatsTestConstants.IsNatsAvailable)
        {
            throw Xunit.Sdk.SkipException.ForSkip("NATS JetStream is not available.");
        }

        var streamName = $"aspire-{Guid.NewGuid():N}";
        await using var model = await NatsAspireTestModel.CreateAsync(streamName);
        model.SiloEnvironment["ConnectionStrings:nats"] = NatsTestConstants.NatsClientOptions.Url;
        model.ClientEnvironment["ConnectionStrings:nats"] = NatsTestConstants.NatsClientOptions.Url;

        var clusterBuilder = new InProcessTestClusterBuilder(1);
        clusterBuilder.ConfigureSiloHost((_, hostBuilder) =>
        {
            hostBuilder.Configuration.AddInMemoryCollection(model.SiloEnvironment);
            hostBuilder.AddKeyedNatsClient("nats");
        });
        clusterBuilder.ConfigureClientHost(hostBuilder =>
        {
            hostBuilder.Configuration.AddInMemoryCollection(model.ClientEnvironment);
            hostBuilder.AddKeyedNatsClient("nats");
        });
        clusterBuilder.ConfigureSilo((_, siloBuilder) =>
            siloBuilder
                .AddMemoryGrainStorage("PubSubStore")
                .AddMemoryGrainStorage("MemoryStore"));

        await DeleteStreamIfPresent(streamName);
        await using var cluster = clusterBuilder.Build();
        try
        {
            await cluster.DeployAsync();
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = cluster.Client
                .GetStreamProvider("orders")
                .GetStream<string>("aspire", Guid.NewGuid());
            await stream.SubscribeAsync((value, _) =>
            {
                received.TrySetResult(value);
                return Task.CompletedTask;
            });

            await stream.OnNextAsync("through-jetstream");

            Assert.Equal("through-jetstream", await received.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await DeleteStreamIfPresent(streamName);
        }
    }

    private static async Task DeleteStreamIfPresent(string streamName)
    {
        await using var connection = NatsTestConstants.CreateConnection();
        var context = new NatsJSContext(connection);
        await connection.ConnectAsync();

        try
        {
            var stream = await context.GetStreamAsync(streamName);
            await stream.DeleteAsync();
        }
        catch (NatsJSApiException)
        {
        }
    }
}

internal sealed class NatsAspireTestModel : IAsyncDisposable
{
    private readonly DistributedApplication _app;

    private NatsAspireTestModel(
        DistributedApplication app,
        Dictionary<string, string?> siloEnvironment,
        Dictionary<string, string?> clientEnvironment)
    {
        _app = app;
        SiloEnvironment = siloEnvironment;
        ClientEnvironment = clientEnvironment;
    }

    public Dictionary<string, string?> SiloEnvironment { get; }

    public Dictionary<string, string?> ClientEnvironment { get; }

    public static async Task<NatsAspireTestModel> CreateAsync(string streamName)
    {
        var builder = DistributedApplicationTestingBuilder.Create();
        var nats = builder.AddNats("nats").WithJetStream();
        var orleans = builder.AddOrleans("cluster")
            .WithDevelopmentClustering()
            .WithStreaming("orders", nats);
        var silo = builder.AddContainer("silo", "unused")
            .WithReference(orleans)
            .WithEnvironment("Orleans__Streaming__orders__StreamName", streamName);
        var client = builder.AddContainer("client", "unused")
            .WithReference(orleans.AsClient())
            .WithEnvironment("Orleans__Streaming__orders__StreamName", streamName);

        var app = await builder.BuildAsync();
        var siloEnvironment = await GetEnvironmentVariablesAsync(silo.Resource, app.Services);
        var clientEnvironment = await GetEnvironmentVariablesAsync(client.Resource, app.Services);
        return new NatsAspireTestModel(app, siloEnvironment, clientEnvironment);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static async Task<Dictionary<string, string?>> GetEnvironmentVariablesAsync(
        IResource resource,
        IServiceProvider services)
    {
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)
            {
                ServiceProvider = services,
            });
        var values = new Dictionary<string, object>();
        var callbackContext = new EnvironmentCallbackContext(executionContext, resource, values);

        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(callbackContext);
        }

        var valueContext = new ValueProviderContext
        {
            Caller = resource,
            ExecutionContext = executionContext,
            Network = KnownNetworkIdentifiers.LocalhostNetwork,
        };
        var result = new Dictionary<string, string?>();
        foreach (var (key, value) in values)
        {
            if (!key.StartsWith("Orleans__", StringComparison.Ordinal))
            {
                continue;
            }

            var normalizedKey = key.Replace("__", ":", StringComparison.Ordinal);
            result[normalizedKey] = value switch
            {
                IValueProvider provider => await provider.GetValueAsync(valueContext),
                _ => value.ToString(),
            };
        }

        return result;
    }
}
