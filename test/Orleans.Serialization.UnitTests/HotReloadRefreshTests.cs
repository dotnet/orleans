using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Hosting;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;
using Xunit;


namespace Orleans.Serialization.UnitTests;

/// <summary>
/// Simulates a hot reload by stripping this assembly's "HotReloadScenario" types from the manifest at
/// container build time and refreshing them back in through the real generated manifest providers.
/// </summary>
[Trait("Category", "BVT")]
public class HotReloadRefreshTests : IDisposable
{
    private const string ScenarioNamespaceFragment = "HotReloadScenario";
    private readonly ServiceProvider _services;
    private readonly SerializationHotReloadRefresher _refresher;

    private readonly ScenarioTypeNameFilter _scenarioFilter = new();

    public HotReloadRefreshTests()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.Configure<TypeManifestOptions>(RemoveScenarioTypes);
        services.AddSingleton<Orleans.Serialization.ITypeNameFilter>(_scenarioFilter);
        _services = services.BuildServiceProvider();
        _refresher = ActivatorUtilities.CreateInstance<SerializationHotReloadRefresher>(_services);
    }

    public void Dispose()
    {
        _refresher.Dispose();
        _services.Dispose();
    }

    [Fact]
    public void RefreshMakesNewTypesSerializable()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var value = new HotReloadScenario.HotReloadAddedType { Value = 42 };

        Assert.Throws<CodecNotFoundException>(() => serializer.SerializeToArray(value));

        Refresh();

        var roundTripped = serializer.Deserialize<HotReloadScenario.HotReloadAddedType>(serializer.SerializeToArray(value));
        Assert.Equal(42, roundTripped!.Value);
    }

    [Fact]
    public void RefreshPurgesCachedFallbackCodecs()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var holder = new HotReloadScenario.HotReloadHolderType { Inner = new HotReloadScenario.HotReloadAddedType { Value = 7 } };

        Assert.ThrowsAny<Exception>(() => serializer.SerializeToArray(holder));

        Refresh();

        var roundTripped = serializer.Deserialize<HotReloadScenario.HotReloadHolderType>(serializer.SerializeToArray(holder));
        Assert.Equal(7, roundTripped!.Inner!.Value);
    }

    [Fact]
    public void RefreshRecoversDeniedTypeNames()
    {
        var typeConverter = _services.GetRequiredService<TypeConverter>();
        var formatted = RuntimeTypeNameFormatter.Format(typeof(HotReloadScenario.HotReloadAddedType));

        // Each denied parse caches a negative allow-list verdict for the name.
        Assert.ThrowsAny<Exception>(() => typeConverter.Parse(formatted));
        Assert.ThrowsAny<Exception>(() => typeConverter.Parse(formatted));

        _scenarioFilter.AllowScenarioTypes();
        Refresh();

        Assert.Equal(typeof(HotReloadScenario.HotReloadAddedType), typeConverter.Parse(formatted));
    }

    private sealed class ScenarioTypeNameFilter : Orleans.Serialization.ITypeNameFilter
    {
        private bool _denyScenarioTypes = true;

        public void AllowScenarioTypes() => _denyScenarioTypes = false;

        public bool? IsTypeNameAllowed(string typeName, string assemblyName)
            => _denyScenarioTypes
                && typeName is not null
                && typeName.Contains(ScenarioNamespaceFragment, StringComparison.Ordinal)
                    ? false
                    : null;
    }

    [Fact]
    public void RefreshIsIdempotent()
    {
        // Well-known type ids and aliases must merge rather than throw on the second run.
        Refresh();
        Refresh();

        var serializer = _services.GetRequiredService<Serializer>();
        var value = new HotReloadScenario.HotReloadAddedType { Value = 1 };
        Assert.Equal(1, serializer.Deserialize<HotReloadScenario.HotReloadAddedType>(serializer.SerializeToArray(value))!.Value);
    }

    [Fact]
    public void RefreshPublishesCollectionSnapshots()
    {
        var options = _services.GetRequiredService<IOptions<TypeManifestOptions>>().Value;
        var previousAllowedTypes = options.AllowedTypes;
        var previousCount = previousAllowedTypes.Count;

        Refresh();

        Assert.NotSame(previousAllowedTypes, options.AllowedTypes);
        Assert.Equal(previousCount, previousAllowedTypes.Count);
    }

    [Fact]
    public void RefreshPreservesExistingWellKnownMappings()
    {
        const uint id = uint.MaxValue;
        const string alias = "hot_reload_existing_alias";
        var options = _services.GetRequiredService<IOptions<TypeManifestOptions>>().Value;
        options.WellKnownTypeIds[id] = typeof(string);
        options.WellKnownTypeAliases[alias] = typeof(string);

        _refresher.Refresh(
        [
            new ConfigureOptions<TypeManifestOptions>(scratch =>
            {
                scratch.WellKnownTypeIds.Add(id, typeof(int));
                scratch.WellKnownTypeAliases.Add(alias, typeof(int));
            }),
        ]);

        Assert.Equal(typeof(string), options.WellKnownTypeIds[id]);
        Assert.Equal(typeof(string), options.WellKnownTypeAliases[alias]);
    }

    [Fact]
    public void RefreshUpdatesCompoundAliases()
    {
        const string alias = "(\"hot_reload_compound_alias\",\"v1\")";
        var typeConverter = _services.GetRequiredService<TypeConverter>();
        Assert.Throws<TypeLoadException>(() => typeConverter.Parse(alias));

        _refresher.Refresh(
        [
            new ConfigureOptions<TypeManifestOptions>(scratch =>
                scratch.CompoundTypeAliases.Add("hot_reload_compound_alias").Add("v1", typeof(string))),
        ]);

        Assert.Equal(typeof(string), typeConverter.Parse(alias));
    }

    private void Refresh() => _refresher.Refresh(new HashSet<System.Reflection.Assembly> { typeof(HotReloadRefreshTests).Assembly });

    private static void RemoveScenarioTypes(TypeManifestOptions options)
    {
        static bool IsScenarioType(Type type)
            => type.FullName is { } name && name.Contains(ScenarioNamespaceFragment, StringComparison.Ordinal);

        options.Serializers.RemoveWhere(IsScenarioType);
        options.Copiers.RemoveWhere(IsScenarioType);
        options.Activators.RemoveWhere(IsScenarioType);
        options.FieldCodecs.RemoveWhere(IsScenarioType);
        options.Interfaces.RemoveWhere(IsScenarioType);
        options.InterfaceProxies.RemoveWhere(IsScenarioType);
        options.InterfaceImplementations.RemoveWhere(IsScenarioType);
        options.AllowedTypes.RemoveWhere(static name => name.Contains(ScenarioNamespaceFragment, StringComparison.Ordinal));

        foreach (var id in options.WellKnownTypeIds.Where(pair => IsScenarioType(pair.Value)).Select(pair => pair.Key).ToList())
        {
            options.WellKnownTypeIds.Remove(id);
        }

        foreach (var alias in options.WellKnownTypeAliases.Where(pair => IsScenarioType(pair.Value)).Select(pair => pair.Key).ToList())
        {
            options.WellKnownTypeAliases.Remove(alias);
        }
    }
}
