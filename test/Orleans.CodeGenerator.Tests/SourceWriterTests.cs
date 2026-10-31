using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Tests the direct source writer and the lexical fragments which it composes.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class SourceWriterTests
{
    [Fact]
    public void WriterSeparatesClosedBlocksFromFollowingStatements()
    {
        var writer = new SourceWriter();
        writer.WriteLine("void M()");
        writer.OpenBlock();
        writer.WriteLine("if (a)");
        writer.OpenBlock();
        writer.WriteLine("x();");
        writer.CloseBlock();
        writer.ContinueStatement();
        writer.WriteLine("else");
        writer.Indent();
        writer.WriteLine("y();");
        writer.Unindent();
        writer.WriteLine("while (true)");
        writer.OpenBlock();
        writer.WriteLine("break;");
        writer.CloseBlock();
        writer.WriteLine("return;");
        writer.CloseBlock();

        const string expected = "void M()\r\n{\r\n    if (a)\r\n    {\r\n        x();\r\n    }\r\n    else\r\n        y();\r\n    while (true)\r\n    {\r\n        break;\r\n    }\r\n\r\n    return;\r\n}";
        Assert.Equal(expected, writer.ToString());
        Assert.Equal(expected, Normalize(expected, static text => ParseMemberDeclaration(text)!));
    }

    [Fact]
    public void WriterEmitsFragmentsAtTheirOwnIndentation()
    {
        var fragment = new SourceWriter(initialIndentation: 2);
        fragment.WriteLine("a();");
        fragment.OpenBlock();
        fragment.WriteLine("b();");
        fragment.CloseBlock();

        var writer = new SourceWriter();
        writer.WriteLine("class C");
        writer.OpenBlock();
        writer.WriteLine("void M()");
        writer.OpenBlock();
        writer.WriteFragment(fragment.ToString());
        writer.CloseBlock();
        writer.CloseBlock();

        var expected = "class C\r\n{\r\n    void M()\r\n    {\r\n        a();\r\n        {\r\n            b();\r\n        }\r\n    }\r\n}";
        Assert.Equal(expected, writer.ToString());
        Assert.Equal(expected, Normalize(expected, static text => ParseCompilationUnit(text)));
    }

    [Theory]
    [InlineData("int", "int")]
    [InlineData("global::System.Collections.Generic.Dictionary<int,string>", "global::System.Collections.Generic.Dictionary<int, string>")]
    [InlineData("global::System.Collections.Generic.Dictionary< int , global::System.Collections.Generic.List<string[]> >", "global::System.Collections.Generic.Dictionary<int, global::System.Collections.Generic.List<string[]>>")]
    [InlineData("global::Outer<T>.Inner<U,V>", "global::Outer<T>.Inner<U, V>")]
    [InlineData("global::@class.@namespace<@event>", "global::@class.@namespace<@event>")]
    [InlineData("int[ , ]", "int[,]")]
    [InlineData("int[,,][]", "int[,,][]")]
    [InlineData("int ?", "int?")]
    [InlineData("int? []", "int?[]")]
    [InlineData("int?[,][]", "int?[,][]")]
    [InlineData("(int a,string b)", "(int a, string b)")]
    [InlineData("(int[]items,int?count,global::System.Collections.Generic.List<int>values,int[,]grid)", "(int[] items, int? count, global::System.Collections.Generic.List<int> values, int[,] grid)")]
    [InlineData("global::System.ValueTuple<(int, string),int?>", "global::System.ValueTuple<(int, string), int?>")]
    [InlineData("A<, , >", "A<,,>")]
    [InlineData("A<,>", "A<,>")]
    [InlineData("A<>", "A<>")]
    [InlineData("global::A<>.B<,>", "global::A<>.B<,>")]
    [InlineData("int *", "int*")]
    [InlineData("delegate*<int,void>", "delegate*<int, void>")]
    [InlineData("dynamic", "dynamic")]
    public void TypeSpellingIsIdiomatic(string type, string expected)
    {
        var syntax = ParseTypeName(type);
        var spelled = CodeText.Type(syntax);
        Assert.Equal(expected, spelled);
        Assert.True(AreEquivalent(syntax, ParseTypeName(spelled), topLevel: false));
    }

    [Fact]
    public void ComposedTypeSpellingIsIdiomatic()
    {
        // Composed names have no trivia; their spelling still separates written arguments and joins omitted ones.
        (TypeSyntax Type, string Expected)[] cases =
        [
            (QualifiedName(ParseName("global::N"), GenericName(Identifier("G"), TypeArgumentList(SeparatedList<TypeSyntax>(
                [IdentifierName("global::System.Int32"), PredefinedType(Token(SyntaxKind.StringKeyword)), IdentifierName("T")])))),
                "global::N.G<global::System.Int32, string, T>"),
            (QualifiedName(ParseName("N"), GenericName(Identifier("Invokable_I"), TypeArgumentList(SeparatedList<TypeSyntax>(
                [OmittedTypeArgument(), OmittedTypeArgument(), OmittedTypeArgument()])))),
                "N.Invokable_I<,,>"),
            (NullableType(ParseTypeName("global::System.TimeSpan")), "global::System.TimeSpan?"),
            (ArrayType(PredefinedType(Token(SyntaxKind.IntKeyword)), SingletonList(ArrayRankSpecifier(SeparatedList<ExpressionSyntax>(
                [OmittedArraySizeExpression(), OmittedArraySizeExpression()])))),
                "int[,]"),
            (TupleType(SeparatedList([TupleElement(ArrayType(PredefinedType(Token(SyntaxKind.IntKeyword)), SingletonList(ArrayRankSpecifier())), Identifier("items")),
                TupleElement(NullableType(PredefinedType(Token(SyntaxKind.IntKeyword))), Identifier("count"))])),
                "(int[] items, int? count)"),
        ];

        foreach (var (type, expected) in cases)
        {
            Assert.Equal(expected, CodeText.Type(type));
        }
    }
    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("quote\" backslash\\ tab\t newline\n return\r nul\0 bell\a")]
    [InlineData("unicode é 中文 \U0001F600 \u2028 \u0085 \uFEFF")]
    [InlineData("{interpolation-like} and \"verbatim\"")]
    public void StringLiteralsMatchRoslynLiteralSpelling(string value)
    {
        var spelled = CodeText.StringLiteral(value);
        Assert.Equal(Literal(value).Text, spelled);
        var parsed = Assert.IsType<LiteralExpressionSyntax>(ParseExpression(spelled));
        Assert.Equal(value, parsed.Token.ValueText);
    }

    [Theory]
    [InlineData("value", "@value")]
    [InlineData("@class", "@class")]
    [InlineData("class", "@class")]
    [InlineData("Value", "Value")]
    [InlineData("_field0", "_field0")]
    public void IdentifiersEscapeKeywords(string identifier, string expected)
    {
        Assert.Equal(expected, CodeText.Identifier(identifier));
        Assert.Equal(identifier.ToIdentifierName().ToString(), CodeText.Identifier(identifier));
    }

    [Fact]
    public void NumericLiteralsUseInvariantSpellingInEveryCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "sv-SE", "ar-SA", "fa-IR", "de-DE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var value in new[] { 0U, 1U, uint.MaxValue })
                {
                    Assert.Equal(Literal(value).Text, CodeText.Literal(value));
                }

                foreach (var value in new[] { 0, -1, int.MinValue, int.MaxValue })
                {
                    Assert.Equal(Literal(value).Text, CodeText.Literal(value));
                }

                foreach (var value in new[] { 0L, -1L, long.MinValue, long.MaxValue, TimeSpan.FromDays(10000).Ticks })
                {
                    Assert.Equal(Literal(value).Text, CodeText.Literal(value));
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(1, "G<>")]
    [InlineData(2, "G<,>")]
    [InlineData(5, "G<,,,,>")]
    public void OpenGenericArgumentsAreIdiomatic(int arity, string expected)
        => Assert.Equal(expected, string.Concat("G", CodeText.OpenGenericArguments(arity)));
    private static string Normalize(string text, Func<string, SyntaxNode> parse) => parse(text).NormalizeWhitespace().ToFullString();
}
