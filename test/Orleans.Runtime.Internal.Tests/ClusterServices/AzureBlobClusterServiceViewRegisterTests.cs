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
    [Fact]
    public async Task AtomicBlobRoundTripIncludesConfigurationMembershipAndBothTopologyIndexes()
    {
        using var transport = new BlobTransport();
        var register = Create(transport);
        var first = RegisteredClusterServiceViewProviderTests.MakeView(1, TestServiceMembership.A);
        var absence = await register.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Null(absence.View);
        Assert.Null(absence.Token);

        Assert.True(await register.TryWriteAsync(first, null, TestContext.Current.CancellationToken));
        var read = await register.ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(read.View);
        Assert.True(first.HasSameContent(read.View));
        Assert.Equal("\"backend-1\"", read.Token);
        Assert.NotEqual(first.Id.Revision.ToString(), read.Token);
        Assert.Equal(first.GetOwnedResources(TestServiceMembership.A), read.View.GetOwnedResources(TestServiceMembership.A));
        Assert.Equal(new[] { "GET", "PUT", "GET" }, transport.Methods);
        Assert.Equal("*", Assert.Single(transport.IfNoneMatches));
        Assert.Empty(transport.IfMatches);
    }

    [Fact]
    public async Task ConditionalBlobUpdatesHaveExactlyOneWinnerAndDoNotOverwriteTheWinner()
    {
        using var transport = new BlobTransport();
        var firstRegister = Create(transport);
        var secondRegister = Create(transport);
        var initial = RegisteredClusterServiceViewProviderTests.MakeView(1, TestServiceMembership.A);
        Assert.True(await firstRegister.TryWriteAsync(initial, null, TestContext.Current.CancellationToken));
        var read = await firstRegister.ReadAsync(TestContext.Current.CancellationToken);
        var firstProposal = RegisteredClusterServiceViewProviderTests.MakeView(2, TestServiceMembership.B, initial.Id);
        var secondProposal = RegisteredClusterServiceViewProviderTests.MakeView(2, TestServiceMembership.A, initial.Id);

        var results = await Task.WhenAll(
            firstRegister.TryWriteAsync(firstProposal, read.Token, TestContext.Current.CancellationToken).AsTask(),
            secondRegister.TryWriteAsync(secondProposal, read.Token, TestContext.Current.CancellationToken).AsTask());
        var winner = await firstRegister.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Single(results, static success => success);
        Assert.Single(results, static success => !success);
        Assert.True((results[0] ? firstProposal : secondProposal).HasSameContent(winner.View!));
        Assert.Equal(new[] { read.Token, read.Token }, transport.IfMatches);
        Assert.Equal("\"backend-2\"", winner.Token);
        Assert.False(await secondRegister.TryWriteAsync(initial, null, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BlobRoundTripPreservesFullAndWrappedRingBoundaries(bool full)
    {
        using var transport = new BlobTransport();
        var register = Create(transport);
        var view = new RegisteredClusterServiceView(
            new("service", "authority", 1), null, new(7), RegisteredClusterServiceViewProviderTests.Configuration,
            full ? [TestServiceMembership.A] : [TestServiceMembership.A, TestServiceMembership.B],
            [], [],
            full
                ? [new(TestServiceMembership.A, 0, RingRange.Full)]
                : [new(TestServiceMembership.A, 0, RingRange.Create(0, 100)), new(TestServiceMembership.B, 0, RingRange.Create(100, 0))]);

        Assert.True(await register.TryWriteAsync(view, null, TestContext.Current.CancellationToken));
        var read = await register.ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(read.View);
        Assert.True(view.HasSameContent(read.View));
        Assert.NotNull(read.View.Topology);
        Assert.Equal(view.RingAssignments, read.View.RingAssignments);
        Assert.Equal(full, read.View.RingAssignments[0].Range.IsFull);
        Assert.True(read.View.RingAssignments[^1].Range.Contains(0));
    }

    [Fact]
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

    [Fact]
    public async Task BlobNamespaceMismatchRequiresExplicitBootstrap()
    {
        using var transport = new BlobTransport();
        var original = Create(transport);
        Assert.True(await original.TryWriteAsync(
            RegisteredClusterServiceViewProviderTests.MakeView(1, TestServiceMembership.A), null, TestContext.Current.CancellationToken));
        var replacement = new AzureBlobClusterServiceViewRegister(
            "service", "replacement", new("https://account.blob.core.windows.net/views/service"), Options(transport));

        await Assert.ThrowsAsync<ClusterServiceAuthorityException>(() => replacement.ReadAsync(TestContext.Current.CancellationToken).AsTask());
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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
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
            response.Headers.Add("x-ms-error-code", code);
            response.Headers.Add("x-ms-request-id", "mock-primary-error");
            return response;
        }
    }
}
