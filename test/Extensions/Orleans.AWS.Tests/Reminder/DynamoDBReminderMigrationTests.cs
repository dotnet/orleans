using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Reminders.DynamoDB;
using Orleans.Runtime;
using System.Collections.Immutable;
using TestExtensions;
using Xunit;

namespace AWSUtils.Tests.RemindersTest;

[TestCategory("Reminders"), TestCategory("AWS"), TestCategory("DynamoDb")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestSuite("Functional")]
[TestProvider("DynamoDB")]
[TestArea("Reminders")]
public sealed class DynamoDBReminderMigrationTests
{
    [Fact]
    public void V2KeyEncoding_IsSortableUnambiguousAndHasStableBuckets()
    {
        var grain = GrainId.Create("type/#", "key_+/");
        var otherGrain = GrainId.Create("type/#", "key_+");

        var first = DynamoDBReminderTable.GetV2SortKey(0, grain, "name/#_+");
        var last = DynamoDBReminderTable.GetV2SortKey(uint.MaxValue, grain, "name/#_+");

        Assert.StartsWith("R#00000000#", first, StringComparison.Ordinal);
        Assert.StartsWith("R#FFFFFFFF#", last, StringComparison.Ordinal);
        Assert.DoesNotContain("/", first, StringComparison.Ordinal);
        Assert.DoesNotContain("+", first, StringComparison.Ordinal);
        Assert.NotEqual(first, DynamoDBReminderTable.GetV2SortKey(0, otherGrain, "name/#_+"));
        Assert.NotEqual(first, DynamoDBReminderTable.GetV2SortKey(0, grain, "name/#_"));
        Assert.True(string.CompareOrdinal(first, last) < 0);
        var longNameKey = DynamoDBReminderTable.GetV2SortKey(1, grain, new string('x', 1_500));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(longNameKey) < 1_024);
        Assert.NotEqual(longNameKey, DynamoDBReminderTable.GetV2SortKey(1, grain, new string('x', 1_499) + "y"));
        Assert.Equal(
            DynamoDBReminderTable.GetV2PartitionKey("service/#", 0),
            DynamoDBReminderTable.GetV2PartitionKey("service/#", DynamoDBReminderTable.V2BucketCount));
        Assert.NotEqual(
            DynamoDBReminderTable.GetV2PartitionKey("service/#", 0),
            DynamoDBReminderTable.GetV2PartitionKey("service/#", 1));

        Assert.Equal(("R#00000000#", "R#FFFFFFFF#~"), DynamoDBReminderTable.GetV2RangeBounds(0, uint.MaxValue));
        Assert.Equal(("R#00000001#", "R#00000001#~"), DynamoDBReminderTable.GetV2RangeBounds(1, 1));
    }

    [Fact]
    public void MigrationOptions_ParseConnectionStringIncludesCustomV2Settings()
    {
        var options = new DynamoDBReminderStorageOptions();

        options.ParseConnectionString(
            "Service=us-east-2;TableName=custom-v1;V2TableName=custom-v2;TableMode=Migrate;MigrationPageSize=37");

        Assert.Equal("us-east-2", options.Service);
        Assert.Equal("custom-v1", options.TableName);
        Assert.Equal("custom-v2", options.V2TableName);
        Assert.Equal(DynamoDBReminderTableMode.Migrate, options.TableMode);
        Assert.Equal(37, options.MigrationPageSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task MigrationOptions_RejectNonpositivePageSize(int pageSize)
    {
        var table = CreateTable(NewTableName(), "invalid-options", DynamoDBReminderTableMode.Legacy, pageSize);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => table.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal("MigrationPageSize", exception.ParamName);
    }

    [Fact]
    public async Task Init_MigrateModeCreatesV2BaseTableSchema()
    {
        EnsureDynamoDb();
        var tableName = NewTableName();
        using var client = CreateClient();
        var table = CreateTable(tableName, "schema", DynamoDBReminderTableMode.Migrate);
        try
        {
            await table.StartAsync(TestContext.Current.CancellationToken);
            var description = (await client.DescribeTableAsync($"{tableName}-v2", TestContext.Current.CancellationToken)).Table;

            Assert.Collection(
                description.KeySchema,
                item =>
                {
                    Assert.Equal(DynamoDBReminderTable.V2PartitionKeyName, item.AttributeName);
                    Assert.Equal(KeyType.HASH, item.KeyType);
                },
                item =>
                {
                    Assert.Equal(DynamoDBReminderTable.V2SortKeyName, item.AttributeName);
                    Assert.Equal(KeyType.RANGE, item.KeyType);
                });
            Assert.True(description.GlobalSecondaryIndexes is null or { Count: 0 });
            Assert.Equal(2, description.AttributeDefinitions.Count);
        }
        finally
        {
            await StopAndDelete(client, tableName, table);
        }
    }

    [Fact]
    public async Task LegacyDiscovery_StrongPointValidationReturnsCurrentRowsAndDropsDeletes()
    {
        EnsureDynamoDb();
        var tableName = NewTableName();
        const string serviceId = "legacy-candidates";
        using var client = CreateClient();
        IReadOnlyList<ReminderEntry> discovery = [];
        var table = CreateTable(
            tableName,
            serviceId,
            DynamoDBReminderTableMode.Legacy,
            hooks: new() { LegacyDiscoveryResults = _ => discovery });
        try
        {
            await table.StartAsync(TestContext.Current.CancellationToken);
            var entry = Entry(1);
            var firstEtag = await table.UpsertRow(entry);
            discovery = [Clone(entry)];

            entry.StartAt = entry.StartAt.AddHours(3);
            entry.Period = TimeSpan.FromHours(2);
            var secondEtag = await table.UpsertRow(entry);
            var rangeResult = Assert.Single((await table.ReadRows(0, 0)).Reminders);
            var grainResult = Assert.Single((await table.ReadRows(entry.GrainId)).Reminders);
            Assert.Equal(secondEtag, rangeResult.ETag);
            Assert.Equal(entry.StartAt, rangeResult.StartAt);
            Assert.Equal(entry.Period, rangeResult.Period);
            Assert.Equal(secondEtag, grainResult.ETag);

            Assert.True(await table.RemoveRow(entry.GrainId, entry.ReminderName, secondEtag!));
            Assert.Empty((await table.ReadRows(0, 0)).Reminders);
            Assert.Empty((await table.ReadRows(entry.GrainId)).Reminders);
            Assert.NotEqual(firstEtag, secondEtag);
        }
        finally
        {
            await StopAndDelete(client, tableName, table);
        }
    }

    [Fact]
    public async Task LegacyMissingDiscoveryCandidateRemainsUndiscoverableWithoutSchemaChange()
    {
        EnsureDynamoDb();
        var tableName = NewTableName();
        using var client = CreateClient();
        var table = CreateTable(
            tableName,
            "legacy-missing",
            DynamoDBReminderTableMode.Legacy,
            hooks: new() { LegacyDiscoveryResults = _ => [] });
        try
        {
            await table.StartAsync(TestContext.Current.CancellationToken);
            var entry = Entry(1);
            await table.UpsertRow(entry);

            Assert.Empty((await table.ReadRows(0, 0)).Reminders);
            Assert.Empty((await table.ReadRows(entry.GrainId)).Reminders);
            var pointRead = Assert.IsType<ReminderEntry>(await table.ReadRow(entry.GrainId, entry.ReminderName));
            Assert.Equal(entry.GrainId, pointRead.GrainId);
            Assert.Equal(entry.ReminderName, pointRead.ReminderName);
            Assert.Equal(entry.ETag, pointRead.ETag);
        }
        finally
        {
            await StopAndDelete(client, tableName, table);
        }
    }

    [Fact]
    public async Task Migration_BackfillsPagesResumesAndPreservesLegacyETags()
    {
        EnsureDynamoDb();
        var tableName = NewTableName();
        const string serviceId = "resume";
        using var client = CreateClient();
        var legacy = CreateTable(tableName, serviceId, DynamoDBReminderTableMode.Legacy);
        DynamoDBReminderTable? interrupted = null;
        DynamoDBReminderTable? resumed = null;
        try
        {
            await legacy.StartAsync(TestContext.Current.CancellationToken);
            var entries = Enumerable.Range(0, 8).Select(index => Entry(index)).ToArray();
            entries[^1].ReminderName = new string('x', 1_500);
            foreach (var entry in entries)
            {
                await legacy.UpsertRow(entry);
            }

            var pages = 0;
            interrupted = CreateTable(
                tableName,
                serviceId,
                DynamoDBReminderTableMode.Migrate,
                pageSize: 2,
                hooks: new()
                {
                    AfterPageCheckpoint = () => Interlocked.Increment(ref pages) == 1
                        ? Task.FromException(new InjectedMigrationException())
                        : Task.CompletedTask,
                });
            await Assert.ThrowsAsync<InjectedMigrationException>(
                () => interrupted.StartAsync(TestContext.Current.CancellationToken));

            var state = await ReadState(client, tableName, serviceId);
            Assert.Equal("Backfilling", state["MigrationStatus"].S);
            Assert.True(state.ContainsKey("CheckpointReminderId"));
            Assert.True(state.ContainsKey("CheckpointGrainHash"));

            resumed = CreateTable(tableName, serviceId, DynamoDBReminderTableMode.Migrate, pageSize: 2);
            await resumed.StartAsync(TestContext.Current.CancellationToken);

            state = await ReadState(client, tableName, serviceId);
            Assert.Equal("Ready", state["MigrationStatus"].S);
            Assert.False(state.ContainsKey("CheckpointReminderId"));
            var v2Items = await ReadV2Items(client, tableName, serviceId);
            Assert.Equal(entries.Length, v2Items.Count);
            Assert.Equal(
                entries.Select(static entry => entry.ETag).OrderBy(static value => value, StringComparer.Ordinal),
                v2Items.Select(static item => Scalar(item["ETag"])).OrderBy(static value => value, StringComparer.Ordinal));
            foreach (var entry in entries)
            {
                var actual = Assert.Single(v2Items, item => item["GrainReference"].S == entry.GrainId.ToString());
                Assert.Equal(entry.StartAt, DateTime.Parse(actual["StartTime"].S));
                Assert.Equal(entry.Period, TimeSpan.Parse(actual["Period"].S));
            }
        }
        finally
        {
            await StopAndDelete(client, tableName, legacy, interrupted, resumed);
        }
    }

    [Fact]
    public async Task Migration_ConcurrentLegacyUpdateAndDeleteCannotResurrectStaleRows()
    {
        EnsureDynamoDb();
        var tableName = NewTableName();
        const string serviceId = "concurrent";
        using var client = CreateClient();
        var legacy = CreateTable(tableName, serviceId, DynamoDBReminderTableMode.Legacy);
        DynamoDBReminderTable? migration = null;
        try
        {
            await legacy.StartAsync(TestContext.Current.CancellationToken);
            var updated = Entry(1);
            var deleted = Entry(2);
            var staleDelete = Entry(3);
            await legacy.UpsertRow(updated);
            await legacy.UpsertRow(deleted);
            await legacy.UpsertRow(staleDelete);

            var pageRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var invoked = 0;
            migration = CreateTable(
                tableName,
                serviceId,
                DynamoDBReminderTableMode.Migrate,
                pageSize: 100,
                hooks: new()
                {
                    AfterLegacyPageRead = async () =>
                    {
                        if (Interlocked.Exchange(ref invoked, 1) == 0)
                        {
                            pageRead.SetResult();
                            await release.Task;
                        }
                    },
                });

            var migrationTask = migration.StartAsync(TestContext.Current.CancellationToken);
            await pageRead.Task.WaitAsync(TestContext.Current.CancellationToken);
            var deletedEtag = deleted.ETag!;
            updated.StartAt = updated.StartAt.AddDays(3);
            var updatedEtag = await legacy.UpsertRow(updated);
            staleDelete.Period = TimeSpan.FromDays(2);
            var staleDeleteEtag = await legacy.UpsertRow(staleDelete);
            Assert.True(await migration.RemoveRow(staleDelete.GrainId, staleDelete.ReminderName, staleDeleteEtag!));
            Assert.True(await legacy.RemoveRow(deleted.GrainId, deleted.ReminderName, deletedEtag));
            release.SetResult();
            await migrationTask;

            var v2Items = await ReadV2Items(client, tableName, serviceId);
            var actual = Assert.Single(v2Items);
            Assert.Equal(updated.GrainId.ToString(), actual["GrainReference"].S);
            Assert.Equal(updatedEtag, Scalar(actual["ETag"]));
            Assert.Equal(updated.StartAt, DateTime.Parse(actual["StartTime"].S));
        }
        finally
        {
            await StopAndDelete(client, tableName, legacy, migration);
        }
    }

    [Fact]
    public async Task Migration_LeaseContentionHasOneOwnerAndContenderRemainsDualWriteCapable()
    {
        EnsureDynamoDb();
        var tableName = NewTableName();
        const string serviceId = "lease";
        using var client = CreateClient();
        var legacy = CreateTable(tableName, serviceId, DynamoDBReminderTableMode.Legacy);
        DynamoDBReminderTable? owner = null;
        DynamoDBReminderTable? contender = null;
        try
        {
            await legacy.StartAsync(TestContext.Current.CancellationToken);
            await legacy.UpsertRow(Entry(1));
            var pageRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner = CreateTable(
                tableName,
                serviceId,
                DynamoDBReminderTableMode.Migrate,
                hooks: new()
                {
                    AfterLegacyPageRead = async () =>
                    {
                        pageRead.SetResult();
                        await release.Task;
                    },
                });
            var ownerTask = owner.StartAsync(TestContext.Current.CancellationToken);
            await pageRead.Task.WaitAsync(TestContext.Current.CancellationToken);

            contender = CreateTable(tableName, serviceId, DynamoDBReminderTableMode.Migrate);
            await contender.StartAsync(TestContext.Current.CancellationToken);
            var contenderEntry = Entry(2);
            var etag = await contender.UpsertRow(contenderEntry);
            Assert.NotNull(etag);

            release.SetResult();
            await ownerTask;
            Assert.Equal(2, (await ReadV2Items(client, tableName, serviceId)).Count);
        }
        finally
        {
            await StopAndDelete(client, tableName, legacy, owner, contender);
        }
    }

    [Fact]
    public async Task Migration_VerificationFailureIsPersistedAndPreventsCutover()
    {
        EnsureDynamoDb();
        var tableName = NewTableName();
        const string serviceId = "verification";
        using var client = CreateClient();
        var legacy = CreateTable(tableName, serviceId, DynamoDBReminderTableMode.Legacy);
        DynamoDBReminderTable? migration = null;
        try
        {
            await legacy.StartAsync(TestContext.Current.CancellationToken);
            var entry = Entry(1);
            await legacy.UpsertRow(entry);
            migration = CreateTable(
                tableName,
                serviceId,
                DynamoDBReminderTableMode.Migrate,
                hooks: new()
                {
                    BeforeVerification = async () =>
                    {
                        await client.DeleteItemAsync(
                            new()
                            {
                                TableName = $"{tableName}-v2",
                                Key = V2Key(serviceId, entry),
                            },
                            TestContext.Current.CancellationToken);
                    },
                });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => migration.StartAsync(TestContext.Current.CancellationToken));
            Assert.Contains("verification failed", exception.Message, StringComparison.OrdinalIgnoreCase);
            var state = await ReadState(client, tableName, serviceId);
            Assert.Equal("VerificationFailed", state["MigrationStatus"].S);
            Assert.Equal("1", state["SourceCount"].N);
            Assert.Equal("0", state["TargetCount"].N);
            Assert.NotNull(await legacy.ReadRow(entry.GrainId, entry.ReminderName));
        }
        finally
        {
            await StopAndDelete(client, tableName, legacy, migration);
        }
    }

    [Fact]
    public async Task Migration_ExpiredLeaseCannotPublishCutover()
    {
        EnsureDynamoDb();
        var tableName = NewTableName();
        const string serviceId = "expired-lease";
        using var client = CreateClient();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero));
        var cluster = CompatibleCluster();
        var legacy = CreateTable(tableName, serviceId, DynamoDBReminderTableMode.Legacy);
        var table = CreateTable(
            tableName,
            serviceId,
            DynamoDBReminderTableMode.V2,
            hooks: new()
            {
                AfterLegacyPageRead = () =>
                {
                    clock.Advance(TimeSpan.FromMinutes(3));
                    return Task.CompletedTask;
                },
            },
            membership: cluster.Membership,
            local: cluster.Local,
            timeProvider: clock);
        try
        {
            await legacy.StartAsync(TestContext.Current.CancellationToken);
            await legacy.UpsertRow(Entry(1));
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => table.StartAsync(TestContext.Current.CancellationToken));
            Assert.Contains("lost while copying", exception.Message, StringComparison.Ordinal);
            Assert.NotEqual("Cutover", (await ReadState(client, tableName, serviceId))["MigrationStatus"].S);
        }
        finally
        {
            await StopAndDelete(client, tableName, legacy, table);
        }
    }

    [Fact]
    public async Task MixedVersionPrepareCutoverAndRollbackPreserveStrongReadsAndETags()
    {
        EnsureDynamoDb();
        var tableName = NewTableName();
        const string serviceId = "cutover";
        using var client = CreateClient();
        var legacy = CreateTable(tableName, serviceId, DynamoDBReminderTableMode.Legacy);
        DynamoDBReminderTable? prepare = null;
        DynamoDBReminderTable? cutover = null;
        DynamoDBReminderTable? failedRollback = null;
        DynamoDBReminderTable? rollback = null;
        try
        {
            await legacy.StartAsync(TestContext.Current.CancellationToken);
            var oldEntry = Entry(1);
            await legacy.UpsertRow(oldEntry);
            prepare = CreateTable(tableName, serviceId, DynamoDBReminderTableMode.Migrate);
            await prepare.StartAsync(TestContext.Current.CancellationToken);

            var oldBinaryEntry = Entry(2);
            await legacy.UpsertRow(oldBinaryEntry);
            var cluster = CompatibleCluster();
            cutover = CreateTable(
                tableName,
                serviceId,
                DynamoDBReminderTableMode.V2,
                membership: cluster.Membership,
                local: cluster.Local);
            await cutover.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal("Cutover", (await ReadState(client, tableName, serviceId))["MigrationStatus"].S);

            var current = Entry(3);
            var firstEtag = await cutover.UpsertRow(current);
            var point = Assert.IsType<ReminderEntry>(await cutover.ReadRow(current.GrainId, current.ReminderName));
            Assert.Equal(firstEtag, point.ETag);
            Assert.Contains((await cutover.ReadRows(current.GrainId)).Reminders, item => item.ETag == firstEtag);
            Assert.Contains((await cutover.ReadRows(0, 0)).Reminders, item => item.ETag == firstEtag);

            current.Period = TimeSpan.FromMinutes(99);
            var secondEtag = await cutover.UpsertRow(current);
            Assert.NotEqual(firstEtag, secondEtag);
            Assert.False(await cutover.RemoveRow(current.GrainId, current.ReminderName, firstEtag!));
            Assert.True(await cutover.RemoveRow(current.GrainId, current.ReminderName, secondEtag!));
            Assert.Null(await cutover.ReadRow(current.GrainId, current.ReminderName));
            Assert.DoesNotContain((await cutover.ReadRows(current.GrainId)).Reminders, item => item.ReminderName == current.ReminderName);
            Assert.DoesNotContain((await cutover.ReadRows(0, 0)).Reminders, item => item.ReminderName == current.ReminderName);

            failedRollback = CreateTable(
                tableName,
                serviceId,
                DynamoDBReminderTableMode.Rollback,
                hooks: new()
                {
                    BeforeVerification = async () =>
                    {
                        await client.DeleteItemAsync(
                            new()
                            {
                                TableName = $"{tableName}-v2",
                                Key = V2Key(serviceId, oldEntry),
                            },
                            TestContext.Current.CancellationToken);
                    },
                },
                membership: cluster.Membership,
                local: cluster.Local);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => failedRollback.StartAsync(TestContext.Current.CancellationToken));
            Assert.Equal("Cutover", (await ReadState(client, tableName, serviceId))["MigrationStatus"].S);

            rollback = CreateTable(
                tableName,
                serviceId,
                DynamoDBReminderTableMode.Rollback,
                membership: cluster.Membership,
                local: cluster.Local);
            await rollback.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal("RolledBack", (await ReadState(client, tableName, serviceId))["MigrationStatus"].S);
            Assert.NotNull(await rollback.ReadRow(oldEntry.GrainId, oldEntry.ReminderName));
            Assert.NotNull(await rollback.ReadRow(oldBinaryEntry.GrainId, oldBinaryEntry.ReminderName));
        }
        finally
        {
            await StopAndDelete(client, tableName, legacy, prepare, cutover, failedRollback, rollback);
        }
    }

    [Fact]
    public async Task V2RangeReads_AreBeginExclusiveEndInclusiveForNormalAndWrapRanges()
    {
        EnsureDynamoDb();
        var tableName = NewTableName();
        const string serviceId = "ranges";
        using var client = CreateClient();
        var prepare = CreateTable(tableName, serviceId, DynamoDBReminderTableMode.Migrate);
        DynamoDBReminderTable? cutover = null;
        try
        {
            await prepare.StartAsync(TestContext.Current.CancellationToken);
            var entries = Enumerable.Range(0, 8)
                .Select(Entry)
                .OrderBy(static entry => entry.GrainId.GetUniformHashCode())
                .ToArray();
            foreach (var entry in entries)
            {
                await prepare.UpsertRow(entry);
            }

            var cluster = CompatibleCluster();
            cutover = CreateTable(
                tableName,
                serviceId,
                DynamoDBReminderTableMode.V2,
                membership: cluster.Membership,
                local: cluster.Local);
            await cutover.StartAsync(TestContext.Current.CancellationToken);

            var normal = await cutover.ReadRows(
                entries[1].GrainId.GetUniformHashCode(),
                entries[5].GrainId.GetUniformHashCode());
            Assert.Equal(
                entries[2..6].Select(static entry => entry.GrainId.ToString()).OrderBy(static id => id, StringComparer.Ordinal),
                normal.Reminders.Select(static entry => entry.GrainId.ToString()).OrderBy(static id => id, StringComparer.Ordinal));

            var wrap = await cutover.ReadRows(
                entries[5].GrainId.GetUniformHashCode(),
                entries[1].GrainId.GetUniformHashCode());
            Assert.Equal(
                entries[6..].Concat(entries[..2]).Select(static entry => entry.GrainId.ToString()).OrderBy(static id => id, StringComparer.Ordinal),
                wrap.Reminders.Select(static entry => entry.GrainId.ToString()).OrderBy(static id => id, StringComparer.Ordinal));

            var fullRing = await cutover.ReadRows(entries[3].GrainId.GetUniformHashCode(), entries[3].GrainId.GetUniformHashCode());
            Assert.Equal(entries.Length, fullRing.Reminders.Count);
        }
        finally
        {
            await StopAndDelete(client, tableName, prepare, cutover);
        }
    }

    [Theory]
    [InlineData(SiloStatus.Created)]
    [InlineData(SiloStatus.Joining)]
    [InlineData(SiloStatus.Active)]
    public async Task Cutover_FailsClosedWhenANonterminalSiloHasNoCompatibilityMarker(SiloStatus incompatibleStatus)
    {
        EnsureDynamoDb();
        var tableName = NewTableName();
        using var client = CreateClient();
        var cluster = CompatibleCluster(incompatibleStatus);
        var table = CreateTable(
            tableName,
            "mixed",
            DynamoDBReminderTableMode.V2,
            membership: cluster.Membership,
            local: cluster.Local);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => table.StartAsync(TestContext.Current.CancellationToken));
            Assert.Contains("did not publish V2 compatibility markers", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await StopAndDelete(client, tableName, table);
        }
    }

    [Fact]
    public async Task V2Only_RetiresLegacyTableAndExistingV2InstancesFollowTheFence()
    {
        EnsureDynamoDb();
        var tableName = NewTableName();
        const string serviceId = "retirement";
        using var client = CreateClient();
        var prepare = CreateTable(tableName, serviceId, DynamoDBReminderTableMode.Migrate);
        DynamoDBReminderTable? cutover = null;
        DynamoDBReminderTable? retirement = null;
        DynamoDBReminderTable? restarted = null;
        try
        {
            await prepare.StartAsync(TestContext.Current.CancellationToken);
            var retained = Entry(1);
            await prepare.UpsertRow(retained);
            var cluster = CompatibleCluster();
            cutover = CreateTable(
                tableName,
                serviceId,
                DynamoDBReminderTableMode.V2,
                membership: cluster.Membership,
                local: cluster.Local);
            await cutover.StartAsync(TestContext.Current.CancellationToken);

            retirement = CreateTable(
                tableName,
                serviceId,
                DynamoDBReminderTableMode.V2Only,
                membership: cluster.Membership,
                local: cluster.Local);
            await retirement.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal("Retired", (await ReadState(client, tableName, serviceId))["MigrationStatus"].S);

            await client.DeleteTableAsync(new DeleteTableRequest { TableName = tableName }, TestContext.Current.CancellationToken);

            var afterRetirement = Entry(2);
            var etag = await cutover.UpsertRow(afterRetirement);
            Assert.Equal(etag, (await cutover.ReadRow(afterRetirement.GrainId, afterRetirement.ReminderName))?.ETag);

            restarted = CreateTable(
                tableName,
                serviceId,
                DynamoDBReminderTableMode.V2,
                membership: cluster.Membership,
                local: cluster.Local);
            await restarted.StartAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(await restarted.ReadRow(retained.GrainId, retained.ReminderName));
            await Assert.ThrowsAsync<ResourceNotFoundException>(
                () => client.DescribeTableAsync(tableName, TestContext.Current.CancellationToken));
            Assert.True(await restarted.RemoveRow(afterRetirement.GrainId, afterRetirement.ReminderName, etag!));
        }
        finally
        {
            await StopAndDelete(client, tableName, prepare, cutover, retirement, restarted);
        }
    }

    [Fact]
    public async Task Migration_IsServiceIsolatedAndBackfillsAllPaginatedRows()
    {
        EnsureDynamoDb();
        var tableName = NewTableName();
        using var client = CreateClient();
        var serviceA = CreateTable(tableName, "service-a", DynamoDBReminderTableMode.Legacy);
        var serviceB = CreateTable(tableName, "service-b", DynamoDBReminderTableMode.Legacy);
        DynamoDBReminderTable? migrationA = null;
        try
        {
            await serviceA.StartAsync(TestContext.Current.CancellationToken);
            await serviceB.StartAsync(TestContext.Current.CancellationToken);
            for (var index = 0; index < 12; index++)
            {
                await serviceA.UpsertRow(Entry(index));
                await serviceB.UpsertRow(Entry(index));
            }

            var checkpoints = 0;
            migrationA = CreateTable(
                tableName,
                "service-a",
                DynamoDBReminderTableMode.Migrate,
                pageSize: 1,
                hooks: new() { AfterPageCheckpoint = () => { checkpoints++; return Task.CompletedTask; } });
            await migrationA.StartAsync(TestContext.Current.CancellationToken);

            Assert.True(checkpoints >= 12);
            Assert.Equal(12, (await ReadV2Items(client, tableName, "service-a")).Count);
            Assert.Empty(await ReadV2Items(client, tableName, "service-b"));
        }
        finally
        {
            await StopAndDelete(client, tableName, serviceA, serviceB, migrationA);
        }
    }

    private static DynamoDBReminderTable CreateTable(
        string tableName,
        string serviceId,
        DynamoDBReminderTableMode mode,
        int pageSize = 100,
        DynamoDBReminderMigrationTestHooks? hooks = null,
        IClusterMembershipService? membership = null,
        ILocalSiloDetails? local = null,
        TimeProvider? timeProvider = null)
    {
        var options = new DynamoDBReminderStorageOptions
        {
            Service = AWSTestConstants.DynamoDbService,
            AccessKey = AWSTestConstants.DynamoDbAccessKey,
            SecretKey = AWSTestConstants.DynamoDbSecretKey,
            TableName = tableName,
            TableMode = mode,
            MigrationPageSize = pageSize,
            CreateIfNotExists = true,
            UpdateIfExists = false,
            UseProvisionedThroughput = false,
        };
        return new(
            NullLoggerFactory.Instance,
            Options.Create(new ClusterOptions { ClusterId = serviceId, ServiceId = serviceId }),
            Options.Create(options),
            membership,
            local,
            timeProvider ?? TimeProvider.System,
            hooks);
    }

    private static ReminderEntry Entry(int index)
        => new()
        {
            GrainId = GrainId.Create("migration", $"grain-{index:D4}"),
            ReminderName = $"reminder/#_{index:D4}",
            StartAt = new DateTime(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc).AddMinutes(index),
            Period = TimeSpan.FromMinutes(index + 1),
        };

    private static ReminderEntry Clone(ReminderEntry entry)
        => new()
        {
            GrainId = entry.GrainId,
            ReminderName = entry.ReminderName,
            StartAt = entry.StartAt,
            Period = entry.Period,
            ETag = entry.ETag,
        };

    private static Dictionary<string, AttributeValue> V2Key(string serviceId, ReminderEntry entry)
    {
        var hash = entry.GrainId.GetUniformHashCode();
        return new()
        {
            [DynamoDBReminderTable.V2PartitionKeyName] = new(DynamoDBReminderTable.GetV2PartitionKey(serviceId, hash)),
            [DynamoDBReminderTable.V2SortKeyName] = new(DynamoDBReminderTable.GetV2SortKey(hash, entry.GrainId, entry.ReminderName)),
        };
    }

    private static async Task<Dictionary<string, AttributeValue>> ReadState(
        AmazonDynamoDBClient client,
        string tableName,
        string serviceId)
    {
        var response = await client.ScanAsync(
            new()
            {
                TableName = $"{tableName}-v2",
                ConsistentRead = true,
                FilterExpression = "MigrationStatus = :status OR attribute_exists(MigrationStatus)",
                ExpressionAttributeValues = new() { [":status"] = new("unused") },
            },
            TestContext.Current.CancellationToken);
        return Assert.Single(response.Items, item => item["SortKey"].S == "STATE" && item["PartitionKey"].S.StartsWith("M#", StringComparison.Ordinal));
    }

    private static async Task<List<Dictionary<string, AttributeValue>>> ReadV2Items(
        AmazonDynamoDBClient client,
        string tableName,
        string serviceId)
    {
        var result = new List<Dictionary<string, AttributeValue>>();
        for (uint bucket = 0; bucket < DynamoDBReminderTable.V2BucketCount; bucket++)
        {
            var partition = DynamoDBReminderTable.GetV2PartitionKey(serviceId, bucket);
            var response = await client.QueryAsync(
                new()
                {
                    TableName = $"{tableName}-v2",
                    ConsistentRead = true,
                    KeyConditionExpression = "PartitionKey = :partition",
                    ExpressionAttributeValues = new() { [":partition"] = new(partition) },
                },
                TestContext.Current.CancellationToken);
            result.AddRange(response.Items);
        }

        return result;
    }

    private static string Scalar(AttributeValue value) => value.S ?? value.N;

    private static (IClusterMembershipService Membership, ILocalSiloDetails Local) CompatibleCluster(SiloStatus? incompatibleStatus = null)
    {
        var localAddress = SiloAddress.FromParsableString("127.0.0.1:21111@100");
        var members = ImmutableDictionary<SiloAddress, ClusterMember>.Empty
            .Add(localAddress, new(localAddress, SiloStatus.Active, "local"));
        if (incompatibleStatus is { } status)
        {
            var oldAddress = SiloAddress.FromParsableString("127.0.0.1:21112@101");
            members = members.Add(oldAddress, new(oldAddress, status, "old"));
        }

        return (new TestMembershipService(new(members, new(1))), new TestLocalSiloDetails(localAddress));
    }

    private static string NewTableName() => $"OrleansReminders-{Guid.NewGuid():N}";

    private static void EnsureDynamoDb()
    {
        if (!AWSTestConstants.IsDynamoDbAvailable)
        {
            throw Xunit.Sdk.SkipException.ForSkip("Unable to connect to AWS DynamoDB simulator");
        }
    }

    private static AmazonDynamoDBClient CreateClient()
    {
        var service = AWSTestConstants.DynamoDbService;
        if (service.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || service.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new(new BasicAWSCredentials("dummy", "dummyKey"), new AmazonDynamoDBConfig { ServiceURL = service });
        }

        var config = new AmazonDynamoDBConfig { RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(service) };
        return string.IsNullOrEmpty(AWSTestConstants.DynamoDbAccessKey)
            || string.IsNullOrEmpty(AWSTestConstants.DynamoDbSecretKey)
                ? new(config)
                : new(
                    new BasicAWSCredentials(AWSTestConstants.DynamoDbAccessKey, AWSTestConstants.DynamoDbSecretKey),
                    config);
    }

    private static async Task StopAndDelete(AmazonDynamoDBClient client, string tableName, params DynamoDBReminderTable?[] tables)
    {
        foreach (var table in tables)
        {
            if (table is not null)
            {
                await table.StopAsync();
            }

        }

        foreach (var name in new[] { tableName, $"{tableName}-v2" })
        {
            try
            {
                await client.DeleteTableAsync(new DeleteTableRequest { TableName = name });
            }
            catch (ResourceNotFoundException)
            {
            }
        }
    }

    private sealed class InjectedMigrationException : Exception
    {
    }

    private sealed class TestLocalSiloDetails(SiloAddress address) : ILocalSiloDetails
    {
        public string Name => "test";
        public string ClusterId => "test";
        public string DnsHostName => "localhost";
        public SiloAddress SiloAddress => address;
        public SiloAddress GatewayAddress => address;
    }

    private sealed class TestMembershipService(ClusterMembershipSnapshot snapshot) : IClusterMembershipService
    {
        public ClusterMembershipSnapshot CurrentSnapshot => snapshot;

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => Empty();

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public Task<bool> TryKill(SiloAddress siloAddress) => Task.FromResult(false);

        private static async IAsyncEnumerable<ClusterMembershipSnapshot> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }

    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan amount) => utcNow += amount;
    }
}
