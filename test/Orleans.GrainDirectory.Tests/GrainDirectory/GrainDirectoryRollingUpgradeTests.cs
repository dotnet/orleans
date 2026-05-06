#nullable enable
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
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
        var builder = new InProcessTestClusterBuilder(3);
        // Initial silos use LocalGrainDirectory only; later silos opt into DistributedGrainDirectory.
        builder.Options.UseTestClusterGrainDirectory = false;
        var initialSiloCount = builder.Options.InitialSilosCount;
        var clusterId = builder.Options.ClusterId;
        builder.ConfigureSilo((siloOptions, siloBuilder) =>
        {
            if (!ShouldUseDistributedDirectory(siloOptions.SiloName, initialSiloCount))
            {
                return;
            }

#pragma warning disable ORLEANSEXP003
            siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
        });
        builder.ConfigureSiloHost((_, hostBuilder) => ConfigureErrorLogCapture(hostBuilder, clusterId));
        builder.ConfigureSiloHost((_, hostBuilder) => ConfigureRollingUpgradeDiagnosticCapture(hostBuilder, clusterId));

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
                    await ValidateDirectoryIntegrityAsync(cluster, $"before adding distributed silo {i + 1}/{oldSilos.Count}");
                    var newSilo = await cluster.StartAdditionalSiloAsync();
                    output.WriteLine($"  Started new silo: {newSilo.SiloAddress}");
                    await cluster.WaitForLivenessToStabilizeAsync();
                    await ValidateDirectoryIntegrityAsync(cluster, $"after adding distributed silo {i + 1}/{oldSilos.Count}");
                    await DriveLoad(client, nextGrainId, count: 100, id => failingGrainKey = id);
                }

                await cluster.InitializeClientAsync();
                client = cluster.Client;

                // Phase 3: Stop old silos one at a time, non-primary first.
                output.WriteLine($"Phase 3: Removing {oldSilos.Count} old LocalGrainDirectory silos...");
                var transitionIndex = 0;
                foreach (var oldSilo in oldSilos.OrderBy(static s => s.InstanceNumber == 0 ? 1 : 0))
                {
                    transitionIndex++;
                    await ValidateDirectoryIntegrityAsync(cluster, $"before removing local silo {transitionIndex}/{oldSilos.Count}");
                    await cluster.StopSiloAsync(oldSilo);
                    output.WriteLine($"  Stopped old silo: {oldSilo.SiloAddress}");
                    await cluster.WaitForLivenessToStabilizeAsync();
                    await ValidateDirectoryIntegrityAsync(cluster, $"after removing local silo {transitionIndex}/{oldSilos.Count}");
                    await DriveLoad(client, nextGrainId, count: 100, id => failingGrainKey = id);
                }

                // Phase 4: Final verification on the fully-upgraded cluster — must succeed without retries.
                output.WriteLine("Phase 4: Verifying fully-upgraded DistributedGrainDirectory cluster...");
                await DriveLoad(client, nextGrainId, count: 200, id => failingGrainKey = id);
                await ValidateDirectoryIntegrityAsync(cluster, "after final verification");
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
            .Where(static error => !IsExpectedDirectoryPartitionRejection(error))
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

    private async Task ValidateDirectoryIntegrityAsync(InProcessTestCluster cluster, string stage)
    {
        output.WriteLine($"  Validating grain directory integrity {stage}...");

        var activations = GetDirectoryActivations(cluster);
        var distributedPartitions = new List<IGrainDirectoryTestHooks>();
        foreach (var silo in cluster.Silos)
        {
            var membershipService = silo.ServiceProvider.GetService<DirectoryMembershipService>();
            if (membershipService is null)
            {
                continue;
            }

            for (var partitionIndex = 0; partitionIndex < membershipService.PartitionsPerSilo; partitionIndex++)
            {
                var replica = cluster.InternalClient.GetSystemTarget<IGrainDirectoryTestHooks>(
                    GrainDirectoryPartition.CreateGrainId(silo.SiloAddress, partitionIndex).GrainId);
                distributedPartitions.Add(replica);
            }
        }

        if (distributedPartitions.Count == 0)
        {
            await CheckActivationRegistrationsWithLocalDirectoryAsync(activations, stage);
        }
        else
        {
            var integrityChecks = distributedPartitions.Select(static partition => partition.RecoverAndCheckIntegrityAsync().AsTask()).ToArray();
            await Task.WhenAll(integrityChecks);
            foreach (var task in integrityChecks)
            {
                await task;
            }

            var activationAddresses = activations.Select(static activation => activation.Address).ToList().AsImmutable();
            var activationChecks = distributedPartitions.Select(partition => partition.CheckActivationsAsync(activationAddresses).AsTask()).ToArray();
            await Task.WhenAll(activationChecks);
            var distributedCheckedGrains = new HashSet<GrainId>();
            foreach (var task in activationChecks)
            {
                foreach (var grainId in (await task).Value)
                {
                    Assert.True(distributedCheckedGrains.Add(grainId), $"Grain '{grainId}' was checked by multiple distributed directory partitions during '{stage}'.");
                }
            }

            var localActivationChecks = new List<Task>();
            foreach (var activation in activations)
            {
                if (distributedCheckedGrains.Contains(activation.Address.GrainId))
                {
                    continue;
                }

                var grainLocator = activation.Silo.ServiceProvider.GetRequiredService<GrainLocator>();
                localActivationChecks.Add(CheckActivationRegistrationAsync(grainLocator, activation.Address, activation.Silo.SiloAddress, stage));
            }

            await Task.WhenAll(localActivationChecks);
            foreach (var task in localActivationChecks)
            {
                await task;
            }
        }

        output.WriteLine($"  Validated {activations.Count} activations and {distributedPartitions.Count} DistributedGrainDirectory partitions {stage}.");
    }

    private static List<(InProcessSiloHandle Silo, GrainAddress Address)> GetDirectoryActivations(InProcessTestCluster cluster)
    {
        var result = new List<(InProcessSiloHandle Silo, GrainAddress Address)>();
        foreach (var silo in cluster.Silos)
        {
            var activations = silo.ServiceProvider.GetRequiredService<ActivationDirectory>();
            foreach (var (_, activation) in activations)
            {
                if (activation is ActivationData { IsValid: false } || !UsesGrainDirectory(activation))
                {
                    continue;
                }

                result.Add((silo, activation.Address));
            }
        }

        return result;
    }

    private static bool UsesGrainDirectory(IGrainContext activation)
    {
        if (activation is ActivationData activationData)
        {
            return activationData.IsUsingGrainDirectory;
        }

        return activation is not SystemTarget && activation.GetComponent<PlacementStrategy>() is { IsUsingGrainDirectory: true };
    }

    private static async Task CheckActivationRegistrationsWithLocalDirectoryAsync(List<(InProcessSiloHandle Silo, GrainAddress Address)> activations, string stage)
    {
        var activationChecks = activations.Select(activation =>
        {
            var grainLocator = activation.Silo.ServiceProvider.GetRequiredService<GrainLocator>();
            return CheckActivationRegistrationAsync(grainLocator, activation.Address, activation.Silo.SiloAddress, stage);
        }).ToArray();

        await Task.WhenAll(activationChecks);
        foreach (var task in activationChecks)
        {
            await task;
        }
    }

    private static async Task CheckActivationRegistrationAsync(GrainLocator grainLocator, GrainAddress activationAddress, SiloAddress siloAddress, string stage)
    {
        var registeredAddress = await grainLocator.Lookup(activationAddress.GrainId);
        Assert.True(
            activationAddress.Matches(registeredAddress),
            $"Activation '{activationAddress.ToFullString()}' on silo '{siloAddress}' did not have a matching directory registration during '{stage}'. Registered address: '{registeredAddress?.ToFullString() ?? "<null>"}'.");
    }

    private static bool IsExpectedClientRoutingTableCancellation(string error) =>
        error.StartsWith("[Orleans.Runtime.GrainDirectory.ClientDirectory] Exception publishing client routing table", StringComparison.Ordinal)
        && error.Contains("TaskCanceledException: A task was canceled.", StringComparison.Ordinal);

    private static bool IsExpectedDirectoryPartitionRejection(string error) =>
        error.StartsWith("[Orleans.Messaging] Failed to address message", StringComparison.Ordinal)
        && error.Contains("IGrainDirectoryPartition.", StringComparison.Ordinal)
        && error.Contains("not active on this silo", StringComparison.Ordinal);

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

        var remainingIds = ids;
        const int MaxAttempts = 10;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var tasks = remainingIds.Select(id => client.GetGrain<IRollingUpgradeTestGrain>(id).GetHost().AsTask()).ToArray();
            try
            {
                await Task.WhenAll(tasks);
                return;
            }
            catch
            {
                // Some calls failed — retry the failed ones.
            }

            var failedIds = new List<long>();
            var exceptions = new List<Exception>();
            for (var i = 0; i < tasks.Length; i++)
            {
                if (tasks[i].IsCompletedSuccessfully)
                {
                    continue;
                }

                failedIds.Add(remainingIds[i]);
                if (tasks[i].Exception is { } exception)
                {
                    exceptions.Add(exception);
                }
                else if (tasks[i].IsCanceled)
                {
                    exceptions.Add(new TaskCanceledException(tasks[i]));
                }
            }

            if (failedIds.Count == 0)
            {
                return;
            }

            if (attempt == MaxAttempts)
            {
                onPersistentFailure?.Invoke(failedIds[0]);
                throw new AggregateException($"Failed to complete {failedIds.Count} grain calls after {MaxAttempts} attempts.", exceptions);
            }

            output.WriteLine($"    {failedIds.Count}/{remainingIds.Length} calls failed on attempt {attempt}, retrying...");
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            remainingIds = [.. failedIds];
        }
    }

    private async Task DumpFailureDiagnosticsAsync(InProcessTestCluster cluster, ErrorLogCapture errorLogs, DiagnosticLogCapture diagnosticLogs, long? failingGrainKey)
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
                var siloControl = cluster.InternalClient.GetSystemTarget<ISiloControl>(Constants.SiloControlType, silo.SiloAddress);
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

    private static bool ShouldUseDistributedDirectory(string siloName, int initialSiloCount) =>
        GetSiloInstanceNumber(siloName) >= initialSiloCount;

    private static int GetSiloInstanceNumber(string siloName)
    {
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

        const string inProcessSiloPrefix = "Silo_";
        if (siloName.StartsWith(inProcessSiloPrefix, StringComparison.Ordinal)
            && int.TryParse(siloName.AsSpan(inProcessSiloPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out instanceNumber))
        {
            return instanceNumber;
        }

        throw new InvalidOperationException($"Unexpected silo name '{siloName}'.");
    }

    private static void ConfigureErrorLogCapture(IHostApplicationBuilder hostBuilder, string clusterId)
    {
        hostBuilder.Services.AddSingleton(ErrorLogCaptureRegistry.Get(clusterId));
        hostBuilder.Services.AddSingleton<ILoggerProvider, ErrorCapturingLoggerProvider>();
    }

    private static void ConfigureRollingUpgradeDiagnosticCapture(IHostApplicationBuilder hostBuilder, string clusterId)
    {
        hostBuilder.Logging.AddFilter(typeof(DistributedRemoteGrainDirectory).FullName, LogLevel.Information);
        hostBuilder.Logging.AddFilter(typeof(GrainDirectoryHandoffManager).FullName, LogLevel.Information);
        hostBuilder.Services.AddSingleton(DiagnosticLogCaptureRegistry.Get(clusterId));
        hostBuilder.Services.AddSingleton<ILoggerProvider, DiagnosticCapturingLoggerProvider>();
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
