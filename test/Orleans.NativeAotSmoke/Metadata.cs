using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;
using Orleans.Serialization.WireProtocol;

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
        ValidateTargetParameterBinding();
        ValidateMatchingImplementationCandidates();
        ValidatePatternContractSelection(services);
        ValidateMixedRegistrationClosure();
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

    private static void ValidateTargetParameterBinding()
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(ReversedPairActivator<,>), typeof(IActivator<>),
            SerializationType.Create(typeof(BindingPair<,>), SerializationType.Parameter(1), SerializationType.Parameter(0)));
        using var services = new ServiceCollection().AddSingleton<ReversedPairActivator<string, int>>().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        var activator = provider.GetActivator<BindingPair<int, string>>();
        if (activator.GetType() != typeof(ReversedPairActivator<string, int>)
            || activator.Create() is not BindingPair<int, string>)
        {
            throw new InvalidOperationException("The target description did not bind reordered implementation parameters.");
        }

        Console.WriteLine("TargetParameterBinding passed.");
    }

    private static void ValidateMatchingImplementationCandidates()
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(IntPairActivator<>), typeof(IActivator<>),
            SerializationType.Create(typeof(BindingPair<,>), SerializationType.Parameter(0), SerializationType.Create(typeof(int))));
        options.AddSerializationContract(typeof(StringPairActivator<>), typeof(IActivator<>),
            SerializationType.Create(typeof(BindingPair<,>), SerializationType.Parameter(0), SerializationType.Create(typeof(string))));
        using var services = new ServiceCollection()
            .AddSingleton<IntPairActivator<Guid>>()
            .AddSingleton<StringPairActivator<Guid>>()
            .BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        if (provider.GetActivator<BindingPair<Guid, int>>().GetType() != typeof(IntPairActivator<Guid>)
            || provider.GetActivator<BindingPair<Guid, string>>().GetType() != typeof(StringPairActivator<Guid>))
        {
            throw new InvalidOperationException("Target pattern lookup did not select the matching implementation.");
        }

        Console.WriteLine("MatchingImplementationCandidates passed.");
    }

    private static void ValidatePatternContractSelection(IServiceProvider serializerServices)
    {
        var options = new TypeManifestOptions();
        var arrayTarget = SerializationType.Array(SerializationType.Parameter(0));
        options.AddSerializationContract(typeof(MetadataParameterCopier<>), typeof(IDeepCopier<>), SerializationType.Parameter(0));
        options.AddSerializationContract(typeof(MetadataArrayCodec<>), typeof(IFieldCodec<>), arrayTarget);
        options.AddSerializationContract(typeof(MetadataArrayCopier<>), typeof(IDeepCopier<>), arrayTarget);
        using var services = new ServiceCollection()
            .AddSingleton(new MetadataArrayCodec<int>(new Int32Codec()))
            .AddSingleton<MetadataArrayCopier<int>>()
            .AddSingleton<MetadataParameterCopier<Guid>>()
            .BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        var codec = provider.GetCodec<int[]>();
        if (codec.GetType() != typeof(MetadataArrayCodec<int>)
            || provider.GetDeepCopier<int[]>().GetType() != typeof(MetadataArrayCopier<int>))
        {
            throw new InvalidOperationException("The most recently registered matching pattern was not selected.");
        }

        var serializer = new Serializer<int[]>(codec, serializerServices.GetRequiredService<SerializerSessionPool>());
        var input = new[] { 13, 29, 47 };
        if (!input.SequenceEqual(serializer.Deserialize(serializer.SerializeToArray(input)))
            || provider.GetDeepCopier<Guid>().GetType() != typeof(MetadataParameterCopier<Guid>))
        {
            throw new InvalidOperationException("Array or bare-parameter contract binding failed.");
        }

        Console.WriteLine("PatternContractSelection passed.");
    }

    private static void ValidateMixedRegistrationClosure()
    {
        using var services = new ServiceCollection()
            .AddSingleton<MetadataMixedActivator<string>>()
            .AddSingleton<MetadataMixedActivator<MetadataMixedTarget<string>>>()
            .BuildServiceProvider();
        foreach (var plainLast in new[] { false, true })
        {
            var options = new TypeManifestOptions();
            if (!plainLast)
            {
                options.AddActivator(typeof(MetadataMixedActivator<>), typeof(MetadataMixedTarget<>));
            }

            options.AddSerializationContract(typeof(MetadataMixedActivator<>), typeof(IActivator<>),
                SerializationType.Create(typeof(MetadataMixedTarget<>),
                    SerializationType.Create(typeof(MetadataMixedTarget<>), SerializationType.Parameter(0))));
            if (plainLast)
            {
                options.AddActivator(typeof(MetadataMixedActivator<>), typeof(MetadataMixedTarget<>));
            }

            var provider = new CodecProvider(services, Options.Create(options));
            var plain = provider.GetActivator<MetadataMixedTarget<string>>();
            var nested = provider.GetActivator<MetadataMixedTarget<MetadataMixedTarget<string>>>();
            if (plain.GetType() != typeof(MetadataMixedActivator<string>)
                || nested.GetType() != (plainLast ? typeof(MetadataMixedActivator<MetadataMixedTarget<string>>) : typeof(MetadataMixedActivator<string>))
                || plain.Create() is not MetadataMixedTarget<string>
                || nested.Create() is not MetadataMixedTarget<MetadataMixedTarget<string>>)
            {
                throw new InvalidOperationException("Implementation closure did not honor the selected plain or described registration.");
            }
        }

        Console.WriteLine("MixedRegistrationClosure passed.");
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

internal sealed class BindingPair<TFirst, TSecond>;
internal sealed class ReversedPairActivator<TFirst, TSecond> : IActivator<BindingPair<TSecond, TFirst>>
{
    public BindingPair<TSecond, TFirst> Create() => new();
}

internal sealed class IntPairActivator<T> : IActivator<BindingPair<T, int>>
{
    public BindingPair<T, int> Create() => new();
}

internal sealed class StringPairActivator<T> : IActivator<BindingPair<T, string>>
{
    public BindingPair<T, string> Create() => new();
}

internal sealed class MetadataArrayCodec<T>(IFieldCodec<T> elementCodec) : IFieldCodec<T[]>
{
    private readonly ArrayCodec<T> _codec = new(elementCodec);

    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, [AllowNull] Type expected, [AllowNull] T[] value)
        where TBufferWriter : IBufferWriter<byte>
        => _codec.WriteField(ref writer, id, expected, value);

    [return: MaybeNull]
    public T[] ReadValue<TInput>(ref Reader<TInput> reader, Field field) => _codec.ReadValue(ref reader, field);
}

internal sealed class MetadataArrayCopier<T> : ShallowCopier<T[]>;
internal sealed class MetadataParameterCopier<T> : ShallowCopier<T>;
internal sealed class MetadataMixedTarget<T>;
internal sealed class MetadataMixedActivator<T> : IActivator<MetadataMixedTarget<T>>, IActivator<MetadataMixedTarget<MetadataMixedTarget<T>>>
{
    MetadataMixedTarget<T> IActivator<MetadataMixedTarget<T>>.Create() => new();
    MetadataMixedTarget<MetadataMixedTarget<T>> IActivator<MetadataMixedTarget<MetadataMixedTarget<T>>>.Create() => new();
}
