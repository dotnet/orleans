using Microsoft.Extensions.DependencyInjection;
using Orleans.Reminders.TestKit;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Reminders.TestKit.Tests;

public sealed class IdealizedReminderServiceFixture : IAsyncLifetime
{
    private InProcessTestCluster? _cluster;

    public IdealizedReminderTable Oracle { get; } = new("ServiceOracle");

    public IGrainFactory GrainFactory => _cluster?.Client
        ?? throw new InvalidOperationException("The cluster has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.UseIdealizedReminderTable(Oracle);
        _cluster = builder.Build();
        await _cluster.DeployAsync();

        Assert.Same(
            Oracle,
            _cluster.Silos[0].ServiceProvider.GetRequiredService<IReminderTable>());
    }

    public async ValueTask DisposeAsync()
    {
        if (_cluster is not { } cluster)
        {
            return;
        }

        await cluster.StopAllSilosAsync();
        await cluster.DisposeAsync();
    }
}

/// <summary>Runs the reusable cluster-level reminder service conformance runner against the oracle.</summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("BVT"), TestCategory("Reminders"), TestCategory("ReminderTestKit")]
public sealed class ReminderServiceConformanceTests : ReminderServiceTestRunner, IClassFixture<IdealizedReminderServiceFixture>
{
    public ReminderServiceConformanceTests(IdealizedReminderServiceFixture fixture)
        : base(fixture.GrainFactory, fixture.Oracle, "IdealizedReminderTable")
    {
    }

    [Fact]
    public override Task ReminderService_RegisterLookupEnumerateAndUnregister()
        => base.ReminderService_RegisterLookupEnumerateAndUnregister();

    [Fact]
    public override Task ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate()
        => base.ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate();
}
