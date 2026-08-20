using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Storage;
using Orleans.TestingHost;

namespace Orleans.Persistence.TestKit;

/// <summary>
/// Base fixture class for testing IGrainStorage implementations using InProcessTestCluster.
/// </summary>
/// <remarks>
/// This fixture provides an in-process Orleans cluster for testing storage providers.
/// Implement the abstract methods to configure your storage provider and retrieve it for testing.
/// </remarks>
public abstract class GrainStorageTestFixture
{
    private ExceptionDispatchInfo? _preconditionException;
    private InProcessTestCluster? _cluster;
    private IGrainStorage? _storage;

    /// <summary>
    /// Gets the in-process test cluster.
    /// </summary>
    protected InProcessTestCluster Cluster => _cluster ?? throw new InvalidOperationException("The test cluster has not been initialized.");

    /// <summary>
    /// Gets the grain factory for creating grain references.
    /// </summary>
    public IGrainFactory GrainFactory => Cluster.Client;

    /// <summary>
    /// Gets the storage provider being tested.
    /// </summary>
    public IGrainStorage Storage
    {
        get
        {
            _preconditionException?.Throw();
            return _storage ?? throw new InvalidOperationException("The storage provider has not been initialized.");
        }
    }

    /// <summary>
    /// Gets the name of the storage provider being tested.
    /// </summary>
    protected abstract string StorageProviderName { get; }

    /// <summary>
    /// Checks preconditions before initializing the cluster.
    /// Override this to check for external dependencies (e.g., Azure Storage emulator).
    /// </summary>
    /// <exception cref="Exception">Thrown if preconditions are not met.</exception>
    protected virtual void CheckPreconditionsOrThrow()
    {
    }

    /// <summary>
    /// Ensures that preconditions are met. Call this from test constructors to skip tests if preconditions fail.
    /// </summary>
    public void EnsurePreconditionsMet()
    {
        _preconditionException?.Throw();
    }

    /// <summary>
    /// Configures the silo with the storage provider to test.
    /// </summary>
    /// <param name="siloBuilder">The silo builder to configure.</param>
    /// <example>
    /// <code>
    /// protected override void ConfigureSilo(ISiloBuilder siloBuilder)
    /// {
    ///     siloBuilder.AddMemoryGrainStorage("TestStorage");
    /// }
    /// </code>
    /// </example>
    protected abstract void ConfigureSilo(ISiloBuilder siloBuilder);

    /// <summary>
    /// Configures additional test cluster options if needed.
    /// </summary>
    /// <param name="builder">The test cluster builder.</param>
    protected virtual void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
    }

    /// <summary>
    /// Initializes the test cluster and resolves the configured storage provider.
    /// </summary>
    /// <returns>A task which represents the asynchronous initialization operation.</returns>
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

        _storage = cluster.Silos[0].ServiceProvider.GetRequiredKeyedService<IGrainStorage>(StorageProviderName);
    }

    /// <summary>
    /// Stops and disposes the test cluster.
    /// </summary>
    /// <returns>A task which represents the asynchronous disposal operation.</returns>
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
