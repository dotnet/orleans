using System.Data.Common;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.GrainDirectory.EntityFrameworkCore.Data;
using Orleans.Runtime;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.GrainDirectory;

[TestArea("EFCore")]
[TestProvider("None")]
[TestSuite("BVT")]
public sealed class EFCoreGrainDirectoryFailureTests
{
    [Fact]
    public async Task OperationalFailuresPropagateFromAllOperations()
    {
        var expected = new InvalidOperationException("database unavailable");
        var directory = CreateDirectory(new ThrowingFactory(expected));
        var address = CreateAddress();

        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => directory.Register(address)));
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => directory.Lookup(address.GrainId)));
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => directory.Unregister(address)));
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
            () => directory.UnregisterSilos([address.SiloAddress!])));
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
            () => directory.UnregisterMany([address])));
    }

    [Fact]
    public async Task Register_DbUpdateExceptionWithoutWinnerPropagates()
    {
        var expected = new DbUpdateException("database unavailable");
        var directory = CreateDirectory(new ThrowingFactory(expected));

        Assert.Same(expected, await Assert.ThrowsAsync<DbUpdateException>(() => directory.Register(CreateAddress())));
    }

    [Fact]
    public async Task PR8654_GrainDirectory_LargeUnregisterBatchesAreChunked()
    {
        await using var fixture = await BatchFixture.Create();
        var silos = Enumerable.Range(0, 600)
            .Select(index => SiloAddress.New(
                new IPEndPoint(IPAddress.Loopback, 12000 + index),
                22000 + index))
            .ToList();

        await fixture.Directory.UnregisterSilos(silos);

        Assert.Equal(1, fixture.Interceptor.ReaderCommandCount);
        fixture.Interceptor.Reset();
        var addresses = Enumerable.Range(0, 600)
            .Select(index => new GrainAddress
            {
                GrainId = GrainId.Create("batch", $"grain-{index}"),
                ActivationId = ActivationId.NewId(),
                MembershipVersion = new MembershipVersion(index + 1)
            })
            .ToList();

        await fixture.Directory.UnregisterMany(addresses);

        Assert.Equal(1, fixture.Interceptor.ReaderCommandCount);
    }

    private static EFCoreGrainDirectory<FailureDbContext, Guid> CreateDirectory(
        IDbContextFactory<FailureDbContext> factory) =>
        new(
            NullLoggerFactory.Instance,
            factory,
            Options.Create(new ClusterOptions { ClusterId = "failure-cluster", ServiceId = "failure-service" }),
            new GuidGrainDirectoryETagConverter());

    private static GrainAddress CreateAddress() =>
        new()
        {
            GrainId = GrainId.Parse($"failure/{Guid.NewGuid():N}"),
            ActivationId = ActivationId.NewId(),
            SiloAddress = SiloAddress.FromParsableString("127.0.0.1:11111@12345"),
            MembershipVersion = new MembershipVersion(1)
        };

    private sealed class ThrowingFactory(Exception exception) : IDbContextFactory<FailureDbContext>
    {
        public FailureDbContext CreateDbContext() => throw exception;

        public ValueTask<FailureDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<FailureDbContext>(exception);
    }

    private sealed class FailureDbContext(DbContextOptions<FailureDbContext> options)
        : GuidGrainDirectoryDbContext<FailureDbContext>(options);

    private sealed class BatchFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private BatchFixture(
            SqliteConnection connection,
            EFCoreGrainDirectory<FailureDbContext, Guid> directory,
            CountingCommandInterceptor interceptor)
        {
            _connection = connection;
            Directory = directory;
            Interceptor = interceptor;
        }

        public EFCoreGrainDirectory<FailureDbContext, Guid> Directory { get; }

        public CountingCommandInterceptor Interceptor { get; }

        public static async Task<BatchFixture> Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var interceptor = new CountingCommandInterceptor();
            var options = new DbContextOptionsBuilder<FailureDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(interceptor)
                .Options;
            var factory = new BatchDbContextFactory(options);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.EnsureCreatedAsync();
            }

            var directory = new EFCoreGrainDirectory<FailureDbContext, Guid>(
                NullLoggerFactory.Instance,
                factory,
                Options.Create(new ClusterOptions
                {
                    ClusterId = "batch-cluster",
                    ServiceId = "batch-service"
                }),
                new GuidGrainDirectoryETagConverter());
            return new BatchFixture(connection, directory, interceptor);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class BatchDbContextFactory(DbContextOptions<FailureDbContext> options)
        : IDbContextFactory<FailureDbContext>
    {
        public FailureDbContext CreateDbContext() => new(options);
    }

    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        public int ReaderCommandCount { get; private set; }

        public void Reset() => ReaderCommandCount = 0;

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommandCount++;
            return ValueTask.FromResult(result);
        }
    }
}
