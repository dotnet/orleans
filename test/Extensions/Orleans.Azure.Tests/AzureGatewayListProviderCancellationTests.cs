using Azure;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.AzureUtils;
using Orleans.Clustering.AzureStorage;
using Orleans.Configuration;
using Xunit;

namespace Tester.AzureUtils;

[TestCategory("AzureStorage"), TestCategory("Membership"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("AzureStorage")]
[TestArea("Membership")]
public class AzureGatewayListProviderCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCanceled_DoesNotCreateClientOrAccessTable(bool initialize)
    {
        var clientCalls = 0;
        var options = new AzureStorageGatewayOptions();
#pragma warning disable CS0618 // A legacy client factory makes pre-cancellation's no-work guarantee observable.
        options.ConfigureTableServiceClient(() =>
        {
            clientCalls++;
            throw new InvalidOperationException("No client should be created.");
        });
#pragma warning restore CS0618
        var provider = CreateProvider(options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialize
            ? provider.InitializeGatewayListProviderAsync(cancellation.Token)
            : provider.GetGatewaysAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, clientCalls);
    }

    [Fact]
    public async Task Initialize_Cancellation_ReachesNativeTableCreation()
    {
        var table = new SdkTableClient();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        table.OnCreate = CreateTable;
        var provider = CreateProvider(OptionsFor(table));
        using var cancellation = new CancellationTokenSource();
        var initialize = provider.InitializeGatewayListProviderAsync(cancellation.Token);

        Assert.Equal(cancellation.Token, await Await(entered.Task));
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Await(initialize));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(exited.Task.IsCompletedSuccessfully);
        Assert.Equal(1, table.CreateCalls);

        async Task<Response<TableItem>> CreateTable(CancellationToken token)
        {
            entered.SetResult(token);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Native table creation must be canceled.");
            }
            finally
            {
                exited.SetResult();
            }
        }
    }

    [Fact]
    public async Task GetGateways_Cancellation_ReachesNativeQueryAndStopsEnumeration()
    {
        var table = InitializedTable();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        table.OnQuery = token => new BlockingQuery(token, entered, exited);
        var provider = CreateProvider(OptionsFor(table));
        await provider.InitializeGatewayListProviderAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var lookup = provider.GetGatewaysAsync(cancellation.Token);

        Assert.Equal(cancellation.Token, await Await(entered.Task));
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Await(lookup));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(exited.Task.IsCompletedSuccessfully);
        Assert.Equal(cancellation.Token, Assert.Single(table.QueryTokens));
    }

    [Fact]
    public async Task GetGateways_ReturnsEndpointsFromNativeQuery()
    {
        var table = InitializedTable();
        var raw = Substitute.For<Response>();
        var rows = new[]
        {
            new SiloInstanceTableEntry { Address = "127.0.0.1", ProxyPort = "30001", Generation = "1" },
            new SiloInstanceTableEntry { Address = "127.0.0.2", ProxyPort = "30002", Generation = "2" }
        };
        table.OnQuery = _ => AsyncPageable<SiloInstanceTableEntry>.FromPages([Page<SiloInstanceTableEntry>.FromValues(rows, null, raw)]);
        var provider = CreateProvider(OptionsFor(table));
        await provider.InitializeGatewayListProviderAsync(TestContext.Current.CancellationToken);

        var gateways = await provider.GetGatewaysAsync(TestContext.Current.CancellationToken);

        Assert.Equal([new Uri("gwy.tcp://127.0.0.1:30001/1"), new Uri("gwy.tcp://127.0.0.2:30002/2")], gateways);
    }

    private static AzureGatewayListProvider CreateProvider(AzureStorageGatewayOptions options) =>
        new(NullLoggerFactory.Instance, Options.Create(options),
            Options.Create(new ClusterOptions { ClusterId = "gateway-cancellation" }), Options.Create(new GatewayOptions()));

    private static AzureStorageGatewayOptions OptionsFor(TableClient table)
    {
        var service = Substitute.For<TableServiceClient>();
        service.GetTableClient(Arg.Any<string>()).Returns(table);
        var options = new AzureStorageGatewayOptions { TableName = "GatewayCancellationTests", TableServiceClient = service };
        return options;
    }

    private static SdkTableClient InitializedTable() => new();

    // A handwritten SDK seam avoids dynamic proxies over an internal table-entity type.
    private sealed class SdkTableClient : TableClient
    {
        public int CreateCalls { get; private set; }
        public List<CancellationToken> QueryTokens { get; } = [];
        public Func<CancellationToken, Task<Response<TableItem>>>? OnCreate { get; set; }
        public Func<CancellationToken, AsyncPageable<SiloInstanceTableEntry>>? OnQuery { get; set; }

        public override Task<Response<TableItem>> CreateIfNotExistsAsync(CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            if (OnCreate is { } create)
            {
                return create(cancellationToken);
            }

            var raw = Substitute.For<Response>();
            raw.Status.Returns(201);
            return Task.FromResult(Response.FromValue(TableModelFactory.TableItem("GatewayCancellationTests"), raw));
        }

        public override AsyncPageable<T> QueryAsync<T>(string? filter = null, int? maxPerPage = null,
            IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(typeof(SiloInstanceTableEntry), typeof(T));
            QueryTokens.Add(cancellationToken);
            return (AsyncPageable<T>)(object)(OnQuery ?? throw new InvalidOperationException("Unexpected query."))(cancellationToken);
        }
    }

    private static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    private static Task<T> Await<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private sealed class BlockingQuery(
        CancellationToken token,
        TaskCompletionSource<CancellationToken> entered,
        TaskCompletionSource exited) : AsyncPageable<SiloInstanceTableEntry>
    {
        public override async IAsyncEnumerable<Page<SiloInstanceTableEntry>> AsPages(string? continuationToken = null, int? pageSizeHint = null)
        {
            entered.SetResult(token);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                exited.SetResult();
            }

            yield break;
        }
    }
}
