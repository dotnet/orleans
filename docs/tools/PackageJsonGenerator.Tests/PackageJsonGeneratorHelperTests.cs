// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace PackageJsonGenerator.Tests;

public sealed class PackageJsonGeneratorHelperTests
{
    [Theory]
    [InlineData("public sealed class Widget;")]
    [InlineData("public string Name { get; set; }")]
    [InlineData("ValueTask<string> GetValue();")]
    [InlineData("await DoWorkAsync();")]
    [InlineData("new Widget()")]
    [InlineData("[Obsolete]")]
    public void CSharpFenceSyntaxValidator_AcceptsCommonFragmentContexts(string source)
    {
        Assert.True(CSharpFenceSyntaxCommand.IsValidInAnyContext(source));
    }

    [Theory]
    [InlineData("public class")]
    [InlineData("if (...)")]
    [InlineData("value ??? fallback")]
    public void CSharpFenceSyntaxValidator_RejectsInvalidFragments(string source)
    {
        Assert.False(CSharpFenceSyntaxCommand.IsValidInAnyContext(source));
    }

    [Theory]
    [InlineData("/_/src/Orleans.Core/Foo.cs", "src/Orleans.Core/Foo.cs")]
    [InlineData("/agent/_work/orleans/src/Orleans.Core/Foo.cs", "src/Orleans.Core/Foo.cs")]
    [InlineData(@"C:\agent\_work\orleans\src\Orleans.Core\Foo.cs", "src/Orleans.Core/Foo.cs")]
    [InlineData("/home/runner/work/generated/Foo.cs", "Foo.cs")]
    [InlineData(@"generated\Foo.cs", "generated/Foo.cs")]
    public void CleanPath_ReturnsRepositoryRelativePath(string path, string expected)
    {
        Assert.Equal(expected, global::PackageJsonGenerator.Helpers.PdbSourceReader.CleanPath(path));
    }

    [Fact]
    public void EmitAssemblySchema_PreservesTheCompatibleMinimalSchema()
    {
        var type = new global::PackageJsonGenerator.Helpers.CanonicalType
        {
            Name = "Widget",
            FullName = "Sample.Library.Widget",
            Namespace = "Sample.Library",
            Kind = "class",
            Accessibility = "public",
            IsSealed = true,
            SourceFile = "src/Sample.Library/Widget.cs",
            SourceLines = "5-5",
            Members =
            [
                new global::PackageJsonGenerator.Helpers.CanonicalMember
                {
                    Name = "Run",
                    Kind = "method",
                    Accessibility = "public",
                    Signature = "public void Widget.Run()",
                    ReturnType = "void",
                    SourceFile = "src/Sample.Library/Widget.cs",
                    SourceLines = "7-9",
                },
            ],
        };

        var schema = global::PackageJsonGenerator.Helpers.SchemaEmitter.EmitAssemblySchema(
            "Sample.Package",
            "1.2.3",
            "net10.0",
            [type],
            "https://github.com/dotnet/orleans",
            "abc123");

        var expected = """
            {
              "package": {
                "name": "Sample.Package",
                "version": "1.2.3",
                "targetFramework": "net10.0",
                "sourceRepository": "https://github.com/dotnet/orleans",
                "sourceCommit": "abc123"
              },
              "types": [
                {
                  "name": "Widget",
                  "fullName": "Sample.Library.Widget",
                  "namespace": "Sample.Library",
                  "kind": "class",
                  "accessibility": "public",
                  "isSealed": true,
                  "sourceFile": "src/Sample.Library/Widget.cs",
                  "sourceLines": "5-5",
                  "members": [
                    {
                      "name": "Run",
                      "kind": "method",
                      "accessibility": "public",
                      "signature": "public void Widget.Run()",
                      "returnType": "void",
                      "sourceFile": "src/Sample.Library/Widget.cs",
                      "sourceLines": "7-9"
                    }
                  ]
                }
              ]
            }
            """;

        Assert.Equal(NormalizeLineEndings(expected), NormalizeLineEndings(schema));
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

    [Fact]
    public void BuildRawGitHubUrl_StripsGitSuffix()
    {
        var rawUrl = PackageJsonGenerator.BuildRawGitHubUrl(
            "https://github.com/dotnet/orleans.git",
            "abc123",
            "src/Orleans.Core/Foo.cs");

        Assert.Equal(
            "https://raw.githubusercontent.com/dotnet/orleans/abc123/src/Orleans.Core/Foo.cs",
            rawUrl);
    }

    [Fact]
    public void BuildRawGitHubUrl_ReturnsNullForNonGitHubRepositories()
    {
        var rawUrl = PackageJsonGenerator.BuildRawGitHubUrl(
            "https://example.com/org/repo",
            "abc123",
            "src/Foo.cs");

        Assert.Null(rawUrl);
    }

    [Theory]
    [InlineData("https://github.com/dotnet/orleans")]
    [InlineData("https://github.com/dotnet/orleans.git")]
    [InlineData(" https://github.com/DOTNET/ORLEANS.git ")]
    public void NormalizeSourceRepository_ReturnsCanonicalOrleansUrl(string sourceRepository)
    {
        Assert.Equal(
            "https://github.com/dotnet/orleans",
            PackageJsonGenerator.NormalizeSourceRepository(sourceRepository));
    }

    [Fact]
    public void FindTypeDeclarationLine_PrefersClosestMatchingDeclaration()
    {
        var lines = new[]
        {
            "// class Widget",
            "public sealed class WidgetBuilder",
            "",
            "public sealed class Widget",
            "{",
            "}",
            "",
            "public sealed class Widget",
            "{",
            "}",
        };

        var declarationLine = PackageJsonGenerator.FindTypeDeclarationLine(lines, "Widget", pdbHintLine: 8);

        Assert.Equal(8, declarationLine);
    }

    [Theory]
    [InlineData("42-42", 42)]
    [InlineData("42-99", 42)]
    [InlineData(null, 0)]
    [InlineData("invalid", 0)]
    public void ParseStartLine_ParsesExpectedValue(string? sourceLines, int expected)
    {
        Assert.Equal(expected, PackageJsonGenerator.ParseStartLine(sourceLines));
    }
}
