using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;
using OrleansCodeGen.Orleans.Serialization.NativeAotFieldAccessSmoke;

namespace Orleans.Serialization.NativeAotFieldAccessSmoke;

internal static class NativeFieldAccessChecks
{
    public static void Run()
    {
        using var services = new ServiceCollection()
            .AddSingleton<IFieldCodec<string>>(new StringCodec())
            .AddSingleton<IFieldCodec<int>>(new Int32Codec())
            .AddSingleton<IDeepCopier<string>>(new ShallowCopier<string>())
            .AddSingleton<IDeepCopier<int>>(new ShallowCopier<int>())
            .BuildServiceProvider();

        // Use a bounded manifest to exercise generated codecs independently of assembly discovery.
        var options = Options.Create(new TypeManifestOptions());
        var provider = new CodecProvider(services, options);
        var typeCodec = new TypeCodec(new TypeConverter([], [], [], options, new CachedTypeResolver()));
        var wellKnownTypes = new WellKnownTypeCollection(options);
        using var context = new CopyContext(provider, static _ => { });

        var fields = new PrivateFields(137, [2, 4, 8]);
        var fieldsResult = RoundTrip(new Codec_PrivateFields(), fields, provider, typeCodec, wellKnownTypes);
        FieldAccessChecks.ValidatePrivateFields(fields, fieldsResult, new Copier_PrivateFields().DeepCopy(fields, context));
        context.Reset();

        var generic = new ConstrainedFields<string>("generic fields");
        var genericResult = RoundTrip(new Codec_ConstrainedFields<string>(provider), generic, provider, typeCodec, wellKnownTypes);
        FieldAccessChecks.ValidateConstrainedFields(generic, genericResult, new Copier_ConstrainedFields<string>(provider).DeepCopy(generic, context));
        context.Reset();

        var value = new ValueFields<int>(211, [3, 6, 9]);
        var valueResult = RoundTrip(new Codec_ValueFields<int>(provider), value, provider, typeCodec, wellKnownTypes);
        FieldAccessChecks.ValidateValueFields(value, valueResult, new Copier_ValueFields<int>(provider).DeepCopy(value, context));
        context.Reset();

        var nested = new Outer<string>.Nested<int>("nested fields", 307);
        var nestedResult = RoundTrip(
            new OrleansCodeGen.Orleans.Serialization.NativeAotFieldAccessSmoke.Outer.Codec_Nested<string, int>(provider),
            nested, provider, typeCodec, wellKnownTypes);
        FieldAccessChecks.ValidateNestedFields(
            nested, nestedResult,
            new OrleansCodeGen.Orleans.Serialization.NativeAotFieldAccessSmoke.Outer.Copier_Nested<string, int>(provider).DeepCopy(nested, context));
        context.Reset();

        var volatileFields = new VolatileFields(419, "volatile", [5, 10, 15], 23);
        var volatileResult = RoundTrip(new Codec_VolatileFields(), volatileFields, provider, typeCodec, wellKnownTypes);
        FieldAccessChecks.ValidateVolatileFields(volatileFields, volatileResult, new Copier_VolatileFields().DeepCopy(volatileFields, context));
        context.Reset();

        var volatileValue = new VolatileValueFields(421, [6, 12, 18]);
        var volatileValueResult = RoundTrip(new Codec_VolatileValueFields(), volatileValue, provider, typeCodec, wellKnownTypes);
        FieldAccessChecks.ValidateVolatileValueFields(volatileValue, volatileValueResult, new Copier_VolatileValueFields().DeepCopy(volatileValue, context));
        context.Reset();

        var genericVolatile = new GenericVolatileFields<string>("generic volatile");
        var genericVolatileResult = RoundTrip(new Codec_GenericVolatileFields<string>(provider), genericVolatile, provider, typeCodec, wellKnownTypes);
        FieldAccessChecks.ValidateGenericVolatileFields(
            genericVolatile, genericVolatileResult, new Copier_GenericVolatileFields<string>(provider).DeepCopy(genericVolatile, context));
    }

    private static T RoundTrip<T>(IFieldCodec<T> codec, T input, CodecProvider provider, TypeCodec typeCodec, WellKnownTypeCollection wellKnownTypes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var session = new SerializerSession(typeCodec, wellKnownTypes, provider))
        {
            var writer = Writer.Create(buffer, session);
            codec.WriteField(ref writer, 0, typeof(T), input);
            writer.Commit();
        }

        using var readSession = new SerializerSession(typeCodec, wellKnownTypes, provider);
        var reader = Reader.Create(buffer.WrittenMemory, readSession);
        return codec.ReadValue(ref reader, reader.ReadFieldHeader())
            ?? throw new InvalidOperationException("The generated codec must restore a non-null payload.");
    }
}
