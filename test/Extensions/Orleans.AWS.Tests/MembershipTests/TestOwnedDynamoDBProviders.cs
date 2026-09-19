using System.Reflection;
using Amazon.DynamoDBv2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Clustering.DynamoDB;
using Orleans.Configuration;
using Orleans.Messaging;

namespace AWSUtils.Tests.MembershipTests;

// Fixtures initialize each handle serially through its interface and finish admitted initialization before disposal.
internal sealed class TestOwnedDynamoDBMembershipTable(
    ILoggerFactory loggerFactory,
    IOptions<DynamoDBClusteringOptions> options,
    IOptions<ClusterOptions> clusterOptions)
    : DynamoDBMembershipTable(loggerFactory, options, clusterOptions), IMembershipTable, IDisposable
{
    private readonly DynamoDBTestClientOwner _clients = new(typeof(DynamoDBMembershipTable));

    internal IReadOnlyCollection<AmazonDynamoDBClient> CapturedClients => _clients.Clients;

    [Obsolete("Use InitializeMembershipTableAsync instead.")]
    Task IMembershipTable.InitializeMembershipTable(bool tryInitTableVersion)
        => InitializeOwnedAsync(tryInitTableVersion, CancellationToken.None);

    Task IMembershipTable.InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken)
        => InitializeOwnedAsync(tryInitTableVersion, cancellationToken);

    private async Task InitializeOwnedAsync(bool tryInitTableVersion, CancellationToken cancellationToken)
    {
        _clients.ThrowIfDisposed();
        try
        {
            await base.InitializeMembershipTableAsync(tryInitTableVersion, cancellationToken);
        }
        finally
        {
            _clients.Capture(this);
        }
    }

    public void Dispose() => _clients.Dispose();
}

internal sealed class TestOwnedDynamoDBGatewayListProvider(
    ILogger<DynamoDBGatewayListProvider> logger,
    IOptions<DynamoDBGatewayOptions> options,
    IOptions<ClusterOptions> clusterOptions,
    IOptions<GatewayOptions> gatewayOptions)
    : DynamoDBGatewayListProvider(logger, options, clusterOptions, gatewayOptions), IGatewayListProvider, IDisposable
{
    private readonly DynamoDBTestClientOwner _clients = new(typeof(DynamoDBGatewayListProvider));

    internal IReadOnlyCollection<AmazonDynamoDBClient> CapturedClients => _clients.Clients;

    async Task IGatewayListProvider.InitializeGatewayListProvider()
    {
        _clients.ThrowIfDisposed();
        try
        {
            await base.InitializeGatewayListProvider();
        }
        finally
        {
            _clients.Capture(this);
        }
    }

    public void Dispose() => _clients.Dispose();
}

internal sealed class DynamoDBTestClientOwner : IDisposable
{
    private static readonly FieldInfo ClientField = RequirePrivateField(
        typeof(DynamoDBStorage), "_ddbClient", typeof(AmazonDynamoDBClient));
    private readonly FieldInfo _storageField;
    private readonly HashSet<AmazonDynamoDBClient> _clients = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    internal DynamoDBTestClientOwner(Type providerType)
        => _storageField = RequirePrivateField(providerType, "storage", typeof(DynamoDBStorage));

    internal IReadOnlyCollection<AmazonDynamoDBClient> Clients => _clients;

    internal void Capture(object provider)
    {
        ThrowIfDisposed();
        if (_storageField.GetValue(provider) is not DynamoDBStorage storage)
        {
            return;
        }

        var client = ClientField.GetValue(storage) as AmazonDynamoDBClient
            ?? throw new InvalidOperationException("The constructed DynamoDB storage has no SDK client.");
        Retain(client);
    }

    internal void Retain(AmazonDynamoDBClient client)
    {
        ThrowIfDisposed();
        _clients.Add(client);
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Exception>? failures = null;
        foreach (var client in _clients)
        {
            try { client.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }

        if (failures is not null)
        {
            throw new AggregateException("Closing fixture-owned DynamoDB clients failed.", failures);
        }
    }

    private static FieldInfo RequirePrivateField(Type declaringType, string name, Type fieldType)
    {
        var field = declaringType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (field is null || !field.IsPrivate || field.FieldType != fieldType)
        {
            throw new InvalidOperationException(
                $"DynamoDB fixture ownership requires private {fieldType.FullName} {declaringType.FullName}.{name}.");
        }

        return field;
    }
}
