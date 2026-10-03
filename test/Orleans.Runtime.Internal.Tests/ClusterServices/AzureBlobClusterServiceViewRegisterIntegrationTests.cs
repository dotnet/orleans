using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Orleans.Persistence.AzureStorage;
using Orleans.Runtime.ClusterServices;
using TestExtensions;
using Xunit;

namespace UnitTests.ClusterServices;

[TestArea("Runtime")]
[TestCategory("BVT"), TestCategory("AzureStorage")]
[TestSuite("BVT")]
[TestProvider("AzureStorage")]
public sealed class AzureBlobClusterServiceViewRegisterIntegrationTests : IAsyncDisposable
{
    private const string BlobName = "authoritative-view.json";
    private readonly string _connectionString;
    private readonly BlobContainerClient _container;

    public AzureBlobClusterServiceViewRegisterIntegrationTests()
    {
        var connectionString = Environment.GetEnvironmentVariable("ORLEANSDATACONNECTIONSTRING")
            ?? TestDefaultConfiguration.DataConnectionString;
        Assert.False(string.IsNullOrWhiteSpace(connectionString),
            "AzureStorage integration tests require ORLEANSDATACONNECTIONSTRING or the configured DataConnectionString.");
        _connectionString = connectionString!;
        _container = new BlobServiceClient(_connectionString).GetBlobContainerClient($"service-view-{Guid.NewGuid():N}");
    }

    [Fact(Timeout = 60_000)]
    public async Task ConcurrentBlobCreatorsPersistExactlyOneCanonicalSnapshot()
    {
        await _container.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var firstRegister = CreateRegister();
        var secondRegister = CreateRegister();
        Assert.Null((await firstRegister.ReadAsync(TestContext.Current.CancellationToken)).View);
        Assert.Null((await secondRegister.ReadAsync(TestContext.Current.CancellationToken)).View);
        var firstProposal = RegisteredClusterServiceViewProviderTests.MakeView(1, TestServiceMembership.A);
        var secondProposal = RegisteredClusterServiceViewProviderTests.MakeView(1, TestServiceMembership.B);
        var options = CreateRaceOptions(out var race);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = WriteAfterStart(CreateRegister(options), firstProposal, null, start.Task);
        var second = WriteAfterStart(CreateRegister(options), secondProposal, null, start.Task);

        start.SetResult();
        var outcomes = await Task.WhenAll(first, second).WaitAsync(TestContext.Current.CancellationToken);

        var writtenToken = Assert.Single(outcomes, static token => token is not null);
        Assert.Single(outcomes, static token => token is null);
        Assert.Equal(2, race.ConditionalWrites);
        Assert.Equal(1, race.FailedConditions);
        var persisted = await CreateRegister().ReadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(persisted.View);
        Assert.True((outcomes[0] is not null ? firstProposal : secondProposal).HasSameContent(persisted.View));
        Assert.Equal(writtenToken, persisted.Token);
        Assert.Null(persisted.View.Predecessor);
        Assert.False(string.IsNullOrWhiteSpace(persisted.Token));
        var properties = await _container.GetBlobClient(BlobName).GetPropertiesAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(properties.Value.ETag.ToString(), persisted.Token);
        Assert.Null(await CreateRegister().TryWriteAsync(outcomes[0] is not null ? secondProposal : firstProposal, null, TestContext.Current.CancellationToken));
        var reopened = await CreateRegister().ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(persisted.Token, reopened.Token);
        Assert.True(persisted.View.HasSameContent(reopened.View!));
    }

    [Fact(Timeout = 60_000)]
    public async Task ConditionalBlobUpdatesPreserveEtagAndAuthoritativePredecessorAcrossReopen()
    {
        await _container.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var initial = RegisteredClusterServiceViewProviderTests.MakeView(1, TestServiceMembership.A);
        Assert.NotNull(await CreateRegister().TryWriteAsync(initial, null, TestContext.Current.CancellationToken));
        var previous = await CreateRegister().ReadAsync(TestContext.Current.CancellationToken);
        var firstProposal = RegisteredClusterServiceViewProviderTests.MakeView(2, TestServiceMembership.B, initial.Id);
        var secondProposal = RegisteredClusterServiceViewProviderTests.MakeView(2, TestServiceMembership.A, initial.Id);
        var options = CreateRaceOptions(out var race);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = WriteAfterStart(CreateRegister(options), firstProposal, previous.Token, start.Task);
        var second = WriteAfterStart(CreateRegister(options), secondProposal, previous.Token, start.Task);

        start.SetResult();
        var outcomes = await Task.WhenAll(first, second).WaitAsync(TestContext.Current.CancellationToken);
        var winner = await CreateRegister().ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(winner.Token, Assert.Single(outcomes, static token => token is not null));
        Assert.Single(outcomes, static token => token is null);
        Assert.Equal(2, race.ConditionalWrites);
        Assert.Equal(1, race.FailedConditions);
        Assert.NotNull(winner.View);
        Assert.True((outcomes[0] is not null ? firstProposal : secondProposal).HasSameContent(winner.View));
        Assert.Equal(initial.Id, winner.View.Predecessor);
        Assert.NotEqual(previous.Token, winner.Token);
        Assert.Null(await CreateRegister().TryWriteAsync(firstProposal, previous.Token, TestContext.Current.CancellationToken));
        Assert.Equal(winner.Token, (await CreateRegister().ReadAsync(TestContext.Current.CancellationToken)).Token);
        var conflicting = outcomes[0] is not null ? secondProposal : firstProposal;
        await Assert.ThrowsAsync<ClusterServiceAuthorityException>(() =>
            CreateRegister().TryWriteAsync(conflicting, winner.Token, TestContext.Current.CancellationToken).AsTask());
        var wrongPredecessor = RegisteredClusterServiceViewProviderTests.MakeView(3, TestServiceMembership.A, initial.Id);
        await Assert.ThrowsAsync<ClusterServiceAuthorityException>(() =>
            CreateRegister().TryWriteAsync(wrongPredecessor, winner.Token, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(winner.Token, (await CreateRegister().ReadAsync(TestContext.Current.CancellationToken)).Token);

        await using var provider = new RegisteredClusterServiceViewProvider(
            "service", "authority", CreateRegister(), new TestServiceMembership(), pollInterval: TimeSpan.FromDays(1));
        var published = await RegisteredClusterServiceViewProviderTests.Publish(provider, TestServiceMembership.B);
        var reopened = await CreateRegister().ReadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(reopened.View);
        Assert.Equal(3, published.Id.Revision);
        Assert.Equal(winner.View.Id, published.Predecessor);
        Assert.NotEqual(winner.Token, reopened.Token);
        Assert.True(published.HasSameContent(reopened.View));
        Assert.Equal(initial.MembershipWatermark, reopened.View.MembershipWatermark);
    }

    [Fact(Timeout = 60_000)]
    public async Task RecreatedBlobWithTheSameViewRequiresExplicitAuthorityBootstrap()
    {
        await _container.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var provider = new RegisteredClusterServiceViewProvider(
            "service", "authority", CreateRegister(), new TestServiceMembership(), pollInterval: TimeSpan.FromDays(1));
        var original = await RegisteredClusterServiceViewProviderTests.Publish(provider, TestServiceMembership.A);
        var previous = await CreateRegister().ReadAsync(TestContext.Current.CancellationToken);

        await _container.GetBlobClient(BlobName).DeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(await CreateRegister().TryWriteAsync(original, null, TestContext.Current.CancellationToken));
        var recreated = await CreateRegister().ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(previous.Token, recreated.Token);
        Assert.True(original.HasSameContent(recreated.View!));
        await Assert.ThrowsAsync<ClusterServiceAuthorityException>(() => provider.RefreshAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.False(provider.TryGetCurrentView(out _));
    }

    public async ValueTask DisposeAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _container.DeleteIfExistsAsync(cancellationToken: cleanup.Token);
    }

    private AzureBlobClusterServiceViewRegister CreateRegister(BlobClientOptions? options = null) =>
        new("service", "authority", _connectionString, _container.Name, BlobName, options);

    private static BlobClientOptions CreateRaceOptions(out PrimaryReadBarrier policy)
    {
        policy = new();
        var options = new BlobClientOptions { Retry = { MaxRetries = 0 } };
        options.AddPolicy(policy, HttpPipelinePosition.PerCall);
        return options;
    }

    private static async Task<string?> WriteAfterStart(
        IClusterServiceViewRegister register,
        RegisteredClusterServiceView view,
        string? expectedToken,
        Task start)
    {
        await start.WaitAsync(TestContext.Current.CancellationToken);
        return await register.TryWriteAsync(view, expectedToken, TestContext.Current.CancellationToken);
    }

    // Coordinates real primary GET responses, not a fake transport, so both conditional PUTs
    // reach Azure with the same observed predecessor and the service must reject one of them.
    private sealed class PrimaryReadBarrier : HttpPipelinePolicy
    {
        private readonly TaskCompletionSource _bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        private int _conditionalWrites;
        private int _failedConditions;

        public int ConditionalWrites => _conditionalWrites;
        public int FailedConditions => _failedConditions;

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) =>
            throw new NotSupportedException("These integration tests use asynchronous Blob operations.");

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            await ProcessNextAsync(message, pipeline);
            if (message.Request.Method == RequestMethod.Get)
            {
                if (Interlocked.Increment(ref _reads) == 2)
                {
                    _bothRead.TrySetResult();
                }

                await _bothRead.Task.WaitAsync(message.CancellationToken);
            }
            else if (message.Request.Method == RequestMethod.Put)
            {
                Interlocked.Increment(ref _conditionalWrites);
                if (message.Response.Status is 409 or 412)
                {
                    Interlocked.Increment(ref _failedConditions);
                }
            }
        }
    }
}
