using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Metadata;
using Orleans.Runtime.Utilities;
using Orleans.Serialization;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.TypeSystem;
using Xunit;

namespace UnitTests.Manifest;

public partial class ClusterManifestProviderTests
{
    private static readonly GrainType TestGrainType = GrainType.Create("test");

    private static readonly GrainInterfaceType TestInterfaceType = GrainInterfaceType.Create("test.interface");

    private static readonly MethodInfo InitializeMethod = typeof(ClusterManifestProvider)
        .GetMethod("Initialize", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo UpdateManifestMethod = typeof(ClusterManifestProvider)
        .GetMethod("UpdateManifest", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ProbePeerForManifestsMethod = typeof(ClusterManifestProvider)
        .GetMethod("ProbePeerForManifests", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ManifestCacheField = typeof(ClusterManifestProvider)
        .GetField("_manifestCache", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static ClusterManifestProvider CreateClusterManifestProvider(
        SiloAddress localSilo,
        TestClusterMembershipService membership,
        IInternalGrainFactory grainFactory,
        ClusterManifestOptions? options = null,
        ClusterManifestInstruments? instruments = null) =>
        CreateClusterManifestProvider(
            localSilo,
            membership,
            grainFactory,
            TimeProvider.System,
            NullLogger<ClusterManifestProvider>.Instance,
            instruments,
            options);

    private static IInternalGrainFactory CreateGrainFactory(SiloAddress remoteSilo, GrainManifest remoteManifest)
    {
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory
            .GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, remoteSilo)
            .Returns(new TestSiloManifestSystemTarget(remoteManifest));
        return grainFactory;
    }

    private static GrainManifest CreateGrainManifest()
    {
        var grains = ImmutableDictionary.CreateRange(
        [
            new KeyValuePair<GrainType, GrainProperties>(
                TestGrainType,
                new GrainProperties(CreatePropertyDictionary(
                [
                    new KeyValuePair<string, string>(WellKnownGrainTypeProperties.TypeName, "Test"),
                    new KeyValuePair<string, string>(WellKnownGrainTypeProperties.FullTypeName, "UnitTests.Grains.Test"),
                    new KeyValuePair<string, string>($"{WellKnownGrainTypeProperties.ImplementedInterfacePrefix}0", TestInterfaceType.ToString())
                ])))
        ]);
        var interfaces = ImmutableDictionary.CreateRange(
        [
            new KeyValuePair<GrainInterfaceType, GrainInterfaceProperties>(
                TestInterfaceType,
                new GrainInterfaceProperties(CreatePropertyDictionary(
                [
                    new KeyValuePair<string, string>(WellKnownGrainInterfaceProperties.TypeName, "ITest"),
                    new KeyValuePair<string, string>(WellKnownGrainInterfaceProperties.Version, "1")
                ])))
        ]);

        return new GrainManifest(grains, interfaces);
    }

    private static ImmutableDictionary<string, string> CreatePropertyDictionary(params KeyValuePair<string, string>[] properties)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal, StringComparer.Ordinal);
        foreach (var property in properties)
        {
            builder.Add(property.Key, property.Value);
        }

        return builder.ToImmutable();
    }

    private static SiloManifestProvider CreateSiloManifestProvider()
    {
        var typeConverter = CreateTypeConverter();
        var interfaceTypeResolver = new GrainInterfaceTypeResolver([new TestGrainInterfaceTypeProvider()], typeConverter);
        var typeNameProvider = new TypeNameGrainPropertiesProvider();
        var options = new GrainTypeOptions();
        options.Classes.Add(typeof(TestManifestGrain));
        options.Interfaces.Add(typeof(ITestManifestGrain));

        return new SiloManifestProvider(
            [typeNameProvider, new ImplementedInterfaceProvider(interfaceTypeResolver)],
            [typeNameProvider, new TestGrainInterfacePropertiesProvider()],
            Options.Create(options),
            new GrainTypeResolver([new TestGrainTypeProvider()], typeConverter),
            interfaceTypeResolver,
            typeConverter);
    }

    internal interface ITestManifestGrain : IGrainWithStringKey;

    internal sealed class TestManifestGrain : ITestManifestGrain;

    private sealed class TestGrainTypeProvider : IGrainTypeProvider
    {
        public bool TryGetGrainType(Type type, out GrainType grainType)
        {
            if (type == typeof(TestManifestGrain))
            {
                grainType = TestGrainType;
                return true;
            }

            grainType = default;
            return false;
        }
    }

    private sealed class TestGrainInterfaceTypeProvider : IGrainInterfaceTypeProvider
    {
        public bool TryGetGrainInterfaceType(Type type, out GrainInterfaceType grainInterfaceType)
        {
            if (type == typeof(ITestManifestGrain))
            {
                grainInterfaceType = TestInterfaceType;
                return true;
            }

            grainInterfaceType = default;
            return false;
        }
    }

    private sealed class TestGrainInterfacePropertiesProvider : IGrainInterfacePropertiesProvider
    {
        public void Populate(Type interfaceType, GrainInterfaceType grainInterfaceType, Dictionary<string, string> properties)
        {
            properties[WellKnownGrainInterfaceProperties.Version] = "1";
        }
    }

    private static Orleans.Serialization.TypeSystem.TypeConverter CreateTypeConverter()
    {
        return new Orleans.Serialization.TypeSystem.TypeConverter(
            Array.Empty<ITypeConverter>(),
            Array.Empty<ITypeNameFilter>(),
            Array.Empty<ITypeFilter>(),
            Options.Create(new TypeManifestOptions { AllowAllTypes = true }),
            new CachedTypeResolver());
    }

    private static ClusterMembershipSnapshot CreateMembershipSnapshot(
        long version,
        params (SiloAddress SiloAddress, SiloStatus Status)[] members)
    {
        var builder = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();
        foreach (var (siloAddress, status) in members)
        {
            builder[siloAddress] = new ClusterMember(siloAddress, status, siloAddress.ToString());
        }

        return new ClusterMembershipSnapshot(builder.ToImmutable(), new MembershipVersion(version));
    }

    private static SiloAddress CreateSiloAddress(int port, int generation)
    {
        return SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), generation);
    }

    private static async Task<SiloLifecycleSubject> StartAsync(ClusterManifestProvider provider, CancellationToken cancellationToken)
    {
        var lifecycle = new SiloLifecycleSubject(NullLoggerFactory.Instance.CreateLogger<SiloLifecycleSubject>());
        ((ILifecycleParticipant<ISiloLifecycle>)provider).Participate(lifecycle);
        await lifecycle.OnStart(cancellationToken);
        return lifecycle;
    }

    private sealed class TestClusterMembershipService : IClusterMembershipService, IDisposable
    {
        private readonly AsyncEnumerable<ClusterMembershipSnapshot> _updates;
        private ClusterMembershipSnapshot _currentSnapshot = ClusterMembershipSnapshot.Default;

        public TestClusterMembershipService(ClusterMembershipSnapshot initialSnapshot)
        {
            _updates = new AsyncEnumerable<ClusterMembershipSnapshot>(
                initialValue: initialSnapshot,
                updateValidator: (previous, proposed) => proposed.Version > previous.Version,
                onPublished: update => Volatile.Write(ref _currentSnapshot, update));
        }

        public ClusterMembershipSnapshot CurrentSnapshot
        {
            get => Volatile.Read(ref _currentSnapshot);
        }

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => _updates;

        public void Update(ClusterMembershipSnapshot snapshot) => _updates.Publish(snapshot);

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default) => default;

        public Task<bool> TryKill(SiloAddress siloAddress) => Task.FromResult(false);

        public void Dispose() => _updates.Dispose();
    }

    private sealed class TestSiloManifestSystemTarget(GrainManifest manifest) : ISiloManifestSystemTarget
    {
        public ValueTask<GrainManifest> GetSiloManifest(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(manifest);
        }
    }

    private static Task InitializeProviderAsync(ClusterManifestProvider provider, CancellationToken cancellationToken) =>
        (Task)InitializeMethod.Invoke(provider, [cancellationToken])!;

    private static Task<bool> UpdateManifestAsync(
        ClusterManifestProvider provider,
        ClusterMembershipSnapshot snapshot,
        CancellationToken cancellationToken) =>
        (Task<bool>)UpdateManifestMethod.Invoke(provider, [snapshot, cancellationToken])!;

    private static Task ProbePeerAsync(
        ClusterManifestProvider provider,
        SiloAddress peer,
        IReadOnlyCollection<SiloAddress> missingSilos,
        ConcurrentDictionary<ManifestHash, GrainManifest> cache,
        CancellationToken cancellationToken) =>
        (Task)ProbePeerForManifestsMethod.Invoke(provider, [peer, missingSilos, cache, cancellationToken])!;

    private static ConcurrentDictionary<ManifestHash, GrainManifest> GetCachedManifests(ClusterManifestProvider provider) =>
        (ConcurrentDictionary<ManifestHash, GrainManifest>)ManifestCacheField.GetValue(provider)!;

    private static ClusterManifestProvider CreateClusterManifestProvider(
        SiloAddress localSilo,
        TestClusterMembershipService membership,
        IInternalGrainFactory grainFactory,
        TimeProvider timeProvider,
        ILogger<ClusterManifestProvider> logger,
        ClusterManifestInstruments? instruments = null,
        ClusterManifestOptions? options = null)
    {
        var siloManifestProvider = CreateSiloManifestProvider();
        grainFactory
            .GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, localSilo)
            .Returns(new TestSiloManifestSystemTarget(siloManifestProvider.SiloManifest));

        var services = new ServiceCollection()
            .AddSingleton(grainFactory)
            .AddMetrics()
            .AddSingleton<OrleansInstruments>()
            .AddSingleton<ClusterManifestInstruments>()
            .BuildServiceProvider();

        var localSiloDetails = Substitute.For<ILocalSiloDetails>();
        localSiloDetails.SiloAddress.Returns(localSilo);

        return new ClusterManifestProvider(
            localSiloDetails,
            siloManifestProvider,
            membership,
            Substitute.For<IFatalErrorHandler>(),
            logger,
            services,
            timeProvider,
            Options.Create(options ?? new ClusterManifestOptions()),
            instruments ?? services.GetRequiredService<ClusterManifestInstruments>());
    }

    private static ClusterMembershipSnapshot CreateActiveMembershipSnapshot(
        long version,
        SiloAddress localSilo,
        SiloAddress[] peers)
    {
        var members = new (SiloAddress SiloAddress, SiloStatus Status)[peers.Length + 1];
        members[0] = (localSilo, SiloStatus.Active);
        for (var index = 0; index < peers.Length; index++)
        {
            members[index + 1] = (peers[index], SiloStatus.Active);
        }

        return CreateMembershipSnapshot(version, members);
    }

    private static IInternalGrainFactory CreateGrainFactory(
        IReadOnlyDictionary<SiloAddress, TestClusterManifestSystemTarget> targets)
    {
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        foreach (var (siloAddress, target) in targets)
        {
            grainFactory
                .GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, siloAddress)
                .Returns(target);
            grainFactory
                .GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, siloAddress)
                .Returns(target);
        }

        return grainFactory;
    }

    private static async Task<ClusterManifest> ObserveManifestAsync(
        ClusterManifestProvider provider,
        MajorMinorVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var updates = provider.Updates.GetAsyncEnumerator(cancellationToken);
        while (await updates.MoveNextAsync())
        {
            if (updates.Current.Version >= expectedVersion)
            {
                return updates.Current;
            }
        }

        throw new InvalidOperationException($"The manifest update stream ended before version {expectedVersion} was published.");
    }

    private sealed class TestClusterManifestSystemTarget(
        Func<CancellationToken, Task<ClusterManifestHashSummary>> getHashSummary,
        Func<MajorMinorVersion, CancellationToken, Task<ClusterManifestUpdate?>> getUpdate,
        Func<CancellationToken, Task<GrainManifest>> getLegacyManifest) : IClusterManifestSystemTarget, ISiloManifestSystemTarget
    {
        public ValueTask<ClusterManifest> GetClusterManifest(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<ClusterManifest>(
                new NotSupportedException("This test target only supports peer repair requests."));
        }

        public ValueTask<ClusterManifestUpdate?> GetClusterManifestUpdate(
            MajorMinorVersion previousVersion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(getUpdate(previousVersion, cancellationToken));
        }

        public ValueTask<ClusterManifestHashSummary> GetClusterManifestHashSummary(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(getHashSummary(cancellationToken));
        }

        public ValueTask<ManifestHash> GetSiloManifestHash(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<ManifestHash>(new InvalidOperationException("Use the legacy manifest fetch path."));
        }

        public ValueTask<GrainManifest?> GetSiloManifestByHash(ManifestHash hash, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new((GrainManifest?)null);
        }

        public ValueTask<GrainManifest> GetSiloManifest(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(getLegacyManifest(cancellationToken));
        }
    }

    private sealed class ManifestRequestLog(int expectedProbeCount, int expectedLegacyFetchCount)
    {
        private readonly object _lock = new();
        private readonly List<SiloAddress> _probeAddresses = [];
        private readonly List<SiloAddress> _legacyFetchAddresses = [];
        private readonly Dictionary<int, TaskCompletionSource> _probeWaiters = [];
        private readonly Dictionary<int, TaskCompletionSource> _legacyFetchWaiters = [];

        public IReadOnlyList<SiloAddress> ProbeAddresses
        {
            get
            {
                lock (_lock)
                {
                    return _probeAddresses.ToArray();
                }
            }
        }

        public IReadOnlyList<SiloAddress> LegacyFetchAddresses
        {
            get
            {
                lock (_lock)
                {
                    return _legacyFetchAddresses.ToArray();
                }
            }
        }

        public void RecordProbe(SiloAddress address)
        {
            lock (_lock)
            {
                _probeAddresses.Add(address);
                CompleteWaiters(_probeWaiters, _probeAddresses.Count);
            }
        }

        public void RecordLegacyFetch(SiloAddress address)
        {
            lock (_lock)
            {
                _legacyFetchAddresses.Add(address);
                CompleteWaiters(_legacyFetchWaiters, _legacyFetchAddresses.Count);
            }
        }

        public Task WaitForProbeCountAsync(int count, CancellationToken cancellationToken) =>
            WaitForCountAsync(_probeWaiters, _probeAddresses, count, expectedProbeCount, cancellationToken);

        public Task WaitForLegacyFetchCountAsync(int count, CancellationToken cancellationToken) =>
            WaitForCountAsync(_legacyFetchWaiters, _legacyFetchAddresses, count, expectedLegacyFetchCount, cancellationToken);

        private Task WaitForCountAsync(
            Dictionary<int, TaskCompletionSource> waiters,
            List<SiloAddress> addresses,
            int count,
            int expectedCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                Assert.True(count <= expectedCount || expectedCount == 0, $"Expected no more than {expectedCount} requests, but waited for {count}.");
                if (addresses.Count >= count)
                {
                    return Task.CompletedTask;
                }

                if (!waiters.TryGetValue(count, out var completion))
                {
                    completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    waiters.Add(count, completion);
                }

                return completion.Task.WaitAsync(cancellationToken);
            }
        }

        private static void CompleteWaiters(Dictionary<int, TaskCompletionSource> waiters, int count)
        {
            foreach (var (expectedCount, completion) in waiters)
            {
                if (count >= expectedCount)
                {
                    completion.TrySetResult();
                }
            }
        }
    }

    private sealed class PeerProbeLogger(int expectedTimeoutCount) : ILogger<ClusterManifestProvider>
    {
        private readonly TaskCompletionSource _timeoutsObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _lateFailuresObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _timeoutCount;
        private int _lateFailureCount;

        public int TimeoutCount => Volatile.Read(ref _timeoutCount);

        public int LateFailureCount => Volatile.Read(ref _lateFailureCount);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message.StartsWith("Cluster manifest peer probe to ", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _timeoutCount) == expectedTimeoutCount)
                {
                    _timeoutsObserved.TrySetResult();
                }
            }
            else if (message.StartsWith("Cluster manifest peer probe task for ", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _lateFailureCount) == expectedTimeoutCount)
                {
                    _lateFailuresObserved.TrySetResult();
                }
            }
        }

        public Task WaitForTimeoutCountAsync(int count, CancellationToken cancellationToken)
        {
            Assert.Equal(expectedTimeoutCount, count);
            return _timeoutsObserved.Task.WaitAsync(cancellationToken);
        }

        public Task WaitForLateFailureCountAsync(int count, CancellationToken cancellationToken)
        {
            Assert.Equal(expectedTimeoutCount, count);
            return _lateFailuresObserved.Task.WaitAsync(cancellationToken);
        }
    }
}
