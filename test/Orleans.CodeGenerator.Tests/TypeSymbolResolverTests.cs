using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator.Tests;

public class TypeSymbolResolverTests
{
    private const string ResolverSource = """
        namespace ResolverCases;

        public class SourceType
        {
        }

        public class FallbackType
        {
        }

        public class Outer
        {
            public class Inner
            {
            }
        }

        public class Generic<TFirst, TSecond>
        {
        }

        public class WrongOuter<TOuter>
        {
            public class InnerGeneric<TInner>
            {
            }
        }

        public class OuterGeneric<TOuter>
        {
            public class WrongName<TInner>
            {
            }

            public class InnerGeneric<TFirst, TSecond>
            {
            }

            public class InnerGeneric<TInner>
            {
            }
        }

        public interface IProxy
        {
        }

        public class ProxyClass
        {
        }
        """;

    [Theory]
    [InlineData("ResolverCases.SourceType", "ResolverCases.SourceType", TypeKind.Class)]
    [InlineData("global::ResolverCases.SourceType", "ResolverCases.SourceType", TypeKind.Class)]
    [InlineData("bool", "System.Boolean", TypeKind.Struct)]
    [InlineData("byte", "System.Byte", TypeKind.Struct)]
    [InlineData("sbyte", "System.SByte", TypeKind.Struct)]
    [InlineData("short", "System.Int16", TypeKind.Struct)]
    [InlineData("ushort", "System.UInt16", TypeKind.Struct)]
    [InlineData("int", "System.Int32", TypeKind.Struct)]
    [InlineData("uint", "System.UInt32", TypeKind.Struct)]
    [InlineData("long", "System.Int64", TypeKind.Struct)]
    [InlineData("ulong", "System.UInt64", TypeKind.Struct)]
    [InlineData("float", "System.Single", TypeKind.Struct)]
    [InlineData("double", "System.Double", TypeKind.Struct)]
    [InlineData("decimal", "System.Decimal", TypeKind.Struct)]
    [InlineData("char", "System.Char", TypeKind.Struct)]
    [InlineData("string", "System.String", TypeKind.Class)]
    [InlineData("object", "System.Object", TypeKind.Class)]
    [InlineData("global::ResolverCases.Outer.Inner", "ResolverCases.Outer+Inner", TypeKind.Class)]
    [InlineData("global::ResolverCases.Generic<,>", "ResolverCases.Generic`2", TypeKind.Class)]
    [InlineData("  global::ResolverCases.Generic < , >  ", "ResolverCases.Generic`2", TypeKind.Class)]
    public async Task TryResolveType_FromTypeRefSyntax_ResolvesExactSymbol(
        string typeSyntax,
        string expectedMetadataName,
        TypeKind expectedTypeKind)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(ResolverSource, "ResolverConsumer");
        var expected = GetRequiredType(compilation, expectedMetadataName);
        var resolver = new TypeSymbolResolver(compilation);

        var success = resolver.TryResolveType(
            new TypeRef(typeSyntax),
            TypeMetadataIdentity.Empty,
            TestContext.Current.CancellationToken,
            out var actual);

        AssertSuccessfulResolution(success, actual, expected, expectedMetadataName, expectedTypeKind);
    }

    [Theory]
    [InlineData("bool", SpecialType.System_Boolean)]
    [InlineData("byte", SpecialType.System_Byte)]
    [InlineData("sbyte", SpecialType.System_SByte)]
    [InlineData("short", SpecialType.System_Int16)]
    [InlineData("ushort", SpecialType.System_UInt16)]
    [InlineData("int", SpecialType.System_Int32)]
    [InlineData("uint", SpecialType.System_UInt32)]
    [InlineData("long", SpecialType.System_Int64)]
    [InlineData("ulong", SpecialType.System_UInt64)]
    [InlineData("float", SpecialType.System_Single)]
    [InlineData("double", SpecialType.System_Double)]
    [InlineData("decimal", SpecialType.System_Decimal)]
    [InlineData("char", SpecialType.System_Char)]
    [InlineData("string", SpecialType.System_String)]
    [InlineData("object", SpecialType.System_Object)]
    public void TryResolveType_FromPrimitiveAliasWithoutFrameworkReferences_ResolvesSpecialType(
        string typeSyntax,
        SpecialType expectedSpecialType)
    {
        var compilation = CSharpCompilation.Create("ResolverConsumer");
        var resolver = new TypeSymbolResolver(compilation);

        var success = resolver.TryResolveType(
            new TypeRef(typeSyntax),
            TypeMetadataIdentity.Empty,
            TestContext.Current.CancellationToken,
            out var actual);

        Assert.True(success);
        Assert.NotNull(actual);
        Assert.Equal(expectedSpecialType, actual.SpecialType);
    }

    [Fact]
    public async Task TryResolveType_FromMetadataIdentity_ResolvesTypeFromExactAssembly()
    {
        const string consumerSource = """
            namespace ResolverCases;

            public class Shadowed
            {
            }
            """;

        var firstReference = await CreateShadowedLibraryReference("1.0.0.0", "First");
        var secondReference = await CreateShadowedLibraryReference("2.0.0.0", "Second");
        var consumer = await TestCompilationHelper.CreateCompilation(
            consumerSource,
            "ResolverConsumer",
            firstReference,
            secondReference);
        var firstAssembly = Assert.IsAssignableFrom<IAssemblySymbol>(
            consumer.GetAssemblyOrModuleSymbol(firstReference));
        var expectedAssembly = Assert.IsAssignableFrom<IAssemblySymbol>(
            consumer.GetAssemblyOrModuleSymbol(secondReference));
        var firstVersionShadow = GetRequiredType(firstAssembly, "ResolverCases.Shadowed");
        var expected = GetRequiredType(expectedAssembly, "ResolverCases.Shadowed");
        var consumerShadow = GetRequiredType(consumer.Assembly, "ResolverCases.Shadowed");
        var resolver = new TypeSymbolResolver(consumer);
        var metadataIdentity = new TypeMetadataIdentity(
            "ResolverCases.Shadowed",
            expectedAssembly.Identity.Name,
            expectedAssembly.Identity.GetDisplayName());

        Assert.Equal(new Version(1, 0, 0, 0), firstAssembly.Identity.Version);
        Assert.Equal(new Version(2, 0, 0, 0), expectedAssembly.Identity.Version);
        Assert.Equal(firstAssembly.Identity.Name, expectedAssembly.Identity.Name);
        Assert.NotEqual(firstAssembly.Identity, expectedAssembly.Identity);

        var success = resolver.TryResolveType(
            new TypeRef("global::ResolverCases.Shadowed"),
            metadataIdentity,
            TestContext.Current.CancellationToken,
            out var actual);

        AssertSuccessfulResolution(
            success,
            actual,
            expected,
            "ResolverCases.Shadowed",
            TypeKind.Class);
        Assert.Equal(new Version(2, 0, 0, 0), actual!.ContainingAssembly.Identity.Version);
        Assert.False(SymbolEqualityComparer.Default.Equals(firstVersionShadow, actual));
        Assert.NotEqual(firstVersionShadow.ContainingAssembly.Identity, actual.ContainingAssembly.Identity);
        Assert.False(SymbolEqualityComparer.Default.Equals(consumerShadow, actual));
        Assert.NotEqual(consumerShadow.ContainingAssembly.Identity, actual.ContainingAssembly.Identity);
    }

    [Fact]
    public async Task TryResolveType_FromAmbiguousAssemblyName_ReturnsFalseAndNull()
    {
        var consumer = await CreateAmbiguousAssemblyConsumer();
        var resolver = new TypeSymbolResolver(consumer);
        var metadataIdentity = new TypeMetadataIdentity(
            "ResolverCases.Shadowed",
            "ResolverLibrary",
            assemblyIdentity: string.Empty);

        var success = resolver.TryResolveType(
            new TypeRef("global::ResolverCases.Shadowed"),
            metadataIdentity,
            TestContext.Current.CancellationToken,
            out var actual);

        AssertFailedResolution(success, actual);
    }

    [Fact]
    public async Task TryResolveSerializableType_FromAmbiguousAssemblyName_DoesNotFallBack()
    {
        var consumer = await CreateAmbiguousAssemblyConsumer();
        var resolver = new TypeSymbolResolver(consumer);
        var model = CreateSerializableModel(
            new TypeRef("global::ResolverCases.Shadowed"),
            "ResolverCases",
            "Shadowed",
            metadataIdentity: new TypeMetadataIdentity(
                "ResolverCases.Shadowed",
                "ResolverLibrary",
                assemblyIdentity: string.Empty));

        var success = resolver.TryResolveSerializableType(
            model,
            TestContext.Current.CancellationToken,
            out var actual);

        AssertFailedResolution(success, actual);
    }

    [Fact]
    public async Task TryResolveProxyInterface_FromAmbiguousAssemblyName_DoesNotFallBack()
    {
        var consumer = await CreateAmbiguousAssemblyConsumer();
        var resolver = new TypeSymbolResolver(consumer);
        var model = CreateProxyModel(
            new TypeRef("global::ResolverCases.IShadowed"),
            "IShadowed",
            new TypeMetadataIdentity(
                "ResolverCases.IShadowed",
                "ResolverLibrary",
                assemblyIdentity: string.Empty));

        var success = resolver.TryResolveProxyInterface(
            model,
            TestContext.Current.CancellationToken,
            out var actual);

        AssertFailedResolution(success, actual);
    }

    [Fact]
    public async Task TryResolveType_WhenMetadataIdentityDoesNotResolve_FallsBackToSyntax()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(ResolverSource, "ResolverConsumer");
        var expected = GetRequiredType(compilation, "ResolverCases.FallbackType");
        var resolver = new TypeSymbolResolver(compilation);
        var missingIdentity = new TypeMetadataIdentity(
            "Missing.Type",
            "Missing.Assembly",
            "Missing.Assembly, Version=9.9.9.9, Culture=neutral, PublicKeyToken=null");

        var success = resolver.TryResolveType(
            new TypeRef("global::ResolverCases.FallbackType"),
            missingIdentity,
            TestContext.Current.CancellationToken,
            out var actual);

        AssertSuccessfulResolution(
            success,
            actual,
            expected,
            "ResolverCases.FallbackType",
            TypeKind.Class);
    }

    [Fact]
    public async Task TryResolveType_WhenTypeIsUnknownOrSyntaxIsBlank_ReturnsFalseAndNull()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(ResolverSource, "ResolverConsumer");
        var resolver = new TypeSymbolResolver(compilation);
        var missingIdentity = new TypeMetadataIdentity(
            "Missing.Type",
            "Missing.Assembly",
            "Missing.Assembly, Version=9.9.9.9, Culture=neutral, PublicKeyToken=null");

        var unknownSuccess = resolver.TryResolveType(
            new TypeRef("global::Missing.Type"),
            missingIdentity,
            TestContext.Current.CancellationToken,
            out var unknownSymbol);
        var blankSuccess = resolver.TryResolveType(
            new TypeRef(" \t\r\n "),
            TypeMetadataIdentity.Empty,
            TestContext.Current.CancellationToken,
            out var blankSymbol);

        AssertFailedResolution(unknownSuccess, unknownSymbol);
        AssertFailedResolution(blankSuccess, blankSymbol);
    }

    [Fact]
    public async Task TryResolveSerializableType_FromMetadataIdentity_ResolvesExactSymbol()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(ResolverSource, "ResolverConsumer");
        var expected = GetRequiredType(compilation, "ResolverCases.SourceType");
        var model = CreateSerializableModel(
            new TypeRef("global::Missing.Type"),
            "Missing",
            "Type",
            metadataIdentity: TypeMetadataIdentity.Create(expected));
        var resolver = new TypeSymbolResolver(compilation);

        var success = resolver.TryResolveSerializableType(
            model,
            TestContext.Current.CancellationToken,
            out var actual);

        AssertSuccessfulResolution(
            success,
            actual,
            expected,
            "ResolverCases.SourceType",
            TypeKind.Class);
    }

    [Fact]
    public async Task TryResolveSerializableType_ByModelShape_ResolvesNestedGenericWithExactTotalArity()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(ResolverSource, "ResolverConsumer");
        var expected = GetRequiredType(
            compilation,
            "ResolverCases.OuterGeneric`1+InnerGeneric`1");
        var model = CreateSerializableModel(
            TypeRef.Empty,
            "ResolverCases.OuterGeneric",
            "InnerGeneric",
            typeParameterCount: 2,
            metadataIdentity: new TypeMetadataIdentity(
                "Missing.Type",
                "Missing.Assembly",
                "Missing.Assembly, Version=9.9.9.9, Culture=neutral, PublicKeyToken=null"));
        var resolver = new TypeSymbolResolver(compilation);

        var success = resolver.TryResolveSerializableType(
            model,
            TestContext.Current.CancellationToken,
            out var actual);

        AssertSuccessfulResolution(
            success,
            actual,
            expected,
            "ResolverCases.OuterGeneric`1+InnerGeneric`1",
            TypeKind.Class);
        Assert.Equal("ResolverCases.OuterGeneric", actual!.GetNamespaceAndNesting());
        Assert.Equal(2, actual!.GetAllTypeParameters().Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TryResolveSerializableType_WhenModelIsNullOrUnknown_ReturnsFalseAndNull(
        bool useNullModel)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(ResolverSource, "ResolverConsumer");
        var resolver = new TypeSymbolResolver(compilation);
        var model = useNullModel
            ? null
            : CreateSerializableModel(
                new TypeRef("global::Missing.UnknownType"),
                "Missing",
                "UnknownType");

        var success = resolver.TryResolveSerializableType(
            model!,
            TestContext.Current.CancellationToken,
            out var actual);

        AssertFailedResolution(success, actual);
    }

    [Fact]
    public async Task TryResolveProxyInterface_WhenModelDescribesInterface_ResolvesExactInterface()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(ResolverSource, "ResolverConsumer");
        var expected = GetRequiredType(compilation, "ResolverCases.IProxy");
        var model = CreateProxyModel(
            new TypeRef("global::ResolverCases.IProxy"),
            "IProxy");
        var resolver = new TypeSymbolResolver(compilation);

        var success = resolver.TryResolveProxyInterface(
            model,
            TestContext.Current.CancellationToken,
            out var actual);

        AssertSuccessfulResolution(
            success,
            actual,
            expected,
            "ResolverCases.IProxy",
            TypeKind.Interface);
    }

    [Theory]
    [InlineData(ProxyFailure.NullModel)]
    [InlineData(ProxyFailure.ClassFromTypeSyntax)]
    [InlineData(ProxyFailure.ClassFromFallback)]
    [InlineData(ProxyFailure.Missing)]
    public async Task TryResolveProxyInterface_WhenModelIsNullOrFallbackTargetIsClassOrTargetIsMissing_ReturnsFalseAndNull(
        ProxyFailure failure)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(ResolverSource, "ResolverConsumer");
        var resolver = new TypeSymbolResolver(compilation);
        var model = failure switch
        {
            ProxyFailure.NullModel => null,
            ProxyFailure.ClassFromTypeSyntax => CreateProxyModel(
                new TypeRef("global::ResolverCases.ProxyClass"),
                "ProxyClass"),
            ProxyFailure.ClassFromFallback => CreateProxyModel(
                TypeRef.Empty,
                "ProxyClass",
                new TypeMetadataIdentity(
                    "ResolverCases.ProxyClass",
                    "Missing.Assembly",
                    "Missing.Assembly, Version=9.9.9.9, Culture=neutral, PublicKeyToken=null")),
            ProxyFailure.Missing => CreateProxyModel(
                new TypeRef("global::ResolverCases.IMissingProxy"),
                "IMissingProxy"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };

        var success = resolver.TryResolveProxyInterface(
            model!,
            TestContext.Current.CancellationToken,
            out var actual);

        AssertFailedResolution(success, actual);
    }

    public enum ProxyFailure
    {
        NullModel,
        ClassFromTypeSyntax,
        ClassFromFallback,
        Missing,
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ResolveEntryPoint_WhenCancellationIsRequested_ThrowsOperationCanceledException(
        int entryPoint)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(ResolverSource, "ResolverConsumer");
        var resolver = new TypeSymbolResolver(compilation);
        var serializableModel = CreateSerializableModel(
            new TypeRef("global::ResolverCases.SourceType"),
            "ResolverCases",
            "SourceType");
        var proxyModel = CreateProxyModel(
            new TypeRef("global::ResolverCases.IProxy"),
            "IProxy");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
        {
            switch (entryPoint)
            {
                case 0:
                    resolver.TryResolveType(
                        new TypeRef("global::ResolverCases.SourceType"),
                        TypeMetadataIdentity.Empty,
                        cancellation.Token,
                        out _);
                    break;
                case 1:
                    resolver.TryResolveSerializableType(
                        serializableModel,
                        cancellation.Token,
                        out _);
                    break;
                case 2:
                    resolver.TryResolveProxyInterface(
                        proxyModel,
                        cancellation.Token,
                        out _);
                    break;
            }
        });
    }

    private static async Task<MetadataReference> CreateShadowedLibraryReference(
        string version,
        string alias)
    {
        var source = $$"""
            [assembly: System.Reflection.AssemblyVersion("{{version}}")]

            namespace ResolverCases;

            public class Shadowed
            {
            }

            public interface IShadowed
            {
            }
            """;
        var library = PublicSign(
            await TestCompilationHelper.CreateCompilation(source, "ResolverLibrary"));
        return MetadataReference.CreateFromImage(
            EmitToImage(library),
            new MetadataReferenceProperties(MetadataImageKind.Assembly, aliases: [alias]));
    }

    private static async Task<CSharpCompilation> CreateAmbiguousAssemblyConsumer()
    {
        var firstReference = await CreateShadowedLibraryReference("1.0.0.0", "First");
        var secondReference = await CreateShadowedLibraryReference("2.0.0.0", "Second");
        return await TestCompilationHelper.CreateCompilation(
            string.Empty,
            "ResolverConsumer",
            firstReference,
            secondReference);
    }

    private static SerializableTypeModel CreateSerializableModel(
        TypeRef typeSyntax,
        string @namespace,
        string name,
        int typeParameterCount = 0,
        TypeMetadataIdentity metadataIdentity = default)
    {
        EquatableArray<TypeParameterModel> typeParameters = typeParameterCount == 0
            ? EquatableArray<TypeParameterModel>.Empty
            : ImmutableArray.CreateRange(
                Enumerable.Range(0, typeParameterCount)
                    .Select(static ordinal => new TypeParameterModel($"T{ordinal}", $"T{ordinal}", ordinal)));

        return new SerializableTypeModel(
            Accessibility: Accessibility.Public,
            TypeSyntax: typeSyntax,
            HasComplexBaseType: false,
            IncludePrimaryConstructorParameters: false,
            BaseTypeSyntax: TypeRef.Empty,
            Namespace: @namespace,
            GeneratedNamespace: "Generated",
            Name: name,
            IsValueType: false,
            IsSealedType: false,
            IsAbstractType: false,
            IsEnumType: false,
            IsGenericType: typeParameterCount > 0,
            TypeParameters: typeParameters,
            Members: EquatableArray<MemberModel>.Empty,
            UseActivator: false,
            IsEmptyConstructable: false,
            HasActivatorConstructor: false,
            TrackReferences: false,
            OmitDefaultMemberValues: false,
            SerializationHooks: EquatableArray<TypeRef>.Empty,
            IsShallowCopyable: false,
            IsUnsealedImmutable: false,
            IsImmutable: false,
            IsExceptionType: false,
            ActivatorConstructorParameters: EquatableArray<TypeRef>.Empty,
            CreationStrategy: default,
            MetadataIdentity: metadataIdentity);
    }

    private static ProxyInterfaceModel CreateProxyModel(
        TypeRef interfaceType,
        string name,
        TypeMetadataIdentity metadataIdentity = default)
        => new(
            InterfaceType: interfaceType,
            Name: name,
            GeneratedNamespace: "Generated",
            TypeParameters: EquatableArray<TypeParameterModel>.Empty,
            ProxyBase: new ProxyBaseModel(TypeRef.Empty, false, string.Empty),
            Methods: EquatableArray<MethodModel>.Empty,
            MetadataIdentity: metadataIdentity);

    private static INamedTypeSymbol GetRequiredType(
        Compilation compilation,
        string metadataName)
    {
        var symbol = compilation.GetTypeByMetadataName(metadataName);
        Assert.NotNull(symbol);
        return symbol;
    }

    private static ImmutableArray<byte> EmitToImage(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics));
        return ImmutableArray.CreateRange(stream.ToArray());
    }

    private static CSharpCompilation PublicSign(CSharpCompilation compilation)
    {
        var publicKey = typeof(object).Assembly.GetName().GetPublicKey();
        Assert.NotNull(publicKey);
        return compilation.WithOptions(
            compilation.Options
                .WithCryptoPublicKey(ImmutableArray.CreateRange(publicKey))
                .WithPublicSign(true));
    }

    private static INamedTypeSymbol GetRequiredType(
        IAssemblySymbol assembly,
        string metadataName)
    {
        var symbol = assembly.GetTypeByMetadataName(metadataName);
        Assert.NotNull(symbol);
        return symbol;
    }

    private static void AssertSuccessfulResolution(
        bool success,
        INamedTypeSymbol? actual,
        INamedTypeSymbol expected,
        string expectedMetadataName,
        TypeKind expectedTypeKind)
    {
        Assert.True(success);
        Assert.NotNull(actual);
        Assert.True(SymbolEqualityComparer.Default.Equals(expected, actual));
        Assert.Equal(expectedMetadataName, TypeMetadataIdentity.Create(actual).MetadataName);
        Assert.Equal(expected.ContainingAssembly.Identity, actual.ContainingAssembly.Identity);
        Assert.Equal(expectedTypeKind, actual.TypeKind);
    }

    private static void AssertFailedResolution(bool success, INamedTypeSymbol? actual)
    {
        Assert.False(success);
        Assert.Null(actual);
    }
}
