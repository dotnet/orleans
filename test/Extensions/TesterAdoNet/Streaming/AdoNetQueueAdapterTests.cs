using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streaming.AdoNet;
using Orleans.Tests.SqlUtils;
using Tester.AdoNet.Fakes;
using TestExtensions;
using UnitTests.General;
using static System.String;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapter"/>.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
public class AdoNetQueueAdapterTests(TestEnvironmentFixture fixture) : IAsyncLifetime
{
    private readonly TestEnvironmentFixture _fixture = fixture;
    private RelationalStorageForTesting _testing;
    private IRelationalStorage _storage;

    private const string TestDatabaseName = "OrleansStreamTest";
    private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameSqlServer;

    public async Task InitializeAsync()
    {
        _testing = await RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName);
        Skip.If(IsNullOrEmpty(_testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        _storage = _testing.Storage;
    }

    /// <summary>
    /// Tests that the <see cref="AdoNetQueueAdapter"/> can enqueue messages.
    /// </summary>
    [SkippableFact]
    public async Task AdoNetQueueAdapter_EnqueuesMessages()
    {
        // arrange - receiver
        var serviceId = "MyServiceId";
        var clusterOptions = Options.Create(new ClusterOptions
        {
            ServiceId = serviceId
        });
        var providerId = "MyProviderId";
        var adoNetStreamingOptions = new AdoNetStreamingOptions
        {
            Invariant = AdoNetInvariantName,
            ConnectionString = _storage.ConnectionString,
            ExpiryTimeout = 100
        };
        var serializer = _fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var logger = NullLogger<AdoNetQueueAdapter>.Instance;
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var mapper = new FakeConsistentRingStreamQueueMapper();
        var queueId = mapper.GetQueueForStream(streamId).ToString();
        var receiverFactory = new FakeAdoNetQueueAdapterReceiverFactory();
        var adapter = new AdoNetQueueAdapter(providerId, adoNetStreamingOptions, logger, mapper, serializer, receiverFactory, clusterOptions);
        var context = new Dictionary<string, object> { { "MyKey", "MyValue" } };

        // act - enqueue (via adapter) some messages
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");
        var beforeEnqueued = DateTime.UtcNow;
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(1) }, null, context);
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(2) }, null, context);
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(3) }, null, context);
        var afterEnqueued = DateTime.UtcNow;

        // assert - stored messages are as expected
        var stored = (await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM [OrleansStreamMessage]")).ToList();
        for (var i = 0; i < stored.Count; i++)
        {
            var item = stored[i];

            Assert.Equal(serviceId, item.ServiceId);
            Assert.Equal(providerId, item.ProviderId);
            Assert.Equal(queueId, item.QueueId);
            Assert.NotEqual(0, item.MessageId);
            Assert.Equal(0, item.Dequeued);
            Assert.True(item.VisibleOn >= beforeEnqueued);
            Assert.True(item.VisibleOn <= afterEnqueued);
            Assert.True(item.ExpiresOn >= beforeEnqueued.AddSeconds(adoNetStreamingOptions.ExpiryTimeout));
            Assert.True(item.ExpiresOn <= afterEnqueued.AddSeconds(adoNetStreamingOptions.ExpiryTimeout));
            Assert.Equal(item.VisibleOn, item.CreatedOn);
            Assert.Equal(item.VisibleOn, item.ModifiedOn);

            var serializedContainer = serializer.Deserialize(item.Payload);
            Assert.Equal(streamId, serializedContainer.StreamId);
            Assert.Null(serializedContainer.SequenceToken);
            Assert.Equal(new[] { new TestModel(i + 1) }, serializedContainer.Events);
            Assert.Single(serializedContainer.RequestContext);
            Assert.Equal("MyValue", serializedContainer.RequestContext["MyKey"]);
            Assert.Equal(0, serializedContainer.Dequeued);
        }
    }

    /// <summary>
    /// Tests that the <see cref="AdoNetQueueAdapter"/> works together with created receivers.
    /// </summary>
    [SkippableFact]
    public void AdoNetQueueAdapter_CreatesReceiver()
    {
        // arrange - receiver
        var serviceId = "MyServiceId";
        var clusterOptions = Options.Create(new ClusterOptions
        {
            ServiceId = serviceId
        });
        var providerId = "MyProviderId";
        var adoNetStreamingOptions = new AdoNetStreamingOptions
        {
            Invariant = AdoNetInvariantName,
            ConnectionString = _storage.ConnectionString,
            ExpiryTimeout = 100
        };
        var serializer = _fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var logger = NullLogger<AdoNetQueueAdapter>.Instance;
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var mapper = new FakeConsistentRingStreamQueueMapper();
        var queueId = mapper.GetQueueForStream(streamId);
        var adoNetQueueId = queueId.ToString();
        var receiver = new FakeAdoNetQueueAdapterReceiver(providerId, adoNetQueueId, adoNetStreamingOptions);
        var receiverFactory = new FakeAdoNetQueueAdapterReceiverFactory(create: (providerIdArg, queueIdArg, adoNetStreamingOptionsArg) =>
        {
            Assert.Equal(providerId, providerIdArg);
            Assert.Equal(adoNetQueueId, queueIdArg);
            Assert.Same(adoNetStreamingOptions, adoNetStreamingOptionsArg);

            return receiver;
        });
        var adapter = new AdoNetQueueAdapter(providerId, adoNetStreamingOptions, logger, mapper, serializer, receiverFactory, clusterOptions);
        var context = new Dictionary<string, object> { { "MyKey", "MyValue" } };

        // act - create receiver
        var result = adapter.CreateReceiver(queueId);

        // assert - receiver created
        Assert.Same(receiver, result);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [GenerateSerializer]
    [Alias("Tester.AdoNet.Streaming.AdoNetQueueAdapterTests.TestModel")]
    public record TestModel(
        [property: Id(0)] int Value);
}