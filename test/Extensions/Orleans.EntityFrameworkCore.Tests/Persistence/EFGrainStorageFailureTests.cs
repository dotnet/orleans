using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Persistence.EntityFrameworkCore.Data;
using Orleans.Runtime;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Persistence;

[TestArea("EFCore")]
[TestProvider("None")]
[TestSuite("BVT")]
public sealed class EFGrainStorageFailureTests
{
    [Fact]
    public async Task PR8654_Persistence_WriteStateAsync_NonDuplicateDbUpdateExceptionPropagatesUntranslated()
    {
        var expected = new DbUpdateException("database unavailable");
        var interceptor = new ThrowingSaveChangesInterceptor(expected);
        var options = new DbContextOptionsBuilder<FailureDbContext>()
            .UseSqlite("Data Source=:memory:")
            .AddInterceptors(interceptor)
            .Options;
        var factory = new FailureDbContextFactory(options);
        await using var services = new ServiceCollection().BuildServiceProvider();
        var storage = new EFGrainStorage<FailureDbContext, Guid>(
            "FailureRegression",
            NullLoggerFactory.Instance,
            Options.Create(new ClusterOptions { ServiceId = "failure-service" }),
            factory,
            new GuidGrainStorageETagConverter(),
            new StubGrainStorageSerializer(),
            new StubActivatorProvider());
        var value = new FailureState("caller-state");
        var state = new GrainState<FailureState>(value);

        var actual = await Assert.ThrowsAsync<DbUpdateException>(
            () => storage.WriteStateAsync(
                "profile",
                GrainId.Create("failure", "non-duplicate"),
                state));

        Assert.Same(expected, actual);
        Assert.Equal(1, interceptor.SavingChangesAsyncCallCount);
        Assert.False(state.RecordExists);
        Assert.Null(state.ETag);
        Assert.Same(value, state.State);
    }

    private sealed class FailureDbContextFactory(DbContextOptions<FailureDbContext> options)
        : IDbContextFactory<FailureDbContext>
    {
        public FailureDbContext CreateDbContext() => new(options);
    }

    private sealed class FailureDbContext(DbContextOptions<FailureDbContext> options)
        : GuidGrainStateDbContext<FailureDbContext>(options);

    private sealed class ThrowingSaveChangesInterceptor(DbUpdateException exception) : SaveChangesInterceptor
    {
        public int SavingChangesAsyncCallCount { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SavingChangesAsyncCallCount++;
            return ValueTask.FromException<InterceptionResult<int>>(exception);
        }
    }

    private sealed class StubGrainStorageSerializer : IGrainStorageSerializer
    {
        public BinaryData Serialize<T>(T? input) => new([1, 2, 3]);

        public T? Deserialize<T>(BinaryData input) => throw new NotSupportedException();
    }

    private sealed record FailureState(string Value);

    private sealed class StubActivatorProvider : IActivatorProvider
    {
        public IActivator<T> GetActivator<T>() => throw new NotSupportedException();
    }
}
