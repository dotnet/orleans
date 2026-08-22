using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
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
        : base(fixture.GrainFactory, ReminderTableProviderProfiles.Oracle("IdealizedReminderTable"), fixture.Oracle)
    {
    }

    [Fact]
    public override Task ReminderService_RegisterLookupEnumerateAndUnregister()
        => base.ReminderService_RegisterLookupEnumerateAndUnregister();

    [Fact]
    public override Task ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate()
        => base.ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate();
}

public sealed class NonRotatingReminderServiceFixture : IAsyncLifetime
{
    private InProcessTestCluster? _cluster;

    public NonRotatingReminderTable Table { get; } = new();

    public IGrainFactory GrainFactory => _cluster?.Client
        ?? throw new InvalidOperationException("The cluster has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.AddReminders();
            siloBuilder.Services.AddSingleton(Table);
            siloBuilder.Services.AddSingleton<IReminderTable>(Table);
        });
        _cluster = builder.Build();
        await _cluster.DeployAsync();

        Assert.Same(
            Table,
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

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("BVT"), TestCategory("Reminders"), TestCategory("ReminderTestKit")]
public sealed class NonRotatingReminderServiceConformanceTests
    : ReminderServiceTestRunner, IClassFixture<NonRotatingReminderServiceFixture>
{
    private readonly NonRotatingReminderTable _table;

    public NonRotatingReminderServiceConformanceTests(NonRotatingReminderServiceFixture fixture)
        : base(
            fixture.GrainFactory,
            ReminderTableCapabilities.Portable("NonRotatingReminderTable"),
            fixture.Table)
    {
        _table = fixture.Table;
    }

    [Fact]
    public override async Task ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate()
    {
        await base.ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate();

        Assert.Equal(2, _table.UpsertCount);
        Assert.Equal(["non-rotating-etag", "non-rotating-etag"], _table.ReturnedETags);
    }
}

public abstract class CorruptingReminderServiceFixture : IAsyncLifetime
{
    private InProcessTestCluster? _cluster;

    protected CorruptingReminderServiceFixture(ServiceEnumerationMutation mutation)
    {
        Table = new NonRotatingReminderTable(mutation);
    }

    public NonRotatingReminderTable Table { get; }

    public IGrainFactory GrainFactory => _cluster?.Client
        ?? throw new InvalidOperationException("The cluster has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.AddReminders();
            siloBuilder.Services.AddSingleton(Table);
            siloBuilder.Services.AddSingleton<IReminderTable>(Table);
        });
        _cluster = builder.Build();
        await _cluster.DeployAsync();
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

public sealed class DuplicateEnumerationReminderServiceFixture()
    : CorruptingReminderServiceFixture(ServiceEnumerationMutation.Duplicate);

public sealed class StaleScheduleReminderServiceFixture()
    : CorruptingReminderServiceFixture(ServiceEnumerationMutation.StaleSchedule);

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("BVT"), TestCategory("Reminders"), TestCategory("ReminderTestKit")]
public sealed class DuplicateEnumerationReminderServiceTests
    : ReminderServiceTestRunner, IClassFixture<DuplicateEnumerationReminderServiceFixture>
{
    public DuplicateEnumerationReminderServiceTests(DuplicateEnumerationReminderServiceFixture fixture)
        : base(fixture.GrainFactory, ReminderTableCapabilities.Portable("DuplicateEnumeration"), fixture.Table)
    {
    }

    [Fact]
    public async Task ReminderService_UpdateRejectsDuplicateEnumeratedIdentityEvenWithoutETagRotation()
    {
        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(
            ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate);

        Assert.Contains("provider=DuplicateEnumeration", exception.Message, StringComparison.Ordinal);
        Assert.Contains("rowCount=2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("enumerated=<null>", exception.Message, StringComparison.Ordinal);
    }
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("BVT"), TestCategory("Reminders"), TestCategory("ReminderTestKit")]
public sealed class StaleScheduleReminderServiceTests
    : ReminderServiceTestRunner, IClassFixture<StaleScheduleReminderServiceFixture>
{
    public StaleScheduleReminderServiceTests(StaleScheduleReminderServiceFixture fixture)
        : base(fixture.GrainFactory, ReminderTableCapabilities.Portable("StaleScheduleEnumeration"), fixture.Table)
    {
    }

    [Fact]
    public async Task ReminderService_UpdateRejectsStaleEnumeratedScheduleEvenWithoutETagRotation()
    {
        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(
            ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate);

        Assert.Contains("provider=StaleScheduleEnumeration", exception.Message, StringComparison.Ordinal);
        Assert.Contains("rowCount=1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Period=00:10:00", exception.Message, StringComparison.Ordinal);
    }
}

public enum ServiceEnumerationMutation
{
    None,
    Duplicate,
    StaleSchedule
}

public sealed class NonRotatingReminderTable(ServiceEnumerationMutation mutation = ServiceEnumerationMutation.None) : IReminderTable
{
    private const string ConstantETag = "non-rotating-etag";
    private readonly IdealizedReminderTable _inner = new(nameof(NonRotatingReminderTable));

    public int UpsertCount { get; private set; }

    public List<string> ReturnedETags { get; } = [];

    public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);

    public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
        => WithConstantETag(await _inner.ReadRow(grainId, reminderName));

    public async Task<ReminderTableData> ReadRows(GrainId grainId)
    {
        var entries = (await _inner.ReadRows(grainId)).Reminders.Select(entry => WithConstantETag(entry)!).ToList();
        if (entries.Count == 1)
        {
            if (mutation == ServiceEnumerationMutation.Duplicate)
            {
                entries.Add(Copy(entries[0]));
            }
            else if (mutation == ServiceEnumerationMutation.StaleSchedule)
            {
                entries[0] = Copy(entries[0], entries[0].Period.Add(TimeSpan.FromMinutes(1)));
            }
        }

        return new ReminderTableData(entries);
    }

    public async Task<ReminderTableData> ReadRows(uint begin, uint end)
        => new((await _inner.ReadRows(begin, end)).Reminders.Select(entry => WithConstantETag(entry)!).ToList());

    public async Task<string?> UpsertRow(ReminderEntry entry)
    {
        await _inner.UpsertRow(entry);
        UpsertCount++;
        ReturnedETags.Add(ConstantETag);
        return ConstantETag;
    }

    public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        var current = await _inner.ReadRow(grainId, reminderName);
        return current is not null
            && string.Equals(eTag, ConstantETag, StringComparison.Ordinal)
            && await _inner.RemoveRow(grainId, reminderName, current.ETag!);
    }

    public Task TestOnlyClearTable() => _inner.TestOnlyClearTable();

    private static ReminderEntry? WithConstantETag(ReminderEntry? entry)
    {
        if (entry is not null)
        {
            entry.ETag = ConstantETag;
        }

        return entry;
    }

    private static ReminderEntry Copy(ReminderEntry entry, TimeSpan? period = null) => new()
    {
        GrainId = entry.GrainId,
        ReminderName = entry.ReminderName,
        StartAt = entry.StartAt,
        Period = period ?? entry.Period,
        ETag = entry.ETag
    };
}
