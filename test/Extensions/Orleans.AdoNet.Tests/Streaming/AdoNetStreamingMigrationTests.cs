using System.Security.Cryptography;
using System.Text;
using Orleans.Streaming.AdoNet;
using Orleans.Tests.SqlUtils;
using UnitTests.General;
using RelationalOrleansQueries = Orleans.Streaming.AdoNet.Storage.RelationalOrleansQueries;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Verifies that the streaming migrations upgrade databases created by earlier streaming scripts.
/// </summary>
[TestSuite("Functional")]
[TestArea("Streaming")]
public sealed class AdoNetStreamingMigrationTests
{
    private const string ServiceId = "migration-service";
    private const string ProviderId = "migration-provider";
    private const string QueueId = "migration-queue";

    // src/AdoNet/Orleans.Streaming.AdoNet/MySQL-Streaming.sql from 845672b8d84813859aa54245efd0eebb8f0233e2,
    // the last script before the 10.5.0 migration. The hash normalizes checkout line endings.
    [Fact]
    [TestProvider("MySql")]
    public async Task MySql_Migration_10_5_0_UpgradesStreamProcedures()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "StreamingUpgradeFixtures", "MySQL-Streaming.sql");
        var original = await File.ReadAllTextAsync(fixturePath, cancellationToken);
        Assert.Equal(
            "B349B59063190EC34AF69702EF32FFC76BF1FF396690572469B894D3F56A49A2",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original.Replace("\r\n", "\n")))));

        var database = await RelationalStorageForTesting.SetupInstance(
            AdoNetInvariants.InvariantNameMySql,
            $"StreamingMigration_{Guid.NewGuid():N}",
            cancellationToken: cancellationToken,
            setupSqlScriptFileNames: [Path.Combine(AppContext.BaseDirectory, "MySQL-Main.sql"), fixturePath]);

        // The migration runs twice, as users run it, to show that repeating it leaves an upgraded database working.
        var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "MySQL-Streaming-10.5.0.sql"), cancellationToken);
        await MySqlStorageForTesting.ExecuteClientScriptAsync(database.CurrentConnectionString, migration, cancellationToken);
        await MySqlStorageForTesting.ExecuteClientScriptAsync(database.CurrentConnectionString, migration, cancellationToken);
        var queries = await RelationalOrleansQueries.CreateInstance(AdoNetInvariants.InvariantNameMySql, database.CurrentConnectionString);

        var confirmed = await queries.QueueStreamMessageAsync(ServiceId, ProviderId, QueueId, [1], 600);
        var released = await queries.QueueStreamMessageAsync(ServiceId, ProviderId, QueueId, [2], 600);
        var failed = await queries.QueueStreamMessageAsync(ServiceId, ProviderId, QueueId, [3], 600);

        // A negative expiry places the message already past due, because eviction compares against whole seconds.
        var expired = await queries.QueueStreamMessageAsync(ServiceId, ProviderId, QueueId, [4], -1);

        // Eviction is due on every dequeue, so each dequeue also runs EvictStreamMessages and EvictStreamDeadLetters.
        var dequeued = await GetMessagesAsync(queries);
        var releasedAcks = await queries.ReleaseStreamMessagesAsync(ServiceId, ProviderId, QueueId, [new(released.MessageId, 1)]);
        var redelivered = Assert.Single(await GetMessagesAsync(queries));
        var confirmedAcks = await queries.ConfirmStreamMessagesAsync(ServiceId, ProviderId, QueueId, [new(confirmed.MessageId, 1), new(released.MessageId, 2)]);
        await queries.FailStreamMessageAsync(ServiceId, ProviderId, QueueId, failed.MessageId, 1, -1);
        var deadLetters = await ReadDeadLettersAsync(database);
        Assert.Empty(await GetMessagesAsync(queries));

        Assert.Equal([confirmed.MessageId, released.MessageId, failed.MessageId], dequeued.Select(static message => message.MessageId));
        Assert.Equal([released.MessageId], releasedAcks.Select(static ack => ack.MessageId));
        Assert.Equal(released.MessageId, redelivered.MessageId);
        Assert.Equal(2, redelivered.Dequeued);
        Assert.Equal([confirmed.MessageId, released.MessageId], confirmedAcks.Select(static ack => ack.MessageId).Order());
        Assert.Equal([failed.MessageId, expired.MessageId], deadLetters.Select(static message => message.MessageId));
        var evicted = Assert.Single(await ReadDeadLettersAsync(database));
        Assert.Equal(expired.MessageId, evicted.MessageId);
        Assert.Equal(TimeSpan.FromSeconds(RemovalTimeout), evicted.RemoveOn - evicted.DeadOn);
        Assert.Empty(await database.Storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM OrleansStreamMessage", cancellationToken));
    }

    private const int RemovalTimeout = 600;

    private static Task<IList<AdoNetStreamMessage>> GetMessagesAsync(RelationalOrleansQueries queries) =>
        queries.GetStreamMessagesAsync(ServiceId, ProviderId, QueueId, 10, 5, 600, RemovalTimeout, 0, 10);

    private static async Task<List<AdoNetStreamDeadLetter>> ReadDeadLettersAsync(RelationalStorageForTesting database) =>
        (await database.Storage.ReadAsync<AdoNetStreamDeadLetter>("SELECT * FROM OrleansStreamDeadLetter", TestContext.Current.CancellationToken))
            .OrderBy(static message => message.MessageId)
            .ToList();
}
