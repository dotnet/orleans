using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Placement.Filtering;
using Orleans.Runtime.Versions;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Benchmarks.Placement;

[MemoryDiagnoser]
public class PlacementFilteringBenchmark
{
    private ServiceProvider _provider = null!;
    private PlacementService _service = null!;
    private CachedVersionSelectorManager _versions = null!;
    private SiloLifecycleSubject _lifecycle = null!;
    private PlacementTarget[] _targets = null!;
    private readonly RandomPlacement _placement = new();

    [Params(16, 128)]
    public int CandidateCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var services = new ServiceCollection();
        services.AddOptions<SiloMessagingOptions>();
        services.AddOptions<GrainVersioningOptions>();
        services.AddKeyedSingleton<VersionSelectorStrategy, AllCompatibleVersions>(nameof(AllCompatibleVersions));
        services.AddKeyedSingleton<CompatibilityStrategy, BackwardCompatible>(nameof(BackwardCompatible));
        services.AddKeyedSingleton<IVersionSelector, AllCompatibleVersionsSelector>(typeof(AllCompatibleVersions));
        services.AddKeyedSingleton<ICompatibilityDirector, BackwardCompatilityDirector>(typeof(BackwardCompatible));
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        services.AddSingleton<PlacementStrategy>(_placement);
        services.AddKeyedSingleton<IPlacementDirector, RandomPlacementDirector>(typeof(RandomPlacement));
        services.AddPlacementFilter<PassThroughStrategy, PassThroughDirector>(ServiceLifetime.Transient);
        services.AddPlacementFilter<SecondPassThroughStrategy, PassThroughDirector>(ServiceLifetime.Transient);
        services.AddPlacementFilter<ThirdPassThroughStrategy, PassThroughDirector>(ServiceLifetime.Transient);
        services.AddPlacementFilter<PendingStrategy, PendingDirector>(ServiceLifetime.Transient);
        services.AddPlacementFilter<RequiredMatchSiloMetadataPlacementFilterStrategy, RequiredMatchSiloMetadataPlacementFilterDirector>(ServiceLifetime.Transient);
        services.AddPlacementFilter<PreferredMatchSiloMetadataPlacementFilterStrategy, PreferredMatchSiloMetadataPlacementFilterDirector>(ServiceLifetime.Transient);
        var environment = new CandidateEnvironment(CandidateCount);
        services.AddSingleton<ILocalSiloDetails>(environment);
        services.AddSingleton<ISiloMetadataCache>(environment);
        OrleansRuntimeResiliencePolicies.AddOrleansRuntimeResiliencePolicies(services);
        _provider = services.BuildServiceProvider();

        PlacementFilterStrategy[][] filters =
        [
            [],
            [new PassThroughStrategy()],
            [new PassThroughStrategy(), new SecondPassThroughStrategy(), new ThirdPassThroughStrategy()],
            [new RequiredMatchSiloMetadataPlacementFilterStrategy(["zone"], 0)],
            [new PreferredMatchSiloMetadataPlacementFilterStrategy(["rack", "zone"], 2, 0)],
            [new PendingStrategy()],
        ];
        _targets = [.. filters.Select((_, i) => new PlacementTarget(GrainId.Create($"placement-benchmark-{i}", "key"), [], default, 0))];
        var grainProperties = new Dictionary<GrainType, GrainProperties>();
        for (var i = 0; i < _targets.Length; i++)
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var filter in filters[i])
            {
                filter.PopulateGrainProperties(_provider, typeof(PlacementFilteringBenchmark), _targets[i].GrainIdentity.Type, properties);
            }

            grainProperties.Add(_targets[i].GrainIdentity.Type, new GrainProperties(properties.ToImmutableDictionary(StringComparer.Ordinal)));
        }

        var manifest = new GrainManifest(grainProperties.ToImmutableDictionary(), []);
        var manifests = new FixedManifestProvider(environment.Silos, manifest);
        var versionManifest = new GrainVersionManifest(manifests);
        var versionOptions = _provider.GetRequiredService<IOptions<GrainVersioningOptions>>();
        _versions = new CachedVersionSelectorManager(
            versionManifest,
            new VersionSelectorManager(_provider, versionOptions),
            new CompatibilityDirectorManager(_provider, versionOptions));
        var propertiesResolver = new GrainPropertiesResolver(manifests);
        _service = new PlacementService(
            _provider.GetRequiredService<IOptionsMonitor<SiloMessagingOptions>>(),
            environment,
            environment,
            NullLogger<PlacementService>.Instance,
            grainLocator: null!,
            versionManifest,
            _versions,
            new PlacementDirectorResolver(_provider),
            new PlacementStrategyResolver(_provider, [], propertiesResolver),
            new PlacementFilterStrategyResolver(_provider, propertiesResolver),
            new PlacementFilterDirectorResolver(_provider),
            _provider.GetRequiredService<Polly.Registry.ResiliencePipelineProvider<string>>());
        _lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        ((ILifecycleParticipant<ISiloLifecycle>)_service).Participate(_lifecycle);
        await _lifecycle.OnStart(CancellationToken.None).ConfigureAwait(false);
        foreach (var target in _targets)
        {
            await _service.GetCompatibleSilosAsync(target).ConfigureAwait(false);
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _lifecycle.OnStop(CancellationToken.None).ConfigureAwait(false);
        await _provider.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark(Baseline = true)]
    public SiloAddress[] CachedCompatibility() => _versions.GetSupportedSilos(_targets[0].GrainIdentity.Type);

    [Benchmark]
    public Task<SiloAddress[]> NoFilters() => _service.GetCompatibleSilosAsync(_targets[0]);

    [Benchmark]
    public Task<SiloAddress[]> OnePassThrough() => _service.GetCompatibleSilosAsync(_targets[1]);

    [Benchmark]
    public Task<SiloAddress[]> ThreePassThrough() => _service.GetCompatibleSilosAsync(_targets[2]);

    [Benchmark]
    public Task<SiloAddress[]> RequiredMetadata() => _service.GetCompatibleSilosAsync(_targets[3]);

    [Benchmark]
    public Task<SiloAddress[]> PreferredMetadata() => _service.GetCompatibleSilosAsync(_targets[4]);

    [Benchmark]
    public Task<SiloAddress[]> PendingFilter() => _service.GetCompatibleSilosAsync(_targets[5]);

    [Benchmark]
    public Task<SiloAddress> OperationContext() =>
        _service.PlaceGrainAsync(_targets[1].GrainIdentity, _targets[1].RequestContextData, _placement);

    public sealed class PassThroughStrategy() : PlacementFilterStrategy(0);

    public sealed class SecondPassThroughStrategy() : PlacementFilterStrategy(1);

    public sealed class ThirdPassThroughStrategy() : PlacementFilterStrategy(2);

    public sealed class PendingStrategy() : PlacementFilterStrategy(0);

    public sealed class PassThroughDirector : IPlacementFilterDirector
    {
        public Task<IReadOnlyList<SiloAddress>> FilterAsync(
            PlacementFilterStrategy filterStrategy,
            PlacementTarget target,
            IReadOnlyList<SiloAddress> silos,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(silos);
        }
    }

    public sealed class PendingDirector : IPlacementFilterDirector
    {
        public async Task<IReadOnlyList<SiloAddress>> FilterAsync(
            PlacementFilterStrategy filterStrategy,
            PlacementTarget target,
            IReadOnlyList<SiloAddress> silos,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return silos;
        }
    }

    private sealed class FixedManifestProvider(SiloAddress[] silos, GrainManifest manifest) : IClusterManifestProvider
    {
        public ClusterManifest Current { get; } = new(MajorMinorVersion.Zero, silos.ToImmutableDictionary(silo => silo, _ => manifest));

        public GrainManifest LocalGrainManifest => manifest;

        public IAsyncEnumerable<ClusterManifest> Updates => GetUpdates();

        private async IAsyncEnumerable<ClusterManifest> GetUpdates()
        {
            yield return Current;
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private sealed class CandidateEnvironment : ILocalSiloDetails, ISiloStatusOracle, ISiloMetadataCache
    {
        private readonly Dictionary<SiloAddress, SiloMetadata> _metadata = [];
        private readonly HashSet<ISiloStatusListener> _listeners = [];

        public CandidateEnvironment(int count)
        {
            Silos = [.. Enumerable.Range(0, count).Select(i => SiloAddress.New(IPAddress.Loopback, 11111 + i, 1))];
            for (var i = 0; i < count; i++)
            {
                var metadata = new SiloMetadata();
                metadata.AddMetadata("zone", (i % 2).ToString(CultureInfo.InvariantCulture));
                metadata.AddMetadata("rack", (i % 4).ToString(CultureInfo.InvariantCulture));
                _metadata.Add(Silos[i], metadata);
            }
        }

        public SiloAddress[] Silos { get; }
        public SiloAddress SiloAddress => Silos[0];
        public SiloAddress GatewayAddress => Silos[0];
        public string Name => "placement-benchmark";
        public string SiloName => Name;
        public string ClusterId => Name;
        public string DnsHostName => "localhost";
        public SiloStatus CurrentStatus => SiloStatus.Active;
        public SiloAddress[] GetActiveSilos() => Silos;
        public SiloMetadata GetSiloMetadata(SiloAddress address) => _metadata[address];
        public SiloStatus GetApproximateSiloStatus(SiloAddress address) => _metadata.ContainsKey(address) ? SiloStatus.Active : SiloStatus.None;
        public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false) => Silos.ToDictionary(silo => silo, _ => SiloStatus.Active);
        public bool IsFunctionalDirectory(SiloAddress address) => _metadata.ContainsKey(address);
        public bool IsDeadSilo(SiloAddress address) => false;
        public bool SubscribeToSiloStatusEvents(ISiloStatusListener observer) => _listeners.Add(observer);
        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer) => _listeners.Remove(observer);

        public bool TryGetSiloName(SiloAddress address, [NotNullWhen(true)] out string? name)
        {
            name = _metadata.ContainsKey(address) ? address.ToString() : null;
            return name is not null;
        }
    }
}
