using DurableOrderProcessing;
using DurableOrderProcessing.Grains;
using Orleans.DurableTasks;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class CatalogItemIdempotencyTests : IAsyncLifetime
{
    private readonly DurableTaskRecoveryFixture _fixture = new(DurableTaskRecoveryFixture.OrleansBinary);

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservationOutcomeSurvivesReleaseOrCommitAndNewTaskIds(bool commit)
    {
        const int units = 2;
        var cancellationToken = TestContext.Current.CancellationToken;
        var catalog = _fixture.Client.GetGrain<ICatalogItemGrain>(1);
        var orderId = Guid.NewGuid();
        var initial = Assert.IsType<CatalogItem>(await catalog.GetAsync());

        Assert.True(await ReserveAsync("reserve"));
        Assert.True(await ReserveAsync("duplicate-reserve"));
        var reserved = await catalog.GetAsync();
        Assert.Equal(initial with { AvailableStock = initial.AvailableStock - units, ReservedStock = units }, reserved);

        if (commit)
        {
            await (await catalog.CommitAsync(orderId).WithId("commit").ScheduleAsync(cancellationToken)).WaitAsync(cancellationToken);
        }
        else
        {
            Assert.True(await (await catalog.ReleaseAsync(orderId).WithId("release").ScheduleAsync(cancellationToken)).WaitAsync(cancellationToken));
        }

        var finished = await catalog.GetAsync();
        Assert.Equal(commit ? initial with { AvailableStock = initial.AvailableStock - units, SoldStock = units } : initial, finished);
        await _fixture.Cluster.DeactivateAsync(catalog);

        Assert.True(await ReserveAsync("reserve-after-finish"));
        await (await catalog.CommitAsync(orderId).WithId("duplicate-commit").ScheduleAsync(cancellationToken)).WaitAsync(cancellationToken);
        Assert.False(await (await catalog.ReleaseAsync(orderId).WithId("duplicate-release").ScheduleAsync(cancellationToken)).WaitAsync(cancellationToken));
        Assert.Equal(finished, await catalog.GetAsync());

        var conflicting = await catalog.ReserveAsync(orderId, units + 1).WithId("conflicting-reserve").ScheduleAsync(cancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await conflicting.WaitAsync(cancellationToken));
        Assert.Equal(finished, await catalog.GetAsync());

        async Task<bool> ReserveAsync(string taskId) =>
            await (await catalog.ReserveAsync(orderId, units).WithId(taskId).ScheduleAsync(cancellationToken)).WaitAsync(cancellationToken);
    }

    [Fact]
    public async Task RejectedReservationKeepsItsOutcomeAfterStockBecomesAvailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var catalog = _fixture.Client.GetGrain<ICatalogItemGrain>(1);
        var initial = Assert.IsType<CatalogItem>(await catalog.GetAsync());
        var holdingOrder = Guid.NewGuid();
        var rejectedOrder = Guid.NewGuid();
        Assert.True(await (await catalog.ReserveAsync(holdingOrder, initial.AvailableStock).WithId("hold").ScheduleAsync(cancellationToken)).WaitAsync(cancellationToken));
        Assert.False(await (await catalog.ReserveAsync(rejectedOrder, 1).WithId("reject").ScheduleAsync(cancellationToken)).WaitAsync(cancellationToken));
        Assert.True(await (await catalog.ReleaseAsync(holdingOrder).WithId("release").ScheduleAsync(cancellationToken)).WaitAsync(cancellationToken));

        await _fixture.Cluster.DeactivateAsync(catalog);

        Assert.False(await (await catalog.ReserveAsync(rejectedOrder, 1).WithId("retry-rejected").ScheduleAsync(cancellationToken)).WaitAsync(cancellationToken));
        Assert.Equal(initial, await catalog.GetAsync());
    }
}
