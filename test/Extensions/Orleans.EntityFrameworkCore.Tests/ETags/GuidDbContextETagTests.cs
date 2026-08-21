using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orleans.Clustering.EntityFrameworkCore.Data;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.GrainDirectory.EntityFrameworkCore.Data;
using Orleans.Persistence.EntityFrameworkCore.Data;
using Orleans.Reminders.EntityFrameworkCore.Data;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.ETags;

[TestArea("EFCore")]
[TestProvider("None")]
[TestSuite("BVT")]
public sealed class GuidDbContextETagTests
{
    private static readonly Guid InitialETag = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public Task ClusterRecord_ETagLifecycleAndStaleConcurrency_AreApplicationManaged() =>
        VerifyLifecycleAndConcurrency<SqliteClusterDbContext, ClusterRecord<Guid>, int>(
            options => new SqliteClusterDbContext(options),
            () => new ClusterRecord<Guid>
            {
                Id = "cluster-one",
                Timestamp = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
                Version = 7,
                ETag = InitialETag
            },
            record => record.ETag,
            (record, value) => record.Version = value,
            record => record.Version,
            winnerValue: 8,
            staleValue: 9);

    [Fact]
    public Task SiloRecord_ETagLifecycleAndStaleConcurrency_AreApplicationManaged() =>
        VerifyLifecycleAndConcurrency<SqliteClusterDbContext, SiloRecord<Guid>, DateTimeOffset>(
            options => new SqliteClusterDbContext(options),
            () => new SiloRecord<Guid>
            {
                ClusterId = "cluster-for-silo",
                Address = "127.0.0.1",
                Port = 11111,
                Generation = 12345,
                Name = "silo-one",
                HostName = "host-one",
                Status = Runtime.SiloStatus.Active,
                ProxyPort = 30000,
                StartTime = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
                IAmAliveTime = new DateTimeOffset(2026, 8, 11, 12, 1, 0, TimeSpan.Zero),
                ETag = InitialETag,
                Cluster = new ClusterRecord<Guid>
                {
                    Id = "cluster-for-silo",
                    Timestamp = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
                    Version = 1,
                    ETag = InitialETag
                }
            },
            record => record.ETag,
            (record, value) => record.IAmAliveTime = value,
            record => record.IAmAliveTime,
            winnerValue: new DateTimeOffset(2026, 8, 11, 12, 2, 0, TimeSpan.Zero),
            staleValue: new DateTimeOffset(2026, 8, 11, 12, 3, 0, TimeSpan.Zero));

    [Fact]
    public Task GrainActivationRecord_ETagLifecycleAndStaleConcurrency_AreApplicationManaged() =>
        VerifyLifecycleAndConcurrency<SqliteGrainDirectoryDbContext, GrainActivationRecord<Guid>, long>(
            options => new SqliteGrainDirectoryDbContext(options),
            () => new GrainActivationRecord<Guid>
            {
                ClusterId = "cluster-one",
                GrainId = "grain-one",
                SiloAddress = "127.0.0.1:11111@12345",
                ActivationId = "activation-one",
                MembershipVersion = 11,
                ETag = InitialETag
            },
            record => record.ETag,
            (record, value) => record.MembershipVersion = value,
            record => record.MembershipVersion,
            winnerValue: 12L,
            staleValue: 13L);

    [Fact]
    public Task GrainStateRecord_ETagLifecycleAndStaleConcurrency_AreApplicationManaged() =>
        VerifyLifecycleAndConcurrency<SqliteGrainStateDbContext, GrainStateRecord<Guid>, string>(
            options => new SqliteGrainStateDbContext(options),
            () => new GrainStateRecord<Guid>
            {
                ServiceId = "service-one",
                GrainType = "grain-type-one",
                StateType = "state-type-one",
                GrainId = "grain-one",
                Data = """{"value":"initial"}""",
                ETag = InitialETag
            },
            record => record.ETag,
            (record, value) => record.Data = value,
            record => record.Data!,
            winnerValue: """{"value":"winner"}""",
            staleValue: """{"value":"stale"}""");

    [Fact]
    public Task ReminderRecord_ETagLifecycleAndStaleConcurrency_AreApplicationManaged() =>
        VerifyLifecycleAndConcurrency<SqliteReminderDbContext, ReminderRecord<Guid>, TimeSpan>(
            options => new SqliteReminderDbContext(options),
            () => new ReminderRecord<Guid>
            {
                ServiceId = "service-one",
                GrainId = "grain-one",
                Name = "reminder-one",
                StartAt = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
                Period = TimeSpan.FromMinutes(5),
                GrainHash = 1234567890,
                ETag = InitialETag
            },
            record => record.ETag,
            (record, value) => record.Period = value,
            record => record.Period,
            winnerValue: TimeSpan.FromMinutes(10),
            staleValue: TimeSpan.FromMinutes(15));

    private static async Task VerifyLifecycleAndConcurrency<TContext, TRecord, TValue>(
        Func<DbContextOptions<TContext>, TContext> createContext,
        Func<TRecord> createRecord,
        Func<TRecord, Guid> getETag,
        Action<TRecord, TValue> mutate,
        Func<TRecord, TValue> getObservable,
        TValue winnerValue,
        TValue staleValue)
        where TContext : DbContext
        where TRecord : class
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TContext>()
            .UseSqlite(connection)
            .Options;

        Guid insertedETag;
        await using (var setup = createContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var record = createRecord();
            setup.Set<TRecord>().Add(record);

            var affected = setup.SaveChanges();

            insertedETag = getETag(record);
            Assert.True(affected >= 1);
            Assert.NotEqual(Guid.Empty, insertedETag);
            Assert.NotEqual(InitialETag, insertedETag);
            Assert.Equal(EntityState.Unchanged, setup.Entry(record).State);

            var unchangedAffected = await setup.SaveChangesAsync();
            Assert.Equal(0, unchangedAffected);
            Assert.Equal(insertedETag, getETag(record));
        }

        await using var winner = createContext(options);
        await using var stale = createContext(options);
        var winnerRecord = await winner.Set<TRecord>().SingleAsync();
        var staleRecord = await stale.Set<TRecord>().SingleAsync();
        Assert.Equal(insertedETag, getETag(winnerRecord));
        Assert.Equal(insertedETag, getETag(staleRecord));

        mutate(winnerRecord, winnerValue);
        var winnerAffected = winner.SaveChanges();
        var winnerETag = getETag(winnerRecord);
        Assert.Equal(1, winnerAffected);
        Assert.NotEqual(insertedETag, winnerETag);

        mutate(staleRecord, staleValue);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        Assert.Equal(insertedETag, stale.Entry(staleRecord).Property("ETag").OriginalValue);
        Assert.NotEqual(insertedETag, getETag(staleRecord));

        await using var verification = createContext(options);
        var persisted = await verification.Set<TRecord>().AsNoTracking().SingleAsync();
        Assert.Equal(winnerETag, getETag(persisted));
        Assert.Equal(winnerValue, getObservable(persisted));
    }

    private sealed class SqliteClusterDbContext(DbContextOptions<SqliteClusterDbContext> options)
        : GuidClusterDbContext<SqliteClusterDbContext>(options);

    private sealed class SqliteGrainDirectoryDbContext(DbContextOptions<SqliteGrainDirectoryDbContext> options)
        : GuidGrainDirectoryDbContext<SqliteGrainDirectoryDbContext>(options);

    private sealed class SqliteGrainStateDbContext(DbContextOptions<SqliteGrainStateDbContext> options)
        : GuidGrainStateDbContext<SqliteGrainStateDbContext>(options);

    private sealed class SqliteReminderDbContext(DbContextOptions<SqliteReminderDbContext> options)
        : GuidReminderDbContext<SqliteReminderDbContext>(options);
}
