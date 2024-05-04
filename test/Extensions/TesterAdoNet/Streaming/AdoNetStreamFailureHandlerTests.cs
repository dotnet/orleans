using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streaming.AdoNet;
using Orleans.Streams;
using Orleans.Tests.SqlUtils;
using UnitTests.General;
using static System.String;
using RelationalOrleansQueries = Orleans.Streaming.AdoNet.Storage.RelationalOrleansQueries;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for <see cref="AdoNetStreamFailureHandler"/>.
/// </summary>
[TestCategory("AdoNet"), TestCategory("Streaming")]
public class AdoNetStreamFailureHandlerTests() : IAsyncLifetime
{
    private RelationalStorageForTesting _testing;
    private IRelationalStorage _storage;
    private RelationalOrleansQueries _queries;

    private const string TestDatabaseName = "OrleansStreamTest";
    private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameSqlServer;

    public async Task InitializeAsync()
    {
        _testing = await RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName);
        Skip.If(IsNullOrEmpty(_testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        _storage = _testing.Storage;
        _queries = await RelationalOrleansQueries.CreateInstance(AdoNetInvariantName, _testing.CurrentConnectionString);
    }

    /// <summary>
    /// Tests that a <see cref="AdoNetStreamFailureHandler"/> can be constructed.
    /// </summary>
    [SkippableFact]
    public void AdoNetStreamFailureHandler_Constructs()
    {
        // arrange
        var faultOnFailure = false;
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = AdoNetInvariantName,
            ConnectionString = _storage.ConnectionString
        };
        var clusterOptions = new ClusterOptions
        {
            ServiceId = "MyServiceId"
        };
        var mapper = new AdoNetStreamQueueMapper(new HashRingBasedStreamQueueMapper(new HashRingStreamQueueMapperOptions(), "MyQueuePrefix"));
        var logger = NullLogger<AdoNetStreamFailureHandler>.Instance;

        // act
        var handler = new AdoNetStreamFailureHandler(faultOnFailure, streamOptions, clusterOptions, mapper, _queries, logger);

        // assert
        Assert.Equal(faultOnFailure, handler.ShouldFaultSubsriptionOnError);
    }

    /// <summary>
    /// Tests that a <see cref="AdoNetStreamFailureHandler"/> can move a dead message to dead letters.
    /// </summary>
    [SkippableFact]
    public async Task AdoNetStreamFailureHandler_OnDeliveryFailure_MovesExpiredMessageToDeadLetters()
    {
        // arrange - handler
        var providerId = "MyProviderId";
        var faultOnFailure = false;
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = AdoNetInvariantName,
            ConnectionString = _storage.ConnectionString
        };
        var clusterOptions = new ClusterOptions
        {
            ServiceId = "MyServiceId"
        };
        var mapper = new AdoNetStreamQueueMapper(new HashRingBasedStreamQueueMapper(new HashRingStreamQueueMapperOptions(), "MyQueuePrefix"));
        var logger = NullLogger<AdoNetStreamFailureHandler>.Instance;
        var handler = new AdoNetStreamFailureHandler(faultOnFailure, streamOptions, clusterOptions, mapper, _queries, logger);

        // arrange - queue an expired message
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamDeadLetter]");
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var queueId = mapper.GetAdoNetQueueId(streamId);
        var payload = new byte[] { 0xFF };

        var beforeQueued = DateTime.UtcNow;
        var ack = await _queries.QueueStreamMessageAsync(clusterOptions.ServiceId, providerId, queueId, payload, 0);
        var afterQueued = DateTime.UtcNow;

        // act
        var beforeFailure = DateTime.UtcNow;
        await handler.OnDeliveryFailure(GuidId.GetNewGuidId(), providerId, streamId, new EventSequenceTokenV2(ack.MessageId));
        var afterFailure = DateTime.UtcNow;

        // assert
        var dead = Assert.Single(await _storage.ReadAsync<AdoNetStreamDeadLetter>("SELECT * FROM [OrleansStreamDeadLetter]"));
        Assert.Equal(clusterOptions.ServiceId, dead.ServiceId);
        Assert.Equal(providerId, dead.ProviderId);
        Assert.Equal(queueId, dead.QueueId);
        Assert.Equal(ack.MessageId, dead.MessageId);
        Assert.Equal(0, dead.Dequeued);
        Assert.True(dead.ExpiresOn >= beforeQueued);
        Assert.True(dead.ExpiresOn <= afterQueued);
        Assert.True(dead.CreatedOn >= beforeQueued);
        Assert.True(dead.CreatedOn <= afterQueued);
        Assert.True(dead.ModifiedOn >= beforeQueued);
        Assert.True(dead.ModifiedOn <= afterQueued);
        Assert.True(dead.DeadOn >= beforeFailure);
        Assert.True(dead.DeadOn <= afterFailure);
        Assert.True(dead.RemoveOn >= beforeFailure.Add(streamOptions.DeadLetterEvictionTimeout));
        Assert.True(dead.RemoveOn <= afterFailure.Add(streamOptions.DeadLetterEvictionTimeout));
        Assert.Equal(payload, dead.Payload);
    }

    /// <summary>
    /// Tests that a <see cref="AdoNetStreamFailureHandler"/> can move a poisoned message to dead letters.
    /// </summary>
    [SkippableFact]
    public async Task AdoNetStreamFailureHandler_OnDeliveryFailure_MovesPoisonedMessageToDeadLetters()
    {
        // arrange - handler
        var providerId = "MyProviderId";
        var faultOnFailure = false;
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = AdoNetInvariantName,
            ConnectionString = _storage.ConnectionString,
            MaxAttempts = 1
        };
        var cacheOptions = new SimpleQueueCacheOptions();
        var agentOptions = new StreamPullingAgentOptions
        {
            MaxEventDeliveryTime = TimeSpan.FromSeconds(0)
        };
        var clusterOptions = new ClusterOptions
        {
            ServiceId = "MyServiceId"
        };
        var mapper = new AdoNetStreamQueueMapper(new HashRingBasedStreamQueueMapper(new HashRingStreamQueueMapperOptions(), "MyQueuePrefix"));
        var logger = NullLogger<AdoNetStreamFailureHandler>.Instance;
        var handler = new AdoNetStreamFailureHandler(faultOnFailure, streamOptions, clusterOptions, mapper, _queries, logger);

        // arrange - queue an expired message
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamDeadLetter]");
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var queueId = mapper.GetAdoNetQueueId(streamId);
        var payload = new byte[] { 0xFF };

        var beforeQueued = DateTime.UtcNow;
        var ack = await _queries.QueueStreamMessageAsync(clusterOptions.ServiceId, providerId, queueId, payload, streamOptions.ExpiryTimeout.TotalSecondsCeiling());
        var afterQueued = DateTime.UtcNow;

        // arrange - dequeue the message and make immediately available
        var beforeDequeued = DateTime.UtcNow;
        await _queries.GetStreamMessagesAsync(
            ack.ServiceId,
            ack.ProviderId,
            ack.QueueId,
            cacheOptions.CacheSize,
            streamOptions.MaxAttempts,
            agentOptions.MaxEventDeliveryTime.TotalSecondsCeiling(),
            streamOptions.DeadLetterEvictionTimeout.TotalSecondsCeiling(),
            streamOptions.EvictionInterval.TotalSecondsCeiling(),
            streamOptions.EvictionBatchSize);
        var afterDequeued = DateTime.UtcNow;

        // act - clean up with max attempts of one so the message above is flagged
        var beforeFailure = DateTime.UtcNow;
        await handler.OnDeliveryFailure(GuidId.GetNewGuidId(), providerId, streamId, new EventSequenceTokenV2(ack.MessageId));
        var afterFailure = DateTime.UtcNow;

        // assert
        var dead = Assert.Single(await _storage.ReadAsync<AdoNetStreamDeadLetter>("SELECT * FROM [OrleansStreamDeadLetter]"));
        Assert.Equal(clusterOptions.ServiceId, dead.ServiceId);
        Assert.Equal(providerId, dead.ProviderId);
        Assert.Equal(queueId, dead.QueueId);
        Assert.Equal(ack.MessageId, dead.MessageId);
        Assert.Equal(1, dead.Dequeued);
        Assert.True(dead.ExpiresOn >= beforeQueued.Add(streamOptions.ExpiryTimeout.SecondsCeiling()));
        Assert.True(dead.ExpiresOn <= afterQueued.Add(streamOptions.ExpiryTimeout.SecondsCeiling()));
        Assert.True(dead.CreatedOn >= beforeQueued);
        Assert.True(dead.CreatedOn <= afterQueued);
        Assert.True(dead.ModifiedOn >= beforeDequeued);
        Assert.True(dead.ModifiedOn <= afterDequeued);
        Assert.True(dead.DeadOn >= beforeFailure);
        Assert.True(dead.DeadOn <= afterFailure);
        Assert.True(dead.RemoveOn >= beforeFailure.Add(streamOptions.DeadLetterEvictionTimeout.SecondsCeiling()));
        Assert.True(dead.RemoveOn <= afterFailure.Add(streamOptions.DeadLetterEvictionTimeout.SecondsCeiling()));
        Assert.Equal(payload, dead.Payload);
    }

    /// <summary>
    /// Tests that a <see cref="AdoNetStreamFailureHandler"/> can move a poisoned message to dead letters.
    /// </summary>
    [SkippableFact]
    public async Task AdoNetStreamFailureHandler_OnDeliveryFailure_DoesNotMoveHealthyMessageToDeadLetters()
    {
        // arrange - handler
        var providerId = "MyProviderId";
        var faultOnFailure = false;
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = AdoNetInvariantName,
            ConnectionString = _storage.ConnectionString
        };
        var cacheOptions = new SimpleQueueCacheOptions();
        var agentOptions = new StreamPullingAgentOptions();
        var clusterOptions = new ClusterOptions
        {
            ServiceId = "MyServiceId"
        };
        var mapper = new AdoNetStreamQueueMapper(new HashRingBasedStreamQueueMapper(new HashRingStreamQueueMapperOptions(), "MyQueuePrefix"));
        var logger = NullLogger<AdoNetStreamFailureHandler>.Instance;
        var handler = new AdoNetStreamFailureHandler(faultOnFailure, streamOptions, clusterOptions, mapper, _queries, logger);

        // arrange - queue an expired message
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamDeadLetter]");
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var queueId = mapper.GetAdoNetQueueId(streamId);
        var payload = new byte[] { 0xFF };
        var ack = await _queries.QueueStreamMessageAsync(clusterOptions.ServiceId, providerId, queueId, payload, streamOptions.ExpiryTimeout.TotalSecondsCeiling());

        // arrange - dequeue the message and make immediately available
        await _queries.GetStreamMessagesAsync(
            ack.ServiceId,
            ack.ProviderId,
            ack.QueueId,
            cacheOptions.CacheSize,
            streamOptions.MaxAttempts,
            agentOptions.MaxEventDeliveryTime.TotalSecondsCeiling(),
            streamOptions.DeadLetterEvictionTimeout.TotalSecondsCeiling(),
            streamOptions.EvictionInterval.TotalSecondsCeiling(),
            streamOptions.EvictionBatchSize);

        // act - clean up with max attempts of one so the message above is flagged
        await handler.OnDeliveryFailure(GuidId.GetNewGuidId(), providerId, streamId, new EventSequenceTokenV2(ack.MessageId));

        // assert
        Assert.Empty(await _storage.ReadAsync<AdoNetStreamDeadLetter>("SELECT * FROM [OrleansStreamDeadLetter]"));
    }

    public Task DisposeAsync() => Task.CompletedTask;
}