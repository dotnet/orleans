#nullable enable
using System;
using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;

namespace Orleans.NativeAotSmoke;

public static class RpcResponseContracts
{
    public static async System.Threading.Tasks.Task GeneratedInvokablesWriteCopiedResponses()
    {
#if NATIVE_AOT_SMOKE
        using var services = CreateServices();
        var provider = services.GetRequiredService<CodecProvider>();
        var contexts = services.GetRequiredService<CopyContextPool>();
        var target = new SelfWritingTarget();
        IRpcSelfWriting proxy = new global::OrleansCodeGen.Orleans.NativeAotSmoke.Proxy_IRpcSelfWriting(provider, contexts);
        _ = proxy.Boolean();
        await Check(((RpcTupleProxyBase)proxy).Captured!, true);
        _ = proxy.Integer();
        await Check(((RpcTupleProxyBase)proxy).Captured!, 42);
        _ = proxy.Payload();
        using var request = ((RpcTupleProxyBase)proxy).Captured!;
        request.SetTarget(target);
        using var response = await ((IResponseInvokable)request).InvokeAndCopy(provider, contexts,
            new DeepCopier<Response>(provider.GetDeepCopier<Response>(), contexts));
        Ensure(response is IRawResponseWriter && !response.GetType().IsGenericType,
            "The actual generated invokable creates a non-generic self-writing holder.");
        var value = response.GetResult<RpcResponsePayload>();
        Ensure(value is not null && !ReferenceEquals(value, target.Result) && ReferenceEquals(value, value.Left),
            "Generated result creation copies the mutable cyclic payload before returning.");
        var buffer = new ArrayBufferWriter<byte>();
        using (var session = services.GetRequiredService<SerializerSessionPool>().GetSession())
        {
            var writer = Writer.Create(buffer, session);
            ((IRawResponseWriter)response).WriteRaw(ref writer);
            writer.Commit();
        }
        Ensure(provider.TryGetRawResponseReader(typeof(RpcResponsePayload), out var registered), "The result reader is statically registered.");
        using var readerSession = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var reader = Reader.Create(buffer.WrittenMemory, readerSession);
        var field = reader.ReadFieldHeader();
        using var reconstructed = registered.ReadRaw(ref reader, ref field);
        var decoded = reconstructed.GetResult<RpcResponsePayload>();
        Ensure(decoded is not null && ReferenceEquals(decoded, decoded.Left), "The static reader reconstructs the self-writing cyclic holder.");

        async System.Threading.Tasks.Task Check<T>(IInvokable invocation, T expected)
        {
            using var request = invocation;
            request.SetTarget(target);
            using var result = await ((IResponseInvokable)request).InvokeAndCopy(provider, contexts,
                new DeepCopier<Response>(provider.GetDeepCopier<Response>(), contexts));
            Ensure(result is IRawResponseWriter && Equals(expected, result.GetResult<T>()), "Generated primitive responses bind direct writers.");
            var output = new ArrayBufferWriter<byte>();
            using (var session = services.GetRequiredService<SerializerSessionPool>().GetSession())
            {
                var writer = Writer.Create(output, session);
                ((IRawResponseWriter)result).WriteRaw(ref writer);
                writer.Commit();
            }
            Ensure(provider.TryGetRawResponseReader(typeof(T), out var registered), "The primitive result reader is statically registered.");
            using var readerSession = services.GetRequiredService<SerializerSessionPool>().GetSession();
            var reader = Reader.Create(output.WrittenMemory, readerSession);
            var field = reader.ReadFieldHeader();
            using var decoded = registered.ReadRaw(ref reader, ref field);
            Ensure(decoded is IRawResponseWriter && Equals(expected, decoded.GetResult<T>()),
                "Generated primitive writers and readers round-trip through the native message session.");
        }
#else
        await System.Threading.Tasks.Task.CompletedTask;
#endif
    }

#if NATIVE_AOT_SMOKE
    private sealed class SelfWritingTarget : IRpcSelfWriting, ITargetHolder
    {
        public RpcResponsePayload Result { get; } = new() { Value = 47 };
        public SelfWritingTarget() => Result.Left = Result;
        public object GetTarget() => this;
        public object? GetComponent(Type type) => type.IsInstanceOfType(this) ? this : null;
        public System.Threading.Tasks.Task<bool> Boolean() => System.Threading.Tasks.Task.FromResult(true);
        public System.Threading.Tasks.ValueTask<int> Integer() => new(42);
        public System.Threading.Tasks.Task<RpcResponsePayload> Payload() => System.Threading.Tasks.Task.FromResult(Result);
    }
#endif

    public static void ConstructTupleArgumentProxyBeforeInvocation()
    {
        using var services = CreateServices();
        var provider = services.GetRequiredService<CodecProvider>();
        var pool = services.GetRequiredService<CopyContextPool>();
#if NATIVE_AOT_SMOKE
        IRpcTupleArguments proxy = new global::OrleansCodeGen.Orleans.NativeAotSmoke.Proxy_IRpcTupleArguments(provider, pool);
        Ensure(proxy is not null, "The actual generated tuple-argument proxy constructs before invocation.");
#endif
        var reference = new RpcTupleReference { Value = 47 };
        var tuple = Tuple.Create(reference, new DateTime(638000000000000000L, DateTimeKind.Utc));
        var input = new System.Collections.Generic.List<Tuple<RpcTupleReference, DateTime>> { tuple, tuple };
        var copier = provider.GetDeepCopier<System.Collections.Generic.List<Tuple<RpcTupleReference, DateTime>>>();
        var copy = new DeepCopier<System.Collections.Generic.List<Tuple<RpcTupleReference, DateTime>>>(copier, pool).Copy(input);
        Ensure(!ReferenceEquals(input, copy) && ReferenceEquals(tuple, copy[0]) && ReferenceEquals(copy[0], copy[1]),
            "Canonical tuple and list construction preserves immutable tuple identity.");
        Ensure(copy[0].Item1.Value == 47 && copy[0].Item2 == tuple.Item2,
            "Mixed reference/value argument tuples preserve their values.");
    }

    public static void CanonicalValueAndArrayServices()
    {
        using var services = CreateServices();
        var provider = services.GetRequiredService<CodecProvider>();
        var sessions = services.GetRequiredService<SerializerSessionPool>();
        var valueSerializer = new ValueSerializer<RpcGeneratedValue<int>>(provider, sessions);
        var original = new RpcGeneratedValue<int> { Value = 47 };
        var output = new ArrayBufferWriter<byte>();
        valueSerializer.Serialize(ref original, output);
        var value = new RpcGeneratedValue<int>();
        valueSerializer.Deserialize(output.WrittenMemory, ref value);
        Ensure(value.Value == 47, "The public value serializer uses the generated struct codec.");
#if NATIVE_AOT_SMOKE
        Ensure(ReferenceEquals(provider.GetValueSerializer<RpcGeneratedValue<int>>(), provider.GetCodec<RpcGeneratedValue<int>>()),
            "Value and field serializer services share the same generated codec instance.");
#endif

        var box = new RpcResponseBox<byte> { Value = [7, 9] };
        using var response = Response.FromResult(box);
        using var copied = Copy(services, response);
        using var roundTrip = RoundTrip(services, response);
        var copy = copied.GetResult<RpcResponseBox<byte>>();
        var result = roundTrip.GetResult<RpcResponseBox<byte>>();
        Ensure(copy is not null && result is not null && result.Value.Length == 2 && result.Value[0] == 7 && result.Value[1] == 9,
            "Closed generic array codecs preserve the response payload.");
        Ensure(!ReferenceEquals(box, copy) && !ReferenceEquals(box.Value, copy.Value), "Canonical generic array copying isolates the result.");
        copy.Value[0] = 3;
        Ensure(box.Value[0] == 7, "Mutating the copied generic byte array leaves the original unchanged.");
        Ensure(provider.GetCodec<byte[]>() is ByteArrayCodec && provider.GetDeepCopier<byte[]>() is ByteArrayCopier,
            "Auxiliary generic array services preserve optimized direct byte-array dispatch.");
    }

    public static void PrimitiveResponses()
    {
        using var services = CreateServices();
        Check(false);
        Check(true);
        Check(0);
        Check(42);
        Check(-17);

        void Check<T>(T value)
        {
            using var response = Response.FromResult(value);
            using var copy = Copy(services, response);
            Ensure(!ReferenceEquals(response, copy), "Response copies have distinct pooled envelopes.");
            Ensure(Equals(value, copy.GetResult<T>()), "The response copier preserves primitive results.");
            using var roundTrip = RoundTrip(services, response);
            Ensure(Equals(value, roundTrip.GetResult<T>()), "The response codec preserves primitive results.");
            Ensure(services.GetRequiredService<CodecProvider>().GetCodec(response.GetType()) is ResponseCodec,
                "Response dispatch retains the concrete raw response codec.");
        }
    }

    public static void ReferenceResponsePreservesCycles()
    {
        using var services = CreateServices();
        var payload = new RpcResponsePayload { Value = 42 };
        var shared = new RpcResponsePayload { Value = 17, Left = payload };
        payload.Left = shared;
        payload.Right = shared;
        using var response = Response.FromResult(payload);
        using var copy = Copy(services, response);
        Verify(copy.GetResult<RpcResponsePayload>());
        using var roundTrip = RoundTrip(services, response);
        Verify(roundTrip.GetResult<RpcResponsePayload>());

        void Verify(RpcResponsePayload? result)
        {
            Ensure(result is not null, "Reference response payloads retain their root.");
            Ensure(!ReferenceEquals(payload, result), "Mutable response payloads are isolated.");
            Ensure(result.Value == 42 && result.Left.Value == 17, "Payload fields retain their values.");
            Ensure(ReferenceEquals(result.Left, result.Right), "Repeated payload references retain identity.");
            Ensure(ReferenceEquals(result, result.Left.Left), "Payload cycles point to the copied root.");
        }
    }

    public static void NullResponsePayload()
    {
        using var services = CreateServices();
        using var response = Response.FromResult<RpcResponsePayload>(null!);
        using var copy = Copy(services, response);
        using var roundTrip = RoundTrip(services, response);
        Ensure(copy.GetResult<RpcResponsePayload>() is null && roundTrip.GetResult<RpcResponsePayload>() is null,
            "Empty reference response payloads retain null.");
    }

    public static void CompletedAndExceptionResponses()
    {
        using var services = CreateServices();
        var cause = new InvalidOperationException("response failure");
        var mutableData = new RpcResponsePayload { Value = 17 };
        cause.Data["payload"] = mutableData;
        using var exception = Response.FromException(cause);
        using var copied = Copy(services, exception);
        Ensure(ReferenceEquals(exception, copied), "Immutable exception responses retain their identity.");
        Ensure(copied.Exception is { } copiedCause && ReferenceEquals(cause, copiedCause) && ReferenceEquals(mutableData, copiedCause.Data["payload"]),
            "Immutable exception envelope copying retains the existing exception and Data references.");
        Ensure(ReferenceEquals(Response.Completed, Copy(services, Response.Completed)), "Completed responses retain their singleton identity.");
    }

    public static void CompletedResponseRoundTrip()
    {
        using var services = CreateServices();
        using var result = RoundTrip(services, Response.Completed);
        Ensure(ReferenceEquals(Response.Completed, result), "Completed response transport restores the canonical singleton.");
    }

    public static void RawResponses()
    {
        using var services = CreateServices();
        Check(true);
        Check(42);
        Check<RpcResponsePayload>(null!);

        void Check<T>(T value)
        {
            using var response = Response.FromResult(value);
            var codec = (ResponseCodec)services.GetRequiredService<CodecProvider>().GetCodec(response.GetType());
            var buffer = new ArrayBufferWriter<byte>();
            var sessions = services.GetRequiredService<SerializerSessionPool>();
            using (var session = sessions.GetSession())
            {
                var writer = Writer.Create(buffer, session);
                codec.WriteRaw(ref writer, response);
                writer.Commit();
            }

            using var readerSession = sessions.GetSession();
            var reader = Reader.Create(buffer.WrittenMemory, readerSession);
            var field = reader.ReadFieldHeader();
            Ensure(field.FieldType == typeof(T), "Raw message responses encode the result type.");
            using var result = (Response)codec.ReadRaw(ref reader, ref field);
            Ensure(Equals(value, result.GetResult<T>()), "Raw message responses retain result values.");
        }
    }

#if NATIVE_AOT_SMOKE
    public static void ExceptionTransportRequiresDeclaredGraph()
    {
        using var services = CreateServices();
        using var exception = Response.FromException(new InvalidOperationException("response failure"));
        try
        {
            using var result = RoundTrip(services, exception);
        }
        catch (NotSupportedException error)
        {
            Ensure(error.Message.Contains("ExceptionResponse", StringComparison.Ordinal)
                && error.Message.Contains("exception and Data value types", StringComparison.Ordinal),
                "Strict exception transport identifies its explicit codec graph contract.");
            return;
        }

        throw new InvalidOperationException("Strict exception transport requires its declared dependency graph.");
    }

    public static void MissingNativeResponseRegistration()
    {
        using var services = CreateServices();
        var provider = services.GetRequiredService<CodecProvider>();
        Check(() => provider.GetCodec(typeof(Response<long>)));
        Check(() => provider.GetDeepCopier(typeof(Response<long>)));

        static void Check(Action lookup)
        {
            try
            {
                lookup();
            }
            catch (NotSupportedException exception)
            {
                Ensure(exception.Message.Contains(typeof(Response<long>).ToString(), StringComparison.Ordinal)
                    && exception.Message.Contains("serializer context", StringComparison.Ordinal),
                    "Missing response registrations identify the closed response and registration contract.");
                return;
            }

            throw new InvalidOperationException("A missing native response registration must fail at lookup.");
        }
    }
#endif

    private static ServiceProvider CreateServices()
    {
#if NATIVE_AOT_SMOKE
        return new ServiceCollection().AddSerializerContext(new global::OrleansCodeGen.OrleansNativeAotSmoke.RpcResponseFactories()).BuildServiceProvider();
#else
        return new ServiceCollection().AddSerializer(builder => builder.AddAssembly(typeof(IRpcResponses).Assembly)).BuildServiceProvider();
#endif
    }

    private static Response Copy(ServiceProvider services, Response response)
    {
        var provider = services.GetRequiredService<CodecProvider>();
        var pool = services.GetRequiredService<CopyContextPool>();
        return new DeepCopier<Response>(provider.GetDeepCopier<Response>(), pool).Copy(response);
    }

    private static Response RoundTrip(ServiceProvider services, Response response)
    {
        var codec = services.GetRequiredService<CodecProvider>().GetCodec<Response>();
        var sessions = services.GetRequiredService<SerializerSessionPool>();
        var buffer = new ArrayBufferWriter<byte>();
        using (var session = sessions.GetSession())
        {
            var writer = Writer.Create(buffer, session);
            codec.WriteField(ref writer, 0, typeof(Response), response);
            writer.Commit();
        }

        using var readerSession = sessions.GetSession();
        var reader = Reader.Create(buffer.WrittenMemory, readerSession);
        return (Response)codec.ReadValue(ref reader, reader.ReadFieldHeader())!;
    }

    private static void Ensure([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
