using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Placement;
using Orleans.Runtime;

namespace Orleans.GrainDirectory.Compatibility;

[Alias("directory-compatibility-probe")]
public interface IDirectoryCompatibilityProbe : IGrainWithStringKey
{
    [Alias("Ping")]
    ValueTask<string> Ping();
}

[GrainType("directory-compatibility-probe")]
[PreferLocalPlacement]
public sealed class DirectoryCompatibilityProbe : Grain, IDirectoryCompatibilityProbe
{
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly ILocalSiloDetails _localSiloDetails;
    private int _callCount;

    public DirectoryCompatibilityProbe(ILocalSiloDetails localSiloDetails)
    {
        _localSiloDetails = localSiloDetails;
    }

    public ValueTask<string> Ping() =>
        new(
            $"{_localSiloDetails.SiloAddress.ToParsableString()}|"
            + $"{GrainContext.ActivationId}|{_instanceId}|{++_callCount}");
}

public static class Program
{
    private const string ResponsePrefix = "@@directory-compatibility:";
    private static readonly object ResponseLock = new();

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 6)
        {
            Console.Error.WriteLine("Expected: silo-name silo-port gateway-port primary-silo-port cluster-id service-id");
            return 2;
        }

        var siloName = args[0];
        var siloPort = int.Parse(args[1]);
        var gatewayPort = int.Parse(args[2]);
        var primarySiloPort = int.Parse(args[3]);
        var clusterId = args[4];
        var serviceId = args[5];

        using var shutdown = new CancellationTokenSource();
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders().AddConsole().SetMinimumLevel(LogLevel.Warning))
            .UseOrleans(siloBuilder =>
            {
                siloBuilder
                    .Configure<ClusterOptions>(options =>
                    {
                        options.ClusterId = clusterId;
                        options.ServiceId = serviceId;
                    })
                    .Configure<SiloOptions>(options => options.SiloName = siloName)
                    .Configure<GrainDirectoryOptions>(options => options.PartitionsPerSilo = 4)
                    .Configure<EndpointOptions>(options =>
                    {
                        options.AdvertisedIPAddress = IPAddress.Loopback;
                        options.SiloPort = siloPort;
                        options.GatewayPort = gatewayPort;
                    })
                    .Configure<ClusterMembershipOptions>(options =>
                    {
                        options.ProbeTimeout = TimeSpan.FromSeconds(1);
                        options.NumMissedProbesLimit = 2;
                        options.TableRefreshTimeout = TimeSpan.FromSeconds(2);
                        options.DeathVoteExpirationTimeout = TimeSpan.FromSeconds(20);
                        options.IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(1);
                        options.ExtendProbeTimeoutDuringDegradation = false;
                    })
                    .UseDevelopmentClustering(new IPEndPoint(IPAddress.Loopback, primarySiloPort))
                    .AddMemoryGrainStorage("Default");
#pragma warning disable ORLEANSEXP003
                siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
            })
            .Build();

        try
        {
            await host.StartAsync(shutdown.Token);
            var services = host.Services;
            var localSilo = services.GetRequiredService<ILocalSiloDetails>().SiloAddress;
            WriteResponse(new HostResponse(
                0,
                true,
                null,
                new
                {
                    Event = "ready",
                    SiloAddress = localSilo.ToParsableString(),
                    Provenance = GetRuntimeProvenance(),
                }));

            await RunCommandLoopAsync(services, shutdown);
            await host.StopAsync(CancellationToken.None);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task RunCommandLoopAsync(IServiceProvider services, CancellationTokenSource shutdown)
    {
        var commands = new ConcurrentDictionary<int, Task>();
        // Console.In's synchronized reader can block before returning its task.
        using var input = new StreamReader(Console.OpenStandardInput(), Console.InputEncoding);
        while (!shutdown.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(shutdown.Token).AsTask().WaitAsync(shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            HostCommand? command;
            try
            {
                command = JsonSerializer.Deserialize<HostCommand>(line)
                    ?? throw new InvalidOperationException("The command was empty.");
            }
            catch (Exception exception)
            {
                WriteResponse(new HostResponse(-1, false, exception.ToString(), null));
                continue;
            }

            var task = HandleCommandAsync(services, command, shutdown);
            if (!commands.TryAdd(command.Id, task))
            {
                WriteResponse(new HostResponse(
                    command.Id,
                    false,
                    $"A command with id {command.Id} is already running.",
                    null));
                continue;
            }

            _ = task.ContinueWith(
                (completedTask, state) =>
                {
                    var (map, id) = ((ConcurrentDictionary<int, Task>, int))state!;
                    map.TryRemove(id, out _);
                },
                (commands, command.Id),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        shutdown.Cancel();
        await Task.WhenAll(commands.Values);
    }

    private static async Task HandleCommandAsync(
        IServiceProvider services,
        HostCommand command,
        CancellationTokenSource shutdown)
    {
        var shouldStop = string.Equals(command.Name, "stop", StringComparison.Ordinal);
        try
        {
            using var deadline = CreateDeadline(command, shutdown.Token);
            var result = await ExecuteCommandAsync(services, command, deadline.Token);
            WriteResponse(new HostResponse(command.Id, true, null, result));
        }
        catch (Exception exception)
        {
            WriteResponse(new HostResponse(command.Id, false, exception.ToString(), null));
        }
        finally
        {
            if (shouldStop)
            {
                shutdown.Cancel();
            }
        }
    }

    private static CancellationTokenSource CreateDeadline(HostCommand command, CancellationToken shutdownToken)
    {
        var result = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        if (command.DeadlineUnixMilliseconds is { } deadline)
        {
            var remaining = DateTimeOffset.FromUnixTimeMilliseconds(deadline) - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                result.Cancel();
            }
            else
            {
                result.CancelAfter(remaining);
            }
        }

        return result;
    }

    private static async Task<object?> ExecuteCommandAsync(
        IServiceProvider services,
        HostCommand command,
        CancellationToken cancellationToken)
    {
        return command.Name switch
        {
            "membership" => await WaitForMembershipAsync(
                services,
                command.Active ?? [],
                command.Dead ?? [],
                cancellationToken),
            "directory" => await WaitForDirectoryAsync(
                services,
                command.Keys ?? [],
                command.Active ?? [],
                command.Dead ?? [],
                cancellationToken),
            "find-owned" => await FindOwnedKeysAsync(
                services,
                command.Owner ?? throw new InvalidOperationException("Owner is required."),
                command.Prefix ?? throw new InvalidOperationException("Prefix is required."),
                command.Count ?? throw new InvalidOperationException("Count is required."),
                command.MaxAttempts ?? throw new InvalidOperationException("MaxAttempts is required."),
                command.Active ?? [],
                command.Dead ?? [],
                cancellationToken),
            "ping" => await PingAsync(services, command.Keys ?? [], cancellationToken),
            "stop" => null,
            _ => throw new InvalidOperationException($"Unknown command '{command.Name}'."),
        };
    }

    private static async Task<object> WaitForMembershipAsync(
        IServiceProvider services,
        string[] active,
        string[] dead,
        CancellationToken cancellationToken)
    {
        var membership = services.GetRequiredService<IClusterMembershipService>();
        var snapshot = membership.CurrentSnapshot;
        if (!IsExpectedMembership(snapshot, active, dead))
        {
            try
            {
                await foreach (var update in membership.MembershipUpdates.WithCancellation(cancellationToken))
                {
                    snapshot = update;
                    if (IsExpectedMembership(snapshot, active, dead))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Local membership did not reach active=[{string.Join(", ", active)}], "
                    + $"dead=[{string.Join(", ", dead)}]. Actual: {DescribeMembership(snapshot)}.",
                    exception);
            }
        }

        if (!IsExpectedMembership(snapshot, active, dead))
        {
            throw new InvalidOperationException(
                $"Membership update stream completed before the expected state. Actual: {DescribeMembership(snapshot)}.");
        }

        return CreateMembershipResult(services, snapshot);
    }

    private static async Task<object> WaitForDirectoryAsync(
        IServiceProvider services,
        string[] keys,
        string[] active,
        string[] dead,
        CancellationToken cancellationToken)
    {
        await WaitForMembershipAsync(services, active, dead, cancellationToken);
        var membership = services.GetRequiredService<IClusterMembershipService>().CurrentSnapshot;
        var directoryViewVersion = await RefreshDirectoryViewAsync(
            services,
            membership.Version,
            cancellationToken);
        var records = await GetDirectoryRecordsAsync(services, keys, cancellationToken);
        return new
        {
            MembershipVersion = membership.Version.Value,
            DirectoryVersion = directoryViewVersion,
            Records = records,
        };
    }

    private static async Task<object> PingAsync(
        IServiceProvider services,
        string[] keys,
        CancellationToken cancellationToken)
    {
        var grainFactory = services.GetRequiredService<IGrainFactory>();
        var results = new List<object>(keys.Length);
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var grain = grainFactory.GetGrain<IDirectoryCompatibilityProbe>(key);
            results.Add(new
            {
                Key = key,
                Identity = await grain.Ping().AsTask().WaitAsync(cancellationToken),
            });
        }

        return results;
    }

    private static async Task<object> FindOwnedKeysAsync(
        IServiceProvider services,
        string owner,
        string prefix,
        int count,
        int maxAttempts,
        string[] active,
        string[] dead,
        CancellationToken cancellationToken)
    {
        if (count <= 0 || maxAttempts < count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                $"Expected 0 < count <= maxAttempts, actual count={count}, maxAttempts={maxAttempts}.");
        }

        await WaitForMembershipAsync(services, active, dead, cancellationToken);
        var membership = services.GetRequiredService<IClusterMembershipService>().CurrentSnapshot;
        var directoryVersion = await RefreshDirectoryViewAsync(services, membership.Version, cancellationToken);
        var grainFactory = services.GetRequiredService<IGrainFactory>();
        var runtimeAssembly = GetRuntimeAssembly();
        var directoryType = runtimeAssembly.GetType(
            "Orleans.Runtime.GrainDirectory.DistributedGrainDirectory",
            throwOnError: true)!;
        var hooksType = directoryType.GetNestedType("ITestHooks", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(directoryType.FullName, "ITestHooks");
        var getOwner = hooksType.GetMethod("GetPrimaryForGrain")
            ?? throw new MissingMethodException(hooksType.FullName, "GetPrimaryForGrain");
        var directory = services.GetRequiredService(directoryType);
        var keys = new List<string>(count);
        for (var index = 0; index < maxAttempts && keys.Count < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"{prefix}-{index}";
            var grainId = grainFactory.GetGrain<IDirectoryCompatibilityProbe>(key).GetGrainId();
            if (getOwner.Invoke(directory, [grainId]) is SiloAddress actualOwner
                && actualOwner.ToParsableString() == owner)
            {
                keys.Add(key);
            }
        }

        if (keys.Count != count)
        {
            throw new InvalidOperationException(
                $"Found {keys.Count} of {count} keys owned by {owner} after deterministically checking "
                + $"{maxAttempts} keys at directory view {directoryVersion}.");
        }

        return new
        {
            MembershipVersion = membership.Version.Value,
            DirectoryVersion = directoryVersion,
            Keys = keys.ToArray(),
        };
    }

    private static async Task<object[]> GetDirectoryRecordsAsync(
        IServiceProvider services,
        string[] keys,
        CancellationToken cancellationToken)
    {
        var grainFactory = services.GetRequiredService<IGrainFactory>();
        var runtimeAssembly = GetRuntimeAssembly();
        var directoryType = runtimeAssembly.GetType(
            "Orleans.Runtime.GrainDirectory.DistributedGrainDirectory",
            throwOnError: true)!;
        var hooksType = directoryType.GetNestedType("ITestHooks", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(directoryType.FullName, "ITestHooks");
        var getOwner = hooksType.GetMethod("GetPrimaryForGrain")
            ?? throw new MissingMethodException(hooksType.FullName, "GetPrimaryForGrain");
        var getLocalRecord = hooksType.GetMethod("GetLocalRecord")
            ?? throw new MissingMethodException(hooksType.FullName, "GetLocalRecord");
        var directory = services.GetRequiredService(directoryType);
        var localSilo = services.GetRequiredService<ILocalSiloDetails>().SiloAddress.ToParsableString();
        var results = new List<object>(keys.Length);

        foreach (var key in keys)
        {
            var grainId = grainFactory.GetGrain<IDirectoryCompatibilityProbe>(key).GetGrainId();
            var owner = getOwner.Invoke(directory, [grainId]) as SiloAddress;
            var localRecordTask = (Task)getLocalRecord.Invoke(directory, [grainId])!;
            await localRecordTask.WaitAsync(cancellationToken);
            var localRecord = localRecordTask.GetType().GetProperty("Result")!.GetValue(localRecordTask);
            var localRecordSilo = localRecord?
                .GetType()
                .GetProperty("SiloAddress")?
                .GetValue(localRecord) as SiloAddress;
            var localRecordActivationId = localRecord?
                .GetType()
                .GetProperty("ActivationId")?
                .GetValue(localRecord)?
                .ToString();
            results.Add(new
            {
                Key = key,
                LocalSilo = localSilo,
                Owner = owner?.ToParsableString(),
                LocalRecord = localRecord?.ToString(),
                LocalRecordSilo = localRecordSilo?.ToParsableString(),
                LocalRecordActivationId = localRecordActivationId,
            });
        }

        return results.ToArray();
    }

    private static async Task<long> RefreshDirectoryViewAsync(
        IServiceProvider services,
        MembershipVersion targetVersion,
        CancellationToken cancellationToken)
    {
        var directoryMembershipType = GetRuntimeAssembly().GetType(
            "Orleans.Runtime.GrainDirectory.DirectoryMembershipService",
            throwOnError: true)!;
        var directoryMembership = services.GetRequiredService(directoryMembershipType);
        var currentViewProperty = directoryMembershipType.GetProperty("CurrentView")
            ?? throw new MissingMemberException(directoryMembershipType.FullName, "CurrentView");
        var currentView = currentViewProperty.GetValue(directoryMembership)!;
        var currentVersion = GetMembershipVersionValue(currentView);
        if (currentVersion < targetVersion.Value)
        {
            var refresh = directoryMembershipType.GetMethod(
                "RefreshViewAsync",
                BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMethodException(directoryMembershipType.FullName, "RefreshViewAsync");
            await AwaitBoxedValueTaskAsync(
                refresh.Invoke(directoryMembership, [targetVersion, cancellationToken])
                    ?? throw new InvalidOperationException("Directory refresh returned null."));
            currentView = currentViewProperty.GetValue(directoryMembership)!;
            currentVersion = GetMembershipVersionValue(currentView);
        }

        if (currentVersion < targetVersion.Value)
        {
            throw new InvalidOperationException(
                $"Directory view {currentVersion} is behind local membership view {targetVersion.Value}.");
        }

        return currentVersion;
    }

    private static async Task AwaitBoxedValueTaskAsync(object valueTask)
    {
        var asTask = valueTask.GetType().GetMethod("AsTask", Type.EmptyTypes)
            ?? throw new MissingMethodException(valueTask.GetType().FullName, "AsTask");
        await (Task)asTask.Invoke(valueTask, null)!;
    }

    private static long GetMembershipVersionValue(object directoryView)
    {
        var version = directoryView.GetType().GetProperty("Version")!.GetValue(directoryView)!;
        return (long)version.GetType().GetProperty("Value")!.GetValue(version)!;
    }

    private static bool IsExpectedMembership(
        ClusterMembershipSnapshot snapshot,
        string[] active,
        string[] dead)
    {
        var actualActive = snapshot.Members
            .Where(static entry => entry.Value.Status == SiloStatus.Active)
            .Select(static entry => entry.Key.ToParsableString())
            .ToHashSet(StringComparer.Ordinal);
        if (!actualActive.SetEquals(active))
        {
            return false;
        }

        foreach (var address in dead)
        {
            if (!snapshot.Members.Any(
                entry => entry.Key.ToParsableString() == address && entry.Value.Status == SiloStatus.Dead))
            {
                return false;
            }
        }

        return true;
    }

    private static object CreateMembershipResult(
        IServiceProvider services,
        ClusterMembershipSnapshot snapshot) =>
        new
        {
            SiloAddress = services.GetRequiredService<ILocalSiloDetails>().SiloAddress.ToParsableString(),
            Version = snapshot.Version.Value,
            Hosts = snapshot.Members
                .OrderBy(static entry => entry.Key.ToParsableString(), StringComparer.Ordinal)
                .Select(static entry => new
                {
                    Address = entry.Key.ToParsableString(),
                    Status = entry.Value.Status.ToString(),
                })
                .ToArray(),
        };

    private static string DescribeMembership(ClusterMembershipSnapshot snapshot) =>
        $"version={snapshot.Version.Value}, "
        + $"hosts=[{string.Join(", ", snapshot.Members.Select(
            static entry => $"{entry.Key.ToParsableString()}:{entry.Value.Status}"))}]";

    private static object GetRuntimeProvenance()
    {
        var assembly = GetRuntimeAssembly();
        var location = assembly.Location;
        var fileVersion = FileVersionInfo.GetVersionInfo(location);
        return new
        {
            AssemblyName = assembly.GetName().Name,
            AssemblyVersion = assembly.GetName().Version?.ToString(),
            InformationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion,
            FileVersion = fileVersion.FileVersion,
            ProductVersion = fileVersion.ProductVersion,
            Location = location,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location))),
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            TargetFramework = AppContext.TargetFrameworkName,
            RuntimeVersion = Environment.Version.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        };
    }

    private static Assembly GetRuntimeAssembly() =>
        AppDomain.CurrentDomain.GetAssemblies().Single(
            static assembly => string.Equals(
                assembly.GetName().Name,
                "Orleans.Runtime",
                StringComparison.Ordinal));

    private static void WriteResponse(HostResponse response)
    {
        lock (ResponseLock)
        {
            Console.WriteLine($"{ResponsePrefix}{JsonSerializer.Serialize(response)}");
        }
    }

    private sealed record HostCommand(
        int Id,
        string Name,
        string[]? Keys,
        string[]? Active,
        string[]? Dead,
        long? DeadlineUnixMilliseconds,
        string? Owner,
        string? Prefix,
        int? Count,
        int? MaxAttempts);

    private sealed record HostResponse(int Id, bool Success, string? Error, object? Data);
}
