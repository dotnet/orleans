#nullable enable
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Configuration;
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
    private static readonly TimeSpan DirectoryConvergenceTimeout = TimeSpan.FromSeconds(30);

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
                    await WaitForDirectoryConvergenceAsync(cluster, $"after adding distributed silo {i + 1}/{oldSilos.Count}");
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
                    await WaitForDirectoryConvergenceAsync(cluster, $"after removing local silo {transitionIndex}/{oldSilos.Count}");
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

    private async Task WaitForDirectoryConvergenceAsync(InProcessTestCluster cluster, string stage)
    {
        var distributedSilos = new List<(InProcessSiloHandle Silo, DirectoryMembershipService MembershipService)>();
        foreach (var silo in cluster.Silos)
        {
            if (silo.ServiceProvider.GetService<DirectoryMembershipService>() is { } membershipService)
            {
                distributedSilos.Add((silo, membershipService));
            }
        }

        if (distributedSilos.Count == 0)
        {
            return;
        }

        output.WriteLine($"  Waiting for grain directory convergence {stage}...");
        var expectedMembers = cluster.Silos.Select(static silo => silo.SiloAddress).ToHashSet();
        var targetVersion = new MembershipVersion(cluster.Silos.Max(static silo =>
            silo.ServiceProvider.GetRequiredService<ClusterMembershipService>().CurrentSnapshot.Version.Value));

        using var timeout = new CancellationTokenSource(DirectoryConvergenceTimeout);
        try
        {
            var views = await Task.WhenAll(distributedSilos.Select(silo =>
                WaitForDirectoryViewAsync(silo.MembershipService, targetVersion, expectedMembers, stage, timeout.Token)));

            var partitionWaits = new List<Task>();
            for (var i = 0; i < distributedSilos.Count; i++)
            {
                var (silo, membershipService) = distributedSilos[i];
                var view = views[i];
                for (var partitionIndex = 0; partitionIndex < membershipService.PartitionsPerSilo; partitionIndex++)
                {
                    var replica = cluster.InternalClient.GetSystemTarget<IGrainDirectoryTestHooks>(
                        GrainDirectoryPartition.CreateGrainId(silo.SiloAddress, partitionIndex).GrainId);
                    partitionWaits.Add(replica.WaitForMembershipVersionAsync(view.Version).AsTask());
                }
            }

            await Task.WhenAll(partitionWaits).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for grain directory convergence {stage} after {DirectoryConvergenceTimeout}.");
        }
    }

    private static async Task<DirectoryMembershipSnapshot> WaitForDirectoryViewAsync(
        DirectoryMembershipService membershipService,
        MembershipVersion targetVersion,
        HashSet<SiloAddress> expectedMembers,
        string stage,
        CancellationToken cancellationToken)
    {
        if (IsExpectedDirectoryView(membershipService.CurrentView, targetVersion, expectedMembers))
        {
            return membershipService.CurrentView;
        }

        var refreshedView = await membershipService.RefreshViewAsync(targetVersion, cancellationToken);
        if (IsExpectedDirectoryView(refreshedView, targetVersion, expectedMembers))
        {
            return refreshedView;
        }

        await foreach (var view in membershipService.ViewUpdates.WithCancellation(cancellationToken))
        {
            if (IsExpectedDirectoryView(view, targetVersion, expectedMembers))
            {
                return view;
            }
        }

        throw new TimeoutException($"Timed out waiting for grain directory membership view {stage}.");
    }

    private static bool IsExpectedDirectoryView(DirectoryMembershipSnapshot view, MembershipVersion targetVersion, HashSet<SiloAddress> expectedMembers)
    {
        if (view.Version < targetVersion || view.Members.Length != expectedMembers.Count)
        {
            return false;
        }

        foreach (var member in view.Members)
        {
            if (!expectedMembers.Contains(member))
            {
                return false;
            }
        }

        return true;
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

    [Fact]
    public async Task RollingUpgrade_RestartInPlace_PreservesTrafficAndDirectoryIntegrity()
    {
        const int SiloCount = 3;
        var operationTimeout = TimeSpan.FromSeconds(45);
        var cleanupTimeout = TimeSpan.FromSeconds(30);
        var upgradedSiloNames = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var phase = new RollingUpgradePhase("deploy-local", output);
        var logs = new PhaseAwareLogCapture(phase);
        var staleCacheEvidence = new StaleCacheEvidenceCapture();
        var retiredAddresses = new HashSet<SiloAddress>();
        var trackedGrainKeys = Enumerable.Range(1, 12).Select(static value => -(long)value).ToList();
        var restartPhases = new List<string>(SiloCount);
        var verificationPhases = new List<string>(SiloCount);
        var nextVerificationGrainKey = 2_000_000L;

        var builder = new InProcessTestClusterBuilder(SiloCount);
        builder.Options.UseTestClusterGrainDirectory = false;
        builder.ConfigureSilo((siloOptions, siloBuilder) =>
        {
            if (!upgradedSiloNames.ContainsKey(siloOptions.SiloName))
            {
                return;
            }

#pragma warning disable ORLEANSEXP003
            siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
        });
        builder.ConfigureSiloHost((siloOptions, hostBuilder) =>
        {
            hostBuilder.Services.Configure<GrainDirectoryOptions>(
                static options => options.CachingStrategy = GrainDirectoryOptions.CachingStrategyType.Custom);
            hostBuilder.Services.AddSingleton<TrackingGrainDirectoryCache>();
            hostBuilder.Services.AddSingleton<IGrainDirectoryCache>(
                static services => services.GetRequiredService<TrackingGrainDirectoryCache>());
            hostBuilder.Logging.AddFilter(typeof(DistributedRemoteGrainDirectory).FullName, LogLevel.Information);
            hostBuilder.Logging.AddFilter(typeof(GrainDirectoryHandoffManager).FullName, LogLevel.Information);
            hostBuilder.Logging.AddFilter("Orleans.Messaging", LogLevel.Warning);
            hostBuilder.Services.AddSingleton<ILoggerProvider>(
                new PhaseAwareLoggerProvider(siloOptions.SiloName, logs));
        });

        var cluster = builder.Build();
        SustainedRollingUpgradeTraffic? traffic = null;
        var deployed = false;
        try
        {
            await cluster.DeployAsync().WaitAsync(operationTimeout);
            deployed = true;

            var originalClient = cluster.Client;
            Assert.Equal(SiloCount, cluster.Silos.Count);
            Assert.Equal(SiloCount, cluster.Silos.Select(static silo => silo.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.All(cluster.Silos, static silo => Assert.Null(silo.ServiceProvider.GetService<DirectoryMembershipService>()));
            Assert.Empty(upgradedSiloNames);

            output.WriteLine(
                $"Phase '{phase.Current}': {SiloCount} LocalGrainDirectory silos: "
                + string.Join(", ", cluster.Silos.Select(static silo => $"{silo.Name}={silo.SiloAddress}")));

            var initialObservations = await CallIdentityGrainsAsync(originalClient, trackedGrainKeys, operationTimeout);
            var repeatedInitialObservations = await CallIdentityGrainsAsync(originalClient, trackedGrainKeys, operationTimeout);
            AssertStableActivationProgress(initialObservations, repeatedInitialObservations, "initial LocalGrainDirectory phase");
            await ValidateTrackedDirectoryCheckpointAsync(
                cluster,
                repeatedInitialObservations,
                cluster.Silos.Select(static silo => silo.SiloAddress).ToHashSet(),
                retiredAddresses,
                "initial LocalGrainDirectory phase",
                staleCacheEvidence);

            traffic = new SustainedRollingUpgradeTraffic(
                originalClient,
                cluster,
                phase,
                trackedGrainKeys.ToArray(),
                expectedMaximumSilos: SiloCount,
                callTimeout: TimeSpan.FromSeconds(10));
            traffic.Start(workerCount: 3);
            var initialWorkerProgress = traffic.GetWorkerProgress();
            await traffic.WaitForPhaseSuccessesAsync(phase.Current, minimumSuccesses: 12, operationTimeout);
            await traffic.WaitForAllWorkersProgressAsync(
                initialWorkerProgress,
                "initial LocalGrainDirectory traffic",
                operationTimeout);
            AssertNoTrafficFailures(traffic, phase.Current);
            AssertTransientTrafficFailuresWithinLimit(traffic, 0, phase.Current);

            var restartOrder = cluster.Silos
                .OrderBy(static silo => silo.InstanceNumber == 0)
                .ThenBy(static silo => silo.InstanceNumber)
                .ToArray();
            Assert.Equal(SiloCount, restartOrder.Length);
            Assert.All(restartOrder[..^1], static silo => Assert.NotEqual(0, silo.InstanceNumber));
            Assert.Equal(0, restartOrder[^1].InstanceNumber);

            for (var restartIndex = 0; restartIndex < restartOrder.Length; restartIndex++)
            {
                var oldSilo = restartOrder[restartIndex];
                var restartPhase = $"restart-{restartIndex + 1}-{oldSilo.Name}";
                restartPhases.Add(restartPhase);
                phase.Set(restartPhase);
                Assert.True(
                    upgradedSiloNames.TryAdd(oldSilo.Name, 0),
                    $"Stable silo name '{oldSilo.Name}' was marked as upgraded more than once.");
                Assert.Equal(SiloCount, cluster.Silos.Count);
                Assert.Same(originalClient, cluster.Client);

                await traffic.WaitForPhaseSuccessesAsync(restartPhase, minimumSuccesses: 4, operationTimeout);
                var oldAddress = oldSilo.SiloAddress;
                output.WriteLine(
                    $"Phase '{restartPhase}': restarting {oldSilo.Name} ({oldAddress}) in place; "
                    + $"upgraded names=[{string.Join(", ", upgradedSiloNames.Keys.Order())}].");
                var cacheSentinelSilos = cluster.Silos
                    .Where(silo => !ReferenceEquals(silo, oldSilo))
                    .ToArray();
                var cacheClearBaselines = cacheSentinelSilos.ToDictionary(
                    static silo => silo,
                    static silo => silo.ServiceProvider.GetRequiredService<TrackingGrainDirectoryCache>().ClearCount);

                var restartTask = cluster.RestartSiloAsync(oldSilo);
                var restartStartedAt = DateTimeOffset.UtcNow;
                var workerProgressBeforeRestart = traffic.GetWorkerProgress();
                var successCountBeforeRestart = traffic.SuccessfulCalls;
                using var progressRaceCancellation = new CancellationTokenSource();
                var progressDuringRestartTask = traffic.WaitForTotalProgressAsync(
                    successCountBeforeRestart + 1,
                    progressRaceCancellation.Token);
                var restartRaceWinner = await Task.WhenAny(restartTask, progressDuringRestartTask);
                if (ReferenceEquals(restartRaceWinner, progressDuringRestartTask))
                {
                    await progressDuringRestartTask;
                    Assert.True(
                        traffic.SuccessfulCalls > successCountBeforeRestart,
                        $"The progress task completed without recording a successful call during '{restartPhase}'.");
                    output.WriteLine(
                        $"  Workload progress won the restart race after {DateTimeOffset.UtcNow - restartStartedAt}: "
                        + $"{successCountBeforeRestart}->{traffic.SuccessfulCalls} successful calls; "
                        + traffic.FormatWorkerProgress(workerProgressBeforeRestart));
                }
                else
                {
                    output.WriteLine(
                        $"  Restart completed before the next post-start workload success after "
                        + $"{DateTimeOffset.UtcNow - restartStartedAt}; "
                        + traffic.FormatWorkerProgress(workerProgressBeforeRestart));
                }

                progressRaceCancellation.Cancel();
                try
                {
                    await progressDuringRestartTask;
                }
                catch (OperationCanceledException) when (progressRaceCancellation.IsCancellationRequested)
                {
                }

                var replacement = await restartTask.WaitAsync(operationTimeout);
                Assert.NotNull(replacement);
                Assert.Equal(oldSilo.Name, replacement.Name);
                Assert.Equal(oldSilo.InstanceNumber, replacement.InstanceNumber);
                Assert.NotEqual(oldAddress, replacement.SiloAddress);
                Assert.Contains(replacement, cluster.Silos);
                Assert.Equal(SiloCount, cluster.Silos.Count);
                Assert.True(cluster.Silos.Count <= SiloCount, "An in-place restart grew the live cluster beyond three silos.");
                Assert.Same(originalClient, cluster.Client);
                Assert.NotNull(replacement.ServiceProvider.GetService<DirectoryMembershipService>());
                Assert.True(retiredAddresses.Add(oldAddress), $"Silo address '{oldAddress}' was retired twice.");
                var retiredAt = DateTimeOffset.UtcNow;
                var workerProgressAfterRetirement = traffic.GetWorkerProgress();

                var expectedDistributedSilos = restartIndex + 1;
                var distributedSilos = cluster.Silos.Count(
                    static silo => silo.ServiceProvider.GetService<DirectoryMembershipService>() is not null);
                Assert.Equal(expectedDistributedSilos, distributedSilos);
                Assert.Equal(SiloCount - expectedDistributedSilos, cluster.Silos.Count - distributedSilos);
                if (expectedDistributedSilos == 2)
                {
                    Assert.Equal(1, cluster.Silos.Count(
                        static silo => silo.ServiceProvider.GetService<DirectoryMembershipService>() is null));
                    Assert.Equal(2, distributedSilos);
                }

                await cluster.WaitForLivenessToStabilizeAsync().WaitAsync(operationTimeout);
                await WaitForClusterMembershipConvergenceAsync(cluster, retiredAddresses, restartPhase, operationTimeout);
                await WaitForDirectoryConvergenceAsync(cluster, $"during {restartPhase}");
                AssertCachesWereNotCleared(cacheClearBaselines, restartPhase);
                await traffic.WaitForAllWorkersProgressAsync(
                    workerProgressAfterRetirement,
                    $"post-retirement portion of restart phase '{restartPhase}'",
                    operationTimeout);
                AssertNoTrafficFailures(traffic, restartPhase);
                AssertTransientTrafficFailuresWithinLimit(traffic, SiloCount, restartPhase);
                var liveAddresses = cluster.Silos.Select(static silo => silo.SiloAddress).ToHashSet();
                AssertTrafficUsesOnlyLiveAddresses(
                    traffic,
                    restartPhase,
                    liveAddresses,
                    retiredAddresses,
                    completedAtOrAfter: retiredAt);

                var verificationPhase = $"{restartPhase}-post-convergence";
                verificationPhases.Add(verificationPhase);
                phase.Set(verificationPhase);
                await traffic.WaitForPhaseSuccessesAsync(verificationPhase, minimumSuccesses: 12, operationTimeout);
                AssertTrafficUsesOnlyLiveAddresses(traffic, verificationPhase, liveAddresses, retiredAddresses);

                var freshKeys = Enumerable.Range(0, 4)
                    .Select(_ => Interlocked.Increment(ref nextVerificationGrainKey))
                    .ToArray();
                trackedGrainKeys.AddRange(freshKeys);
                var observations = await CallIdentityGrainsAsync(originalClient, freshKeys, operationTimeout);
                var repeatedObservations = await CallIdentityGrainsAsync(originalClient, freshKeys, operationTimeout);
                AssertStableActivationProgress(observations, repeatedObservations, verificationPhase);
                await ValidateTrackedDirectoryCheckpointAsync(
                    cluster,
                    repeatedObservations,
                    liveAddresses,
                    retiredAddresses,
                    verificationPhase,
                    staleCacheEvidence);
                AssertNoTrafficFailures(traffic, restartPhase, verificationPhase);
                AssertTransientTrafficFailuresWithinLimit(traffic, SiloCount, restartPhase);
                AssertTransientTrafficFailuresWithinLimit(traffic, 0, verificationPhase);
                AssertNoImpactfulErrors(logs);
                WriteInPlacePhaseReport(
                    verificationPhase,
                    cluster,
                    upgradedSiloNames,
                    retiredAddresses,
                    traffic,
                    logs,
                    staleCacheEvidence);
            }

            var finalWorkerProgress = traffic.GetWorkerProgress();
            phase.Set("all-distributed-final");
            Assert.Equal(SiloCount, cluster.Silos.Count);
            Assert.Equal(SiloCount, upgradedSiloNames.Count);
            Assert.All(
                cluster.Silos,
                static silo => Assert.NotNull(silo.ServiceProvider.GetService<DirectoryMembershipService>()));
            Assert.Same(originalClient, cluster.Client);

            await traffic.WaitForPhaseSuccessesAsync(phase.Current, minimumSuccesses: 20, operationTimeout);
            await traffic.WaitForAllWorkersProgressAsync(
                finalWorkerProgress,
                "final all-DistributedGrainDirectory phase",
                operationTimeout);
            var finalLiveAddresses = cluster.Silos.Select(static silo => silo.SiloAddress).ToHashSet();
            AssertTrafficUsesOnlyLiveAddresses(traffic, phase.Current, finalLiveAddresses, retiredAddresses);
            await traffic.StopSchedulingAndDrainAsync(operationTimeout);
            foreach (var restartPhase in restartPhases)
            {
                AssertTransientTrafficFailuresWithinLimit(traffic, SiloCount, restartPhase);
            }

            foreach (var verificationPhase in verificationPhases)
            {
                AssertTransientTrafficFailuresWithinLimit(traffic, 0, verificationPhase);
            }

            var finalObservations = await CallIdentityGrainsAsync(originalClient, trackedGrainKeys, operationTimeout);
            var repeatedFinalObservations = await CallIdentityGrainsAsync(originalClient, trackedGrainKeys, operationTimeout);
            AssertStableActivationProgress(finalObservations, repeatedFinalObservations, phase.Current);
            await ValidateTrackedDirectoryCheckpointAsync(
                cluster,
                repeatedFinalObservations,
                finalLiveAddresses,
                retiredAddresses,
                phase.Current,
                staleCacheEvidence);
            await WaitForClusterMembershipConvergenceAsync(cluster, retiredAddresses, phase.Current, operationTimeout);
            await WaitForDirectoryConvergenceAsync(cluster, "in the final all-DistributedGrainDirectory phase");

            Assert.Equal(SiloCount, traffic.MaximumObservedSiloCount);
            Assert.True(traffic.MaximumObservedSiloCount <= SiloCount);
            AssertNoTrafficFailures(traffic);
            AssertTransientTrafficFailuresWithinLimit(traffic, 0, phase.Current);
            AssertNoImpactfulErrors(logs);
            WriteInPlacePhaseReport(
                phase.Current,
                cluster,
                upgradedSiloNames,
                retiredAddresses,
                traffic,
                logs,
                staleCacheEvidence);
        }
        catch
        {
            if (deployed)
            {
                await DumpInPlaceFailureDiagnosticsAsync(
                    cluster,
                    phase,
                    upgradedSiloNames,
                    retiredAddresses,
                    trackedGrainKeys,
                    traffic,
                    logs,
                    staleCacheEvidence);
            }

            throw;
        }
        finally
        {
            phase.Set("cleanup");
            try
            {
                if (traffic is not null)
                {
                    await traffic.CancelAsync(cleanupTimeout);
                }
            }
            finally
            {
                try
                {
                    if (deployed)
                    {
                        await cluster.StopAllSilosAsync().WaitAsync(cleanupTimeout);
                    }
                }
                finally
                {
                    await cluster.DisposeAsync().AsTask().WaitAsync(cleanupTimeout);
                }
            }
        }
    }

    private static async Task<Dictionary<long, RollingUpgradeGrainObservation>> CallIdentityGrainsAsync(
        IGrainFactory client,
        IEnumerable<long> grainKeys,
        TimeSpan timeout)
    {
        var calls = grainKeys.Distinct().Select(async grainKey =>
        {
            var grain = client.GetGrain<IRollingUpgradeIdentityGrain>(grainKey);
            var observation = await grain.Observe().AsTask().WaitAsync(timeout);
            Assert.Equal(grain.GetGrainId(), observation.Address.GrainId);
            Assert.NotNull(observation.Address.SiloAddress);
            Assert.False(observation.Address.ActivationId.IsDefault);
            Assert.True(observation.CallCount > 0);
            return (GrainKey: grainKey, Observation: observation);
        }).ToArray();

        return (await Task.WhenAll(calls)).ToDictionary(static result => result.GrainKey, static result => result.Observation);
    }

    private static void AssertCachesWereNotCleared(
        Dictionary<InProcessSiloHandle, int> baselines,
        string stage)
    {
        foreach (var (silo, baseline) in baselines)
        {
            var cache = silo.ServiceProvider.GetRequiredService<TrackingGrainDirectoryCache>();
            Assert.Equal(baseline, cache.ClearCount);
        }
    }

    private static void AssertStableActivationProgress(
        Dictionary<long, RollingUpgradeGrainObservation> first,
        Dictionary<long, RollingUpgradeGrainObservation> second,
        string stage)
    {
        Assert.Equal(first.Keys.Order(), second.Keys.Order());
        foreach (var (grainKey, firstObservation) in first)
        {
            var secondObservation = second[grainKey];
            Assert.Equal(
                firstObservation.Address.ActivationId,
                secondObservation.Address.ActivationId);
            Assert.Equal(
                firstObservation.Address.SiloAddress,
                secondObservation.Address.SiloAddress);
            Assert.True(
                secondObservation.CallCount > firstObservation.CallCount,
                $"Grain {grainKey} did not make call-count progress during '{stage}': "
                + $"{firstObservation.CallCount} -> {secondObservation.CallCount}.");
        }
    }

    private async Task ValidateTrackedDirectoryCheckpointAsync(
        InProcessTestCluster cluster,
        Dictionary<long, RollingUpgradeGrainObservation> observations,
        HashSet<SiloAddress> liveAddresses,
        HashSet<SiloAddress> retiredAddresses,
        string stage,
        StaleCacheEvidenceCapture staleCacheEvidence)
    {
        var distributedPartitions = new List<IGrainDirectoryTestHooks>();
        foreach (var silo in cluster.Silos)
        {
            if (silo.ServiceProvider.GetService<DirectoryMembershipService>() is not { } membershipService)
            {
                continue;
            }

            for (var partitionIndex = 0; partitionIndex < membershipService.PartitionsPerSilo; partitionIndex++)
            {
                distributedPartitions.Add(
                    cluster.InternalClient.GetSystemTarget<IGrainDirectoryTestHooks>(
                        GrainDirectoryPartition.CreateGrainId(silo.SiloAddress, partitionIndex).GrainId));
            }
        }

        // Validate the observable directory state before invoking recovery so that recovery cannot
        // repair a lost registration and mask a rolling-upgrade regression.
        await ValidateObservedAddressesAsync(
            cluster,
            observations,
            liveAddresses,
            retiredAddresses,
            stage,
            staleCacheEvidence);

        var integrityChecks = distributedPartitions
            .Select(static partition => partition.RecoverAndCheckIntegrityAsync().AsTask())
            .ToArray();
        await Task.WhenAll(integrityChecks);
        output.WriteLine(
            $"  Validated {observations.Count} bounded tracked grains and "
            + $"{distributedPartitions.Count} DistributedGrainDirectory partitions during '{stage}'.");
    }

    private static async Task ValidateObservedAddressesAsync(
        InProcessTestCluster cluster,
        Dictionary<long, RollingUpgradeGrainObservation> observations,
        HashSet<SiloAddress> liveAddresses,
        HashSet<SiloAddress> retiredAddresses,
        string stage,
        StaleCacheEvidenceCapture staleCacheEvidence)
    {
        var activations = GetDirectoryActivations(cluster);
        foreach (var (grainKey, observation) in observations)
        {
            var observedAddress = observation.Address;
            Assert.Contains(observedAddress.SiloAddress!, liveAddresses);
            Assert.DoesNotContain(observedAddress.SiloAddress!, retiredAddresses);

            var activationMatches = activations
                .Where(activation => activation.Address.GrainId.Equals(observedAddress.GrainId))
                .ToArray();
            var activation = Assert.Single(activationMatches);
            Assert.Equal(observedAddress.SiloAddress, activation.Address.SiloAddress);
            Assert.Equal(observedAddress.ActivationId, activation.Address.ActivationId);

            foreach (var silo in cluster.Silos)
            {
                var grainLocator = silo.ServiceProvider.GetRequiredService<GrainLocator>();
                if (grainLocator.TryLookupInCache(observedAddress.GrainId, out var cachedAddress))
                {
                    Assert.Equal(observedAddress.GrainId, cachedAddress.GrainId);
                    if (!CacheHintMatchesObservation(cachedAddress, observedAddress))
                    {
                        staleCacheEvidence.Add(stage, silo, grainKey, cachedAddress, observedAddress, retiredAddresses);
                    }
                }

                grainLocator.InvalidateCache(observedAddress.GrainId);
                var resolvedAddress = await grainLocator.Lookup(observedAddress.GrainId);
                Assert.NotNull(resolvedAddress);
                AssertAddressMatchesObservation(
                    resolvedAddress,
                    observedAddress,
                    liveAddresses,
                    retiredAddresses,
                    silo,
                    grainKey,
                    stage,
                    "authoritative directory lookup",
                    requireActivationIdentity: true);

                Assert.True(
                    grainLocator.TryLookupInCache(observedAddress.GrainId, out var repopulatedAddress),
                    $"Authoritative lookup on '{silo.Name}' did not repopulate the cache for grain {grainKey} during '{stage}'.");
                AssertAddressMatchesObservation(
                    repopulatedAddress,
                    observedAddress,
                    liveAddresses,
                    retiredAddresses,
                    silo,
                    grainKey,
                    stage,
                    "repopulated cache",
                    requireActivationIdentity: true);
            }
        }
    }

    private static bool CacheHintMatchesObservation(GrainAddress cachedAddress, GrainAddress observedAddress) =>
        cachedAddress.GrainId.Equals(observedAddress.GrainId)
        && Equals(cachedAddress.SiloAddress, observedAddress.SiloAddress)
        && (cachedAddress.ActivationId.IsDefault || cachedAddress.ActivationId.Equals(observedAddress.ActivationId));

    private static void AssertAddressMatchesObservation(
        GrainAddress resolvedAddress,
        GrainAddress observedAddress,
        HashSet<SiloAddress> liveAddresses,
        HashSet<SiloAddress> retiredAddresses,
        InProcessSiloHandle resolvingSilo,
        long grainKey,
        string stage,
        string source,
        bool requireActivationIdentity)
    {
        Assert.Equal(observedAddress.GrainId, resolvedAddress.GrainId);
        Assert.NotNull(resolvedAddress.SiloAddress);
        Assert.Contains(resolvedAddress.SiloAddress, liveAddresses);
        Assert.DoesNotContain(resolvedAddress.SiloAddress!, retiredAddresses);
        Assert.Equal(
            observedAddress.SiloAddress,
            resolvedAddress.SiloAddress);
        if (requireActivationIdentity || !resolvedAddress.ActivationId.IsDefault)
        {
            Assert.Equal(
                observedAddress.ActivationId,
                resolvedAddress.ActivationId);
            Assert.True(
                observedAddress.Matches(resolvedAddress),
                $"{source} on '{resolvingSilo.Name}' returned '{resolvedAddress.ToFullString()}' for grain {grainKey} "
                + $"during '{stage}', but the grain reported '{observedAddress.ToFullString()}'.");
        }
    }

    private async Task WaitForClusterMembershipConvergenceAsync(
        InProcessTestCluster cluster,
        HashSet<SiloAddress> retiredAddresses,
        string stage,
        TimeSpan timeout)
    {
        output.WriteLine($"  Waiting for cluster membership convergence during '{stage}'...");
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var silos = cluster.Silos;
                var liveAddresses = silos.Select(static silo => silo.SiloAddress).ToHashSet();
                var membershipServices = silos
                    .Select(static silo => silo.ServiceProvider.GetRequiredService<ClusterMembershipService>())
                    .ToArray();
                var targetVersion = new MembershipVersion(
                    membershipServices.Max(static service => service.CurrentSnapshot.Version.Value));
                await Task.WhenAll(
                    membershipServices.Select(service => service.Refresh(targetVersion, cancellation.Token).AsTask()));

                if (membershipServices.All(service =>
                        liveAddresses.All(address => service.CurrentSnapshot.GetSiloStatus(address) == SiloStatus.Active)
                        && retiredAddresses.All(address => service.CurrentSnapshot.GetSiloStatus(address) == SiloStatus.Dead)))
                {
                    output.WriteLine(
                        $"  Cluster membership converged at version {targetVersion} during '{stage}': "
                        + $"live={liveAddresses.Count}, retired={retiredAddresses.Count}.");
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellation.Token);
            }
        }
        catch (OperationCanceledException exception) when (cancellation.IsCancellationRequested)
        {
            var statusReport = string.Join(
                Environment.NewLine,
                cluster.Silos.Select(silo =>
                {
                    var snapshot = silo.ServiceProvider.GetRequiredService<ClusterMembershipService>().CurrentSnapshot;
                    return $"{silo.Name} ({silo.SiloAddress}) version={snapshot.Version}: "
                        + string.Join(", ", snapshot.Members.Select(static member => $"{member.Key}={member.Value.Status}"));
                }));
            throw new TimeoutException(
                $"Timed out waiting for cluster membership convergence during '{stage}' after {timeout}."
                + Environment.NewLine
                + statusReport,
                exception);
        }
    }

    private void AssertNoTrafficFailures(SustainedRollingUpgradeTraffic traffic, params string[] phases)
    {
        var failures = traffic.Failures
            .Where(failure => phases.Length == 0 || phases.Contains(failure.Phase, StringComparer.Ordinal))
            .ToArray();
        foreach (var failure in failures.Take(20))
        {
            output.WriteLine(
                $"CALLER FAILURE phase='{failure.Phase}' worker={failure.Worker} grain={failure.GrainKey}: "
                + $"{failure.Exception.GetType().Name}: {failure.Exception.Message}");
        }

        Assert.Empty(failures);
    }

    private void AssertTransientTrafficFailuresWithinLimit(
        SustainedRollingUpgradeTraffic traffic,
        int maximumFailures,
        params string[] phases)
    {
        var failures = traffic.TransientFailures
            .Where(failure => phases.Contains(failure.Phase, StringComparer.Ordinal))
            .ToArray();
        foreach (var failure in failures.Take(20))
        {
            output.WriteLine(
                $"RETRIED TRANSIENT CALL phase='{failure.Phase}' worker={failure.Worker} grain={failure.GrainKey}: "
                + $"{failure.Exception.GetType().Name}: {failure.Exception.Message}");
        }

        Assert.True(
            failures.Length <= maximumFailures,
            $"Observed {failures.Length} retried transient calls in [{string.Join(", ", phases)}]; "
            + $"the maximum is {maximumFailures}.");
    }

    private static void AssertTrafficUsesOnlyLiveAddresses(
        SustainedRollingUpgradeTraffic traffic,
        string phase,
        HashSet<SiloAddress> liveAddresses,
        HashSet<SiloAddress> retiredAddresses,
        DateTimeOffset? completedAtOrAfter = null)
    {
        var observations = traffic.GetObservations(phase);
        if (completedAtOrAfter is { } lowerBound)
        {
            observations = observations
                .Where(observation => observation.CompletedAt >= lowerBound)
                .ToArray();
        }

        Assert.NotEmpty(observations);
        foreach (var observation in observations)
        {
            var address = observation.Observation.Address;
            Assert.Contains(address.SiloAddress!, liveAddresses);
            Assert.DoesNotContain(address.SiloAddress!, retiredAddresses);
            Assert.False(address.ActivationId.IsDefault);
            Assert.True(observation.Observation.CallCount > 0);
        }
    }

    private void AssertNoImpactfulErrors(PhaseAwareLogCapture logs)
    {
        var errors = logs.ToArray()
            .Where(static entry => entry.Level >= LogLevel.Error)
            .Where(static entry => !IsExpectedIntentionalRestartLog(entry))
            .ToArray();
        foreach (var error in errors.Take(20))
        {
            output.WriteLine($"IMPACTFUL ERROR {error}");
        }

        Assert.Empty(errors);
    }

    private static bool IsExpectedIntentionalRestartLog(PhaseAwareLogEntry entry)
    {
        if (!entry.Phase.StartsWith("restart-", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(entry.Category, "Orleans.Runtime.GrainDirectory.ClientDirectory", StringComparison.Ordinal)
            && entry.Message.StartsWith("Exception publishing client routing table", StringComparison.Ordinal)
            && string.Equals(entry.ExceptionType, typeof(TaskCanceledException).FullName, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(entry.Category, "Orleans.Messaging", StringComparison.Ordinal)
            || !entry.Message.StartsWith("Failed to address message", StringComparison.Ordinal))
        {
            return false;
        }

        return (entry.Message.Contains("IGrainDirectoryPartition.", StringComparison.Ordinal)
                && entry.Message.Contains("not active on this silo", StringComparison.Ordinal))
            || (string.Equals(entry.ExceptionType, typeof(SiloUnavailableException).FullName, StringComparison.Ordinal)
                && entry.ExceptionMessage?.Contains("is shutting down", StringComparison.Ordinal) == true);
    }

    private void WriteInPlacePhaseReport(
        string stage,
        InProcessTestCluster cluster,
        ConcurrentDictionary<string, byte> upgradedSiloNames,
        HashSet<SiloAddress> retiredAddresses,
        SustainedRollingUpgradeTraffic traffic,
        PhaseAwareLogCapture logs,
        StaleCacheEvidenceCapture staleCacheEvidence)
    {
        var distributedCount = cluster.Silos.Count(
            static silo => silo.ServiceProvider.GetService<DirectoryMembershipService>() is not null);
        var stageLogs = logs.ToArray().Where(entry => string.Equals(entry.Phase, stage, StringComparison.Ordinal)).ToArray();
        var stageStaleCacheEvidence = staleCacheEvidence
            .ToArray()
            .Where(entry => string.Equals(entry.Phase, stage, StringComparison.Ordinal))
            .ToArray();
        output.WriteLine(
            $"PHASE REPORT '{stage}': live={cluster.Silos.Count}, Local={cluster.Silos.Count - distributedCount}, "
            + $"DGD={distributedCount}, upgradedNames={upgradedSiloNames.Count}, retiredAddresses={retiredAddresses.Count}, "
            + $"trafficSuccesses={traffic.SuccessfulCalls}, callerFailures={traffic.Failures.Length}, "
            + $"retriedTransientCalls={traffic.TransientFailures.Length}, "
            + $"maxObservedLiveSilos={traffic.MaximumObservedSiloCount}, phaseLogs={stageLogs.Length}, "
            + $"staleCacheHints={stageStaleCacheEvidence.Length}.");
        output.WriteLine(
            "  Live silos: "
            + string.Join(", ", cluster.Silos.Select(static silo => $"{silo.Name}={silo.SiloAddress}")));
        foreach (var entry in stageStaleCacheEvidence.Take(20))
        {
            output.WriteLine($"  STALE CACHE HINT {entry}");
        }

        foreach (var entry in stageLogs.Take(20))
        {
            output.WriteLine($"  {entry}");
        }
    }

    private async Task DumpInPlaceFailureDiagnosticsAsync(
        InProcessTestCluster cluster,
        RollingUpgradePhase phase,
        ConcurrentDictionary<string, byte> upgradedSiloNames,
        HashSet<SiloAddress> retiredAddresses,
        List<long> trackedGrainKeys,
        SustainedRollingUpgradeTraffic? traffic,
        PhaseAwareLogCapture logs,
        StaleCacheEvidenceCapture staleCacheEvidence)
    {
        output.WriteLine($"ROLLING UPGRADE FAILURE in phase '{phase.Current}'.");
        output.WriteLine($"Upgraded stable silo names: [{string.Join(", ", upgradedSiloNames.Keys.Order())}]");
        output.WriteLine($"Retired silo addresses: [{string.Join(", ", retiredAddresses)}]");
        output.WriteLine(
            "Live silo handles: "
            + string.Join(", ", cluster.Silos.Select(static silo => $"{silo.Name}={silo.SiloAddress} active={silo.IsActive}")));

        if (traffic is not null)
        {
            output.WriteLine(
                $"Traffic: successes={traffic.SuccessfulCalls}, failures={traffic.Failures.Length}, "
                + $"retriedTransientCalls={traffic.TransientFailures.Length}, "
                + $"maxObservedLiveSilos={traffic.MaximumObservedSiloCount}.");
            foreach (var failure in traffic.Failures.Take(50))
            {
                output.WriteLine(
                    $"  CALLER FAILURE phase='{failure.Phase}' worker={failure.Worker} "
                    + $"grain={failure.GrainKey}: {failure.Exception}");
            }

            foreach (var failure in traffic.TransientFailures.TakeLast(50))
            {
                output.WriteLine(
                    $"  RETRIED TRANSIENT CALL phase='{failure.Phase}' worker={failure.Worker} "
                    + $"grain={failure.GrainKey}: {failure.Exception}");
            }
        }

        foreach (var entry in logs.ToArray().TakeLast(200))
        {
            output.WriteLine($"  CAPTURED LOG {entry}");
        }

        foreach (var entry in staleCacheEvidence.ToArray().TakeLast(200))
        {
            output.WriteLine($"  STALE CACHE HINT {entry}");
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach (var grainKey in trackedGrainKeys.Take(6))
        {
            var grainId = cluster.Client.GetGrain<IRollingUpgradeIdentityGrain>(grainKey).GetGrainId();
            output.WriteLine($"DETAILED GRAIN REPORTS for grain {grainKey} ({grainId}):");
            foreach (var silo in cluster.Silos)
            {
                try
                {
                    var siloControl = cluster.InternalClient.GetSystemTarget<ISiloControl>(
                        Constants.SiloControlType,
                        silo.SiloAddress);
                    var report = await siloControl.GetDetailedGrainReport(grainId).WaitAsync(cancellation.Token);
                    output.WriteLine(report.ToString());
                }
                catch (Exception exception)
                {
                    output.WriteLine(
                        $"  Failed to get report from {silo.Name} ({silo.SiloAddress}): "
                        + $"{exception.GetType().Name}: {exception.Message}");
                }
            }
        }
    }

    private sealed class RollingUpgradePhase(string initialPhase, ITestOutputHelper testOutput)
    {
        private string _current = initialPhase;

        public string Current => Volatile.Read(ref _current);

        public void Set(string value)
        {
            Volatile.Write(ref _current, value);
            testOutput.WriteLine($"BEGIN PHASE '{value}'");
        }
    }

    private sealed class SustainedRollingUpgradeTraffic(
        IGrainFactory client,
        InProcessTestCluster cluster,
        RollingUpgradePhase phase,
        long[] hotGrainKeys,
        int expectedMaximumSilos,
        TimeSpan callTimeout)
    {
        private readonly CancellationTokenSource _shutdown = new();
        private readonly ConcurrentDictionary<string, long> _phaseSuccesses = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<RollingUpgradeTrafficFailure> _failures = new();
        private readonly ConcurrentQueue<RollingUpgradeTrafficFailure> _transientFailures = new();
        private readonly ConcurrentQueue<RollingUpgradeTrafficObservation> _observations = new();
        private TaskCompletionSource<bool> _progressSignal = CreateProgressSignal();
        private Task _runTask = Task.CompletedTask;
        private long[] _workerSuccesses = [];
        private long _successfulCalls;
        private long _nextFreshGrainKey = 1_000_000;
        private int _maximumObservedSiloCount;
        private int _observationCount;
        private int _stopScheduling;

        public long SuccessfulCalls => Interlocked.Read(ref _successfulCalls);

        public int MaximumObservedSiloCount => Volatile.Read(ref _maximumObservedSiloCount);

        public RollingUpgradeTrafficFailure[] Failures => _failures.ToArray();

        public RollingUpgradeTrafficFailure[] TransientFailures => _transientFailures.ToArray();

        public void Start(int workerCount)
        {
            Assert.True(_runTask.IsCompleted);
            Assert.True(workerCount > 0);
            _workerSuccesses = new long[workerCount];
            ObserveSiloCount();
            _runTask = Task.WhenAll(Enumerable.Range(0, workerCount).Select(worker => RunWorkerAsync(worker, _shutdown.Token)));
        }

        public long[] GetWorkerProgress()
        {
            var result = new long[_workerSuccesses.Length];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = Interlocked.Read(ref _workerSuccesses[i]);
            }

            return result;
        }

        public string FormatWorkerProgress(long[] baseline)
        {
            var current = GetWorkerProgress();
            Assert.Equal(baseline.Length, current.Length);
            return "worker progress=["
                + string.Join(", ", current.Select((value, worker) => $"{worker}:{baseline[worker]}->{value}"))
                + "]";
        }

        public RollingUpgradeTrafficObservation[] GetObservations(string targetPhase) =>
            _observations
                .ToArray()
                .Where(observation => string.Equals(observation.Phase, targetPhase, StringComparison.Ordinal))
                .ToArray();

        public async Task WaitForPhaseSuccessesAsync(string targetPhase, long minimumSuccesses, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            try
            {
                while (GetPhaseSuccesses(targetPhase) < minimumSuccesses)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), cancellation.Token);
                }
            }
            catch (OperationCanceledException exception) when (cancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Traffic made {GetPhaseSuccesses(targetPhase)}/{minimumSuccesses} successful calls "
                    + $"during phase '{targetPhase}' after {timeout}.",
                    exception);
            }
        }

        public async Task WaitForAllWorkersProgressAsync(long[] baseline, string stage, TimeSpan timeout)
        {
            Assert.Equal(_workerSuccesses.Length, baseline.Length);
            using var cancellation = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var progressSignal = Volatile.Read(ref _progressSignal);
                    var current = GetWorkerProgress();
                    if (current.Select((value, worker) => value > baseline[worker]).All(static value => value))
                    {
                        return;
                    }

                    await progressSignal.Task.WaitAsync(cancellation.Token);
                }
            }
            catch (OperationCanceledException exception) when (cancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Not every traffic worker made progress during {stage} after {timeout}: "
                    + FormatWorkerProgress(baseline)
                    + $", caller failures={Failures.Length}.",
                    exception);
            }
        }

        public async Task WaitForTotalProgressAsync(long target, CancellationToken cancellationToken)
        {
            while (SuccessfulCalls < target)
            {
                var progressSignal = Volatile.Read(ref _progressSignal);
                if (SuccessfulCalls >= target)
                {
                    return;
                }

                await progressSignal.Task.WaitAsync(cancellationToken);
            }
        }

        public async Task StopSchedulingAndDrainAsync(TimeSpan timeout)
        {
            Volatile.Write(ref _stopScheduling, 1);
            try
            {
                await _runTask.WaitAsync(timeout);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Timed out after {timeout} while draining in-flight traffic calls: "
                    + FormatWorkerProgress(new long[_workerSuccesses.Length])
                    + $", caller failures={Failures.Length}.",
                    exception);
            }
        }

        public async Task CancelAsync(TimeSpan timeout)
        {
            Volatile.Write(ref _stopScheduling, 1);
            _shutdown.Cancel();
            await _runTask.WaitAsync(timeout);
            _shutdown.Dispose();
        }

        private long GetPhaseSuccesses(string targetPhase) =>
            _phaseSuccesses.TryGetValue(targetPhase, out var value) ? value : 0;

        private async Task RunWorkerAsync(int worker, CancellationToken cancellationToken)
        {
            var iteration = 0;
            while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _stopScheduling) == 0)
            {
                ObserveSiloCount();
                var hotKey = hotGrainKeys[(worker + iteration) % hotGrainKeys.Length];
                await CallOnceAsync(worker, hotKey, cancellationToken);
                if (iteration % 5 == 0
                    && !cancellationToken.IsCancellationRequested
                    && Volatile.Read(ref _stopScheduling) == 0)
                {
                    await CallOnceAsync(worker, Interlocked.Increment(ref _nextFreshGrainKey), cancellationToken);
                }

                iteration++;
                if (Volatile.Read(ref _stopScheduling) != 0)
                {
                    return;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        private async Task CallOnceAsync(int worker, long grainKey, CancellationToken cancellationToken)
        {
            var callPhase = phase.Current;
            try
            {
                var grain = client.GetGrain<IRollingUpgradeIdentityGrain>(grainKey);
                var observation = await ObserveWithRetryAsync(grain, callPhase, worker, grainKey, cancellationToken);
                if (!grain.GetGrainId().Equals(observation.Address.GrainId)
                    || observation.Address.SiloAddress is null
                    || observation.Address.ActivationId.IsDefault
                    || observation.CallCount <= 0)
                {
                    throw new InvalidOperationException(
                        $"Grain {grainKey} returned an invalid activation observation '{observation}'.");
                }

                _phaseSuccesses.AddOrUpdate(callPhase, 1, static (_, current) => current + 1);
                _observations.Enqueue(new(callPhase, worker, grainKey, observation, DateTimeOffset.UtcNow));
                if (Interlocked.Increment(ref _observationCount) > 4_096
                    && _observations.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _observationCount);
                }

                Interlocked.Increment(ref _workerSuccesses[worker]);
                Interlocked.Increment(ref _successfulCalls);
                SignalProgress();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _failures.Enqueue(new(callPhase, worker, grainKey, exception, DateTimeOffset.UtcNow));
            }
        }

        private async Task<RollingUpgradeGrainObservation> ObserveWithRetryAsync(
            IRollingUpgradeIdentityGrain grain,
            string callPhase,
            int worker,
            long grainKey,
            CancellationToken cancellationToken)
        {
            const int MaxAttempts = 2;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await grain.Observe().AsTask().WaitAsync(callTimeout, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (attempt < MaxAttempts && IsTransientUpgradeFailure(exception))
                {
                    _transientFailures.Enqueue(new(callPhase, worker, grainKey, exception, DateTimeOffset.UtcNow));
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                }
            }
        }

        private static bool IsTransientUpgradeFailure(Exception exception) =>
            exception.GetBaseException() is TimeoutException
                or SiloUnavailableException
                or OrleansMessageRejectionException;

        private void SignalProgress()
        {
            var nextSignal = CreateProgressSignal();
            Interlocked.Exchange(ref _progressSignal, nextSignal).TrySetResult(true);
        }

        private static TaskCompletionSource<bool> CreateProgressSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void ObserveSiloCount()
        {
            var count = cluster.Silos.Count;
            var currentMaximum = Volatile.Read(ref _maximumObservedSiloCount);
            while (count > currentMaximum)
            {
                var observed = Interlocked.CompareExchange(ref _maximumObservedSiloCount, count, currentMaximum);
                if (observed == currentMaximum)
                {
                    break;
                }

                currentMaximum = observed;
            }

            if (count > expectedMaximumSilos)
            {
                _failures.Enqueue(
                    new(
                        phase.Current,
                        null,
                        null,
                        new InvalidOperationException(
                            $"Observed {count} live silo handles; the maximum is {expectedMaximumSilos}."),
                        DateTimeOffset.UtcNow));
            }
        }
    }

    private sealed record RollingUpgradeTrafficObservation(
        string Phase,
        int Worker,
        long GrainKey,
        RollingUpgradeGrainObservation Observation,
        DateTimeOffset CompletedAt);

    private sealed record RollingUpgradeTrafficFailure(
        string Phase,
        int? Worker,
        long? GrainKey,
        Exception Exception,
        DateTimeOffset Timestamp);

    private sealed class StaleCacheEvidenceCapture
    {
        private readonly ConcurrentQueue<StaleCacheEvidence> _entries = new();

        public void Add(
            string phase,
            InProcessSiloHandle resolvingSilo,
            long grainKey,
            GrainAddress cachedAddress,
            GrainAddress observedAddress,
            HashSet<SiloAddress> retiredAddresses)
        {
            _entries.Enqueue(
                new(
                    DateTimeOffset.UtcNow,
                    phase,
                    resolvingSilo.Name,
                    resolvingSilo.SiloAddress,
                    grainKey,
                    cachedAddress,
                    observedAddress,
                    cachedAddress.SiloAddress is { } cachedSilo && retiredAddresses.Contains(cachedSilo)));
        }

        public StaleCacheEvidence[] ToArray() => _entries.ToArray();
    }

    private sealed record StaleCacheEvidence(
        DateTimeOffset Timestamp,
        string Phase,
        string ResolvingSiloName,
        SiloAddress ResolvingSiloAddress,
        long GrainKey,
        GrainAddress CachedAddress,
        GrainAddress ObservedAddress,
        bool ReferencedRetiredSilo)
    {
        public override string ToString() =>
            $"{Timestamp:O} phase='{Phase}' resolvingSilo='{ResolvingSiloName}' ({ResolvingSiloAddress}) "
            + $"grain={GrainKey} retired={ReferencedRetiredSilo} cached='{CachedAddress.ToFullString()}' "
            + $"observed='{ObservedAddress.ToFullString()}'";
    }

    private sealed class PhaseAwareLoggerProvider(string siloName, PhaseAwareLogCapture capture) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new PhaseAwareLogger(siloName, categoryName, capture);

        public void Dispose()
        {
        }

        private sealed class PhaseAwareLogger(
            string siloName,
            string category,
            PhaseAwareLogCapture capture) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) =>
                logLevel >= LogLevel.Error
                || (logLevel >= LogLevel.Information
                    && (string.Equals(category, typeof(DistributedRemoteGrainDirectory).FullName, StringComparison.Ordinal)
                        || string.Equals(category, typeof(GrainDirectoryHandoffManager).FullName, StringComparison.Ordinal)))
                || (logLevel >= LogLevel.Warning
                    && string.Equals(category, "Orleans.Messaging", StringComparison.Ordinal));

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    capture.Add(siloName, category, logLevel, eventId, formatter(state, exception), exception);
                }
            }
        }
    }

    private sealed class PhaseAwareLogCapture(RollingUpgradePhase phase)
    {
        private readonly ConcurrentQueue<PhaseAwareLogEntry> _entries = new();

        public void Add(
            string siloName,
            string category,
            LogLevel level,
            EventId eventId,
            string message,
            Exception? exception)
        {
            var baseException = exception?.GetBaseException();
            _entries.Enqueue(
                new(
                    DateTimeOffset.UtcNow,
                    phase.Current,
                    siloName,
                    category,
                    level,
                    eventId,
                    message,
                    baseException?.GetType().FullName,
                    baseException?.Message));
        }

        public PhaseAwareLogEntry[] ToArray() => _entries.ToArray();
    }

    private sealed record PhaseAwareLogEntry(
        DateTimeOffset Timestamp,
        string Phase,
        string SiloName,
        string Category,
        LogLevel Level,
        EventId EventId,
        string Message,
        string? ExceptionType,
        string? ExceptionMessage)
    {
        public override string ToString() =>
            $"{Timestamp:O} phase='{Phase}' silo='{SiloName}' [{Level}] [{Category}] ({EventId.Id}:{EventId.Name}) "
            + $"{Message}"
            + (ExceptionType is null ? string.Empty : $" | {ExceptionType}: {ExceptionMessage}");
    }
}

internal interface IRollingUpgradeIdentityGrain : IGrainWithIntegerKey
{
    ValueTask<RollingUpgradeGrainObservation> Observe();
}

[GenerateSerializer, Immutable]
internal sealed record RollingUpgradeGrainObservation(
    [property: Id(0)] GrainAddress Address,
    [property: Id(1)] long CallCount);

internal sealed class RollingUpgradeIdentityGrain : Grain, IRollingUpgradeIdentityGrain
{
    private long _callCount;

    public ValueTask<RollingUpgradeGrainObservation> Observe() =>
        new(new RollingUpgradeGrainObservation(GrainContext.Address, ++_callCount));
}

internal sealed class TrackingGrainDirectoryCache : IGrainDirectoryCache
{
    private readonly IGrainDirectoryCache _inner = new LruGrainDirectoryCache(
        maxCacheSize: 4_096,
        maxCacheTTL: TimeSpan.FromMinutes(10),
        timeProvider: TimeProvider.System);
    private int _clearCount;

    public int ClearCount => Volatile.Read(ref _clearCount);

    public IEnumerable<(GrainAddress ActivationAddress, int Version)> KeyValues => _inner.KeyValues;

    public void AddOrUpdate(GrainAddress value, int version) => _inner.AddOrUpdate(value, version);

    public bool Remove(GrainId key) => _inner.Remove(key);

    public bool Remove(GrainAddress key) => _inner.Remove(key);

    public void Clear()
    {
        Interlocked.Increment(ref _clearCount);
        _inner.Clear();
    }

    public bool LookUp(GrainId key, out GrainAddress result, out int version) =>
        _inner.LookUp(key, out result, out version);
}
