using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Clustering.DynamoDB;
using Orleans.Configuration;
using Orleans.Runtime;
using Xunit;

namespace AWSUtils.Tests.MembershipTests;

[TestCategory("Membership"), TestCategory("AWS"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Membership")]
public class DynamoDBGatewayListProviderCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCanceled_DoesNotInitializeOrScanStorage(bool initialize)
    {
        var provider = CreateProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialize
            ? provider.InitializeGatewayListProviderAsync(cancellation.Token)
            : provider.GetGatewaysAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task GetGateways_Cancellation_ReachesSecondNativeScanPage()
    {
        using var client = new ScanClient { BlockSecondPage = true };
        var provider = CreateProvider(client);
        using var cancellation = new CancellationTokenSource();
        var lookup = provider.GetGatewaysAsync(cancellation.Token);

        Assert.Equal(cancellation.Token, await client.SecondPageEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(2, client.Requests.Count);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lookup.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(client.SecondPageExited.Task.IsCompletedSuccessfully);
        Assert.Equal([cancellation.Token, cancellation.Token], client.Tokens);
        Assert.Same(client.Continuation, client.Requests[1].ExclusiveStartKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetGateways_AsyncAndLegacyMethods_PreservePaginatedEndpoints(bool legacy)
    {
        using var client = new ScanClient();
        var provider = CreateProvider(client);
        using var cancellation = new CancellationTokenSource();

        var gateways = legacy
            ? await provider.GetGateways()
            : await provider.GetGatewaysAsync(cancellation.Token);

        Assert.Equal([new Uri("gwy.tcp://127.0.0.1:30001/1"), new Uri("gwy.tcp://127.0.0.2:30002/2")], gateways);
        Assert.Equal(2, client.Tokens.Count);
        Assert.All(client.Tokens, token => Assert.Equal(legacy ? CancellationToken.None : cancellation.Token, token));
        Assert.All(client.Requests, request => Assert.Equal("GatewayCancellation", request.TableName));
        Assert.Same(client.Continuation, client.Requests[1].ExclusiveStartKey);
    }

    private static DynamoDBGatewayListProvider CreateProvider(ScanClient? client = null)
    {
        var provider = new DynamoDBGatewayListProvider(
            NullLogger<DynamoDBGatewayListProvider>.Instance,
            Options.Create(new DynamoDBGatewayOptions { TableName = "GatewayCancellation", Service = "http://localhost" }),
            Options.Create(new ClusterOptions { ClusterId = "cluster" }), Options.Create(new GatewayOptions()));
        if (client is not null)
        {
            // Match the existing DynamoDB tests' SDK injection seam; no production API added for testing.
            var storage = new DynamoDBStorage(NullLogger<DynamoDBStorage>.Instance, "http://localhost");
            var field = typeof(DynamoDBStorage).GetField("_ddbClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.IsAssignableFrom<IDisposable>(field.GetValue(storage)).Dispose();
            field.SetValue(storage, client);
            typeof(DynamoDBGatewayListProvider).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider, storage);
        }

        return provider;
    }

    private sealed class ScanClient() : AmazonDynamoDBClient(
        new BasicAWSCredentials("test", "test"), new AmazonDynamoDBConfig { ServiceURL = "http://localhost" })
    {
        public bool BlockSecondPage { get; init; }
        public List<CancellationToken> Tokens { get; } = [];
        public List<ScanRequest> Requests { get; } = [];
        public Dictionary<string, AttributeValue> Continuation { get; } = new() { ["row"] = new AttributeValue("next") };
        public TaskCompletionSource<CancellationToken> SecondPageEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondPageExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<ScanResponse> ScanAsync(ScanRequest request, CancellationToken cancellationToken = default)
        {
            Tokens.Add(cancellationToken);
            Requests.Add(request);
            if (Requests.Count == 2 && BlockSecondPage)
            {
                SecondPageEntered.SetResult(cancellationToken);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    SecondPageExited.SetResult();
                }
            }

            var page = Requests.Count;
            return new ScanResponse
            {
                Items =
                [
                    new()
                    {
                        ["Address"] = new AttributeValue($"127.0.0.{page}"),
                        ["ProxyPort"] = new AttributeValue { N = $"3000{page}" },
                        ["Generation"] = new AttributeValue { N = page.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                    }
                ],
                LastEvaluatedKey = page == 1 ? Continuation : []
            };
        }
    }
}
