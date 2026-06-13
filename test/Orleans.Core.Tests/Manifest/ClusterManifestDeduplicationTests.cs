using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Orleans.Metadata;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.Manifest
{
    [TestCategory("BVT"), TestCategory("Manifest")]
    public class ClusterManifestDeduplicationTests
    {
        private static readonly GrainType TestGrainType = GrainType.Create("test");
        private static readonly GrainInterfaceType TestInterfaceType = GrainInterfaceType.Create("test.interface");

        [Fact]
        public void GrainProperties_UsesStructuralEquality()
        {
            var properties = new GrainProperties(CreatePropertyDictionary(
                new KeyValuePair<string, string>("one", "1"),
                new KeyValuePair<string, string>("two", "2")));
            var equalProperties = new GrainProperties(CreatePropertyDictionary(
                new KeyValuePair<string, string>("two", "2"),
                new KeyValuePair<string, string>("one", "1")));
            var differentProperties = new GrainProperties(CreatePropertyDictionary(
                new KeyValuePair<string, string>("one", "1"),
                new KeyValuePair<string, string>("two", "different")));

            Assert.NotSame(properties, equalProperties);
            Assert.Equal(properties, equalProperties);
            Assert.Equal(properties.GetHashCode(), equalProperties.GetHashCode());
            Assert.NotEqual(properties, differentProperties);
        }

        [Fact]
        public void GrainProperties_DoesNotUseDictionaryComparerForKeyEquality()
        {
            var properties = new GrainProperties(CreatePropertyDictionary(
                StringComparer.Ordinal,
                new KeyValuePair<string, string>("Name", "Value")));
            var differentProperties = new GrainProperties(CreatePropertyDictionary(
                StringComparer.OrdinalIgnoreCase,
                new KeyValuePair<string, string>("name", "Value")));

            Assert.False(properties.Equals(differentProperties));
            Assert.False(differentProperties.Equals(properties));
        }

        [Fact]
        public void GrainInterfaceProperties_UsesStructuralEquality()
        {
            var properties = new GrainInterfaceProperties(CreatePropertyDictionary(
                new KeyValuePair<string, string>("one", "1"),
                new KeyValuePair<string, string>("two", "2")));
            var equalProperties = new GrainInterfaceProperties(CreatePropertyDictionary(
                new KeyValuePair<string, string>("two", "2"),
                new KeyValuePair<string, string>("one", "1")));
            var differentProperties = new GrainInterfaceProperties(CreatePropertyDictionary(
                new KeyValuePair<string, string>("one", "1"),
                new KeyValuePair<string, string>("two", "different")));

            Assert.NotSame(properties, equalProperties);
            Assert.Equal(properties, equalProperties);
            Assert.Equal(properties.GetHashCode(), equalProperties.GetHashCode());
            Assert.NotEqual(properties, differentProperties);
        }

        [Fact]
        public void GrainInterfaceProperties_DoesNotUseDictionaryComparerForKeyEquality()
        {
            var properties = new GrainInterfaceProperties(CreatePropertyDictionary(
                StringComparer.Ordinal,
                new KeyValuePair<string, string>("Name", "Value")));
            var differentProperties = new GrainInterfaceProperties(CreatePropertyDictionary(
                StringComparer.OrdinalIgnoreCase,
                new KeyValuePair<string, string>("name", "Value")));

            Assert.False(properties.Equals(differentProperties));
            Assert.False(differentProperties.Equals(properties));
        }

        [Fact]
        public void GrainManifest_UsesStructuralEquality()
        {
            var manifest = CreateGrainManifest();
            var equalManifest = CreateGrainManifest();
            var differentManifest = CreateGrainManifest(
                new KeyValuePair<string, string>("different", "true"));

            Assert.NotSame(manifest, equalManifest);
            Assert.Equal(manifest, equalManifest);
            Assert.Equal(manifest.GetHashCode(), equalManifest.GetHashCode());
            Assert.NotEqual(manifest, differentManifest);
        }

        [Fact]
        public void GrainManifest_DoesNotUseDictionaryComparerForKeyEquality()
        {
            var grainProperties = new GrainProperties(CreatePropertyDictionary(
                new KeyValuePair<string, string>(WellKnownGrainTypeProperties.TypeName, "Test")));
            var manifest = new GrainManifest(
                ImmutableDictionary.CreateRange(
                [
                    new KeyValuePair<GrainType, GrainProperties>(GrainType.Create("Test"), grainProperties)
                ]),
                ImmutableDictionary.CreateRange(
                [
                    new KeyValuePair<GrainInterfaceType, GrainInterfaceProperties>(
                        TestInterfaceType,
                        new GrainInterfaceProperties(CreatePropertyDictionary(
                            new KeyValuePair<string, string>(WellKnownGrainInterfaceProperties.TypeName, "ITest"))))
                ]));
            var caseInsensitiveGrains = ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>(CaseInsensitiveGrainTypeComparer.Instance);
            caseInsensitiveGrains.Add(GrainType.Create("test"), grainProperties);
            var differentManifest = new GrainManifest(caseInsensitiveGrains.ToImmutable(), manifest.Interfaces);

            Assert.False(manifest.Equals(differentManifest));
            Assert.False(differentManifest.Equals(manifest));
        }

        [Fact]
        public void ClusterManifest_DeduplicatesEqualManifests()
        {
            var silo1 = CreateSiloAddress(11111, 1);
            var silo2 = CreateSiloAddress(11112, 1);
            var silo1Manifest = CreateGrainManifest();
            var silo2Manifest = CreateGrainManifest();
            var localManifest = CreateGrainManifest();

            var manifest = new ClusterManifest(
                new MajorMinorVersion(1, 0),
                ImmutableDictionary.CreateRange(
                [
                    new KeyValuePair<SiloAddress, GrainManifest>(silo1, silo1Manifest),
                    new KeyValuePair<SiloAddress, GrainManifest>(silo2, silo2Manifest)
                ]),
                ImmutableArray.Create(localManifest));

            Assert.Single(manifest.AllGrainManifests);
            Assert.Same(manifest.Silos[silo1], manifest.Silos[silo2]);
            Assert.Same(manifest.Silos[silo1], manifest.AllGrainManifests[0]);
        }

        [Fact]
        public void ClusterManifest_PreservesDistinctAdditionalManifests()
        {
            var silo = CreateSiloAddress(11111, 1);
            var siloManifest = CreateGrainManifest();
            var additionalManifest = CreateGrainManifest(
                new KeyValuePair<string, string>("additional", "true"));
            var duplicateAdditionalManifest = CreateGrainManifest(
                new KeyValuePair<string, string>("additional", "true"));

            var manifest = new ClusterManifest(
                new MajorMinorVersion(1, 0),
                ImmutableDictionary.CreateRange(
                [
                    new KeyValuePair<SiloAddress, GrainManifest>(silo, siloManifest)
                ]),
                ImmutableArray.Create(additionalManifest, duplicateAdditionalManifest, siloManifest));

            Assert.Equal(2, manifest.AllGrainManifests.Length);
            Assert.Same(manifest.Silos[silo], manifest.AllGrainManifests[0]);
            var preservedAdditionalManifest = Assert.Single(manifest.AllGrainManifests.Skip(1));
            Assert.Equal(additionalManifest, preservedAdditionalManifest);
            Assert.NotSame(preservedAdditionalManifest.Grains[TestGrainType], manifest.AllGrainManifests[0].Grains[TestGrainType]);
            Assert.Same(preservedAdditionalManifest.Interfaces[TestInterfaceType], manifest.AllGrainManifests[0].Interfaces[TestInterfaceType]);
        }

        [Fact]
        public void ClusterManifest_DeduplicatesEquivalentManifestEntries()
        {
            var silo1 = CreateSiloAddress(11111, 1);
            var silo2 = CreateSiloAddress(11112, 1);
            var otherGrainType = GrainType.Create("other");
            var otherInterfaceType = GrainInterfaceType.Create("other.interface");
            var sharedGrainProperties = new GrainProperties(CreatePropertyDictionary(
                new KeyValuePair<string, string>(WellKnownGrainTypeProperties.TypeName, "Test"),
                new KeyValuePair<string, string>(WellKnownGrainTypeProperties.FullTypeName, "UnitTests.Grains.Test")));
            var equalSharedGrainProperties = new GrainProperties(CreatePropertyDictionary(
                new KeyValuePair<string, string>(WellKnownGrainTypeProperties.FullTypeName, "UnitTests.Grains.Test"),
                new KeyValuePair<string, string>(WellKnownGrainTypeProperties.TypeName, "Test")));
            var sharedInterfaceProperties = new GrainInterfaceProperties(CreatePropertyDictionary(
                new KeyValuePair<string, string>(WellKnownGrainInterfaceProperties.TypeName, "ITest"),
                new KeyValuePair<string, string>(WellKnownGrainInterfaceProperties.Version, "1")));
            var equalSharedInterfaceProperties = new GrainInterfaceProperties(CreatePropertyDictionary(
                new KeyValuePair<string, string>(WellKnownGrainInterfaceProperties.Version, "1"),
                new KeyValuePair<string, string>(WellKnownGrainInterfaceProperties.TypeName, "ITest")));
            var silo1Manifest = new GrainManifest(
                ImmutableDictionary.CreateRange(
                [
                    new KeyValuePair<GrainType, GrainProperties>(TestGrainType, sharedGrainProperties)
                ]),
                ImmutableDictionary.CreateRange(
                [
                    new KeyValuePair<GrainInterfaceType, GrainInterfaceProperties>(TestInterfaceType, sharedInterfaceProperties)
                ]));
            var silo2Manifest = new GrainManifest(
                ImmutableDictionary.CreateRange(
                [
                    new KeyValuePair<GrainType, GrainProperties>(TestGrainType, equalSharedGrainProperties),
                    new KeyValuePair<GrainType, GrainProperties>(otherGrainType, new GrainProperties(CreatePropertyDictionary(
                        new KeyValuePair<string, string>(WellKnownGrainTypeProperties.TypeName, "Other"))))
                ]),
                ImmutableDictionary.CreateRange(
                [
                    new KeyValuePair<GrainInterfaceType, GrainInterfaceProperties>(TestInterfaceType, equalSharedInterfaceProperties),
                    new KeyValuePair<GrainInterfaceType, GrainInterfaceProperties>(otherInterfaceType, new GrainInterfaceProperties(CreatePropertyDictionary(
                        new KeyValuePair<string, string>(WellKnownGrainInterfaceProperties.TypeName, "IOther"))))
                ]));

            var manifest = new ClusterManifest(
                new MajorMinorVersion(1, 0),
                ImmutableDictionary.CreateRange(
                [
                    new KeyValuePair<SiloAddress, GrainManifest>(silo1, silo1Manifest),
                    new KeyValuePair<SiloAddress, GrainManifest>(silo2, silo2Manifest)
                ]));

            Assert.Equal(2, manifest.AllGrainManifests.Length);
            Assert.NotSame(manifest.Silos[silo1], manifest.Silos[silo2]);
            Assert.Same(manifest.Silos[silo1].Grains[TestGrainType], manifest.Silos[silo2].Grains[TestGrainType]);
            Assert.Same(manifest.Silos[silo1].Interfaces[TestInterfaceType], manifest.Silos[silo2].Interfaces[TestInterfaceType]);
        }

        [Fact]
        public void ClusterManifest_DeduplicatesStringEquivalentProperties()
        {
            var silo1 = CreateSiloAddress(11111, 1);
            var silo2 = CreateSiloAddress(11112, 1);
            var propertyKey = Copy("property");
            var propertyValue = Copy("value");
            var equalPropertyKey = Copy("property");
            var equalPropertyValue = Copy("value");
            Assert.NotSame(propertyKey, equalPropertyKey);
            Assert.NotSame(propertyValue, equalPropertyValue);

            var silo1Manifest = CreateGrainManifest(new KeyValuePair<string, string>(propertyKey, propertyValue));
            var silo2Manifest = CreateGrainManifest(new KeyValuePair<string, string>(equalPropertyKey, equalPropertyValue));

            var manifest = new ClusterManifest(
                new MajorMinorVersion(1, 0),
                ImmutableDictionary.CreateRange(
                [
                    new KeyValuePair<SiloAddress, GrainManifest>(silo1, silo1Manifest),
                    new KeyValuePair<SiloAddress, GrainManifest>(silo2, silo2Manifest)
                ]));

            Assert.Single(manifest.AllGrainManifests);
            Assert.Same(manifest.Silos[silo1], manifest.Silos[silo2]);
            Assert.Same(manifest.Silos[silo1].Grains[TestGrainType], manifest.Silos[silo2].Grains[TestGrainType]);
        }

        private static GrainManifest CreateGrainManifest(params KeyValuePair<string, string>[] additionalGrainProperties)
        {
            var grainProperties = CreatePropertyDictionary(
                new KeyValuePair<string, string>(WellKnownGrainTypeProperties.TypeName, "Test"),
                new KeyValuePair<string, string>(WellKnownGrainTypeProperties.FullTypeName, "UnitTests.Grains.Test"),
                new KeyValuePair<string, string>($"{WellKnownGrainTypeProperties.ImplementedInterfacePrefix}0", TestInterfaceType.ToString()));
            foreach (var property in additionalGrainProperties)
            {
                grainProperties = grainProperties.SetItem(property.Key, property.Value);
            }

            var grains = ImmutableDictionary.CreateRange(
            [
                new KeyValuePair<GrainType, GrainProperties>(
                    TestGrainType,
                    new GrainProperties(grainProperties))
            ]);
            var interfaces = ImmutableDictionary.CreateRange(
            [
                new KeyValuePair<GrainInterfaceType, GrainInterfaceProperties>(
                    TestInterfaceType,
                    new GrainInterfaceProperties(CreatePropertyDictionary(
                        new KeyValuePair<string, string>(WellKnownGrainInterfaceProperties.TypeName, "ITest"),
                        new KeyValuePair<string, string>(WellKnownGrainInterfaceProperties.Version, "1"))))
            ]);

            return new GrainManifest(grains, interfaces);
        }

        private static ImmutableDictionary<string, string> CreatePropertyDictionary(params KeyValuePair<string, string>[] properties)
            => CreatePropertyDictionary(ConstantHashStringComparer.Instance, properties);

        private static ImmutableDictionary<string, string> CreatePropertyDictionary(IEqualityComparer<string> comparer, params KeyValuePair<string, string>[] properties)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, string>(comparer);
            foreach (var property in properties)
            {
                builder.Add(property.Key, property.Value);
            }

            return builder.ToImmutable();
        }

        private static SiloAddress CreateSiloAddress(int port, int generation)
        {
            return SiloAddress.New(new(System.Net.IPAddress.Loopback, port), generation);
        }

        private static string Copy(string value) => new(value.ToCharArray());

        private sealed class ConstantHashStringComparer : IEqualityComparer<string>
        {
            public static readonly ConstantHashStringComparer Instance = new();

            private ConstantHashStringComparer()
            {
            }

            public bool Equals(string x, string y) => string.Equals(x, y, System.StringComparison.Ordinal);

            public int GetHashCode(string obj) => 0;
        }

        private sealed class CaseInsensitiveGrainTypeComparer : IEqualityComparer<GrainType>
        {
            public static readonly CaseInsensitiveGrainTypeComparer Instance = new();

            private CaseInsensitiveGrainTypeComparer()
            {
            }

            public bool Equals(GrainType x, GrainType y) => string.Equals(x.ToString(), y.ToString(), StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(GrainType obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ToString());
        }
    }
}
