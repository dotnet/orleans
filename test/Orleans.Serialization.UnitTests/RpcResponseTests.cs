using System;
using System.Buffers;
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
        using var services = new ServiceCollection().AddSerializer(builder =>
        {
            builder.Configure(options =>
            {
                options.AddDefaultSerializer<int>(_ => codec, _ => copier);
                options.AddDefaultSerializerService<FactoryService>(_ => service);
            });
            builder.Configure(options =>
            {
                options.AddDefaultSerializer<int>(static _ => throw new InvalidOperationException("second manifest codec"),
                    static _ => throw new InvalidOperationException("second manifest copier"));
                options.AddDefaultSerializerService<FactoryService>(static _ => throw new InvalidOperationException("second manifest service"));
            });
        }).BuildServiceProvider();
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
