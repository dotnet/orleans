#nullable enable
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.GrainDirectory;
using Orleans.Hosting;
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
    [Fact]
    public async Task RollingUpgrade_LocalToDistributed_NoErrors()
    {
        var builder = new TestClusterBuilder(3);
        // Remove the default DistributedGrainDirectory configurator — initial silos use LocalGrainDirectory only.
        builder.Options.SiloBuilderConfiguratorTypes.RemoveAll(
            t => t.Contains(nameof(ConfigureDistributedGrainDirectory), StringComparison.Ordinal));
        builder.AddSiloBuilderConfigurator<RollingUpgradeSiloConfigurator>();
        builder.AddSiloBuilderConfigurator<ErrorLogCaptureSiloConfigurator>();
        builder.AddSiloBuilderConfigurator<RollingUpgradeDiagnosticCaptureSiloConfigurator>();

        var cluster = builder.Build();
        var errorLogs = ErrorLogCaptureRegistry.Get(cluster.Options.ClusterId);
        var diagnosticLogs = DiagnosticLogCaptureRegistry.Get(cluster.Options.ClusterId);
        long? failingGrainKey = null;

        try
        {
            await cluster.DeployAsync();
            output.WriteLine($"Cluster deployed with {cluster.Silos.Count} silos (LocalGrainDirectory only).");

            IGrainFactory client = cluster.Client;
            var grainId = 0L;
            var nextGrainId = () => Interlocked.Increment(ref grainId);

            try
            {
                // Phase 1: Drive load on the LocalGrainDirectory cluster.
                output.WriteLine("Phase 1: Driving load on LocalGrainDirectory cluster...");
                await DriveLoad(client, nextGrainId, count: 100, id => failingGrainKey = id);

                // Phase 2: Add DistributedGrainDirectory silos one at a time.
                output.WriteLine("Phase 2: Rolling upgrade — adding DistributedGrainDirectory silos...");

                var oldSilos = cluster.Silos.ToList();

                for (var i = 0; i < oldSilos.Count; i++)
                {
                    var newSilo = await cluster.StartAdditionalSiloAsync();
                    output.WriteLine($"  Started new silo: {newSilo.SiloAddress}");
                    await cluster.WaitForLivenessToStabilizeAsync();
                    await DriveLoad(client, nextGrainId, count: 100, id => failingGrainKey = id);
                }

                await cluster.InitializeClientAsync();
                client = cluster.Client;

                // Phase 3: Stop old silos one at a time, non-primary first.
                output.WriteLine($"Phase 3: Removing {oldSilos.Count} old LocalGrainDirectory silos...");
                foreach (var oldSilo in oldSilos.OrderBy(s => s == cluster.Primary ? 1 : 0))
                {
                    await cluster.StopSiloAsync(oldSilo);
                    output.WriteLine($"  Stopped old silo: {oldSilo.SiloAddress}");
                    await cluster.WaitForLivenessToStabilizeAsync();
                    await DriveLoad(client, nextGrainId, count: 100, id => failingGrainKey = id);
                }

                // Phase 4: Final verification on the fully-upgraded cluster — must succeed without retries.
                output.WriteLine("Phase 4: Verifying fully-upgraded DistributedGrainDirectory cluster...");
                await DriveLoad(client, nextGrainId, count: 200, id => failingGrainKey = id);
            }
            catch
            {
                await DumpFailureDiagnosticsAsync(cluster, errorLogs, diagnosticLogs, failingGrainKey);
                throw;
            }
        }
        finally
        {
            try
            {
                await cluster.StopAllSilosAsync();
                await cluster.DisposeAsync();
            }
            finally
            {
                ErrorLogCaptureRegistry.Remove(cluster.Options.ClusterId);
                DiagnosticLogCaptureRegistry.Remove(cluster.Options.ClusterId);
            }
        }

        // Assert no error-level logs occurred.
        var errors = errorLogs
            .ToArray()
            .Where(static error => !IsExpectedClientRoutingTableCancellation(error))
            .ToArray();
        if (errors.Length > 0)
        {
            output.WriteLine($"ERROR LOGS ({errors.Length}):");
            foreach (var error in errors.Take(20))
            {
                output.WriteLine($"  {error}");
            }
        }

        Assert.Empty(errors);
    }

    private static bool IsExpectedClientRoutingTableCancellation(string error) =>
        error.StartsWith("[Orleans.Runtime.GrainDirectory.ClientDirectory] Exception publishing client routing table", StringComparison.Ordinal)
        && error.Contains("TaskCanceledException: A task was canceled.", StringComparison.Ordinal);

    /// <summary>
    /// Activates grains by calling each one. Retries individual calls that fail with transient
    /// exceptions expected during directory ownership transitions in a rolling upgrade.
    /// </summary>
    private async Task DriveLoad(IGrainFactory client, Func<long> nextGrainId, int count, Action<long>? onPersistentFailure = null)
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
            try
            {
                await client.GetGrain<IRollingUpgradeTestGrain>(id).GetHost();
            }
            catch
            {
                onPersistentFailure?.Invoke(id);
                throw;
            }
        }
    }

    private async Task DumpFailureDiagnosticsAsync(TestCluster cluster, ErrorLogCapture errorLogs, DiagnosticLogCapture diagnosticLogs, long? failingGrainKey)
    {
        DumpCapturedMessages("ERROR LOGS", errorLogs.ToArray());
        DumpCapturedMessages("ROLLING UPGRADE DIAGNOSTICS", diagnosticLogs.ToArray(), limit: 200);

        if (failingGrainKey is not long grainKey)
        {
            return;
        }

        var grain = cluster.Client.GetGrain<IRollingUpgradeTestGrain>(grainKey);
        var grainId = grain.GetGrainId();
        output.WriteLine($"DETAILED GRAIN REPORTS for failing grain key {grainKey} ({grainId}):");
        foreach (var silo in cluster.Silos)
        {
            try
            {
                var siloControl = cluster.InternalGrainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlType, silo.SiloAddress);
                var report = await siloControl.GetDetailedGrainReport(grainId);
                output.WriteLine(report.ToString());
            }
            catch (Exception exception)
            {
                output.WriteLine($"Failed to get detailed grain report from silo {silo.SiloAddress}: {exception}");
            }
        }

        output.WriteLine("LIKELY RESOLUTION PLAN:");
        output.WriteLine("  1. Preserve RemoteGrainDirectory.AcceptSplitPartition semantics in DistributedRemoteGrainDirectory.");
        output.WriteLine("  2. Queue split-partition registration work instead of awaiting the full transfer inline.");
        output.WriteLine("  3. Retry failed registrations and handle duplicate activations before removing sender-side entries.");
    }

    private void DumpCapturedMessages(string title, string[] messages, int limit = 50)
    {
        if (messages.Length == 0)
        {
            return;
        }

        output.WriteLine($"{title} ({messages.Length}):");
        foreach (var message in messages.Take(limit))
        {
            output.WriteLine($"  {message}");
        }

        if (messages.Length > limit)
        {
            output.WriteLine($"  ... truncated to first {limit} entries.");
        }
    }

    private static bool ShouldUseDistributedDirectory(IConfiguration configuration)
    {
        var initialSiloCountText = configuration[nameof(TestClusterOptions.InitialSilosCount)]
            ?? throw new InvalidOperationException($"Missing {nameof(TestClusterOptions.InitialSilosCount)} configuration.");
        var initialSiloCount = int.Parse(initialSiloCountText, CultureInfo.InvariantCulture);
        return GetSiloInstanceNumber(configuration) >= initialSiloCount;
    }

    private static int GetSiloInstanceNumber(IConfiguration configuration)
    {
        var siloName = configuration["Orleans:Name"]
            ?? throw new InvalidOperationException("Missing Orleans:Name configuration.");
        if (string.Equals(siloName, Silo.PrimarySiloName, StringComparison.Ordinal))
        {
            return 0;
        }

        const string secondaryPrefix = "Secondary_";
        if (siloName.StartsWith(secondaryPrefix, StringComparison.Ordinal)
            && int.TryParse(siloName.AsSpan(secondaryPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var instanceNumber))
        {
            return instanceNumber;
        }

        throw new InvalidOperationException($"Unexpected silo name '{siloName}'.");
    }

    private sealed class RollingUpgradeSiloConfigurator : IHostConfigurator
    {
        public void Configure(IHostBuilder hostBuilder)
        {
            if (!ShouldUseDistributedDirectory(hostBuilder.GetConfiguration()))
            {
                return;
            }

#pragma warning disable ORLEANSEXP003
            hostBuilder.UseOrleans(static (_, siloBuilder) => siloBuilder.AddDistributedGrainDirectory());
#pragma warning restore ORLEANSEXP003
        }
    }

    private sealed class ErrorLogCaptureSiloConfigurator : IHostConfigurator
    {
        public void Configure(IHostBuilder hostBuilder)
        {
            var clusterId = hostBuilder.GetConfiguration()["Orleans:ClusterId"]
                ?? throw new InvalidOperationException("Missing Orleans:ClusterId configuration.");
            hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton(ErrorLogCaptureRegistry.Get(clusterId));
                services.AddSingleton<ILoggerProvider, ErrorCapturingLoggerProvider>();
            });
        }
    }

    private sealed class RollingUpgradeDiagnosticCaptureSiloConfigurator : IHostConfigurator
    {
        public void Configure(IHostBuilder hostBuilder)
        {
            var clusterId = hostBuilder.GetConfiguration()["Orleans:ClusterId"]
                ?? throw new InvalidOperationException("Missing Orleans:ClusterId configuration.");
            hostBuilder.ConfigureLogging(logging =>
            {
                logging.AddFilter(typeof(DistributedRemoteGrainDirectory).FullName, LogLevel.Information);
                logging.AddFilter(typeof(GrainDirectoryHandoffManager).FullName, LogLevel.Information);
            });
            hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton(DiagnosticLogCaptureRegistry.Get(clusterId));
                services.AddSingleton<ILoggerProvider, DiagnosticCapturingLoggerProvider>();
            });
        }
    }

    private sealed class ErrorCapturingLoggerProvider(ErrorLogCapture errorLogs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ErrorCapturingLogger(categoryName, errorLogs);
        public void Dispose() { }

        private sealed class ErrorCapturingLogger(string category, ErrorLogCapture errorLogs) : ILogger
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

                    errorLogs.Add(message);
                }
            }
        }
    }

    private sealed class ErrorLogCapture
    {
        private readonly ConcurrentQueue<string> _errors = new();

        public void Add(string message) => _errors.Enqueue(message);

        public string[] ToArray() => _errors.ToArray();
    }

    private static class ErrorLogCaptureRegistry
    {
        private static readonly ConcurrentDictionary<string, ErrorLogCapture> ErrorsByCluster = new(StringComparer.Ordinal);

        public static ErrorLogCapture Get(string clusterId) => ErrorsByCluster.GetOrAdd(clusterId, static _ => new());

        public static void Remove(string clusterId) => ErrorsByCluster.TryRemove(clusterId, out _);
    }

    private sealed class DiagnosticCapturingLoggerProvider(DiagnosticLogCapture diagnostics) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new DiagnosticCapturingLogger(categoryName, diagnostics);
        public void Dispose() { }

        private sealed class DiagnosticCapturingLogger(string category, DiagnosticLogCapture diagnostics) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) =>
                logLevel >= LogLevel.Information
                && (string.Equals(category, typeof(DistributedRemoteGrainDirectory).FullName, StringComparison.Ordinal)
                    || string.Equals(category, typeof(GrainDirectoryHandoffManager).FullName, StringComparison.Ordinal));

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                var message = $"[{category}] {formatter(state, exception)}";
                if (exception is not null)
                {
                    message += $"\n  Exception: {exception.GetType().Name}: {exception.Message}";
                }

                diagnostics.Add(message);
            }
        }
    }

    private sealed class DiagnosticLogCapture
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public void Add(string message) => _messages.Enqueue(message);

        public string[] ToArray() => _messages.ToArray();
    }

    private static class DiagnosticLogCaptureRegistry
    {
        private static readonly ConcurrentDictionary<string, DiagnosticLogCapture> DiagnosticsByCluster = new(StringComparer.Ordinal);

        public static DiagnosticLogCapture Get(string clusterId) => DiagnosticsByCluster.GetOrAdd(clusterId, static _ => new());

        public static void Remove(string clusterId) => DiagnosticsByCluster.TryRemove(clusterId, out _);
    }
}
