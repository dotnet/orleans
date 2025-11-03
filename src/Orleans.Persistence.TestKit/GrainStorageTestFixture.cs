using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Storage;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Persistence.TestKit;

/// <summary>
/// Base fixture class for testing IGrainStorage implementations using InProcessTestCluster.
/// </summary>
/// <remarks>
/// This fixture provides an in-process Orleans cluster for testing storage providers.
/// Implement the abstract methods to configure your storage provider and retrieve it for testing.
/// </remarks>
public abstract class GrainStorageTestFixture : IAsyncLifetime
{
    /// <summary>
    /// Gets the in-process test cluster.
    /// </summary>
    protected InProcessTestCluster Cluster { get; private set; }

    /// <summary>
    /// Gets the grain factory for creating grain references.
    /// </summary>
    public IGrainFactory GrainFactory => Cluster?.Client;

    /// <summary>
    /// Gets the storage provider being tested.
    /// </summary>
    public IGrainStorage Storage { get; private set; }

    /// <summary>
    /// Gets the name of the storage provider being tested.
    /// </summary>
    protected abstract string StorageProviderName { get; }

    /// <summary>
    /// Checks preconditions before initializing the cluster.
    /// Override this to check for external dependencies (e.g., Azure Storage emulator).
    /// </summary>
    /// <exception cref="SkipException">Thrown if preconditions are not met.</exception>
    protected virtual void CheckPreconditionsOrThrow()
    {
    }

    /// <summary>
    /// Ensures that preconditions are met. Call this from test constructors to skip tests if preconditions fail.
    /// </summary>
    public void EnsurePreconditionsMet()
    {
        CheckPreconditionsOrThrow();
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
        // Default implementation does nothing
    }

    /// <inheritdoc/>
    public virtual async Task InitializeAsync()
    {
        CheckPreconditionsOrThrow();

        var builder = new InProcessTestClusterBuilder();
        
        builder.ConfigureSilo((siloBuilder) =>
        {
            ConfigureSilo(siloBuilder);
        });

        ConfigureTestCluster(builder);

        Cluster = builder.Build();
        await Cluster.DeployAsync().ConfigureAwait(false);

        // Retrieve the storage provider from the first silo
        var silo = Cluster.Silos.First();
        Storage = silo.Services.GetKeyedService<IGrainStorage>(StorageProviderName);
        
        if (Storage is null)
        {
            throw new InvalidOperationException(
                $"Storage provider '{StorageProviderName}' not found. " +
                $"Ensure ConfigureSilo adds a storage provider with this name.");
        }
    }

    /// <inheritdoc/>
    public virtual async Task DisposeAsync()
    {
        if (Cluster is not null)
        {
            await Cluster.StopAllSilosAsync().ConfigureAwait(false);
            await Cluster.DisposeAsync().ConfigureAwait(false);
        }
    }
}
