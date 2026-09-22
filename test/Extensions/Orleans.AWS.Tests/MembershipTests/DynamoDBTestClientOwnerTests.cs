using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Clustering.DynamoDB;
using Orleans.Clustering.TestKit;
using Orleans.Configuration;
using Orleans.Messaging;
using Xunit;

namespace AWSUtils.Tests.MembershipTests;

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Membership")]
[TestCategory("BVT"), TestCategory("Membership"), TestCategory("DynamoDb")]
public sealed class DynamoDBTestClientOwnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Adapter_ReimplementsOnlyInitializationSlots(bool gateway)
    {
        var ownerType = gateway ? typeof(TestOwnedDynamoDBGatewayListProvider) : typeof(TestOwnedDynamoDBMembershipTable);
        var interfaceType = gateway ? typeof(IGatewayListProvider) : typeof(IMembershipTable);
        var providerType = gateway ? typeof(DynamoDBGatewayListProvider) : typeof(DynamoDBMembershipTable);
        var map = ownerType.GetInterfaceMap(interfaceType);
        var ownedMethods = new List<string>();
        for (var index = 0; index < map.InterfaceMethods.Length; index++)
        {
            if (map.TargetMethods[index].DeclaringType == ownerType)
            {
                ownedMethods.Add(map.InterfaceMethods[index].Name);
            }
            else
            {
                Assert.Equal(providerType, map.TargetMethods[index].DeclaringType);
            }
        }

        Assert.Equal(
            gateway ? ["InitializeGatewayListProvider"] : new[] { "InitializeMembershipTable", "InitializeMembershipTableAsync" },
            ownedMethods.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Membership_RepeatedInitializationRetainsEveryDistinctNativeClient(bool legacy)
    {
        using var owner = CreateMembershipTable();

        await Initialize(owner, legacy);
        var first = Assert.Single(owner.CapturedClients);
        await Initialize(owner, legacy);

        Assert.Equal(2, owner.CapturedClients.Count);
        Assert.Contains(first, owner.CapturedClients);
        Assert.All(owner.CapturedClients, client => Assert.IsType<AmazonDynamoDBClient>(client));
        await CloseAndAssertNativeClientsDisposed(owner);
    }

    [Fact]
    public async Task Gateway_RepeatedInitializationRetainsEveryDistinctNativeClient()
    {
        using var owner = CreateGateway();

        await Initialize(owner);
        var first = Assert.Single(owner.CapturedClients);
        await Initialize(owner);

        Assert.Equal(2, owner.CapturedClients.Count);
        Assert.Contains(first, owner.CapturedClients);
        await CloseAndAssertNativeClientsDisposed(owner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCanceledInitialization_DoesNotAllocateOrDuplicateCapturedClient(bool initialized)
    {
        using var owner = CreateMembershipTable();
        if (initialized)
        {
            await Initialize(owner);
        }

        var before = owner.CapturedClients.ToArray();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ((IMembershipTable)owner).InitializeMembershipTableAsync(false, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(before, owner.CapturedClients);
        await CloseAndAssertNativeClientsDisposed(owner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInitialization_PreservesFailureAndCapturesClientBeforeRetry(bool gateway)
    {
        var failure = new InvalidOperationException("Initialization logger failed after native client allocation.");
        var fail = true;
        using var loggerFactory = new CallbackLoggerFactory(() =>
        {
            if (fail)
            {
                throw failure;
            }
        });
        using IDisposable owner = gateway ? CreateGateway(loggerFactory) : CreateMembershipTable(loggerFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Initialize(owner));

        Assert.Same(failure, exception);
        var failedClient = Assert.Single(GetClients(owner));
        fail = false;
        await Initialize(owner);
        Assert.Equal(2, GetClients(owner).Count);
        Assert.Contains(failedClient, GetClients(owner));
        await CloseAndAssertNativeClientsDisposed(owner);
    }

    [Fact]
    public async Task CancellationAfterAllocation_PreservesNativeTokenAndCapturesClient()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var loggerFactory = new CallbackLoggerFactory(cancellation.Cancel);
        using var owner = CreateMembershipTable(loggerFactory);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ((IMembershipTable)owner).InitializeMembershipTableAsync(false, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Single(owner.CapturedClients);
        await CloseAndAssertNativeClientsDisposed(owner);
    }

    [Fact]
    public void Close_DeduplicatesClientReferencesAndIsIdempotent()
    {
        using var owner = new DynamoDBTestClientOwner(typeof(DynamoDBMembershipTable));
        var first = new RecordingClient();
        var second = new RecordingClient();
        owner.Retain(first);
        owner.Retain(second);
        owner.Retain(first);
        Assert.Equal(0, first.DisposeCount);
        Assert.Equal(0, second.DisposeCount);

        owner.Dispose();
        owner.Dispose();

        Assert.Equal(2, owner.Clients.Count);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task ProbeLifetime_RemainsIndependentUntilHandleTeardown()
    {
        using var owner = CreateMembershipTable();
        var probe = new RecordingClient();
        await using var handle = new MembershipTableTestHandle(owner, () =>
        {
            try { owner.Dispose(); }
            finally { probe.Dispose(); }
            return ValueTask.CompletedTask;
        });
        await Initialize(owner);

        await CloseAndAssertNativeClientsDisposed(owner);

        Assert.Equal(0, probe.DisposeCount);
        await handle.DisposeAsync();
        await handle.DisposeAsync();
        Assert.Equal(1, probe.DisposeCount);
    }

    [Fact]
    public void UnexpectedProviderFieldShape_FailsFast()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new DynamoDBTestClientOwner(typeof(string)));

        Assert.Contains("System.String.storage", exception.Message);
        Assert.Contains(typeof(DynamoDBStorage).FullName!, exception.Message);
    }

    private static TestOwnedDynamoDBMembershipTable CreateMembershipTable(ILoggerFactory? loggerFactory = null)
        => new(
            loggerFactory ?? NullLoggerFactory.Instance,
            Options.Create(new DynamoDBClusteringOptions
            {
                Service = "http://localhost",
                CreateIfNotExists = false,
                UpdateIfExists = false
            }),
            Options.Create(new ClusterOptions { ClusterId = "owner-test" }));

    private static TestOwnedDynamoDBGatewayListProvider CreateGateway(ILoggerFactory? loggerFactory = null)
        => new(
            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<DynamoDBGatewayListProvider>(),
            Options.Create(new DynamoDBGatewayOptions
            {
                Service = "http://localhost",
                CreateIfNotExists = false,
                UpdateIfExists = false
            }),
            Options.Create(new ClusterOptions { ClusterId = "owner-test" }),
            Options.Create(new GatewayOptions()));

    private static Task Initialize(IDisposable owner, bool legacy = false)
    {
        if (owner is IMembershipTable table)
        {
#pragma warning disable CS0618 // Verify ownership through the existing tokenless interface slot too.
            return legacy
                ? table.InitializeMembershipTable(false)
                : table.InitializeMembershipTableAsync(false, TestContext.Current.CancellationToken);
#pragma warning restore CS0618
        }

        return ((IGatewayListProvider)owner).InitializeGatewayListProvider();
    }

    private static IReadOnlyCollection<AmazonDynamoDBClient> GetClients(IDisposable owner) => owner switch
    {
        TestOwnedDynamoDBMembershipTable membership => membership.CapturedClients,
        TestOwnedDynamoDBGatewayListProvider gateway => gateway.CapturedClients,
        _ => throw new ArgumentOutOfRangeException(nameof(owner))
    };

    private static async Task CloseAndAssertNativeClientsDisposed(IDisposable owner)
    {
        var clients = GetClients(owner).ToArray();
        foreach (var client in clients)
        {
            // A failed ownership assertion must not send a request to any backend.
            client.BeforeRequestEvent += (_, _) => throw new InvalidOperationException("Unexpected backend request.");
        }

        owner.Dispose();
        owner.Dispose();
        foreach (var client in clients)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => client.DescribeTableAsync(new DescribeTableRequest { TableName = "membership" }, TestContext.Current.CancellationToken));
        }
    }

    private sealed class RecordingClient() : AmazonDynamoDBClient(
        new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://localhost" })
    {
        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CallbackLoggerFactory(Action callback) : ILoggerFactory
    {
        private readonly ILogger _logger = new CallbackLogger(callback);

        public ILogger CreateLogger(string categoryName) => _logger;
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public void Dispose() { }

        private sealed class CallbackLogger(Action callback) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => callback();
        }
    }
}
