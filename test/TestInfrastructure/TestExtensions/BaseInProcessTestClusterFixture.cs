using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.TestingHost;

namespace TestExtensions;

public abstract class BaseInProcessTestClusterFixture : Xunit.IAsyncLifetime
{
    private readonly ExceptionDispatchInfo preconditionsException;

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

    public InProcessTestCluster HostedCluster { get; private set; }

    public IGrainFactory GrainFactory => Client;

    public IClusterClient Client => HostedCluster?.Client;

    public ILogger Logger { get; private set; }

    public string GetClientServiceId() => Client.ServiceProvider.GetRequiredService<IOptions<ClusterOptions>>().Value.ServiceId;

    public virtual async Task InitializeAsync()
    {
        EnsurePreconditionsMet();
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
        ConfigureTestCluster(builder);

        var testCluster = builder.Build();
        await testCluster.DeployAsync().ConfigureAwait(false);

        HostedCluster = testCluster;
        Logger = Client.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Application");
    }

    public virtual async Task DisposeAsync()
    {
        var cluster = HostedCluster;
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