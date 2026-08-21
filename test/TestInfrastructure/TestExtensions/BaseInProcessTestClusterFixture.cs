using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.TestingHost;

namespace TestExtensions;

public abstract class BaseInProcessTestClusterFixture : Xunit.IAsyncLifetime
{
    private readonly ExceptionDispatchInfo? preconditionsException;
    private InProcessTestCluster? hostedCluster;

    protected bool PreconditionsMet => preconditionsException is null;

    static BaseInProcessTestClusterFixture()
    {
        TestDefaultConfiguration.InitializeDefaults();
    }

    protected BaseInProcessTestClusterFixture()
    {
        try
        {
            CheckPreconditionsOrThrow();
        }
        catch (Exception ex)
        {
            preconditionsException = ExceptionDispatchInfo.Capture(ex);
            return;
        }
    }

    public void EnsurePreconditionsMet()
    {
        preconditionsException?.Throw();
    }

    protected virtual void CheckPreconditionsOrThrow() { }

    protected virtual void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
    }

    public InProcessTestCluster HostedCluster
    {
        get
        {
            EnsurePreconditionsMet();
            return hostedCluster ?? throw new InvalidOperationException("The test cluster has not been initialized.");
        }
        private set => hostedCluster = value;
    }

    public IGrainFactory GrainFactory
    {
        get
        {
            EnsurePreconditionsMet();
            return Client;
        }
    }

    public IClusterClient Client
    {
        get
        {
            EnsurePreconditionsMet();
            return HostedCluster.Client;
        }
    }

    public ILogger Logger { get; private set; } = null!;

    public string GetClientServiceId() => Client.ServiceProvider.GetRequiredService<IOptions<ClusterOptions>>().Value.ServiceId;

    public virtual async ValueTask InitializeAsync()
    {
        if (!PreconditionsMet)
        {
            return;
        }

        var builder = new InProcessTestClusterBuilder();
        builder.Options.UseDistributedGrainDirectory = true;
        builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
        ConfigureTestCluster(builder);

        var testCluster = builder.Build();
        await testCluster.DeployAsync().ConfigureAwait(false);

        HostedCluster = testCluster;
        Logger = Client.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Application");
    }

    public virtual async ValueTask DisposeAsync()
    {
        var cluster = hostedCluster;
        if (cluster is null) return;

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