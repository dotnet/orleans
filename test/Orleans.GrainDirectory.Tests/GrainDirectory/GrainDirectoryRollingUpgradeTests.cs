#nullable enable
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
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
/// while removing old silos, all under continuous load.
/// </summary>
[TestCategory("Directory"), TestCategory("Functional")]
public sealed class GrainDirectoryRollingUpgradeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RollingUpgrade_LocalToDistributed_NoErrors()
    {
        var useDistributedDirectory = false;
        var errorLogs = new ConcurrentBag<string>();
        var logProvider = new ErrorCapturingLoggerProvider(errorLogs);

        var builder = new InProcessTestClusterBuilder(3);
        builder.Options.UseTestClusterMembership = true;
        builder.Options.UseTestClusterGrainDirectory = false;
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.Configure<SiloMessagingOptions>(o =>
            {
                o.ResponseTimeout = TimeSpan.FromMinutes(2);
                o.SystemResponseTimeout = TimeSpan.FromMinutes(2);
            });

            if (useDistributedDirectory)
            {
#pragma warning disable ORLEANSEXP003
                siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
            }
        });

        builder.ConfigureSiloHost((_, hostBuilder) =>
        {
            hostBuilder.Services.AddSingleton<ILoggerProvider>(logProvider);
        });

        var cluster = builder.Build();
        await cluster.DeployAsync();
        output.WriteLine($"Cluster deployed with {cluster.Silos.Count} silos (LocalGrainDirectory only).");

        var client = cluster.Client;

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var idBase = 0L;
        Func<long> getNextIdBase = () => Interlocked.Add(ref idBase, 50);

        // Drive load continuously in the background throughout the test.
        var loadTask = DriveLoad(client, getNextIdBase, cts.Token);

        // Phase 1: Quick health check on the LocalGrainDirectory cluster.
        output.WriteLine("Phase 1: Verifying LocalGrainDirectory cluster is healthy...");
        await VerifyClusterHealthy(client, getNextIdBase, cts.Token);

        // Phase 2: Enable DistributedGrainDirectory for new silos and start them.
        output.WriteLine("Phase 2: Rolling upgrade — adding DistributedGrainDirectory silos...");
        useDistributedDirectory = true;

        var oldSilos = cluster.Silos.ToList();
        var newSilos = new List<InProcessSiloHandle>();

        for (var i = 0; i < oldSilos.Count; i++)
        {
            var newSilo = await cluster.StartAdditionalSiloAsync();
            newSilos.Add(newSilo);
            output.WriteLine($"  Started new silo: {newSilo.SiloAddress}");
            await cluster.WaitForLivenessToStabilizeAsync();
        }

        // Phase 3: Stop old silos one at a time, primary last.
        output.WriteLine($"Phase 3: Removing {oldSilos.Count} old LocalGrainDirectory silos...");
        foreach (var oldSilo in oldSilos.OrderBy(s => s == cluster.Silos[0] ? 1 : 0))
        {
            await cluster.StopSiloAsync(oldSilo);
            output.WriteLine($"  Stopped old silo: {oldSilo.SiloAddress}");
            await cluster.WaitForLivenessToStabilizeAsync();
        }

        // Phase 4: Verify the fully-upgraded cluster works.
        output.WriteLine("Phase 4: Verifying fully-upgraded DistributedGrainDirectory cluster...");
        await VerifyClusterHealthy(client, getNextIdBase, cts.Token);

        // Stop load and clean up.
        cts.Cancel();
        try { await loadTask; }
        catch (OperationCanceledException) { }

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
    /// Verifies cluster health by completing a batch of grain calls successfully.
    /// Retries on transient errors to allow membership to settle.
    /// </summary>
    private static async Task VerifyClusterHealthy(IGrainFactory client, Func<long> getNextIdBase, CancellationToken ct)
    {
        const int BatchSize = 10;
        const int MaxAttempts = 60;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var batch = getNextIdBase();
                var tasks = Enumerable.Range(0, BatchSize)
                    .Select(i => client.GetGrain<IRollingUpgradeTestGrain>(batch + i).GetHost().AsTask())
                    .ToList();
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await Task.Delay(500, ct);
            }
        }

        throw new TimeoutException($"Cluster did not become healthy after {MaxAttempts} attempts.");
    }

    private static async Task DriveLoad(IGrainFactory client, Func<long> getNextIdBase, CancellationToken ct)
    {
        const int CallsPerIteration = 50;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var idBase = getNextIdBase();
                var tasks = Enumerable.Range(0, CallsPerIteration)
                    .Select(i => client.GetGrain<IRollingUpgradeTestGrain>(idBase + i).GetHost().AsTask())
                    .ToList();

                await Task.WhenAll(tasks).WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SiloUnavailableException)
            {
                // Expected during membership changes.
            }
            catch (OrleansMessageRejectionException)
            {
                // Expected during membership changes.
            }
            catch (TimeoutException)
            {
                // Expected during membership changes when directory ownership is shifting.
            }
        }
    }

    /// <summary>
    /// Logger provider that captures Error-level log messages.
    /// </summary>
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
                    // SiloUnavailableException errors in the messaging layer are expected
                    // when silos are stopped under load during a rolling upgrade.
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
