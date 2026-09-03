using System;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using TestExtensions;
using TestGrainInterfaces;
using UnitTests.GrainInterfaces;
using Xunit;
using SystemTextJson = System.Text.Json.JsonSerializer;

namespace UnitTests.Serialization;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("Serialization")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
public sealed class UniversalReferenceSerializationTests(TestEnvironmentFixture environment)
{
    private const string ServiceId = "phase2-service";
    private const string ClusterId = "phase2-cluster";
    private const string HomeClusterId = "phase2-home";
    private static readonly GrainInterfaceType TestInterfaceType = GrainInterfaceType.Create("test.interface");
    private static readonly Gen<(int Key, int Service, int Cluster)> PropertyInputs =
        Gen.Select(Gen.Int, Gen.Int, Gen.Int);

    [Fact]
    public void BinaryRoundTrip_VirtualUniversalReference_PreservesAllFields()
    {
        var expected = UniversalReference.CreateVirtual(
            GrainId.Create("binary.virtual", "key/17"),
            TestInterfaceType,
            ServiceId);

        var actual = BinaryRoundTrip(expected);

        AssertUniversalReference(expected, actual);
        Assert.Null(actual.ClusterId);
    }

    [Fact]
    public void BinaryRoundTrip_ClusterUniversalReference_PreservesAllFields()
    {
        var expected = UniversalReference.CreateCluster(
            GrainId.Create("binary.cluster", "key/23"),
            TestInterfaceType,
            ServiceId,
            ClusterId);

        var actual = BinaryRoundTrip(expected);

        AssertUniversalReference(expected, actual);
        Assert.Equal(ClusterId, actual.ClusterId);
    }

    [Fact]
    public void BinaryRoundTrip_TypedGrainReference_PreservesUniversalIdentityAndInterface()
    {
        var source = CreateSystemTargetReference(ClusterId, out var expectedSilo);

        var actual = environment.Serializer.Deserialize<ISiloControl>(
            environment.Serializer.SerializeToArray<ISiloControl>(source));

        Assert.IsAssignableFrom<ISiloControl>(actual);
        AssertAddressable(source.GetUniversalReference(), actual!);
        Assert.True(SystemTargetGrainId.TryParse(actual!.GetGrainId(), out var systemTargetId));
        Assert.Equal(expectedSilo, systemTargetId.GetSiloAddress());
    }

    [Fact]
    public void BinaryRoundTrip_UntypedGrainReference_PreservesUniversalIdentity()
    {
        var source = CreateUntypedReference(62, UniversalReferenceBinding.Cluster, ClusterId);

        var actual = environment.Serializer.Deserialize<IAddressable>(
            environment.Serializer.SerializeToArray(source));

        Assert.IsAssignableFrom<GrainReference>(actual);
        AssertAddressable(source.GetUniversalReference(), actual!);
        Assert.Equal(source.GetGrainId(), actual!.GetGrainId());
    }

    [Fact]
    public void BinaryReader_LegacyTwoFieldUniversalReference_UsesLocalDefaultBinding()
    {
        var legacy = new LegacyReferenceData
        {
            GrainId = GrainId.Create("legacy.universal", "legacy-key"),
            InterfaceType = TestInterfaceType
        };

        var actual = ReadWireValue<LegacyReferenceData, UniversalReference>(legacy);

        Assert.Equal(legacy.GrainId, actual.GrainId);
        Assert.Equal(legacy.InterfaceType, actual.InterfaceType);
        Assert.Null(actual.ServiceId);
        Assert.Equal(UniversalReferenceBinding.Virtual, actual.Binding);
        Assert.Null(actual.ClusterId);
    }

    [Fact]
    public void BinaryReader_LegacyTypedReference_ReactivatesWithExpectedInterface()
    {
        var source = CreateSystemTargetReference(ClusterId, out _).GetUniversalReference();
        var legacy = new LegacyReferenceData
        {
            GrainId = source.GrainId,
            InterfaceType = source.InterfaceType
        };
        var universalReference = ReadWireValue<LegacyReferenceData, UniversalReference>(legacy);

        var actual = ReferenceActivator.CreateReference(universalReference);

        Assert.IsAssignableFrom<ISiloControl>(actual);
        Assert.Equal(source.GrainId, actual.GrainId);
        Assert.Equal(source.InterfaceType, actual.InterfaceType);
        Assert.Equal(UniversalReferenceBinding.Virtual, actual.UniversalReference.Binding);
        Assert.False(string.IsNullOrWhiteSpace(actual.UniversalReference.ServiceId));
    }

    [Fact]
    public void GrainReferenceActivator_ActivatesVirtualClusterObserverAndSystemTargetBindings()
    {
        var virtualReference = CreateUntypedReference(64, UniversalReferenceBinding.Virtual);
        var clusterReference = CreateUntypedReference(65, UniversalReferenceBinding.Cluster, ClusterId);
        var observerReference = CreateObserverReference(HomeClusterId);
        var systemTargetReference = CreateSystemTargetReference(ClusterId, out var siloAddress);

        Assert.Equal(UniversalReferenceBinding.Virtual, virtualReference.GetUniversalReference().Binding);
        Assert.Equal(UniversalReferenceBinding.Cluster, clusterReference.GetUniversalReference().Binding);
        Assert.IsAssignableFrom<ISimpleGrainObserver>(observerReference);
        Assert.Equal(HomeClusterId, observerReference.GetUniversalReference().ClusterId);
        Assert.IsAssignableFrom<ISiloControl>(systemTargetReference);
        Assert.True(SystemTargetGrainId.TryParse(systemTargetReference.GetGrainId(), out var systemTargetId));
        Assert.Equal(siloAddress, systemTargetId.GetSiloAddress());
    }

    [Fact]
    public void GrainReferenceActivator_RejectsMalformedUniversalIdentity()
    {
        var defaultIdentity = ReadWireValue<ReferenceWireData, UniversalReference>(new ReferenceWireData
        {
            GrainId = default,
            InterfaceType = TestInterfaceType,
            ServiceId = ServiceId,
            Binding = UniversalReferenceBinding.Virtual
        });
        var missingCluster = ReadWireValue<ReferenceWireData, UniversalReference>(new ReferenceWireData
        {
            GrainId = GrainId.Create("malformed.cluster", "key"),
            InterfaceType = default,
            ServiceId = ServiceId,
            Binding = UniversalReferenceBinding.Cluster,
            ClusterId = null
        });

        var defaultException = Assert.Throws<ArgumentException>(() => ReferenceActivator.CreateReference(defaultIdentity));
        var clusterException = Assert.Throws<ArgumentException>(() => ReferenceActivator.CreateReference(missingCluster));

        Assert.Equal("reference", defaultException.ParamName);
        Assert.Null(clusterException.ParamName);
        Assert.Contains("binding", clusterException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewtonsoftJsonRoundTrip_VirtualReference_PreservesAllFields()
    {
        var source = CreateUntypedReference(66, UniversalReferenceBinding.Virtual);

        var (json, actual) = NewtonsoftRoundTrip<IAddressable>(source);

        Assert.Contains("\"ServiceId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Binding\":0", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ClusterId\"", json, StringComparison.Ordinal);
        AssertAddressable(source.GetUniversalReference(), actual);
    }

    [Fact]
    public void NewtonsoftJsonRoundTrip_ClusterReference_PreservesAllFields()
    {
        var source = CreateUntypedReference(67, UniversalReferenceBinding.Cluster, ClusterId);

        var (json, actual) = NewtonsoftRoundTrip<IAddressable>(source);

        Assert.Contains($"\"ServiceId\":\"{source.GetUniversalReference().ServiceId}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Binding\":1", json, StringComparison.Ordinal);
        Assert.Contains($"\"ClusterId\":\"{ClusterId}\"", json, StringComparison.Ordinal);
        AssertAddressable(source.GetUniversalReference(), actual);
    }

    [Fact]
    public void NewtonsoftJsonReader_LegacyObject_UsesLocalDefaultBinding()
    {
        var source = CreateUntypedReference(68, UniversalReferenceBinding.Virtual).GetUniversalReference();
        var json = $$"""{"Id":{"Type":"{{source.GrainId.Type}}","Key":"{{source.GrainId.Key}}"},"Interface":""}""";

        var actual = (IAddressable)NewtonsoftSerializer.Deserialize(typeof(IAddressable), json)!;
        var reference = actual.GetUniversalReference();

        Assert.Equal(source.GrainId, reference.GrainId);
        Assert.Equal(source.InterfaceType, reference.InterfaceType);
        Assert.Equal(UniversalReferenceBinding.Virtual, reference.Binding);
        Assert.False(string.IsNullOrWhiteSpace(reference.ServiceId));
        Assert.Null(reference.ClusterId);
    }

    [Theory]
    [InlineData("""{"Id":{"Type":"test.grain","Key":"1"},"Interface":"test.interface","ServiceId":"service"}""")]
    [InlineData("""{"Id":{"Type":"test.grain","Key":"1"},"Interface":"test.interface","Binding":0}""")]
    public void NewtonsoftJsonReader_RejectsPartialNewFormat(string json)
    {
        var exception = Assert.Throws<JsonSerializationException>(
            () => NewtonsoftSerializer.Deserialize(typeof(IAddressable), json));

        Assert.Contains("must both be present", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"Id":{"Type":"test.grain","Key":"1"},"Interface":"test.interface","ServiceId":"service","Binding":1}""")]
    [InlineData("""{"Id":{"Type":"test.grain","Key":"1"},"Interface":"test.interface","ServiceId":"service","Binding":1,"ClusterId":null}""")]
    [InlineData("""{"Id":{"Type":"test.grain","Key":"1"},"Interface":"test.interface","ServiceId":"service","Binding":1,"ClusterId":""}""")]
    [InlineData("""{"Id":{"Type":"test.grain","Key":"1"},"Interface":"test.interface","ServiceId":"service","Binding":1,"ClusterId":" "}""")]
    public void NewtonsoftJsonReader_ClusterBindingRequiresClusterId(string json)
    {
        var exception = Assert.Throws<JsonSerializationException>(
            () => NewtonsoftSerializer.Deserialize(typeof(IAddressable), json));

        Assert.Equal(
            "A cluster-bound universal reference must specify a non-empty ClusterId.",
            exception.Message);
        Assert.IsAssignableFrom<ArgumentException>(exception.InnerException);
    }

    [Theory]
    [InlineData(
        """{"Id":{"Type":"test.grain","Key":"1"},"Interface":"test.interface","ServiceId":" ","Binding":0}""",
        "Could not deserialize an invalid universal reference.")]
    [InlineData(
        """{"Id":{"Type":"test.grain","Key":"1"},"Interface":"test.interface","ServiceId":"service","Binding":0,"ClusterId":"unexpected"}""",
        "Could not deserialize an invalid universal reference.")]
    [InlineData(
        """{"Id":{"Type":"test.grain","Key":"1"},"Interface":"test.interface","ServiceId":"service","Binding":255}""",
        "Unknown universal reference binding '255'.")]
    public void NewtonsoftJsonReader_InvalidUniversalReferenceThrowsJsonSerializationException(
        string json,
        string expectedMessage)
    {
        var exception = Assert.Throws<JsonSerializationException>(
            () => NewtonsoftSerializer.Deserialize(typeof(IAddressable), json));

        Assert.Equal(expectedMessage, exception.Message);
        if (!expectedMessage.StartsWith("Unknown", StringComparison.Ordinal))
        {
            Assert.IsAssignableFrom<ArgumentException>(exception.InnerException);
        }
    }

    [Fact]
    public void SystemTextJsonRoundTrip_VirtualReference_PreservesAllFields()
    {
        var source = CreateUntypedReference(69, UniversalReferenceBinding.Virtual);

        var (json, actual) = SystemTextJsonRoundTrip<IAddressable>(source);

        Assert.StartsWith("[{", json, StringComparison.Ordinal);
        Assert.Contains("\"binding\":0", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clusterId", json, StringComparison.OrdinalIgnoreCase);
        AssertAddressable(source.GetUniversalReference(), actual);
    }

    [Fact]
    public void SystemTextJsonRoundTrip_LocalVirtualReference_UsesRollbackCompatibleLayout()
    {
        var source = environment.InternalGrainFactory.GetGrain(
            GrainId.Create("phase2.local-json", "rollback-compatible"));

        var (json, actual) = SystemTextJsonRoundTrip<IAddressable>(source);
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(2, document.RootElement.GetArrayLength());
        Assert.Equal(source.GetGrainId().ToString(), document.RootElement[0].GetString());
        Assert.Equal(source.AsReference().InterfaceType.ToString(), document.RootElement[1].GetString());
        AssertAddressable(source.GetUniversalReference(), actual);
    }

    [Fact]
    public void SystemTextJsonRoundTrip_ClusterReference_PreservesAllFields()
    {
        var source = CreateUntypedReference(70, UniversalReferenceBinding.Cluster, ClusterId);

        var (json, actual) = SystemTextJsonRoundTrip<IAddressable>(source);

        Assert.StartsWith("[{", json, StringComparison.Ordinal);
        Assert.Contains(ClusterId, json, StringComparison.Ordinal);
        AssertAddressable(source.GetUniversalReference(), actual);
    }

    [Fact]
    public void SystemTextJsonRoundTrip_LocalClusterReference_UsesUniversalLayout()
    {
        var bindingResolver = environment.Services.GetRequiredService<UniversalReferenceBindingResolver>();
        var interfaceType = environment.Services
            .GetRequiredService<GrainInterfaceTypeResolver>()
            .GetGrainInterfaceType(typeof(ISiloControl));
        var siloAddress = SiloAddress.New(IPAddress.Loopback, 30_002, generation: 12);
        var grainId = SystemTargetGrainId.Create(Constants.SiloControlType, siloAddress, "local-json").GrainId;
        var reference = UniversalReference.CreateCluster(
            grainId,
            interfaceType,
            bindingResolver.ServiceId,
            bindingResolver.ClusterId);
        var source = (IAddressable)ReferenceActivator.CreateReference(reference);

        var (json, actual) = SystemTextJsonRoundTrip<IAddressable>(source);

        Assert.StartsWith("[{", json, StringComparison.Ordinal);
        Assert.Contains("\"binding\":1", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"\"clusterId\":\"{bindingResolver.ClusterId}\"",
            json,
            StringComparison.OrdinalIgnoreCase);
        AssertAddressable(reference, actual);
    }

    [Fact]
    public void SystemTextJsonReader_LegacyTwoElementArray_UsesLocalDefaultBinding()
    {
        var source = CreateUntypedReference(71, UniversalReferenceBinding.Virtual).GetUniversalReference();
        var grainIdJson = SystemTextJson.Serialize(source.GrainId, SystemTextJsonOptions);
        var json = $"[{grainIdJson},\"\"]";

        var actual = SystemTextJson.Deserialize<IAddressable>(json, SystemTextJsonOptions)!;
        var reference = actual.GetUniversalReference();

        Assert.Equal(source.GrainId, reference.GrainId);
        Assert.Equal(source.InterfaceType, reference.InterfaceType);
        Assert.Equal(UniversalReferenceBinding.Virtual, reference.Binding);
        Assert.False(string.IsNullOrWhiteSpace(reference.ServiceId));
        Assert.Null(reference.ClusterId);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""["test.grain/1"]""")]
    [InlineData("""["test.grain/1","test.interface","extra"]""")]
    [InlineData("""[{"grainId":"test.grain/1","interfaceType":"test.interface","serviceId":"service","binding":1}]""")]
    public void SystemTextJsonReader_RejectsWrongArrayLengthOrMalformedUniversalReference(string json)
    {
        var result = default(IAddressable);

        var exception = Record.Exception(
            () => result = SystemTextJson.Deserialize<IAddressable>(json, SystemTextJsonOptions));

        Assert.Null(result);
        Assert.NotNull(exception);
        Assert.IsType<System.Text.Json.JsonException>(exception);
    }

    [Theory]
    [InlineData("""[{"grainId":"test.grain/1","interfaceType":"test.interface","serviceId":" ","binding":0}]""")]
    [InlineData("""[{"grainId":"test.grain/1","interfaceType":"test.interface","serviceId":"service","binding":255}]""")]
    [InlineData("""[{"grainId":"/","interfaceType":"test.interface","serviceId":"service","binding":0}]""")]
    [InlineData("""[{"grainId":"test.grain/1","interfaceType":"test.interface","serviceId":"service","binding":1}]""")]
    public void SystemTextJsonReader_InvalidUniversalReferenceThrowsJsonException(string json)
    {
        var exception = Assert.Throws<System.Text.Json.JsonException>(
            () => SystemTextJson.Deserialize<IAddressable>(json, SystemTextJsonOptions));

        Assert.Equal("Could not deserialize an invalid universal reference.", exception.Message);
        Assert.IsAssignableFrom<ArgumentException>(exception.InnerException);
    }

    [Fact]
    public void SerializationRoundTrip_ObserverReference_PreservesHomeCluster()
    {
        var source = CreateObserverReference(HomeClusterId);

        var binary = environment.Serializer.Deserialize<ISimpleGrainObserver>(
            environment.Serializer.SerializeToArray<ISimpleGrainObserver>(source));
        var (_, newtonsoft) = NewtonsoftRoundTrip<ISimpleGrainObserver>(source);
        var (_, systemTextJson) = SystemTextJsonRoundTrip<ISimpleGrainObserver>(source);

        AssertAddressable(source.GetUniversalReference(), binary!);
        AssertAddressable(source.GetUniversalReference(), newtonsoft);
        AssertAddressable(source.GetUniversalReference(), systemTextJson);
        Assert.All(
            new[] { binary, newtonsoft, systemTextJson },
            value => Assert.Equal(HomeClusterId, value!.GetUniversalReference().ClusterId));
    }

    [Fact]
    public void SerializationRoundTrip_SystemTargetReference_PreservesClusterAndExactSiloIdentity()
    {
        var source = CreateSystemTargetReference(ClusterId, out var expectedSilo);

        var binary = environment.Serializer.Deserialize<ISiloControl>(
            environment.Serializer.SerializeToArray<ISiloControl>(source));
        var (_, newtonsoft) = NewtonsoftRoundTrip<ISiloControl>(source);
        var (_, systemTextJson) = SystemTextJsonRoundTrip<ISiloControl>(source);

        AssertSystemTarget(source.GetUniversalReference(), binary!, expectedSilo);
        AssertSystemTarget(source.GetUniversalReference(), newtonsoft, expectedSilo);
        AssertSystemTarget(source.GetUniversalReference(), systemTextJson, expectedSilo);
    }

    [Fact]
    public void GrainReferenceRuntime_Cast_ObserverProjectionPreservesUniversalIdentity()
    {
        var source = CreateObserverReference(HomeClusterId);
        var expected = source.GetUniversalReference();

        var actual = source.Cast<IClusterTestListener>();
        var projected = actual.GetUniversalReference();

        Assert.Equal(expected, projected);
        Assert.Equal(expected.GrainId, projected.GrainId);
        Assert.Equal(expected.ServiceId, projected.ServiceId);
        Assert.Equal(expected.Binding, projected.Binding);
        Assert.Equal(expected.ClusterId, projected.ClusterId);
        Assert.NotEqual(expected.InterfaceType, projected.InterfaceType);
    }

    [Fact]
    public void CsCheck_UniversalReferenceBinaryRoundTrip_PreservesEqualityHashAndFields()
    {
        PropertyInputs.Sample(
            input =>
            {
                var expected = CreatePropertyReference(input).GetUniversalReference();
                var actual = BinaryRoundTrip(expected);
                AssertEquivalentRoundTrip(expected, actual, "binary", "0N0XIzNsQ0B1");
            },
            seed: "0N0XIzNsQ0B1",
            iter: 180,
            threads: 1,
            print: Describe);
    }

    [Fact]
    public void CsCheck_UniversalReferenceNewtonsoftJsonRoundTrip_PreservesEqualityHashAndFields()
    {
        PropertyInputs.Sample(
            input =>
            {
                var source = CreatePropertyReference(input);
                var expected = source.GetUniversalReference();
                var (_, actual) = NewtonsoftRoundTrip<IAddressable>(source);
                AssertEquivalentRoundTrip(
                    expected,
                    actual.GetUniversalReference(),
                    "newtonsoft",
                    "0N0XIzNsQ0J1");
            },
            seed: "0N0XIzNsQ0J1",
            iter: 180,
            threads: 1,
            print: Describe);
    }

    [Fact]
    public void CsCheck_UniversalReferenceSystemTextJsonRoundTrip_PreservesEqualityHashAndFields()
    {
        PropertyInputs.Sample(
            input =>
            {
                var source = CreatePropertyReference(input);
                var expected = source.GetUniversalReference();
                var (_, actual) = SystemTextJsonRoundTrip<IAddressable>(source);
                AssertEquivalentRoundTrip(
                    expected,
                    actual.GetUniversalReference(),
                    "system-text-json",
                    "0N0XIzNsQ0S1");
            },
            seed: "0N0XIzNsQ0S1",
            iter: 180,
            threads: 1,
            print: Describe);
    }

    [Fact]
    public void CsCheck_UniversalReferenceSerializationMalformedInput_FailsWithoutPartialValue()
    {
        Gen.Int.Select(static value => (int)((uint)value % 6)).Sample(
            testCase =>
            {
                object? result = null;
                var exception = Record.Exception(() =>
                {
                    result = testCase switch
                    {
                        0 => NewtonsoftSerializer.Deserialize(
                            typeof(IAddressable),
                            """{"Id":{"Type":"test.grain","Key":"1"},"Interface":"test.interface","ServiceId":"service"}"""),
                        1 => NewtonsoftSerializer.Deserialize(
                            typeof(IAddressable),
                            """{"Id":{"Type":"test.grain","Key":"1"},"Interface":"test.interface","Binding":0}"""),
                        2 => NewtonsoftSerializer.Deserialize(
                            typeof(IAddressable),
                            """{"Id":{"Type":"test.grain","Key":"1"},"Interface":"test.interface","ServiceId":"service","Binding":255}"""),
                        3 => SystemTextJson.Deserialize<IAddressable>("[]", SystemTextJsonOptions),
                        4 => SystemTextJson.Deserialize<IAddressable>(
                            """["test.grain/1","test.interface","extra"]""",
                            SystemTextJsonOptions),
                        _ => SystemTextJson.Deserialize<IAddressable>(
                            """[{"grainId":"test.grain/1","interfaceType":"test.interface","serviceId":"service","binding":1}]""",
                            SystemTextJsonOptions)
                    };
                });
                var history = $"seed=0N0XIzNsQ0M1; malformed-case={testCase}; result={result}; exception={exception}";

                Assert.True(exception is JsonException or System.Text.Json.JsonException or ArgumentException, history);
                Assert.Null(result);
            },
            seed: "0N0XIzNsQ0M1",
            iter: 180,
            threads: 1,
            print: static value => $"malformed-case={value}");
    }

    private GrainReferenceActivator ReferenceActivator =>
        environment.Services.GetRequiredService<GrainReferenceActivator>();

    private OrleansJsonSerializer NewtonsoftSerializer =>
        new(Options.Create(new OrleansJsonSerializerOptions
        {
            JsonSerializerSettings = OrleansJsonSerializerSettings.GetDefaultSerializerSettings(environment.Services)
        }));

    private System.Text.Json.JsonSerializerOptions SystemTextJsonOptions
    {
        get
        {
            var options = new SystemTextJsonGrainStorageSerializerOptions();
            new SystemTextJsonSerializerOptionsConfigure(ReferenceActivator).PostConfigure(null, options);
            return options.JsonSerializerOptions;
        }
    }

    private IAddressable CreateUntypedReference(
        long key,
        UniversalReferenceBinding binding,
        string? clusterId = null)
    {
        var grainId = GrainId.Create(
            (key & 1) == 0 ? "phase2.int-key" : "phase2.string-key",
            (key & 1) == 0
                ? key.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"key/{key}");
        var reference = binding == UniversalReferenceBinding.Virtual
            ? UniversalReference.CreateVirtual(grainId, default, ServiceId)
            : UniversalReference.CreateCluster(grainId, default, ServiceId, clusterId!);
        return ReferenceActivator.CreateReference(reference);
    }

    private IAddressable CreatePropertyReference((int Key, int Service, int Cluster) input)
    {
        var serviceId = $"service-{(uint)input.Service % 7}";
        var clusterId = $"cluster-{(uint)input.Cluster % 5}";
        var binding = (input.Cluster & 1) == 0
            ? UniversalReferenceBinding.Virtual
            : UniversalReferenceBinding.Cluster;
        var shape = (int)((uint)input.Key % 3);
        UniversalReference reference;
        if (shape == 0)
        {
            var grainId = GrainId.Create(
                (input.Key & 1) == 0 ? "property.int-key" : "property.string-key",
                (input.Key & 1) == 0
                    ? input.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : $"key/{(uint)input.Key:X8}");
            reference = binding == UniversalReferenceBinding.Virtual
                ? UniversalReference.CreateVirtual(grainId, default, serviceId)
                : UniversalReference.CreateCluster(grainId, default, serviceId, clusterId);
        }
        else if (shape == 1)
        {
            var interfaceType = environment.Services
                .GetRequiredService<GrainInterfaceTypeResolver>()
                .GetGrainInterfaceType(typeof(ISimpleGrainObserver));
            var grainId = ObserverGrainId.Create(
                ClientGrainId.Create($"property-observer-{(uint)input.Key:X8}")).GrainId;
            reference = binding == UniversalReferenceBinding.Virtual
                ? UniversalReference.CreateVirtual(grainId, interfaceType, serviceId)
                : UniversalReference.CreateCluster(grainId, interfaceType, serviceId, clusterId);
        }
        else
        {
            var interfaceType = environment.Services
                .GetRequiredService<GrainInterfaceTypeResolver>()
                .GetGrainInterfaceType(typeof(ISiloControl));
            var port = 20_000 + (int)((uint)input.Key % 10_000);
            var generation = 1 + (int)((uint)input.Service % 1_000);
            var grainId = SystemTargetGrainId.Create(
                Constants.SiloControlType,
                SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), generation),
                $"property-{(uint)input.Cluster:X8}").GrainId;
            reference = binding == UniversalReferenceBinding.Virtual
                ? UniversalReference.CreateVirtual(grainId, interfaceType, serviceId)
                : UniversalReference.CreateCluster(grainId, interfaceType, serviceId, clusterId);
        }

        return ReferenceActivator.CreateReference(reference);
    }

    private ISimpleGrainObserver CreateObserverReference(string clusterId)
    {
        var interfaceType = environment.Services
            .GetRequiredService<GrainInterfaceTypeResolver>()
            .GetGrainInterfaceType(typeof(ISimpleGrainObserver));
        var grainId = ObserverGrainId.Create(ClientGrainId.Create("phase2-observer-client")).GrainId;
        var reference = UniversalReference.CreateCluster(grainId, interfaceType, ServiceId, clusterId);
        return (ISimpleGrainObserver)ReferenceActivator.CreateReference(reference);
    }

    private ISiloControl CreateSystemTargetReference(string clusterId, out SiloAddress siloAddress)
    {
        siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 32145), 17);
        var interfaceType = environment.Services
            .GetRequiredService<GrainInterfaceTypeResolver>()
            .GetGrainInterfaceType(typeof(ISiloControl));
        var grainId = SystemTargetGrainId.Create(Constants.SiloControlType, siloAddress, "phase2").GrainId;
        var reference = UniversalReference.CreateCluster(grainId, interfaceType, ServiceId, clusterId);
        return (ISiloControl)ReferenceActivator.CreateReference(reference);
    }

    private UniversalReference BinaryRoundTrip(UniversalReference value) =>
        environment.Serializer.Deserialize<UniversalReference>(environment.Serializer.SerializeToArray(value));

    private (string Json, T Value) NewtonsoftRoundTrip<T>(T source)
        where T : class, IAddressable
    {
        var json = NewtonsoftSerializer.Serialize(source, typeof(T));
        return (json, (T)NewtonsoftSerializer.Deserialize(typeof(T), json)!);
    }

    private (string Json, T Value) SystemTextJsonRoundTrip<T>(T source)
        where T : class, IAddressable
    {
        var json = SystemTextJson.Serialize(source, typeof(T), SystemTextJsonOptions);
        return (json, (T)SystemTextJson.Deserialize(json, typeof(T), SystemTextJsonOptions)!);
    }

    private TTarget ReadWireValue<TWire, TTarget>(TWire value)
        where TWire : struct
        where TTarget : struct
    {
        var wireSerializer = environment.Services.GetRequiredService<IValueSerializer<TWire>>();
        var targetSerializer = environment.Services.GetRequiredService<IValueSerializer<TTarget>>();
        var sessionPool = environment.Services.GetRequiredService<SerializerSessionPool>();

        using var writerSession = sessionPool.GetSession();
        var writer = Writer.CreatePooled(writerSession);
        wireSerializer.Serialize(ref writer, ref value);
        writer.WriteEndObject();
        writer.Commit();

        using var readerSession = sessionPool.GetSession();
        var reader = Reader.Create(writer.Output.AsReadOnlySequence(), readerSession);
        var result = default(TTarget)!;
        targetSerializer.Deserialize(ref reader, ref result);
        return result;
    }

    private static void AssertAddressable(UniversalReference expected, IAddressable actual) =>
        AssertUniversalReference(expected, actual.GetUniversalReference());

    private static void AssertUniversalReference(UniversalReference expected, UniversalReference actual)
    {
        Assert.Equal(expected, actual);
        Assert.Equal(expected.GrainId, actual.GrainId);
        Assert.Equal(expected.InterfaceType, actual.InterfaceType);
        Assert.Equal(expected.ServiceId, actual.ServiceId);
        Assert.Equal(expected.Binding, actual.Binding);
        Assert.Equal(expected.ClusterId, actual.ClusterId);
        Assert.Equal(expected.GetHashCode(), actual.GetHashCode());
        Assert.Equal(expected.GetUniformHashCode(), actual.GetUniformHashCode());
    }

    private static void AssertSystemTarget(
        UniversalReference expected,
        IAddressable actual,
        SiloAddress expectedSilo)
    {
        AssertAddressable(expected, actual);
        Assert.True(SystemTargetGrainId.TryParse(actual.GetGrainId(), out var systemTargetId));
        Assert.Equal(expectedSilo, systemTargetId.GetSiloAddress());
        Assert.Equal(ClusterId, actual.GetUniversalReference().ClusterId);
    }

    private static void AssertEquivalentRoundTrip(
        UniversalReference expected,
        UniversalReference actual,
        string format,
        string seed)
    {
        var history = $"{format}; seed={seed}; input={Describe(expected)}";
        Assert.True(expected.Equals(actual), history);
        Assert.True(expected.GetHashCode() == actual.GetHashCode(), history);
        Assert.True(expected.GetUniformHashCode() == actual.GetUniformHashCode(), history);
        Assert.True(expected.GrainId.Equals(actual.GrainId), history);
        Assert.True(expected.InterfaceType.Equals(actual.InterfaceType), history);
        Assert.True(string.Equals(expected.ServiceId, actual.ServiceId, StringComparison.Ordinal), history);
        Assert.True(expected.Binding == actual.Binding, history);
        Assert.True(string.Equals(expected.ClusterId, actual.ClusterId, StringComparison.Ordinal), history);
    }

    private static string Describe(UniversalReference value) =>
        $"grain={value.GrainId}; interface={value.InterfaceType}; service={value.ServiceId}; binding={value.Binding}; cluster={value.ClusterId ?? "<null>"}";

    private static string Describe((int Key, int Service, int Cluster) value) =>
        $"key={value.Key}; service={value.Service}; cluster={value.Cluster}";

    [GenerateSerializer]
    public struct LegacyReferenceData
    {
        [Id(0)]
        public GrainId GrainId;

        [Id(1)]
        public GrainInterfaceType InterfaceType;
    }

    [GenerateSerializer]
    public struct ReferenceWireData
    {
        [Id(0)]
        public GrainId GrainId;

        [Id(1)]
        public GrainInterfaceType InterfaceType;

        [Id(2)]
        public string? ServiceId;

        [Id(3)]
        public UniversalReferenceBinding Binding;

        [Id(4)]
        public string? ClusterId;
    }
}
