using Microsoft.EntityFrameworkCore;
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
}
