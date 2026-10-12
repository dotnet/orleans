using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Analyzer")]
public class TypeUnifierTests
{
    private const string Source = """
        public class Pair<A, B> { }
        public class Box<A> { }
        public class Defaultless { private Defaultless() { } }
        public class Cases<T, U, TClass, TStruct, TNew, TComparable, TStream, TMemoryStream, TException>
            where TClass : class
            where TStruct : struct
            where TNew : new()
            where TComparable : System.IComparable
            where TStream : System.IO.Stream
            where TMemoryStream : System.IO.MemoryStream
            where TException : System.Exception
        {
            public Box<T> BoxT;
            public Box<U> BoxU;
            public Box<int> BoxInt;
            public Box<string> BoxString;
            public Box<object> BoxObject;
            public Box<int?> BoxNullableInt;
            public Box<Defaultless> BoxDefaultless;
            public Box<Box<T>> BoxBoxT;
            public Box<TClass> BoxClass;
            public Box<TStruct> BoxStruct;
            public Box<TNew> BoxNew;
            public Box<TComparable> BoxComparable;
            public Box<TStream> BoxStream;
            public Box<TMemoryStream> BoxMemoryStream;
            public Box<TException> BoxException;
            public Pair<TClass, int> PairClassInt;
            public Pair<TClass, T> PairClassT;
            public Pair<T, TStruct> PairTStruct;
            public Pair<T, T> PairTT;
            public Pair<T, U> PairTU;
            public Pair<U, int> PairUInt;
            public Pair<int, int> PairIntInt;
            public Pair<int, string> PairIntString;
            public Pair<T, Box<T>> PairTBoxT;
            public Pair<Box<U>, U> PairBoxUU;
        }
        """;

    private static readonly Compilation Compilation = CSharpCompilation.Create(
        "TypeUnifierTests",
        [CSharpSyntaxTree.ParseText(Source)],
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(static path => MetadataReference.CreateFromFile(path)),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    [Theory]
    [InlineData("BoxT", "BoxU", true)]
    [InlineData("BoxT", "BoxInt", true)]
    [InlineData("BoxInt", "BoxString", false)]
    [InlineData("PairTT", "PairIntInt", true)]
    [InlineData("PairTT", "PairIntString", false)]
    [InlineData("PairTU", "PairUInt", true)]
    [InlineData("BoxT", "BoxBoxT", false)]
    [InlineData("PairTBoxT", "PairBoxUU", false)]
    [InlineData("BoxClass", "BoxString", true)]
    [InlineData("BoxClass", "BoxInt", false)]
    [InlineData("BoxStruct", "BoxInt", true)]
    [InlineData("BoxStruct", "BoxString", false)]
    [InlineData("BoxStruct", "BoxNullableInt", false)]
    [InlineData("BoxNew", "BoxObject", true)]
    [InlineData("BoxNew", "BoxDefaultless", false)]
    [InlineData("BoxComparable", "BoxString", true)]
    [InlineData("BoxComparable", "BoxObject", false)]
    [InlineData("BoxClass", "BoxStruct", false)]
    [InlineData("BoxClass", "BoxStream", true)]
    [InlineData("BoxStruct", "BoxStream", false)]
    [InlineData("BoxStream", "BoxMemoryStream", true)]
    [InlineData("BoxStream", "BoxException", false)]
    [InlineData("PairTT", "PairClassInt", false)]
    [InlineData("PairClassT", "PairTStruct", false)]
    public void CanUnifyRespectsConsistentSubstitutionsAndConstraints(string first, string second, bool expected)
    {
        var firstType = GetFieldType(first);
        var secondType = GetFieldType(second);

        Assert.Equal(expected, TypeUnifier.CanUnify(Compilation, firstType, secondType));
        Assert.Equal(expected, TypeUnifier.CanUnify(Compilation, secondType, firstType));
    }

    [Fact]
    public void TestSourceCompiles() => Assert.Empty(Compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

    private static ITypeSymbol GetFieldType(string name)
        => Compilation.GetTypeByMetadataName("Cases`9")!.GetMembers(name).OfType<IFieldSymbol>().Single().Type;
}
