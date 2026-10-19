using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Reminders.DynamoDB;
using Orleans.Runtime;
using Xunit;

namespace AWSUtils.Tests.RemindersTest;

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Reminders")]
[TestCategory("AWS"), TestCategory("DynamoDB"), TestCategory("BVT")]
public sealed class DynamoDBReminderStateTests
{
    [Theory]
    [InlineData(DynamoDBReminderTableMode.V2)]
    [InlineData(DynamoDBReminderTableMode.V2Only)]
    [InlineData(DynamoDBReminderTableMode.Rollback)]
    public async Task MissingStateStartupDoesNotInitializeLegacyOrMutate(DynamoDBReminderTableMode mode)
    {
        var described = new List<string>();
        using var client = new RequestClient
        {
            Read = (_, _) => Task.FromResult(new GetItemResponse()),
            Describe = tableName =>
            {
                described.Add(tableName);
                return new() { Table = new() { TableName = tableName, TableStatus = TableStatus.ACTIVE } };
            },
        };
        var table = CreateTable(client, mode, createIfNotExists: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => table.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("requires existing migration metadata", error.Message, StringComparison.Ordinal);
        Assert.Equal(["reminders-v2"], described);
        Assert.Equal(1, client.ReadCount);
        Assert.Equal(0, client.CreateCount);
        Assert.Equal(0, client.WriteCount);
        Assert.Equal(0, client.DeleteCount);
    }

    [Fact]
    public async Task ProductionRegistrationBindsMembershipSiloAndClockThroughStartup()
    {
        var address = SiloAddress.FromParsableString("127.0.0.1:21111@100");
        var local = new RegistrationLocalSiloDetails(address);
        var membership = new RegistrationMembershipService(address);
        var clock = new RegistrationTimeProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILocalSiloDetails>(local);
        services.AddSingleton<IClusterMembershipService>(membership);
        services.AddSingleton<TimeProvider>(clock);
        services.Configure<ClusterOptions>(options => { options.ClusterId = "cluster"; options.ServiceId = "service"; });
        services.UseDynamoDBReminderService(options =>
        {
            options.Service = "http://localhost";
            options.TableName = "reminders";
            options.TableMode = DynamoDBReminderTableMode.V2;
            options.CreateIfNotExists = false;
            options.UpdateIfExists = false;
        });
        using var provider = services.BuildServiceProvider();
        var table = Assert.IsType<DynamoDBReminderTable>(provider.GetRequiredService<IReminderTable>());
        using var client = new RequestClient
        {
            Read = (_, _) => Task.FromResult(new GetItemResponse { Item = StateItem("Retired") }),
            Query = _ => new()
            {
                Items =
                [
                    new()
                    {
                        ["ServiceId"] = new("service"),
                        ["LeaseOwner"] = new(address.ToParsableString()),
                        ["ExpiresAt"] = new() { N = clock.GetUtcNow().AddMinutes(5).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) },
                    },
                ],
            },
        };
        Assert.Same(local, typeof(DynamoDBReminderTable).GetField("localSiloDetails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table));
        Assert.Same(membership, typeof(DynamoDBReminderTable).GetField("membershipService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table));
        Assert.Same(clock, typeof(DynamoDBReminderTable).GetField("timeProvider", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table));
        // Replace only the backend client after production DI construction.
        typeof(DynamoDBReminderTable).GetField("testHooks", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
            table, new DynamoDBReminderMigrationTestHooks { ConfigureStorage = storage => AttachClient(storage, client) });
        try
        {
            await table.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, membership.RefreshCount);
            Assert.Equal(1, client.WriteCount);
            Assert.Equal(address.ToParsableString(), typeof(DynamoDBReminderTable).GetField("migrationOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table));
            Assert.Same(table, provider.GetRequiredService<IReminderTable>());
        }
        finally
        {
            await table.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task LegacyStartupAcceptsMaximumLengthTableNameWithoutProbingAnInvalidDerivedName()
    {
        using var client = new RequestClient();
        var table = CreateTable(client, DynamoDBReminderTableMode.Legacy, new string('a', 255));

        await table.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, client.ReadCount);
    }

    [Fact]
    public async Task LegacyStartupExplainsRequiredMigrationStatePermissionAndPreservesAccessDenial()
    {
        var denied = new AmazonDynamoDBException("Access denied.") { ErrorCode = "AccessDeniedException" };
        using var client = new RequestClient { Read = (_, _) => Task.FromException<GetItemResponse>(denied) };
        var table = CreateTable(client, DynamoDBReminderTableMode.Legacy);

        var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(
            () => table.StartAsync(TestContext.Current.CancellationToken));

        Assert.Same(denied, exception.InnerException);
        Assert.Contains("dynamodb:GetItem", exception.Message, StringComparison.Ordinal);
        Assert.Contains("reminders-v2", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, client.ReadCount);
    }

    [Fact]
    public async Task LegacyStartupAcceptsAnUnprovisionedMigrationTable()
    {
        using var client = new RequestClient
        {
            Read = (_, _) => Task.FromException<GetItemResponse>(new ResourceNotFoundException("Missing table.")),
        };
        var table = CreateTable(client, DynamoDBReminderTableMode.Legacy);

        await table.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, client.ReadCount);
    }

    [Theory]
    [InlineData(DynamoDBReminderTableMode.Legacy, 1)]
    [InlineData(DynamoDBReminderTableMode.Migrate, 1)]
    [InlineData(DynamoDBReminderTableMode.Migrate, 2)]
    public async Task StartupStateReadsForwardCancellation(DynamoDBReminderTableMode mode, int blockedRead)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        using var client = new RequestClient
        {
            Read = async (_, token) =>
            {
                if (++reads == blockedRead)
                {
                    entered.SetResult(token);
                    await release.Task.WaitAsync(token);
                }

                return new();
            },
        };
        var table = CreateTable(client, mode);
        var startup = table.StartAsync(cancellation.Token);
        try
        {
            var forwarded = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(cancellation.Token, forwarded);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(blockedRead, client.ReadCount);
        }
        finally
        {
            await cancellation.CancelAsync();
            release.TrySetResult();
            await table.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(DynamoDBReminderTableMode.V2)]
    [InlineData(DynamoDBReminderTableMode.V2Only)]
    [InlineData(DynamoDBReminderTableMode.Rollback)]
    public async Task MissingStateRejectsReadsAndMutations(DynamoDBReminderTableMode mode)
    {
        using var client = new RequestClient { Read = (_, _) => Task.FromResult(new GetItemResponse()) };
        var table = CreateTable(client, mode);
        AttachStorage(table, client);
        var entry = Entry();

        await Assert.ThrowsAsync<InvalidOperationException>(() => table.ReadRow(entry.GrainId, entry.ReminderName));
        await Assert.ThrowsAsync<InvalidOperationException>(() => table.UpsertRow(entry));
        await Assert.ThrowsAsync<InvalidOperationException>(() => table.RemoveRow(entry.GrainId, entry.ReminderName, "1"));

        Assert.Equal(0, client.WriteCount);
    }

    [Fact]
    public async Task RollbackRechecksMissingStateAfterAcquiringTheLease()
    {
        var reads = 0;
        using var client = new RequestClient
        {
            Read = (_, _) => Task.FromResult(new GetItemResponse { Item = ++reads < 3 ? StateItem("Cutover") : null }),
        };
        var table = CreateTable(client, DynamoDBReminderTableMode.Rollback);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => table.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("requires existing migration metadata", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, client.ReadCount);
        Assert.Equal(2, client.WriteCount);
        Assert.Equal(1, client.DeleteCount);
    }

    [Theory]
    [InlineData("ServiceId")]
    [InlineData("GrainReference")]
    [InlineData("ReminderName")]
    public async Task LegacyDiscoveryRejectsConflictingCandidateIdentity(string attribute)
    {
        var entry = Entry();
        var stored = RowItem(entry);
        using var client = new RequestClient
        {
            Read = (request, _) => Task.FromResult(new GetItemResponse { Item = request.TableName == "reminders-v2" ? null : stored }),
            Query = _ => new() { Items = [RowItem(entry)] },
        };
        var table = CreateTable(client, DynamoDBReminderTableMode.Legacy);
        await table.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(entry.GrainId, Assert.Single((await table.ReadRows(entry.GrainId)).Reminders).GrainId);

        stored[attribute] = new("conflicting-identity");

        var grainError = await Assert.ThrowsAsync<InvalidOperationException>(() => table.ReadRows(entry.GrainId));
        var rangeError = await Assert.ThrowsAsync<InvalidOperationException>(() => table.ReadRows(1, 10));
        Assert.Contains("conflicting stored identity", grainError.Message, StringComparison.Ordinal);
        Assert.Contains("conflicting stored identity", rangeError.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.WriteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationCopyFencesCompleteSourceIdentityAndPreservesEtagType(bool stringEtag)
    {
        var entry = Entry();
        var item = LegacyItem(entry);
        if (stringEtag)
        {
            item["ETag"] = new("legacy-etag");
        }

        using var client = new RequestClient
        {
            Transact = request =>
            {
                Assert.Equal(3, request.TransactItems.Count);
                var source = request.TransactItems[1].ConditionCheck;
                Assert.Equal("reminders", source.TableName);
                Assert.Equal(item["ReminderId"].S, source.Key["ReminderId"].S);
                Assert.Equal(item["GrainHash"].N, source.Key["GrainHash"].N);
                Assert.Equal(
                    "ServiceId = :service AND GrainReference = :grain AND ReminderName = :reminder AND ETag = :currentETag",
                    source.ConditionExpression);
                Assert.Equal("service", source.ExpressionAttributeValues[":service"].S);
                Assert.Equal(entry.GrainId.ToString(), source.ExpressionAttributeValues[":grain"].S);
                Assert.Equal(entry.ReminderName, source.ExpressionAttributeValues[":reminder"].S);
                Assert.Equal(item["ETag"].S, source.ExpressionAttributeValues[":currentETag"].S);
                Assert.Equal(item["ETag"].N, source.ExpressionAttributeValues[":currentETag"].N);
                var target = request.TransactItems[2].Put.Item;
                Assert.Equal(item["ETag"].S, target["ETag"].S);
                Assert.Equal(item["ETag"].N, target["ETag"].N);
                return new();
            },
        };
        var table = CreateTable(client, DynamoDBReminderTableMode.Migrate);
        AttachStorage(table, client);
        typeof(DynamoDBReminderTable).GetField("leaseToken", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, "lease");

        Assert.True(await CopyLegacyRecord(table, item));

        Assert.Equal(1, client.WriteCount);
    }

    [Theory]
    [InlineData("ServiceId")]
    [InlineData("ReminderId")]
    [InlineData("GrainHash")]
    [InlineData("GrainReference")]
    [InlineData("ReminderName")]
    public async Task MigrationCopyRejectsConflictingSourceKeyOrServiceBeforeTransaction(string attribute)
    {
        var entry = Entry();
        var item = LegacyItem(entry);
        item[attribute] = attribute switch
        {
            "GrainHash" => new() { N = unchecked(entry.GrainId.GetUniformHashCode() + 1).ToString(CultureInfo.InvariantCulture) },
            "GrainReference" => new(GrainId.Create("state-test", "another-grain").ToString()),
            _ => new("conflicting-identity"),
        };
        using var client = new RequestClient();
        var table = CreateTable(client, DynamoDBReminderTableMode.Migrate);
        AttachStorage(table, client);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CopyLegacyRecord(table, item));

        Assert.Contains(attribute == "ServiceId" ? "stored service identity" : "stored key", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.WriteCount);
        Assert.Equal(0, client.ReadCount);
        Assert.Equal(0, client.DeleteCount);
    }

    [Theory]
    [InlineData(DynamoDBReminderTableMode.Legacy)]
    [InlineData(DynamoDBReminderTableMode.Migrate)]
    public async Task LegacyAndDualPutsConditionOnCompleteIdentity(DynamoDBReminderTableMode mode)
    {
        var entry = Entry();
        void AssertPut(Put put)
        {
            Assert.Equal(
                $"attribute_not_exists({(put.TableName == "reminders" ? "ReminderId" : "PartitionKey")}) OR (ServiceId = :service AND GrainReference = :grain AND ReminderName = :reminder)",
                put.ConditionExpression);
            Assert.Equal("service", put.ExpressionAttributeValues[":service"].S);
            Assert.Equal(entry.GrainId.ToString(), put.ExpressionAttributeValues[":grain"].S);
            Assert.Equal(entry.ReminderName, put.ExpressionAttributeValues[":reminder"].S);
        }

        using var client = new RequestClient
        {
            Read = (_, _) => Task.FromResult(new GetItemResponse { Item = StateItem("Ready") }),
            Put = request =>
            {
                AssertPut(new() { TableName = request.TableName, Item = request.Item, ConditionExpression = request.ConditionExpression, ExpressionAttributeValues = request.ExpressionAttributeValues });
                return new();
            },
            Transact = request =>
            {
                Assert.Equal(3, request.TransactItems.Count);
                Assert.NotNull(request.TransactItems[0].ConditionCheck);
                AssertPut(request.TransactItems[1].Put);
                AssertPut(request.TransactItems[2].Put);
                return new();
            },
        };
        var table = CreateTable(client, mode);
        AttachStorage(table, client);

        var etag = await table.UpsertRow(entry);

        Assert.False(string.IsNullOrEmpty(etag));
        Assert.Equal(etag, entry.ETag);
        Assert.Equal(1, client.WriteCount);
    }

    [Theory]
    [InlineData(DynamoDBReminderTableMode.Legacy, "ServiceId")]
    [InlineData(DynamoDBReminderTableMode.Legacy, "GrainReference")]
    [InlineData(DynamoDBReminderTableMode.Legacy, "ReminderName")]
    [InlineData(DynamoDBReminderTableMode.Migrate, "ServiceId")]
    [InlineData(DynamoDBReminderTableMode.Migrate, "GrainReference")]
    [InlineData(DynamoDBReminderTableMode.Migrate, "ReminderName")]
    [InlineData(DynamoDBReminderTableMode.Rollback, "ServiceId")]
    [InlineData(DynamoDBReminderTableMode.Rollback, "GrainReference")]
    [InlineData(DynamoDBReminderTableMode.Rollback, "ReminderName")]
    public async Task LegacyPointReadsValidateIdentityAndPreserveMissingRowSemantics(DynamoDBReminderTableMode mode, string attribute)
    {
        var entry = Entry();
        Dictionary<string, AttributeValue>? stored = RowItem(entry);
        using var client = new RequestClient
        {
            Read = (request, _) => Task.FromResult(new GetItemResponse
            {
                Item = request.TableName == "reminders-v2"
                    ? StateItem(mode == DynamoDBReminderTableMode.Rollback ? "RolledBack" : "Ready")
                    : stored,
            }),
        };
        var table = CreateTable(client, mode);
        AttachStorage(table, client);
        var actual = Assert.IsType<ReminderEntry>(await table.ReadRow(entry.GrainId, entry.ReminderName));
        Assert.Equal(entry.GrainId, actual.GrainId);
        Assert.Equal(entry.ReminderName, actual.ReminderName);
        Assert.Equal(entry.StartAt, actual.StartAt);

        stored[attribute] = new("conflicting-identity");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => table.ReadRow(entry.GrainId, entry.ReminderName));
        Assert.Contains("conflicting stored identity", error.Message, StringComparison.Ordinal);

        stored = null;
        Assert.Null(await table.ReadRow(entry.GrainId, entry.ReminderName));
        Assert.Equal(0, client.WriteCount);
        Assert.Equal(0, client.DeleteCount);
    }

    [Theory]
    [InlineData(DynamoDBReminderTableMode.V2Only, "service")]
    [InlineData(DynamoDBReminderTableMode.V2Only, "foreign-service")]
    [InlineData(DynamoDBReminderTableMode.V2Only, null)]
    [InlineData(DynamoDBReminderTableMode.Migrate, "foreign-service")]
    [InlineData(DynamoDBReminderTableMode.Migrate, null)]
    public async Task V2CleanupValidatesAllPagesBeforeDeletingAnyRows(DynamoDBReminderTableMode mode, string? finalService)
    {
        var firstPartition = DynamoDBReminderTable.GetV2PartitionKey("service", 0);
        var lastPartition = DynamoDBReminderTable.GetV2PartitionKey("service", DynamoDBReminderTable.V2BucketCount - 1);
        Dictionary<string, AttributeValue> Item(string partition, string sort) => new()
        {
            ["PartitionKey"] = new(partition),
            ["SortKey"] = new(sort),
            ["ServiceId"] = new("service"),
        };
        var final = Item(lastPartition, "final");
        if (finalService is null)
        {
            final.Remove("ServiceId");
        }
        else
        {
            final["ServiceId"] = new(finalService);
        }

        var queryCount = 0;
        using var client = new RequestClient
        {
            Read = (_, _) => Task.FromResult(new GetItemResponse { Item = StateItem(mode == DynamoDBReminderTableMode.V2Only ? "Retired" : "Ready") }),
            Query = request =>
            {
                Assert.Equal("reminders-v2", request.TableName);
                Assert.True(request.ConsistentRead);
                queryCount++;
                var partition = request.ExpressionAttributeValues[":partition"].S;
                if (partition == firstPartition)
                {
                    return new() { Items = [Item(partition, "first")] };
                }

                if (partition != lastPartition)
                {
                    return new() { Items = [] };
                }

                if (request.ExclusiveStartKey is null or { Count: 0 })
                {
                    return new()
                    {
                        Items = Enumerable.Range(0, 25).Select(index => Item(partition, $"row-{index}")).ToList(),
                        LastEvaluatedKey = new() { ["PartitionKey"] = new(partition), ["SortKey"] = new("next-page") },
                    };
                }

                Assert.Equal("next-page", request.ExclusiveStartKey["SortKey"].S);
                return new() { Items = [final] };
            },
        };
        var table = CreateTable(client, mode);
        AttachStorage(table, client);

        if (finalService == "service")
        {
            await table.TestOnlyClearTable();
            Assert.Equal([25, 2], client.BatchWrites.Select(request => Assert.Single(request.RequestItems).Value.Count));
            Assert.All(client.BatchWrites, request => Assert.Equal("reminders-v2", Assert.Single(request.RequestItems).Key));
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => table.TestOnlyClearTable());
            Assert.Contains("stored service identity", error.Message, StringComparison.Ordinal);
            Assert.Empty(client.BatchWrites);
        }

        Assert.Equal(DynamoDBReminderTable.V2BucketCount + 1, queryCount);
        Assert.Equal(0, client.WriteCount);
        Assert.Equal(0, client.DeleteCount);
        Assert.Equal(0, client.ScanCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationStateRejectsMissingOrForeignServiceBeforeWrites(bool missing)
    {
        var state = StateItem("Ready");
        if (missing)
        {
            state.Remove("ServiceId");
        }
        else
        {
            state["ServiceId"] = new("foreign-service");
        }

        using var client = new RequestClient { Read = (_, _) => Task.FromResult(new GetItemResponse { Item = state }) };
        var table = CreateTable(client, DynamoDBReminderTableMode.Migrate);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => table.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("stored service identity", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, client.ReadCount);
        Assert.Equal(0, client.WriteCount);
        Assert.Equal(0, client.DeleteCount);
    }

    [Theory]
    [InlineData(DynamoDBReminderTableMode.Legacy, "Ready")]
    [InlineData(DynamoDBReminderTableMode.Migrate, "Ready")]
    [InlineData(DynamoDBReminderTableMode.V2, "Cutover")]
    [InlineData(DynamoDBReminderTableMode.V2Only, "Cutover")]
    [InlineData(DynamoDBReminderTableMode.Rollback, "Cutover")]
    public async Task ExistingMigrationStateRequiresLegacyTableBeforeMutations(DynamoDBReminderTableMode mode, string state)
    {
        using var client = new RequestClient
        {
            Read = (_, _) => Task.FromResult(new GetItemResponse { Item = StateItem(state) }),
            Describe = tableName => tableName == "reminders"
                ? throw new ResourceNotFoundException("V1 was deleted.")
                : new() { Table = new() { TableName = tableName, TableStatus = TableStatus.ACTIVE } },
        };
        var table = CreateTable(client, mode, createIfNotExists: true);

        var error = await Assert.ThrowsAsync<OrleansConfigurationException>(() => table.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Restore the table", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.CreateCount);
        Assert.Equal(0, client.WriteCount);
        Assert.Equal(0, client.DeleteCount);
    }

    [Theory]
    [InlineData("ServiceId")]
    [InlineData("GrainReference")]
    [InlineData("ReminderName")]
    public async Task V2PointReadRejectsConflictingStoredIdentity(string attribute)
    {
        var entry = Entry();
        entry.GrainId = GrainId.Create("state-test", new string('g', 400));
        Assert.Contains("#H", DynamoDBReminderTable.GetV2GrainPrefix(entry.GrainId.GetUniformHashCode(), entry.GrainId), StringComparison.Ordinal);
        entry.ReminderName = new string('r', 700);
        var item = RowItem(entry);
        item[attribute] = new("conflicting-identity");
        using var client = new RequestClient
        {
            Read = (request, _) => Task.FromResult(new GetItemResponse
            {
                Item = request.Key["SortKey"].S == "STATE" ? StateItem("Cutover") : item,
            }),
        };
        var table = CreateTable(client, DynamoDBReminderTableMode.V2);
        AttachStorage(table, client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => table.ReadRow(entry.GrainId, entry.ReminderName));

        Assert.Contains("conflicting stored identity", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, client.ReadCount);
        Assert.Equal(0, client.WriteCount);
    }

    [Fact]
    public async Task V2GrainReadFiltersConflictingPrefixIdentitiesAcrossPages()
    {
        var entry = Entry();
        entry.GrainId = GrainId.Create("state-test", new string('g', 400));
        var wrongGrain = RowItem(entry);
        wrongGrain["GrainReference"] = new(GrainId.Create("state-test", new string('h', 400)).ToString());
        var wrongService = RowItem(entry);
        wrongService["ServiceId"] = new("another-service");
        var pages = 0;
        using var client = new RequestClient
        {
            Read = (_, _) => Task.FromResult(new GetItemResponse { Item = StateItem("Cutover") }),
            Query = request =>
            {
                Assert.True(request.ConsistentRead);
                Assert.Equal(
                    DynamoDBReminderTable.GetV2GrainPrefix(entry.GrainId.GetUniformHashCode(), entry.GrainId),
                    request.ExpressionAttributeValues[":prefix"].S);
                if (++pages == 1)
                {
                    Assert.True(request.ExclusiveStartKey is null or { Count: 0 });
                    return new()
                    {
                        Items = [wrongGrain, wrongService],
                        LastEvaluatedKey = new() { ["SortKey"] = new("next-page") },
                    };
                }

                Assert.Equal("next-page", request.ExclusiveStartKey["SortKey"].S);
                return new() { Items = [RowItem(entry)] };
            },
        };
        var table = CreateTable(client, DynamoDBReminderTableMode.V2);
        AttachStorage(table, client);

        var result = Assert.Single((await table.ReadRows(entry.GrainId)).Reminders);

        Assert.Equal(entry.GrainId, result.GrainId);
        Assert.Equal(entry.ReminderName, result.ReminderName);
        Assert.Equal(entry.StartAt, result.StartAt);
        Assert.Equal(2, pages);
    }

    [Theory]
    [InlineData(1u, 10u)]
    [InlineData(10u, 1u)]
    [InlineData(0u, 0u)]
    public async Task V2RangeReadRejectsConflictingServiceAcrossPages(uint begin, uint end)
    {
        var entry = Entry();
        var item = RowItem(entry);
        var pages = 0;
        using var client = new RequestClient
        {
            Read = (_, _) => Task.FromResult(new GetItemResponse { Item = StateItem("Cutover") }),
            Query = request =>
            {
                Assert.True(request.ConsistentRead);
                if (request.ExpressionAttributeValues[":partition"].S != DynamoDBReminderTable.GetV2PartitionKey("service", 0)
                    || request.ExpressionAttributeValues[":lower"].S != DynamoDBReminderTable.GetV2RangeBounds(begin + 1, uint.MaxValue).Lower)
                {
                    return new() { Items = [] };
                }

                if (++pages == 1)
                {
                    Assert.True(request.ExclusiveStartKey is null or { Count: 0 });
                    return new()
                    {
                        Items = [],
                        LastEvaluatedKey = new() { ["SortKey"] = new("next-page") },
                    };
                }

                Assert.Equal("next-page", request.ExclusiveStartKey["SortKey"].S);
                return new() { Items = [item] };
            },
        };
        var table = CreateTable(client, DynamoDBReminderTableMode.V2);
        AttachStorage(table, client);

        var result = Assert.Single((await table.ReadRows(begin, end)).Reminders);
        Assert.Equal(entry.GrainId, result.GrainId);
        Assert.Equal(entry.ReminderName, result.ReminderName);
        Assert.Equal(2, pages);

        pages = 0;
        item["ServiceId"] = new("another-service");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => table.ReadRows(begin, end));

        Assert.Contains("conflicting stored service identity", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'service'", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, pages);
        Assert.Equal(0, client.WriteCount);
    }

    [Theory]
    [InlineData(DynamoDBReminderTableMode.Migrate, null)]
    [InlineData(DynamoDBReminderTableMode.V2, null)]
    [InlineData(DynamoDBReminderTableMode.V2Only, null)]
    [InlineData(DynamoDBReminderTableMode.Migrate, "Ready")]
    [InlineData(DynamoDBReminderTableMode.V2, "RolledBack")]
    [InlineData(DynamoDBReminderTableMode.V2Only, "Cutover")]
    public async Task ObservedRetirementRejectsMissingOrRegressedState(DynamoDBReminderTableMode mode, string? nextState)
    {
        string? state = "Retired";
        using var client = new RequestClient
        {
            Read = (request, _) => Task.FromResult(new GetItemResponse
            {
                Item = request.Key.ContainsKey("SortKey") && request.Key["SortKey"].S == "STATE" && state is not null
                    ? StateItem(state)
                    : null,
            }),
        };
        var table = CreateTable(client, mode);
        AttachStorage(table, client);
        var entry = Entry();
        Assert.Null(await table.ReadRow(entry.GrainId, entry.ReminderName));
        state = nextState;

        await Assert.ThrowsAsync<InvalidOperationException>(() => table.UpsertRow(entry));

        Assert.Equal(0, client.WriteCount);
    }

    [Theory]
    [InlineData("2026-08-28T01:02:03.0000000Z", "en-US", DateTimeKind.Utc)]
    [InlineData("28/08/2026 01:02:03", "en-GB", DateTimeKind.Unspecified)]
    public async Task RowReadsPreserveRoundtripUtcAndLegacyCultureFormats(string timestamp, string culture, DateTimeKind kind)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var entry = Entry();
            using var client = new RequestClient
            {
                Read = (request, _) => Task.FromResult(new GetItemResponse
                {
                    Item = request.TableName == "reminders-v2" ? null : new()
                    {
                        ["ServiceId"] = new("service"),
                        ["ETag"] = new() { N = "1" },
                        ["GrainReference"] = new(entry.GrainId.ToString()),
                        ["ReminderName"] = new(entry.ReminderName),
                        ["Period"] = new("00:01:00"),
                        ["StartTime"] = new(timestamp),
                    },
                }),
            };
            var table = CreateTable(client, DynamoDBReminderTableMode.Legacy);
            await table.StartAsync(TestContext.Current.CancellationToken);

            var actual = Assert.IsType<ReminderEntry>(await table.ReadRow(entry.GrainId, entry.ReminderName));

            Assert.Equal(new DateTime(2026, 8, 28, 1, 2, 3, kind), actual.StartAt);
            Assert.Equal(kind, actual.StartAt.Kind);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static ReminderEntry Entry() => new()
    {
        GrainId = GrainId.Create("state-test", "grain"),
        ReminderName = "reminder",
        StartAt = new DateTime(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc),
        Period = TimeSpan.FromMinutes(1),
    };

    private static Dictionary<string, AttributeValue> RowItem(ReminderEntry entry) => new()
    {
        ["ServiceId"] = new("service"),
        ["GrainReference"] = new(entry.GrainId.ToString()),
        ["ReminderName"] = new(entry.ReminderName),
        ["ETag"] = new() { N = "1" },
        ["Period"] = new(entry.Period.ToString()),
        ["StartTime"] = new(entry.StartAt.ToString("O", CultureInfo.InvariantCulture)),
    };

    private static Dictionary<string, AttributeValue> LegacyItem(ReminderEntry entry)
    {
        var item = RowItem(entry);
        item["ReminderId"] = new($"service_{entry.GrainId}_{entry.ReminderName}");
        item["GrainHash"] = new() { N = entry.GrainId.GetUniformHashCode().ToString(CultureInfo.InvariantCulture) };
        return item;
    }

    private static Task<bool> CopyLegacyRecord(DynamoDBReminderTable table, Dictionary<string, AttributeValue> item)
    {
        var recordType = typeof(DynamoDBReminderTable).GetNestedType("LegacyReminderRecord", BindingFlags.NonPublic)!;
        var record = Activator.CreateInstance(recordType, [item]);
        return (Task<bool>)typeof(DynamoDBReminderTable).GetMethod("CopyLegacyRecord", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(table, [record])!;
    }

    private static Dictionary<string, AttributeValue> StateItem(string state) => new()
    {
        ["ServiceId"] = new("service"),
        ["MigrationStatus"] = new(state),
        ["SourceCount"] = new() { N = "0" },
        ["TargetCount"] = new() { N = "0" },
    };

    private static DynamoDBReminderTable CreateTable(RequestClient client, DynamoDBReminderTableMode mode, string tableName = "reminders", bool createIfNotExists = false)
        => new(
            NullLoggerFactory.Instance,
            Options.Create(new ClusterOptions { ClusterId = "cluster", ServiceId = "service" }),
            Options.Create(new DynamoDBReminderStorageOptions
            {
                Service = "http://localhost",
                TableName = tableName,
                TableMode = mode,
                CreateIfNotExists = createIfNotExists,
                UpdateIfExists = false,
            }),
            null, null, null,
            new() { ConfigureStorage = storage => AttachClient(storage, client) });

    private static void AttachStorage(DynamoDBReminderTable table, RequestClient client)
    {
        var storage = new DynamoDBStorage(NullLogger<DynamoDBStorage>.Instance, "http://localhost");
        AttachClient(storage, client);
        typeof(DynamoDBReminderTable).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, storage);
    }

    private static void AttachClient(DynamoDBStorage storage, RequestClient client)
    {
        var field = typeof(DynamoDBStorage).GetField("_ddbClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.IsAssignableFrom<IDisposable>(field.GetValue(storage)).Dispose();
        field.SetValue(storage, client);
    }

    private sealed class RegistrationTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class RegistrationLocalSiloDetails(SiloAddress address) : ILocalSiloDetails
    {
        public string Name => "registration-test";
        public string ClusterId => "cluster";
        public string DnsHostName => "localhost";
        public SiloAddress SiloAddress => address;
        public SiloAddress GatewayAddress => address;
    }

    private sealed class RegistrationMembershipService(SiloAddress address) : IClusterMembershipService
    {
        public int RefreshCount { get; private set; }
        public ClusterMembershipSnapshot CurrentSnapshot { get; } = new(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(address, new(address, SiloStatus.Active, "local")),
            new(1));
        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => Empty();
        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCount++;
            return ValueTask.CompletedTask;
        }

        public Task<bool> TryKill(SiloAddress siloAddress) => Task.FromResult(false);
        private static async IAsyncEnumerable<ClusterMembershipSnapshot> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RequestClient() : AmazonDynamoDBClient(
        new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://localhost" })
    {
        public Func<GetItemRequest, CancellationToken, Task<GetItemResponse>> Read { get; init; }
            = (_, _) => throw new InvalidOperationException("Unexpected read.");

        public Func<QueryRequest, QueryResponse> Query { get; init; }
            = _ => throw new InvalidOperationException("Unexpected query.");

        public Func<string, DescribeTableResponse> Describe { get; init; }
            = tableName => new() { Table = new() { TableName = tableName, TableStatus = TableStatus.ACTIVE } };

        public Func<PutItemRequest, PutItemResponse> Put { get; init; } = _ => new();
        public Func<TransactWriteItemsRequest, TransactWriteItemsResponse> Transact { get; init; }
            = _ => throw new InvalidOperationException("Unexpected transaction.");
        public List<BatchWriteItemRequest> BatchWrites { get; } = [];

        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public int DeleteCount { get; private set; }
        public int CreateCount { get; private set; }
        public int ScanCount { get; private set; }

        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            Assert.True(request.ConsistentRead);
            return Read(request, cancellationToken);
        }

        public override Task<DescribeTableResponse> DescribeTableAsync(string tableName, CancellationToken cancellationToken = default)
            => Task.FromResult(Describe(tableName));

        public override Task<CreateTableResponse> CreateTableAsync(CreateTableRequest request, CancellationToken cancellationToken = default)
        {
            CreateCount++;
            throw new InvalidOperationException("Unexpected table creation.");
        }

        public override Task<PutItemResponse> PutItemAsync(PutItemRequest request, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return Task.FromResult(Put(request));
        }

        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(Query(request));

        public override Task<DeleteItemResponse> DeleteItemAsync(DeleteItemRequest request, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            return Task.FromResult(new DeleteItemResponse());
        }

        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return Task.FromResult(Transact(request));
        }

        public override Task<BatchWriteItemResponse> BatchWriteItemAsync(BatchWriteItemRequest request, CancellationToken cancellationToken = default)
        {
            BatchWrites.Add(request);
            return Task.FromResult(new BatchWriteItemResponse());
        }

        public override Task<ScanResponse> ScanAsync(ScanRequest request, CancellationToken cancellationToken = default)
        {
            ScanCount++;
            throw new InvalidOperationException("Unexpected Legacy scan.");
        }
    }
}
