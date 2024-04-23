using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streaming.AdoNet;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;
using static System.String;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapterReceiverTests"/>.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
public class AdoNetQueueAdapterReceiverTests(TestEnvironmentFixture fixture) : IAsyncLifetime
{
    private readonly TestEnvironmentFixture _fixture = fixture;
    private RelationalStorageForTesting _storage;
    private RelationalOrleansQueries _queries;

    private const string TestDatabaseName = "OrleansStreamTest";
    private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameSqlServer;

    public async Task InitializeAsync()
    {
        _storage = await RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName);

        Skip.If(IsNullOrEmpty(_storage.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        _queries = await RelationalOrleansQueries.CreateInstance(AdoNetInvariantName, _storage.CurrentConnectionString);
    }

    [SkippableFact]
    public async Task AdoNetQueueAdapterReceiver_GetsMessages()
    {
        // arrange - receiver
        var clusterOptions = new ClusterOptions
        {
            ServiceId = "MyServiceId",
            ClusterId = "MyClusterId"
        };
        var providerId = "MyProviderId";
        var queueId = 1;
        var adoNetStreamingOptions = new AdoNetStreamingOptions
        {
            Invariant = AdoNetInvariantName,
            ConnectionString = _storage.CurrentConnectionString
        };
        var serializer = _fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var logger = NullLogger<AdoNetQueueAdapterReceiver>.Instance;
        var receiver = new AdoNetQueueAdapterReceiver(providerId, queueId, clusterOptions, adoNetStreamingOptions, serializer, logger);
        await receiver.Initialize(TimeSpan.FromSeconds(10));

        // arrange - data
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var events = new List<object> { new TestModel(1), new TestModel(2), new TestModel(3) };
        var context = new Dictionary<string, object> { { "MyKey", "MyValue" } };
        var container = new AdoNetBatchContainer(streamId, events, context);
        var payload = serializer.SerializeToArray(container);
        var ack = await _queries.QueueMessageBatchAsync(clusterOptions.ServiceId, providerId, queueId, payload, 100);

        // act
        var messages = await receiver.GetQueueMessagesAsync(10);

        // assert
        Assert.NotNull(messages);
        var single = Assert.Single(messages);
        var typed = Assert.IsType<AdoNetBatchContainer>(single);
        Assert.Equal(streamId, typed.StreamId);
        Assert.Equal(events, typed.Events);
        Assert.Equal(context.Select(x => (x.Key, x.Value)), typed.RequestContext.Select(x => (x.Key, x.Value)));
        Assert.Equal(ack.MessageId, typed.SequenceToken.SequenceNumber);
        Assert.Equal(1, typed.Dequeued);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [GenerateSerializer]
    [Alias("Tester.AdoNet.Streaming.AdoNetQueueAdapterReceiverTests.TestModel")]
    public record TestModel(
        [property: Id(0)] int Value);
}