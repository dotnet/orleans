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

public sealed class EventuallyVisibleReminderServiceFixture : IAsyncLifetime
{
    private InProcessTestCluster? _cluster;

    public EventuallyVisibleReminderTable Table { get; } = new();

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

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("BVT"), TestCategory("Reminders"), TestCategory("ReminderTestKit")]
public sealed class EventuallyVisibleReminderServiceTests
    : ReminderServiceTestRunner, IClassFixture<EventuallyVisibleReminderServiceFixture>
{
    private readonly EventuallyVisibleReminderTable _table;

    public EventuallyVisibleReminderServiceTests(EventuallyVisibleReminderServiceFixture fixture)
        : base(fixture.GrainFactory, fixture.Table, "EventuallyVisibleReminderTable")
    {
        _table = fixture.Table;
    }

    [Fact]
    public override async Task ReminderService_RegisterLookupEnumerateAndUnregister()
    {
        await base.ReminderService_RegisterLookupEnumerateAndUnregister();

        Assert.True(_table.HiddenPointReads > 0);
        Assert.True(_table.HiddenEnumerationReads > 0);
    }

    [Fact]
    public override async Task ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate()
    {
        await base.ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate();

        Assert.True(_table.HiddenPointReads > 0);
        Assert.True(_table.HiddenEnumerationReads > 0);
    }
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
            fixture.Table,
            "NonRotatingReminderTable")
    {
        _table = fixture.Table;
    }

    [Fact]
    public override async Task ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate()
    {
        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(
            base.ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate);

        Assert.Equal(2, _table.UpsertCount);
        Assert.Equal(["non-rotating-etag", "non-rotating-etag"], _table.ReturnedETags);
        Assert.Contains("updated schedule is visible with the reused ETag", exception.Message, StringComparison.Ordinal);
    }
}

public abstract class CorruptingReminderServiceFixture : IAsyncLifetime
{
    private InProcessTestCluster? _cluster;

    protected CorruptingReminderServiceFixture(ServiceEnumerationMutation mutation)
    {
        Table = new CorruptingEnumerationReminderTable(mutation);
    }

    public CorruptingEnumerationReminderTable Table { get; }

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
        : base(fixture.GrainFactory, fixture.Table, "DuplicateEnumeration")
    {
    }

    [Fact]
    public async Task ReminderService_UpdateRejectsDuplicateEnumeratedIdentity()
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
        : base(fixture.GrainFactory, fixture.Table, "StaleScheduleEnumeration")
    {
    }

    [Fact]
    public async Task ReminderService_UpdateRejectsStaleEnumeratedSchedule()
    {
        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(
            ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate);

        Assert.Contains("provider=StaleScheduleEnumeration", exception.Message, StringComparison.Ordinal);
        Assert.Contains("rowCount=1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Period=00:12:00", exception.Message, StringComparison.Ordinal);
    }
}

public enum ServiceEnumerationMutation
{
    None,
    Duplicate,
    StaleSchedule
}

public sealed class CorruptingEnumerationReminderTable(ServiceEnumerationMutation mutation) : IReminderTable
{
    private readonly IdealizedReminderTable _inner = new(nameof(CorruptingEnumerationReminderTable));

    public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);

    public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => _inner.ReadRow(grainId, reminderName);

    public async Task<ReminderTableData> ReadRows(GrainId grainId)
    {
        var entries = (await _inner.ReadRows(grainId)).Reminders.Select(entry => Copy(entry)).ToList();
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

    public Task<ReminderTableData> ReadRows(uint begin, uint end) => _inner.ReadRows(begin, end);

    public Task<string?> UpsertRow(ReminderEntry entry) => _inner.UpsertRow(entry);

    public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        => _inner.RemoveRow(grainId, reminderName, eTag);

    public Task TestOnlyClearTable() => _inner.TestOnlyClearTable();

    private static ReminderEntry Copy(ReminderEntry entry, TimeSpan? period = null) => new()
    {
        GrainId = entry.GrainId,
        ReminderName = entry.ReminderName,
        StartAt = entry.StartAt,
        Period = period ?? entry.Period,
        ETag = entry.ETag
    };
}

public sealed class EventuallyVisibleReminderTable : IReminderTable
{
    private const int HiddenReadsPerMutation = 2;
    private readonly IdealizedReminderTable _inner = new(nameof(EventuallyVisibleReminderTable));
    private int _remainingHiddenPointReads;
    private int _remainingHiddenEnumerationReads;

    public int HiddenPointReads { get; private set; }

    public int HiddenEnumerationReads { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);

    public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
    {
        if (TryConsumeHiddenRead(ref _remainingHiddenPointReads))
        {
            HiddenPointReads++;
            return null;
        }

        return await _inner.ReadRow(grainId, reminderName);
    }

    public async Task<ReminderTableData> ReadRows(GrainId grainId)
    {
        if (TryConsumeHiddenRead(ref _remainingHiddenEnumerationReads))
        {
            HiddenEnumerationReads++;
            return new ReminderTableData();
        }

        return await _inner.ReadRows(grainId);
    }

    public async Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        if (TryConsumeHiddenRead(ref _remainingHiddenEnumerationReads))
        {
            HiddenEnumerationReads++;
            return new ReminderTableData();
        }

        return await _inner.ReadRows(begin, end);
    }

    public async Task<string?> UpsertRow(ReminderEntry entry)
    {
        var result = await _inner.UpsertRow(entry);
        HideReads();
        return result;
    }

    public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        var result = await _inner.RemoveRow(grainId, reminderName, eTag);
        if (result)
        {
            HideReads();
        }

        return result;
    }

    public Task TestOnlyClearTable() => _inner.TestOnlyClearTable();

    private void HideReads()
    {
        Volatile.Write(ref _remainingHiddenPointReads, HiddenReadsPerMutation);
        Volatile.Write(ref _remainingHiddenEnumerationReads, HiddenReadsPerMutation);
    }

    private static bool TryConsumeHiddenRead(ref int remaining)
    {
        while (true)
        {
            var current = Volatile.Read(ref remaining);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref remaining, current - 1, current) == current)
            {
                return true;
            }
        }
    }
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
