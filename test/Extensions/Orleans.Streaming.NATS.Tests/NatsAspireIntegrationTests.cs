using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Orleans.Persistence.FileStorage;
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
    public async Task AspireAppModel_ProducesSiloAndClientProviderConfiguration()
    {
        await using var model = await NatsAspireTestModel.CreateAsync("orders-stream");

        Assert.Equal("NatsServer", model.SiloEnvironment["Orleans:Streaming:orders:ProviderType"]);
        Assert.Equal("NatsServer", model.ClientEnvironment["Orleans:Streaming:orders:ProviderType"]);
        Assert.Equal("nats", model.SiloEnvironment["Orleans:Streaming:orders:ServiceKey"]);
        Assert.Equal("nats", model.ClientEnvironment["Orleans:Streaming:orders:ServiceKey"]);
        Assert.Equal("orders-stream", model.SiloEnvironment["Orleans:Streaming:orders:StreamName"]);
        Assert.Equal("orders-stream", model.ClientEnvironment["Orleans:Streaming:orders:StreamName"]);
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
        await using var model = await NatsAspireTestModel.CreateAsync(
            streamName,
            NatsTestConstants.NatsClientOptions.Url);

        var clusterBuilder = new InProcessTestClusterBuilder(1);
        var storageDirectory = Path.Combine(
            Path.GetTempPath(),
            "orleans-nats-aspire",
            Guid.NewGuid().ToString("N"));
        clusterBuilder.ConfigureSiloHost((_, hostBuilder) =>
        {
            hostBuilder.Configuration.AddConfiguration(model.SiloEnvironment);
            hostBuilder.AddKeyedNatsClient("nats");
        });
        clusterBuilder.ConfigureClientHost(hostBuilder =>
        {
            hostBuilder.Configuration.AddConfiguration(model.ClientEnvironment);
            hostBuilder.AddKeyedNatsClient("nats");
        });
        clusterBuilder.ConfigureSilo((_, siloBuilder) =>
            siloBuilder.AddFileGrainStorage(
                "PubSubStore",
                options => options.RootDirectory = storageDirectory));

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
            if (Directory.Exists(storageDirectory))
            {
                Directory.Delete(storageDirectory, recursive: true);
            }
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
        IConfigurationRoot siloEnvironment,
        IConfigurationRoot clientEnvironment)
    {
        _app = app;
        SiloEnvironment = siloEnvironment;
        ClientEnvironment = clientEnvironment;
    }

    public IConfigurationRoot SiloEnvironment { get; }

    public IConfigurationRoot ClientEnvironment { get; }

    public static async Task<NatsAspireTestModel> CreateAsync(
        string streamName,
        string? connectionString = null)
    {
        var builder = DistributedApplicationTestingBuilder.Create();
        var nats = builder.AddNats("nats").WithJetStream();
        var orleans = builder.AddOrleans("cluster")
            .WithDevelopmentClustering()
            .WithStreaming("orders", nats);
        var silo = builder.AddContainer("silo", "unused")
            .WithReference(orleans)
            .WithEnvironment("Orleans__Streaming__orders__StreamName", streamName)
            .WithEnvironment("ConnectionStrings__nats", connectionString);
        var client = builder.AddContainer("client", "unused")
            .WithReference(orleans.AsClient())
            .WithEnvironment("Orleans__Streaming__orders__StreamName", streamName)
            .WithEnvironment("ConnectionStrings__nats", connectionString);

        var app = await builder.BuildAsync();
        var siloEnvironment = await AspireResourceConfiguration.CreateAsync(
            silo.Resource,
            app.Services,
            include: IsRelevantEnvironmentVariable);
        var clientEnvironment = await AspireResourceConfiguration.CreateAsync(
            client.Resource,
            app.Services,
            include: IsRelevantEnvironmentVariable);
        return new NatsAspireTestModel(app, siloEnvironment, clientEnvironment);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static bool IsRelevantEnvironmentVariable(string key)
        => key.StartsWith("Orleans__Streaming__", StringComparison.Ordinal)
            || key.StartsWith("ConnectionStrings__", StringComparison.Ordinal);
}
