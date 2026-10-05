using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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

        Assert.Equal(implementation, SelectImplementation(provider, typeof(IFieldCodec<>), typeof(FirstTarget)));
        Assert.Equal(implementation, SelectImplementation(provider, typeof(IFieldCodec<>), typeof(SecondTarget)));
        Assert.Equal(implementation, SelectImplementation(provider, typeof(IBaseCodec<>), typeof(FirstTarget)));
        Assert.Equal(implementation, SelectImplementation(provider, typeof(IValueSerializer<>), typeof(ValueTarget)));
        Assert.Equal(implementation, SelectImplementation(provider, typeof(IDeepCopier<>), typeof(FirstTarget)));
        Assert.Equal(implementation, SelectImplementation(provider, typeof(IBaseCopier<>), typeof(FirstTarget)));
        Assert.Equal(implementation, SelectImplementation(provider, typeof(IActivator<>), typeof(FirstTarget)));
        Assert.Equal(implementation, SelectImplementation(provider, typeof(IConverter<,>), typeof(FirstTarget)));
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
        Assert.Equal(typeof(ReplacementCodec), SelectImplementation(provider, typeof(IFieldCodec<>), typeof(int)));
        Assert.IsType<ShallowCopier<int>>(provider.GetDeepCopier<int>());

        options.SerializerTypes.Remove(typeof(ReplacementCodec));
        provider = new CodecProvider(services, Options.Create(options));
        Assert.IsType<Int32Codec>(provider.GetCodec<int>());
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
        Assert.IsType<MixedImplementation>(provider.GetCodec<FirstTarget>());
        Assert.IsType<MixedImplementation>(provider.GetBaseCodec<SecondTarget>());
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
        Assert.Equal(typeof(MixedImplementation), SelectImplementation(provider, typeof(IFieldCodec<>), typeof(FirstTarget)));
        Assert.Equal(typeof(MixedImplementation), SelectImplementation(provider, typeof(IFieldCodec<>), typeof(SecondTarget)));
        var converter = CreateConverter(options);
        Assert.Equal(typeof(FirstTarget), converter.Parse(converter.Format(typeof(FirstTarget))));
        Assert.Equal(typeof(SecondTarget), converter.Parse(converter.Format(typeof(SecondTarget))));
    }

    [Theory]
    [InlineData(nameof(TypeManifestOptions.AddSerializer))]
    [InlineData(nameof(TypeManifestOptions.AddFieldCodec))]
    [InlineData(nameof(TypeManifestOptions.AddCopier))]
    [InlineData(nameof(TypeManifestOptions.AddActivator))]
    [InlineData(nameof(TypeManifestOptions.AddConverter))]
    public void InterfaceDiscoveryRequestsAccumulateAndRemainScopedToTheirFamily(string registration)
    {
        var options = new TypeManifestOptions();
        var implementation = typeof(MixedImplementation);
        switch (registration)
        {
            case nameof(TypeManifestOptions.AddSerializer): options.AddSerializer(implementation); break;
            case nameof(TypeManifestOptions.AddFieldCodec): options.AddFieldCodec(implementation); break;
            case nameof(TypeManifestOptions.AddCopier): options.AddCopier(implementation); break;
            case nameof(TypeManifestOptions.AddActivator): options.AddActivator(implementation); break;
            case nameof(TypeManifestOptions.AddConverter): options.AddConverter(implementation); break;
        }

        var serializer = registration is nameof(TypeManifestOptions.AddSerializer) or nameof(TypeManifestOptions.AddFieldCodec);
        Assert.Equal(serializer, options.DiscoverInterfaces(implementation, typeof(IFieldCodec<>)));
        Assert.Equal(serializer, options.DiscoverInterfaces(implementation, typeof(IBaseCodec<>)));
        Assert.Equal(serializer, options.DiscoverInterfaces(implementation, typeof(IValueSerializer<>)));
        Assert.Equal(registration == nameof(TypeManifestOptions.AddCopier), options.DiscoverInterfaces(implementation, typeof(IDeepCopier<>)));
        Assert.Equal(registration == nameof(TypeManifestOptions.AddCopier), options.DiscoverInterfaces(implementation, typeof(IBaseCopier<>)));
        Assert.Equal(registration == nameof(TypeManifestOptions.AddActivator), options.DiscoverInterfaces(implementation, typeof(IActivator<>)));
        Assert.Equal(registration == nameof(TypeManifestOptions.AddConverter), options.DiscoverInterfaces(implementation, typeof(IConverter<,>)));
        Assert.False(options.DiscoverInterfaces(typeof(SecondTarget), typeof(IFieldCodec<>)));

        options.AddSerializer(implementation);
        options.AddCopier(implementation);
        options.AddActivator(implementation);
        options.AddConverter(implementation);
        foreach (var role in new[] { typeof(IFieldCodec<>), typeof(IBaseCodec<>), typeof(IValueSerializer<>), typeof(IDeepCopier<>),
            typeof(IBaseCopier<>), typeof(IActivator<>), typeof(IConverter<,>) })
        {
            Assert.True(options.DiscoverInterfaces(implementation, role));
            Assert.False(options.DiscoverInterfaces(typeof(SecondTarget), role));
        }
    }

    [Fact]
    public void ExplicitOpenGenericEntriesSelectClosedImplementationsAndAuthorizeTargets()
    {
        var options = new TypeManifestOptions();
        options.AddSerializer(typeof(ValueTupleCodec<,>), typeof(ValueTuple<,>));
        options.AddCopier(typeof(ValueTupleCopier<,>), typeof(ValueTuple<,>));
        options.AddActivator(typeof(DefaultValueTypeActivator<>), typeof(ValueTarget));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        Assert.Equal(typeof(ValueTupleCodec<int, string>), SelectImplementation(provider, typeof(IFieldCodec<>), typeof(ValueTuple<int, string>)));
        Assert.Equal(typeof(ValueTupleCopier<int, string>), SelectImplementation(provider, typeof(IDeepCopier<>), typeof(ValueTuple<int, string>)));
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
    public void ExecutableParameterizedArraySurrogatesRequireClosedRegistration()
    {
        var options = new TypeManifestOptions();
        options.AddConverter(typeof(GenericConverter<,>), typeof(GenericTarget<,>),
            SerializationType.Create(typeof(GenericSurrogate<>),
                SerializationType.Array(SerializationType.Create(typeof(ValueTuple<,>),
                    SerializationType.Parameter(1), SerializationType.Parameter(0)))));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        object?[] arguments = [typeof(GenericTarget<string, int>), typeof(GenericTarget<,>), null, null];
        var exception = Assert.Throws<TargetInvocationException>(() =>
            typeof(CodecProvider).GetMethod("TryGetSurrogateCodec", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, arguments));
        Assert.Contains("SerializationType.Create(typeof(ClosedArray))", Assert.IsType<NotSupportedException>(exception.InnerException).Message);
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
        Assert.Throws<KeyNotFoundException>(() => provider.GetBaseCodec<PatternOuter<string>.Nested<FixedArgument<Guid>>>());
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MixedPlainAndDescribedRegistrationsCloseTheSelectedCandidate(bool plainLast, bool useDescription)
    {
        var options = new TypeManifestOptions();
        if (!plainLast)
        {
            AddPlain();
        }

        options.AddSerializationContract(typeof(MixedRegistrationActivator<>), typeof(IActivator<>),
            SerializationType.Create(typeof(FixedArgument<>),
                SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Parameter(0))));
        if (plainLast)
        {
            AddPlain();
        }

        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        var plain = provider.GetActivator<FixedArgument<string>>();
        Assert.IsType<MixedRegistrationActivator<string>>(plain);
        Assert.IsType<FixedArgument<string>>(plain.Create());
        var nested = provider.GetActivator<FixedArgument<GenericSurrogate<string>>>();
        Assert.IsType(plainLast ? typeof(MixedRegistrationActivator<GenericSurrogate<string>>) : typeof(MixedRegistrationActivator<string>), nested);
        Assert.IsType<FixedArgument<GenericSurrogate<string>>>(nested.Create());

        void AddPlain()
        {
            if (useDescription)
            {
                options.AddSerializationContract(typeof(MixedRegistrationActivator<>), typeof(IActivator<>),
                    SerializationType.Create(typeof(FixedArgument<>)));
            }
            else
            {
                options.AddActivator(typeof(MixedRegistrationActivator<>), typeof(FixedArgument<>));
            }
        }
    }

    [Fact]
    public void GenericDefinitionDescriptionsBindPositionalTargetArguments()
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(LegacyOrderedCopier<,>), typeof(IDeepCopier<>),
            SerializationType.Create(typeof(GenericTarget<,>)));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType<LegacyOrderedCopier<Guid, int>>(provider.GetDeepCopier<GenericTarget<Guid, int>>());
    }

    [Fact]
    public void NestedGenericDefinitionDescriptionsRespectExistingParameterBindings()
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(PatternCodec<>), typeof(IBaseCodec<>),
            SerializationType.Create(typeof(PatternOuter<>.Nested<>),
                SerializationType.Parameter(0), SerializationType.Create(typeof(FixedArgument<>))));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType<PatternCodec<int>>(provider.GetBaseCodec<PatternOuter<int>.Nested<FixedArgument<int>>>());
        Assert.Throws<KeyNotFoundException>(() => provider.GetBaseCodec<PatternOuter<string>.Nested<FixedArgument<int>>>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericDefinitionDescriptionsRejectArityMismatches(bool nested)
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(nested ? typeof(PairFallbackCopier<,>) : typeof(ParameterCopier<>), typeof(IDeepCopier<>),
            nested
                ? SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Create(typeof(FixedArgument<>)), SerializationType.Parameter(1))
                : SerializationType.Create(typeof(GenericTarget<,>)));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        var target = nested ? typeof(GenericTarget<FixedArgument<Guid>, int>) : typeof(GenericTarget<Guid, int>);

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetDeepCopier(target));
        Assert.Contains(nested ? "arity 1, but implementation arity is 2" : "arity 2, but implementation arity is 1", exception.Message);
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
        Assert.Equal(implementation, SelectImplementation(provider, typeof(IBaseCopier<>), typeof(FirstTarget)));
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
        options.AddSerializationContract(typeof(PatternConverter<Guid>), typeof(IConverter<,>),
            SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Create(typeof(Guid)), SerializationType.Create(typeof(string))),
            SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Create(typeof(Guid[]))));
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
    public void ClosedConverterRegistrationOverridesAnUnresolvedArraySurrogateRecipe()
    {
        var options = new TypeManifestOptions();
        options.AddConverter(typeof(GenericConverter<,>), typeof(GenericTarget<,>),
            SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Array(SerializationType.Parameter(0))));
        options.AddConverter(typeof(GenericConverter<string, int>), typeof(GenericTarget<string, int>),
            typeof(GenericSurrogate<(int, string)[]>));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        object?[] arguments = [typeof(GenericTarget<string, int>), typeof(GenericTarget<,>), null, null];

        Assert.Equal(true, typeof(CodecProvider).GetMethod("TryGetSurrogateCodec", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, arguments));
        Assert.Equal(typeof(GenericSurrogate<(int, string)[]>), Assert.IsAssignableFrom<Type>(arguments[2]).GetGenericArguments()[1]);
        Assert.IsType<GenericConverter<string, int>>(Assert.Single(Assert.IsType<object[]>(arguments[3])));
    }

    [Fact]
    public void LegacyClosedConverterDiscoveryPreservesItsArraySurrogate()
    {
        var options = new TypeManifestOptions();
        options.AddConverter(typeof(GenericConverter<string, int>));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        object?[] arguments = [typeof(GenericTarget<string, int>), typeof(GenericTarget<,>), null, null];

        Assert.Equal(true, typeof(CodecProvider).GetMethod("TryGetSurrogateCodec", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, arguments));
        Assert.Equal(typeof(GenericSurrogate<(int, string)[]>), Assert.IsAssignableFrom<Type>(arguments[2]).GetGenericArguments()[1]);
        Assert.IsType<GenericConverter<string, int>>(Assert.Single(Assert.IsType<object[]>(arguments[3])));
    }

    [Fact]
    public void ConcreteArrayDescriptorsAuthorizeTheirElementTypeNames()
    {
        var options = new TypeManifestOptions();
        options.AddConverter(typeof(GenericConverter<string, int>), typeof(GenericTarget<string, int>),
            SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Create(typeof((int, string)[]))));
        var converter = new TypeConverter(Array.Empty<ITypeConverter>(), Array.Empty<ITypeNameFilter>(), [new RejectUnregisteredTypes()],
            Options.Create(options), new CachedTypeResolver());
        var surrogate = typeof(GenericSurrogate<(int, string)[]>);

        Assert.Equal(surrogate, converter.Parse(converter.Format(surrogate)));
        Assert.Throws<InvalidOperationException>(() => converter.Format(typeof(SecondTarget)));
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
        var parameter = typeof(GenericSurrogate<>).GetGenericArguments()[0];
        Assert.Equal("type", Assert.Throws<ArgumentException>(() => SerializationType.Create(parameter.MakeArrayType())).ParamName);
    }

    [Fact]
    public void ExecutableTypeResolutionUsesSourceKnownClosedArrays()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(new TypeManifestOptions()));
        var generic = typeof(CodecProvider).GetMethod("ConstructGenericImplementation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(typeof(GenericSurrogate<string>), generic.Invoke(provider, [typeof(GenericSurrogate<>), new[] { typeof(string) }]));
        var resolve = typeof(CodecProvider).GetMethod("ResolveSerializationType", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var type in new[] { typeof(string[]), typeof(string[,]), typeof(FixedArgument<byte>[]) })
        {
            Assert.Equal(type, resolve.Invoke(provider, [SerializationType.Create(type), Type.EmptyTypes]));
        }
        Assert.Equal(typeof(GenericTarget<string, int[]>), resolve.Invoke(provider,
            [SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Parameter(0), SerializationType.Create(typeof(int[]))),
                new[] { typeof(string) }]));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public void ExecutableArrayPatternsRejectWithClosedRegistrationGuidance(int rank, bool openParameter)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(new TypeManifestOptions()));
        var resolve = typeof(CodecProvider).GetMethod("ResolveSerializationType", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var parameter = openParameter ? typeof(GenericSurrogate<>).GetGenericArguments()[0] : typeof(string);
        var description = SerializationType.Array(SerializationType.Parameter(0), rank);

        var exception = Assert.Throws<TargetInvocationException>(() => resolve.Invoke(provider, [description, new[] { parameter }]));
        var failure = Assert.IsType<NotSupportedException>(exception.InnerException);
        Assert.Contains("SerializationType.Create(typeof(ClosedArray))", failure.Message);
        Assert.Contains("explicit closed converter registration", failure.Message);
        exception = Assert.Throws<TargetInvocationException>(() =>
            resolve.Invoke(provider, [SerializationType.Array(SerializationType.Create(typeof(string)), rank), Type.EmptyTypes]));
        Assert.Equal(failure.Message, Assert.IsType<NotSupportedException>(exception.InnerException).Message);
    }

    [Fact]
    public void MaterializationBoundariesPreserveInvalidShapeErrorsOnJit()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(new TypeManifestOptions()));
        var generic = typeof(CodecProvider).GetMethod("ConstructGenericImplementation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var exception = Assert.Throws<TargetInvocationException>(() => generic.Invoke(provider, [typeof(GenericSurrogate<>), Type.EmptyTypes]));
        Assert.IsType<ArgumentException>(exception.InnerException);
        Assert.Throws<ArgumentOutOfRangeException>(() => SerializationType.Array(SerializationType.Create(typeof(string)), 0));
    }

    [Fact]
    public void ParameterizedArrayContractsInitializeWithoutImplementationInterfaceDiscovery()
    {
        var implementation = new UninspectableImplementation(typeof(ArrayCodec<>));
        var options = new TypeManifestOptions();
        options.AddSerializationContract(implementation, typeof(IFieldCodec<>), SerializationType.Array(SerializationType.Parameter(0)));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        _ = CreateConverter(options);
        Assert.Equal(0, implementation.InterfaceInspections);
        Assert.Equal("contractType", Assert.Throws<ArgumentException>(() =>
            options.AddSerializationContract(typeof(Int32Codec), typeof(IDisposable), SerializationType.Create(typeof(int)))).ParamName);
    }

    [Fact]
    public void ParameterizedArrayContractsResolveTheirCodecAndCopier()
    {
        var options = new TypeManifestOptions();
        var target = SerializationType.Array(SerializationType.Parameter(0));
        options.AddSerializationContract(typeof(PatternArrayCodec<>), typeof(IFieldCodec<>), target);
        options.AddSerializationContract(typeof(PatternArrayCopier<>), typeof(IDeepCopier<>), target);
        using var services = new ServiceCollection()
            .AddSingleton<IFieldCodec<int>>(new Int32Codec())
            .BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType<PatternArrayCodec<int>>(provider.GetCodec<int[]>());
        var copier = Assert.IsType<PatternArrayCopier<int>>(provider.GetDeepCopier<int[]>());
        var input = new[] { 1, 2, 3 };
        var result = copier.DeepCopy(input, null!);
        Assert.Equal(input, result);
        Assert.NotSame(input, result);
    }

    [Fact]
    public void ArrayPatternsMatchRankAndNestedElementShape()
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(PatternArrayCopier<>), typeof(IDeepCopier<>),
            SerializationType.Array(SerializationType.Parameter(0)));
        options.AddSerializationContract(typeof(PatternMatrixCopier<>), typeof(IDeepCopier<>),
            SerializationType.Array(SerializationType.Parameter(0), 2));
        options.AddSerializationContract(typeof(PatternNestedArrayCopier<>), typeof(IDeepCopier<>),
            SerializationType.Array(SerializationType.Create(typeof(GenericTarget<,>),
                SerializationType.Parameter(0), SerializationType.Create(typeof(int)))));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType<PatternArrayCopier<int>>(provider.GetDeepCopier<int[]>());
        Assert.IsType<PatternMatrixCopier<int>>(provider.GetDeepCopier<int[,]>());
        Assert.IsType<PatternArrayCopier<int[]>>(provider.GetDeepCopier<int[][]>());
        Assert.IsType<PatternNestedArrayCopier<string>>(provider.GetDeepCopier<GenericTarget<string, int>[]>());
        var nonVector = typeof(int).MakeArrayType(1);
        object?[] arguments = [typeof(IDeepCopier<>), nonVector, nonVector, null];
        Assert.Equal(false, typeof(CodecProvider).GetMethod("TrySelectImplementation", BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(Type), typeof(Type), typeof(Type), typeof(Type).MakeByRefType()])!.Invoke(provider, arguments));
        Assert.Null(arguments[3]);
    }

    [Fact]
    public void NamedContractsTakePriorityOverBareParameterPatterns()
    {
        var options = new TypeManifestOptions();
        options.AddCopier(typeof(ShallowCopier<int>), typeof(int));
        options.AddCopier(typeof(ShallowCopier<GenericTarget<string, int>>), typeof(GenericTarget<string, int>));
        options.AddSerializationContract(typeof(ParameterCopier<>), typeof(IDeepCopier<>), SerializationType.Parameter(0));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType<ShallowCopier<int>>(provider.GetDeepCopier<int>());
        Assert.IsType<ShallowCopier<GenericTarget<string, int>>>(provider.GetDeepCopier<GenericTarget<string, int>>());
        Assert.IsType<ParameterCopier<FirstTarget>>(provider.GetDeepCopier<FirstTarget>());
        Assert.IsType<ParameterCopier<GenericTarget<Guid, string>>>(provider.GetDeepCopier<GenericTarget<Guid, string>>());
    }

    [Fact]
    public void ExactArrayContractsTakePriorityOverLaterPatterns()
    {
        var options = new TypeManifestOptions();
        options.AddCopier(typeof(ShallowCopier<int[]>), typeof(int[]));
        options.AddSerializationContract(typeof(PatternArrayCopier<>), typeof(IDeepCopier<>),
            SerializationType.Array(SerializationType.Parameter(0)));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType<ShallowCopier<int[]>>(provider.GetDeepCopier<int[]>());
        Assert.IsType<PatternArrayCopier<string>>(provider.GetDeepCopier<string[]>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnnamedConverterContractsSelectTheirStoredSurrogate(bool arrayTarget)
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(arrayTarget ? typeof(PatternArrayConverter<>) : typeof(ParameterConverter<>), typeof(IConverter<,>),
            arrayTarget ? SerializationType.Array(SerializationType.Parameter(0)) : SerializationType.Parameter(0),
            SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Parameter(0)));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        var target = arrayTarget ? typeof(FirstTarget[]) : typeof(FirstTarget);
        object?[] arguments = [target, target, null, null];
        var result = typeof(CodecProvider).GetMethod("TryGetSurrogateCodec", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, arguments);

        Assert.Equal(true, result);
        Assert.Equal(typeof(GenericSurrogate<FirstTarget>), Assert.IsAssignableFrom<Type>(arguments[2]).GetGenericArguments()[1]);
        Assert.IsType(arrayTarget ? typeof(PatternArrayConverter<FirstTarget>) : typeof(ParameterConverter<FirstTarget>),
            Assert.Single(Assert.IsType<object[]>(arguments[3])));
    }

    [Theory]
    [InlineData("Legacy")]
    [InlineData("Closed")]
    [InlineData("Pattern")]
    public void ArrayConvertersParticipateInPublicSerializationAndCopying(string registration)
    {
        var converter = new ArrayConverter<int>();
        using var services = new ServiceCollection()
            .AddSingleton(converter)
            .AddSerializer(builder => builder.Configure(options =>
            {
                switch (registration)
                {
                    case "Legacy":
                        options.AddConverter(typeof(ArrayConverter<>));
                        break;
                    case "Closed":
                        options.AddConverter(typeof(ArrayConverter<int>), typeof(int[]), typeof(ArraySurrogate<int>));
                        break;
                    case "Pattern":
                        options.AddSerializationContract(typeof(ArrayConverter<>), typeof(IConverter<,>),
                            SerializationType.Array(SerializationType.Parameter(0)),
                            SerializationType.Create(typeof(ArraySurrogate<>), SerializationType.Parameter(0)));
                        break;
                }
            }))
            .BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        Assert.IsType<SurrogateCodec<int[], ArraySurrogate<int>, ArrayConverter<int>>>(provider.GetCodec<int[]>());
        Assert.IsType<SurrogateCodec<int[], ArraySurrogate<int>, ArrayConverter<int>>>(provider.GetDeepCopier<int[]>());
        var serializer = services.GetRequiredService<Serializer<int[]>>();
        var input = new[] { 7, 11, 19 };

        Assert.Equal(input, serializer.Deserialize(serializer.SerializeToArray(input)));
        var copy = services.GetRequiredService<DeepCopier<int[]>>().Copy(input);
        Assert.Equal(input, copy);
        Assert.NotSame(input, copy);
        Assert.Equal(2, converter.ToSurrogateCalls);
        Assert.Equal(2, converter.FromSurrogateCalls);
        Assert.Null(serializer.Deserialize(serializer.SerializeToArray(null!)));
        Assert.Null(services.GetRequiredService<DeepCopier<int[]>>().Copy(null!));
        Assert.Equal(2, converter.ToSurrogateCalls);
        Assert.Equal(2, converter.FromSurrogateCalls);
    }

    [Theory]
    [InlineData("ClosedBuiltin")]
    [InlineData("ClosedCustom")]
    [InlineData("PatternCustom")]
    public void DirectArrayCodecsAndCopiersRetainPriorityOverConverters(string registration)
    {
        var converter = new ArrayConverter<int>();
        using var services = new ServiceCollection()
            .AddSingleton(converter)
            .AddSerializer(builder => builder.Configure(options =>
            {
                options.AddConverter(typeof(ArrayConverter<int>), typeof(int[]), typeof(ArraySurrogate<int>));
                if (registration == "PatternCustom")
                {
                    var target = SerializationType.Array(SerializationType.Parameter(0));
                    options.AddSerializationContract(typeof(PatternArrayCodec<>), typeof(IFieldCodec<>), target);
                    options.AddSerializationContract(typeof(PatternArrayCopier<>), typeof(IDeepCopier<>), target);
                }
                else
                {
                    options.AddSerializer(registration == "ClosedBuiltin" ? typeof(ArrayCodec<int>) : typeof(PatternArrayCodec<int>), typeof(int[]));
                    options.AddCopier(registration == "ClosedBuiltin" ? typeof(ArrayCopier<int>) : typeof(PatternArrayCopier<int>), typeof(int[]));
                }
            }))
            .BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        Assert.IsType(registration == "ClosedBuiltin" ? typeof(ArrayCodec<int>) : typeof(PatternArrayCodec<int>), provider.GetCodec<int[]>());
        Assert.IsType(registration == "ClosedBuiltin" ? typeof(ArrayCopier<int>) : typeof(PatternArrayCopier<int>), provider.GetDeepCopier<int[]>());
        var serializer = services.GetRequiredService<Serializer<int[]>>();
        var input = new[] { 37, 41 };

        Assert.Equal(input, serializer.Deserialize(serializer.SerializeToArray(input)));
        var copy = services.GetRequiredService<DeepCopier<int[]>>().Copy(input);
        Assert.Equal(input, copy);
        Assert.NotSame(input, copy);
        Assert.Equal(0, converter.ToSurrogateCalls);
        Assert.Equal(0, converter.FromSurrogateCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnnamedPatternsUseReverseRegistrationOrder(bool arrayLast)
    {
        var options = new TypeManifestOptions();
        var array = SerializationType.Array(SerializationType.Parameter(0));
        var parameter = SerializationType.Parameter(0);
        options.AddSerializationContract(arrayLast ? typeof(ParameterCopier<>) : typeof(PatternArrayCopier<>), typeof(IDeepCopier<>),
            arrayLast ? parameter : array);
        options.AddSerializationContract(arrayLast ? typeof(PatternArrayCopier<>) : typeof(ParameterCopier<>), typeof(IDeepCopier<>),
            arrayLast ? array : parameter);
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        var copier = provider.GetDeepCopier<int[]>();

        Assert.IsType(arrayLast ? typeof(PatternArrayCopier<int>) : typeof(ParameterCopier<int[]>), copier);
        var input = new[] { 5, 7 };
        var result = copier.DeepCopy(input, null!);
        Assert.Equal(input, result);
        if (arrayLast)
        {
            Assert.NotSame(input, result);
        }
        else
        {
            Assert.Same(input, result);
        }
    }

    [Theory]
    [InlineData("Codec", false)]
    [InlineData("Copier", false)]
    [InlineData("Converter", false)]
    [InlineData("Codec", true)]
    [InlineData("Copier", true)]
    [InlineData("Converter", true)]
    public void InterleavedContractsPreserveGlobalOrderAndCollectionMembership(string role, bool arrayTarget)
    {
        var options = CreateInterleavedOptions(role, arrayTarget);
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        var first = role == "Converter" ? typeof(InterleavedConverter<>) : typeof(InterleavedCodecCopier<>);
        var expected = role == "Converter" ? typeof(InterleavedConverter<int>) : typeof(InterleavedCodecCopier<int>);
        Assert.IsType(expected, ResolveInterleaved(provider, role, arrayTarget, typeof(GenericSurrogate<int>)));

        var types = role switch
        {
            "Codec" => options.SerializerTypes,
            "Copier" => options.CopierTypes,
            _ => options.ConverterTypes
        };
        Assert.True(types.Remove(first));
        provider = new CodecProvider(services, Options.Create(options));
        Assert.IsType(role == "Converter" ? typeof(AlternativeInterleavedConverter<string, int>) : typeof(AlternativeInterleavedCodecCopier<string, int>),
            ResolveInterleaved(provider, role, arrayTarget, typeof(GenericSurrogate<(string, int)>)));

        Assert.True(types.Add(first));
        provider = new CodecProvider(services, Options.Create(options));
        Assert.IsType(expected, ResolveInterleaved(provider, role, arrayTarget, typeof(GenericSurrogate<int>)));
    }

    [Fact]
    public void DuplicateIdenticalContractsRetainTheirOriginalPriority()
    {
        var options = CreateInterleavedOptions("Copier", false, includeLast: false);
        var original = options.CopierContracts[typeof(InterleavedCodecCopier<>)][0];
        options.AddSerializationContract(typeof(InterleavedCodecCopier<>), typeof(IDeepCopier<>), original.TargetDescription!);
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType<AlternativeInterleavedCodecCopier<string, int>>(provider.GetDeepCopier<GenericTarget<string, int>>());
        Assert.Single(options.CopierContracts[typeof(InterleavedCodecCopier<>)]);
    }

    [Theory]
    [InlineData("Codec", false)]
    [InlineData("Copier", false)]
    [InlineData("Converter", false)]
    [InlineData("Codec", true)]
    [InlineData("Copier", true)]
    [InlineData("Converter", true)]
    public void IndependentlyBuiltEquivalentDescriptorsPreserveOriginalPriority(string role, bool arrayTarget)
    {
        var options = CreateInterleavedOptions(role, arrayTarget, includeLast: false);
        var contract = role == "Codec" ? typeof(IFieldCodec<>) : role == "Copier" ? typeof(IDeepCopier<>) : typeof(IConverter<,>);
        var first = role == "Converter" ? typeof(InterleavedConverter<>) : typeof(InterleavedCodecCopier<>);
        var registrations = role == "Codec" ? options.SerializerContracts : role == "Copier" ? options.CopierContracts : options.ConverterContracts;
        var original = Assert.Single(registrations[first]);
        var copiedTarget = SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Parameter(0), SerializationType.Create(typeof(int)));
        if (arrayTarget)
        {
            copiedTarget = SerializationType.Array(copiedTarget);
        }
        var copiedSurrogate = role == "Converter" ? SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Parameter(0)) : null;
        Assert.NotSame(original.TargetDescription, copiedTarget);
        if (role == "Converter")
        {
            Assert.NotSame(original.SurrogateDescription, copiedSurrogate);
        }
        options.AddSerializationContract(first, contract, copiedTarget, copiedSurrogate);
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType(role == "Converter" ? typeof(AlternativeInterleavedConverter<string, int>) : typeof(AlternativeInterleavedCodecCopier<string, int>),
            ResolveInterleaved(provider, role, arrayTarget, typeof(GenericSurrogate<(string, int)>)));
        Assert.Single(registrations[first]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitPartialShapesPreserveLegacyTargetsAndOtherRoles(bool rawCollection)
    {
        var options = new TypeManifestOptions();
        if (rawCollection)
        {
            options.Serializers.Add(typeof(LegacyMultiShapeCodec<>));
        }
        else
        {
            options.AddSerializer(typeof(LegacyMultiShapeCodec<>));
        }
        options.AddSerializationContract(typeof(LegacyMultiShapeCodec<>), typeof(IBaseCodec<>),
            SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Parameter(0), SerializationType.Create(typeof(int))));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType<LegacyMultiShapeCodec<Guid>>(provider.GetBaseCodec<GenericTarget<Guid, int>>());
        Assert.IsType<LegacyMultiShapeCodec<Guid>>(provider.GetBaseCodec<GenericTarget<Guid, string>>());
        Assert.IsType<LegacyMultiShapeCodec<Guid>>(provider.GetBaseCodec<GenericTarget<FixedArgument<string>, Guid>>());
        Assert.IsType<LegacyMultiShapeCodec<Guid>>(provider.GetBaseCodec<GenericTarget<Guid, string>[]>());
        Assert.IsType<LegacyMultiShapeCodec<Guid>>(provider.GetBaseCodec<GenericTarget<Guid, FixedArgument<int>>>());
        Assert.IsType<LegacyMultiShapeCodec<Guid>>(provider.GetValueSerializer<GenericSurrogate<Guid>>());
        Assert.Throws<KeyNotFoundException>(() => provider.GetBaseCodec<GenericTarget<Guid, decimal>>());
        options.AddSerializationContract(typeof(ReplacementStringShapeCodec<>), typeof(IBaseCodec<>),
            SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Parameter(0), SerializationType.Create(typeof(string))));
        provider = new CodecProvider(services, Options.Create(options));
        Assert.IsType<ReplacementStringShapeCodec<Guid>>(provider.GetBaseCodec<GenericTarget<Guid, string>>());
        Assert.IsType<LegacyMultiShapeCodec<Guid>>(provider.GetBaseCodec<GenericTarget<Guid, int>>());
    }

    [Fact]
    public void LegacyConvertersUseTheSelectedPartialTargetBindings()
    {
        var options = new TypeManifestOptions();
        options.AddConverter(typeof(LegacyMultiShapeConverter<>));
        options.AddSerializationContract(typeof(LegacyMultiShapeConverter<>), typeof(IConverter<,>),
            SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Parameter(0), SerializationType.Create(typeof(int))),
            SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Parameter(0)));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        foreach (var target in new[] { typeof(GenericTarget<Guid, int>), typeof(GenericTarget<Guid, string>), typeof(GenericTarget<FixedArgument<string>, Guid>) })
        {
            object?[] arguments = [target, typeof(GenericTarget<,>), null, null];
            Assert.Equal(true, typeof(CodecProvider).GetMethod("TryGetSurrogateCodec", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, arguments));
            Assert.Equal(typeof(GenericSurrogate<Guid>), Assert.IsAssignableFrom<Type>(arguments[2]).GetGenericArguments()[1]);
            Assert.IsType<LegacyMultiShapeConverter<Guid>>(Assert.Single(Assert.IsType<object[]>(arguments[3])));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterleavedExplicitContractsPreserveRawLegacyCollectionPriority(bool legacyLast)
    {
        var options = new TypeManifestOptions();
        var copiers = options.Copiers;
        if (!legacyLast)
        {
            copiers.Add(typeof(LegacyOrderedCopier<,>));
        }
        AddInterleavedContracts(options, "Copier", false);
        if (legacyLast)
        {
            copiers.Add(typeof(LegacyOrderedCopier<,>));
        }
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType(legacyLast ? typeof(LegacyOrderedCopier<string, int>) : typeof(InterleavedCodecCopier<int>),
            provider.GetDeepCopier<GenericTarget<string, int>>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterleavedConverterRecordsPreserveLegacySelection(bool legacyLast)
    {
        var options = new TypeManifestOptions();
        var converters = options.Converters;
        if (!legacyLast)
        {
            converters.Add(typeof(LegacyOrderedConverter<,>));
        }
        AddInterleavedContracts(options, "Converter", false);
        if (legacyLast)
        {
            converters.Add(typeof(LegacyOrderedConverter<,>));
        }
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType(legacyLast ? typeof(LegacyOrderedConverter<string, int>) : typeof(InterleavedConverter<int>),
            ResolveInterleaved(provider, "Converter", false, legacyLast ? typeof(GenericSurrogate<string>) : typeof(GenericSurrogate<int>)));
    }

    [Theory]
    [InlineData("Codec", false, false)]
    [InlineData("Codec", false, true)]
    [InlineData("Codec", true, false)]
    [InlineData("Codec", true, true)]
    [InlineData("Copier", false, false)]
    [InlineData("Copier", false, true)]
    [InlineData("Copier", true, false)]
    [InlineData("Copier", true, true)]
    [InlineData("Converter", false, false)]
    [InlineData("Converter", false, true)]
    [InlineData("Converter", true, false)]
    [InlineData("Converter", true, true)]
    public void NonMatchingExplicitContractsPreserveTheirImplementationsLegacyPriority(string role, bool arrayTarget, bool legacyLast)
    {
        var options = new TypeManifestOptions();
        var first = role == "Converter" ? typeof(InterleavedConverter<>) : typeof(InterleavedCodecCopier<>);
        var second = role == "Converter" ? typeof(AlternativeInterleavedConverter<,>) : typeof(AlternativeInterleavedCodecCopier<,>);
        var contract = role == "Codec" ? typeof(IFieldCodec<>) : role == "Copier" ? typeof(IDeepCopier<>) : typeof(IConverter<,>);
        var types = role == "Codec" ? options.Serializers : role == "Copier" ? options.Copiers : options.Converters;
        if (!legacyLast)
        {
            types.Add(first);
        }
        var matching = SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Parameter(0), SerializationType.Parameter(1));
        options.AddSerializationContract(second, contract, arrayTarget ? SerializationType.Array(matching) : matching,
            role == "Converter" ? SerializationType.Create(typeof(GenericSurrogate<>),
                SerializationType.Create(typeof(ValueTuple<,>), SerializationType.Parameter(0), SerializationType.Parameter(1))) : null);
        if (legacyLast)
        {
            types.Add(first);
        }
        var other = SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Create(typeof(string)), SerializationType.Parameter(0));
        options.AddSerializationContract(first, contract, arrayTarget ? SerializationType.Array(other) : other,
            role == "Converter" ? SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Parameter(0)) : null);
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        var target = arrayTarget ? typeof(GenericTarget<Guid, int>[]) : typeof(GenericTarget<Guid, int>);
        var expected = role == "Converter"
            ? legacyLast ? typeof(InterleavedConverter<Guid>) : typeof(AlternativeInterleavedConverter<Guid, int>)
            : legacyLast ? typeof(InterleavedCodecCopier<Guid>) : typeof(AlternativeInterleavedCodecCopier<Guid, int>);

        Assert.Equal(expected, SelectImplementation(provider, contract, target));
        if (role == "Converter")
        {
            object?[] arguments = [target, arrayTarget ? target : typeof(GenericTarget<,>), null, null];
            Assert.Equal(true, typeof(CodecProvider).GetMethod("TryGetSurrogateCodec", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, arguments));
            Assert.Equal(legacyLast ? typeof(GenericSurrogate<Guid>) : typeof(GenericSurrogate<(Guid, int)>),
                Assert.IsAssignableFrom<Type>(arguments[2]).GetGenericArguments()[1]);
            Assert.IsType(expected, Assert.Single(Assert.IsType<object[]>(arguments[3])));
        }
        else if (role == "Codec")
        {
            Assert.IsType(expected, provider.GetCodec(target));
        }
        else
        {
            Assert.IsType(expected, provider.GetDeepCopier(target));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MiddleLegacyEntriesRetainTheirPositionAmongInterleavedExplicitContracts(bool converter)
    {
        var options = new TypeManifestOptions();
        var types = converter ? options.Converters : options.Copiers;
        var contract = converter ? typeof(IConverter<,>) : typeof(IDeepCopier<>);
        var first = converter ? typeof(InterleavedConverter<>) : typeof(InterleavedCodecCopier<>);
        var second = converter ? typeof(AlternativeInterleavedConverter<,>) : typeof(AlternativeInterleavedCodecCopier<,>);
        options.AddSerializationContract(first, contract,
            SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Parameter(0), SerializationType.Create(typeof(int))),
            converter ? SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Parameter(0)) : null);
        types.Add(converter ? typeof(LegacyOrderedConverter<,>) : typeof(LegacyOrderedCopier<,>));
        options.AddSerializationContract(second, contract,
            SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Parameter(0), SerializationType.Parameter(1)),
            converter ? SerializationType.Create(typeof(GenericSurrogate<>),
                SerializationType.Create(typeof(ValueTuple<,>), SerializationType.Parameter(0), SerializationType.Parameter(1))) : null);
        options.AddSerializationContract(first, contract,
            SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Create(typeof(string)), SerializationType.Parameter(0)),
            converter ? SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Parameter(0)) : null);
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType(converter ? typeof(InterleavedConverter<int>) : typeof(InterleavedCodecCopier<int>),
            ResolveInterleaved(provider, converter ? "Converter" : "Copier", false, typeof(GenericSurrogate<int>)));
        types.Remove(first);
        provider = new CodecProvider(services, Options.Create(options));
        Assert.IsType(converter ? typeof(AlternativeInterleavedConverter<string, int>) : typeof(AlternativeInterleavedCodecCopier<string, int>),
            ResolveInterleaved(provider, converter ? "Converter" : "Copier", false, typeof(GenericSurrogate<(string, int)>)));
    }

    [Fact]
    public void ClosedServiceFactoriesRetainExactTargetAndActivationPriority()
    {
        var options = new TypeManifestOptions();
        options.AddSerializer(typeof(InterleavedCodecCopier<string>), typeof(GenericTarget<string, int>));
        options.AddCopier(typeof(InterleavedCodecCopier<string>), typeof(GenericTarget<string, int>));
        AddInterleavedContracts(options, "Codec", false);
        AddInterleavedContracts(options, "Copier", false);
        var probe = new ClosedFactoryProbe();
        using var services = new ServiceCollection()
            .AddSingleton(probe)
            .AddSingleton(static provider => provider.GetRequiredService<ClosedFactoryProbe>().Create())
            .BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.Same(probe.Instance, provider.GetCodec<GenericTarget<string, int>>());
        Assert.Same(probe.Instance, provider.GetDeepCopier<GenericTarget<string, int>>());
        Assert.Equal(1, probe.Calls);
    }

    private static TypeManifestOptions CreateInterleavedOptions(string role, bool arrayTarget, bool includeLast = true)
    {
        var options = new TypeManifestOptions();
        AddInterleavedContracts(options, role, arrayTarget, includeLast);
        return options;
    }

    private static void AddInterleavedContracts(TypeManifestOptions options, string role, bool arrayTarget, bool includeLast = true)
    {
        var contract = role == "Codec" ? typeof(IFieldCodec<>) : role == "Copier" ? typeof(IDeepCopier<>) : typeof(IConverter<,>);
        var first = role == "Converter" ? typeof(InterleavedConverter<>) : typeof(InterleavedCodecCopier<>);
        var second = role == "Converter" ? typeof(AlternativeInterleavedConverter<,>) : typeof(AlternativeInterleavedCodecCopier<,>);
        var firstTarget = SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Parameter(0), SerializationType.Create(typeof(int)));
        var secondTarget = SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Parameter(0), SerializationType.Parameter(1));
        var lastTarget = SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Create(typeof(string)), SerializationType.Parameter(0));
        if (arrayTarget)
        {
            firstTarget = SerializationType.Array(firstTarget);
            secondTarget = SerializationType.Array(secondTarget);
            lastTarget = SerializationType.Array(lastTarget);
        }
        var firstSurrogate = role == "Converter" ? SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Parameter(0)) : null;
        var secondSurrogate = role == "Converter" ? SerializationType.Create(typeof(GenericSurrogate<>),
            SerializationType.Create(typeof(ValueTuple<,>), SerializationType.Parameter(0), SerializationType.Parameter(1))) : null;
        options.AddSerializationContract(first, contract, firstTarget, firstSurrogate);
        options.AddSerializationContract(second, contract, secondTarget, secondSurrogate);
        if (includeLast)
        {
            options.AddSerializationContract(first, contract, lastTarget, firstSurrogate);
        }
    }

    private static object ResolveInterleaved(CodecProvider provider, string role, bool arrayTarget, Type surrogate)
    {
        if (role == "Codec")
        {
            if (arrayTarget) return provider.GetCodec<GenericTarget<string, int>[]>();
            return provider.GetCodec<GenericTarget<string, int>>();
        }
        if (role == "Copier")
        {
            if (arrayTarget)
            {
                var copier = provider.GetDeepCopier<GenericTarget<string, int>[]>();
                var input = new[] { new GenericTarget<string, int>() };
                var result = copier.DeepCopy(input, null!);
                Assert.NotSame(input, result);
                Assert.Equal(input, result);
                return copier;
            }
            var scalar = provider.GetDeepCopier<GenericTarget<string, int>>();
            var value = new GenericTarget<string, int>();
            Assert.NotSame(value, scalar.DeepCopy(value, null!));
            return scalar;
        }
        var target = arrayTarget ? typeof(GenericTarget<string, int>[]) : typeof(GenericTarget<string, int>);
        object?[] arguments = [target, arrayTarget ? target : typeof(GenericTarget<,>), null, null];
        Assert.Equal(true, typeof(CodecProvider).GetMethod("TryGetSurrogateCodec", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, arguments));
        Assert.Equal(surrogate, Assert.IsAssignableFrom<Type>(arguments[2]).GetGenericArguments()[1]);
        return Assert.Single(Assert.IsType<object[]>(arguments[3]));
    }

    [Fact]
    public void StructConstrainedCopiersSelectTheLatestApplicableRegistration()
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(ParameterCopier<>), typeof(IDeepCopier<>), SerializationType.Parameter(0));
        options.AddSerializationContract(typeof(StructConstrainedCopier<>), typeof(IDeepCopier<>), SerializationType.Parameter(0));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType<ParameterCopier<string>>(provider.GetDeepCopier<string>());
        Assert.IsType<ParameterCopier<int?>>(provider.GetDeepCopier<int?>());
        Assert.IsType<StructConstrainedCopier<int>>(provider.GetDeepCopier<int>());
        Assert.IsType<StructConstrainedCopier<Guid>>(provider.GetDeepCopier<Guid>());
        var input = "preserved";
        Assert.Same(input, provider.GetDeepCopier<string>().DeepCopy(input, null!));
    }

    [Theory]
    [InlineData(typeof(ReferenceConstrainedCopier<>), typeof(string), true)]
    [InlineData(typeof(ReferenceConstrainedCopier<>), typeof(int), false)]
    [InlineData(typeof(ConstructorConstrainedCopier<>), typeof(PublicConstructorTarget), true)]
    [InlineData(typeof(ConstructorConstrainedCopier<>), typeof(PrivateConstructorTarget), false)]
    // MakeGenericType permits an abstract new()-constrained argument on .NET 8 and rejects it on .NET 10.
#if NET10_0_OR_GREATER
    [InlineData(typeof(ConstructorConstrainedCopier<>), typeof(AbstractConstructorTarget), false)]
#else
    [InlineData(typeof(ConstructorConstrainedCopier<>), typeof(AbstractConstructorTarget), true)]
#endif
    [InlineData(typeof(ConstructorConstrainedCopier<>), typeof(int), true)]
    [InlineData(typeof(ComparableConstrainedCopier<>), typeof(string), true)]
    [InlineData(typeof(ComparableConstrainedCopier<>), typeof(int), true)]
    [InlineData(typeof(ComparableConstrainedCopier<>), typeof(FirstTarget), false)]
    public void GenericConstraintRejectionsContinueToEarlierMatchingCopiers(Type implementation, Type target, bool accepted)
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(ParameterCopier<>), typeof(IDeepCopier<>), SerializationType.Parameter(0));
        options.AddSerializationContract(implementation, typeof(IDeepCopier<>), SerializationType.Parameter(0));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.Equal((accepted ? implementation : typeof(ParameterCopier<>)).MakeGenericType(target), provider.GetDeepCopier(target).GetType());
    }

    [Fact]
    public void ConstraintRejectionsDoNotFaultPendingClosedFactoryConstruction()
    {
        var attempts = 0;
        var registrations = new ServiceCollection().AddSerializer();
        registrations.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializationContract(typeof(ParameterCopier<>), typeof(IDeepCopier<>), SerializationType.Parameter(0));
            options.AddSerializationContract(typeof(StructConstrainedCopier<>), typeof(IDeepCopier<>), SerializationType.Parameter(0));
            options.AddSerializerService<ConstraintSelection>(provider =>
            {
                attempts++;
                return new ConstraintSelection(provider.GetDeepCopier<string>(), provider.GetDeepCopier<int>());
            });
        });
        using var services = registrations.BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var root = GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<ConstraintSelection>(null!, provider);
        Assert.IsType<ParameterCopier<string>>(root.ReferenceCopier);
        Assert.IsType<StructConstrainedCopier<int>>(root.ValueCopier);
        Assert.Same(root.ReferenceCopier, provider.GetDeepCopier<string>());
        Assert.Same(root.ValueCopier, provider.GetDeepCopier<int>());
        Assert.Same(root, GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<ConstraintSelection>(null!, provider));
        Assert.Equal(1, attempts);
        var input = "preserved";
        Assert.Same(input, root.ReferenceCopier.DeepCopy(input, null!));
        Assert.Equal(17, root.ValueCopier.DeepCopy(17, null!));
    }

    private sealed class ConstraintSelection(IDeepCopier<string> referenceCopier, IDeepCopier<int> valueCopier)
    {
        public IDeepCopier<string> ReferenceCopier { get; } = referenceCopier;
        public IDeepCopier<int> ValueCopier { get; } = valueCopier;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamedAndArrayConstraintsUseTheBoundImplementationParameterOrder(bool arrayTarget)
    {
        var options = new TypeManifestOptions();
        var fallback = SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Parameter(0), SerializationType.Parameter(1));
        var reordered = SerializationType.Create(typeof(GenericTarget<,>), SerializationType.Parameter(1), SerializationType.Parameter(0));
        options.AddSerializationContract(arrayTarget ? typeof(PairArrayFallbackCopier<,>) : typeof(PairFallbackCopier<,>), typeof(IDeepCopier<>),
            arrayTarget ? SerializationType.Array(fallback) : fallback);
        options.AddSerializationContract(arrayTarget ? typeof(DependentArrayConstrainedCopier<,>) : typeof(DependentConstrainedCopier<,>), typeof(IDeepCopier<>),
            arrayTarget ? SerializationType.Array(reordered) : reordered);
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        if (arrayTarget)
        {
            Assert.IsType<DependentArrayConstrainedCopier<ConstraintBase, ConstraintDerived>>(provider.GetDeepCopier<GenericTarget<ConstraintDerived, ConstraintBase>[]>());
            Assert.IsType<PairArrayFallbackCopier<ConstraintBase, ConstraintDerived>>(provider.GetDeepCopier<GenericTarget<ConstraintBase, ConstraintDerived>[]>());
        }
        else
        {
            Assert.IsType<DependentConstrainedCopier<ConstraintBase, ConstraintDerived>>(provider.GetDeepCopier<GenericTarget<ConstraintDerived, ConstraintBase>>());
            Assert.IsType<PairFallbackCopier<ConstraintBase, ConstraintDerived>>(provider.GetDeepCopier<GenericTarget<ConstraintBase, ConstraintDerived>>());
        }
    }

    [Fact]
    public void ConverterSurrogatesUseTheConstraintValidatedWinningPattern()
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(StructPatternConverter<>), typeof(IConverter<,>),
            SerializationType.Create(typeof(FixedArgument<>), SerializationType.Parameter(0)),
            SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Parameter(0)));
        options.AddSerializationContract(typeof(StructPatternConverter<>), typeof(IConverter<,>),
            SerializationType.Create(typeof(FixedArgument<>), SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Parameter(0))),
            SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Parameter(0))));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        object?[] arguments = [typeof(FixedArgument<GenericSurrogate<string>>), typeof(FixedArgument<>), null, null];

        Assert.Equal(true, typeof(CodecProvider).GetMethod("TryGetSurrogateCodec", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, arguments));
        Assert.Equal(typeof(GenericSurrogate<GenericSurrogate<string>>), Assert.IsAssignableFrom<Type>(arguments[2]).GetGenericArguments()[1]);
        Assert.IsType<StructPatternConverter<GenericSurrogate<string>>>(Assert.Single(Assert.IsType<object[]>(arguments[3])));
    }

    [Fact]
    public void InvalidConstraintCandidatesLeaveOrdinaryUnsupportedLookupBehavior()
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(StructConstrainedCopier<>), typeof(IDeepCopier<>), SerializationType.Parameter(0));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.Null(provider.TryGetDeepCopier<FirstTarget>());
        Assert.Throws<CodecNotFoundException>(() => provider.GetDeepCopier<FirstTarget>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CandidateValidationPropagatesUnavailableCodeOrMetadataInsteadOfFallingBack(bool missingMetadata)
    {
        Exception failure = missingMetadata ? new TypeLoadException("Metadata unavailable.") : new NotSupportedException("Native instantiation unavailable.");
        var implementation = new UnavailableGenericImplementation(typeof(ParameterCopier<>), failure);
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(ParameterCopier<>), typeof(IDeepCopier<>), SerializationType.Parameter(0));
        options.AddSerializationContract(implementation, typeof(IDeepCopier<>), SerializationType.Parameter(0));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        var exception = Assert.Throws(failure.GetType(), () => provider.GetDeepCopier<string>());
        Assert.Same(failure, exception);
        Assert.Equal(1, implementation.ClosureAttempts);
    }

    [Fact]
    public void CandidateValidationPreservesMalformedRegistrationErrors()
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(ParameterCopier<>), typeof(IDeepCopier<>), SerializationType.Parameter(0));
        options.AddSerializationContract(typeof(PairFallbackCopier<,>), typeof(IDeepCopier<>), SerializationType.Parameter(0));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetDeepCopier<string>());
        Assert.Contains("does not bind generic parameter 1", exception.Message);
        options = new TypeManifestOptions();
        options.AddCopier(typeof(PairFallbackCopier<,>), typeof(FixedArgument<>));
        provider = new CodecProvider(services, Options.Create(options));
        Assert.Throws<ArgumentException>(() => provider.GetDeepCopier<FixedArgument<string>>());
    }

    [Fact]
    public void ConstraintRejectionPreservesExactNamedAndPatternLookupTiers()
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(ParameterCopier<>), typeof(IDeepCopier<>), SerializationType.Parameter(0));
        options.AddSerializationContract(typeof(StructTargetConstrainedCopier<>), typeof(IDeepCopier<>),
            SerializationType.Create(typeof(FixedArgument<>), SerializationType.Parameter(0)));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        Assert.IsType<ParameterCopier<FixedArgument<string>>>(provider.GetDeepCopier<FixedArgument<string>>());
        Assert.IsType<StructTargetConstrainedCopier<int>>(provider.GetDeepCopier<FixedArgument<int>>());
        options.AddCopier(typeof(ShallowCopier<FixedArgument<int>>), typeof(FixedArgument<int>));
        provider = new CodecProvider(services, Options.Create(options));
        Assert.IsType<ShallowCopier<FixedArgument<int>>>(provider.GetDeepCopier<FixedArgument<int>>());
    }

    [Fact]
    public void CanonicalSelectorReturnsTheConstraintValidatedClosedImplementation()
    {
        var options = new TypeManifestOptions();
        options.AddSerializationContract(typeof(ParameterCopier<>), typeof(IDeepCopier<>), SerializationType.Parameter(0));
        options.AddSerializationContract(typeof(StructConstrainedCopier<>), typeof(IDeepCopier<>), SerializationType.Parameter(0));
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));
        Assert.Equal(typeof(ParameterCopier<string>), SelectImplementation(provider, typeof(IDeepCopier<>), typeof(string)));
        Assert.Equal(typeof(StructConstrainedCopier<int>), SelectImplementation(provider, typeof(IDeepCopier<>), typeof(int)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WinningImplementationIsClosedOnceDuringResolution(bool converter)
    {
        var implementation = new CountingGenericImplementation(converter ? typeof(ParameterConverter<>) : typeof(ParameterCopier<>));
        var options = new TypeManifestOptions();
        options.AddSerializationContract(implementation, converter ? typeof(IConverter<,>) : typeof(IDeepCopier<>),
            SerializationType.Parameter(0),
            converter ? SerializationType.Create(typeof(GenericSurrogate<>), SerializationType.Parameter(0)) : null);
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = new CodecProvider(services, Options.Create(options));

        if (converter)
        {
            object?[] arguments = [typeof(FirstTarget), typeof(FirstTarget), null, null];
            Assert.Equal(true, typeof(CodecProvider).GetMethod("TryGetSurrogateCodec", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, arguments));
            Assert.Equal(typeof(SurrogateCodec<FirstTarget, GenericSurrogate<FirstTarget>, ParameterConverter<FirstTarget>>), arguments[2]);
            Assert.IsType<ParameterConverter<FirstTarget>>(Assert.Single(Assert.IsType<object[]>(arguments[3])));
        }
        else
        {
            var copier = Assert.IsType<ParameterCopier<FirstTarget>>(provider.GetDeepCopier<FirstTarget>());
            var input = new FirstTarget();
            Assert.Same(input, copier.DeepCopy(input, null!));
        }

        Assert.Equal(1, implementation.ClosureAttempts);
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

    private static Type SelectImplementation(CodecProvider provider, Type contract, Type target)
    {
        var searchType = target.IsConstructedGenericType ? target.GetGenericTypeDefinition() : target;
        object?[] arguments = [contract, target, searchType, null];
        Assert.Equal(true, typeof(CodecProvider).GetMethod("TrySelectImplementation", BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(Type), typeof(Type), typeof(Type), typeof(Type).MakeByRefType()])!.Invoke(provider, arguments));
        return Assert.IsAssignableFrom<Type>(arguments[3]);
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
    private sealed class UnavailableGenericImplementation(Type type, Exception failure) : TypeDelegator(type)
    {
        public int ClosureAttempts { get; private set; }
        public override bool IsGenericTypeDefinition => true;
        public override Type[] GetGenericArguments() => typeImpl!.GetGenericArguments();
        public override Type MakeGenericType(params Type[] arguments)
        {
            ClosureAttempts++;
            throw failure;
        }
    }

    private sealed class CountingGenericImplementation(Type type) : TypeDelegator(type)
    {
        public int ClosureAttempts { get; private set; }
        public override bool IsGenericTypeDefinition => true;
        public override Type[] GetGenericArguments() => typeImpl!.GetGenericArguments();
        public override Type MakeGenericType(params Type[] arguments)
        {
            ClosureAttempts++;
            return typeImpl!.MakeGenericType(arguments);
        }
    }

    private sealed class FirstTarget;
    private sealed class SecondTarget;
    private struct ValueTarget;
    private struct Surrogate;
    private sealed class ReplacementCodec;
    private sealed class RejectUnregisteredTypes : ITypeFilter
    {
        public bool? IsTypeAllowed(Type type) => false;
    }
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
    public sealed class MixedRegistrationActivator<T> : IActivator<FixedArgument<T>>, IActivator<FixedArgument<GenericSurrogate<T>>>
    {
        FixedArgument<T> IActivator<FixedArgument<T>>.Create() => new();
        FixedArgument<GenericSurrogate<T>> IActivator<FixedArgument<GenericSurrogate<T>>>.Create() => new();
    }
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
    public sealed class PatternArrayCodec<T>(IFieldCodec<T> elementCodec) : IFieldCodec<T[]>
    {
        private readonly ArrayCodec<T> _codec = new(elementCodec);
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, [AllowNull] Type expected, [AllowNull] T[] value)
            where TBufferWriter : IBufferWriter<byte>
            => _codec.WriteField(ref writer, id, expected, value);
        [return: MaybeNull]
        public T[] ReadValue<TInput>(ref Reader<TInput> reader, Field field) => _codec.ReadValue(ref reader, field);
    }
    public sealed class PatternArrayCopier<T> : IDeepCopier<T[]>
    {
        [return: NotNullIfNotNull(nameof(input))]
        public T[]? DeepCopy(T[]? input, CopyContext context) => input is null ? null : (T[])input.Clone();
    }
    public sealed class PatternMatrixCopier<T> : IDeepCopier<T[,]>
    {
        [return: NotNullIfNotNull(nameof(input))]
        public T[,]? DeepCopy(T[,]? input, CopyContext context) => input is null ? null : (T[,])input.Clone();
    }
    public sealed class ParameterCopier<T> : ShallowCopier<T>;
    public sealed class StructConstrainedCopier<T> : ShallowCopier<T> where T : struct;
    public sealed class StructTargetConstrainedCopier<T> : ShallowCopier<FixedArgument<T>> where T : struct;
    public sealed class ReferenceConstrainedCopier<T> : ShallowCopier<T> where T : class;
    public sealed class ConstructorConstrainedCopier<T> : ShallowCopier<T> where T : new();
    public sealed class ComparableConstrainedCopier<T> : ShallowCopier<T> where T : IComparable<T>;
    public sealed class PairFallbackCopier<TFirst, TSecond> : ShallowCopier<GenericTarget<TFirst, TSecond>>;
    public sealed class PairArrayFallbackCopier<TFirst, TSecond> : ShallowCopier<GenericTarget<TFirst, TSecond>[]>;
    public sealed class DependentConstrainedCopier<TBase, TDerived> : ShallowCopier<GenericTarget<TDerived, TBase>> where TDerived : TBase;
    public sealed class DependentArrayConstrainedCopier<TBase, TDerived> : ShallowCopier<GenericTarget<TDerived, TBase>[]> where TDerived : TBase;
    public class ConstraintBase;
    public sealed class ConstraintDerived : ConstraintBase;
    public sealed class PublicConstructorTarget;
    public sealed class PrivateConstructorTarget
    {
        private PrivateConstructorTarget() { }
    }
    public abstract class AbstractConstructorTarget
    {
        public AbstractConstructorTarget() { }
    }
    public sealed class StructPatternConverter<T> :
        IConverter<FixedArgument<T>, GenericSurrogate<T>>,
        IConverter<FixedArgument<GenericSurrogate<T>>, GenericSurrogate<GenericSurrogate<T>>> where T : struct
    {
        public FixedArgument<T> ConvertFromSurrogate(in GenericSurrogate<T> surrogate) => new();
        public FixedArgument<GenericSurrogate<T>> ConvertFromSurrogate(in GenericSurrogate<GenericSurrogate<T>> surrogate) => new();
        public GenericSurrogate<T> ConvertToSurrogate(in FixedArgument<T> value) => default;
        public GenericSurrogate<GenericSurrogate<T>> ConvertToSurrogate(in FixedArgument<GenericSurrogate<T>> value) => default;
    }
    public sealed class PatternNestedArrayCopier<T> : ShallowCopier<GenericTarget<T, int>[]>;
    public sealed class ParameterConverter<T> : IConverter<T, GenericSurrogate<T>>
    {
        public T ConvertFromSurrogate(in GenericSurrogate<T> surrogate) => throw new NotSupportedException();
        public GenericSurrogate<T> ConvertToSurrogate(in T value) => default;
    }
    public sealed class PatternArrayConverter<T> : IConverter<T[], GenericSurrogate<T>>
    {
        public T[] ConvertFromSurrogate(in GenericSurrogate<T> surrogate) => [];
        public GenericSurrogate<T> ConvertToSurrogate(in T[] value) => default;
    }
    [GenerateSerializer]
    public struct ArraySurrogate<T>
    {
        [Id(0)]
        public List<T> Values { get; set; }
    }
    public sealed class ArrayConverter<T> : IConverter<T[], ArraySurrogate<T>>
    {
        public int ToSurrogateCalls { get; private set; }
        public int FromSurrogateCalls { get; private set; }
        public T[] ConvertFromSurrogate(in ArraySurrogate<T> surrogate)
        {
            FromSurrogateCalls++;
            return surrogate.Values.ToArray();
        }
        public ArraySurrogate<T> ConvertToSurrogate(in T[] value)
        {
            ToSurrogateCalls++;
            return new() { Values = [.. value] };
        }
    }
    public sealed class InterleavedCodecCopier<T> :
        IFieldCodec<GenericTarget<T, int>>, IFieldCodec<GenericTarget<string, T>>,
        IFieldCodec<GenericTarget<T, int>[]>, IFieldCodec<GenericTarget<string, T>[]>,
        IDeepCopier<GenericTarget<T, int>>, IDeepCopier<GenericTarget<string, T>>,
        IDeepCopier<GenericTarget<T, int>[]>, IDeepCopier<GenericTarget<string, T>[]>
    {
        void IFieldCodec.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, [AllowNull] Type expected, object? value) => throw new NotSupportedException();
        object? IFieldCodec.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
        object? IDeepCopier.DeepCopy(object? input, CopyContext context) => throw new NotSupportedException();
        void IFieldCodec<GenericTarget<T, int>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, [AllowNull] Type expected, [AllowNull] GenericTarget<T, int> value) => throw new NotSupportedException();
        void IFieldCodec<GenericTarget<string, T>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, [AllowNull] Type expected, [AllowNull] GenericTarget<string, T> value) => throw new NotSupportedException();
        void IFieldCodec<GenericTarget<T, int>[]>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, [AllowNull] Type expected, [AllowNull] GenericTarget<T, int>[] value) => throw new NotSupportedException();
        void IFieldCodec<GenericTarget<string, T>[]>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, [AllowNull] Type expected, [AllowNull] GenericTarget<string, T>[] value) => throw new NotSupportedException();
        GenericTarget<T, int> IFieldCodec<GenericTarget<T, int>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
        GenericTarget<string, T> IFieldCodec<GenericTarget<string, T>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
        GenericTarget<T, int>[] IFieldCodec<GenericTarget<T, int>[]>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
        GenericTarget<string, T>[] IFieldCodec<GenericTarget<string, T>[]>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
        [return: NotNullIfNotNull(nameof(input))]
        GenericTarget<T, int>? IDeepCopier<GenericTarget<T, int>>.DeepCopy(GenericTarget<T, int>? input, CopyContext context) => input is null ? null : new();
        [return: NotNullIfNotNull(nameof(input))]
        GenericTarget<string, T>? IDeepCopier<GenericTarget<string, T>>.DeepCopy(GenericTarget<string, T>? input, CopyContext context) => input is null ? null : new();
        [return: NotNullIfNotNull(nameof(input))]
        GenericTarget<T, int>[]? IDeepCopier<GenericTarget<T, int>[]>.DeepCopy(GenericTarget<T, int>[]? input, CopyContext context) => input is null ? null : (GenericTarget<T, int>[])input.Clone();
        [return: NotNullIfNotNull(nameof(input))]
        GenericTarget<string, T>[]? IDeepCopier<GenericTarget<string, T>[]>.DeepCopy(GenericTarget<string, T>[]? input, CopyContext context) => input is null ? null : (GenericTarget<string, T>[])input.Clone();
    }
    public sealed class AlternativeInterleavedCodecCopier<TFirst, TSecond> : IFieldCodec<GenericTarget<TFirst, TSecond>>,
        IFieldCodec<GenericTarget<TFirst, TSecond>[]>, IDeepCopier<GenericTarget<TFirst, TSecond>>, IDeepCopier<GenericTarget<TFirst, TSecond>[]>
    {
        void IFieldCodec.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, [AllowNull] Type expected, object? value) => throw new NotSupportedException();
        object? IFieldCodec.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
        object? IDeepCopier.DeepCopy(object? input, CopyContext context) => throw new NotSupportedException();
        void IFieldCodec<GenericTarget<TFirst, TSecond>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, [AllowNull] Type expected, [AllowNull] GenericTarget<TFirst, TSecond> value) => throw new NotSupportedException();
        void IFieldCodec<GenericTarget<TFirst, TSecond>[]>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, [AllowNull] Type expected, [AllowNull] GenericTarget<TFirst, TSecond>[] value) => throw new NotSupportedException();
        GenericTarget<TFirst, TSecond> IFieldCodec<GenericTarget<TFirst, TSecond>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
        GenericTarget<TFirst, TSecond>[] IFieldCodec<GenericTarget<TFirst, TSecond>[]>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
        [return: NotNullIfNotNull(nameof(input))]
        public GenericTarget<TFirst, TSecond>? DeepCopy(GenericTarget<TFirst, TSecond>? input, CopyContext context) => input is null ? null : new();
        [return: NotNullIfNotNull(nameof(input))]
        public GenericTarget<TFirst, TSecond>[]? DeepCopy(GenericTarget<TFirst, TSecond>[]? input, CopyContext context) => input is null ? null : (GenericTarget<TFirst, TSecond>[])input.Clone();
    }
    public sealed class InterleavedConverter<T> :
        IConverter<GenericTarget<T, int>, GenericSurrogate<T>>, IConverter<GenericTarget<string, T>, GenericSurrogate<T>>,
        IConverter<GenericTarget<T, int>[], GenericSurrogate<T>>, IConverter<GenericTarget<string, T>[], GenericSurrogate<T>>
    {
        GenericTarget<T, int> IConverter<GenericTarget<T, int>, GenericSurrogate<T>>.ConvertFromSurrogate(in GenericSurrogate<T> surrogate) => new();
        GenericTarget<string, T> IConverter<GenericTarget<string, T>, GenericSurrogate<T>>.ConvertFromSurrogate(in GenericSurrogate<T> surrogate) => new();
        GenericTarget<T, int>[] IConverter<GenericTarget<T, int>[], GenericSurrogate<T>>.ConvertFromSurrogate(in GenericSurrogate<T> surrogate) => [];
        GenericTarget<string, T>[] IConverter<GenericTarget<string, T>[], GenericSurrogate<T>>.ConvertFromSurrogate(in GenericSurrogate<T> surrogate) => [];
        public GenericSurrogate<T> ConvertToSurrogate(in GenericTarget<T, int> value) => default;
        public GenericSurrogate<T> ConvertToSurrogate(in GenericTarget<string, T> value) => default;
        public GenericSurrogate<T> ConvertToSurrogate(in GenericTarget<T, int>[] value) => default;
        public GenericSurrogate<T> ConvertToSurrogate(in GenericTarget<string, T>[] value) => default;
    }
    public sealed class AlternativeInterleavedConverter<TFirst, TSecond> :
        IConverter<GenericTarget<TFirst, TSecond>, GenericSurrogate<(TFirst, TSecond)>>,
        IConverter<GenericTarget<TFirst, TSecond>[], GenericSurrogate<(TFirst, TSecond)>>
    {
        GenericTarget<TFirst, TSecond> IConverter<GenericTarget<TFirst, TSecond>, GenericSurrogate<(TFirst, TSecond)>>.ConvertFromSurrogate(in GenericSurrogate<(TFirst, TSecond)> surrogate) => new();
        GenericTarget<TFirst, TSecond>[] IConverter<GenericTarget<TFirst, TSecond>[], GenericSurrogate<(TFirst, TSecond)>>.ConvertFromSurrogate(in GenericSurrogate<(TFirst, TSecond)> surrogate) => [];
        public GenericSurrogate<(TFirst, TSecond)> ConvertToSurrogate(in GenericTarget<TFirst, TSecond> value) => default;
        public GenericSurrogate<(TFirst, TSecond)> ConvertToSurrogate(in GenericTarget<TFirst, TSecond>[] value) => default;
    }
    public sealed class LegacyOrderedCopier<TFirst, TSecond> : ShallowCopier<GenericTarget<TFirst, TSecond>>;
    public sealed class LegacyMultiShapeCodec<T> :
        IBaseCodec<GenericTarget<T, int>>, IBaseCodec<GenericTarget<T, string>>, IBaseCodec<GenericTarget<FixedArgument<string>, T>>,
        IBaseCodec<GenericTarget<T, string>[]>, IBaseCodec<GenericTarget<T, FixedArgument<int>>>, IValueSerializer<GenericSurrogate<T>>
    {
        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, GenericTarget<T, int> value) where TBufferWriter : IBufferWriter<byte> { }
        public void Deserialize<TInput>(ref Reader<TInput> reader, GenericTarget<T, int> value) { }
        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, GenericTarget<T, string> value) where TBufferWriter : IBufferWriter<byte> { }
        public void Deserialize<TInput>(ref Reader<TInput> reader, GenericTarget<T, string> value) { }
        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, GenericTarget<FixedArgument<string>, T> value) where TBufferWriter : IBufferWriter<byte> { }
        public void Deserialize<TInput>(ref Reader<TInput> reader, GenericTarget<FixedArgument<string>, T> value) { }
        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, GenericTarget<T, string>[] value) where TBufferWriter : IBufferWriter<byte> { }
        public void Deserialize<TInput>(ref Reader<TInput> reader, GenericTarget<T, string>[] value) { }
        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, GenericTarget<T, FixedArgument<int>> value) where TBufferWriter : IBufferWriter<byte> { }
        public void Deserialize<TInput>(ref Reader<TInput> reader, GenericTarget<T, FixedArgument<int>> value) { }
        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, scoped ref GenericSurrogate<T> value) where TBufferWriter : IBufferWriter<byte> { }
        public void Deserialize<TInput>(ref Reader<TInput> reader, scoped ref GenericSurrogate<T> value) { }
    }
    public sealed class ReplacementStringShapeCodec<T> : IBaseCodec<GenericTarget<T, string>>
    {
        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, GenericTarget<T, string> value) where TBufferWriter : IBufferWriter<byte> { }
        public void Deserialize<TInput>(ref Reader<TInput> reader, GenericTarget<T, string> value) { }
    }
    public sealed class LegacyMultiShapeConverter<T> : IConverter<GenericTarget<T, int>, GenericSurrogate<T>>,
        IConverter<GenericTarget<T, string>, GenericSurrogate<T>>, IConverter<GenericTarget<FixedArgument<string>, T>, GenericSurrogate<T>>
    {
        public GenericTarget<T, int> ConvertFromSurrogate(in GenericSurrogate<T> surrogate) => new();
        GenericTarget<T, string> IConverter<GenericTarget<T, string>, GenericSurrogate<T>>.ConvertFromSurrogate(in GenericSurrogate<T> surrogate) => new();
        GenericTarget<FixedArgument<string>, T> IConverter<GenericTarget<FixedArgument<string>, T>, GenericSurrogate<T>>.ConvertFromSurrogate(in GenericSurrogate<T> surrogate) => new();
        public GenericSurrogate<T> ConvertToSurrogate(in GenericTarget<T, int> value) => default;
        public GenericSurrogate<T> ConvertToSurrogate(in GenericTarget<T, string> value) => default;
        public GenericSurrogate<T> ConvertToSurrogate(in GenericTarget<FixedArgument<string>, T> value) => default;
    }
    public sealed class LegacyOrderedConverter<TFirst, TSecond> : IConverter<GenericTarget<TFirst, TSecond>, GenericSurrogate<TFirst>>
    {
        public GenericTarget<TFirst, TSecond> ConvertFromSurrogate(in GenericSurrogate<TFirst> surrogate) => new();
        public GenericSurrogate<TFirst> ConvertToSurrogate(in GenericTarget<TFirst, TSecond> value) => default;
    }
    private sealed class ClosedFactoryProbe
    {
        public InterleavedCodecCopier<string> Instance { get; } = new();
        public int Calls { get; private set; }
        public InterleavedCodecCopier<string> Create()
        {
            Calls++;
            return Instance;
        }
    }
}
