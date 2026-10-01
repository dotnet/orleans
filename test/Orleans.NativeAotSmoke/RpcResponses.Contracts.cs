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
#if NATIVE_AOT_SMOKE
        using var context = pool.GetContext();
        return (Response)provider.GetDeepCopier(response.GetType()).DeepCopy(response, context)!;
#else
        return new DeepCopier<Response>(provider.GetDeepCopier<Response>(), pool).Copy(response);
#endif
    }

    private static Response RoundTrip(ServiceProvider services, Response response)
    {
        var codec = services.GetRequiredService<CodecProvider>().GetCodec(response.GetType());
        var sessions = services.GetRequiredService<SerializerSessionPool>();
        var buffer = new ArrayBufferWriter<byte>();
        using (var session = sessions.GetSession())
        {
            var writer = Writer.Create(buffer, session);
            codec.WriteField(ref writer, 0, response.GetType(), response);
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
