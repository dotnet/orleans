using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;

namespace Orleans.NativeAotSmoke;

internal static class Metadata
{
    private static void Main()
    {
        var registrations = new ServiceCollection().AddSerializer();
        var integerCodec = new Int32Codec();
        var stringCodec = new StringCodec();
        AddClosedSerializer(registrations, integerCodec);
        AddClosedSerializer(registrations, stringCodec);
        AddClosedSerializer(registrations, new TupleCodec<string, int>(stringCodec, integerCodec));
        AddClosedSerializer(registrations, new ValueTupleCodec<int, string>(integerCodec, stringCodec));

        using var services = registrations.BuildServiceProvider();
        MetadataInitialization(services);
        TupleDefinitionMetadata(services);
        ValidateInheritedInterfaceTarget(services.GetRequiredService<TypeConverter>());
        PrivateContractContainer.Validate(services.GetRequiredService<TypeConverter>());
        PrimitiveRoundTrip(services);
        ReferenceTupleRoundTrip(services);
        ValueTupleRoundTrip(services);
    }

    internal static class PrivateContractContainer
    {
        private sealed class Hidden<T>;

        [RegisterCopier]
        internal sealed class Copier : IDeepCopier<Hidden<int>>
        {
            [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(input))]
            Hidden<int>? IDeepCopier<Hidden<int>>.DeepCopy(Hidden<int>? input, CopyContext context) => input;
        }

        internal static void Validate(TypeConverter converter)
        {
            var type = converter.Parse(
                "Orleans.NativeAotSmoke.Metadata+PrivateContractContainer+Hidden`1[[System.Int32,System.Private.CoreLib]],Orleans.NativeAotSmoke");
            if (type != typeof(Hidden<int>))
            {
                throw new InvalidOperationException("The private target contract was not registered.");
            }

            Console.WriteLine("PrivateTargetContract passed.");
        }
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

    private static void ValidateInheritedInterfaceTarget(TypeConverter converter)
    {
        var type = converter.Parse("Orleans.NativeAotSmoke.InheritedInterfaceTarget,Orleans.NativeAotSmoke");
        if (converter.Parse(converter.Format(type)) != type)
        {
            throw new InvalidOperationException("The inherited interface target was not preserved.");
        }

        Console.WriteLine("InheritedInterfaceTarget passed.");
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

internal interface ITargetMetadata<T>;
internal sealed class InterfaceOnlyArgument<T>;
internal class TargetMetadataBase : ITargetMetadata<InterfaceOnlyArgument<int>>;
internal sealed class InheritedInterfaceTarget : TargetMetadataBase;

[RegisterCopier]
internal sealed class InheritedInterfaceTargetCopier : ShallowCopier<InheritedInterfaceTarget>;
