using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Orleans.Reminders.TestKit;

/// <summary>
/// Base fixture for exercising an <see cref="IReminderTable"/> implementation which is hosted by a silo.
/// </summary>
/// <remarks>
/// The fixture deploys an <see cref="InProcessTestCluster"/>, configures the provider under test through
/// <see cref="ConfigureSilo"/>, and resolves the reminder table from the first silo. Connect
/// <see cref="InitializeAsync"/> and <see cref="DisposeAsync"/> to the lifecycle hooks of your test framework.
/// </remarks>
public abstract class ReminderTableTestFixture
{
    private ExceptionDispatchInfo? _preconditionException;
    private InProcessTestCluster? _cluster;
    private IReminderTable? _reminderTable;

    /// <summary>
    /// Gets the deployed in-process test cluster.
    /// </summary>
    protected InProcessTestCluster Cluster => _cluster ?? throw new InvalidOperationException("The test cluster has not been initialized.");

    /// <summary>
    /// Gets the grain factory of the cluster client.
    /// </summary>
    public IGrainFactory GrainFactory => Cluster.Client;

    /// <summary>
    /// Gets the reminder table under test.
    /// </summary>
    public IReminderTable ReminderTable
    {
        get
        {
            _preconditionException?.Throw();
            return _reminderTable ?? throw new InvalidOperationException("The reminder table has not been initialized.");
        }
    }

    /// <summary>
    /// Configures the silo with the reminder provider to test.
    /// </summary>
    /// <param name="siloBuilder">The silo builder.</param>
    protected abstract void ConfigureSilo(ISiloBuilder siloBuilder);

    /// <summary>
    /// Configures additional test cluster options.
    /// </summary>
    /// <param name="builder">The test cluster builder.</param>
    protected virtual void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
    }

    /// <summary>
    /// Resolves the reminder table under test from a silo's service provider.
    /// </summary>
    /// <param name="services">The silo's service provider.</param>
    /// <returns>The reminder table.</returns>
    protected virtual IReminderTable ResolveReminderTable(IServiceProvider services) => services.GetRequiredService<IReminderTable>();

    /// <summary>
    /// Checks preconditions such as the availability of an external service.
    /// </summary>
    protected virtual void CheckPreconditionsOrThrow()
    {
    }

    /// <summary>
    /// Rethrows the captured precondition failure, if any.
    /// </summary>
    public void EnsurePreconditionsMet() => _preconditionException?.Throw();

    /// <summary>
    /// Deploys the cluster and resolves the reminder table.
    /// </summary>
    /// <returns>A task which represents the asynchronous initialization.</returns>
    public virtual async ValueTask InitializeAsync()
    {
        try
        {
            CheckPreconditionsOrThrow();
        }
        catch (Exception exception)
        {
            _preconditionException = ExceptionDispatchInfo.Capture(exception);
            return;
        }

        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureSilo((_, siloBuilder) => ConfigureSilo(siloBuilder));
        ConfigureTestCluster(builder);

        var cluster = builder.Build();
        await cluster.DeployAsync().ConfigureAwait(false);
        _cluster = cluster;

        var reminderTable = ResolveReminderTable(cluster.Silos[0].ServiceProvider);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await reminderTable.StartAsync(cancellation.Token).ConfigureAwait(false);
        _reminderTable = reminderTable;
    }

    /// <summary>
    /// Stops and disposes the cluster.
    /// </summary>
    /// <returns>A task which represents the asynchronous disposal.</returns>
    public virtual async ValueTask DisposeAsync()
    {
        if (_cluster is not { } cluster)
        {
            return;
        }

        try
        {
            await cluster.StopAllSilosAsync().ConfigureAwait(false);
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }
}
