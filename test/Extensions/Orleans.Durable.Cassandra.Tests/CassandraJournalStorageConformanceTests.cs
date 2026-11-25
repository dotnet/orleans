using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Durable.Journaling;
using Orleans.Durable.Journaling.Tests;
using TestExtensions;
using Xunit;

namespace Tester.Cassandra.Journaling;

[TestSuite("BVT")]
[TestProvider("Cassandra")]
[TestArea("Journaling")]
[TestCategory("BVT")]
[TestCategory("Cassandra")]
[Collection("Cassandra journal storage")]
public sealed class CassandraJournalStorageConformanceTests(CassandraJournalContainer container) : JournalStorageConformanceTests
{
    private CassandraJournalStorageProvider? _provider;

    protected override long? MaxAppendBytes => 2 * 1024 * 1024;

    protected override async ValueTask<IJournalStorageProvider> CreateProviderAsync(
        JournalStorageFaultInjector? faults,
        CancellationToken cancellationToken)
    {
        var session = await container.GetSessionAsync(cancellationToken);
        var options = new CassandraJournalStorageOptions { TableName = $"jc_{Guid.NewGuid():N}" };
        options.ConfigureClient(_ => Task.FromResult(session));
        _provider = new CassandraJournalStorageProvider(
            Options.Create(options),
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = "json" }),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<CassandraJournalStorage>.Instance);
        await _provider.InitializeAsync(cancellationToken);
        return _provider;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_provider is { } provider)
        {
            await provider.CloseAsync(CancellationToken.None);
        }
    }
}
