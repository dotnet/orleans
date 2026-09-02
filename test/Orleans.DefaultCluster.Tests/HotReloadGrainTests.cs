using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Runtime.Metadata;
using Orleans.Runtime.Versions;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Hosting;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace DefaultCluster.Tests
{
    /// <summary>
    /// End-to-end check of the hot reload manifest refresh: a grain whose metadata was withheld at startup
    /// becomes callable after the refreshers re-run the generated manifest providers.
    /// </summary>
    [TestCategory("BVT")]
    public class HotReloadGrainTests : IClassFixture<HotReloadGrainTests.Fixture>
    {
        private const string ScenarioNamespaceFragment = "HotReloadScenario";
        private readonly Fixture _fixture;

        public HotReloadGrainTests(Fixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task RefreshMakesNewGrainTypesCallable()
        {
            var services = _fixture.HostedCluster.GetSiloServiceProvider();
            var grainFactory = services.GetRequiredService<IGrainFactory>();
            var manifestProvider = services.GetRequiredService<ClusterManifestProvider>();
            var versionBefore = manifestProvider.Current.Version;

            Assert.ThrowsAny<Exception>(() =>
            {
                var grain = grainFactory.GetGrain<HotReloadScenario.IHotReloadAddedGrain>("before");
                grain.Ping().GetAwaiter().GetResult();
            });

            using var serializationRefresher = ActivatorUtilities.CreateInstance<SerializationHotReloadRefresher>(services);
            using var siloRefresher = ActivatorUtilities.CreateInstance<SiloHotReloadRefresher>(services, serializationRefresher);

            var updatedAssemblies = new HashSet<Assembly> { typeof(HotReloadGrainTests).Assembly };
            serializationRefresher.Refresh(updatedAssemblies);
            siloRefresher.Refresh(updatedAssemblies);

            var response = await grainFactory.GetGrain<HotReloadScenario.IHotReloadAddedGrain>("after").Ping();
            Assert.Equal("pong:after", response);

            var versionAfter = manifestProvider.Current.Version;
            Assert.Equal(versionBefore.Major, versionAfter.Major);
            Assert.True(versionAfter.Minor > versionBefore.Minor, $"Expected a minor version bump, but went from {versionBefore} to {versionAfter}.");
        }

        public sealed class Fixture : BaseInProcessTestClusterFixture
        {
            protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.ConfigureSilo((_, siloBuilder) =>
                    siloBuilder.Configure<TypeManifestOptions>(RemoveScenarioTypes));
            }

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
    }
}

namespace DefaultCluster.Tests.HotReloadScenario
{
    public interface IHotReloadAddedGrain : IGrainWithStringKey
    {
        Task<string> Ping();
    }

    public sealed class HotReloadAddedGrain : Grain, IHotReloadAddedGrain
    {
        public Task<string> Ping() => Task.FromResult($"pong:{this.GetPrimaryKeyString()}");
    }
}
