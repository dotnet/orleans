using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace Orleans.Dissemination.IntegrationHarness;

internal sealed record AssemblyManifest(string AssemblyVersion, string Sha256);
internal sealed record BuildManifest(
    string Runtime,
    string SourceRevision,
    string AssemblyName,
    string AssemblyVersion,
    string Sha256,
    Dictionary<string, AssemblyManifest> Assemblies);

internal sealed class ProcessCluster : IAsyncDisposable
{
    public const string Baseline = "6739589254b746a8790cf53524e6abe372bb53d4";
    private readonly List<SiloProcess> _all = [];
    private readonly string _membershipDirectory;
    private int _sequence;

    public ProcessCluster(string scenario, bool fastRecovery = true)
    {
        var artifactRoot = Environment.GetEnvironmentVariable("ORLEANS_DISSEMINATION_RESULTS")
            ?? throw new InvalidOperationException("Run .github/scripts/dissemination-evidence.ps1 first, or set ORLEANS_DISSEMINATION_{OLD,NEW,RESULTS}.");
        Directory = Path.Combine(Path.GetFullPath(artifactRoot), $"{scenario}-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
        _membershipDirectory = Path.Combine(Directory, "membership");
        System.IO.Directory.CreateDirectory(_membershipDirectory);
        FastRecovery = fastRecovery;
    }

    public string Directory { get; }
    public bool FastRecovery { get; }
    public IReadOnlyList<SiloProcess> All => _all;
    public SiloProcess[] Active => _all.Where(node => !node.Stopped).ToArray();

    public async Task<SiloProcess> Start(string runtime, bool enabled, int? port = null)
    {
        var binaryDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable($"ORLEANS_DISSEMINATION_{runtime.ToUpperInvariant()}")
            ?? throw new InvalidOperationException($"Missing {runtime} published binary directory."));
        var manifest = JsonSerializer.Deserialize<BuildManifest>(await File.ReadAllTextAsync(Path.Combine(binaryDirectory, "manifest.json")))!;
        Assert.Equal(runtime, manifest.Runtime);
        if (runtime == "Old")
        {
            Assert.Equal(Baseline, manifest.SourceRevision);
        }
        else
        {
            Assert.NotEqual(Baseline, manifest.SourceRevision);
        }

        var name = $"{runtime}-{_sequence++}-{(enabled ? "enabled" : "default")}";
        var node = new SiloProcess(
            binaryDirectory,
            new(name, Path.GetFileName(Directory), _membershipDirectory, port ?? AvailablePort(), Environment.ProcessId, enabled, FastRecovery),
            Directory,
            manifest);
        _all.Add(node);
        await node.Start();
        Assert.DoesNotContain(_all.Where(other => !ReferenceEquals(other, node) && !other.Stopped),
            other => other.Last.Identity.ProcessId == node.Last.Identity.ProcessId);
        return node;
    }

    public async Task Stabilize()
    {
        var expected = Active.Select(node => node.Last.Address).Order(StringComparer.Ordinal).ToArray();
        await Eventually("every process has the exact active membership", async () =>
        {
            var snapshots = await Task.WhenAll(Active.Select(node => node.Send("refresh")));
            return snapshots.All(snapshot => snapshot.ActiveMembers.SequenceEqual(expected));
        });
    }

    public async Task AssertControlRpcs(bool allPairs = true)
    {
        var nodes = Active;
        for (var index = 0; index < nodes.Length; index++)
        {
            var peers = allPairs ? nodes.Where(peer => !ReferenceEquals(peer, nodes[index])) : [nodes[(index + 1) % nodes.Length]];
            foreach (var peer in peers)
            {
                var result = await nodes[index].Send("echo", peer: peer.Last.Address);
                Assert.Equal(peer.Last.Identity.ProcessId, result.RemoteProcessId);
                Assert.NotEqual(nodes[index].Last.Identity.ProcessId, result.RemoteProcessId);
            }
        }
    }

    public async Task<double> PublishAndConverge()
    {
        var clock = Stopwatch.StartNew();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in Active)
        {
            var previous = node.Last.LoadVersions.GetValueOrDefault(node.Last.Address);
            var snapshot = await node.Send("publish");
            Assert.True(snapshot.LoadVersions[snapshot.Address] > previous, "The production publisher did not create a new sample.");
            expected.Add(snapshot.Address, snapshot.Load[snapshot.Address]);
        }

        await Eventually("exact production load state at every active process", async () =>
        {
            var snapshots = await Task.WhenAll(Active.Select(node => node.Send("snapshot")));
            return snapshots.All(snapshot => expected.All(pair =>
                snapshot.Load.TryGetValue(pair.Key, out var actual) && StringComparer.Ordinal.Equals(pair.Value, actual)));
        }, expected: expected);
        return clock.Elapsed.TotalMilliseconds;
    }

    public async Task HealPartition(SiloProcess partitioned)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var cancellationToken = deadline.Token;
        var phase = "retiring partitioned connections at both endpoints";
        try
        {
            var nodes = Active;
            // Drain admitted middleware, including unfinished handshakes, before reopening any affected endpoint.
            await Task.WhenAll(nodes.Select(node => node.Send(
                cancellationToken,
                "drain-connections",
                peer: ReferenceEquals(node, partitioned) ? null : partitioned.Last.Address)));
            await Task.WhenAll(nodes.Select(node => node.Send(cancellationToken, "resume-connections")));
            phase = "bidirectional control RPCs after transport healing";
            await Eventually(phase, async () =>
            {
                foreach (var node in nodes)
                {
                    foreach (var peer in nodes.Where(peer => !ReferenceEquals(peer, node)))
                    {
                        var result = await node.Send(cancellationToken, "probe-echo", peer: peer.Last.Address);
                        if (result.RemoteProcessId is not { } processId)
                        {
                            Assert.NotNull(result.ProbeError);
                            return false;
                        }

                        Assert.Equal(peer.Last.Identity.ProcessId, processId);
                        Assert.NotEqual(node.Last.Identity.ProcessId, processId);
                    }
                }

                return true;
            });
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            await Save("failure-state.json", new { Phase = phase, Nodes = _all.Select(node => node.Last) });
            Assert.Fail($"Timed out during {phase}. Exact snapshots/logs: {Directory}");
        }
    }

    public async Task Eventually(string phase, Func<Task<bool>> assertion, TimeSpan? timeout = null, object? expected = null)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (await assertion())
            {
                return;
            }

            if (clock.Elapsed > (timeout ?? TimeSpan.FromSeconds(60)))
            {
                await Save("failure-state.json", new { Phase = phase, Expected = expected, Nodes = _all.Select(node => node.Last) });
                Assert.Fail($"Timed out during {phase}. Exact snapshots/logs: {Directory}");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    public Task Save(string filename, object value) =>
        File.WriteAllTextAsync(Path.Combine(Directory, filename), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    public async ValueTask DisposeAsync()
    {
        List<Exception> failures = [];
        foreach (var node in _all.AsEnumerable().Reverse())
        {
            try
            {
                await node.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        await Save("final-processes.json", _all.Select(node => new { node.Configuration, node.Last, node.ShutdownMilliseconds, node.ForcedKill }));
        if (failures.Count > 0)
        {
            throw new AggregateException("One or more silos exceeded shutdown bounds.", failures);
        }
    }

    private static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

internal sealed class SiloProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _configurationPath;
    private readonly string _logPath;
    private readonly string _errorPath;
    private readonly BuildManifest _manifest;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Response>> _pending = new();
    private readonly SemaphoreSlim _commandLock = new(1);
    private readonly TaskCompletionSource<Response> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _output = Task.CompletedTask;
    private Task _error = Task.CompletedTask;
    private int _commandId;
    private bool _started;
    private bool _disposed;

    public SiloProcess(string binaryDirectory, NodeConfiguration configuration, string artifacts, BuildManifest manifest)
    {
        Configuration = configuration;
        _manifest = manifest;
        _configurationPath = Path.Combine(artifacts, configuration.Name + ".configuration.json");
        _logPath = Path.Combine(artifacts, configuration.Name + ".stdout.jsonl");
        _errorPath = Path.Combine(artifacts, configuration.Name + ".stderr.log");
        _process = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = binaryDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { Path.Combine(binaryDirectory, "Orleans.Runtime.Tests.dll"), "--silo", _configurationPath },
            },
        };
        _pending[0] = _ready;
    }

    public NodeConfiguration Configuration { get; }
    public NodeSnapshot Last { get; private set; } = null!;
    public NodeSnapshot Initial { get; private set; } = null!;
    public bool Stopped { get; private set; }
    public bool ForcedKill { get; private set; }
    public double ShutdownMilliseconds { get; private set; }

    public async Task Start()
    {
        await File.WriteAllTextAsync(_configurationPath, JsonSerializer.Serialize(Configuration));
        Assert.True(_process.Start(), "Failed to launch silo OS process.");
        _started = true;
        _output = PumpOutput();
        _error = PumpErrors();
        var ready = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(100), TestContext.Current.CancellationToken);
        Assert.Null(ready.Error);
        Last = ready.Snapshot!;
        Initial = Last;
        Assert.Equal(_process.Id, Last.Identity.ProcessId);
        Assert.Equal(_manifest.Runtime, Last.Identity.Runtime);
        Assert.Equal(_manifest.SourceRevision, Last.Identity.SourceRevision);
        Assert.Equal(_manifest.AssemblyName, Last.Identity.AssemblyName);
        Assert.Equal(_manifest.AssemblyVersion, Last.Identity.AssemblyVersion);
        Assert.Equal(_manifest.Sha256, Last.Identity.Sha256, ignoreCase: true);
        Assert.Contains(_manifest.SourceRevision, Last.Identity.InformationalVersion, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(_manifest.Runtime == "New", Last.Identity.HasDissemination);
        Assert.Equal(Configuration.Enabled, Last.Enabled);
        Assert.Equal(Configuration.Enabled, Last.NamespaceEnabled);
        var actualDirectory = Path.GetDirectoryName(Path.GetFullPath(Last.Identity.AssemblyPath));
        Assert.Equal(Path.GetFullPath(_process.StartInfo.WorkingDirectory), actualDirectory);
        foreach (var name in new[] { "Orleans.Runtime", "Orleans.Core", "Orleans.Core.Abstractions", "Orleans.Serialization" })
        {
            Assert.Contains(name, Last.Identity.Assemblies.Keys);
        }

        foreach (var (name, proof) in Last.Identity.Assemblies)
        {
            Assert.True(_manifest.Assemblies.TryGetValue(name, out var expected), $"Loaded unexpected assembly {name}.");
            Assert.Equal(expected!.AssemblyVersion, proof.Version);
            Assert.Equal(expected.Sha256, proof.Sha256, ignoreCase: true);
            Assert.Equal(Path.GetFullPath(_process.StartInfo.WorkingDirectory), Path.GetDirectoryName(Path.GetFullPath(proof.Path)));
        }
    }

    public Task<NodeSnapshot> Send(string operation, string? peer = null, int count = 1, bool value = false, long version = 0) =>
        Send(TestContext.Current.CancellationToken, operation, peer, count, value, version);

    public async Task<NodeSnapshot> Send(CancellationToken cancellationToken, string operation, string? peer = null, int count = 1, bool value = false, long version = 0)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            var command = new Command(Interlocked.Increment(ref _commandId), operation, peer, count, value, version);
            var completed = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[command.Id] = completed;
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
            var response = await completed.Task.WaitAsync(TimeSpan.FromSeconds(70), cancellationToken);
            Assert.True(response.Error is null, $"{Configuration.Name}/{operation}: {response.Error}; {_errorPath}");
            Last = response.Snapshot!;
            return Last;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task Stop()
    {
        if (Stopped || !_started)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        try
        {
            if (!_process.HasExited)
            {
                // Cleanup has its own bound, independent of a cancelled xUnit test token.
                await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new Command(-2, "stop")));
                await _process.StandardInput.FlushAsync();
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
            }

            await Task.WhenAll(_output, _error).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, _process.ExitCode);
        }
        catch
        {
            if (!_process.HasExited)
            {
                ForcedKill = true;
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            throw;
        }
        finally
        {
            ShutdownMilliseconds = clock.Elapsed.TotalMilliseconds;
            Stopped = true;
        }
    }

    private async Task PumpOutput()
    {
        await using var log = File.CreateText(_logPath);
        while (await _process.StandardOutput.ReadLineAsync() is { } line)
        {
            await log.WriteLineAsync(line);
            await log.FlushAsync();
            if (!line.StartsWith("HARNESS ", StringComparison.Ordinal))
            {
                continue;
            }

            var response = JsonSerializer.Deserialize<Response>(line["HARNESS ".Length..])!;
            if (response.Snapshot is not null)
            {
                Last = response.Snapshot;
            }

            if (_pending.TryRemove(response.Id, out var completed))
            {
                completed.TrySetResult(response);
            }
        }

        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(new InvalidOperationException($"Silo stdout closed; inspect {_errorPath}."));
        }
    }

    private async Task PumpErrors()
    {
        await using var log = File.CreateText(_errorPath);
        while (await _process.StandardError.ReadLineAsync() is { } line)
        {
            await log.WriteLineAsync(line);
            await log.FlushAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await Stop();
        }
        finally
        {
            _process.Dispose();
            _commandLock.Dispose();
        }
    }
}
