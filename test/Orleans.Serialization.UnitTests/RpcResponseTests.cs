using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class RpcResponseTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    [Fact]
    public void ConcreteResponseCodecsPreserveLegacyWireFormat()
    {
        Check(false, new BoolCodec());
        Check(true, new BoolCodec());
        Check(0, new Int32Codec());
        Check(42, new Int32Codec());
        Check(-17, new Int32Codec());
        Check<string, StringCodec>(null!, new StringCodec());

        void Check<T, TCodec>(T value, TCodec resultCodec) where TCodec : class, IFieldCodec<T>
        {
            using var response = Response.FromResult(value);
            var concrete = new ResponseCodec<T, TCodec>(resultCodec);
            var legacy = new ResponseCodec<T>(_services.GetRequiredService<CodecProvider>());
            var expected = Write(legacy, (Response<T>)response);
            var actual = Write(concrete, (Response<T>)response);
            Assert.Equal(expected, actual);

            using var session = _services.GetRequiredService<SerializerSessionPool>().GetSession();
            var reader = Reader.Create(actual, session);
            using var roundTrip = concrete.ReadValue(ref reader, reader.ReadFieldHeader());
            Assert.NotNull(roundTrip);
            Assert.Equal(value, roundTrip.TypedResult);
        }
    }

    [Fact]
    public void ConcreteResponseCodecPreservesRawMessageEncoding()
    {
        var codec = new ResponseCodec<int, Int32Codec>(new Int32Codec());
        using var response = Response.FromResult(42);
        var buffer = new ArrayBufferWriter<byte>();
        var sessions = _services.GetRequiredService<SerializerSessionPool>();
        using (var session = sessions.GetSession())
        {
            var writer = Writer.Create(buffer, session);
            codec.WriteRaw(ref writer, response);
            writer.Commit();
        }

        using var readerSession = sessions.GetSession();
        var reader = Reader.Create(buffer.WrittenMemory, readerSession);
        var header = reader.ReadFieldHeader();
        Assert.Equal(typeof(int), header.FieldType);
        using var result = (Response<int>)codec.ReadRaw(ref reader, ref header);
        Assert.Equal(42, result.TypedResult);
    }

    [Fact]
    public void ConcreteResponseCopiersPreserveNullAndDistinctEnvelopes()
    {
        var copier = new ResponseCopier<int, ShallowCopier<int>>(new ShallowCopier<int>());
        using var context = _services.GetRequiredService<CopyContextPool>().GetContext();
        Assert.Null(copier.DeepCopy(null, context));
        using var response = (Response<int>)Response.FromResult(42);
        var copy = copier.DeepCopy(response, context);
        Assert.NotSame(response, copy);
        Assert.Equal(42, copy.TypedResult);
        copy.Dispose();
        Assert.Equal(0, copy.TypedResult);
        Assert.Equal(42, response.TypedResult);
    }

    [Fact]
    public void ConcreteResponseFactoriesValidateResultDependencies()
    {
        Assert.Equal("codec", Assert.Throws<ArgumentNullException>(() => new ResponseCodec<int, Int32Codec>((Int32Codec)null!)).ParamName);
        Assert.Equal("copier", Assert.Throws<ArgumentNullException>(() => new ResponseCopier<int, ShallowCopier<int>>((ShallowCopier<int>)null!)).ParamName);
    }

    [Fact]
    public void GeneratedResponseFactoriesCopyAndSerializePrimitiveResults()
        => NativeAotSmoke.RpcResponseContracts.PrimitiveResponses();

    [Fact]
    public void RegisteredResponseCodecsPreservePublicationContracts()
        => NativeAotSmoke.RpcResponseContracts.ResponseCodecPublication();

    [Fact]
    public void GeneratedResponseFactoriesPreserveReferencePayloadCycles()
        => NativeAotSmoke.RpcResponseContracts.ReferenceResponsePreservesCycles();

    [Fact]
    public void GeneratedResponseFactoriesPreserveNullPayloads()
        => NativeAotSmoke.RpcResponseContracts.NullResponsePayload();

    [Fact]
    public void GeneratedResponseFactoriesPreserveRawMessageResponses()
        => NativeAotSmoke.RpcResponseContracts.RawResponses();

    [Fact]
    public void GeneratedResponseFactoriesPreserveCompletionAndExceptionIdentity()
    {
        var provider = _services.GetRequiredService<CodecProvider>();
        Assert.IsType<ShallowCopier<CompletedResponse>>(provider.GetDeepCopier<CompletedResponse>());
        Assert.IsType<ShallowCopier<ExceptionResponse>>(provider.GetDeepCopier<ExceptionResponse>());
        NativeAotSmoke.RpcResponseContracts.CompletedAndExceptionResponses();
    }

    [Fact]
    public void GeneratedCompletedResponsesPreserveTransportIdentity()
        => NativeAotSmoke.RpcResponseContracts.CompletedResponseRoundTrip();

    [Fact]
    public void GeneratedValueAndArrayServicesRoundTripAndCopy()
        => NativeAotSmoke.RpcResponseContracts.CanonicalValueAndArrayServices();

    [Fact]
    public void CanonicalTupleArgumentConstructionPreservesIdentity()
        => NativeAotSmoke.RpcResponseContracts.ConstructTupleArgumentProxyBeforeInvocation();

    [Fact]
    public void ContextRegistrationPreservesExistingExceptionMetadata()
    {
        using var services = new ServiceCollection().AddSerializer().AddSerializerContext(new EmptyContext()).BuildServiceProvider();
        Assert.NotNull(services.GetRequiredService<CodecProvider>().GetCodec<ExceptionResponse>());
    }

    private sealed class EmptyContext : SerializerContext
    {
        protected override void ConfigureInner(TypeManifestOptions options) { }
    }

    [Fact]
    public void MultidimensionalArrayTransportAndCopyPreserveConcreteTypeCyclesAndAliases()
    {
        var shared = new List<int> { 47, 59 };
        var original = new object[1, 3];
        original[0, 0] = original;
        original[0, 1] = shared;
        original[0, 2] = shared;

        var copy = _services.GetRequiredService<DeepCopier>().Copy(original);
        var serializer = _services.GetRequiredService<Serializer>();
        var result = Assert.IsType<object[,]>(serializer.Deserialize<object[,]>(serializer.SerializeToArray(original)));

        Assert.NotSame(original, copy);
        Assert.Same(copy, copy[0, 0]);
        Assert.NotSame(shared, copy[0, 1]);
        Assert.Same(copy[0, 1], copy[0, 2]);
        Assert.Equal(new[] { 47, 59 }, Assert.IsType<List<int>>(copy[0, 1]));
        Assert.NotSame(original, result);
        Assert.Same(result, result[0, 0]);
        Assert.Same(result[0, 1], result[0, 2]);
        Assert.Equal(new[] { 47, 59 }, Assert.IsType<List<int>>(result[0, 1]));
        shared.Clear();
        Assert.Equal(new[] { 47, 59 }, Assert.IsType<List<int>>(copy[0, 1]));
        Assert.Equal(new[] { 47, 59 }, Assert.IsType<List<int>>(result[0, 1]));
    }

    [Fact]
    public void CompoundAliasTraversalPreservesPrefixesAndAddClearsThem()
    {
        var tree = Orleans.Serialization.TypeSystem.CompoundTypeAliasTree.Create();
        var prefix = tree.Add("rpc.prefix", typeof(int));
        var child = tree.GetOrAdd("rpc.prefix").Add("child", typeof(string));
        Assert.Same(prefix, tree.GetOrAdd("rpc.prefix"));
        Assert.Equal(typeof(int), prefix.Value);
        Assert.Equal(typeof(string), child.Value);
        tree.Add("rpc.prefix");
        Assert.Null(prefix.Value);
        tree.Add("rpc.prefix", typeof(long));
        Assert.Equal(typeof(long), tree.GetOrAdd("rpc.prefix").Value);

        var typePrefix = tree.Add(typeof(RpcResponseTests), typeof(int));
        Assert.Same(typePrefix, tree.GetOrAdd(typeof(RpcResponseTests)));
        Assert.Equal(typeof(int), typePrefix.Value);
        tree.Add(typeof(RpcResponseTests));
        Assert.Null(typePrefix.Value);
        tree.Add(typeof(RpcResponseTests), typeof(long));
        Assert.Equal(typeof(long), tree.GetOrAdd(typeof(RpcResponseTests)).Value);
    }

    [Fact]
    public void CompoundResponseAliasesResolveAndRoundTrip()
    {
        var converter = _services.GetRequiredService<Orleans.Serialization.TypeSystem.TypeConverter>();
        Assert.Equal(typeof(RpcMultipleAliasPayload), converter.Parse("(\"rpc.response.multiple\",\"2\")"));
        Assert.Equal(typeof(RpcMultipleAliasPayload), converter.Parse("(\"rpc.response.multiple\",\"1\")"));
        var serializer = _services.GetRequiredService<Serializer>();
        var multiple = new List<RpcMultipleAliasPayload> { new() { Value = 79 } };
        var result = serializer.Deserialize<List<RpcMultipleAliasPayload>>(serializer.SerializeToArray(multiple));
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(79, result[0].Value);
        var nested = new List<RpcNestedAliasPayload> { new() { Value = 89 } };
        var nestedResult = serializer.Deserialize<List<RpcNestedAliasPayload>>(serializer.SerializeToArray(nested));
        Assert.NotNull(nestedResult);
        Assert.Single(nestedResult);
        Assert.Equal(89, nestedResult[0].Value);
    }

    [Fact]
    public void GeneratedResponseFactoriesPreserveCanonicalClosedImplementations()
    {
        var provider = _services.GetRequiredService<CodecProvider>();
        Assert.IsType<ResponseCodec<int, Int32Codec>>(provider.GetCodec<Response<int>>());
        Assert.IsType<ResponseCopier<int, ShallowCopier<int>>>(provider.GetDeepCopier<Response<int>>());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void InferredResponseParentsHonorExplicitChildFactories(
        bool explicitFirst, bool describeCanonicalServices, bool consumeChildContracts)
    {
        foreach (var untyped in new[] { false, true })
        {
            var payloadCodec = new DelegatingCodec<int>(new Int32Codec());
            var payloadCopier = new TransformingIntCopier();
            var codecCalls = 0;
            var copierCalls = 0;
            var options = new TypeManifestOptions();
            options.AddSerializer(typeof(ResponseCodec<>), typeof(Response<>));
            options.AddCopier(typeof(ResponseCopier<>), typeof(Response<>));
            if (explicitFirst) RegisterExplicit();
            if (describeCanonicalServices)
            {
                options.AddDefaultSerializerService<Int32Codec>(static _ => new(), []);
                options.AddDefaultSerializerService<ShallowCopier<int>>(static _ => new(), []);
            }
            else
            {
                options.AddDefaultSerializerService<Int32Codec>(static _ => new());
                options.AddDefaultSerializerService<ShallowCopier<int>>(static _ => new());
            }
            options.AddDefaultSerializer<int, Int32Codec, ShallowCopier<int>>(
                static provider => OrleansGeneratedCodeHelper.GetService<Int32Codec>(null!, provider),
                static provider => OrleansGeneratedCodeHelper.GetService<ShallowCopier<int>>(null!, provider));
            if (consumeChildContracts) RegisterParent<IFieldCodec<int>, IDeepCopier<int>>();
            else RegisterParent<Int32Codec, ShallowCopier<int>>();
            if (!explicitFirst) RegisterExplicit();
            using var services = new ServiceCollection().AddSerializer()
                .AddSingleton<Microsoft.Extensions.Options.IOptions<TypeManifestOptions>>(Microsoft.Extensions.Options.Options.Create(options))
                .BuildServiceProvider();
            var provider = services.GetRequiredService<CodecProvider>();
            var codec = untyped
                ? Assert.IsAssignableFrom<IFieldCodec<Response<int>>>(provider.GetCodec(typeof(Response<int>)))
                : provider.GetCodec<Response<int>>();
            var copier = untyped
                ? Assert.IsAssignableFrom<IDeepCopier<Response<int>>>(provider.GetDeepCopier(typeof(Response<int>)))
                : provider.GetDeepCopier<Response<int>>();
            if (!consumeChildContracts)
            {
                Assert.IsType<ResponseCodec<int>>(codec);
                Assert.IsType<ResponseCopier<int>>(copier);
            }

            using var response = (Response<int>)Response.FromResult(42);
            var bytes = Write(codec, response);
            using var session = _services.GetRequiredService<SerializerSessionPool>().GetSession();
            var reader = Reader.Create(bytes, session);
            using var roundTrip = codec.ReadValue(ref reader, reader.ReadFieldHeader());
            Assert.NotNull(roundTrip);
            Assert.Equal(42, roundTrip.TypedResult);
            Assert.Equal(1, payloadCodec.Writes);
            Assert.Equal(1, payloadCodec.Reads);
            using var context = _services.GetRequiredService<CopyContextPool>().GetContext();
            using var copy = copier.DeepCopy(response, context);
            Assert.Equal(43, copy.TypedResult);
            Assert.Equal(1, payloadCopier.Copies);
            Assert.Equal(consumeChildContracts ? 1 : 0, codecCalls);
            Assert.Equal(consumeChildContracts ? 1 : 0, copierCalls);
            Assert.Same(payloadCodec, provider.GetCodec<int>());
            Assert.Same(payloadCopier, provider.GetDeepCopier<int>());
            Assert.False(provider.IsConstructionPending);

            void RegisterExplicit() => options.AddSerializer<int>(_ => payloadCodec, _ => payloadCopier);

            void RegisterParent<TCodec, TCopier>()
                where TCodec : class, IFieldCodec<int>
                where TCopier : class, IDeepCopier<int>
            {
                options.AddDefaultSerializerService<ResponseCodec<int, TCodec>>(provider =>
                {
                    codecCalls++;
                    return new(OrleansGeneratedCodeHelper.GetService<TCodec>(null!, provider));
                }, [typeof(TCodec)]);
                options.AddDefaultSerializerService<ResponseCopier<int, TCopier>>(provider =>
                {
                    copierCalls++;
                    return new(OrleansGeneratedCodeHelper.GetService<TCopier>(null!, provider));
                }, [typeof(TCopier)]);
                options.AddDefaultSerializer<Response<int>, ResponseCodec<int, TCodec>, ResponseCopier<int, TCopier>>(
                    static provider => OrleansGeneratedCodeHelper.GetService<ResponseCodec<int, TCodec>>(null!, provider),
                    static provider => OrleansGeneratedCodeHelper.GetService<ResponseCopier<int, TCopier>>(null!, provider),
                    codecDependencies: [typeof(TCodec)], copierDependencies: [typeof(TCopier)]);
            }
        }
    }

    [Fact]
    public void InferredResponseFactoriesInspectDefinitionsWithoutMaterializingLegacyImplementations()
    {
        var codecDefinition = new DefinitionOnlyType(typeof(ResponseCodec<>));
        var copierDefinition = new DefinitionOnlyType(typeof(ResponseCopier<>));
        var options = new TypeManifestOptions();
        options.AddSerializer(codecDefinition, typeof(Response<>));
        options.AddCopier(copierDefinition, typeof(Response<>));
        options.AddDefaultSerializer<Response<int>, ResponseCodec<int, Int32Codec>, ResponseCopier<int, ShallowCopier<int>>>(
            static _ => new ResponseCodec<int, Int32Codec>(new Int32Codec()),
            static _ => new ResponseCopier<int, ShallowCopier<int>>(new ShallowCopier<int>()));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Microsoft.Extensions.Options.Options.Create(options));

        Assert.IsType<ResponseCodec<int, Int32Codec>>(provider.GetCodec<Response<int>>());
        Assert.IsType<ResponseCopier<int, ShallowCopier<int>>>(provider.GetDeepCopier<Response<int>>());
        Assert.Equal(0, codecDefinition.MaterializationCalls);
        Assert.Equal(0, copierDefinition.MaterializationCalls);
    }

    private sealed class DefinitionOnlyType(Type definition) : System.Reflection.TypeDelegator(definition)
    {
        public int MaterializationCalls { get; private set; }
        public override bool IsGenericType => typeImpl.IsGenericType;
        public override bool IsGenericTypeDefinition => typeImpl.IsGenericTypeDefinition;
        public override bool IsConstructedGenericType => typeImpl.IsConstructedGenericType;
        public override Type GetGenericTypeDefinition() => typeImpl.GetGenericTypeDefinition();
        public override Type[] GetGenericArguments() => typeImpl.GetGenericArguments();

        public override Type MakeGenericType(params Type[] typeArguments)
        {
            MaterializationCalls++;
            throw new InvalidOperationException("The inferred factory supplies its closed executable implementation.");
        }
    }

    [Fact]
    public void AutomaticResponseFactoriesPreserveCustomJitPayloadCopier()
    {
        using var services = new ServiceCollection()
            .AddSerializer(builder => builder.Configure(options => options.AddCopier(typeof(CustomPayloadCopier))))
            .BuildServiceProvider();
        var payload = new NativeAotSmoke.RpcResponsePayload { Value = 42 };
        using var response = Response.FromResult(payload);
        using var copy = services.GetRequiredService<DeepCopier>().Copy(response);
        Assert.NotSame(response, copy);
        Assert.Same(payload, copy.GetResult<NativeAotSmoke.RpcResponsePayload>());
        Assert.IsType<ResponseCopier<NativeAotSmoke.RpcResponsePayload>>(
            services.GetRequiredService<CodecProvider>().GetDeepCopier(response.GetType()));
    }

    [GenerateSerializer, CompoundTypeAlias("rpc.response.multiple", "2"), CompoundTypeAlias("rpc.response.multiple", "1")]
    public sealed class RpcMultipleAliasPayload
    {
        [Id(0)]
        public int Value { get; set; }
    }

    [CompoundTypeAlias("rpc.response.marker")]
    public sealed class RpcAliasMarker { }

    [GenerateSerializer, CompoundTypeAlias(typeof(RpcAliasMarker), "payload")]
    public sealed class RpcNestedAliasPayload
    {
        [Id(0)]
        public int Value { get; set; }
    }

    [Fact]
    public void LegacyJitDictionaryResponsesPreserveCustomComparers()
    {
        var dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["entry"] = 42 };
        using var response = (Response<Dictionary<string, int>>)Response.FromResult(dictionary);
        using var copy = _services.GetRequiredService<DeepCopier>().Copy(response);
        Assert.NotSame(dictionary, copy.TypedResult);
        Assert.Same(StringComparer.OrdinalIgnoreCase, copy.TypedResult!.Comparer);
        Assert.Equal(42, copy.TypedResult["ENTRY"]);
        var serializer = _services.GetRequiredService<Serializer>();
        using var result = serializer.Deserialize<Response<Dictionary<string, int>>>(serializer.SerializeToArray(response));
        Assert.NotNull(result);
        Assert.NotSame(dictionary, result.TypedResult);
        Assert.Same(StringComparer.OrdinalIgnoreCase, result.TypedResult!.Comparer);
        Assert.Equal(42, result.TypedResult["ENTRY"]);
    }

    [Theory]
    [InlineData("None", false, false)]
    [InlineData("None", false, true)]
    [InlineData("ImplementationType", false, false)]
    [InlineData("ImplementationType", false, true)]
    [InlineData("ImplementationFactory", false, false)]
    [InlineData("ImplementationFactory", false, true)]
    [InlineData("Instance", false, false)]
    [InlineData("Instance", false, true)]
    [InlineData("SerializerFactory", true, false)]
    [InlineData("SerializerFactory", true, true)]
    public void DefaultFactoryConstructorDependenciesRespectPublicationBoundary(string registration, bool eligible, bool bridgeFirst)
    {
        var dependency = new FactoryDependency();
        var codec = new Int32Codec();
        var calls = 0;
        var collection = new ServiceCollection();
        if (registration == "ImplementationType") collection.AddSingleton<FactoryDependency>();
        if (registration == "ImplementationFactory") collection.AddSingleton(_ => dependency);
        if (registration == "Instance") collection.AddSingleton(dependency);
        using var services = collection.Configure<TypeManifestOptions>(options =>
        {
            if (registration == "SerializerFactory")
                options.AddSerializerService<FactoryDependency>(_ => dependency);
            options.AddDefaultSerializerService<DependencyBoundFactoryService, DependencyBoundFactoryService>(
                provider => new(OrleansGeneratedCodeHelper.GetService<FactoryDependency>(null!, provider)),
                dependencies: [typeof(FactoryDependency)]);
            var bridgeDependencies = new[] { typeof(DependencyBoundFactoryService) };
            options.AddDefaultSerializerService<ConstructorBridge>(
                provider => new(OrleansGeneratedCodeHelper.GetService<DependencyBoundFactoryService>(null!, provider)),
                bridgeDependencies);
            bridgeDependencies[0] = typeof(InvalidOperationException);
            if (bridgeFirst) RegisterBridge(options);
            options.AddDefaultSerializerService<Int32Codec>(provider =>
            {
                calls++;
                var bridge = OrleansGeneratedCodeHelper.GetService<ConstructorBridge>(null!, provider);
                Assert.Same(dependency, bridge.Service.Dependency);
                return codec;
            }, [typeof(ConstructorBridge)]);
            options.AddDefaultSerializer<int, Int32Codec, ShallowCopier<int>>(
                provider => OrleansGeneratedCodeHelper.GetService<Int32Codec>(null!, provider),
                static _ => new ShallowCopier<int>(), codecDependencies: [typeof(ConstructorBridge)]);
            if (!bridgeFirst) RegisterBridge(options);
        }).AddSerializer().BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var result = provider.GetCodec<int>();
        var concrete = OrleansGeneratedCodeHelper.GetService<Int32Codec>(null!, provider);
        Assert.Equal(eligible ? 1 : 0, calls);
        if (eligible)
        {
            Assert.Same(codec, result);
            Assert.Same(codec, concrete);
            Assert.Same(dependency, OrleansGeneratedCodeHelper.GetService<DependencyBoundFactoryService>(null!, provider).Dependency);
        }
        else
        {
            Assert.IsType<Int32Codec>(result);
            Assert.NotSame(codec, result);
            Assert.NotSame(codec, concrete);
        }

        void RegisterBridge(TypeManifestOptions options)
            => options.AddDefaultSerializerService<IFieldCodec<int>>(
                _ => codec, [typeof(ConstructorBridge)]);
    }

    private sealed class FactoryDependency;

    private sealed class DependencyBoundFactoryService(FactoryDependency dependency)
    {
        public FactoryDependency Dependency { get; } = dependency;
    }

    private sealed class ConstructorBridge(DependencyBoundFactoryService service)
    {
        public DependencyBoundFactoryService Service { get; } = service;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultFactoryRegistrationsYieldToExplicitFactories(bool explicitFirst)
    {
        var codec = new Int32Codec();
        var copier = new ShallowCopier<int>();
        var service = new FactoryService();
        using var services = new ServiceCollection()
            .AddSerializer(builder => builder.Configure(options =>
            {
                if (explicitFirst) RegisterExplicit(options);
                options.AddDefaultSerializer<int>(static _ => new Int32Codec(), static _ => new ShallowCopier<int>());
                options.AddDefaultSerializerService<FactoryService>(static _ => new FactoryService());
                options.AddDefaultSerializer<int>(static _ => throw new InvalidOperationException("duplicate default codec"), static _ => throw new InvalidOperationException("duplicate default copier"));
                options.AddDefaultSerializerService<FactoryService>(static _ => throw new InvalidOperationException("duplicate default service"));
                if (!explicitFirst) RegisterExplicit(options);
            }))
            .BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        Assert.Same(codec, provider.GetCodec<int>());
        Assert.Same(copier, provider.GetDeepCopier<int>());
        Assert.Same(service, OrleansGeneratedCodeHelper.GetService<FactoryService>(null!, provider));

        void RegisterExplicit(TypeManifestOptions options)
        {
            options.AddSerializer<int>(_ => codec, _ => copier);
            options.AddSerializerService<FactoryService>(_ => service);
        }
    }

    [Fact]
    public void DuplicateDefaultFactoryRegistrationsUseFirstImplementation()
    {
        var codec = new Int32Codec();
        var copier = new ShallowCopier<int>();
        var service = new FactoryService();
        using var services = new ServiceCollection()
            .Configure<TypeManifestOptions>(options =>
            {
                options.AddDefaultSerializer<int>(_ => codec, _ => copier);
                options.AddDefaultSerializerService<FactoryService>(_ => service);
            })
            .Configure<TypeManifestOptions>(options =>
            {
                options.AddDefaultSerializer<int>(static _ => throw new InvalidOperationException("second manifest codec"),
                    static _ => throw new InvalidOperationException("second manifest copier"));
                options.AddDefaultSerializerService<FactoryService>(static _ => throw new InvalidOperationException("second manifest service"));
            })
            .AddSerializer()
            .BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        Assert.Same(codec, provider.GetCodec<int>());
        Assert.Same(copier, provider.GetDeepCopier<int>());
        Assert.Same(service, OrleansGeneratedCodeHelper.GetService<FactoryService>(null!, provider));
    }

    [Theory]
    [InlineData("Boolean")]
    [InlineData("Integer")]
    [InlineData("Payload")]
    public async Task GeneratedInvokablesReturnClosedResponses(string methodName)
    {
        var target = new RpcResponseTarget();
        using var invokable = CreateInvokable(methodName);
        invokable.SetTarget(new TargetHolder(target));
        using var response = await invokable.Invoke();
        using var copy = new DeepCopier<Response>(_services.GetRequiredService<CodecProvider>().GetDeepCopier<Response>(),
            _services.GetRequiredService<CopyContextPool>()).Copy(response);
        if (methodName == "Boolean")
        {
            Assert.IsType<Response<bool>>(response);
            Assert.True(copy.GetResult<bool>());
        }
        else if (methodName == "Integer")
        {
            Assert.IsType<Response<int>>(response);
            Assert.Equal(42, copy.GetResult<int>());
        }
        else
        {
            Assert.IsType<Response<NativeAotSmoke.RpcResponsePayload>>(response);
            var result = copy.GetResult<NativeAotSmoke.RpcResponsePayload>();
            Assert.NotNull(result);
            Assert.NotSame(target.Result, result);
            Assert.Equal(17, result.Value);
        }
    }

    [Fact]
    public async Task GeneratedInvokablesPreserveExceptionResponses()
    {
        using var invokable = CreateInvokable("Boolean");
        invokable.SetTarget(new TargetHolder(new RpcResponseTarget { Fail = true }));
        using var response = await invokable.Invoke();
        Assert.IsType<ExceptionResponse>(response);
        Assert.Equal("response failure", Assert.IsType<InvalidOperationException>(response.Exception).Message);
        Assert.Throws<InvalidOperationException>(() => response.GetResult<bool>());
        using var copied = _services.GetRequiredService<DeepCopier>().Copy(response);
        Assert.Same(response, copied);
    }

    private static IInvokable CreateInvokable(string methodName)
    {
        var invokables = typeof(NativeAotSmoke.IRpcResponses).Assembly.GetTypes()
            .Where(static type => !type.IsAbstract && typeof(IInvokable).IsAssignableFrom(type)
                && type.Name.StartsWith("Invokable_IRpcResponses_", StringComparison.Ordinal))
            .Select(static type => (IInvokable)Activator.CreateInstance(type)!);
        var result = invokables.Single(invokable => invokable.GetMethodName() == methodName);
        return result;
    }

    [Fact]
    public void RawResponseCodecIsTheSelectedResponseCodec()
    {
        var provider = _services.GetRequiredService<CodecProvider>();
        Assert.True(provider.TryGetResponseCodec(typeof(int), out var codec));
        Assert.Same(provider.GetCodec(typeof(Response<int>)), codec);
        Assert.IsType<ResponseCodec<int, Int32Codec>>(codec);
        Assert.False(provider.TryGetResponseCodec(typeof(UnregisteredResult), out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void RawResponseCodecHonorsExplicitResponseSerializer()
    {
        var custom = new CustomResponseCodec();
        using var services = new ServiceCollection()
            .AddSerializer(builder => builder.Configure(options => options.AddSerializer<Response<int>>(
                _ => custom, static _ => new ResponseCopier<int, ShallowCopier<int>>(new ShallowCopier<int>()))))
            .BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        Assert.True(provider.TryGetResponseCodec(typeof(int), out var codec));
        Assert.Same(custom, codec);
    }

    [Fact]
    public void ResponsePoolCreatesDistinctResponses()
    {
        var first = ResponsePool.Get<int>();
        var second = ResponsePool.Get<int>();
        Assert.NotSame(first, second);
        first.TypedResult = 42;
        ResponsePool.Return(first);
        Assert.Equal(42, first.TypedResult);
        Assert.NotSame(Response.FromResult(1), Response.FromResult(1));
    }

    // Raw response bytes are a wire contract between silos and clients of different versions. The expected values were produced by the encoding which predates generated response registrations.
    [Fact]
    public void RawResponseEncodingMatchesWireContract()
    {
        Assert.Equal("28070003E0", Encode(true));
        Assert.Equal("280300A9E0", Encode(42));
        Assert.Equal("2805400F6F726C65616E73E0", Encode("orleans"));
        Assert.Equal("2805E0", Encode<string>(null!));

        string Encode<T>(T value)
        {
            var provider = _services.GetRequiredService<CodecProvider>();
            Assert.True(provider.TryGetResponseCodec(typeof(T), out var codec));
            var buffer = new ArrayBufferWriter<byte>();
            using var session = _services.GetRequiredService<SerializerSessionPool>().GetSession();
            var writer = Writer.Create(buffer, session);
            codec.WriteRaw(ref writer, Response.FromResult(value));
            writer.Commit();
            return Convert.ToHexString(buffer.WrittenSpan);
        }
    }

    private sealed class UnregisteredResult;

    private sealed class CustomResponseCodec() : ResponseCodec<int, Int32Codec>(new Int32Codec());

    private sealed class DelegatingCodec<T>(IFieldCodec<T> codec) : IFieldCodec<T>
    {
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta,
            [System.Diagnostics.CodeAnalysis.AllowNull] Type expectedType, [System.Diagnostics.CodeAnalysis.AllowNull] T value)
            where TBufferWriter : IBufferWriter<byte>
        {
            Writes++;
            codec.WriteField(ref writer, fieldIdDelta, expectedType, value);
        }
        [return: System.Diagnostics.CodeAnalysis.MaybeNull]
        public T ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            Reads++;
            return codec.ReadValue(ref reader, field);
        }
    }

    private sealed class TransformingIntCopier : IDeepCopier<int>
    {
        public int Copies { get; private set; }
        public int DeepCopy(int input, CopyContext context)
        {
            Copies++;
            return input + 1;
        }
    }

    private sealed class TargetHolder(object target) : ITargetHolder
    {
        public object GetTarget() => target;
        public object? GetComponent(Type componentType) => componentType.IsInstanceOfType(target) ? target : null;
    }

    private sealed class RpcResponseTarget : NativeAotSmoke.IRpcResponses
    {
        public bool Fail { get; init; }
        public NativeAotSmoke.RpcResponsePayload Result { get; } = new() { Value = 17 };
        public Task<bool> Boolean() => Fail ? throw new InvalidOperationException("response failure") : Task.FromResult(true);
        public ValueTask<int> Integer() => ValueTask.FromResult(42);
        public Task<NativeAotSmoke.RpcResponsePayload> Payload() => Task.FromResult(Result);
        public Task<NativeAotSmoke.RpcGeneratedValue<int>> Value() => Task.FromResult(new NativeAotSmoke.RpcGeneratedValue<int> { Value = 47 });
        public Task<NativeAotSmoke.RpcResponseBox<byte>> Bytes() => Task.FromResult(new NativeAotSmoke.RpcResponseBox<byte> { Value = [7, 9] });
    }

    public sealed class FactoryService
    {
    }

    public sealed class CustomPayloadCopier : IDeepCopier<NativeAotSmoke.RpcResponsePayload>
    {
        [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(input))]
        public NativeAotSmoke.RpcResponsePayload? DeepCopy(NativeAotSmoke.RpcResponsePayload? input, CopyContext context) => input;
    }

    private byte[] Write<T>(IFieldCodec<T> codec, T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var session = _services.GetRequiredService<SerializerSessionPool>().GetSession();
        var writer = Writer.Create(buffer, session);
        codec.WriteField(ref writer, 0, typeof(T), value);
        writer.Commit();
        return buffer.WrittenSpan.ToArray();
    }
}
