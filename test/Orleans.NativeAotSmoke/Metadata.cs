using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
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
        var registrations = new ServiceCollection().AddSerializer()
            .AddSingleton<MetadataKnownArrayConverter<string>>();
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
        ValidateArrayMetadataAvailability(services);
        ValidateInterleavedRegistrationOrder();
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

    private static void ValidateArrayMetadataAvailability(IServiceProvider serializerServices)
    {
        var resolve = typeof(CodecProvider).GetMethod("ResolveSerializationType", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<SerializationType, Type[], Type>>();
        var rooted = new MetadataRootedArrayValue[1].GetType();
        if (resolve(SerializationType.Create(typeof(MetadataRootedArrayValue[])), []).TypeHandle.Value != rooted.TypeHandle.Value)
        {
            throw new InvalidOperationException("Typed array code and concrete descriptors must supply the same native array representation.");
        }
        foreach (var (descriptor, knownArray) in new[]
        {
            (SerializationType.Create(typeof(byte[])), typeof(byte[])),
            (SerializationType.Create(typeof(MetadataMixedTarget<byte>[])), typeof(MetadataMixedTarget<byte>[])),
            (SerializationType.Create(typeof(int[,])), typeof(int[,]))
        })
        {
            if (resolve(descriptor, []).TypeHandle.Value != knownArray.TypeHandle.Value)
            {
                throw new InvalidOperationException("Source-known array descriptors must retain their concrete native type.");
            }
        }

        var parameter = typeof(MetadataArrayCodec<>).GetGenericArguments()[0];
        foreach (var (description, parameters) in new[]
        {
            (SerializationType.Array(SerializationType.Parameter(0)), new[] { parameter }),
            (SerializationType.Array(SerializationType.Parameter(0)), new[] { typeof(MetadataRootedArrayValue) }),
            (SerializationType.Array(SerializationType.Create(typeof(MetadataUnrootedArrayValue))), Type.EmptyTypes)
        })
        {
            try
            {
                _ = resolve(description, parameters);
                throw new InvalidOperationException("Executable resolution must require a source-known closed array descriptor.");
            }
            catch (NotSupportedException exception)
            {
                if (!exception.Message.Contains("SerializationType.Create(typeof(ClosedArray))", StringComparison.Ordinal))
                {
                    throw;
                }
            }
        }

        using var services = new ServiceCollection()
            .AddSingleton<MetadataKnownArrayConverter<string>>()
            .BuildServiceProvider();
        var options = new TypeManifestOptions();
        options.AddConverter(typeof(MetadataKnownArrayConverter<string>), typeof(MetadataMixedTarget<string>),
            typeof(MetadataKnownArraySurrogate<string, int[]>));
        foreach (var provider in new[]
        {
            new CodecProvider(services, Options.Create(options)),
            serializerServices.GetRequiredService<CodecProvider>()
        })
        {
            var selectConverter = typeof(CodecProvider).GetMethod("TryGetSurrogateCodec", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<SurrogateSelection>(provider);
            if (!selectConverter(typeof(MetadataMixedTarget<string>), typeof(MetadataMixedTarget<>), out var codec, out var arguments)
                || codec != typeof(SurrogateCodec<MetadataMixedTarget<string>, MetadataKnownArraySurrogate<string, int[]>, MetadataKnownArrayConverter<string>>)
                || arguments is not [MetadataKnownArrayConverter<string>])
            {
                throw new InvalidOperationException("The closed or generated array surrogate registration did not select its source-known types.");
            }
        }

        var closeImplementation = typeof(CodecProvider).GetMethod("CloseImplementation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Type, Type, Type, Type?>>(serializerServices.GetRequiredService<CodecProvider>());
        if (closeImplementation(typeof(ImmutableArrayCodec<>), typeof(ImmutableArray<int>), typeof(IFieldCodec<>)) != typeof(ImmutableArrayCodec<int>))
        {
            throw new InvalidOperationException("The source-known immutable-array implementation did not retain ordinary generic closure.");
        }

        var typeOptions = new TypeManifestOptions();
        typeOptions.AddConverter(typeof(MetadataValueArrayConverter), typeof(MetadataMixedTarget<string>),
            SerializationType.Create(typeof(MetadataKnownArraySurrogate<,>),
                SerializationType.Create(typeof(string)), SerializationType.Create(typeof(MetadataRootedArrayValue[]))));
        var converter = new TypeConverter([], [], [new RejectUnregisteredTypes()], Options.Create(typeOptions), new CachedTypeResolver());
        var arraySurrogate = typeof(MetadataKnownArraySurrogate<string, MetadataRootedArrayValue[]>);
        if (converter.Parse(converter.Format(arraySurrogate)) != arraySurrogate)
        {
            throw new InvalidOperationException("The concrete array descriptor did not authorize its element type names.");
        }
        try
        {
            _ = converter.Format(typeof(MetadataUnrootedArrayValue));
            throw new InvalidOperationException("Concrete array authorization must remain scoped to its registered type names.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("not allowed", StringComparison.Ordinal))
        {
        }

        Console.WriteLine("ArrayMetadataAvailability passed.");
    }

    private delegate bool SurrogateSelection(Type target, Type searchType, out Type? codec, out object[]? arguments);
    private sealed class RejectUnregisteredTypes : ITypeFilter
    {
        public bool? IsTypeAllowed(Type type) => false;
    }

    private static void ValidateInterleavedRegistrationOrder()
    {
        var options = new TypeManifestOptions();
        var firstTarget = SerializationType.Create(typeof(BindingPair<,>), SerializationType.Create(typeof(Guid)), SerializationType.Create(typeof(int)));
        var target = SerializationType.Create(typeof(BindingPair<,>), SerializationType.Create(typeof(string)), SerializationType.Create(typeof(int)));
        foreach (var role in new[] { typeof(IFieldCodec<>), typeof(IDeepCopier<>) })
        {
            options.AddSerializationContract(typeof(MetadataOrderedCodecCopier), role, firstTarget);
            options.AddSerializationContract(typeof(MetadataAlternativeOrderedCodecCopier), role, target);
            options.AddSerializationContract(typeof(MetadataOrderedCodecCopier), role, target);
        }
        options.AddSerializationContract(typeof(MetadataOrderedConverter), typeof(IConverter<,>), firstTarget,
            SerializationType.Create(typeof(MetadataKnownArraySurrogate<Guid, int>)));
        options.AddSerializationContract(typeof(MetadataAlternativeOrderedConverter), typeof(IConverter<,>), target,
            SerializationType.Create(typeof(MetadataKnownArraySurrogate<int, int>)));
        options.AddSerializationContract(typeof(MetadataOrderedConverter), typeof(IConverter<,>), target,
            SerializationType.Create(typeof(MetadataKnownArraySurrogate<string, int>)));
        using var services = new ServiceCollection()
            .AddSingleton<MetadataOrderedCodecCopier>()
            .AddSingleton<MetadataAlternativeOrderedCodecCopier>()
            .AddSingleton<MetadataOrderedConverter>()
            .AddSingleton<MetadataAlternativeOrderedConverter>()
            .BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        if (provider.GetCodec<BindingPair<string, int>>().GetType() != typeof(MetadataOrderedCodecCopier)
            || provider.GetDeepCopier<BindingPair<string, int>>().GetType() != typeof(MetadataOrderedCodecCopier))
        {
            throw new InvalidOperationException("Interleaved A/B/A codec and copier registrations did not select the last matching contract.");
        }
        var selectConverter = typeof(CodecProvider).GetMethod("TryGetSurrogateCodec", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<SurrogateSelection>(provider);
        if (!selectConverter(typeof(BindingPair<string, int>), typeof(BindingPair<,>), out var codec, out var arguments)
            || codec != typeof(SurrogateCodec<BindingPair<string, int>, MetadataKnownArraySurrogate<string, int>, MetadataOrderedConverter>)
            || arguments is not [MetadataOrderedConverter])
        {
            throw new InvalidOperationException("Interleaved A/B/A converter registrations did not retain the selected surrogate contract.");
        }
        Console.WriteLine("InterleavedRegistrationOrder passed.");
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
internal readonly record struct MetadataRootedArrayValue(long Value);
internal readonly record struct MetadataUnrootedArrayValue(long Value);
internal struct MetadataKnownArraySurrogate<T, TArray>;

[RegisterConverter]
internal sealed class MetadataKnownArrayConverter<T> : IConverter<MetadataMixedTarget<T>, MetadataKnownArraySurrogate<T, int[]>>
{
    public MetadataMixedTarget<T> ConvertFromSurrogate(in MetadataKnownArraySurrogate<T, int[]> surrogate) => new();
    public MetadataKnownArraySurrogate<T, int[]> ConvertToSurrogate(in MetadataMixedTarget<T> value) => default;
}
internal sealed class MetadataValueArrayConverter : IConverter<MetadataMixedTarget<string>, MetadataKnownArraySurrogate<string, MetadataRootedArrayValue[]>>
{
    public MetadataMixedTarget<string> ConvertFromSurrogate(in MetadataKnownArraySurrogate<string, MetadataRootedArrayValue[]> surrogate) => new();
    public MetadataKnownArraySurrogate<string, MetadataRootedArrayValue[]> ConvertToSurrogate(in MetadataMixedTarget<string> value) => default;
}
internal sealed class MetadataOrderedCodecCopier : IFieldCodec<BindingPair<Guid, int>>, IFieldCodec<BindingPair<string, int>>,
    IDeepCopier<BindingPair<Guid, int>>, IDeepCopier<BindingPair<string, int>>
{
    void IFieldCodec.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, [AllowNull] Type expected, object? value) => throw new NotSupportedException();
    object? IFieldCodec.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
    object? IDeepCopier.DeepCopy(object? input, CopyContext context) => throw new NotSupportedException();
    void IFieldCodec<BindingPair<Guid, int>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, [AllowNull] Type expected, [AllowNull] BindingPair<Guid, int> value) => throw new NotSupportedException();
    void IFieldCodec<BindingPair<string, int>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, [AllowNull] Type expected, [AllowNull] BindingPair<string, int> value) => throw new NotSupportedException();
    BindingPair<Guid, int> IFieldCodec<BindingPair<Guid, int>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
    BindingPair<string, int> IFieldCodec<BindingPair<string, int>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
    [return: NotNullIfNotNull(nameof(input))]
    public BindingPair<Guid, int>? DeepCopy(BindingPair<Guid, int>? input, CopyContext context) => input is null ? null : new();
    [return: NotNullIfNotNull(nameof(input))]
    public BindingPair<string, int>? DeepCopy(BindingPair<string, int>? input, CopyContext context) => input is null ? null : new();
}
internal sealed class MetadataAlternativeOrderedCodecCopier : IFieldCodec<BindingPair<string, int>>, IDeepCopier<BindingPair<string, int>>
{
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, [AllowNull] Type expected, [AllowNull] BindingPair<string, int> value)
        where TBufferWriter : IBufferWriter<byte> => throw new NotSupportedException();
    public BindingPair<string, int> ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
    [return: NotNullIfNotNull(nameof(input))]
    public BindingPair<string, int>? DeepCopy(BindingPair<string, int>? input, CopyContext context) => input is null ? null : new();
}
internal sealed class MetadataOrderedConverter :
    IConverter<BindingPair<Guid, int>, MetadataKnownArraySurrogate<Guid, int>>,
    IConverter<BindingPair<string, int>, MetadataKnownArraySurrogate<string, int>>
{
    public BindingPair<Guid, int> ConvertFromSurrogate(in MetadataKnownArraySurrogate<Guid, int> surrogate) => new();
    public BindingPair<string, int> ConvertFromSurrogate(in MetadataKnownArraySurrogate<string, int> surrogate) => new();
    public MetadataKnownArraySurrogate<Guid, int> ConvertToSurrogate(in BindingPair<Guid, int> value) => default;
    public MetadataKnownArraySurrogate<string, int> ConvertToSurrogate(in BindingPair<string, int> value) => default;
}
internal sealed class MetadataAlternativeOrderedConverter : IConverter<BindingPair<string, int>, MetadataKnownArraySurrogate<int, int>>
{
    public BindingPair<string, int> ConvertFromSurrogate(in MetadataKnownArraySurrogate<int, int> surrogate) => new();
    public MetadataKnownArraySurrogate<int, int> ConvertToSurrogate(in BindingPair<string, int> value) => default;
}
