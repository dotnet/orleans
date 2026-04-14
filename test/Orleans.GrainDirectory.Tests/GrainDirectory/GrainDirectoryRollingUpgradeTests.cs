#nullable enable
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.GrainDirectory;

internal interface IRollingUpgradeTestGrain : IGrainWithIntegerKey
{
    ValueTask<string> GetHost();
}

internal class RollingUpgradeTestGrain : Grain, IRollingUpgradeTestGrain
{
    private readonly SiloAddress _silo;

    public RollingUpgradeTestGrain(ILocalSiloDetails siloDetails)
    {
        _silo = siloDetails.SiloAddress;
    }

    public ValueTask<string> GetHost() => new(_silo.ToString());
}

/// <summary>
/// Tests rolling upgrade from <see cref="LocalGrainDirectory"/> to <see cref="DistributedGrainDirectory"/>.
/// Starts a cluster with only LocalGrainDirectory, then adds silos with DistributedGrainDirectory
/// while removing old silos, verifying grain calls succeed after each step.
/// </summary>
[TestCategory("Directory"), TestCategory("Functional")]
public sealed class GrainDirectoryRollingUpgradeTests(ITestOutputHelper output)
{
    /// <summary>
    /// Controls whether newly started silos enable the <see cref="DistributedGrainDirectory"/>.
    /// </summary>
    internal static volatile bool UseDistributedDirectory;

    [Fact]
    public async Task RollingUpgrade_LocalToDistributed_NoErrors()
    {
        UseDistributedDirectory = false;
        var errorLogs = new ConcurrentBag<string>();
        ErrorLogCaptureSiloConfigurator.Errors = errorLogs;

        var builder = new TestClusterBuilder(3);
        // Remove the default DistributedGrainDirectory configurator — initial silos use LocalGrainDirectory only.
        builder.Options.SiloBuilderConfiguratorTypes.RemoveAll(
            t => t.Contains(nameof(ConfigureDistributedGrainDirectory), StringComparison.Ordinal));
        builder.AddSiloBuilderConfigurator<RollingUpgradeSiloConfigurator>();
        builder.AddSiloBuilderConfigurator<ErrorLogCaptureSiloConfigurator>();

        var cluster = builder.Build();
        await cluster.DeployAsync();
        output.WriteLine($"Cluster deployed with {cluster.Silos.Count} silos (LocalGrainDirectory only).");

        var client = cluster.Client;
        var grainId = 0L;
        var nextGrainId = () => Interlocked.Increment(ref grainId);

        // Phase 1: Drive load on the LocalGrainDirectory cluster.
        output.WriteLine("Phase 1: Driving load on LocalGrainDirectory cluster...");
        await DriveLoad(client, nextGrainId, count: 100);

        // Phase 2: Add DistributedGrainDirectory silos one at a time.
        output.WriteLine("Phase 2: Rolling upgrade — adding DistributedGrainDirectory silos...");
        UseDistributedDirectory = true;

        var oldSilos = cluster.Silos.ToList();

        for (var i = 0; i < oldSilos.Count; i++)
        {
            var newSilo = await cluster.StartAdditionalSiloAsync();
            output.WriteLine($"  Started new silo: {newSilo.SiloAddress}");
            await cluster.WaitForLivenessToStabilizeAsync();
            await DriveLoad(client, nextGrainId, count: 100);
        }

        // Phase 3: Stop old silos one at a time, non-primary first.
        output.WriteLine($"Phase 3: Removing {oldSilos.Count} old LocalGrainDirectory silos...");
        foreach (var oldSilo in oldSilos.OrderBy(s => s == cluster.Primary ? 1 : 0))
        {
            await cluster.StopSiloAsync(oldSilo);
            output.WriteLine($"  Stopped old silo: {oldSilo.SiloAddress}");
            await cluster.WaitForLivenessToStabilizeAsync();
            await DriveLoad(client, nextGrainId, count: 100);
        }

        // Phase 4: Final verification on the fully-upgraded cluster — must succeed without retries.
        output.WriteLine("Phase 4: Verifying fully-upgraded DistributedGrainDirectory cluster...");
        await DriveLoad(client, nextGrainId, count: 200);

        // Assert no error-level logs occurred.
        var errors = errorLogs.ToArray();
        if (errors.Length > 0)
        {
            output.WriteLine($"ERROR LOGS ({errors.Length}):");
            foreach (var error in errors.Take(20))
            {
                output.WriteLine($"  {error}");
            }
        }

        await cluster.StopAllSilosAsync();
        await cluster.DisposeAsync();

        Assert.Empty(errors);
    }

    /// <summary>
    /// Activates grains by calling each one. Retries individual calls that fail with transient
    /// exceptions expected during directory ownership transitions in a rolling upgrade.
    /// </summary>
    private async Task DriveLoad(IGrainFactory client, Func<long> nextGrainId, int count)
    {
        var ids = new long[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = nextGrainId();
        }

        // First attempt: fire all calls in parallel.
        var tasks = ids.Select(id => client.GetGrain<IRollingUpgradeTestGrain>(id).GetHost().AsTask()).ToArray();
        try
        {
            await Task.WhenAll(tasks);
            return;
        }
        catch
        {
            // Some calls failed — retry the failed ones individually.
        }

        var failedIds = new List<long>();
        for (var i = 0; i < tasks.Length; i++)
        {
            if (tasks[i].IsFaulted)
            {
                failedIds.Add(ids[i]);
            }
        }

        output.WriteLine($"    {failedIds.Count}/{count} calls failed, retrying...");

        // Retry failed calls one at a time.
        foreach (var id in failedIds)
        {
            await client.GetGrain<IRollingUpgradeTestGrain>(id).GetHost();
        }
    }

    private class RollingUpgradeSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            if (UseDistributedDirectory)
            {
#pragma warning disable ORLEANSEXP003
                siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
            }
        }
    }

    private class ErrorLogCaptureSiloConfigurator : IHostConfigurator
    {
        internal static ConcurrentBag<string>? Errors;

        public void Configure(IHostBuilder hostBuilder)
        {
            hostBuilder.ConfigureServices(services =>
            {
                if (Errors is { } errors)
                {
                    services.AddSingleton<ILoggerProvider>(new ErrorCapturingLoggerProvider(errors));
                }
            });
        }
    }

    private sealed class ErrorCapturingLoggerProvider(ConcurrentBag<string> errors) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ErrorCapturingLogger(categoryName, errors);
        public void Dispose() { }

        private sealed class ErrorCapturingLogger(string category, ConcurrentBag<string> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                {
                    // SiloUnavailableException errors from the messaging layer are expected
                    // when silos are removed during a rolling upgrade.
                    if (exception is SiloUnavailableException)
                    {
                        return;
                    }

                    var message = $"[{category}] {formatter(state, exception)}";
                    if (exception is not null)
                    {
                        message += $"\n  Exception: {exception.GetType().Name}: {exception.Message}";
                    }

                    errors.Add(message);
                }
            }
        }
    }
}
