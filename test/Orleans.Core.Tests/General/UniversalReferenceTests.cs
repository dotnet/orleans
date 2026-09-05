using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using TestExtensions;
using Xunit;

namespace UnitTests.General;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("Runtime")]
[TestCategory("BVT")]
[TestSuite("BVT")]
public sealed class UniversalReferenceTests(TestEnvironmentFixture environment)
{
    private static readonly GrainId GrainId = GrainId.Create("test.grain", "key");
    private static readonly GrainInterfaceType InterfaceType = GrainInterfaceType.Create("test.interface");

    [Fact]
    public void VirtualReferenceHasServiceScopedIdentity()
    {
        var reference = UniversalReference.CreateVirtual(GrainId, InterfaceType, "service");
        var otherInterface = reference.WithInterfaceType(GrainInterfaceType.Create("other.interface"));

        Assert.Equal(UniversalReferenceBinding.Virtual, reference.Binding);
        Assert.Null(reference.ClusterId);
        Assert.Equal(reference, otherInterface);
        Assert.Equal(reference.GetHashCode(), otherInterface.GetHashCode());
    }

    [Fact]
    public void ClusterReferenceIncludesClusterInIdentity()
    {
        var first = UniversalReference.CreateCluster(GrainId, InterfaceType, "service", "cluster-a");
        var same = UniversalReference.CreateCluster(GrainId, InterfaceType, "service", "cluster-a");
        var otherCluster = UniversalReference.CreateCluster(GrainId, InterfaceType, "service", "cluster-b");
        var virtualReference = UniversalReference.CreateVirtual(GrainId, InterfaceType, "service");

        Assert.Equal(first, same);
        Assert.NotEqual(first, otherCluster);
        Assert.NotEqual(first, virtualReference);
        Assert.NotEqual(first.GetUniformHashCode(), otherCluster.GetUniformHashCode());
    }

    [Fact]
    public void InvalidReferencesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => UniversalReference.CreateVirtual(default, InterfaceType, "service"));
        Assert.Throws<ArgumentException>(() => UniversalReference.CreateVirtual(GrainId, InterfaceType, ""));
        Assert.Throws<ArgumentException>(() => UniversalReference.CreateCluster(GrainId, InterfaceType, "service", ""));
    }

    [Fact]
    public void OrleansSerializerRoundTripsBothBindings()
    {
        var virtualReference = UniversalReference.CreateVirtual(GrainId, InterfaceType, "service");
        var clusterReference = UniversalReference.CreateCluster(GrainId, InterfaceType, "service", "cluster");

        Assert.Equal(
            virtualReference,
            environment.Serializer.Deserialize<UniversalReference>(environment.Serializer.SerializeToArray(virtualReference)));
        Assert.Equal(
            clusterReference,
            environment.Serializer.Deserialize<UniversalReference>(environment.Serializer.SerializeToArray(clusterReference)));
    }

    [Fact]
    public void UniversalReferenceReadsPreviousTwoFieldPayload()
    {
        var legacySerializer = environment.Services.GetRequiredService<IValueSerializer<LegacyReferenceData>>();
        var referenceSerializer = environment.Services.GetRequiredService<IValueSerializer<UniversalReference>>();
        var sessionPool = environment.Services.GetRequiredService<SerializerSessionPool>();
        var legacy = new LegacyReferenceData { GrainId = GrainId, InterfaceType = InterfaceType };

        using var writerSession = sessionPool.GetSession();
        var writer = Writer.CreatePooled(writerSession);
        legacySerializer.Serialize(ref writer, ref legacy);
        writer.WriteEndObject();
        writer.Commit();

        using var readerSession = sessionPool.GetSession();
        var reader = Reader.Create(writer.Output.AsReadOnlySequence(), readerSession);
        var reference = default(UniversalReference);
        referenceSerializer.Deserialize(ref reader, ref reference);

        Assert.Equal(GrainId, reference.GrainId);
        Assert.Equal(InterfaceType, reference.InterfaceType);
        Assert.Null(reference.ServiceId);
        Assert.Equal(UniversalReferenceBinding.Virtual, reference.Binding);
    }

    [GenerateSerializer]
    public struct LegacyReferenceData
    {
        [Id(0)]
        public GrainId GrainId;

        [Id(1)]
        public GrainInterfaceType InterfaceType;
    }

    [Fact]
    public void ClusterIdentity_EqualityHashAndString_IncludeServiceAndCluster()
    {
        var identity = new ClusterIdentity("service-a", "cluster-a");
        var same = new ClusterIdentity(string.Concat("service", "-a"), string.Concat("cluster", "-a"));

        Assert.Equal(identity, same);
        Assert.True(identity == same);
        Assert.False(identity != same);
        Assert.Equal(identity.GetHashCode(), same.GetHashCode());
        Assert.Equal("service-a/cluster-a", identity.ToString());
        Assert.NotEqual(identity, new ClusterIdentity("service-b", "cluster-a"));
        Assert.NotEqual(identity, new ClusterIdentity("service-a", "cluster-b"));
        Assert.False(identity.Equals("service-a/cluster-a"));
    }

    [Fact]
    public void ClusterIdentity_DefaultOrMalformedValues_AreRejected()
    {
        var uninitialized = default(ClusterIdentity);

        Assert.Throws<ArgumentNullException>(() => new ClusterIdentity(null!, "cluster"));
        Assert.Throws<ArgumentException>(() => new ClusterIdentity(" ", "cluster"));
        Assert.Throws<ArgumentNullException>(() => new ClusterIdentity("service", null!));
        Assert.Throws<ArgumentException>(() => new ClusterIdentity("service", "\t"));
        Assert.Throws<ArgumentNullException>(() => new ClusterIdentity(uninitialized.ServiceId!, "cluster"));
        Assert.Equal("/", uninitialized.ToString());
    }

    [Fact]
    public void UniversalReference_VirtualBinding_EqualityIgnoresInterfaceProjection()
    {
        var first = UniversalReference.CreateVirtual(GrainId, InterfaceType, "service");
        var projected = first.WithInterfaceType(GrainInterfaceType.Create("other.interface"));

        Assert.Equal(first, projected);
        Assert.True(first == projected);
        Assert.Equal(first.GetHashCode(), projected.GetHashCode());
        Assert.Equal(first.GetUniformHashCode(), projected.GetUniformHashCode());
        Assert.NotEqual(first.InterfaceType, projected.InterfaceType);
        Assert.Equal(first.GrainId, projected.GrainId);
    }

    [Fact]
    public void UniversalReference_ClusterBinding_EqualityIncludesServiceAndCluster()
    {
        var first = UniversalReference.CreateCluster(GrainId, InterfaceType, "service-a", "cluster-a");
        var projected = first.WithInterfaceType(GrainInterfaceType.Create("other.interface"));
        var otherService = UniversalReference.CreateCluster(GrainId, InterfaceType, "service-b", "cluster-a");
        var otherCluster = UniversalReference.CreateCluster(GrainId, InterfaceType, "service-a", "cluster-b");

        Assert.Equal(first, projected);
        Assert.Equal(first.GetHashCode(), projected.GetHashCode());
        Assert.NotEqual(first, otherService);
        Assert.NotEqual(first, otherCluster);
        Assert.NotEqual(first.GetUniformHashCode(), otherService.GetUniformHashCode());
        Assert.NotEqual(first.GetUniformHashCode(), otherCluster.GetUniformHashCode());
    }

    [Fact]
    public void UniversalReference_UnequalValues_AreNotRequiredToHaveDifferentHashes()
    {
        var first = UniversalReference.CreateVirtual(GrainId, InterfaceType, "service-a");
        var otherService = UniversalReference.CreateVirtual(GrainId, InterfaceType, "service-b");

        Assert.NotEqual(first, otherService);
        Assert.Equal(first.GetHashCode(), otherService.GetHashCode());
        Assert.Equal(first.GetUniformHashCode(), otherService.GetUniformHashCode());
        Assert.NotEqual(first.ServiceId, otherService.ServiceId);
    }

    [Fact]
    public void UniversalReference_ToString_ContainsIdentityBindingAndClusterWhenPresent()
    {
        var virtualReference = UniversalReference.CreateVirtual(GrainId, InterfaceType, "service");
        var clusterReference = UniversalReference.CreateCluster(GrainId, InterfaceType, "service", "cluster");
        Span<char> buffer = stackalloc char[256];

        Assert.Equal($"service/virtual/{GrainId}:{InterfaceType}", virtualReference.ToString());
        Assert.Equal($"service/cluster/{GrainId}:{InterfaceType}", clusterReference.ToString());
        Assert.True(((ISpanFormattable)clusterReference).TryFormat(buffer, out var written, default, null));
        Assert.Equal(clusterReference.ToString(), buffer[..written].ToString());
    }

    [Fact]
    public void UniversalReference_InvalidBindingClusterCombinations_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new UniversalReference(
            GrainId, InterfaceType, "service", UniversalReferenceBinding.Virtual, "cluster"));
        Assert.Throws<ArgumentNullException>(() => new UniversalReference(
            GrainId, InterfaceType, "service", UniversalReferenceBinding.Cluster, null));
        Assert.Throws<ArgumentException>(() => new UniversalReference(
            GrainId, InterfaceType, "service", UniversalReferenceBinding.Cluster, " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UniversalReference(
            GrainId, InterfaceType, "service", (UniversalReferenceBinding)byte.MaxValue, null));
        Assert.Throws<ArgumentException>(() => new UniversalReference(
            default, InterfaceType, "service", UniversalReferenceBinding.Virtual, null));
    }

    [Fact]
    public void GrainFactory_GetGrainByUniversalReference_ReturnsTypedReference()
    {
        var original = CreateTestGrainReference(42);
        var reference = original.GetUniversalReference();

        var result = environment.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ITestGrain>(reference);

        Assert.IsAssignableFrom<UnitTests.GrainInterfaces.ITestGrain>(result);
        Assert.Equal(reference, result.GetUniversalReference());
        Assert.Equal(reference.InterfaceType, result.GetUniversalReference().InterfaceType);
        Assert.Equal(42, result.GetPrimaryKeyLong());
    }

    [Fact]
    public void GrainFactory_GetGrainByUniversalReference_RejectsInterfaceMismatch()
    {
        var reference = CreateTestGrainReference(43)
            .GetUniversalReference()
            .WithInterfaceType(GrainInterfaceType.Create("missing.interface"));
        var activator = environment.Services.GetRequiredService<Orleans.GrainReferences.GrainReferenceActivator>();

        var exception = Assert.Throws<InvalidOperationException>(() => activator.CreateReference(reference));

        Assert.Contains("Unable to find", exception.Message, StringComparison.Ordinal);
        Assert.Contains(reference.GrainId.Type.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GrainFactory_GetGrainByUniversalReference_RejectsServiceMismatch()
    {
        var original = CreateTestGrainReference(44);
        var reference = original.GetUniversalReference();
        var mismatched = new UniversalReference(
            reference.GrainId,
            reference.InterfaceType,
            reference.ServiceId + "-other",
            reference.Binding,
            reference.ClusterId);

        var exception = Assert.Throws<ArgumentException>(
            () => environment.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ITestGrain>(mismatched));

        Assert.Contains("service", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("reference", exception.ParamName);
    }

    [Fact]
    public void ClusterClient_GetGrainByUniversalReference_PreservesUniversalIdentity()
    {
        var original = CreateTestGrainReference(45);
        var universalReference = original.GetUniversalReference();

        var result = ((IGrainFactory)environment.Client)
            .GetGrain<UnitTests.GrainInterfaces.ITestGrain>(universalReference);

        Assert.Equal(universalReference, result.GetUniversalReference());
        Assert.Equal(universalReference.InterfaceType, result.GetUniversalReference().InterfaceType);
        Assert.Equal(45, result.GetPrimaryKeyLong());
    }

    [Fact]
    public void InternalClusterClient_GetGrainByUniversalReference_PreservesUniversalIdentity()
    {
        var original = CreateTestGrainReference(46);
        var universalReference = original.GetUniversalReference();
        var client = new InternalClusterClient(environment.RuntimeClient, environment.InternalGrainFactory);

        var result = ((IGrainFactory)client)
            .GetGrain<UnitTests.GrainInterfaces.ITestGrain>(universalReference);

        Assert.Equal(universalReference, result.GetUniversalReference());
        Assert.Equal(universalReference.InterfaceType, result.GetUniversalReference().InterfaceType);
        Assert.Equal(46, result.GetPrimaryKeyLong());
    }

    [Fact]
    public void GrainExtensions_GetUniversalReference_ReturnsOriginalIdentity()
    {
        var original = CreateTestGrainReference(47);
        var expected = ((GrainReference)original).UniversalReference;

        var actual = original.GetUniversalReference();

        Assert.Equal(expected, actual);
        Assert.Equal(expected.InterfaceType, actual.InterfaceType);
        Assert.Same(original, original.AsReference<UnitTests.GrainInterfaces.ITestGrain>());
        Assert.Throws<ArgumentNullException>(() => GrainExtensions.GetUniversalReference(null!));
    }

    [Fact]
    public void GrainReferenceRuntime_Cast_PreservesUniversalIdentityAcrossInterfaces()
    {
        var original = CreateTestGrainReference(48);
        var source = original.GetUniversalReference();

        var result = original.Cast<UnitTests.GrainInterfaces.IGuidTestGrain>();
        var projected = result.GetUniversalReference();

        Assert.Equal(source, projected);
        Assert.Equal(source.GrainId, projected.GrainId);
        Assert.Equal(source.ServiceId, projected.ServiceId);
        Assert.Equal(source.Binding, projected.Binding);
        Assert.Equal(source.ClusterId, projected.ClusterId);
        Assert.NotEqual(source.InterfaceType, projected.InterfaceType);
    }

    [Fact]
    public void CsCheck_UniversalReferenceEqualityAndHash_ObeyEquivalenceContract()
    {
        CsCheck.Gen.Select(CsCheck.Gen.Int, CsCheck.Gen.Int, CsCheck.Gen.Int).Sample(
            (key, serviceValue, clusterValue) =>
            {
                var grainId = GrainId.Create("property.grain", key.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var service = $"service-{(uint)serviceValue % 7}";
                var cluster = $"cluster-{(uint)clusterValue % 5}";
                var first = UniversalReference.CreateCluster(grainId, InterfaceType, service, cluster);
                var second = first.WithInterfaceType(GrainInterfaceType.Create("projection.two"));
                var third = second.WithInterfaceType(GrainInterfaceType.Create("projection.three"));
                var history = $"seed=0N0XIzNsQ0U2; key={key}; service={service}; cluster={cluster}";

                Assert.True(first.Equals(first), history);
                Assert.True(first.Equals(second) && second.Equals(first), history);
                Assert.True(first.Equals(second) && second.Equals(third) && first.Equals(third), history);
                Assert.True(first.GetHashCode() == second.GetHashCode(), history);
                Assert.True(first.GetUniformHashCode() == second.GetUniformHashCode(), history);
                Assert.False(first.Equals(UniversalReference.CreateCluster(grainId, InterfaceType, service + "-other", cluster)), history);
                Assert.False(first.Equals(UniversalReference.CreateCluster(grainId, InterfaceType, service, cluster + "-other")), history);
                Assert.False(first.Equals(UniversalReference.CreateVirtual(grainId, InterfaceType, service)), history);
            },
            seed: "0N0XIzNsQ0U2",
            iter: 180,
            threads: 1,
            print: static values => values.ToString()!);
    }

    [Fact]
    public void CsCheck_UniversalReferenceMalformedInput_IsRejected()
    {
        CsCheck.Gen.Int.Select(static value => (int)((uint)value % 8)).Sample(
            testCase =>
            {
                var exception = Record.Exception(() => CreateMalformedReference(testCase));
                Assert.True(
                    exception is ArgumentException,
                    $"seed=0N0XIzNsQ0U3; malformed-case={testCase}; exception={exception}");
            },
            seed: "0N0XIzNsQ0U3",
            iter: 160,
            threads: 1,
            print: static value => $"malformed-case={value}");
    }

    private static UniversalReference CreateMalformedReference(int testCase) => testCase switch
    {
        0 => new UniversalReference(default, InterfaceType, "service", UniversalReferenceBinding.Virtual, null),
        1 => new UniversalReference(GrainId, InterfaceType, null!, UniversalReferenceBinding.Virtual, null),
        2 => new UniversalReference(GrainId, InterfaceType, " ", UniversalReferenceBinding.Virtual, null),
        3 => new UniversalReference(GrainId, InterfaceType, "service", UniversalReferenceBinding.Virtual, "cluster"),
        4 => new UniversalReference(GrainId, InterfaceType, "service", UniversalReferenceBinding.Cluster, null),
        5 => new UniversalReference(GrainId, InterfaceType, "service", UniversalReferenceBinding.Cluster, ""),
        6 => new UniversalReference(GrainId, InterfaceType, "service", (UniversalReferenceBinding)2, null),
        _ => new UniversalReference(GrainId, InterfaceType, "service", (UniversalReferenceBinding)byte.MaxValue, "cluster")
    };

    private UnitTests.GrainInterfaces.ITestGrain CreateTestGrainReference(long key) =>
        environment.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ITestGrain>(
            GrainId.Create(GrainType.Create("phase2.test-grain"), GrainIdKeyExtensions.CreateIntegerKey(key)));

    [Fact]
    public void UniversalReferenceBindingResolver_DisabledMetaclusterDefaultVirtualReferenceResolvesLocally()
    {
        var options = environment.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Orleans.Configuration.MetaclusterOptions>>()
            .Value;
        var clusterOptions = environment.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Orleans.Configuration.ClusterOptions>>()
            .Value;
        var source = CreateTestGrainReference(49).GetUniversalReference();
        var legacyReference = UniversalReference.CreateVirtual(
            source.GrainId,
            source.InterfaceType,
            "default");
        var foreignReference = UniversalReference.CreateVirtual(
            source.GrainId,
            source.InterfaceType,
            clusterOptions.ServiceId + "-foreign");

        var result = environment.GrainFactory
            .GetGrain<UnitTests.GrainInterfaces.ITestGrain>(legacyReference);
        var actual = result.GetUniversalReference();
        var foreignException = Assert.Throws<ArgumentException>(
            () => environment.GrainFactory
                .GetGrain<UnitTests.GrainInterfaces.ITestGrain>(foreignReference));

        Assert.False(options.Enabled);
        Assert.Equal(source.GrainId, actual.GrainId);
        Assert.Equal(source.InterfaceType, actual.InterfaceType);
        Assert.Equal(clusterOptions.ServiceId, actual.ServiceId);
        Assert.Equal(UniversalReferenceBinding.Virtual, actual.Binding);
        Assert.Null(actual.ClusterId);
        Assert.Equal(49, result.GetPrimaryKeyLong());
        Assert.Equal("reference", foreignException.ParamName);
        Assert.Contains("service identity", foreignException.Message, StringComparison.Ordinal);
    }
}
