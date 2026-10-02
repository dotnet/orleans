using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;
using Orleans.Serialization.WireProtocol;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
public class ManifestContractTests
{
    [Fact]
    public void ExplicitContractsInitializeBothConsumersWithoutInspectingImplementations()
    {
        var implementation = new UninspectableImplementation(typeof(Int32Codec));
        var options = new TypeManifestOptions();
        options.AddSerializer(implementation, typeof(FirstTarget));
        options.AddBaseCodec(implementation, typeof(FirstTarget));
        options.AddValueSerializer(implementation, typeof(ValueTarget));
        options.AddFieldCodec(implementation, typeof(SecondTarget));
        options.AddCopier(implementation, typeof(FirstTarget));
        options.AddBaseCopier(implementation, typeof(FirstTarget));
        options.AddActivator(implementation, typeof(FirstTarget));
        options.AddConverter(implementation, typeof(FirstTarget), typeof(Surrogate));

        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        var converter = CreateConverter(options);

        AssertMapping(provider, "_fieldCodecs", typeof(FirstTarget), implementation);
        AssertMapping(provider, "_fieldCodecs", typeof(SecondTarget), implementation);
        AssertMapping(provider, "_baseCodecs", typeof(FirstTarget), implementation);
        AssertMapping(provider, "_valueSerializers", typeof(ValueTarget), implementation);
        AssertMapping(provider, "_copiers", typeof(FirstTarget), implementation);
        AssertMapping(provider, "_baseCopiers", typeof(FirstTarget), implementation);
        AssertMapping(provider, "_activators", typeof(FirstTarget), implementation);
        AssertMapping(provider, "_converters", typeof(FirstTarget), implementation);
        Assert.Equal(typeof(FirstTarget), converter.Parse(converter.Format(typeof(FirstTarget))));
        Assert.Equal(typeof(SecondTarget), converter.Parse(converter.Format(typeof(SecondTarget))));
        Assert.Equal(typeof(Surrogate), converter.Parse(converter.Format(typeof(Surrogate))));
        Assert.Equal(0, implementation.InterfaceInspections);
    }

    [Fact]
    public void ExplicitContractsAreIdempotentAndSupportMultipleTargets()
    {
        var options = new TypeManifestOptions();
        options.AddSerializer(typeof(Int32Codec), typeof(FirstTarget));
        options.AddSerializer(typeof(Int32Codec), typeof(FirstTarget));
        options.AddSerializer(typeof(Int32Codec), typeof(SecondTarget));

        Assert.Equal(2, options.SerializerContracts[typeof(Int32Codec)].Count);
        Assert.Single(options.SerializerTypes);
        var converter = CreateConverter(options);
        Assert.Equal(typeof(FirstTarget), converter.Parse(converter.Format(typeof(FirstTarget))));
        Assert.Equal(typeof(SecondTarget), converter.Parse(converter.Format(typeof(SecondTarget))));
    }

    [Fact]
    public void LegacyAndExplicitEntriesPreserveCollectionOrderAndRemoval()
    {
        var options = new TypeManifestOptions();
        options.AddSerializer(typeof(Int32Codec));
        options.AddSerializer(typeof(ReplacementCodec), typeof(int));
        options.AddCopier(typeof(ShallowCopier<int>));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        AssertMapping(provider, "_fieldCodecs", typeof(int), typeof(ReplacementCodec));
        AssertMapping(provider, "_copiers", typeof(int), typeof(ShallowCopier<int>));

        options.SerializerTypes.Remove(typeof(ReplacementCodec));
        provider = new CodecProvider(services, Options.Create(options));
        AssertMapping(provider, "_fieldCodecs", typeof(int), typeof(Int32Codec));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyDiscoveryRetainsOtherContractKindsWhenOneKindIsExplicit(bool useCollection)
    {
        var options = new TypeManifestOptions();
        if (useCollection)
        {
            options.Serializers.Add(typeof(MixedImplementation));
        }
        else
        {
            options.AddSerializer(typeof(MixedImplementation));
        }

        options.AddBaseCodec(typeof(MixedImplementation), typeof(SecondTarget));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        AssertMapping(provider, "_fieldCodecs", typeof(FirstTarget), typeof(MixedImplementation));
        AssertMapping(provider, "_baseCodecs", typeof(SecondTarget), typeof(MixedImplementation));
        var converter = CreateConverter(options);
        Assert.Equal(typeof(FirstTarget), converter.Parse(converter.Format(typeof(FirstTarget))));
        Assert.Equal(typeof(SecondTarget), converter.Parse(converter.Format(typeof(SecondTarget))));
    }

    [Fact]
    public void LegacyDiscoveryRetainsOtherTargetsOfTheSameContractKind()
    {
        var options = new TypeManifestOptions();
        options.AddSerializer(typeof(MixedImplementation));
        options.AddSerializer(typeof(MixedImplementation), typeof(SecondTarget));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        AssertMapping(provider, "_fieldCodecs", typeof(FirstTarget), typeof(MixedImplementation));
        AssertMapping(provider, "_fieldCodecs", typeof(SecondTarget), typeof(MixedImplementation));
        var converter = CreateConverter(options);
        Assert.Equal(typeof(FirstTarget), converter.Parse(converter.Format(typeof(FirstTarget))));
        Assert.Equal(typeof(SecondTarget), converter.Parse(converter.Format(typeof(SecondTarget))));
    }

    [Fact]
    public void ExplicitOpenGenericEntriesMapDefinitionsAndAuthorizeTargets()
    {
        var options = new TypeManifestOptions();
        options.AddSerializer(typeof(ValueTupleCodec<,>), typeof(ValueTuple<,>));
        options.AddCopier(typeof(ValueTupleCopier<,>), typeof(ValueTuple<,>));
        options.AddActivator(typeof(DefaultValueTypeActivator<>), typeof(ValueTarget));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        AssertMapping(provider, "_fieldCodecs", typeof(ValueTuple<,>), typeof(ValueTupleCodec<,>));
        AssertMapping(provider, "_copiers", typeof(ValueTuple<,>), typeof(ValueTupleCopier<,>));
        var converter = CreateConverter(options);
        Assert.Equal(typeof(ValueTuple<,>), converter.Parse(converter.Format(typeof(ValueTuple<,>))));
    }

    [Theory]
    [InlineData(nameof(TypeManifestOptions.AddSerializer))]
    [InlineData(nameof(TypeManifestOptions.AddFieldCodec))]
    [InlineData(nameof(TypeManifestOptions.AddBaseCodec))]
    [InlineData(nameof(TypeManifestOptions.AddValueSerializer))]
    [InlineData(nameof(TypeManifestOptions.AddCopier))]
    [InlineData(nameof(TypeManifestOptions.AddBaseCopier))]
    [InlineData(nameof(TypeManifestOptions.AddActivator))]
    public void ExplicitContractApisPreserveConstructorsAndTargetInterfaces(string methodName)
    {
        var parameters = typeof(TypeManifestOptions).GetMethod(methodName, [typeof(Type), typeof(Type)])!.GetParameters();
        Assert.Equal(DynamicallyAccessedMemberTypes.PublicConstructors,
            parameters[0].GetCustomAttribute<DynamicallyAccessedMembersAttribute>()!.MemberTypes);
        Assert.Equal(DynamicallyAccessedMemberTypes.Interfaces,
            parameters[1].GetCustomAttribute<DynamicallyAccessedMembersAttribute>()!.MemberTypes);
    }

    [Fact]
    public void ConverterContractPreservesBothTargetTypes()
    {
        var parameters = typeof(TypeManifestOptions).GetMethod(nameof(TypeManifestOptions.AddConverter),
            [typeof(Type), typeof(Type), typeof(Type)])!.GetParameters();
        Assert.Equal(DynamicallyAccessedMemberTypes.PublicConstructors,
            parameters[0].GetCustomAttribute<DynamicallyAccessedMembersAttribute>()!.MemberTypes);
        Assert.All(parameters[1..], parameter => Assert.Equal(DynamicallyAccessedMemberTypes.Interfaces,
            parameter.GetCustomAttribute<DynamicallyAccessedMembersAttribute>()!.MemberTypes));
    }

    [Fact]
    public void GenericConverterSurrogateDescriptionPreservesNestedArraysAndParameterOrder()
    {
        var options = new TypeManifestOptions();
        options.AddConverter(typeof(GenericConverter<,>), typeof(GenericTarget<,>),
            SerializationType.Create(typeof(GenericSurrogate<>),
                SerializationType.Array(SerializationType.Create(typeof(ValueTuple<,>),
                    SerializationType.Parameter(1), SerializationType.Parameter(0)))));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        object?[] arguments = [typeof(GenericTarget<string, int>), typeof(GenericTarget<,>), null, null];
        var result = typeof(CodecProvider).GetMethod("TryGetSurrogateCodec", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, arguments);

        Assert.Equal(true, result);
        var codecType = Assert.IsAssignableFrom<Type>(arguments[2]);
        Assert.Equal(typeof(GenericSurrogate<(int, string)[]>), codecType.GetGenericArguments()[1]);
        Assert.IsType<GenericConverter<string, int>>(Assert.Single(Assert.IsType<object[]>(arguments[3])));
    }

    [Fact]
    public void TargetDescriptionsBindFixedNestedArgumentsWithoutUsingTargetArity()
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(PatternCodec<>), typeof(IBaseCodec<>),
            SerializationType.Create(typeof(PatternOuter<>.Nested<>),
                SerializationType.Parameter(0), SerializationType.Create(typeof(FixedArgument<int>))));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType<PatternCodec<string>>(provider.GetBaseCodec<PatternOuter<string>.Nested<FixedArgument<int>>>());
        Assert.Null(provider.GetType().GetMethod("CloseImplementation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider,
            [typeof(PatternCodec<>), typeof(PatternOuter<string>.Nested<FixedArgument<Guid>>), typeof(IBaseCodec<>)]));
    }

    [Fact]
    public void TargetDescriptionsBindReorderedImplementationParameters()
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(ReorderedCodec<,>), typeof(IBaseCodec<>),
            SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Parameter(1), SerializationType.Parameter(0)));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType<ReorderedCodec<int, string>>(provider.GetBaseCodec<GenericTarget<string, int>>());
    }

    [Fact]
    public void DifferentImplementationsWithTheSameOpenTargetSelectMatchingPatterns()
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(PatternCodec<>), typeof(IBaseCodec<>),
            SerializationType.Create(typeof(PatternOuter<>.Nested<>), SerializationType.Parameter(0),
                SerializationType.Create(typeof(FixedArgument<int>))));
        options.AddSerializationContract(typeof(OtherPatternCodec<>), typeof(IBaseCodec<>),
            SerializationType.Create(typeof(PatternOuter<>.Nested<>), SerializationType.Parameter(0),
                SerializationType.Create(typeof(FixedArgument<string>))));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType<PatternCodec<Guid>>(provider.GetBaseCodec<PatternOuter<Guid>.Nested<FixedArgument<int>>>());
        Assert.IsType<OtherPatternCodec<Guid>>(provider.GetBaseCodec<PatternOuter<Guid>.Nested<FixedArgument<string>>>());
    }

    [Fact]
    public void BaseCopierOnlyContractsAuthorizeTheirTargets()
    {
        var options = new TypeManifestOptions();
        var implementation = new UninspectableImplementation(typeof(Int32Codec));
        options.AddBaseCopier(implementation, typeof(FirstTarget));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        AssertMapping(provider, "_baseCopiers", typeof(FirstTarget), implementation);
        var converter = CreateConverter(options);
        Assert.Equal(typeof(FirstTarget), converter.Parse(converter.Format(typeof(FirstTarget))));
        Assert.Equal(0, implementation.InterfaceInspections);
    }

    [Fact]
    public void ConverterSelectsTheSurrogateForItsMatchingTargetPattern()
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(PatternConverter<>), typeof(IConverter<,>),
            SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Parameter(0), SerializationType.Create(typeof(int))),
            SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Parameter(0)));
        options.AddSerializationContract(typeof(PatternConverter<>), typeof(IConverter<,>),
            SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Parameter(0), SerializationType.Create(typeof(string))),
            SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Array(SerializationType.Parameter(0))));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        foreach (var (target, surrogate) in new[]
        {
            (typeof(GenericTarget<Guid, int>), typeof(GenericSurrogate<Guid>)),
            (typeof(GenericTarget<Guid, string>), typeof(GenericSurrogate<Guid[]>))
        })
        {
            object?[] arguments = [target, typeof(GenericTarget<,>), null, null];
            var result = typeof(CodecProvider).GetMethod("TryGetSurrogateCodec", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, arguments);
            Assert.Equal(true, result);
            Assert.Equal(surrogate, Assert.IsAssignableFrom<Type>(arguments[2]).GetGenericArguments()[1]);
            Assert.IsType<PatternConverter<Guid>>(Assert.Single(Assert.IsType<object[]>(arguments[3])));
        }
    }

    [Fact]
    public void ClosedGenericConverterContractsUseTheirExactTargetEntry()
    {
        var options = new TypeManifestOptions();
        options.AddConverter(typeof(GenericConverter<string, int>), typeof(GenericTarget<string, int>),
            typeof(GenericSurrogate<(int, string)[]>));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        object?[] arguments = [typeof(GenericTarget<string, int>), typeof(GenericTarget<,>), null, null];
        var result = typeof(CodecProvider).GetMethod("TryGetSurrogateCodec", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, arguments);

        Assert.Equal(true, result);
        Assert.Equal(typeof(GenericSurrogate<(int, string)[]>), Assert.IsAssignableFrom<Type>(arguments[2]).GetGenericArguments()[1]);
        Assert.IsType<GenericConverter<string, int>>(Assert.Single(Assert.IsType<object[]>(arguments[3])));
    }

    [Fact]
    public void SerializationDescriptionsValidateAndCopyTheirArguments()
    {
        var first = SerializationType.Parameter(0);
        var arguments = new[] { first };
        var description = SerializationType.Create(typeof(GenericSurrogate<>), arguments);
        arguments[0] = SerializationType.Parameter(1);

        Assert.Same(first, Assert.Single(description.Arguments));
        Assert.Throws<ArgumentOutOfRangeException>(() => SerializationType.Parameter(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SerializationType.Array(first, 0));
        Assert.Throws<ArgumentNullException>(() => SerializationType.Array(null!));
        Assert.Throws<ArgumentException>(() => SerializationType.Create(typeof(int), first));
        Assert.Throws<ArgumentException>(() => SerializationType.Create(typeof(GenericSurrogate<>), first, first));
    }

    [Fact]
    public void ParameterizedArrayContractsInitializeWithoutImplementationInterfaceDiscovery()
    {
        var implementation = new UninspectableImplementation(typeof(ArrayCodec<>));
        var options = new TypeManifestOptions();
        options.AddSerializationContract(implementation, typeof(IFieldCodec<>), SerializationType.Array(SerializationType.Parameter(0)));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        var target = typeof(ArrayCodec<>).GetGenericArguments()[0].MakeArrayType();

        AssertMapping(provider, "_fieldCodecs", target, implementation);
        _ = CreateConverter(options);
        Assert.Equal(0, implementation.InterfaceInspections);
        Assert.Equal("contractType", Assert.Throws<ArgumentException>(() =>
            options.AddSerializationContract(typeof(Int32Codec), typeof(IDisposable), SerializationType.Create(typeof(int)))).ParamName);
    }

    [Fact]
    public void InvalidContractArgumentsThrowBeforeMutatingOptions()
    {
        var options = new TypeManifestOptions();
        Assert.Equal("type", Assert.Throws<ArgumentNullException>(() => options.AddSerializer(null!, typeof(int))).ParamName);
        Assert.Equal("targetType", Assert.Throws<ArgumentNullException>(() => options.AddCopier(typeof(Int32Codec), null!)).ParamName);
        Assert.Equal("surrogateType", Assert.Throws<ArgumentNullException>(() => options.AddConverter(typeof(Int32Codec), typeof(int), (Type)null!)).ParamName);
        Assert.Empty(options.SerializerTypes);
        Assert.Empty(options.CopierTypes);
        Assert.Empty(options.ConverterTypes);
    }

    private static TypeConverter CreateConverter(TypeManifestOptions options)
        => new(Array.Empty<ITypeConverter>(), Array.Empty<ITypeNameFilter>(), Array.Empty<ITypeFilter>(),
            Options.Create(options), new CachedTypeResolver());

    private static void AssertMapping(CodecProvider provider, string fieldName, Type target, Type expected)
    {
        var mappings = (Dictionary<Type, Type>)typeof(CodecProvider).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!;
        Assert.Equal(expected, mappings[target]);
    }

    private sealed class UninspectableImplementation(Type type) : TypeDelegator(type)
    {
        public int InterfaceInspections { get; private set; }
        public override Type[] GetGenericArguments() => typeImpl!.GetGenericArguments();
        public override Type[] GetInterfaces()
        {
            InterfaceInspections++;
            throw new InvalidOperationException("Explicit implementation contracts must be consumed directly.");
        }
    }

    private sealed class FirstTarget;
    private sealed class SecondTarget;
    private struct ValueTarget;
    private struct Surrogate;
    private sealed class ReplacementCodec;
    private sealed class MixedImplementation : IFieldCodec<FirstTarget>, IBaseCodec<SecondTarget>
    {
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, [AllowNull] Type expected, [AllowNull] FirstTarget value)
            where TBufferWriter : IBufferWriter<byte> => throw new NotSupportedException();
        public FirstTarget ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, SecondTarget value)
            where TBufferWriter : IBufferWriter<byte> => throw new NotSupportedException();
        public void Deserialize<TInput>(ref Reader<TInput> reader, SecondTarget value) => throw new NotSupportedException();
    }
    public sealed class GenericTarget<TFirst, TSecond>;
    public struct GenericSurrogate<T>;
    public class PatternOuter<T>
    {
        public sealed class Nested<TItem>;
    }
    public sealed class FixedArgument<T>;
    public sealed class PatternCodec<T> : IBaseCodec<PatternOuter<T>.Nested<FixedArgument<int>>>
    {
        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, PatternOuter<T>.Nested<FixedArgument<int>> value)
            where TBufferWriter : IBufferWriter<byte>
        { }
        public void Deserialize<TInput>(ref Reader<TInput> reader, PatternOuter<T>.Nested<FixedArgument<int>> value) { }
    }
    public sealed class OtherPatternCodec<T> : IBaseCodec<PatternOuter<T>.Nested<FixedArgument<string>>>
    {
        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, PatternOuter<T>.Nested<FixedArgument<string>> value)
            where TBufferWriter : IBufferWriter<byte>
        { }
        public void Deserialize<TInput>(ref Reader<TInput> reader, PatternOuter<T>.Nested<FixedArgument<string>> value) { }
    }
    public sealed class ReorderedCodec<TFirst, TSecond> : IBaseCodec<GenericTarget<TSecond, TFirst>>
    {
        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, GenericTarget<TSecond, TFirst> value)
            where TBufferWriter : IBufferWriter<byte>
        { }
        public void Deserialize<TInput>(ref Reader<TInput> reader, GenericTarget<TSecond, TFirst> value) { }
    }
    public sealed class PatternConverter<T> :
        IConverter<GenericTarget<T, int>, GenericSurrogate<T>>,
        IConverter<GenericTarget<T, string>, GenericSurrogate<T[]>>
    {
        public GenericTarget<T, int> ConvertFromSurrogate(in GenericSurrogate<T> surrogate) => new();
        public GenericSurrogate<T> ConvertToSurrogate(in GenericTarget<T, int> value) => default;
        public GenericTarget<T, string> ConvertFromSurrogate(in GenericSurrogate<T[]> surrogate) => new();
        public GenericSurrogate<T[]> ConvertToSurrogate(in GenericTarget<T, string> value) => default;
    }
    public sealed class GenericConverter<TFirst, TSecond> : IConverter<GenericTarget<TFirst, TSecond>, GenericSurrogate<(TSecond, TFirst)[]>>
    {
        public GenericTarget<TFirst, TSecond> ConvertFromSurrogate(in GenericSurrogate<(TSecond, TFirst)[]> surrogate) => new();
        public GenericSurrogate<(TSecond, TFirst)[]> ConvertToSurrogate(in GenericTarget<TFirst, TSecond> value) => default;
    }
}
