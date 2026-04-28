using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator;
using Orleans.CodeGenerator.Model.Incremental;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Tests structural equality semantics for all incremental pipeline value models.
/// These tests ensure the models correctly support Roslyn's incremental generator caching.
/// </summary>
public class IncrementalModelEqualityTests
{
    #region ImmutableArrayValueComparer

    [Fact]
    public void ImmutableArrayValueComparer_DefaultEquals_Empty()
    {
        var a = default(ImmutableArray<string>);
        var b = ImmutableArray<string>.Empty;

        Assert.True(ImmutableArrayValueComparer.Equals(a, b));
        Assert.Equal(
            ImmutableArrayValueComparer.GetHashCode(a),
            ImmutableArrayValueComparer.GetHashCode(b));
    }

    [Fact]
    public void ImmutableArrayValueComparer_SameElements_AreEqual()
    {
        var a = ImmutableArray.Create("foo", "bar");
        var b = ImmutableArray.Create("foo", "bar");

        Assert.True(ImmutableArrayValueComparer.Equals(a, b));
        Assert.Equal(
            ImmutableArrayValueComparer.GetHashCode(a),
            ImmutableArrayValueComparer.GetHashCode(b));
    }

    [Fact]
    public void ImmutableArrayValueComparer_DifferentElements_AreNotEqual()
    {
        var a = ImmutableArray.Create("foo", "bar");
        var b = ImmutableArray.Create("foo", "baz");

        Assert.False(ImmutableArrayValueComparer.Equals(a, b));
    }

    [Fact]
    public void ImmutableArrayValueComparer_DifferentLength_AreNotEqual()
    {
        var a = ImmutableArray.Create("foo");
        var b = ImmutableArray.Create("foo", "bar");

        Assert.False(ImmutableArrayValueComparer.Equals(a, b));
    }

    #endregion

    #region TypeRef

    [Fact]
    public void TypeRef_SameString_AreEqual()
    {
        var a = new TypeRef("global::MyNamespace.MyType");
        var b = new TypeRef("global::MyNamespace.MyType");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TypeRef_DifferentString_AreNotEqual()
    {
        var a = new TypeRef("global::MyNamespace.MyType");
        var b = new TypeRef("global::MyNamespace.OtherType");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TypeRef_Empty_IsEmpty()
    {
        Assert.True(TypeRef.Empty.IsEmpty);
        Assert.False(new TypeRef("int").IsEmpty);
    }

    [Fact]
    public void TypeRef_ToTypeSyntax_ProducesValidSyntax()
    {
        var typeRef = new TypeRef("int");
        var syntax = typeRef.ToTypeSyntax();
        Assert.Equal("int", syntax.ToString());
    }

    #endregion

    #region TypeParameterModel

    [Fact]
    public void TypeParameterModel_SameValues_AreEqual()
    {
        var a = new TypeParameterModel("T", "T", 0);
        var b = new TypeParameterModel("T", "T", 0);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TypeParameterModel_DifferentName_AreNotEqual()
    {
        var a = new TypeParameterModel("T", "T", 0);
        var b = new TypeParameterModel("U", "U", 0);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TypeParameterModel_DifferentOrdinal_AreNotEqual()
    {
        var a = new TypeParameterModel("T", "T", 0);
        var b = new TypeParameterModel("T", "T", 1);
        Assert.NotEqual(a, b);
    }

    #endregion

    #region MemberModel

    [Fact]
    public void MemberModel_SameValues_AreEqual()
    {
        var a = CreateMemberModel(fieldId: 0, name: "Value", type: "int");
        var b = CreateMemberModel(fieldId: 0, name: "Value", type: "int");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void MemberModel_DifferentFieldId_AreNotEqual()
    {
        var a = CreateMemberModel(fieldId: 0, name: "Value", type: "int");
        var b = CreateMemberModel(fieldId: 1, name: "Value", type: "int");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MemberModel_DifferentName_AreNotEqual()
    {
        var a = CreateMemberModel(fieldId: 0, name: "Value", type: "int");
        var b = CreateMemberModel(fieldId: 0, name: "Amount", type: "int");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MemberModel_DifferentType_AreNotEqual()
    {
        var a = CreateMemberModel(fieldId: 0, name: "Value", type: "int");
        var b = CreateMemberModel(fieldId: 0, name: "Value", type: "string");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MemberModel_DifferentAccessStrategy_AreNotEqual()
    {
        var a = CreateMemberModel(fieldId: 0, name: "Value", type: "int", getterStrategy: AccessStrategy.Direct);
        var b = CreateMemberModel(fieldId: 0, name: "Value", type: "int", getterStrategy: AccessStrategy.UnsafeAccessor);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MemberModel_NullDoesNotEqual()
    {
        var a = CreateMemberModel(fieldId: 0, name: "Value", type: "int");
        Assert.False(a.Equals(null!));
    }

    #endregion

    #region RegisteredCodecModel

    [Fact]
    public void RegisteredCodecModel_SameValues_AreEqual()
    {
        var a = new RegisteredCodecModel(new TypeRef("MySerializer"), RegisteredCodecKind.Serializer);
        var b = new RegisteredCodecModel(new TypeRef("MySerializer"), RegisteredCodecKind.Serializer);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void RegisteredCodecModel_DifferentKind_AreNotEqual()
    {
        var a = new RegisteredCodecModel(new TypeRef("MyType"), RegisteredCodecKind.Serializer);
        var b = new RegisteredCodecModel(new TypeRef("MyType"), RegisteredCodecKind.Copier);
        Assert.NotEqual(a, b);
    }

    #endregion

    #region SerializableTypeModel

    [Fact]
    public void SerializableTypeModel_SameValues_AreEqual()
    {
        var a = CreateSerializableTypeModel("MyType", "MyNamespace");
        var b = CreateSerializableTypeModel("MyType", "MyNamespace");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void SerializableTypeModel_DifferentName_AreNotEqual()
    {
        var a = CreateSerializableTypeModel("MyType", "MyNamespace");
        var b = CreateSerializableTypeModel("OtherType", "MyNamespace");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SerializableTypeModel_DifferentMembers_AreNotEqual()
    {
        var members1 = ImmutableArray.Create(
            CreateMemberModel(0, "X", "int"));
        var members2 = ImmutableArray.Create(
            CreateMemberModel(0, "X", "int"),
            CreateMemberModel(1, "Y", "int"));

        var a = CreateSerializableTypeModel("MyType", "MyNamespace", members: members1);
        var b = CreateSerializableTypeModel("MyType", "MyNamespace", members: members2);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SerializableTypeModel_DifferentFlags_AreNotEqual()
    {
        var a = CreateSerializableTypeModel("MyType", "MyNamespace", isValueType: true);
        var b = CreateSerializableTypeModel("MyType", "MyNamespace", isValueType: false);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SerializableTypeModel_NullDoesNotEqual()
    {
        var a = CreateSerializableTypeModel("MyType", "MyNamespace");
        Assert.False(a.Equals(null!));
    }

    #endregion

    #region ProxyInterfaceModel

    [Fact]
    public void ProxyInterfaceModel_SameValues_AreEqual()
    {
        var a = CreateProxyInterfaceModel("IMyGrain");
        var b = CreateProxyInterfaceModel("IMyGrain");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void ProxyInterfaceModel_DifferentName_AreNotEqual()
    {
        var a = CreateProxyInterfaceModel("IMyGrain");
        var b = CreateProxyInterfaceModel("IOtherGrain");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ProxyInterfaceModel_DifferentMethods_AreNotEqual()
    {
        var methods1 = ImmutableArray.Create(
            CreateMethodModel("DoSomething"));
        var methods2 = ImmutableArray.Create(
            CreateMethodModel("DoSomething"),
            CreateMethodModel("DoSomethingElse"));

        var a = CreateProxyInterfaceModel("IMyGrain", methods: methods1);
        var b = CreateProxyInterfaceModel("IMyGrain", methods: methods2);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ProxyInterfaceModel_NullDoesNotEqual()
    {
        var a = CreateProxyInterfaceModel("IMyGrain");
        Assert.False(a.Equals(null!));
    }

    #endregion

    #region MethodModel

    [Fact]
    public void MethodModel_SameValues_AreEqual()
    {
        var a = CreateMethodModel("DoWork");
        var b = CreateMethodModel("DoWork");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void MethodModel_DifferentName_AreNotEqual()
    {
        var a = CreateMethodModel("DoWork");
        var b = CreateMethodModel("DoOtherWork");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MethodModel_HasAlias_WhenIdsAreDifferent()
    {
        var m = new MethodModel(
            "DoWork",
            new TypeRef("global::System.Threading.Tasks.Task"),
            ImmutableArray<MethodParameterModel>.Empty,
            ImmutableArray<TypeParameterModel>.Empty,
            new TypeRef("global::MyNamespace.IMyGrain"),
            new TypeRef("global::MyNamespace.IMyGrain"),
            "IMyGrain",
            "OrleansCodeGen.MyNamespace",
            0,
            "AABB1122",
            "MyAlias",
            null,
            ImmutableArray<CustomInitializerModel>.Empty,
            false);
        Assert.True(m.HasAlias);
    }

    [Fact]
    public void MethodModel_NoAlias_WhenIdsMatch()
    {
        var m = CreateMethodModel("DoWork");
        Assert.False(m.HasAlias);
    }

    #endregion

    #region ReferenceAssemblyModel

    [Fact]
    public void ReferenceAssemblyModel_SameValues_AreEqual()
    {
        var a = CreateReferenceAssemblyModel("PartA");
        var b = CreateReferenceAssemblyModel("PartA");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void ReferenceAssemblyModel_DifferentParts_AreNotEqual()
    {
        var a = CreateReferenceAssemblyModel("PartA");
        var b = CreateReferenceAssemblyModel("PartB");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ReferenceAssemblyModel_NullDoesNotEqual()
    {
        var a = CreateReferenceAssemblyModel("PartA");
        Assert.False(a.Equals(null!));
    }

    #endregion

    #region MetadataAggregateModel

    [Fact]
    public void MetadataAggregateModel_SameValues_AreEqual()
    {
        var a = CreateMetadataAggregateModel("TestAssembly");
        var b = CreateMetadataAggregateModel("TestAssembly");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void MetadataAggregateModel_DifferentAssemblyName_AreNotEqual()
    {
        var a = CreateMetadataAggregateModel("TestAssembly");
        var b = CreateMetadataAggregateModel("OtherAssembly");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MetadataAggregateModel_NullDoesNotEqual()
    {
        var a = CreateMetadataAggregateModel("TestAssembly");
        Assert.False(a.Equals(null!));
    }

    [Fact]
    public void MetadataAggregateModel_CreateMetadataAggregate_MergesAndSortsDeterministically()
    {
        var sourceSerializableTypes = ImmutableArray.Create(
            CreateSerializableTypeModel("ZuluType", "MyNamespace"),
            CreateSerializableTypeModel("AlphaType", "MyNamespace"));
        var sourceProxyInterfaces = ImmutableArray.Create(
            CreateProxyInterfaceModel("IZulu"),
            CreateProxyInterfaceModel("IAlpha"));

        var refData = CreateReferenceAssemblyModel(
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
                new InterfaceImplementationModel(new TypeRef("global::Impl.Alpha"))));

        var aggregate = ModelExtractor.CreateMetadataAggregate("TestAssembly", sourceSerializableTypes, sourceProxyInterfaces, refData);

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

    [Fact]
    public void MetadataAggregateModel_DifferentInterfaceImplementations_ProduceDifferentHashCodes()
    {
        var a = ModelExtractor.CreateMetadataAggregate(
            "TestAssembly",
            ImmutableArray<SerializableTypeModel>.Empty,
            ImmutableArray<ProxyInterfaceModel>.Empty,
            CreateReferenceAssemblyModel(
                interfaceImplementations: ImmutableArray<InterfaceImplementationModel>.Empty));

        var b = ModelExtractor.CreateMetadataAggregate(
            "TestAssembly",
            ImmutableArray<SerializableTypeModel>.Empty,
            ImmutableArray<ProxyInterfaceModel>.Empty,
            CreateReferenceAssemblyModel(
                interfaceImplementations: ImmutableArray.Create(
                    new InterfaceImplementationModel(new TypeRef("global::MyNamespace.MyImplementation")))));

        Assert.NotEqual(a, b);
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    #endregion

    #region CompoundAliasComponentModel

    [Fact]
    public void CompoundAliasComponentModel_SameString_AreEqual()
    {
        var a = new CompoundAliasComponentModel("inv");
        var b = new CompoundAliasComponentModel("inv");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void CompoundAliasComponentModel_SameType_AreEqual()
    {
        var a = new CompoundAliasComponentModel(new TypeRef("global::MyType"));
        var b = new CompoundAliasComponentModel(new TypeRef("global::MyType"));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void CompoundAliasComponentModel_StringVsType_AreNotEqual()
    {
        var a = new CompoundAliasComponentModel("MyType");
        var b = new CompoundAliasComponentModel(new TypeRef("MyType"));
        Assert.NotEqual(a, b);
    }

    #endregion

    #region WellKnownTypeIdModel

    [Fact]
    public void WellKnownTypeIdModel_SameValues_AreEqual()
    {
        var a = new WellKnownTypeIdModel(new TypeRef("global::MyType"), 42);
        var b = new WellKnownTypeIdModel(new TypeRef("global::MyType"), 42);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void WellKnownTypeIdModel_DifferentType_AreNotEqual()
    {
        var a = new WellKnownTypeIdModel(new TypeRef("global::MyType"), 42);
        var b = new WellKnownTypeIdModel(new TypeRef("global::OtherType"), 42);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void WellKnownTypeIdModel_DifferentId_AreNotEqual()
    {
        var a = new WellKnownTypeIdModel(new TypeRef("global::MyType"), 42);
        var b = new WellKnownTypeIdModel(new TypeRef("global::MyType"), 99);
        Assert.NotEqual(a, b);
    }

    #endregion

    #region TypeAliasModel

    [Fact]
    public void TypeAliasModel_SameValues_AreEqual()
    {
        var a = new TypeAliasModel(new TypeRef("global::MyType"), "my-alias");
        var b = new TypeAliasModel(new TypeRef("global::MyType"), "my-alias");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TypeAliasModel_DifferentAlias_AreNotEqual()
    {
        var a = new TypeAliasModel(new TypeRef("global::MyType"), "alias-a");
        var b = new TypeAliasModel(new TypeRef("global::MyType"), "alias-b");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TypeAliasModel_DifferentType_AreNotEqual()
    {
        var a = new TypeAliasModel(new TypeRef("global::TypeA"), "my-alias");
        var b = new TypeAliasModel(new TypeRef("global::TypeB"), "my-alias");
        Assert.NotEqual(a, b);
    }

    #endregion

    #region CompoundTypeAliasModel

    [Fact]
    public void CompoundTypeAliasModel_SameValues_AreEqual()
    {
        var components = ImmutableArray.Create(new CompoundAliasComponentModel("part1"));
        var a = new CompoundTypeAliasModel(components, new TypeRef("global::MyType"));
        var b = new CompoundTypeAliasModel(components, new TypeRef("global::MyType"));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void CompoundTypeAliasModel_DifferentComponents_AreNotEqual()
    {
        var compsA = ImmutableArray.Create(new CompoundAliasComponentModel("part1"));
        var compsB = ImmutableArray.Create(new CompoundAliasComponentModel("part2"));
        var a = new CompoundTypeAliasModel(compsA, new TypeRef("global::MyType"));
        var b = new CompoundTypeAliasModel(compsB, new TypeRef("global::MyType"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void CompoundTypeAliasModel_DifferentTargetType_AreNotEqual()
    {
        var components = ImmutableArray.Create(new CompoundAliasComponentModel("part1"));
        var a = new CompoundTypeAliasModel(components, new TypeRef("global::TypeA"));
        var b = new CompoundTypeAliasModel(components, new TypeRef("global::TypeB"));
        Assert.NotEqual(a, b);
    }

    #endregion

    #region InterfaceImplementationModel

    [Fact]
    public void InterfaceImplementationModel_SameValues_AreEqual()
    {
        var a = new InterfaceImplementationModel(new TypeRef("global::MyImpl"));
        var b = new InterfaceImplementationModel(new TypeRef("global::MyImpl"));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void InterfaceImplementationModel_DifferentType_AreNotEqual()
    {
        var a = new InterfaceImplementationModel(new TypeRef("global::ImplA"));
        var b = new InterfaceImplementationModel(new TypeRef("global::ImplB"));
        Assert.NotEqual(a, b);
    }

    #endregion

    #region InvokableBaseTypeMapping

    [Fact]
    public void InvokableBaseTypeMapping_SameValues_AreEqual()
    {
        var a = new InvokableBaseTypeMapping(new TypeRef("global::System.Threading.Tasks.Task"), new TypeRef("global::Orleans.TaskRequest"));
        var b = new InvokableBaseTypeMapping(new TypeRef("global::System.Threading.Tasks.Task"), new TypeRef("global::Orleans.TaskRequest"));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void InvokableBaseTypeMapping_DifferentReturnType_AreNotEqual()
    {
        var a = new InvokableBaseTypeMapping(new TypeRef("global::System.Threading.Tasks.Task"), new TypeRef("global::Orleans.TaskRequest"));
        var b = new InvokableBaseTypeMapping(new TypeRef("global::System.Threading.Tasks.ValueTask"), new TypeRef("global::Orleans.TaskRequest"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void InvokableBaseTypeMapping_DifferentInvokableBase_AreNotEqual()
    {
        var a = new InvokableBaseTypeMapping(new TypeRef("global::System.Threading.Tasks.Task"), new TypeRef("global::Orleans.TaskRequest"));
        var b = new InvokableBaseTypeMapping(new TypeRef("global::System.Threading.Tasks.Task"), new TypeRef("global::Orleans.ValueTaskRequest"));
        Assert.NotEqual(a, b);
    }

    #endregion

    #region ProxyBaseModel

    [Fact]
    public void ProxyBaseModel_SameValues_AreEqual()
    {
        var a = new ProxyBaseModel(
            proxyBaseType: new TypeRef("global::Orleans.Runtime.GrainReference"),
            isExtension: false,
            generatedClassNameComponent: "GrainReference",
            invokableBaseTypes: ImmutableArray<InvokableBaseTypeMapping>.Empty);
        var b = new ProxyBaseModel(
            proxyBaseType: new TypeRef("global::Orleans.Runtime.GrainReference"),
            isExtension: false,
            generatedClassNameComponent: "GrainReference",
            invokableBaseTypes: ImmutableArray<InvokableBaseTypeMapping>.Empty);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void ProxyBaseModel_DifferentExtension_AreNotEqual()
    {
        var a = new ProxyBaseModel(
            proxyBaseType: new TypeRef("global::Orleans.Runtime.GrainReference"),
            isExtension: false,
            generatedClassNameComponent: "GrainReference",
            invokableBaseTypes: ImmutableArray<InvokableBaseTypeMapping>.Empty);
        var b = new ProxyBaseModel(
            proxyBaseType: new TypeRef("global::Orleans.Runtime.GrainReference"),
            isExtension: true,
            generatedClassNameComponent: "GrainReference",
            invokableBaseTypes: ImmutableArray<InvokableBaseTypeMapping>.Empty);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ProxyBaseModel_DifferentClassNameComponent_AreNotEqual()
    {
        var a = new ProxyBaseModel(
            proxyBaseType: new TypeRef("global::Orleans.Runtime.GrainReference"),
            isExtension: false,
            generatedClassNameComponent: "GrainReference",
            invokableBaseTypes: ImmutableArray<InvokableBaseTypeMapping>.Empty);
        var b = new ProxyBaseModel(
            proxyBaseType: new TypeRef("global::Orleans.Runtime.GrainReference"),
            isExtension: false,
            generatedClassNameComponent: "OtherReference",
            invokableBaseTypes: ImmutableArray<InvokableBaseTypeMapping>.Empty);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ProxyBaseModel_DifferentInvokableBaseTypes_AreNotEqual()
    {
        var mappingA = new InvokableBaseTypeMapping(new TypeRef("global::Task"), new TypeRef("global::TaskRequest"));
        var a = new ProxyBaseModel(
            proxyBaseType: new TypeRef("global::Orleans.Runtime.GrainReference"),
            isExtension: false,
            generatedClassNameComponent: "GrainReference",
            invokableBaseTypes: ImmutableArray.Create(mappingA));
        var b = new ProxyBaseModel(
            proxyBaseType: new TypeRef("global::Orleans.Runtime.GrainReference"),
            isExtension: false,
            generatedClassNameComponent: "GrainReference",
            invokableBaseTypes: ImmutableArray<InvokableBaseTypeMapping>.Empty);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ProxyBaseModel_NullDoesNotEqual()
    {
        var a = new ProxyBaseModel(
            proxyBaseType: new TypeRef("global::Orleans.Runtime.GrainReference"),
            isExtension: false,
            generatedClassNameComponent: "GrainReference",
            invokableBaseTypes: ImmutableArray<InvokableBaseTypeMapping>.Empty);
        Assert.False(a.Equals(null!));
    }

    #endregion

    #region MethodParameterModel

    [Fact]
    public void MethodParameterModel_SameValues_AreEqual()
    {
        var a = new MethodParameterModel("arg", new TypeRef("int"), 0, false);
        var b = new MethodParameterModel("arg", new TypeRef("int"), 0, false);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void MethodParameterModel_DifferentName_AreNotEqual()
    {
        var a = new MethodParameterModel("arg1", new TypeRef("int"), 0, false);
        var b = new MethodParameterModel("arg2", new TypeRef("int"), 0, false);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MethodParameterModel_DifferentType_AreNotEqual()
    {
        var a = new MethodParameterModel("arg", new TypeRef("int"), 0, false);
        var b = new MethodParameterModel("arg", new TypeRef("string"), 0, false);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MethodParameterModel_DifferentCancellation_AreNotEqual()
    {
        var a = new MethodParameterModel("ct", new TypeRef("global::System.Threading.CancellationToken"), 0, false);
        var b = new MethodParameterModel("ct", new TypeRef("global::System.Threading.CancellationToken"), 0, true);
        Assert.NotEqual(a, b);
    }

    #endregion

    #region CustomInitializerModel

    [Fact]
    public void CustomInitializerModel_SameValues_AreEqual()
    {
        var a = new CustomInitializerModel("Init", "value");
        var b = new CustomInitializerModel("Init", "value");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void CustomInitializerModel_DifferentMethodName_AreNotEqual()
    {
        var a = new CustomInitializerModel("Init", "value");
        var b = new CustomInitializerModel("Setup", "value");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void CustomInitializerModel_DifferentArgument_AreNotEqual()
    {
        var a = new CustomInitializerModel("Init", "value1");
        var b = new CustomInitializerModel("Init", "value2");
        Assert.NotEqual(a, b);
    }

    #endregion

    #region DefaultCopierModel

    [Fact]
    public void DefaultCopierModel_SameValues_AreEqual()
    {
        var a = new DefaultCopierModel(new TypeRef("global::MyType"), new TypeRef("global::MyCopier"));
        var b = new DefaultCopierModel(new TypeRef("global::MyType"), new TypeRef("global::MyCopier"));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DefaultCopierModel_DifferentOriginalType_AreNotEqual()
    {
        var a = new DefaultCopierModel(new TypeRef("global::TypeA"), new TypeRef("global::MyCopier"));
        var b = new DefaultCopierModel(new TypeRef("global::TypeB"), new TypeRef("global::MyCopier"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DefaultCopierModel_DifferentCopierType_AreNotEqual()
    {
        var a = new DefaultCopierModel(new TypeRef("global::MyType"), new TypeRef("global::CopierA"));
        var b = new DefaultCopierModel(new TypeRef("global::MyType"), new TypeRef("global::CopierB"));
        Assert.NotEqual(a, b);
    }

    #endregion

    #region Helpers

    private static MemberModel CreateMemberModel(
        uint fieldId = 0,
        string name = "Field",
        string type = "int",
        AccessStrategy getterStrategy = AccessStrategy.Direct,
        AccessStrategy setterStrategy = AccessStrategy.Direct)
    {
        return new MemberModel(
            fieldId: fieldId,
            name: name,
            type: new TypeRef(type),
            containingType: new TypeRef("global::MyNamespace.MyType"),
            assemblyName: "TestAssembly",
            typeNameIdentifier: type,
            isPrimaryConstructorParameter: false,
            isSerializable: true,
            isCopyable: true,
            kind: MemberKind.Field,
            getterStrategy: getterStrategy,
            setterStrategy: setterStrategy,
            isObsolete: false,
            hasImmutableAttribute: false,
            isShallowCopyable: false,
            isValueType: true,
            containingTypeIsValueType: false,
            backingPropertyName: null);
    }

    private static SerializableTypeModel CreateSerializableTypeModel(
        string name = "MyType",
        string ns = "MyNamespace",
        bool isValueType = false,
        ImmutableArray<MemberModel> members = default)
    {
        return new SerializableTypeModel(
            accessibility: Accessibility.Public,
            typeSyntax: new TypeRef($"global::{ns}.{name}"),
            hasComplexBaseType: false,
            includePrimaryConstructorParameters: false,
            baseTypeSyntax: new TypeRef("object"),
            ns: ns,
            generatedNamespace: $"OrleansCodeGen.{ns}",
            name: name,
            isValueType: isValueType,
            isSealedType: false,
            isAbstractType: false,
            isEnumType: false,
            isGenericType: false,
            typeParameters: ImmutableArray<TypeParameterModel>.Empty,
            members: members.IsDefault ? ImmutableArray<MemberModel>.Empty : members,
            useActivator: false,
            isEmptyConstructable: true,
            hasActivatorConstructor: false,
            trackReferences: true,
            omitDefaultMemberValues: false,
            serializationHooks: ImmutableArray<TypeRef>.Empty,
            isShallowCopyable: false,
            isUnsealedImmutable: false,
            isImmutable: false,
            isExceptionType: false,
            activatorConstructorParameters: ImmutableArray<TypeRef>.Empty,
            creationStrategy: ObjectCreationStrategy.NewExpression);
    }

    private static MethodModel CreateMethodModel(string name = "DoWork")
    {
        return new MethodModel(
            name: name,
            returnType: new TypeRef("global::System.Threading.Tasks.Task"),
            parameters: ImmutableArray<MethodParameterModel>.Empty,
            typeParameters: ImmutableArray<TypeParameterModel>.Empty,
            containingInterfaceType: new TypeRef("global::MyNamespace.IMyGrain"),
            originalContainingInterfaceType: new TypeRef("global::MyNamespace.IMyGrain"),
            containingInterfaceName: "IMyGrain",
            containingInterfaceGeneratedNamespace: "OrleansCodeGen.MyNamespace",
            containingInterfaceTypeParameterCount: 0,
            generatedMethodId: "AABB1122",
            methodId: "AABB1122",
            responseTimeoutTicks: null,
            customInitializerMethods: ImmutableArray<CustomInitializerModel>.Empty,
            isCancellable: false);
    }

    private static ProxyInterfaceModel CreateProxyInterfaceModel(
        string name = "IMyGrain",
        ImmutableArray<MethodModel> methods = default)
    {
        var proxyBase = new ProxyBaseModel(
            proxyBaseType: new TypeRef("global::Orleans.Runtime.GrainReference"),
            isExtension: false,
            generatedClassNameComponent: "GrainReference",
            invokableBaseTypes: ImmutableArray<InvokableBaseTypeMapping>.Empty);

        return new ProxyInterfaceModel(
            interfaceType: new TypeRef($"global::MyNamespace.{name}"),
            name: name,
            generatedNamespace: "OrleansCodeGen.MyNamespace",
            typeParameters: ImmutableArray<TypeParameterModel>.Empty,
            proxyBase: proxyBase,
            methods: methods.IsDefault ? ImmutableArray.Create(CreateMethodModel()) : methods);
    }

    private static ReferenceAssemblyModel CreateReferenceAssemblyModel(
        string partName = "PartA",
        ImmutableArray<string> applicationParts = default,
        ImmutableArray<WellKnownTypeIdModel> wellKnownTypeIds = default,
        ImmutableArray<TypeAliasModel> typeAliases = default,
        ImmutableArray<CompoundTypeAliasModel> compoundTypeAliases = default,
        ImmutableArray<SerializableTypeModel> referencedSerializableTypes = default,
        ImmutableArray<ProxyInterfaceModel> referencedProxyInterfaces = default,
        ImmutableArray<RegisteredCodecModel> registeredCodecs = default,
        ImmutableArray<InterfaceImplementationModel> interfaceImplementations = default)
    {
        return new ReferenceAssemblyModel(
            assemblyName: "TestAssembly",
            applicationParts: applicationParts.IsDefault ? ImmutableArray.Create(partName) : applicationParts,
            wellKnownTypeIds: wellKnownTypeIds.IsDefault ? ImmutableArray<WellKnownTypeIdModel>.Empty : wellKnownTypeIds,
            typeAliases: typeAliases.IsDefault ? ImmutableArray<TypeAliasModel>.Empty : typeAliases,
            compoundTypeAliases: compoundTypeAliases.IsDefault ? ImmutableArray<CompoundTypeAliasModel>.Empty : compoundTypeAliases,
            referencedSerializableTypes: referencedSerializableTypes.IsDefault ? ImmutableArray<SerializableTypeModel>.Empty : referencedSerializableTypes,
            referencedProxyInterfaces: referencedProxyInterfaces.IsDefault ? ImmutableArray<ProxyInterfaceModel>.Empty : referencedProxyInterfaces,
            registeredCodecs: registeredCodecs.IsDefault ? ImmutableArray<RegisteredCodecModel>.Empty : registeredCodecs,
            interfaceImplementations: interfaceImplementations.IsDefault ? ImmutableArray<InterfaceImplementationModel>.Empty : interfaceImplementations);
    }

    private static MetadataAggregateModel CreateMetadataAggregateModel(string assemblyName = "TestAssembly")
    {
        return new MetadataAggregateModel(
            assemblyName: assemblyName,
            serializableTypes: ImmutableArray<SerializableTypeModel>.Empty,
            proxyInterfaces: ImmutableArray<ProxyInterfaceModel>.Empty,
            registeredCodecs: ImmutableArray<RegisteredCodecModel>.Empty,
            referenceAssemblyData: CreateReferenceAssemblyModel(),
            activatableTypes: ImmutableArray<TypeRef>.Empty,
            generatedProxyTypes: ImmutableArray<TypeRef>.Empty,
            invokableInterfaces: ImmutableArray<TypeRef>.Empty,
            interfaceImplementations: ImmutableArray<InterfaceImplementationModel>.Empty,
            defaultCopiers: ImmutableArray<DefaultCopierModel>.Empty);
    }

    #endregion
}
