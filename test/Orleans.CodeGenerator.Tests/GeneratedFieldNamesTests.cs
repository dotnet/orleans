using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

public class GeneratedFieldNamesTests
{
    [Theory]
    [InlineData(0u, false, "_0")]
    [InlineData(7u, false, "_7")]
    [InlineData(3u, true, "_3_ctor")]
    public async Task AccessorNamesAreKeyedByFieldIdAndConstructorParameterKind(uint fieldId, bool isCtorParameter, string expectedSuffix)
    {
        var compilation = await Compile("");
        var member = new FakeMember(compilation.GetSpecialType(SpecialType.System_Int32), fieldId, isCtorParameter);

        Assert.Equal($"setField{expectedSuffix}", GeneratedFieldNames.Accessor("setField", member));
        Assert.Equal($"getField{expectedSuffix}", GeneratedFieldNames.Accessor("getField", member));
    }

    [Fact]
    public async Task TypeKeyedNamesAreReadableForCommonShapes()
    {
        var compilation = await Compile("");
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var members = new List<IMemberDescription>
        {
            new FakeMember(intType, 0),
            new FakeMember(compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!.Construct(intType), 1),
            new FakeMember(compilation.CreateArrayTypeSymbol(intType), 2),
            new FakeMember(compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2")!.Construct(compilation.GetSpecialType(SpecialType.System_String), intType), 3),
        };

        var names = GeneratedFieldNames.ForTypes("_codec", members);
        Assert.Collection(
            names,
            name => Assert.Matches("^_codec_Int32_[0-9A-F]{16}$", name),
            name => Assert.Matches("^_codec_List_Int32_[0-9A-F]{16}$", name),
            name => Assert.Matches("^_codec_Int32_1_[0-9A-F]{16}$", name),
            name => Assert.Matches("^_codec_Dictionary_String_Int32_[0-9A-F]{16}$", name));
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task CollidingSimpleNamesGetOrderIndependentHashSuffixes()
    {
        var compilation = await Compile("""
            namespace First { public class Item { } }
            namespace Second { public class Item { } }
            """);
        var first = new FakeMember(compilation.GetTypeByMetadataName("First.Item")!, 0);
        var second = new FakeMember(compilation.GetTypeByMetadataName("Second.Item")!, 1);

        var names = GeneratedFieldNames.ForTypes("_copier", [first, second]);
        var reversed = GeneratedFieldNames.ForTypes("_copier", [second, first]);

        Assert.All(names, name => Assert.Matches("^_copier_Item_[0-9A-F]{16}$", name));
        Assert.NotEqual(names[0], names[1]);
        Assert.Equal(names[0], reversed[1]);
        Assert.Equal(names[1], reversed[0]);
    }

    [Fact]
    public async Task AddingCollidingSimpleNameDoesNotRenameExistingField()
    {
        var compilation = await Compile("""
            namespace First { public class Item { } }
            namespace Second { public class Item { } }
            """);
        var first = new FakeMember(compilation.GetTypeByMetadataName("First.Item")!, 0);
        var second = new FakeMember(compilation.GetTypeByMetadataName("Second.Item")!, 1);

        var originalName = Assert.Single(GeneratedFieldNames.ForTypes("_copier", [first]));
        var namesWithCollision = GeneratedFieldNames.ForTypes("_copier", [first, second]);

        Assert.Equal(originalName, namesWithCollision[0]);
        Assert.NotEqual(namesWithCollision[0], namesWithCollision[1]);
    }

    [Fact]
    public async Task UnspeakableTypesFallBackToHashOnlyNames()
    {
        var compilation = await Compile("");
        var pointer = compilation.CreatePointerTypeSymbol(compilation.GetSpecialType(SpecialType.System_Int32));
        var members = new List<IMemberDescription> { new FakeMember(pointer, 0) };

        var name = Assert.Single(GeneratedFieldNames.ForTypes("_codec", members));
        Assert.Matches("^_codec_Type_[0-9A-F]{16}$", name);

        Assert.Equal(name, Assert.Single(GeneratedFieldNames.ForTypes("_codec", members)));
    }

    private static Task<CSharpCompilation> Compile(string source) => TestCompilationHelper.CreateCompilation(source);

    private sealed class FakeMember(ITypeSymbol type, uint fieldId, bool isPrimaryConstructorParameter = false) : IMemberDescription
    {
        public uint FieldId => fieldId;
        public ISymbol Symbol => type;
        public ITypeSymbol Type => type;
        public INamedTypeSymbol ContainingType => throw new NotSupportedException();
        public string AssemblyName => type.ContainingAssembly?.Name ?? "";
        public string TypeName => type.ToDisplayString();
        public TypeSyntax TypeSyntax => throw new NotSupportedException();
        public string TypeNameIdentifier => throw new NotSupportedException();
        public TypeSyntax GetTypeSyntax(ITypeSymbol typeSymbol) => throw new NotSupportedException();
        public bool IsPrimaryConstructorParameter => isPrimaryConstructorParameter;
        public bool IsSerializable => true;
        public bool IsCopyable => true;
    }
}
