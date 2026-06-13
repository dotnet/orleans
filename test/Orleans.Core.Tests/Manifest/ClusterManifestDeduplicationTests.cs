using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
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
            Assert.Same(StringComparer.Ordinal, properties.Properties.KeyComparer);
            Assert.Same(StringComparer.Ordinal, properties.Properties.ValueComparer);
            Assert.Equal(properties, equalProperties);
            Assert.Equal(properties.GetHashCode(), equalProperties.GetHashCode());
            Assert.NotEqual(properties, differentProperties);
        }

        [Fact]
        public void GrainProperties_NormalizesValueComparer()
        {
            var properties = new GrainProperties(CreatePropertyDictionary(
                StringComparer.Ordinal,
                StringComparer.OrdinalIgnoreCase,
                new KeyValuePair<string, string>("Name", "Value")));

            Assert.Same(StringComparer.Ordinal, properties.Properties.KeyComparer);
            Assert.Same(StringComparer.Ordinal, properties.Properties.ValueComparer);
        }

        [Fact]
        public void GrainProperties_RejectsNonOrdinalKeyComparer()
        {
            var exception = Assert.Throws<ArgumentException>(() => new GrainProperties(CreatePropertyDictionary(
                StringComparer.OrdinalIgnoreCase,
                new KeyValuePair<string, string>("Name", "Value"))));

            Assert.Equal("values", exception.ParamName);
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
            Assert.Same(StringComparer.Ordinal, properties.Properties.KeyComparer);
            Assert.Same(StringComparer.Ordinal, properties.Properties.ValueComparer);
            Assert.Equal(properties, equalProperties);
            Assert.Equal(properties.GetHashCode(), equalProperties.GetHashCode());
            Assert.NotEqual(properties, differentProperties);
        }

        [Fact]
        public void GrainInterfaceProperties_NormalizesValueComparer()
        {
            var properties = new GrainInterfaceProperties(CreatePropertyDictionary(
                StringComparer.Ordinal,
                StringComparer.OrdinalIgnoreCase,
                new KeyValuePair<string, string>("Name", "Value")));

            Assert.Same(StringComparer.Ordinal, properties.Properties.KeyComparer);
            Assert.Same(StringComparer.Ordinal, properties.Properties.ValueComparer);
        }

        [Fact]
        public void GrainInterfaceProperties_RejectsNonOrdinalKeyComparer()
        {
            var exception = Assert.Throws<ArgumentException>(() => new GrainInterfaceProperties(CreatePropertyDictionary(
                StringComparer.OrdinalIgnoreCase,
                new KeyValuePair<string, string>("Name", "Value"))));

            Assert.Equal("values", exception.ParamName);
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
        public void GrainManifest_NormalizesValueComparers()
        {
            var manifest = CreateGrainManifest();
            var grains = manifest.Grains.WithComparers(EqualityComparer<GrainType>.Default, ReferenceComparer<GrainProperties>.Instance);
            var interfaces = manifest.Interfaces.WithComparers(EqualityComparer<GrainInterfaceType>.Default, ReferenceComparer<GrainInterfaceProperties>.Instance);

            var normalized = new GrainManifest(grains, interfaces);

            Assert.Same(EqualityComparer<GrainType>.Default, normalized.Grains.KeyComparer);
            Assert.Same(EqualityComparer<GrainProperties>.Default, normalized.Grains.ValueComparer);
            Assert.Same(EqualityComparer<GrainInterfaceType>.Default, normalized.Interfaces.KeyComparer);
            Assert.Same(EqualityComparer<GrainInterfaceProperties>.Default, normalized.Interfaces.ValueComparer);
        }

        [Fact]
        public void GrainManifest_RejectsNonDefaultDictionaryComparers()
        {
            var manifest = CreateGrainManifest();
            var caseInsensitiveGrains = ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>(CaseInsensitiveGrainTypeComparer.Instance);
            caseInsensitiveGrains.Add(TestGrainType, manifest.Grains[TestGrainType]);
            var grainException = Assert.Throws<ArgumentException>(() => new GrainManifest(caseInsensitiveGrains.ToImmutable(), manifest.Interfaces));
            var caseInsensitiveInterfaces = ImmutableDictionary.CreateBuilder<GrainInterfaceType, GrainInterfaceProperties>(CaseInsensitiveGrainInterfaceTypeComparer.Instance);
            caseInsensitiveInterfaces.Add(TestInterfaceType, manifest.Interfaces[TestInterfaceType]);
            var interfaceException = Assert.Throws<ArgumentException>(() => new GrainManifest(manifest.Grains, caseInsensitiveInterfaces.ToImmutable()));

            Assert.Equal("grains", grainException.ParamName);
            Assert.Equal("interfaces", interfaceException.ParamName);
        }

        [Fact]
        public void ClusterManifest_NormalizesValueComparer()
        {
            var silo = CreateSiloAddress(11111, 1);
            var silos = ImmutableDictionary
                .CreateRange([new KeyValuePair<SiloAddress, GrainManifest>(silo, CreateGrainManifest())])
                .WithComparers(EqualityComparer<SiloAddress>.Default, ReferenceComparer<GrainManifest>.Instance);

            var manifest = new ClusterManifest(new MajorMinorVersion(1, 0), silos);

            Assert.Same(EqualityComparer<SiloAddress>.Default, manifest.Silos.KeyComparer);
            Assert.Same(EqualityComparer<GrainManifest>.Default, manifest.Silos.ValueComparer);
        }

        [Fact]
        public void ClusterManifest_RejectsNonDefaultDictionaryComparers()
        {
            var silo = CreateSiloAddress(11111, 1);
            var silos = ImmutableDictionary.CreateBuilder<SiloAddress, GrainManifest>(SiloAddressComparer.Instance);
            silos.Add(silo, CreateGrainManifest());

            var exception = Assert.Throws<ArgumentException>(() => new ClusterManifest(new MajorMinorVersion(1, 0), silos.ToImmutable()));

            Assert.Equal("silos", exception.ParamName);
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
            => CreatePropertyDictionary(StringComparer.Ordinal, StringComparer.Ordinal, properties);

        private static ImmutableDictionary<string, string> CreatePropertyDictionary(IEqualityComparer<string> comparer, params KeyValuePair<string, string>[] properties)
            => CreatePropertyDictionary(comparer, StringComparer.Ordinal, properties);

        private static ImmutableDictionary<string, string> CreatePropertyDictionary(
            IEqualityComparer<string> keyComparer,
            IEqualityComparer<string> valueComparer,
            params KeyValuePair<string, string>[] properties)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, string>(keyComparer, valueComparer);
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

        private sealed class CaseInsensitiveGrainTypeComparer : IEqualityComparer<GrainType>
        {
            public static readonly CaseInsensitiveGrainTypeComparer Instance = new();

            private CaseInsensitiveGrainTypeComparer()
            {
            }

            public bool Equals(GrainType x, GrainType y) => string.Equals(x.ToString(), y.ToString(), StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(GrainType obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ToString());
        }

        private sealed class CaseInsensitiveGrainInterfaceTypeComparer : IEqualityComparer<GrainInterfaceType>
        {
            public static readonly CaseInsensitiveGrainInterfaceTypeComparer Instance = new();

            private CaseInsensitiveGrainInterfaceTypeComparer()
            {
            }

            public bool Equals(GrainInterfaceType x, GrainInterfaceType y) => string.Equals(x.ToString(), y.ToString(), StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(GrainInterfaceType obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ToString());
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T>
            where T : class
        {
            public static readonly ReferenceComparer<T> Instance = new();

            private ReferenceComparer()
            {
            }

            public bool Equals(T x, T y) => ReferenceEquals(x, y);

            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class SiloAddressComparer : IEqualityComparer<SiloAddress>
        {
            public static readonly SiloAddressComparer Instance = new();

            private SiloAddressComparer()
            {
            }

            public bool Equals(SiloAddress x, SiloAddress y) => EqualityComparer<SiloAddress>.Default.Equals(x, y);

            public int GetHashCode(SiloAddress obj) => EqualityComparer<SiloAddress>.Default.GetHashCode(obj);
        }
    }
}
