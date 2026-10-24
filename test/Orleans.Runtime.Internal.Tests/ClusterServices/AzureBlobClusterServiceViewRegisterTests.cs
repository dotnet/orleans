using System.Net;
using System.Net.Http.Headers;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Orleans.Persistence.AzureStorage;
using Orleans.Runtime.ClusterServices;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using Xunit;

namespace UnitTests.ClusterServices;

[TestArea("Runtime"), TestCategory("BVT"), TestSuite("BVT"), TestProvider("None")]
public sealed class AzureBlobClusterServiceViewRegisterTests
{
    [Fact(Timeout = 30_000)]
    public async Task AtomicBlobRoundTripIncludesConfigurationMembershipAndBothTopologyIndexes()
    {
        using var transport = new BlobTransport();
        var register = Create(transport);
        var first = RegisteredClusterServiceViewProviderTests.MakeView(1, TestServiceMembership.A);
        var absence = await register.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Null(absence.View);
        Assert.Null(absence.Token);

        var writtenToken = await register.TryWriteAsync(first, null, TestContext.Current.CancellationToken);
        Assert.NotNull(writtenToken);
        var read = await register.ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(read.View);
        Assert.True(first.HasSameContent(read.View));
        Assert.Equal("\"backend-1\"", read.Token);
        Assert.Equal(writtenToken, read.Token);
        Assert.NotEqual(first.Id.Revision.ToString(), read.Token);
        Assert.Equal(first.GetOwnedResources(TestServiceMembership.A), read.View.GetOwnedResources(TestServiceMembership.A));
        Assert.Equal(new[] { "GET", "GET", "PUT", "GET" }, transport.Methods);
        Assert.Equal("*", Assert.Single(transport.IfNoneMatches));
        Assert.Empty(transport.IfMatches);
    }

    [Fact(Timeout = 30_000)]
    public async Task ConditionalBlobUpdatesHaveExactlyOneWinnerAndDoNotOverwriteTheWinner()
    {
        using var transport = new BlobTransport();
        var firstRegister = Create(transport);
        var secondRegister = Create(transport);
        var initial = RegisteredClusterServiceViewProviderTests.MakeView(1, TestServiceMembership.A);
        Assert.NotNull(await firstRegister.TryWriteAsync(initial, null, TestContext.Current.CancellationToken));
        var read = await firstRegister.ReadAsync(TestContext.Current.CancellationToken);
        var firstProposal = RegisteredClusterServiceViewProviderTests.MakeView(2, TestServiceMembership.B, initial.Id);
        var secondProposal = RegisteredClusterServiceViewProviderTests.MakeView(2, TestServiceMembership.A, initial.Id);
        var readCount = 0;
        var bothRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.AfterGet = () =>
        {
            if (Interlocked.Increment(ref readCount) == 2)
            {
                bothRead.TrySetResult();
            }

            return bothRead.Task.WaitAsync(TestContext.Current.CancellationToken);
        };

        var results = await Task.WhenAll(
            firstRegister.TryWriteAsync(firstProposal, read.Token, TestContext.Current.CancellationToken).AsTask(),
            secondRegister.TryWriteAsync(secondProposal, read.Token, TestContext.Current.CancellationToken).AsTask())
            .WaitAsync(TestContext.Current.CancellationToken);
        transport.AfterGet = null;
        var winner = await firstRegister.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(winner.Token, Assert.Single(results, static token => token is not null));
        Assert.Single(results, static token => token is null);
        Assert.True((results[0] is not null ? firstProposal : secondProposal).HasSameContent(winner.View!));
        Assert.Equal(new[] { read.Token, read.Token }, transport.IfMatches);
        Assert.Equal("\"backend-2\"", winner.Token);
        Assert.Null(await secondRegister.TryWriteAsync(initial, null, TestContext.Current.CancellationToken));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BlobRoundTripPreservesFullAndWrappedRingBoundaries(bool full)
    {
        using var transport = new BlobTransport();
        var register = Create(transport);
        var view = new RegisteredClusterServiceView(
            new("service", "authority", 1), null, new(7), RegisteredClusterServiceViewProviderTests.Configuration,
            full ? [TestServiceMembership.A] : [TestServiceMembership.A, TestServiceMembership.B],
            full ? ["partition-0"] : ["partition-0", "partition-1"],
            full
                ? [KeyValuePair.Create("partition-0", TestServiceMembership.A)]
                : [KeyValuePair.Create("partition-0", TestServiceMembership.A), KeyValuePair.Create("partition-1", TestServiceMembership.B)],
            full
                ? [new(TestServiceMembership.A, 0, RingRange.Full)]
                : [new(TestServiceMembership.A, 0, RingRange.Create(0, 100)), new(TestServiceMembership.B, 0, RingRange.Create(100, 0))],
            full
                ? [KeyValuePair.Create("partition-0", RingRange.Full)]
                : [KeyValuePair.Create("partition-0", RingRange.Create(0, 100)), KeyValuePair.Create("partition-1", RingRange.Create(100, 0))]);

        Assert.NotNull(await register.TryWriteAsync(view, null, TestContext.Current.CancellationToken));
        var read = await register.ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(read.View);
        Assert.True(view.HasSameContent(read.View));
        Assert.NotNull(read.View.Topology);
        Assert.Equal(view.RingAssignments, read.View.RingAssignments);
        Assert.Equal(full, read.View.RingAssignments[0].Range.IsFull);
        Assert.True(read.View.RingAssignments[^1].Range.Contains(0));
        Assert.True(read.View.TryGetRingResource(50, out var resource, out var owner));
        Assert.Equal("partition-0", resource);
        Assert.Equal(TestServiceMembership.A, owner);
    }

    [Fact(Timeout = 30_000)]
    public async Task BlobAuthorityAndContainerFailuresAreNotMistakenForAnUninitializedView()
    {
        using var transport = new BlobTransport { MissingCode = "ContainerNotFound" };
        var register = Create(transport);
        var failure = await Assert.ThrowsAsync<RequestFailedException>(() => register.ReadAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("ContainerNotFound", failure.ErrorCode);
        transport.MissingCode = "AuthorizationFailure";
        transport.MissingStatus = HttpStatusCode.Forbidden;
        failure = await Assert.ThrowsAsync<RequestFailedException>(() => register.ReadAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(403, failure.Status);
    }

    [Fact(Timeout = 30_000)]
    public async Task BlobNamespaceMismatchRequiresExplicitBootstrap()
    {
        using var transport = new BlobTransport();
        var original = Create(transport);
        Assert.NotNull(await original.TryWriteAsync(
            RegisteredClusterServiceViewProviderTests.MakeView(1, TestServiceMembership.A), null, TestContext.Current.CancellationToken));
        var replacement = new AzureBlobClusterServiceViewRegister(
            "service", "replacement", new("https://account.blob.core.windows.net/views/service"), Options(transport));

        await Assert.ThrowsAsync<ClusterServiceAuthorityException>(() => replacement.ReadAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact(Timeout = 30_000)]
    public async Task BlobCasRejectsConflictingCanonicalPayloadAndIncorrectPredecessorWithTheCurrentEtag()
    {
        using var transport = new BlobTransport();
        var register = Create(transport);
        var first = RegisteredClusterServiceViewProviderTests.MakeView(1, TestServiceMembership.A);
        Assert.NotNull(await register.TryWriteAsync(first, null, TestContext.Current.CancellationToken));
        var previous = await register.ReadAsync(TestContext.Current.CancellationToken);
        var second = RegisteredClusterServiceViewProviderTests.MakeView(2, TestServiceMembership.B, first.Id);
        Assert.NotNull(await register.TryWriteAsync(second, previous.Token, TestContext.Current.CancellationToken));
        var current = await register.ReadAsync(TestContext.Current.CancellationToken);
        var conflicting = RegisteredClusterServiceViewProviderTests.MakeView(2, TestServiceMembership.A, first.Id);
        var wrongPredecessor = RegisteredClusterServiceViewProviderTests.MakeView(3, TestServiceMembership.A, first.Id);

        await Assert.ThrowsAsync<ClusterServiceAuthorityException>(() =>
            register.TryWriteAsync(conflicting, current.Token, TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ClusterServiceAuthorityException>(() =>
            register.TryWriteAsync(wrongPredecessor, current.Token, TestContext.Current.CancellationToken).AsTask());

        var unchanged = await register.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(current.Token, unchanged.Token);
        Assert.True(second.HasSameContent(unchanged.View!));
    }

    [Fact]
    public void BlobRegisterDisallowsSecondaryEndpointsAndSecondaryRetryPolicies()
    {
        Assert.Throws<ArgumentException>(() => new AzureBlobClusterServiceViewRegister(
            "service", "authority", new("https://account-secondary.blob.core.windows.net/views/service")));
        Assert.Throws<ArgumentException>(() => new AzureBlobClusterServiceViewRegister(
            "service", "authority", new("https://account.blob.core.windows.net/views/service"),
            new() { GeoRedundantSecondaryUri = new("https://account-secondary.blob.core.windows.net/") }));
    }

    private static AzureBlobClusterServiceViewRegister Create(BlobTransport handler) =>
        new("service", "authority", new("https://account.blob.core.windows.net/views/service"), Options(handler));

    private static BlobClientOptions Options(BlobTransport handler) =>
        new() { Transport = new HttpClientTransport(new HttpClient(handler, disposeHandler: false)), Retry = { MaxRetries = 0 } };

    // Exercises the real SDK HTTP pipeline and production JSON codec without a cloud account.
    private sealed class BlobTransport : HttpMessageHandler
    {
        private readonly object _lock = new();
        private byte[]? _payload;
        private int _version;
        public List<string> Methods { get; } = [];
        public List<string> IfMatches { get; } = [];
        public List<string> IfNoneMatches { get; } = [];
        public string MissingCode { get; set; } = "BlobNotFound";
        public HttpStatusCode MissingStatus { get; set; } = HttpStatusCode.NotFound;
        public Func<Task>? AfterGet { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            if (request.Method == HttpMethod.Get && AfterGet is { } afterGet)
            {
                HttpResponseMessage response;
                lock (_lock)
                {
                    Methods.Add(request.Method.Method);
                    response = _payload is null ? Error(MissingStatus, MissingCode) : Success(HttpStatusCode.OK, _payload);
                }

                await afterGet().WaitAsync(cancellationToken);
                return response;
            }

            lock (_lock)
            {
                Methods.Add(request.Method.Method);
                if (request.Method == HttpMethod.Get)
                {
                    return _payload is null ? Error(MissingStatus, MissingCode) : Success(HttpStatusCode.OK, _payload);
                }

                Assert.Equal(HttpMethod.Put, request.Method);
                if (request.Headers.TryGetValues("If-None-Match", out var ifNoneMatches))
                {
                    IfNoneMatches.Add(Assert.Single(ifNoneMatches));
                    if (_payload is not null)
                    {
                        return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
                    }
                }
                else
                {
                    var expected = Assert.Single(request.Headers.GetValues("If-Match"));
                    IfMatches.Add(expected);
                    if (expected != $"\"backend-{_version}\"")
                    {
                        return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
                    }
                }

                _payload = bytes!;
                _version++;
                return Success(HttpStatusCode.Created, []);
            }
        }

        private HttpResponseMessage Success(HttpStatusCode status, byte[] payload)
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(payload) };
            response.Headers.ETag = new EntityTagHeaderValue($"\"backend-{_version}\"");
            response.Content.Headers.LastModified = DateTimeOffset.UnixEpoch;
            response.Content.Headers.ContentLength = payload.Length;
            response.Content.Headers.ContentType = new("application/json");
            response.Headers.Add("x-ms-blob-type", "BlockBlob");
            response.Headers.Add("x-ms-request-id", "mock-primary-request");
            response.Headers.Add("x-ms-server-encrypted", "true");
            return response;
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string code)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent($"<Error><Code>{code}</Code><Message>Mock Azure response</Message></Error>")
            };
            response.Content.Headers.ContentType = new("application/xml");
            response.Headers.Add("x-ms-error-code", code);
            response.Headers.Add("x-ms-request-id", "mock-primary-error");
            return response;
        }
    }
}
