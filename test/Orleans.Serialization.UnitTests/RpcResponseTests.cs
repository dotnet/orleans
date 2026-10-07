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
            var concrete = new PooledResponseCodec<T, TCodec>(resultCodec);
            var legacy = new PooledResponseCodec<T>(_services.GetRequiredService<CodecProvider>());
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
        var codec = new PooledResponseCodec<int, Int32Codec>(new Int32Codec());
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
    public void ConcreteResponseCopiersPreserveNullAndPooledEnvelopeSemantics()
    {
        var copier = new PooledResponseCopier<int, ShallowCopier<int>>(new ShallowCopier<int>());
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
        Assert.Equal("codec", Assert.Throws<ArgumentNullException>(() => new PooledResponseCodec<int, Int32Codec>((Int32Codec)null!)).ParamName);
        Assert.Equal("copier", Assert.Throws<ArgumentNullException>(() => new PooledResponseCopier<int, ShallowCopier<int>>((ShallowCopier<int>)null!)).ParamName);
    }

    [Fact]
    public void GeneratedResponseFactoriesCopyAndSerializePrimitiveResults()
        => NativeAotSmoke.RpcResponseContracts.PrimitiveResponses();

    [Fact]
    public void GeneratedResponseFactoriesPreserveNativePublicationContracts()
        => NativeAotSmoke.RpcResponseContracts.GeneratedFactoryPublication();

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
        Assert.IsType<PooledResponseCodec<int, Int32Codec>>(provider.GetCodec<Response<int>>());
        Assert.IsType<PooledResponseCopier<int, ShallowCopier<int>>>(provider.GetDeepCopier<Response<int>>());
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
            options.AddSerializer(typeof(PooledResponseCodec<>), typeof(Response<>));
            options.AddCopier(typeof(PooledResponseCopier<>), typeof(Response<>));
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
                Assert.IsType<PooledResponseCodec<int>>(codec);
                Assert.IsType<PooledResponseCopier<int>>(copier);
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
                options.AddDefaultSerializerService<PooledResponseCodec<int, TCodec>>(provider =>
                {
                    codecCalls++;
                    return new(OrleansGeneratedCodeHelper.GetService<TCodec>(null!, provider));
                }, [typeof(TCodec)]);
                options.AddDefaultSerializerService<PooledResponseCopier<int, TCopier>>(provider =>
                {
                    copierCalls++;
                    return new(OrleansGeneratedCodeHelper.GetService<TCopier>(null!, provider));
                }, [typeof(TCopier)]);
                options.AddDefaultSerializer<Response<int>, PooledResponseCodec<int, TCodec>, PooledResponseCopier<int, TCopier>>(
                    static provider => OrleansGeneratedCodeHelper.GetService<PooledResponseCodec<int, TCodec>>(null!, provider),
                    static provider => OrleansGeneratedCodeHelper.GetService<PooledResponseCopier<int, TCopier>>(null!, provider),
                    codecDependencies: [typeof(TCodec)], copierDependencies: [typeof(TCopier)]);
            }
        }
    }

    [Fact]
    public void InferredResponseFactoriesInspectDefinitionsWithoutMaterializingLegacyImplementations()
    {
        var codecDefinition = new DefinitionOnlyType(typeof(PooledResponseCodec<>));
        var copierDefinition = new DefinitionOnlyType(typeof(PooledResponseCopier<>));
        var options = new TypeManifestOptions();
        options.AddSerializer(codecDefinition, typeof(Response<>));
        options.AddCopier(copierDefinition, typeof(Response<>));
        options.AddDefaultSerializer<Response<int>, PooledResponseCodec<int, Int32Codec>, PooledResponseCopier<int, ShallowCopier<int>>>(
            static _ => new PooledResponseCodec<int, Int32Codec>(new Int32Codec()),
            static _ => new PooledResponseCopier<int, ShallowCopier<int>>(new ShallowCopier<int>()));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Microsoft.Extensions.Options.Options.Create(options));

        Assert.IsType<PooledResponseCodec<int, Int32Codec>>(provider.GetCodec<Response<int>>());
        Assert.IsType<PooledResponseCopier<int, ShallowCopier<int>>>(provider.GetDeepCopier<Response<int>>());
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
        Assert.IsType<PooledResponseCopier<NativeAotSmoke.RpcResponsePayload>>(
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedResponseFactoryResolvesDependenciesOnlyDuringConstruction(bool readerFirst)
    {
        var options = _services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TypeManifestOptions>>().Value;
        var resultCodec = options.CodecFactories[typeof(int)];
        var resultCopier = options.CopierFactories[typeof(int)];
        var responseCodec = options.CodecFactories[typeof(Response<int>)];
        var responseCopier = options.CopierFactories[typeof(Response<int>)];
        var codecCalls = 0;
        var copierCalls = 0;
        var responseCodecCalls = 0;
        var responseCopierCalls = 0;
        options.CodecFactories[typeof(int)] = resultCodec with
        {
            Factory = provider => { codecCalls++; return resultCodec.Factory(provider); }
        };
        options.CopierFactories[typeof(int)] = resultCopier with
        {
            Factory = provider => { copierCalls++; return resultCopier.Factory(provider); }
        };
        options.CodecFactories[typeof(Response<int>)] = responseCodec with
        {
            Factory = provider => { responseCodecCalls++; return responseCodec.Factory(provider); }
        };
        options.CopierFactories[typeof(Response<int>)] = responseCopier with
        {
            Factory = provider => { responseCopierCalls++; return responseCopier.Factory(provider); }
        };

        var provider = _services.GetRequiredService<CodecProvider>();
        var contexts = _services.GetRequiredService<CopyContextPool>();
        var copier = _services.GetRequiredService<DeepCopier>().GetCopier<Response>();
        if (readerFirst) Assert.True(provider.TryGetRawResponseReader(typeof(int), out _));
        using var invokable = CreateInvokable("Integer");
        invokable.SetTarget(new TargetHolder(new RpcResponseTarget()));
        using var response = await invokable.InvokeAndCopy(provider, contexts, copier);
        Assert.IsAssignableFrom<IRawResponseWriter>(response);
        Assert.Equal(42, response.GetResult<int>());
        Assert.True(provider.TryGetRawResponseReader(typeof(int), out var reader));
        for (var i = 0; i < 100; i++) provider.TryGetRawResponseReader(typeof(int), out _);

        IRawResponseReader? actual = null;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++) provider.TryGetRawResponseReader(typeof(int), out actual);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Same(reader, actual);
        Assert.Equal(1, codecCalls);
        Assert.Equal(1, copierCalls);
        Assert.Equal(1, responseCodecCalls);
        Assert.Equal(1, responseCopierCalls);
        Assert.Equal(0, allocated);
        Assert.False(provider.IsConstructionPending);
    }

    [Fact]
    public void GeneratedResponseFactoryInitializesBeforeBeginningItsGraphAndRetriesInitializationFailure()
    {
        var initializationCalls = 0;
        var failure = new InvalidOperationException("serializer initialization failed");
        using var services = new ServiceCollection().AddSerializer()
            .AddSingleton<IGeneralizedCodec>(services =>
            {
                initializationCalls++;
                Assert.False(services.GetRequiredService<CodecProvider>().IsConstructionPending);
                if (initializationCalls == 1) throw failure;
                return new InitializationCodec();
            }).BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => provider.TryGetRawResponseReader(typeof(int), out _)));
        Assert.False(provider.IsConstructionPending);
        Assert.True(provider.TryGetRawResponseReader(typeof(int), out var reader));
        Assert.Equal(2, initializationCalls);
        Assert.True(provider.TryGetRawResponseReader(typeof(int), out var repeated));
        Assert.Same(reader, repeated);
        Assert.Equal(2, initializationCalls);
    }

    [Fact]
    public void GeneratedResponseFactoryRollsBackWithItsConstructionGraph()
    {
        IRawResponseReader? unpublished = null;
        var failure = new InvalidOperationException("response graph failed");
        using var services = new ServiceCollection().AddSerializer(builder => builder.Configure(options =>
            options.AddSerializerService<ResponseFactoryRoot>(provider =>
            {
                Assert.True(((CodecProvider)provider).TryGetRawResponseReader(typeof(int), out unpublished));
                throw failure;
            }))).BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => OrleansGeneratedCodeHelper.GetService<ResponseFactoryRoot>(null!, provider)));
        Assert.NotNull(unpublished);
        Assert.False(provider.IsConstructionPending);
        Assert.True(provider.TryGetRawResponseReader(typeof(int), out var published));
        Assert.NotSame(unpublished, published);
        Assert.True(provider.TryGetRawResponseReader(typeof(int), out var repeated));
        Assert.Same(published, repeated);
    }

    [Fact]
    public void PublishedGeneratedResponseFactoryPropagatesPendingGraphFailure()
    {
        var failure = new InvalidOperationException("response graph already faulted");
        using var services = new ServiceCollection().AddSerializer(builder => builder.Configure(options =>
        {
            options.AddSerializerService<FailedResponseFactoryRoot>(_ => throw failure);
            options.AddSerializerService<ResponseFactoryRoot>(provider =>
            {
                Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
                    OrleansGeneratedCodeHelper.GetService<FailedResponseFactoryRoot>(null!, provider)));
                ((CodecProvider)provider).TryGetRawResponseReader(typeof(int), out _);
                return new ResponseFactoryRoot();
            });
        })).BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        Assert.True(provider.TryGetRawResponseReader(typeof(int), out var published));

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => OrleansGeneratedCodeHelper.GetService<ResponseFactoryRoot>(null!, provider)));
        Assert.False(provider.IsConstructionPending);
        Assert.True(provider.TryGetRawResponseReader(typeof(int), out var repeated));
        Assert.Same(published, repeated);
    }

    [Fact]
    public async Task GeneratedResponseFactoryKeepsProviderSpecificOverridesWhenInvokableIsReused()
    {
        var payloadCopier = new TransformingIntCopier();
        using var overridden = new ServiceCollection().AddSerializer(builder => builder.Configure(options =>
            options.AddSerializer<int>(static _ => new Int32Codec(), _ => payloadCopier))).BuildServiceProvider();
        using var invokable = CreateInvokable("Integer");
        invokable.SetTarget(new TargetHolder(new RpcResponseTarget()));
        var provider = _services.GetRequiredService<CodecProvider>();
        Assert.True(provider.TryGetRawResponseReader(typeof(int), out var originalReader));

        for (var i = 0; i < 6; i++)
        {
            var selected = i % 2 == 0 ? _services : overridden;
            var selectedProvider = selected.GetRequiredService<CodecProvider>();
            using var response = await invokable.InvokeAndCopy(
                selectedProvider, selected.GetRequiredService<CopyContextPool>(),
                selected.GetRequiredService<DeepCopier>().GetCopier<Response>());
            Assert.Null(response.Exception);
            Assert.Equal(i % 2 == 0 ? 42 : 43, response.GetResult<int>());
            Assert.Equal(i % 2 == 0, response is IRawResponseWriter);
        }

        Assert.Equal(3, payloadCopier.Copies);
        Assert.False(overridden.GetRequiredService<CodecProvider>().TryGetRawResponseReader(typeof(int), out _));
        Assert.True(provider.TryGetRawResponseReader(typeof(int), out var repeated));
        Assert.Same(originalReader, repeated);
    }

    private sealed class ResponseFactoryRoot;
    private sealed class FailedResponseFactoryRoot;

    private sealed class InitializationCodec : IGeneralizedCodec
    {
        public bool IsSupportedType(Type type) => false;
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta,
            [System.Diagnostics.CodeAnalysis.AllowNull] Type expectedType, object? value)
            where TBufferWriter : IBufferWriter<byte> => throw new NotSupportedException();
        public object? ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("Boolean")]
    [InlineData("Integer")]
    [InlineData("Payload")]
    public async Task GeneratedResponseHoldersCopyAndWriteDirectly(string methodName)
    {
        var target = new RpcResponseTarget();
        using var invokable = CreateInvokable(methodName);
        invokable.SetTarget(new TargetHolder(target));
        var direct = invokable;
        var provider = _services.GetRequiredService<CodecProvider>();
        var contexts = _services.GetRequiredService<CopyContextPool>();
        var compatibility = new CountingResponseCopier();
        using var response = await direct.InvokeAndCopy(provider, contexts, new DeepCopier<Response>(compatibility, contexts));
        var writer = Assert.IsAssignableFrom<IRawResponseWriter>(response);
        Assert.False(response.GetType().IsGenericType);
        Assert.Equal(0, compatibility.Copies);
        if (methodName == "Payload")
        {
            var value = response.GetResult<NativeAotSmoke.RpcResponsePayload>();
            Assert.NotNull(value);
            Assert.NotSame(target.Result, value);
            Assert.Equal(17, value.Value);
            target.Result.Value = 91;
            Assert.Equal(17, value.Value);
        }

        Assert.NotNull(response.GetSimpleResultType());
        var expected = response.GetSimpleResultType()!;
        Assert.True(provider.TryGetRawResponseReader(expected, out var registered));
        var output = new ArrayBufferWriter<byte>();
        using (var session = _services.GetRequiredService<SerializerSessionPool>().GetSession())
        {
            var body = Writer.Create(output, session);
            writer.WriteRaw(ref body);
            body.Commit();
        }

        using var readerSession = _services.GetRequiredService<SerializerSessionPool>().GetSession();
        var reader = Reader.Create(output.WrittenMemory, readerSession);
        var field = reader.ReadFieldHeader();
        Assert.Equal(expected, field.FieldType);
        using var decoded = registered!.ReadRaw(ref reader, ref field);
        Assert.IsAssignableFrom<IRawResponseWriter>(decoded);
        if (methodName == "Payload") Assert.Equal(17, decoded.GetResult<NativeAotSmoke.RpcResponsePayload>()!.Value);
        else Assert.Equal(response.Result, decoded.Result);
    }

    [Theory]
    [InlineData("Boolean", typeof(bool), "ContextOnly")]
    [InlineData("Integer", typeof(int), "ContextOnly")]
    [InlineData("Payload", typeof(NativeAotSmoke.RpcResponsePayload), "ContextOnly")]
    [InlineData("Boolean", typeof(bool), "ProviderBeforeContext")]
    [InlineData("Integer", typeof(int), "ProviderBeforeContext")]
    [InlineData("Payload", typeof(NativeAotSmoke.RpcResponsePayload), "ProviderBeforeContext")]
    [InlineData("Boolean", typeof(bool), "SameProviderBeforeContext")]
    [InlineData("Integer", typeof(int), "SameProviderBeforeContext")]
    [InlineData("Payload", typeof(NativeAotSmoke.RpcResponsePayload), "SameProviderBeforeContext")]
    public async Task GeneratedResponseHoldersUseContextInJit(string methodName, Type resultType, string configurationOrder)
    {
        Assert.True(System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported);
        var serviceCollection = new ServiceCollection();
        var context = new global::OrleansCodeGen.OrleansSerializationUnitTests.RpcResponseFactories();
        var beforeStrictCalls = 0;
        if (configurationOrder != "ContextOnly")
        {
            if (configurationOrder == "SameProviderBeforeContext")
                serviceCollection.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<TypeManifestOptions>>(context);
            else
                serviceCollection.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<TypeManifestOptions>,
                    global::OrleansCodeGen.OrleansSerializationUnitTests.RpcResponseFactories>();
            serviceCollection.Configure<TypeManifestOptions>(options =>
            {
                beforeStrictCalls++;
            });
        }
        serviceCollection.AddSerializerContext(context);
        using var services = serviceCollection.BuildServiceProvider();
        _ = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TypeManifestOptions>>().Value;
        Assert.Equal(configurationOrder == "ContextOnly" ? 0 : 1, beforeStrictCalls);
        var provider = services.GetRequiredService<CodecProvider>();
        var contexts = services.GetRequiredService<CopyContextPool>();
        var target = new RpcResponseTarget();
        var compatibility = new CountingResponseCopier();
        using var invokable = CreateInvokable(methodName);
        invokable.SetTarget(new TargetHolder(target));
        using var response = await invokable.InvokeAndCopy(
            provider, contexts, new DeepCopier<Response>(compatibility, contexts));

        Assert.IsAssignableFrom<IRawResponseWriter>(response);
        Assert.Equal(0, compatibility.Copies);
        Assert.Equal(resultType, response.GetSimpleResultType());
        Assert.True(provider.TryGetRawResponseReader(resultType, out var registered));
        var buffer = new ArrayBufferWriter<byte>();
        using (var session = services.GetRequiredService<SerializerSessionPool>().GetSession())
        {
            var writer = Writer.Create(buffer, session);
            ((IRawResponseWriter)response).WriteRaw(ref writer);
            writer.Commit();
        }
        using var readerSession = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var reader = Reader.Create(buffer.WrittenMemory, readerSession);
        var field = reader.ReadFieldHeader();
        using var decoded = registered!.ReadRaw(ref reader, ref field);
        Assert.IsAssignableFrom<IRawResponseWriter>(decoded);
        if (methodName == "Payload")
        {
            var value = decoded.GetResult<NativeAotSmoke.RpcResponsePayload>();
            Assert.NotNull(value);
            Assert.NotSame(target.Result, response.GetResult<NativeAotSmoke.RpcResponsePayload>());
            Assert.NotSame(target.Result, value);
            Assert.Equal(17, value.Value);
        }
        else if (methodName == "Boolean")
            Assert.True(decoded.GetResult<bool>());
        else
            Assert.Equal(42, decoded.GetResult<int>());
    }

    [Fact]
    public async Task GeneratedResponseHolderPreservesRawWireBytesAndPoolReset()
    {
        using var invokable = CreateInvokable("Integer");
        invokable.SetTarget(new TargetHolder(new RpcResponseTarget()));
        var provider = _services.GetRequiredService<CodecProvider>();
        var contexts = _services.GetRequiredService<CopyContextPool>();
        var direct = invokable;
        var response = await direct.InvokeAndCopy(provider, contexts, _services.GetRequiredService<DeepCopier>().GetCopier<Response>());
        var body = new ArrayBufferWriter<byte>();
        var legacy = new ArrayBufferWriter<byte>();
        var sessions = _services.GetRequiredService<SerializerSessionPool>();
        using (var session = sessions.GetSession())
        {
            var writer = Writer.Create(body, session);
            Assert.IsAssignableFrom<IRawResponseWriter>(response).WriteRaw(ref writer);
            writer.Commit();
        }
        using (var session = sessions.GetSession())
        {
            using var original = Response.FromResult(42);
            var writer = Writer.Create(legacy, session);
            ((ResponseCodec)provider.GetCodec(original.GetType())).WriteRaw(ref writer, original);
            writer.Commit();
        }
        Assert.Equal(legacy.WrittenSpan.ToArray(), body.WrittenSpan.ToArray());
        response.Dispose();
        Assert.Equal(0, response.GetResult<int>());
        var factory = response.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(static field => field.Name == "_factory");
        Assert.Null(factory.GetValue(response));
        using var reused = await direct.InvokeAndCopy(provider, contexts, _services.GetRequiredService<DeepCopier>().GetCopier<Response>());
        Assert.Same(response, reused);
        Assert.Equal(42, reused.GetResult<int>());
    }

    [Fact]
    public async Task GeneratedResponseHolderKeepsExceptionBehavior()
    {
        using var invokable = CreateInvokable("Boolean");
        invokable.SetTarget(new TargetHolder(new RpcResponseTarget { Fail = true }));
        var contexts = _services.GetRequiredService<CopyContextPool>();
        var compatibility = new CountingResponseCopier();
        using var response = await invokable.InvokeAndCopy(
            _services.GetRequiredService<CodecProvider>(), contexts, new DeepCopier<Response>(compatibility, contexts));
        Assert.IsType<ExceptionResponse>(response);
        Assert.Equal("response failure", response.Exception!.Message);
        Assert.Equal(0, compatibility.Copies);
    }

    [Fact]
    public async Task GeneratedResponseHolderPreservesCyclesAndNullPayloads()
    {
        var target = new RpcResponseTarget();
        target.Result.Left = target.Result;
        target.Result.Right = target.Result;
        using var invokable = CreateInvokable("Payload");
        invokable.SetTarget(new TargetHolder(target));
        var provider = _services.GetRequiredService<CodecProvider>();
        var contexts = _services.GetRequiredService<CopyContextPool>();
        using var response = await invokable.InvokeAndCopy(
            provider, contexts, _services.GetRequiredService<DeepCopier>().GetCopier<Response>());
        var value = response.GetResult<NativeAotSmoke.RpcResponsePayload>();
        Assert.NotNull(value);
        Assert.NotSame(target.Result, value);
        Assert.Same(value, value.Left);
        Assert.Same(value.Left, value.Right);
        Assert.True(provider.TryGetRawResponseReader(typeof(NativeAotSmoke.RpcResponsePayload), out var registered));
        var buffer = new ArrayBufferWriter<byte>();
        using (var session = _services.GetRequiredService<SerializerSessionPool>().GetSession())
        {
            var writer = Writer.Create(buffer, session);
            ((IRawResponseWriter)response).WriteRaw(ref writer);
            writer.Commit();
        }
        using var readerSession = _services.GetRequiredService<SerializerSessionPool>().GetSession();
        var reader = Reader.Create(buffer.WrittenMemory, readerSession);
        var field = reader.ReadFieldHeader();
        using var result = registered!.ReadRaw(ref reader, ref field);
        var copied = result.GetResult<NativeAotSmoke.RpcResponsePayload>();
        Assert.NotNull(copied);
        Assert.Same(copied, copied.Left);
        Assert.Same(copied.Left, copied.Right);

        invokable.SetTarget(new TargetHolder(new NullPayloadTarget()));
        using var empty = await invokable.InvokeAndCopy(provider, contexts, _services.GetRequiredService<DeepCopier>().GetCopier<Response>());
        Assert.IsAssignableFrom<IRawResponseWriter>(empty);
        Assert.Null(empty.GetResult<NativeAotSmoke.RpcResponsePayload>());
    }

    [Fact]
    public async Task GeneratedResponseHolderHonorsCustomPayloadCopier()
    {
        using var services = new ServiceCollection().AddSerializer(builder =>
            builder.Configure(options => options.AddCopier(typeof(CustomPayloadCopier)))).BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var contexts = services.GetRequiredService<CopyContextPool>();
        var target = new RpcResponseTarget();
        using var invokable = CreateInvokable("Payload");
        invokable.SetTarget(new TargetHolder(target));
        using var result = await invokable.InvokeAndCopy(
            provider, contexts, services.GetRequiredService<DeepCopier>().GetCopier<Response>());
        Assert.IsType<Response<NativeAotSmoke.RpcResponsePayload>>(result);
        Assert.Same(target.Result, result.GetResult<NativeAotSmoke.RpcResponsePayload>());
        Assert.False(provider.TryGetRawResponseReader(typeof(NativeAotSmoke.RpcResponsePayload), out _));
    }

    [Theory]
    [InlineData("PayloadCodec")]
    [InlineData("PayloadCopier")]
    [InlineData("ResponseCodec")]
    [InlineData("ResponseCopier")]
    public async Task GeneratedResponseHolderHonorsCustomResultAndResponseServices(string service)
    {
        var payloadCodec = new DelegatingCodec<int>(new Int32Codec());
        var payloadCopier = new TransformingIntCopier();
        var responseCodec = new DelegatingCodec<Response<int>>(new PooledResponseCodec<int, Int32Codec>(new Int32Codec()));
        var responseCopier = new TransformingResponseCopier();
        using var services = new ServiceCollection().AddSerializer(builder => builder.Configure(options =>
        {
            if (service == "PayloadCodec")
                options.AddSerializer<int>(_ => payloadCodec, _ => new ShallowCopier<int>());
            else if (service == "PayloadCopier")
                options.AddSerializer<int>(static _ => new Int32Codec(), _ => payloadCopier);
            else if (service == "ResponseCodec")
                options.AddSerializer<Response<int>>(_ => responseCodec, _ => new PooledResponseCopier<int, ShallowCopier<int>>(new ShallowCopier<int>()));
            else
                options.AddSerializer<Response<int>>(_ => new PooledResponseCodec<int, Int32Codec>(new Int32Codec()), _ => responseCopier);
        })).BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var contexts = services.GetRequiredService<CopyContextPool>();
        using var invokable = CreateInvokable("Integer");
        invokable.SetTarget(new TargetHolder(new RpcResponseTarget()));
        using var result = await invokable.InvokeAndCopy(
            provider, contexts, services.GetRequiredService<DeepCopier>().GetCopier<Response>());

        Assert.Null(result.Exception);
        Assert.IsType<Response<int>>(result);
        Assert.Equal(service is "PayloadCopier" or "ResponseCopier" ? 43 : 42, result.GetResult<int>());
        Assert.Equal(service == "PayloadCopier" ? 1 : 0, payloadCopier.Copies);
        Assert.Equal(service == "ResponseCopier" ? 1 : 0, responseCopier.Copies);
        Assert.False(provider.TryGetRawResponseReader(typeof(int), out _));
        if (service == "PayloadCodec") Assert.Same(payloadCodec, provider.GetCodec<int>());
        if (service == "ResponseCodec") Assert.Same(responseCodec, provider.GetCodec<Response<int>>());
        if (service == "ResponseCopier") Assert.Same(responseCopier, provider.GetDeepCopier<Response<int>>());
    }

    [Fact]
    public async Task GeneratedResponseReaderReturnsHolderAfterMalformedPayload()
    {
        using var invokable = CreateInvokable("Integer");
        invokable.SetTarget(new TargetHolder(new RpcResponseTarget()));
        var provider = _services.GetRequiredService<CodecProvider>();
        var contexts = _services.GetRequiredService<CopyContextPool>();
        var direct = invokable;
        var copier = _services.GetRequiredService<DeepCopier>().GetCopier<Response>();
        var original = await direct.InvokeAndCopy(provider, contexts, copier);
        original.Dispose();
        Assert.True(provider.TryGetRawResponseReader(typeof(int), out var registered));
        var sessions = _services.GetRequiredService<SerializerSessionPool>();
        var malformed = new ArrayBufferWriter<byte>();
        using (var session = sessions.GetSession())
        {
            var writer = Writer.Create(malformed, session);
            writer.WriteStartObject(0, null!, typeof(int));
            writer.WriteFieldHeaderExpected(0, WireType.TagDelimited);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Commit();
        }

        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var session = sessions.GetSession();
            var reader = Reader.Create(malformed.WrittenMemory, session);
            var field = reader.ReadFieldHeader();
            registered!.ReadRaw(ref reader, ref field);
        });
        Assert.Equal("wireType", error.ParamName);
        Assert.Equal(0, original.GetResult<int>());
        Assert.Null(original.GetType().GetField("_factory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(original));
        using var reused = await direct.InvokeAndCopy(provider, contexts, copier);
        Assert.Same(original, reused);
        Assert.Equal(42, reused.GetResult<int>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GeneratedResponseHolderHonorsCanonicalResponseSubclasses(bool interfaceServices, bool customCodec)
    {
        var counts = new SubclassCounts();
        ResponseCodec codec = interfaceServices
            ? new CustomRawResponseCodec<IFieldCodec<int>>(new Int32Codec(), counts)
            : new CustomRawResponseCodec<Int32Codec>(new Int32Codec(), counts);
        IDeepCopier<Response<int>> copier = interfaceServices
            ? new CustomResponseSubclassCopier<IDeepCopier<int>>(new ShallowCopier<int>(), counts)
            : new CustomResponseSubclassCopier<ShallowCopier<int>>(new ShallowCopier<int>(), counts);
        using var services = new ServiceCollection().AddSerializer(builder => builder.Configure(options =>
            options.AddSerializer<Response<int>>(
                _ => customCodec ? (IFieldCodec<Response<int>>)codec : new PooledResponseCodec<int, Int32Codec>(new Int32Codec()),
                _ => customCodec ? new PooledResponseCopier<int, ShallowCopier<int>>(new ShallowCopier<int>()) : copier)))
            .BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var contexts = services.GetRequiredService<CopyContextPool>();
        using var invokable = CreateInvokable("Integer");
        invokable.SetTarget(new TargetHolder(new RpcResponseTarget()));

        using var response = await invokable.InvokeAndCopy(provider, contexts, services.GetRequiredService<DeepCopier>().GetCopier<Response>());

        Assert.IsType<Response<int>>(response);
        Assert.Equal(customCodec ? 42 : 43, response.GetResult<int>());
        Assert.Equal(customCodec ? 0 : 1, counts.Copies);
        Assert.False(provider.TryGetRawResponseReader(typeof(int), out _));
        if (customCodec)
        {
            Assert.Same(codec, provider.GetCodec<Response<int>>());
            var buffer = new ArrayBufferWriter<byte>();
            using (var session = services.GetRequiredService<SerializerSessionPool>().GetSession())
            {
                var writer = Writer.Create(buffer, session);
                ((ResponseCodec)provider.GetCodec(response.GetType())).WriteRaw(ref writer, response);
                writer.Commit();
            }
            using var readSession = services.GetRequiredService<SerializerSessionPool>().GetSession();
            var reader = Reader.Create(buffer.WrittenMemory, readSession);
            var field = reader.ReadFieldHeader();
            using var decoded = (Response)codec.ReadRaw(ref reader, ref field);
            Assert.Equal(142, decoded.GetResult<int>());
            Assert.Equal(1, counts.Writes);
            Assert.Equal(1, counts.Reads);
        }
        else
        {
            Assert.Same(copier, provider.GetDeepCopier<Response<int>>());
        }
    }

    private sealed class SubclassCounts
    {
        public int Writes;
        public int Reads;
        public int Copies;
    }

    private sealed class CustomRawResponseCodec<TCodec>(TCodec codec, SubclassCounts counts) : PooledResponseCodec<int, TCodec>(codec)
        where TCodec : class, IFieldCodec<int>
    {
        public override void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer, object value)
        {
            counts.Writes++;
            using var transformed = Response.FromResult(((Response<int>)value).TypedResult + 100);
            base.WriteRaw(ref writer, transformed);
        }
        public override object ReadRaw<TInput>(ref Reader<TInput> reader, scoped ref Field field)
        {
            counts.Reads++;
            return base.ReadRaw(ref reader, ref field);
        }
    }

    private sealed class CustomResponseSubclassCopier<TCopier>(TCopier copier, SubclassCounts counts)
        : PooledResponseCopier<int, TCopier>(copier), IDeepCopier<Response<int>>
        where TCopier : class, IDeepCopier<int>
    {
        [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(input))]
        public new Response<int>? DeepCopy(Response<int>? input, CopyContext context)
        {
            if (input is null) return null;
            counts.Copies++;
            return (Response<int>)Response.FromResult(input.TypedResult + 1);
        }
    }

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

    private sealed class TransformingResponseCopier : IDeepCopier<Response<int>>
    {
        public int Copies { get; private set; }
        [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(input))]
        public Response<int>? DeepCopy(Response<int>? input, CopyContext context)
        {
            if (input is null) return null;
            Copies++;
            return (Response<int>)Response.FromResult(input.TypedResult + 1);
        }
    }

    private sealed class NullPayloadTarget : NativeAotSmoke.IRpcResponses
    {
        public Task<bool> Boolean() => Task.FromResult(true);
        public ValueTask<int> Integer() => new(42);
        public Task<NativeAotSmoke.RpcResponsePayload> Payload() => Task.FromResult<NativeAotSmoke.RpcResponsePayload>(null!);
        public Task<NativeAotSmoke.RpcGeneratedValue<int>> Value() => Task.FromResult(new NativeAotSmoke.RpcGeneratedValue<int>());
        public Task<NativeAotSmoke.RpcResponseBox<byte>> Bytes() => Task.FromResult(new NativeAotSmoke.RpcResponseBox<byte> { Value = [7, 9] });
    }

    private sealed class CountingResponseCopier : IDeepCopier<Response>
    {
        public int Copies { get; private set; }
        [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(input))]
        public Response? DeepCopy(Response? input, CopyContext context)
        {
            Copies++;
            return input;
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
