#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Orleans.Testing.Reminders;

/// <summary>
/// Provides deterministic time control for reminder tests running in an <see cref="InProcessTestCluster"/>.
/// </summary>
/// <remarks>
/// Attach an instance to an <see cref="InProcessTestClusterBuilder"/> before building the cluster.
/// The attached clock replaces the silo <see cref="TimeProvider"/> and tunes
/// <see cref="ReminderOptions"/> for deterministic reminder scheduling.
/// </remarks>
public sealed class ReminderTestClock : IDisposable
{
    private readonly SemaphoreSlim _advanceLock = new(1, 1);
    private bool _disposed;

    private ReminderTestClock(
        DateTimeOffset initialTime,
        TimeSpan minimumReminderPeriod,
        TimeSpan refreshReminderListPeriod,
        TimeSpan initializationTimeout)
    {
        TimeProvider = new FakeTimeProvider(initialTime);
        MinimumReminderPeriod = minimumReminderPeriod;
        RefreshReminderListPeriod = refreshReminderListPeriod;
        InitializationTimeout = initializationTimeout;
    }

    internal FakeTimeProvider TimeProvider { get; }

    /// <summary>
    /// Gets the minimum reminder period configured for clusters using this clock.
    /// </summary>
    public TimeSpan MinimumReminderPeriod { get; }

    /// <summary>
    /// Gets the reminder table refresh period configured for clusters using this clock.
    /// </summary>
    public TimeSpan RefreshReminderListPeriod { get; }

    /// <summary>
    /// Gets the reminder service initialization timeout configured for clusters using this clock.
    /// </summary>
    public TimeSpan InitializationTimeout { get; }

    /// <summary>
    /// Attaches a deterministic reminder clock to the provided <see cref="InProcessTestClusterBuilder"/>.
    /// </summary>
    /// <param name="builder">The test cluster builder.</param>
    /// <param name="minimumReminderPeriod">An optional minimum reminder period override.</param>
    /// <param name="refreshReminderListPeriod">An optional reminder list refresh period override.</param>
    /// <param name="initializationTimeout">An optional reminder initialization timeout override.</param>
    /// <returns>The attached reminder test clock.</returns>
    public static ReminderTestClock Attach(
        InProcessTestClusterBuilder builder,
        TimeSpan? minimumReminderPeriod = null,
        TimeSpan? refreshReminderListPeriod = null,
        TimeSpan? initializationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var clock = new ReminderTestClock(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            minimumReminderPeriod ?? TimeSpan.FromMinutes(1),
            refreshReminderListPeriod ?? TimeSpan.FromSeconds(1),
            initializationTimeout ?? TimeSpan.FromSeconds(30));

        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock.TimeProvider));
            siloBuilder.Services.PostConfigure<ReminderOptions>(options =>
            {
                options.MinimumReminderPeriod = clock.MinimumReminderPeriod;
                options.RefreshReminderListPeriod = clock.RefreshReminderListPeriod;
                options.InitializationTimeout = clock.InitializationTimeout;
            });
        });

        return clock;
    }

    /// <summary>
    /// Advances the cluster reminder clock by the specified amount.
    /// </summary>
    /// <param name="amount">The amount of time to advance.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task AdvanceAsync(TimeSpan amount, CancellationToken cancellationToken = default)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "The advance amount must not be negative.");
        }

        await _advanceLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            TimeProvider.Advance(amount);
        }
        finally
        {
            _advanceLock.Release();
        }

        await Task.Yield();
    }

    /// <summary>
    /// Prevents concurrent clock advances until the returned handle is disposed.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async-disposable handle which resumes clock advances when disposed.</returns>
    public async Task<IAsyncDisposable> FreezeAsync(CancellationToken cancellationToken = default)
    {
        await _advanceLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return new AsyncLockRelease(_advanceLock);
        }
        catch
        {
            _advanceLock.Release();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _advanceLock.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class AsyncLockRelease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _semaphore = semaphore;
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
