#nullable enable

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.GrainDirectory.Compatibility;

[TestSuite("Functional")]
[TestProvider("None")]
[TestCategory("Directory"), TestCategory("Functional")]
public sealed class GrainDirectoryProcessCompatibilityTests(ITestOutputHelper output)
{
    internal static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(75);

    [Theory]
    [InlineData(HostVersion.Released, HostVersion.Current)]
    [InlineData(HostVersion.Current, HostVersion.Released)]
    public async Task DirectedRollingReplacement_PreservesRegistrationAndTraffic(
        HostVersion sourceVersion,
        HostVersion destinationVersion)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new CompatibilityProcessCluster(output);

        var destinationAnchor = await cluster.StartAsync(
            destinationVersion,
            $"{destinationVersion}-anchor",
            isPrimary: true,
            cancellationToken);
        var sourceOwner = await cluster.StartAsync(
            sourceVersion,
            $"{sourceVersion}-source-owner",
            isPrimary: false,
            cancellationToken);
        await cluster.WaitForMembershipAsync(
            $"{sourceVersion}-to-{destinationVersion} baseline membership",
            [destinationAnchor, sourceOwner],
            [],
            cancellationToken);

        var keys = await cluster.FindKeysOwnedByAsync(
            $"selecting {sourceVersion}-owned baseline registrations",
            destinationAnchor,
            sourceOwner.SiloAddress,
            count: 64,
            $"directed-{sourceVersion}-{destinationVersion}-{cluster.ClusterId}",
            [destinationAnchor, sourceOwner],
            [],
            cancellationToken);
        var baselineOwners = await cluster.WaitForDirectoryAgreementAsync(
            $"{sourceVersion}-owned baseline directory ownership",
            [destinationAnchor, sourceOwner],
            keys,
            cancellationToken);
        Assert.All(keys, key => Assert.Equal(sourceOwner.SiloAddress, baselineOwners[key]));
        var accepted = (await destinationAnchor.PingAsync(keys, cancellationToken))
            .ToDictionary(static ping => ping.Key, StringComparer.Ordinal);
        Assert.All(accepted.Values, ping => Assert.Equal(destinationAnchor.SiloAddress, ping.SiloAddress));

        CompatibilityHostProcess replacement;
        Dictionary<string, string> finalOwners;
        await using (var traffic = await CompatibilityTraffic.StartAsync(
            destinationAnchor,
            accepted.Values.ToArray(),
            cancellationToken))
        {
            replacement = await cluster.StartAsync(
                destinationVersion,
                $"{destinationVersion}-replacement",
                isPrimary: false,
                cancellationToken);
            var released = sourceVersion == HostVersion.Released ? sourceOwner : destinationAnchor;
            var current = sourceVersion == HostVersion.Current ? sourceOwner : destinationAnchor;
            CompatibilityProcessCluster.AssertRuntimeProvenance(released, current);
            Assert.Equal(destinationVersion, destinationAnchor.Version);
            Assert.Equal(destinationVersion, replacement.Version);
            Assert.Equal(destinationAnchor.Provenance.Sha256, replacement.Provenance.Sha256);

            await cluster.WaitForMembershipAsync(
                $"{sourceVersion}-to-{destinationVersion} replacement join",
                [destinationAnchor, sourceOwner, replacement],
                [],
                cancellationToken);
            await cluster.AssertRegistrationsAsync(
                $"registrations after {destinationVersion} replacement joins",
                [destinationAnchor, sourceOwner, replacement],
                accepted,
                cancellationToken);

            await sourceOwner.StopAsync(cancellationToken);
            Assert.Equal(0, sourceOwner.ExitCode);
            await cluster.WaitForMembershipAsync(
                $"membership after {sourceVersion} owner removal",
                [destinationAnchor, replacement],
                [sourceOwner],
                cancellationToken);
            finalOwners = await cluster.AssertRegistrationsAsync(
                $"registrations after {sourceVersion} owner removal",
                [destinationAnchor, replacement],
                accepted,
                cancellationToken);
        }

        var destinationAddresses = new[] { destinationAnchor.SiloAddress, replacement.SiloAddress };
        Assert.All(
            keys,
            key =>
            {
                Assert.Equal(sourceOwner.SiloAddress, baselineOwners[key]);
                Assert.NotEqual(sourceOwner.SiloAddress, finalOwners[key]);
                Assert.Contains(finalOwners[key], destinationAddresses);
            });
        AssertIdentityBatch(
            accepted,
            await replacement.PingAsync(keys, cancellationToken));
    }

    [Theory]
    [InlineData(HostVersion.Released, HostVersion.Current)]
    [InlineData(HostVersion.Current, HostVersion.Released)]
    public async Task ResumedDirectoryOwner_SelfTerminatesAfterDeadDeclarationAndTakeover(
        HostVersion sourceVersion,
        HostVersion destinationVersion)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new CompatibilityProcessCluster(output);

        var survivingAnchor = await cluster.StartAsync(
            destinationVersion,
            $"{destinationVersion}-anchor",
            isPrimary: true,
            cancellationToken);
        var activationHost = await cluster.StartAsync(
            destinationVersion,
            $"{destinationVersion}-activation-host",
            isPrimary: false,
            cancellationToken);
        var pausedOwner = await cluster.StartAsync(
            sourceVersion,
            $"{sourceVersion}-directory-owner",
            isPrimary: false,
            cancellationToken);
        var released = sourceVersion == HostVersion.Released ? pausedOwner : survivingAnchor;
        var current = sourceVersion == HostVersion.Current ? pausedOwner : survivingAnchor;
        CompatibilityProcessCluster.AssertRuntimeProvenance(released, current);
        await cluster.WaitForMembershipAsync(
            "pre-pause mixed-version membership",
            [survivingAnchor, activationHost, pausedOwner],
            [],
            cancellationToken);

        var candidates = await cluster.FindKeysOwnedByAsync(
            $"selecting a {sourceVersion} directory-owned grain",
            survivingAnchor,
            pausedOwner.SiloAddress,
            count: 1,
            $"pause-{cluster.ClusterId}",
            [survivingAnchor, activationHost, pausedOwner],
            [],
            cancellationToken);
        var grainKey = candidates.Single();

        var identity = (await activationHost.PingAsync([grainKey], cancellationToken)).Single();
        Assert.Equal(activationHost.SiloAddress, identity.SiloAddress);
        var accepted = new Dictionary<string, GrainPing>(StringComparer.Ordinal)
        {
            [grainKey] = identity,
        };
        await cluster.AssertRegistrationsAsync(
            "registration on source directory owner before pause",
            [survivingAnchor, activationHost, pausedOwner],
            accepted,
            cancellationToken);

        pausedOwner.Suspend();
        try
        {
            await cluster.WaitForMembershipAsync(
                "survivors declare paused source owner dead",
                [survivingAnchor, activationHost],
                [pausedOwner],
                cancellationToken);
            var takeoverOwners = await cluster.AssertRegistrationsAsync(
                "directory takeover after source owner death",
                [survivingAnchor, activationHost],
                accepted,
                cancellationToken);
            Assert.NotEqual(pausedOwner.SiloAddress, takeoverOwners[grainKey]);
            AssertIdentityBatch(
                accepted,
                await survivingAnchor.PingAsync([grainKey], cancellationToken));
        }
        finally
        {
            pausedOwner.Resume();
        }

        await pausedOwner.WaitForExitAsync(
            "source owner observes its dead membership entry and self-terminates",
            PhaseTimeout,
            cancellationToken);
        Assert.NotEqual(0, pausedOwner.ExitCode);
        Assert.Contains("I have been told I am dead", pausedOwner.StandardError, StringComparison.Ordinal);

        AssertIdentityBatch(
            accepted,
            await activationHost.PingAsync([grainKey], cancellationToken));
        await cluster.AssertRegistrationsAsync(
            "registration remains authoritative after stale owner self-termination",
            [survivingAnchor, activationHost],
            accepted,
            cancellationToken);
    }

    [Fact]
    public async Task FailedStartCleanup_DescribeRemainsSafeAfterDisposal()
    {
        var name = $"failed-start-{Guid.NewGuid():N}";
        var logDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "compatibility-process-logs",
            name);
        var process = new CompatibilityHostProcess(
            HostVersion.Current,
            name,
            Path.Combine(AppContext.BaseDirectory, $"{name}.missing.dll"),
            logDirectory,
            siloPort: 1,
            gatewayPort: 2,
            primarySiloPort: 1,
            clusterId: name,
            serviceId: name);

        var startException = await Record.ExceptionAsync(
            () => process.StartAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(startException);
        var cleanupException = await Record.ExceptionAsync(() => process.DisposeAsync().AsTask());
        Assert.Null(cleanupException);
        var description = process.Describe();
        Assert.Contains(name, description, StringComparison.Ordinal);
        Assert.Contains("exited", description, StringComparison.Ordinal);
    }

    private static void AssertIdentityBatch(
        IReadOnlyDictionary<string, GrainPing> expected,
        GrainPing[] actual)
    {
        Assert.Equal(expected.Count, actual.Length);
        Assert.Equal(
            expected.Keys.Order(StringComparer.Ordinal),
            actual.Select(static ping => ping.Key).Order(StringComparer.Ordinal));
        foreach (var ping in actual)
        {
            var expectedPing = expected[ping.Key];
            Assert.Equal(expectedPing.SiloAddress, ping.SiloAddress);
            Assert.Equal(expectedPing.ActivationId, ping.ActivationId);
            Assert.Equal(expectedPing.InstanceId, ping.InstanceId);
            Assert.True(
                ping.CallCount > expectedPing.CallCount,
                $"Expected '{ping.Key}' call count greater than {expectedPing.CallCount}, actual {ping.CallCount}.");
        }
    }
}

public enum HostVersion
{
    Released,
    Current,
}

internal sealed class CompatibilityTraffic : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _runTask;

    private CompatibilityTraffic(
        CompatibilityHostProcess process,
        GrainPing[] expected,
        TaskCompletionSource firstPass,
        CancellationToken cancellationToken)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunAsync(process, expected, firstPass, _cancellation.Token);
    }

    public static async Task<CompatibilityTraffic> StartAsync(
        CompatibilityHostProcess process,
        GrainPing[] expected,
        CancellationToken cancellationToken)
    {
        var firstPass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new CompatibilityTraffic(process, expected, firstPass, cancellationToken);
        try
        {
            var completed = await Task.WhenAny(firstPass.Task, result._runTask)
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (ReferenceEquals(completed, result._runTask))
            {
                await result._runTask;
            }

            return result;
        }
        catch
        {
            result._cancellation.Cancel();
            await result._runTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            result._cancellation.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        try
        {
            await _runTask;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private static async Task RunAsync(
        CompatibilityHostProcess process,
        GrainPing[] expected,
        TaskCompletionSource firstPass,
        CancellationToken cancellationToken)
    {
        var lastByKey = expected.ToDictionary(static ping => ping.Key, StringComparer.Ordinal);
        var keys = expected.Select(static ping => ping.Key).ToArray();
        while (true)
        {
            var actual = await process.PingAsync(keys, cancellationToken);
            if (actual.Length != keys.Length)
            {
                throw new InvalidOperationException(
                    $"Traffic response cardinality mismatch. Expected {keys.Length}, actual {actual.Length}.");
            }

            var actualKeys = actual.Select(static ping => ping.Key).ToHashSet(StringComparer.Ordinal);
            if (!actualKeys.SetEquals(keys))
            {
                throw new InvalidOperationException(
                    $"Traffic response keys mismatch. Expected [{string.Join(", ", keys)}], "
                    + $"actual [{string.Join(", ", actualKeys)}].");
            }

            foreach (var ping in actual)
            {
                var previous = lastByKey[ping.Key];
                Assert.Equal(previous.SiloAddress, ping.SiloAddress);
                Assert.Equal(previous.ActivationId, ping.ActivationId);
                Assert.Equal(previous.InstanceId, ping.InstanceId);
                Assert.True(
                    ping.CallCount > previous.CallCount,
                    $"Traffic call count for '{ping.Key}' did not advance: previous={previous.CallCount}, actual={ping.CallCount}.");
                lastByKey[ping.Key] = ping;
            }

            firstPass.TrySetResult();
        }
    }
}

internal sealed class CompatibilityProcessCluster : IAsyncDisposable
{
    private readonly List<CompatibilityHostProcess> _processes = [];
    private readonly ITestOutputHelper _output;
    private readonly TestClusterPortAllocator _portAllocator = new();
    private readonly string _logDirectory;
    private readonly int _baseSiloPort;
    private readonly int _baseGatewayPort;
    private int _nextPortOffset;

    public CompatibilityProcessCluster(ITestOutputHelper output)
    {
        _output = output;
        ClusterId = $"directory-compat-{Guid.NewGuid():N}";
        ServiceId = $"directory-compat-service-{Guid.NewGuid():N}";
        _logDirectory = Path.Combine(AppContext.BaseDirectory, "compatibility-process-logs", ClusterId);
        Directory.CreateDirectory(_logDirectory);
        (_baseSiloPort, _baseGatewayPort) = _portAllocator.AllocateConsecutivePortPairs(12);
    }

    public string ClusterId { get; }

    public string ServiceId { get; }

    public async Task<CompatibilityHostProcess> StartAsync(
        HostVersion version,
        string name,
        bool isPrimary,
        CancellationToken cancellationToken)
    {
        if (isPrimary && _processes.Count > 0)
        {
            throw new InvalidOperationException("The primary process must be started first.");
        }

        var offset = _nextPortOffset++;
        var process = new CompatibilityHostProcess(
            version,
            name,
            GetHostPath(version),
            _logDirectory,
            _baseSiloPort + offset,
            _baseGatewayPort + offset,
            _baseSiloPort,
            ClusterId,
            ServiceId);
        _processes.Add(process);
        await process.StartAsync(cancellationToken);
        _output.WriteLine(
            $"Started {name}: version={version}, pid={process.Id}, silo={process.SiloAddress}, "
            + $"runtime={process.Provenance.InformationalVersion}, tfm={process.Provenance.TargetFramework}, "
            + $"framework={process.Provenance.FrameworkDescription}, sha256={process.Provenance.Sha256}, "
            + $"logs={process.LogDirectory}.");
        return process;
    }

    public async Task WaitForMembershipAsync(
        string phase,
        CompatibilityHostProcess[] activeProcesses,
        CompatibilityHostProcess[] deadProcesses,
        CancellationToken cancellationToken)
    {
        using var deadline = CreatePhaseDeadline(cancellationToken, out var deadlineUtc);
        var active = activeProcesses.Select(static process => process.SiloAddress).ToArray();
        var dead = deadProcesses.Select(static process => process.SiloAddress).ToArray();
        HostStatus[] statuses;
        try
        {
            statuses = await Task.WhenAll(
                activeProcesses.Select(
                    process => process.WaitForMembershipAsync(active, dead, deadlineUtc, deadline.Token)));
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested
            && deadline.IsCancellationRequested)
        {
            throw CreatePhaseTimeout(
                phase,
                $"local membership active=[{string.Join(", ", active)}], dead=[{string.Join(", ", dead)}]",
                exception);
        }

        _output.WriteLine(
            $"Phase '{phase}' complete: "
            + string.Join(
                "; ",
                statuses.Select(
                    static status =>
                        $"{status.SiloAddress}@v{status.Version}=["
                        + $"{string.Join(", ", status.Hosts.Select(host => $"{host.Address}:{host.Status}"))}]")));
    }

    public async Task<Dictionary<string, string>> WaitForDirectoryAgreementAsync(
        string phase,
        CompatibilityHostProcess[] processes,
        string[] keys,
        CancellationToken cancellationToken)
    {
        using var deadline = CreatePhaseDeadline(cancellationToken, out var deadlineUtc);
        var active = processes.Select(static process => process.SiloAddress).ToArray();
        DirectoryStatus[] snapshots;
        try
        {
            snapshots = await Task.WhenAll(
                processes.Select(
                    process => process.WaitForDirectoryAsync(keys, active, [], deadlineUtc, deadline.Token)));
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested
            && deadline.IsCancellationRequested)
        {
            throw CreatePhaseTimeout(
                phase,
                $"directory views for active=[{string.Join(", ", active)}]",
                exception);
        }

        var result = AssertDirectoryAgreement(phase, snapshots, keys);
        _output.WriteLine(
            $"Phase '{phase}' complete: {keys.Length} owners agreed; views=["
            + $"{string.Join(", ", snapshots.Select(snapshot => $"{snapshot.MembershipVersion}/{snapshot.DirectoryVersion}"))}].");
        return result;
    }

    public async Task<string[]> FindKeysOwnedByAsync(
        string phase,
        CompatibilityHostProcess observer,
        string owner,
        int count,
        string prefix,
        CompatibilityHostProcess[] activeProcesses,
        CompatibilityHostProcess[] deadProcesses,
        CancellationToken cancellationToken)
    {
        using var deadline = CreatePhaseDeadline(cancellationToken, out var deadlineUtc);
        var active = activeProcesses.Select(static process => process.SiloAddress).ToArray();
        var dead = deadProcesses.Select(static process => process.SiloAddress).ToArray();
        OwnedKeysStatus result;
        try
        {
            result = await observer.FindOwnedKeysAsync(
                owner,
                prefix,
                count,
                maxAttempts: 100_000,
                active,
                dead,
                deadlineUtc,
                deadline.Token);
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested
            && deadline.IsCancellationRequested)
        {
            throw CreatePhaseTimeout(
                phase,
                $"{count} deterministically enumerated keys owned by {owner}",
                exception);
        }

        Assert.Equal(count, result.Keys.Length);
        Assert.Equal(count, result.Keys.Distinct(StringComparer.Ordinal).Count());
        _output.WriteLine(
            $"Phase '{phase}' complete: selected {count} keys at membership/directory "
            + $"views {result.MembershipVersion}/{result.DirectoryVersion}.");
        return result.Keys;
    }

    public async Task<Dictionary<string, string>> AssertRegistrationsAsync(
        string phase,
        CompatibilityHostProcess[] processes,
        IReadOnlyDictionary<string, GrainPing> expected,
        CancellationToken cancellationToken)
    {
        using var deadline = CreatePhaseDeadline(cancellationToken, out var deadlineUtc);
        var active = processes.Select(static process => process.SiloAddress).ToArray();
        var keys = expected.Keys.ToArray();
        DirectoryStatus[] snapshots;
        try
        {
            snapshots = await Task.WhenAll(
                processes.Select(
                    process => process.WaitForDirectoryAsync(keys, active, [], deadlineUtc, deadline.Token)));
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested
            && deadline.IsCancellationRequested)
        {
            throw CreatePhaseTimeout(
                phase,
                $"owner-local registrations for {keys.Length} keys and active=[{string.Join(", ", active)}]",
                exception);
        }

        var owners = AssertDirectoryAgreement(phase, snapshots, keys);
        foreach (var key in keys)
        {
            var accepted = expected[key];
            var ownerRecord = snapshots
                .SelectMany(static snapshot => snapshot.Records)
                .Single(record => record.Key == key && record.LocalSilo == owners[key]);
            Assert.True(
                ownerRecord.LocalRecord is not null,
                $"{phase}: owner {owners[key]} has no local registration for '{key}'.");
            Assert.Equal(accepted.SiloAddress, ownerRecord.LocalRecordSilo);
            Assert.Equal(accepted.ActivationId, ownerRecord.LocalRecordActivationId);
        }

        _output.WriteLine(
            $"Phase '{phase}' complete: {keys.Length} owner-local records preserve accepted activation ids.");
        return owners;
    }

    public static void AssertRuntimeProvenance(
        CompatibilityHostProcess released,
        CompatibilityHostProcess current)
    {
        Assert.Equal(HostVersion.Released, released.Version);
        Assert.Equal(HostVersion.Current, current.Version);
        Assert.StartsWith("10.3.1", released.Provenance.InformationalVersion, StringComparison.Ordinal);
        foreach (var process in new[] { released, current })
        {
            Assert.Equal(AppContext.TargetFrameworkName, process.Provenance.TargetFramework);
            Assert.Equal(RuntimeInformation.FrameworkDescription, process.Provenance.FrameworkDescription);
            Assert.Equal(Environment.Version.ToString(), process.Provenance.RuntimeVersion);
            Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), process.Provenance.ProcessArchitecture);
        }

        var runnerRuntime = typeof(IClusterMembershipService).Assembly;
        var runnerRuntimeHash = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(runnerRuntime.Location)));
        Assert.Equal(runnerRuntimeHash, current.Provenance.Sha256);
        Assert.NotEqual(released.Provenance.Sha256, current.Provenance.Sha256);

        using var releaseDeps = JsonDocument.Parse(
            File.ReadAllText(Path.ChangeExtension(released.HostPath, ".deps.json")));
        var orleansPackages = releaseDeps.RootElement
            .GetProperty("libraries")
            .EnumerateObject()
            .Select(static item => item.Name)
            .Where(static name => name.StartsWith("Microsoft.Orleans.", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(orleansPackages);
        Assert.All(
            orleansPackages,
            package => Assert.EndsWith("/10.3.1", package, StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        var errors = new List<Exception>();
        foreach (var process in _processes.AsEnumerable().Reverse())
        {
            try
            {
                await process.DisposeAsync();
                _output.WriteLine($"Cleaned up {process.Name}; logs={process.LogDirectory}.");
            }
            catch (Exception exception)
            {
                errors.Add(new InvalidOperationException(
                    $"Cleanup failed for '{process.Name}'. {process.Describe()}",
                    exception));
            }
        }

        _portAllocator.Dispose();
        if (errors.Count > 0)
        {
            throw new AggregateException("One or more compatibility child processes failed cleanup.", errors);
        }
    }

    private static Dictionary<string, string> AssertDirectoryAgreement(
        string phase,
        DirectoryStatus[] snapshots,
        string[] keys)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var observedOwners = snapshots
                .Select(snapshot => snapshot.Records.Single(record => record.Key == key).Owner)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert.True(
                observedOwners.Length == 1 && observedOwners[0] is not null,
                $"{phase}: '{key}' owners=["
                + $"{string.Join(", ", observedOwners.Select(static owner => owner ?? "<null>"))}].");
            result[key] = observedOwners[0]!;
        }

        return result;
    }

    private static CancellationTokenSource CreatePhaseDeadline(
        CancellationToken cancellationToken,
        out DateTimeOffset deadlineUtc)
    {
        deadlineUtc = DateTimeOffset.UtcNow + GrainDirectoryProcessCompatibilityTests.PhaseTimeout;
        var result = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        result.CancelAfter(GrainDirectoryProcessCompatibilityTests.PhaseTimeout);
        return result;
    }

    private TimeoutException CreatePhaseTimeout(string phase, string expected, Exception exception) =>
        new(
            $"Timed out in phase '{phase}'. Expected: {expected}.{Environment.NewLine}"
            + string.Join(Environment.NewLine, _processes.Select(static process => process.Describe())),
            exception);

    private static string GetHostPath(HostVersion version, [CallerFilePath] string sourceFile = "")
    {
        var repositoryRoot = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (!File.Exists(Path.Combine(repositoryRoot.FullName, "Orleans.slnx")))
        {
            repositoryRoot = repositoryRoot.Parent
                ?? throw new InvalidOperationException($"Could not locate repository root from '{sourceFile}'.");
        }

        var framework = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Name;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var projectName = version switch
        {
            HostVersion.Released => "Orleans.GrainDirectory.Compatibility.ReleaseHost",
            HostVersion.Current => "Orleans.GrainDirectory.Compatibility.CurrentHost",
            _ => throw new ArgumentOutOfRangeException(nameof(version)),
        };
        var result = Path.Combine(
            repositoryRoot.FullName,
            "test",
            "TestInfrastructure",
            projectName,
            "bin",
            configuration,
            framework,
            $"{projectName}.dll");
        if (!File.Exists(result))
        {
            throw new FileNotFoundException(
                $"Compatibility host was not built for {configuration}/{framework}.",
                result);
        }

        return result;
    }
}

internal sealed class CompatibilityHostProcess : IAsyncDisposable
{
    private const string ResponsePrefix = "@@directory-compatibility:";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly ConcurrentDictionary<int, TaskCompletionSource<HostResponse>> _responses = new();
    private readonly SemaphoreSlim _commandWriteLock = new(1, 1);
    private readonly TaskCompletionSource _outputClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _errorClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Process _process;
    private readonly BoundedProcessLog _standardOutput;
    private readonly BoundedProcessLog _standardError;
    private int _nextCommandId;
    private int? _processId;
    private int? _cachedExitCode;
    private Exception? _callbackFailure;
    private bool _started;
    private bool _suspended;
    private bool _disposed;
    private bool _processDisposed;

    public CompatibilityHostProcess(
        HostVersion version,
        string name,
        string hostPath,
        string logDirectory,
        int siloPort,
        int gatewayPort,
        int primarySiloPort,
        string clusterId,
        string serviceId)
    {
        Version = version;
        Name = name;
        HostPath = hostPath;
        LogDirectory = logDirectory;
        _standardOutput = new(Path.Combine(logDirectory, $"{name}.stdout.log"));
        _standardError = new(Path.Combine(logDirectory, $"{name}.stderr.log"));
        _process = new Process
        {
            StartInfo = new ProcessStartInfo(
                Environment.GetEnvironmentVariable("ORLEANS_COMPAT_DOTNET_HOST") ?? "dotnet")
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(hostPath)!,
                ArgumentList =
                {
                    hostPath,
                    name,
                    siloPort.ToString(),
                    gatewayPort.ToString(),
                    primarySiloPort.ToString(),
                    clusterId,
                    serviceId,
                },
            },
            EnableRaisingEvents = true,
        };
    }

    public HostVersion Version { get; }

    public string Name { get; }

    public string HostPath { get; }

    public string LogDirectory { get; }

    public int Id => _processId is { } processId
        ? processId
        : throw new InvalidOperationException($"Process '{Name}' has not started.");

    public int ExitCode => TryGetExitCode() is { } exitCode
        ? exitCode
        : throw new InvalidOperationException($"Process '{Name}' has not exited.");

    public string SiloAddress { get; private set; } = string.Empty;

    public RuntimeProvenance Provenance { get; private set; } = null!;

    public string StandardError => _standardError.Tail;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var ready = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _responses[0] = ready;
        Subscribe();
        try
        {
            if (!_process.Start())
            {
                throw new InvalidOperationException($"Could not start compatibility host '{Name}'.");
            }

            _started = true;
            _processId = _process.Id;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            var response = await ready.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
            if (!response.Success)
            {
                throw new InvalidOperationException($"Host '{Name}' startup failed: {response.Error}");
            }

            var data = response.Data.Deserialize<ReadyResponse>(JsonOptions)!;
            SiloAddress = data.SiloAddress;
            Provenance = data.Provenance;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Failed to start compatibility host '{Name}'.{Environment.NewLine}{Describe()}",
                exception);
        }
        finally
        {
            _responses.TryRemove(0, out _);
        }
    }

    public async Task<HostStatus> WaitForMembershipAsync(
        string[] active,
        string[] dead,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken) =>
        (await SendAsync(
            new HostCommand(0, "membership", null, active, dead, deadlineUtc.ToUnixTimeMilliseconds()),
            cancellationToken)).Deserialize<HostStatus>(JsonOptions)!;

    public async Task<DirectoryStatus> WaitForDirectoryAsync(
        string[] keys,
        string[] active,
        string[] dead,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken) =>
        (await SendAsync(
            new HostCommand(0, "directory", keys, active, dead, deadlineUtc.ToUnixTimeMilliseconds()),
            cancellationToken)).Deserialize<DirectoryStatus>(JsonOptions)!;

    public async Task<OwnedKeysStatus> FindOwnedKeysAsync(
        string owner,
        string prefix,
        int count,
        int maxAttempts,
        string[] active,
        string[] dead,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken) =>
        (await SendAsync(
            new HostCommand(
                0,
                "find-owned",
                null,
                active,
                dead,
                deadlineUtc.ToUnixTimeMilliseconds(),
                owner,
                prefix,
                count,
                maxAttempts),
            cancellationToken)).Deserialize<OwnedKeysStatus>(JsonOptions)!;

    public async Task<GrainPing[]> PingAsync(string[] keys, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        var data = await SendAsync(
            new HostCommand(0, "ping", keys, null, null, deadline.ToUnixTimeMilliseconds()),
            cancellationToken);
        var responses = data.Deserialize<GrainPingResponse[]>(JsonOptions)!;
        if (responses.Length != keys.Length)
        {
            throw new InvalidOperationException(
                $"Host '{Name}' returned {responses.Length} pings for {keys.Length} keys.");
        }

        var expectedKeys = keys.ToHashSet(StringComparer.Ordinal);
        var actualKeys = responses.Select(static response => response.Key).ToHashSet(StringComparer.Ordinal);
        if (responses.Length != actualKeys.Count || !actualKeys.SetEquals(expectedKeys))
        {
            throw new InvalidOperationException(
                $"Host '{Name}' ping keys mismatch. Expected [{string.Join(", ", keys)}], "
                + $"actual [{string.Join(", ", responses.Select(static response => response.Key))}].");
        }

        return responses.Select(
            response =>
            {
                var identity = response.Identity.Split('|');
                if (identity.Length != 4 || !int.TryParse(identity[3], out var callCount))
                {
                    throw new InvalidOperationException(
                        $"Host '{Name}' returned invalid grain identity '{response.Identity}'.");
                }

                return new GrainPing(
                    response.Key,
                    identity[0],
                    identity[1],
                    identity[2],
                    callCount);
            }).ToArray();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started || _process.HasExited)
        {
            return;
        }

        var errors = await StopAndReapAsync(TimeSpan.FromSeconds(30), cancellationToken);
        if (errors.Count > 0)
        {
            throw new AggregateException($"Failed to stop compatibility host '{Name}'.", errors);
        }
    }

    public void Suspend()
    {
        EnsureRunning();
        var result = OperatingSystem.IsWindows()
            ? NativeMethods.NtSuspendProcess(_process.Handle)
            : NativeMethods.Kill(_process.Id, GetStopSignal());
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"Could not suspend child process '{Name}' (pid {_process.Id}); native result={result}, "
                + $"lastError={Marshal.GetLastPInvokeError()}.");
        }

        _suspended = true;
    }

    public void Resume()
    {
        if (!_started || !_suspended || _process.HasExited)
        {
            _suspended = false;
            return;
        }

        var result = OperatingSystem.IsWindows()
            ? NativeMethods.NtResumeProcess(_process.Handle)
            : NativeMethods.Kill(_process.Id, GetContinueSignal());
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"Could not resume child process '{Name}' (pid {_process.Id}); native result={result}, "
                + $"lastError={Marshal.GetLastPInvokeError()}.");
        }

        _suspended = false;
    }

    public async Task WaitForExitAsync(
        string phase,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        try
        {
            await _process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
            await DrainOutputAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Timed out in phase '{phase}'. Expected child '{Name}' (pid {_process.Id}) to exit. "
                + $"Actual: still running.{Environment.NewLine}{Describe()}",
                exception);
        }
    }

    public string Describe()
    {
        var processState = !_started
            ? "not-started"
            : TryGetExitCode() is { } exitCode
                ? $"exited({exitCode})"
                : $"running({_processId})";
        return $"Process {Name}: version={Version}, state={processState}, silo={SiloAddress}, suspended={_suspended}, "
            + $"stdout={_standardOutput.Path}, stderr={_standardError.Path}"
            + $"{Environment.NewLine}stdout tail:{Environment.NewLine}{_standardOutput.Tail}"
            + $"{Environment.NewLine}stderr tail:{Environment.NewLine}{_standardError.Tail}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var errors = new List<Exception>();
        if (_started)
        {
            errors.AddRange(await StopAndReapAsync(TimeSpan.FromSeconds(20), CancellationToken.None));
        }

        if (_callbackFailure is { } callbackFailure)
        {
            errors.Add(new InvalidOperationException(
                $"A redirected-output callback failed for '{Name}'.",
                callbackFailure));
        }

        TryCleanup(Unsubscribe, errors);
        TryCleanup(_commandWriteLock.Dispose, errors);
        CacheExitState();
        try
        {
            _process.Dispose();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        finally
        {
            _processDisposed = true;
        }

        TryCleanup(_standardOutput.Dispose, errors);
        TryCleanup(_standardError.Dispose, errors);
        if (errors.Count > 0)
        {
            throw new AggregateException($"Cleanup failed for compatibility host '{Name}'.", errors);
        }
    }

    private async Task<List<Exception>> StopAndReapAsync(
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken)
    {
        var errors = new List<Exception>();
        if (!_started)
        {
            return errors;
        }

        if (_suspended && !_process.HasExited)
        {
            try
            {
                Resume();
            }
            catch (Exception exception)
            {
                errors.Add(new InvalidOperationException($"Failed to resume '{Name}' before stopping.", exception));
            }
        }

        if (!_process.HasExited)
        {
            try
            {
                using var graceful = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                graceful.CancelAfter(gracefulTimeout);
                var deadline = DateTimeOffset.UtcNow + gracefulTimeout;
                await SendAsync(
                    new HostCommand(0, "stop", null, null, null, deadline.ToUnixTimeMilliseconds()),
                    graceful.Token);
                await _process.WaitForExitAsync(graceful.Token);
            }
            catch (Exception exception)
            {
                errors.Add(new InvalidOperationException($"Graceful stop failed for '{Name}'.", exception));
            }
        }

        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await _process.WaitForExitAsync(killTimeout.Token);
            }
            catch (Exception exception)
            {
                errors.Add(new InvalidOperationException($"Scoped kill/reap failed for '{Name}'.", exception));
            }
        }

        if (_process.HasExited)
        {
            try
            {
                await DrainOutputAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (Exception exception)
            {
                errors.Add(new InvalidOperationException($"Output drain failed for '{Name}'.", exception));
            }
        }

        return errors;
    }

    private async Task<JsonElement> SendAsync(HostCommand command, CancellationToken cancellationToken)
    {
        if (_callbackFailure is { } callbackFailure)
        {
            throw new InvalidOperationException(
                $"A redirected-output callback failed for host '{Name}'.",
                callbackFailure);
        }

        EnsureRunning();
        var id = Interlocked.Increment(ref _nextCommandId);
        command = command with { Id = id };
        var completion = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var added = false;
        var lockTaken = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (command.DeadlineUnixMilliseconds is { } deadlineMilliseconds)
        {
            var remaining = DateTimeOffset.FromUnixTimeMilliseconds(deadlineMilliseconds) - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                deadline.Cancel();
            }
            else
            {
                deadline.CancelAfter(remaining);
            }
        }

        try
        {
            if (!_responses.TryAdd(id, completion))
            {
                throw new InvalidOperationException($"Duplicate command id {id}.");
            }

            added = true;
            await _commandWriteLock.WaitAsync(deadline.Token);
            lockTaken = true;
            var serialized = JsonSerializer.Serialize(command);
            try
            {
                await _process.StandardInput.WriteLineAsync(serialized.AsMemory(), deadline.Token);
                await _process.StandardInput.FlushAsync(deadline.Token);
            }
            finally
            {
                _commandWriteLock.Release();
                lockTaken = false;
            }

            var response = await completion.Task.WaitAsync(deadline.Token);
            if (!response.Success)
            {
                throw new InvalidOperationException(
                    $"Host '{Name}' command '{command.Name}' failed: {response.Error}{Environment.NewLine}{Describe()}");
            }

            return response.Data;
        }
        finally
        {
            if (lockTaken)
            {
                _commandWriteLock.Release();
            }

            if (added)
            {
                _responses.TryRemove(id, out _);
            }
        }
    }

    private async Task DrainOutputAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drain.CancelAfter(timeout);
        await Task.WhenAll(_outputClosed.Task, _errorClosed.Task).WaitAsync(drain.Token);
    }

    private void EnsureStarted()
    {
        if (!_started)
        {
            throw new InvalidOperationException($"Process '{Name}' has not started.");
        }
    }

    private void EnsureRunning()
    {
        EnsureStarted();
        if (_process.HasExited)
        {
            throw new InvalidOperationException(
                $"Host '{Name}' exited with code {_process.ExitCode}.{Environment.NewLine}{Describe()}");
        }
    }

    private void Subscribe()
    {
        _process.OutputDataReceived += OnOutput;
        _process.ErrorDataReceived += OnError;
        _process.Exited += OnExited;
    }

    private void Unsubscribe()
    {
        _process.OutputDataReceived -= OnOutput;
        _process.ErrorDataReceived -= OnError;
        _process.Exited -= OnExited;
    }

    private void OnOutput(object sender, DataReceivedEventArgs eventArgs)
    {
        try
        {
            if (eventArgs.Data is not { } line)
            {
                _outputClosed.TrySetResult();
                return;
            }

            _standardOutput.Append(line);
            if (!line.StartsWith(ResponsePrefix, StringComparison.Ordinal))
            {
                return;
            }

            var response = JsonSerializer.Deserialize<HostResponse>(line[ResponsePrefix.Length..], JsonOptions)
                ?? throw new JsonException("The host response was null.");
            if (_responses.TryGetValue(response.Id, out var completion))
            {
                completion.TrySetResult(response);
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure(exception);
        }
    }

    private void OnError(object sender, DataReceivedEventArgs eventArgs)
    {
        try
        {
            if (eventArgs.Data is { } line)
            {
                _standardError.Append(line);
            }
            else
            {
                _errorClosed.TrySetResult();
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure(exception);
        }
    }

    private void OnExited(object? sender, EventArgs eventArgs)
    {
        CacheExitState();
        var exception = new InvalidOperationException(
            $"Host '{Name}' exited with code {_cachedExitCode}.{Environment.NewLine}{Describe()}");
        foreach (var completion in _responses.Values)
        {
            completion.TrySetException(exception);
        }
    }

    private void RecordCallbackFailure(Exception exception)
    {
        Interlocked.CompareExchange(ref _callbackFailure, exception, null);
        foreach (var completion in _responses.Values)
        {
            completion.TrySetException(exception);
        }
    }

    private int? TryGetExitCode()
    {
        if (_cachedExitCode is { } cached)
        {
            return cached;
        }

        if (!_started || _processDisposed)
        {
            return null;
        }

        try
        {
            if (_process.HasExited)
            {
                _cachedExitCode = _process.ExitCode;
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return _cachedExitCode;
    }

    private void CacheExitState() => _ = TryGetExitCode();

    private static void TryCleanup(Action action, List<Exception> errors)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

    private static int GetStopSignal() =>
        OperatingSystem.IsLinux()
            ? 19
            : OperatingSystem.IsMacOS()
                ? 17
                : throw new PlatformNotSupportedException(
                    "Process suspension is supported only on Windows, Linux, and macOS.");

    private static int GetContinueSignal() =>
        OperatingSystem.IsLinux()
            ? 18
            : OperatingSystem.IsMacOS()
                ? 19
                : throw new PlatformNotSupportedException(
                    "Process resumption is supported only on Windows, Linux, and macOS.");

    private sealed record HostCommand(
        int Id,
        string Name,
        string[]? Keys,
        string[]? Active,
        string[]? Dead,
        long? DeadlineUnixMilliseconds,
        string? Owner = null,
        string? Prefix = null,
        int? Count = null,
        int? MaxAttempts = null);

    private sealed record HostResponse(int Id, bool Success, string? Error, JsonElement Data);

    private sealed record ReadyResponse(string SiloAddress, RuntimeProvenance Provenance);

    private sealed record GrainPingResponse(string Key, string Identity);

    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054
        [DllImport("ntdll.dll")]
        internal static extern int NtSuspendProcess(nint processHandle);

        [DllImport("ntdll.dll")]
        internal static extern int NtResumeProcess(nint processHandle);

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        internal static extern int Kill(int processId, int signal);
#pragma warning restore SYSLIB1054
    }
}

internal sealed class BoundedProcessLog : IDisposable
{
    private const int MaxTailCharacters = 64 * 1024;
    private readonly Queue<string> _tail = new();
    private readonly StreamWriter _writer;
    private int _tailCharacters;

    public BoundedProcessLog(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    public string Path { get; }

    public string Tail
    {
        get
        {
            lock (_tail)
            {
                return string.Join(Environment.NewLine, _tail);
            }
        }
    }

    public void Append(string line)
    {
        lock (_tail)
        {
            _writer.WriteLine(line);
            _tail.Enqueue(line);
            _tailCharacters += line.Length + Environment.NewLine.Length;
            while (_tailCharacters > MaxTailCharacters && _tail.TryDequeue(out var removed))
            {
                _tailCharacters -= removed.Length + Environment.NewLine.Length;
            }
        }
    }

    public void Dispose()
    {
        lock (_tail)
        {
            _writer.Dispose();
        }
    }
}

internal sealed record RuntimeProvenance(
    string AssemblyName,
    string AssemblyVersion,
    string InformationalVersion,
    string FileVersion,
    string ProductVersion,
    string Location,
    string Sha256,
    string FrameworkDescription,
    string? TargetFramework,
    string RuntimeVersion,
    string ProcessArchitecture);

internal sealed record HostStatus(
    string SiloAddress,
    long Version,
    HostMembershipEntry[] Hosts);

internal sealed record HostMembershipEntry(string Address, string Status);

internal sealed record DirectoryStatus(
    long MembershipVersion,
    long DirectoryVersion,
    DirectoryRecord[] Records);

internal sealed record OwnedKeysStatus(
    long MembershipVersion,
    long DirectoryVersion,
    string[] Keys);

internal sealed record DirectoryRecord(
    string Key,
    string LocalSilo,
    string? Owner,
    string? LocalRecord,
    string? LocalRecordSilo,
    string? LocalRecordActivationId);

internal sealed record GrainPing(
    string Key,
    string SiloAddress,
    string ActivationId,
    string InstanceId,
    int CallCount);
