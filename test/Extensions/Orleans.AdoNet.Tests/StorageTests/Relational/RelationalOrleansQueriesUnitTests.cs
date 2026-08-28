using System.Data;
using System.Net;
using Orleans;
using Orleans.GrainDirectory.AdoNet;
using Orleans.Runtime;
using Orleans.Streaming.AdoNet;
using UnitTests.StorageTests.Relational.Fakes;
using ClusteringQueries = Orleans.Clustering.AdoNet.Storage.RelationalOrleansQueries;
using DirectoryQueries = Orleans.GrainDirectory.AdoNet.Storage.RelationalOrleansQueries;
using PersistenceQueries = Orleans.Persistence.AdoNet.Storage.RelationalOrleansQueries;
using ReminderQueries = Orleans.Reminders.AdoNet.Storage.RelationalOrleansQueries;
using StreamingQueries = Orleans.Streaming.AdoNet.Storage.RelationalOrleansQueries;

namespace UnitTests.StorageTests.Relational;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Persistence")]
public sealed class RelationalOrleansQueriesUnitTests
{
    private const string GetQueriesSql = "SELECT QueryKey, QueryText FROM OrleansQuery";

    private static readonly string[] ReminderQueryKeys =
    [
        "ReadReminderRowsKey",
        "ReadRangeRows1Key",
        "ReadRangeRows2Key",
        "ReadReminderRowKey",
        "UpsertReminderRowKey",
        "DeleteReminderRowKey",
        "DeleteReminderRowsKey",
    ];

    private static readonly string[] MembershipQueryKeys =
    [
        "GatewaysQueryKey",
        "MembershipReadRowKey",
        "MembershipReadAllKey",
        "InsertMembershipVersionKey",
        "UpdateIAmAlivetimeKey",
        "InsertMembershipKey",
        "UpdateMembershipKey",
        "DeleteMembershipTableEntriesKey",
        "CleanupDefunctSiloEntriesKey",
    ];

    private static readonly string[] StreamingQueryKeys =
    [
        "StreamSchemaVersionKey",
        "AppendStreamMessageKey",
        "AcquireStreamPartitionKey",
        "ReadStreamMessagesKey",
        "AdvanceStreamCheckpointKey",
        "GetStreamPartitionBoundsKey",
        "CleanupStreamMessagesKey",
    ];

    private static readonly string[] DirectoryQueryKeys =
    [
        "RegisterGrainActivationKey",
        "UnregisterGrainActivationKey",
        "LookupGrainActivationKey",
        "UnregisterGrainActivationsKey",
    ];

    [Fact]
    public async Task CreateInstance_LoadsQueriesUsingDbStoredQueriesKey()
    {
        var storage = new ScriptedRelationalStorage().ExpectRead(GetQueriesSql, CreateQueryTable());

        _ = await PersistenceQueries.CreateInstance(storage);

        var call = Assert.Single(storage.Calls);
        Assert.Equal(GetQueriesSql, call.Query);
        Assert.Empty(call.Command.Parameters);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task CreateInstance_ThrowsWhenStoredQueryIsMissing()
    {
        var suppliedKeys = ReminderQueryKeys.Where(key => key != "DeleteReminderRowsKey").ToArray();
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), suppliedKeys);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => ReminderQueries.CreateInstance(storage));

        Assert.Contains("DeleteReminderRowsKey", exception.Message, StringComparison.Ordinal);
        Assert.Single(storage.Calls);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task CreateInstance_ThrowsWhenStoredQueryIsDuplicated()
    {
        var storage = new ScriptedRelationalStorage().ExpectRead(
            GetQueriesSql,
            CreateQueryTable(
                ("DuplicateKey", "first"),
                ("DuplicateKey", "second")));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => PersistenceQueries.CreateInstance(storage));

        Assert.Contains("same key", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(storage.Calls);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task CreateInstance_AcceptsCompleteStoredQuerySet()
    {
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), ReminderQueryKeys);

        var queries = await ReminderQueries.CreateInstance(storage);

        Assert.NotNull(queries);
        Assert.Single(storage.Calls);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ReminderRange_UsesSignedNonWrappingQueryAndExpectedParameters()
    {
        const uint BeginHash = 0x8000_0000;
        const uint EndHash = 0;
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), ReminderQueryKeys)
            .ExpectRead(Sql("ReadRangeRows1Key"));
        var queries = await ReminderQueries.CreateInstance(storage);

        var result = await queries.ReadReminderRowsAsync("service-a", BeginHash, EndHash);

        Assert.Empty(result.Reminders);
        AssertParameters(
            AssertOperationCall(storage, Sql("ReadRangeRows1Key")),
            ("ServiceId", "service-a"),
            ("BeginHash", int.MinValue),
            ("EndHash", 0));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ReminderRange_UsesSignedWrapQueryAndExpectedParameters()
    {
        const uint BeginHash = 0;
        const uint EndHash = 0x8000_0000;
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), ReminderQueryKeys)
            .ExpectRead(Sql("ReadRangeRows2Key"));
        var queries = await ReminderQueries.CreateInstance(storage);

        var result = await queries.ReadReminderRowsAsync("service-b", BeginHash, EndHash);

        Assert.Empty(result.Reminders);
        AssertParameters(
            AssertOperationCall(storage, Sql("ReadRangeRows2Key")),
            ("ServiceId", "service-b"),
            ("BeginHash", 0),
            ("EndHash", int.MinValue));
        Assert.False(ReminderQueries.IsReminderRangeNonWrappingInSignedOrder(BeginHash, BeginHash));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ReadReminderRow_ReturnsNullWhenNoRowsExist()
    {
        var grainId = GrainId.Create("reminder-type", "grain-7");
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), ReminderQueryKeys)
            .ExpectRead(Sql("ReadReminderRowKey"));
        var queries = await ReminderQueries.CreateInstance(storage);

        var result = await queries.ReadReminderRowAsync("service-c", grainId, "wake-up");

        Assert.Null(result);
        var call = AssertOperationCall(storage, Sql("ReadReminderRowKey"));
        AssertParameters(
            call,
            ("ServiceId", "service-c"),
            ("GrainId", grainId.ToString()),
            ("ReminderName", "wake-up"));
        Assert.Equal(DbType.AnsiString, Parameter(call, "GrainId").DbType);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ReadReminderRow_AggregatesTheReturnedRow()
    {
        var grainId = GrainId.Create("reminder-type", "grain-8");
        var startAt = new DateTime(2026, 8, 27, 12, 30, 0, DateTimeKind.Utc);
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), ReminderQueryKeys)
            .ExpectRead(
                Sql("ReadReminderRowKey"),
                CreateTable(
                    [
                        ("GrainId", typeof(string)),
                        ("ReminderName", typeof(string)),
                        ("StartTime", typeof(DateTime)),
                        ("Period", typeof(long)),
                        ("Version", typeof(long)),
                    ],
                    [grainId.ToString(), "refresh", startAt, 90_000L, 23L]));
        var queries = await ReminderQueries.CreateInstance(storage);

        var result = await queries.ReadReminderRowAsync("service-d", grainId, "refresh");

        Assert.NotNull(result);
        Assert.Equal(grainId, result.GrainId);
        Assert.Equal("refresh", result.ReminderName);
        Assert.Equal(startAt, result.StartAt);
        Assert.Equal(TimeSpan.FromSeconds(90), result.Period);
        Assert.Equal("23", result.ETag);
        AssertOperationCall(storage, Sql("ReadReminderRowKey"));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ReadAllMembershipRows_ReturnsVersionOnlyWhenRowsAreEmpty()
    {
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), MembershipQueryKeys)
            .ExpectRead(
                Sql("MembershipReadAllKey"),
                CreateTable(
                    [("StartTime", typeof(DateTime)), ("Version", typeof(long))],
                    [DBNull.Value, 17L]));
        var queries = await ClusteringQueries.CreateInstance(storage);

        var result = await queries.MembershipReadAllAsync("cluster-a");

        Assert.Empty(result.Members);
        Assert.Equal(17, result.Version.Version);
        Assert.Equal("17", result.Version.VersionEtag);
        AssertParameters(
            AssertOperationCall(storage, Sql("MembershipReadAllKey")),
            ("DeploymentId", "cluster-a"));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ReadAllMembershipRows_ConvertsRowsAndVersion()
    {
        var startTime = new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc);
        var aliveTime = startTime.AddMinutes(5);
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), MembershipQueryKeys)
            .ExpectRead(
                Sql("MembershipReadAllKey"),
                CreateTable(
                    [
                        ("StartTime", typeof(DateTime)),
                        ("Port", typeof(int)),
                        ("Generation", typeof(int)),
                        ("Address", typeof(string)),
                        ("SiloName", typeof(string)),
                        ("HostName", typeof(string)),
                        ("Status", typeof(int)),
                        ("ProxyPort", typeof(int)),
                        ("IAmAliveTime", typeof(DateTime)),
                        ("SuspectTimes", typeof(string)),
                        ("Version", typeof(long)),
                    ],
                    [startTime, 11_111, 9, "127.0.0.1", "silo-a", "host-a", (int)SiloStatus.Active, 30_000, aliveTime, DBNull.Value, 42L]));
        var queries = await ClusteringQueries.CreateInstance(storage);

        var result = await queries.MembershipReadAllAsync("cluster-b");

        var member = Assert.Single(result.Members).Item1;
        Assert.Equal(SiloAddress.New(IPAddress.Loopback, 11_111, 9), member.SiloAddress);
        Assert.Equal("silo-a", member.SiloName);
        Assert.Equal("host-a", member.HostName);
        Assert.Equal(SiloStatus.Active, member.Status);
        Assert.Equal(30_000, member.ProxyPort);
        Assert.Equal(startTime, member.StartTime);
        Assert.Equal(aliveTime, member.IAmAliveTime);
        Assert.Null(member.SuspectTimes);
        Assert.Equal(42, result.Version.Version);
        Assert.Equal("42", result.Version.VersionEtag);
        AssertOperationCall(storage, Sql("MembershipReadAllKey"));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task MembershipMutation_ReturnsResultAndCapturesAllParameters()
    {
        var startTime = new DateTime(2026, 8, 27, 8, 0, 0, DateTimeKind.Utc);
        var aliveTime = startTime.AddMinutes(1);
        var address = SiloAddress.New(IPAddress.Parse("10.20.30.40"), 12_345, 7);
        var entry = new MembershipEntry
        {
            SiloAddress = address,
            SiloName = "silo-mutation",
            HostName = "host-mutation",
            Status = SiloStatus.Joining,
            ProxyPort = 30_001,
            StartTime = startTime,
            IAmAliveTime = aliveTime,
        };
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), MembershipQueryKeys)
            .ExpectRead(
                Sql("InsertMembershipKey"),
                CreateTable([("Result", typeof(int))], [1]));
        var queries = await ClusteringQueries.CreateInstance(storage);

        var result = await queries.InsertMembershipRowAsync("cluster-c", entry, "73");

        Assert.True(result);
        AssertParameters(
            AssertOperationCall(storage, Sql("InsertMembershipKey")),
            ("DeploymentId", "cluster-c"),
            ("IAmAliveTime", aliveTime),
            ("SiloName", "silo-mutation"),
            ("HostName", "host-mutation"),
            ("Address", "10.20.30.40"),
            ("Port", 12_345),
            ("Generation", 7),
            ("StartTime", startTime),
            ("Status", (int)SiloStatus.Joining),
            ("ProxyPort", 30_001),
            ("Version", 73));
        storage.VerifyComplete();
    }


    [Fact]
    public async Task GrainDirectoryLookup_ReturnsSingleEntry()
    {
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), DirectoryQueryKeys)
            .ExpectRead(
                Sql("LookupGrainActivationKey"),
                CreateTable(
                    DirectoryEntryColumns,
                    ["cluster-d", "directory-a", "type/key", "127.0.0.1:11111@9", "activation-1"]));
        var queries = await DirectoryQueries.CreateInstance(storage);

        var result = await queries.LookupGrainActivationAsync("cluster-d", "directory-a", "type/key");

        Assert.Equal(
            new AdoNetGrainDirectoryEntry("cluster-d", "directory-a", "type/key", "127.0.0.1:11111@9", "activation-1"),
            result);
        AssertParameters(
            AssertOperationCall(storage, Sql("LookupGrainActivationKey")),
            ("ClusterId", "cluster-d"),
            ("ProviderId", "directory-a"),
            ("GrainId", "type/key"));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task GrainDirectoryLookup_ReturnsDefaultWhenNoRowsExist()
    {
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), DirectoryQueryKeys)
            .ExpectRead(Sql("LookupGrainActivationKey"));
        var queries = await DirectoryQueries.CreateInstance(storage);

        var result = await queries.LookupGrainActivationAsync("cluster-e", "directory-b", "type/missing");

        Assert.Null(result);
        AssertParameters(
            AssertOperationCall(storage, Sql("LookupGrainActivationKey")),
            ("ClusterId", "cluster-e"),
            ("ProviderId", "directory-b"),
            ("GrainId", "type/missing"));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task GrainDirectoryLookup_ThrowsWhenMultipleRowsAreReturned()
    {
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), DirectoryQueryKeys)
            .ExpectRead(
                Sql("LookupGrainActivationKey"),
                CreateTable(
                    DirectoryEntryColumns,
                    ["cluster-f", "directory-c", "type/duplicate", "127.0.0.1:11111@1", "activation-1"],
                    ["cluster-f", "directory-c", "type/duplicate", "127.0.0.1:11112@2", "activation-2"]));
        var queries = await DirectoryQueries.CreateInstance(storage);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => queries.LookupGrainActivationAsync("cluster-f", "directory-c", "type/duplicate"));

        AssertParameters(
            AssertOperationCall(storage, Sql("LookupGrainActivationKey")),
            ("ClusterId", "cluster-f"),
            ("ProviderId", "directory-c"),
            ("GrainId", "type/duplicate"));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task Operation_PropagatesStorageErrorAcrossProductionCopies()
    {
        var expected = new InvalidOperationException("scripted storage failure");
        var reminderStorage = ExpectQueryLoad(new ScriptedRelationalStorage(), ReminderQueryKeys)
            .ExpectReadException(Sql("ReadReminderRowKey"), expected);
        var reminderQueries = await ReminderQueries.CreateInstance(reminderStorage);
        var reminderError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reminderQueries.ReadReminderRowAsync("service-error", GrainId.Create("type", "key"), "name"));
        Assert.Same(expected, reminderError);
        AssertOperationCall(reminderStorage, Sql("ReadReminderRowKey"));
        reminderStorage.VerifyComplete();

        var membershipStorage = ExpectQueryLoad(new ScriptedRelationalStorage(), MembershipQueryKeys)
            .ExpectReadException(Sql("MembershipReadAllKey"), expected);
        var membershipQueries = await ClusteringQueries.CreateInstance(membershipStorage);
        var membershipError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => membershipQueries.MembershipReadAllAsync("cluster-error"));
        Assert.Same(expected, membershipError);
        AssertOperationCall(membershipStorage, Sql("MembershipReadAllKey"));
        membershipStorage.VerifyComplete();

        var streamingStorage = ExpectQueryLoad(new ScriptedRelationalStorage(), StreamingQueryKeys)
            .ExpectReadException(Sql("ReadStreamMessagesKey"), expected);
        var streamingQueries = await StreamingQueries.CreateInstance(streamingStorage);
        var streamingError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => streamingQueries.ReadStreamMessagesAsync(
                "service-error",
                "provider-error",
                "queue-error",
                afterMessageId: 0,
                maxCount: 5,
                TestContext.Current.CancellationToken));
        Assert.Same(expected, streamingError);
        AssertOperationCall(streamingStorage, Sql("ReadStreamMessagesKey"));
        streamingStorage.VerifyComplete();

        var directoryStorage = ExpectQueryLoad(new ScriptedRelationalStorage(), DirectoryQueryKeys)
            .ExpectReadException(Sql("LookupGrainActivationKey"), expected);
        var directoryQueries = await DirectoryQueries.CreateInstance(directoryStorage);
        var directoryError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => directoryQueries.LookupGrainActivationAsync("cluster-error", "provider-error", "grain-error"));
        Assert.Same(expected, directoryError);
        AssertOperationCall(directoryStorage, Sql("LookupGrainActivationKey"));
        directoryStorage.VerifyComplete();
    }

    [Fact]
    public async Task RegisterGrainActivationAsync_ReturnsEntryAndCapturesParameters()
    {
        var expected = new AdoNetGrainDirectoryEntry(
            "cluster-register",
            "directory-register",
            "registered/type/key",
            "10.10.20.30:11111@17",
            "activation-register");
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), DirectoryQueryKeys)
            .ExpectRead(
                Sql("RegisterGrainActivationKey"),
                CreateTable(
                    DirectoryEntryColumns,
                    [expected.ClusterId, expected.ProviderId, expected.GrainId, expected.SiloAddress, expected.ActivationId]));
        var queries = await DirectoryQueries.CreateInstance(storage);

        var result = await queries.RegisterGrainActivationAsync(
            expected.ClusterId,
            expected.ProviderId,
            expected.GrainId,
            expected.SiloAddress,
            expected.ActivationId);

        Assert.Equal(expected, result);
        AssertParameters(
            AssertOperationCall(storage, Sql("RegisterGrainActivationKey")),
            ("ClusterId", "cluster-register"),
            ("ProviderId", "directory-register"),
            ("GrainId", "registered/type/key"),
            ("SiloAddress", "10.10.20.30:11111@17"),
            ("ActivationId", "activation-register"));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task UnregisterGrainActivationAsync_ReturnsResultAndCapturesParameters()
    {
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), DirectoryQueryKeys)
            .ExpectRead(
                Sql("UnregisterGrainActivationKey"),
                CreateTable([("Result", typeof(int))], [3]));
        var queries = await DirectoryQueries.CreateInstance(storage);

        var result = await queries.UnregisterGrainActivationAsync(
            "cluster-unregister",
            "directory-unregister",
            "unregistered/type/key",
            "activation-unregister");

        Assert.Equal(3, result);
        AssertParameters(
            AssertOperationCall(storage, Sql("UnregisterGrainActivationKey")),
            ("ClusterId", "cluster-unregister"),
            ("ProviderId", "directory-unregister"),
            ("GrainId", "unregistered/type/key"),
            ("ActivationId", "activation-unregister"));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task UnregisterGrainActivationsAsync_ReturnsCountAndCapturesParameters()
    {
        const string SiloAddresses = "10.0.0.1:11111@3|10.0.0.2:11112@4";
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), DirectoryQueryKeys)
            .ExpectRead(
                Sql("UnregisterGrainActivationsKey"),
                CreateTable([("Result", typeof(int))], [5]));
        var queries = await DirectoryQueries.CreateInstance(storage);

        var result = await queries.UnregisterGrainActivationsAsync(
            "cluster-unregister-all",
            "directory-unregister-all",
            SiloAddresses);

        Assert.Equal(5, result);
        AssertParameters(
            AssertOperationCall(storage, Sql("UnregisterGrainActivationsKey")),
            ("ClusterId", "cluster-unregister-all"),
            ("ProviderId", "directory-unregister-all"),
            ("SiloAddresses", SiloAddresses));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task UpsertReminderRowAsync_ReturnsVersionAndCapturesParameters()
    {
        var grainId = GrainId.Create("reminder-upsert-type", "grain-upsert");
        var startTime = new DateTime(2026, 8, 27, 20, 15, 30, DateTimeKind.Utc);
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), ReminderQueryKeys)
            .ExpectRead(
                Sql("UpsertReminderRowKey"),
                CreateTable([("Version", typeof(long))], [81L]));
        var queries = await ReminderQueries.CreateInstance(storage);

        var result = await queries.UpsertReminderRowAsync(
            "service-upsert",
            grainId,
            "reminder-upsert",
            startTime,
            TimeSpan.FromMilliseconds(123_456));

        Assert.Equal("81", result);
        var call = AssertOperationCall(storage, Sql("UpsertReminderRowKey"));
        AssertParameters(
            call,
            ("ServiceId", "service-upsert"),
            ("GrainHash", unchecked((int)grainId.GetUniformHashCode())),
            ("GrainId", grainId.ToString()),
            ("ReminderName", "reminder-upsert"),
            ("StartTime", startTime),
            ("Period", 123_456));
        Assert.Equal(DbType.AnsiString, Parameter(call, "GrainId").DbType);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task DeleteReminderRowAsync_ReturnsResultAndCapturesParameters()
    {
        var grainId = GrainId.Create("reminder-delete-type", "grain-delete");
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), ReminderQueryKeys)
            .ExpectRead(
                Sql("DeleteReminderRowKey"),
                CreateTable([("Result", typeof(int))], [1]));
        var queries = await ReminderQueries.CreateInstance(storage);

        var result = await queries.DeleteReminderRowAsync(
            "service-delete",
            grainId,
            "reminder-delete",
            "44");

        Assert.True(result);
        var call = AssertOperationCall(storage, Sql("DeleteReminderRowKey"));
        AssertParameters(
            call,
            ("ServiceId", "service-delete"),
            ("GrainId", grainId.ToString()),
            ("ReminderName", "reminder-delete"),
            ("Version", 44));
        Assert.Equal(DbType.AnsiString, Parameter(call, "GrainId").DbType);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ReadReminderRowsAsync_ForGrain_ReturnsEntriesAndCapturesParameters()
    {
        var grainId = GrainId.Create("reminder-read-type", "grain-read");
        var firstStart = new DateTime(2026, 8, 27, 18, 0, 0, DateTimeKind.Utc);
        var secondStart = firstStart.AddMinutes(15);
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), ReminderQueryKeys)
            .ExpectRead(
                Sql("ReadReminderRowsKey"),
                CreateTable(
                    [
                        ("GrainId", typeof(string)),
                        ("ReminderName", typeof(string)),
                        ("StartTime", typeof(DateTime)),
                        ("Period", typeof(long)),
                        ("Version", typeof(long)),
                    ],
                    [grainId.ToString(), "first-reminder", firstStart, 45_000L, 91L],
                    [grainId.ToString(), "second-reminder", secondStart, 120_000L, 92L]));
        var queries = await ReminderQueries.CreateInstance(storage);

        var result = await queries.ReadReminderRowsAsync("service-read", grainId);

        Assert.Collection(
            result.Reminders,
            reminder =>
            {
                Assert.Equal(grainId, reminder.GrainId);
                Assert.Equal("first-reminder", reminder.ReminderName);
                Assert.Equal(firstStart, reminder.StartAt);
                Assert.Equal(TimeSpan.FromSeconds(45), reminder.Period);
                Assert.Equal("91", reminder.ETag);
            },
            reminder =>
            {
                Assert.Equal(grainId, reminder.GrainId);
                Assert.Equal("second-reminder", reminder.ReminderName);
                Assert.Equal(secondStart, reminder.StartAt);
                Assert.Equal(TimeSpan.FromMinutes(2), reminder.Period);
                Assert.Equal("92", reminder.ETag);
            });
        var call = AssertOperationCall(storage, Sql("ReadReminderRowsKey"));
        AssertParameters(
            call,
            ("ServiceId", "service-read"),
            ("GrainId", grainId.ToString()));
        Assert.Equal(DbType.AnsiString, Parameter(call, "GrainId").DbType);
        storage.VerifyComplete();
    }

    [Fact]
    public void GetReminderEntry_WithDbNullGrainId_ReturnsNull()
    {
        using var reader = new DataTableReader(
            CreateTable([("GrainId", typeof(string))], [DBNull.Value]));
        Assert.True(reader.Read());

        var result = ReminderQueries.GetReminderEntry(reader);

        Assert.Null(result);
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task UpdateMembershipRowAsync_ReturnsResultAndCapturesParameters()
    {
        var address = SiloAddress.New(IPAddress.Parse("10.30.40.50"), 12_345, 6);
        var aliveTime = new DateTime(2026, 8, 27, 21, 5, 0, DateTimeKind.Utc);
        var suspectTime1 = new DateTime(2026, 8, 27, 20, 1, 2, 345, DateTimeKind.Utc);
        var suspectTime2 = new DateTime(2026, 8, 27, 20, 2, 3, 456, DateTimeKind.Utc);
        var entry = new MembershipEntry
        {
            SiloAddress = address,
            IAmAliveTime = aliveTime,
            Status = SiloStatus.Active,
            SuspectTimes =
            [
                Tuple.Create(SiloAddress.New(IPAddress.Parse("10.30.40.60"), 12_346, 7), suspectTime1),
                Tuple.Create(SiloAddress.New(IPAddress.Parse("10.30.40.70"), 12_347, 8), suspectTime2),
            ],
        };
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), MembershipQueryKeys)
            .ExpectRead(
                Sql("UpdateMembershipKey"),
                CreateTable([("Result", typeof(int))], [1]));
        var queries = await ClusteringQueries.CreateInstance(storage);

        var result = await queries.UpdateMembershipRowAsync("cluster-update", entry, "105");

        Assert.True(result);
        AssertParameters(
            AssertOperationCall(storage, Sql("UpdateMembershipKey")),
            ("DeploymentId", "cluster-update"),
            ("Address", "10.30.40.50"),
            ("Port", 12_345),
            ("Generation", 6),
            ("IAmAliveTime", aliveTime),
            ("Status", (int)SiloStatus.Active),
            ("SuspectTimes", "10.30.40.60:12346@7,2026-08-27 20:01:02.345 GMT|10.30.40.70:12347@8,2026-08-27 20:02:03.456 GMT"),
            ("Version", 105));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ActiveGatewaysAsync_ReturnsUrisAndCapturesStatus()
    {
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), MembershipQueryKeys)
            .ExpectRead(
                Sql("GatewaysQueryKey"),
                CreateTable(
                    [("ProxyPort", typeof(int)), ("Generation", typeof(int)), ("Address", typeof(string))],
                    [30_002, 12, "10.2.3.4"],
                    [30_003, 13, "10.2.3.5"]));
        var queries = await ClusteringQueries.CreateInstance(storage);

        var result = await queries.ActiveGatewaysAsync("cluster-gateways");

        Assert.Equal(
            [new Uri("gwy.tcp://10.2.3.4:30002/12"), new Uri("gwy.tcp://10.2.3.5:30003/13")],
            result);
        AssertParameters(
            AssertOperationCall(storage, Sql("GatewaysQueryKey")),
            ("DeploymentId", "cluster-gateways"),
            ("Status", (int)SiloStatus.Active));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task MembershipReadRowAsync_ReturnsEntryAndCapturesParameters()
    {
        var address = SiloAddress.New(IPAddress.Parse("10.4.5.6"), 11_222, 14);
        var startTime = new DateTime(2026, 8, 27, 17, 30, 0, DateTimeKind.Utc);
        var aliveTime = startTime.AddMinutes(7);
        var suspectTime = startTime.AddMinutes(3);
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), MembershipQueryKeys)
            .ExpectRead(
                Sql("MembershipReadRowKey"),
                CreateTable(
                    [
                        ("StartTime", typeof(DateTime)),
                        ("Port", typeof(int)),
                        ("Generation", typeof(int)),
                        ("Address", typeof(string)),
                        ("SiloName", typeof(string)),
                        ("HostName", typeof(string)),
                        ("Status", typeof(int)),
                        ("ProxyPort", typeof(int)),
                        ("IAmAliveTime", typeof(DateTime)),
                        ("SuspectTimes", typeof(string)),
                        ("Version", typeof(long)),
                    ],
                    [
                        startTime,
                        11_222,
                        14,
                        "10.4.5.6",
                        "silo-read-row",
                        "host-read-row",
                        (int)SiloStatus.Active,
                        30_004,
                        aliveTime,
                        "10.4.5.7:11223@15,2026-08-27 17:33:00.000 GMT",
                        106L,
                    ]));
        var queries = await ClusteringQueries.CreateInstance(storage);

        var result = await queries.MembershipReadRowAsync("cluster-read-row", address);

        var memberAndEtag = Assert.Single(result.Members);
        var member = memberAndEtag.Item1;
        Assert.Equal(string.Empty, memberAndEtag.Item2);
        Assert.Equal(address, member.SiloAddress);
        Assert.Equal("silo-read-row", member.SiloName);
        Assert.Equal("host-read-row", member.HostName);
        Assert.Equal(SiloStatus.Active, member.Status);
        Assert.Equal(30_004, member.ProxyPort);
        Assert.Equal(startTime, member.StartTime);
        Assert.Equal(aliveTime, member.IAmAliveTime);
        Assert.NotNull(member.SuspectTimes);
        var suspect = Assert.Single(member.SuspectTimes);
        Assert.Equal(SiloAddress.New(IPAddress.Parse("10.4.5.7"), 11_223, 15), suspect.Item1);
        Assert.Equal(suspectTime, suspect.Item2);
        Assert.Equal(106, result.Version.Version);
        Assert.Equal("106", result.Version.VersionEtag);
        AssertParameters(
            AssertOperationCall(storage, Sql("MembershipReadRowKey")),
            ("DeploymentId", "cluster-read-row"),
            ("Address", "10.4.5.6"),
            ("Port", 11_222),
            ("Generation", 14));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task InsertMembershipVersionRowAsync_ReturnsResultAndCapturesDeployment()
    {
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), MembershipQueryKeys)
            .ExpectRead(
                Sql("InsertMembershipVersionKey"),
                CreateTable([("Result", typeof(int))], [1]));
        var queries = await ClusteringQueries.CreateInstance(storage);

        var result = await queries.InsertMembershipVersionRowAsync("cluster-version");

        Assert.True(result);
        AssertParameters(
            AssertOperationCall(storage, Sql("InsertMembershipVersionKey")),
            ("DeploymentId", "cluster-version"));
        storage.VerifyComplete();
    }


    [Theory]
    [InlineData(0, "clusterId")]
    [InlineData(1, "providerId")]
    [InlineData(2, "grainId")]
    [InlineData(3, "siloAddress")]
    [InlineData(4, "activationId")]
    public async Task RegisterGrainActivationAsync_WithNullRequiredArgument_ThrowsArgumentNullException(
        int nullIndex,
        string expectedParameterName)
    {
        var arguments = new string?[]
        {
            "cluster-null",
            "provider-null",
            "grain-null",
            "silo-null",
            "activation-null",
        };
        arguments[nullIndex] = null;
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), DirectoryQueryKeys);
        var queries = await DirectoryQueries.CreateInstance(storage);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => queries.RegisterGrainActivationAsync(
                arguments[0]!,
                arguments[1]!,
                arguments[2]!,
                arguments[3]!,
                arguments[4]!));

        Assert.Equal(expectedParameterName, exception.ParamName);
        AssertOnlyQueryLoadCall(storage);
        storage.VerifyComplete();
    }

    [Theory]
    [InlineData(0, "clusterId")]
    [InlineData(1, "providerId")]
    [InlineData(2, "grainId")]
    [InlineData(3, "activationId")]
    public async Task UnregisterGrainActivationAsync_WithNullRequiredArgument_ThrowsArgumentNullException(
        int nullIndex,
        string expectedParameterName)
    {
        var arguments = new string?[]
        {
            "cluster-null",
            "provider-null",
            "grain-null",
            "activation-null",
        };
        arguments[nullIndex] = null;
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), DirectoryQueryKeys);
        var queries = await DirectoryQueries.CreateInstance(storage);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => queries.UnregisterGrainActivationAsync(
                arguments[0]!,
                arguments[1]!,
                arguments[2]!,
                arguments[3]!));

        Assert.Equal(expectedParameterName, exception.ParamName);
        AssertOnlyQueryLoadCall(storage);
        storage.VerifyComplete();
    }

    [Theory]
    [InlineData(0, "clusterId")]
    [InlineData(1, "providerId")]
    [InlineData(2, "siloAddresses")]
    public async Task UnregisterGrainActivationsAsync_WithNullRequiredArgument_ThrowsArgumentNullException(
        int nullIndex,
        string expectedParameterName)
    {
        var arguments = new string?[]
        {
            "cluster-null",
            "provider-null",
            "silos-null",
        };
        arguments[nullIndex] = null;
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), DirectoryQueryKeys);
        var queries = await DirectoryQueries.CreateInstance(storage);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => queries.UnregisterGrainActivationsAsync(
                arguments[0]!,
                arguments[1]!,
                arguments[2]!));

        Assert.Equal(expectedParameterName, exception.ParamName);
        AssertOnlyQueryLoadCall(storage);
        storage.VerifyComplete();
    }


    [Fact]
    public async Task UpdateIAmAliveTimeAsync_ExecutesSentinelQueryAndCapturesUtcValue()
    {
        var address = SiloAddress.New(IPAddress.Parse("10.50.60.70"), 12_345, 9);
        var aliveTime = new DateTime(2026, 8, 27, 22, 31, 45, DateTimeKind.Utc);
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), MembershipQueryKeys)
            .ExpectExecute(Sql("UpdateIAmAlivetimeKey"), affectedRows: 1);
        var queries = await ClusteringQueries.CreateInstance(storage);

        await queries.UpdateIAmAliveTimeAsync("cluster-alive", address, aliveTime);

        var call = AssertOperationCall(storage, Sql("UpdateIAmAlivetimeKey"), ExpectedCallKind.Execute);
        AssertParameters(
            call,
            ("DeploymentId", "cluster-alive"),
            ("Address", "10.50.60.70"),
            ("Port", 12_345),
            ("Generation", 9),
            ("IAmAliveTime", aliveTime));
        Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(Parameter(call, "IAmAliveTime").Value).Kind);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task DeleteReminderRowsAsync_ExecutesSentinelQueryAndCapturesServiceId()
    {
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), ReminderQueryKeys)
            .ExpectExecute(Sql("DeleteReminderRowsKey"), affectedRows: 12);
        var queries = await ReminderQueries.CreateInstance(storage);

        await queries.DeleteReminderRowsAsync("service-delete-all");

        AssertParameters(
            AssertOperationCall(storage, Sql("DeleteReminderRowsKey"), ExpectedCallKind.Execute),
            ("ServiceId", "service-delete-all"));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task DeleteMembershipTableEntriesAsync_ExecutesSentinelQueryAndCapturesDeploymentId()
    {
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), MembershipQueryKeys)
            .ExpectExecute(Sql("DeleteMembershipTableEntriesKey"), affectedRows: 17);
        var queries = await ClusteringQueries.CreateInstance(storage);

        await queries.DeleteMembershipTableEntriesAsync("cluster-delete-all");

        AssertParameters(
            AssertOperationCall(storage, Sql("DeleteMembershipTableEntriesKey"), ExpectedCallKind.Execute),
            ("DeploymentId", "cluster-delete-all"));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task CleanupDefunctSiloEntriesAsync_ExecutesSentinelQueryAndCapturesUtcDateTime()
    {
        var beforeDate = new DateTimeOffset(2026, 8, 28, 4, 15, 30, TimeSpan.FromHours(5.5));
        var storage = ExpectQueryLoad(new ScriptedRelationalStorage(), MembershipQueryKeys)
            .ExpectExecute(Sql("CleanupDefunctSiloEntriesKey"), affectedRows: 5);
        var queries = await ClusteringQueries.CreateInstance(storage);

        await queries.CleanupDefunctSiloEntriesAsync(beforeDate, "cluster-cleanup");

        var call = AssertOperationCall(storage, Sql("CleanupDefunctSiloEntriesKey"), ExpectedCallKind.Execute);
        AssertParameters(
            call,
            ("DeploymentId", "cluster-cleanup"),
            ("IAmAliveTime", new DateTime(2026, 8, 27, 22, 45, 30, DateTimeKind.Utc)));
        Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(Parameter(call, "IAmAliveTime").Value).Kind);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task CreateInstance_WithInvalidInvariantName_DelegatesThroughTwoArgumentFactory()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => PersistenceQueries.CreateInstance("   ", "Data Source=:memory:"));

        Assert.Equal("invariantName", exception.ParamName);
        Assert.Equal(
            "The name of invariant must contain characters (Parameter 'invariantName')",
            exception.Message);
    }

    [Fact]
    public void GetQueryKeyAndValue_WithDuplicateColumns_ReturnsExpectedPair()
    {
        using var reader = new DataTableReader(
            CreateTable(
                [("QueryKey", typeof(string)), ("QueryText", typeof(string))],
                ["duplicate-query-key", "/* duplicate converter sentinel */ SELECT 42"]));
        Assert.True(reader.Read());
        IDataRecord record = reader;

        var result = ReminderQueries.GetQueryKeyAndValue(record);

        Assert.Equal("duplicate-query-key", result.Key);
        Assert.Equal("/* duplicate converter sentinel */ SELECT 42", result.Value);
        Assert.False(reader.Read());
    }


    private static readonly (string Name, Type Type)[] DirectoryEntryColumns =
    [
        ("ClusterId", typeof(string)),
        ("ProviderId", typeof(string)),
        ("GrainId", typeof(string)),
        ("SiloAddress", typeof(string)),
        ("ActivationId", typeof(string)),
    ];

    private static ScriptedRelationalStorage ExpectQueryLoad(
        ScriptedRelationalStorage storage,
        IEnumerable<string> keys) =>
        storage.ExpectRead(
            GetQueriesSql,
            CreateQueryTable(keys
                .Select(key => (key, key == "StreamSchemaVersionKey" ? "2" : Sql(key)))
                .ToArray()));

    private static DataTable CreateQueryTable(params (string Key, string Query)[] queries) =>
        CreateTable(
            [("QueryKey", typeof(string)), ("QueryText", typeof(string))],
            queries.Select(query => new object?[] { query.Key, query.Query }).ToArray());

    private static DataTable CreateTable(
        (string Name, Type Type)[] columns,
        params object?[][] rows)
    {
        var table = new DataTable();
        foreach (var (name, type) in columns)
        {
            table.Columns.Add(name, type);
        }

        foreach (var row in rows)
        {
            table.Rows.Add(row.Select(value => value ?? DBNull.Value).ToArray());
        }

        return table;
    }

    private static string Sql(string key) => $"/* sentinel:{key} */ SELECT 1";

    private static RecordedStorageCall AssertOperationCall(
        ScriptedRelationalStorage storage,
        string expectedQuery)
    {
        Assert.Equal(2, storage.Calls.Count);
        var call = storage.Calls[1];
        Assert.Equal(ExpectedCallKind.Read, call.Kind);
        Assert.Equal(expectedQuery, call.Query);
        return call;
    }

    private static void AssertParameters(
        RecordedStorageCall call,
        params (string Name, object? Value)[] expected)
    {
        var parameters = call.Command.Parameters
            .Cast<RecordingDbParameter>()
            .ToDictionary(parameter => parameter.ParameterName, StringComparer.Ordinal);
        Assert.Equal(expected.Length, parameters.Count);
        foreach (var (name, value) in expected)
        {
            Assert.Equal(value, parameters[name].Value);
        }
    }

    private static RecordingDbParameter Parameter(RecordedStorageCall call, string name) =>
        Assert.IsType<RecordingDbParameter>(call.Command.Parameters[name]);

    private static RecordedStorageCall AssertOperationCall(
        ScriptedRelationalStorage storage,
        string expectedQuery,
        ExpectedCallKind expectedKind)
    {
        Assert.Equal(2, storage.Calls.Count);
        var call = storage.Calls[1];
        Assert.Equal(expectedKind, call.Kind);
        Assert.Equal(expectedQuery, call.Query);
        return call;
    }

    private static void AssertOnlyQueryLoadCall(ScriptedRelationalStorage storage)
    {
        var call = Assert.Single(storage.Calls);
        Assert.Equal(ExpectedCallKind.Read, call.Kind);
        Assert.Equal(GetQueriesSql, call.Query);
        Assert.Empty(call.Command.Parameters);
    }
}
