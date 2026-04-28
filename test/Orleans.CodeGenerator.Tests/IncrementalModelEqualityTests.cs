using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator;
using Orleans.CodeGenerator.Model;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Tests structural equality behavior that Roslyn incremental caching depends on.
/// </summary>
public class IncrementalModelEqualityTests
{
    [Fact]
    public void StructuralEquality_DefaultArray_EqualsEmptyArray()
    {
        var defaultArray = default(ImmutableArray<string>);
        var emptyArray = ImmutableArray<string>.Empty;

        Assert.True(StructuralEquality.SequenceEqual(defaultArray, emptyArray));
        Assert.Equal(
            StructuralEquality.GetSequenceHashCode(defaultArray),
            StructuralEquality.GetSequenceHashCode(emptyArray));
    }

    [Fact]
    public void StructuralEquality_UsesElementValues()
    {
        var left = ImmutableArray.Create("alpha", "beta");
        var right = ImmutableArray.Create("alpha", "beta");
        var different = ImmutableArray.Create("alpha", "gamma");

        Assert.True(StructuralEquality.SequenceEqual(left, right));
        Assert.Equal(
            StructuralEquality.GetSequenceHashCode(left),
            StructuralEquality.GetSequenceHashCode(right));
        Assert.False(StructuralEquality.SequenceEqual(left, different));
    }

    [Fact]
    public void TypeRef_ToTypeSyntax_ProducesValidSyntax()
    {
        var typeRef = new TypeRef("global::System.Collections.Generic.List<string>");

        Assert.Equal("global::System.Collections.Generic.List<string>", typeRef.ToTypeSyntax().ToString());
        Assert.True(TypeRef.Empty.IsEmpty);
    }

    [Fact]
    public void CompoundAliasComponentModel_DistinguishesStringAndTypeComponents()
    {
        var stringComponent = new CompoundAliasComponentModel("part");
        var matchingStringComponent = new CompoundAliasComponentModel("part");
        var typeComponent = new CompoundAliasComponentModel(new TypeRef("global::Example.Part"));

        Assert.Equal(stringComponent, matchingStringComponent);
        Assert.NotEqual(stringComponent, typeComponent);
        Assert.True(stringComponent.IsString);
        Assert.True(typeComponent.IsType);
    }

    [Fact]
    public void SerializableTypeModel_DefaultArrays_AreEqualToEmptyArrays()
    {
        var defaultArrays = CreateSerializableTypeModel("MyType", "MyNamespace", members: default);
        var emptyArrays = CreateSerializableTypeModel("MyType", "MyNamespace", members: ImmutableArray<MemberModel>.Empty);

        Assert.Equal(defaultArrays, emptyArrays);
        Assert.Equal(defaultArrays.GetHashCode(), emptyArrays.GetHashCode());
    }

    [Fact]
    public void SerializableTypeModel_DifferentStructuralArrayValues_AreNotEqual()
    {
        var oneMember = CreateSerializableTypeModel(
            "MyType",
            "MyNamespace",
            members: ImmutableArray.Create(CreateMemberModel(0, "Value", "int")));
        var twoMembers = CreateSerializableTypeModel(
            "MyType",
            "MyNamespace",
            members: ImmutableArray.Create(
                CreateMemberModel(0, "Value", "int"),
                CreateMemberModel(1, "Other", "int")));

        Assert.NotEqual(oneMember, twoMembers);
    }

    [Fact]
    public void MetadataAggregateModel_CreateMetadataAggregate_MergesAndSortsDeterministically()
    {
        var aggregate = ModelExtractor.CreateMetadataAggregate(
            "TestAssembly",
            ImmutableArray.Create(
                CreateSerializableTypeModel("ZuluType", "MyNamespace"),
                CreateSerializableTypeModel("AlphaType", "MyNamespace")),
            ImmutableArray.Create(
                CreateProxyInterfaceModel("IZulu"),
                CreateProxyInterfaceModel("IAlpha")),
            CreateReferenceAssemblyModel(
                applicationParts: ImmutableArray.Create("PartZ", "PartA"),
                referencedSerializableTypes: ImmutableArray.Create(
                    CreateSerializableTypeModel("MiddleType", "MyNamespace"),
                    CreateSerializableTypeModel("AlphaType", "MyNamespace")),
                referencedProxyInterfaces: ImmutableArray.Create(
                    CreateProxyInterfaceModel("IMiddle"),
                    CreateProxyInterfaceModel("IAlpha")),
                registeredCodecs: ImmutableArray.Create(
                    new RegisteredCodecModel(new TypeRef("global::Codecs.ZuluCodec"), RegisteredCodecKind.Serializer),
                    new RegisteredCodecModel(new TypeRef("global::Codecs.AlphaCodec"), RegisteredCodecKind.Serializer)),
                interfaceImplementations: ImmutableArray.Create(
                    new InterfaceImplementationModel(new TypeRef("global::Impl.Zulu")),
                    new InterfaceImplementationModel(new TypeRef("global::Impl.Alpha")))));

        Assert.Equal(
            ["global::MyNamespace.AlphaType", "global::MyNamespace.MiddleType", "global::MyNamespace.ZuluType"],
            aggregate.SerializableTypes.Select(static type => type.TypeSyntax.SyntaxString).ToArray());
        Assert.Equal(
            ["global::MyNamespace.IAlpha", "global::MyNamespace.IMiddle", "global::MyNamespace.IZulu"],
            aggregate.ProxyInterfaces.Select(static proxy => proxy.InterfaceType.SyntaxString).ToArray());
        Assert.Equal(
            ["global::Codecs.AlphaCodec", "global::Codecs.ZuluCodec"],
            aggregate.RegisteredCodecs.Select(static codec => codec.Type.SyntaxString).ToArray());
        Assert.Equal(
            ["global::Impl.Alpha", "global::Impl.Zulu"],
            aggregate.InterfaceImplementations.Select(static implementation => implementation.ImplementationType.SyntaxString).ToArray());
        Assert.Equal(
            ["PartZ", "PartA"],
            aggregate.ReferenceAssemblyData.ApplicationParts.ToArray());
    }

    [Fact]
    public void MetadataAggregateModel_CreateMetadataAggregate_OrderIndependentInputs_AreEqual()
    {
        var serializableAlpha = CreateSerializableTypeModel("AlphaType", "MyNamespace");
        var serializableBeta = CreateSerializableTypeModel("BetaType", "MyNamespace");
        var serializableGamma = CreateSerializableTypeModel("GammaType", "MyNamespace");
        var proxyAlpha = CreateProxyInterfaceModel("IAlpha");
        var proxyBeta = CreateProxyInterfaceModel("IBeta");
        var proxyGamma = CreateProxyInterfaceModel("IGamma");

        var aggregateA = ModelExtractor.CreateMetadataAggregate(
            "TestAssembly",
            ImmutableArray.Create(serializableBeta, serializableAlpha),
            ImmutableArray.Create(proxyBeta, proxyAlpha),
            CreateReferenceAssemblyModel(
                applicationParts: ImmutableArray.Create("PartZ", "PartA"),
                referencedSerializableTypes: ImmutableArray.Create(serializableGamma, serializableAlpha),
                referencedProxyInterfaces: ImmutableArray.Create(proxyGamma, proxyAlpha),
                registeredCodecs: ImmutableArray.Create(
                    new RegisteredCodecModel(new TypeRef("global::Codecs.ZuluCodec"), RegisteredCodecKind.Serializer),
                    new RegisteredCodecModel(new TypeRef("global::Codecs.AlphaCodec"), RegisteredCodecKind.Serializer)),
                interfaceImplementations: ImmutableArray.Create(
                    new InterfaceImplementationModel(new TypeRef("global::Impl.Zulu")),
                    new InterfaceImplementationModel(new TypeRef("global::Impl.Alpha")))));

        var aggregateB = ModelExtractor.CreateMetadataAggregate(
            "TestAssembly",
            ImmutableArray.Create(serializableAlpha, serializableBeta),
            ImmutableArray.Create(proxyAlpha, proxyBeta),
            CreateReferenceAssemblyModel(
                applicationParts: ImmutableArray.Create("PartZ", "PartA"),
                referencedSerializableTypes: ImmutableArray.Create(serializableAlpha, serializableGamma),
                referencedProxyInterfaces: ImmutableArray.Create(proxyAlpha, proxyGamma),
                registeredCodecs: ImmutableArray.Create(
                    new RegisteredCodecModel(new TypeRef("global::Codecs.AlphaCodec"), RegisteredCodecKind.Serializer),
                    new RegisteredCodecModel(new TypeRef("global::Codecs.ZuluCodec"), RegisteredCodecKind.Serializer)),
                interfaceImplementations: ImmutableArray.Create(
                    new InterfaceImplementationModel(new TypeRef("global::Impl.Alpha")),
                    new InterfaceImplementationModel(new TypeRef("global::Impl.Zulu")))));

        Assert.Equal(aggregateA, aggregateB);
        Assert.Equal(aggregateA.GetHashCode(), aggregateB.GetHashCode());
    }

    private static MemberModel CreateMemberModel(uint fieldId, string name, string type) => new(
        fieldId,
        name,
        new TypeRef(type),
        new TypeRef("global::MyNamespace.MyType"),
        "TestAssembly",
        type.Replace("global::", string.Empty).Replace(".", "_"),
        isPrimaryConstructorParameter: false,
        isSerializable: true,
        isCopyable: true,
        MemberKind.Property,
        AccessStrategy.Direct,
        AccessStrategy.Direct,
        isObsolete: false,
        hasImmutableAttribute: false,
        isShallowCopyable: false,
        isValueType: type is "int" or "bool" or "double",
        containingTypeIsValueType: false,
        backingPropertyName: name);

    private static SerializableTypeModel CreateSerializableTypeModel(
        string name,
        string ns,
        ImmutableArray<MemberModel> members = default,
        TypeMetadataIdentity metadataIdentity = default) => new(
            Accessibility.Public,
            new TypeRef($"global::{ns}.{name}"),
            hasComplexBaseType: false,
            includePrimaryConstructorParameters: false,
            TypeRef.Empty,
            ns,
            $"OrleansCodeGen.{ns}",
            name,
            isValueType: false,
            isSealedType: true,
            isAbstractType: false,
            isEnumType: false,
            isGenericType: false,
            ImmutableArray<TypeParameterModel>.Empty,
            members,
            useActivator: false,
            isEmptyConstructable: true,
            hasActivatorConstructor: false,
            trackReferences: true,
            omitDefaultMemberValues: false,
            ImmutableArray<TypeRef>.Empty,
            isShallowCopyable: false,
            isUnsealedImmutable: false,
            isImmutable: false,
            isExceptionType: false,
            ImmutableArray<TypeRef>.Empty,
            ObjectCreationStrategy.NewExpression,
            metadataIdentity: metadataIdentity);

    private static ProxyInterfaceModel CreateProxyInterfaceModel(string name) => new(
        new TypeRef($"global::MyNamespace.{name}"),
        name,
        "OrleansCodeGen.MyNamespace",
        ImmutableArray<TypeParameterModel>.Empty,
        new ProxyBaseModel(
            new TypeRef("global::Orleans.Runtime.GrainReference"),
            isExtension: false,
            generatedClassNameComponent: "GrainReference",
            ImmutableArray<InvokableBaseTypeMapping>.Empty),
        ImmutableArray<MethodModel>.Empty,
        metadataIdentity: new TypeMetadataIdentity($"MyNamespace.{name}", "TestAssembly", "TestAssembly"));

    private static ReferenceAssemblyModel CreateReferenceAssemblyModel(
        ImmutableArray<string> applicationParts = default,
        ImmutableArray<SerializableTypeModel> referencedSerializableTypes = default,
        ImmutableArray<ProxyInterfaceModel> referencedProxyInterfaces = default,
        ImmutableArray<RegisteredCodecModel> registeredCodecs = default,
        ImmutableArray<InterfaceImplementationModel> interfaceImplementations = default) => new(
            "TestAssembly",
            applicationParts,
            ImmutableArray<WellKnownTypeIdModel>.Empty,
            ImmutableArray<TypeAliasModel>.Empty,
            ImmutableArray<CompoundTypeAliasModel>.Empty,
            referencedSerializableTypes,
            referencedProxyInterfaces,
            registeredCodecs,
            interfaceImplementations);
}
