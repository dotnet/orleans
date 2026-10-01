using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;

namespace Orleans.NativeAotSmoke;

internal static class Metadata
{
    private static void Main(string[] args)
    {
        var registrations = new ServiceCollection().AddSerializer();
        if (args.Length == 1 && args[0] == "--runtime-selection")
        {
            registrations.AddSingleton<Serializer<int>>(services => new(services.GetRequiredService<SerializerSessionPool>()));
            using var runtimeServices = registrations.BuildServiceProvider();
            RoundTrip(runtimeServices, 42, nameof(PrimitiveRoundTrip));
            return;
        }

        var integerCodec = new Int32Codec();
        var stringCodec = new StringCodec();
        AddClosedSerializer(registrations, integerCodec);
        AddClosedSerializer(registrations, stringCodec);
        AddClosedSerializer(registrations, new TupleCodec<string, int>(stringCodec, integerCodec));
        AddClosedSerializer(registrations, new ValueTupleCodec<int, string>(integerCodec, stringCodec));

        using var services = registrations.BuildServiceProvider();
        MetadataInitialization(services);
        TupleDefinitionMetadata(services);
        PrimitiveRoundTrip(services);
        ReferenceTupleRoundTrip(services);
        ValueTupleRoundTrip(services);
    }

    private static void AddClosedSerializer<T>(IServiceCollection services, IFieldCodec<T> codec)
    {
        services.AddSingleton<Serializer<T>>(serviceProvider => new(codec, serviceProvider.GetRequiredService<SerializerSessionPool>()));
    }

    private static void MetadataInitialization(IServiceProvider services)
    {
        _ = services.GetRequiredService<TypeConverter>();
        var provider = services.GetRequiredService<CodecProvider>();
        if (!ReferenceEquals(provider, services.GetRequiredService<ICodecProvider>()))
        {
            throw new InvalidOperationException("The serializer resolved different manifest codec providers.");
        }

        Console.WriteLine($"{nameof(MetadataInitialization)} passed.");
    }

    private static void TupleDefinitionMetadata(IServiceProvider services)
    {
        var converter = services.GetRequiredService<TypeConverter>();
        foreach (var family in new[] { "System.Tuple", "System.ValueTuple" })
        {
            for (var arity = 1; arity <= 8; arity++)
            {
                var type = converter.Parse($"{family}`{arity}");
                if (!type.IsGenericTypeDefinition || type.GetGenericArguments().Length != arity
                    || converter.Parse(converter.Format(type)) != type)
                {
                    throw new InvalidOperationException($"Tuple definition metadata was not preserved for {family}`{arity}.");
                }
            }
        }

        Console.WriteLine($"{nameof(TupleDefinitionMetadata)} passed.");
    }

    private static void PrimitiveRoundTrip(IServiceProvider services)
    {
        RoundTrip(services, 42, nameof(PrimitiveRoundTrip));
        RoundTrip(services, "native manifest", nameof(PrimitiveRoundTrip));
    }

    private static void ReferenceTupleRoundTrip(IServiceProvider services)
        => RoundTrip(services, Tuple.Create("reference tuple", 17), nameof(ReferenceTupleRoundTrip));

    private static void ValueTupleRoundTrip(IServiceProvider services)
        => RoundTrip(services, (29, "value tuple"), nameof(ValueTupleRoundTrip));

    private static void RoundTrip<T>(IServiceProvider services, T input, string testName)
    {
        var serializer = services.GetRequiredService<Serializer<T>>();
        var result = serializer.Deserialize(serializer.SerializeToArray(input));
        if (!EqualityComparer<T>.Default.Equals(input, result))
        {
            throw new InvalidOperationException($"{testName}: expected {input}, received {result}.");
        }

        Console.WriteLine($"{testName}<{typeof(T)}> passed.");
    }
}
