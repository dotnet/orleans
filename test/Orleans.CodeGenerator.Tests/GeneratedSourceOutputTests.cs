using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Tests;

public class GeneratedSourceOutputTests
{
    [Fact]
    public void EmptyOutputsRemainEmpty()
    {
        Assert.Empty(GeneratedSourceOutput.DeduplicateSourceOutputs(ImmutableArray<SourceOutputResult>.Empty));
        Assert.Empty(GeneratedSourceOutput.DeduplicateSourceOutputs(ImmutableArray.CreateBuilder<SourceOutputResult>()));
    }

    [Fact]
    public void UniqueHintsPreserveOrderDiagnosticsAndExactSourceText()
    {
        var diagnostic = Diagnostic.Create(
            new DiagnosticDescriptor("TEST001", "Title", "Message", "Test", DiagnosticSeverity.Warning, true),
            Location.None);
        ImmutableArray<SourceOutputResult> entries =
        [
            new(new GeneratedSourceEntry("z.g.cs", "#pragma warning disable\r\nclass Z { }\r\n"), diagnostic),
            SourceOutputResult.FromDiagnostic(diagnostic),
            Source("a.g.cs", "\n// Leading and trailing whitespace  \n"),
            default,
            Source("A.g.cs", "\n// Leading and trailing whitespace  \n"),
        ];

        var actual = GeneratedSourceOutput.DeduplicateSourceOutputs(entries);

        Assert.Equal(entries.ToArray(), actual.ToArray());
    }

    [Fact]
    public void RepeatedSourcesAreDeduplicatedByHintAndOrdinalContent()
    {
        var first = Source("first.g.cs", "class First { }");
        var second = Source("second.g.cs", "class Second { }");
        ImmutableArray<SourceOutputResult> entries =
        [
            first,
            second,
            Source("first.g.cs", new string("class First { }".ToCharArray())),
            Source("First.g.cs", "class First { }"),
            second,
        ];

        var actual = GeneratedSourceOutput.DeduplicateSourceOutputs(entries);

        Assert.Equal(new[] { first, second, entries[3] }, actual.ToArray());
    }

    [Theory]
    [InlineData("same.g.cs", "same.collision.", ".g.cs")]
    [InlineData("same.cs", "same.collision.", ".cs")]
    [InlineData("same", "same.collision.", "")]
    public void CollidingHintsPreserveFirstOccurrenceAndDisambiguateDistinctSources(
        string hintName, string collisionPrefix, string suffix)
    {
        const string firstText = "class First { }\r\n";
        const string secondText = "class Second { }\n";
        const string thirdText = "class second { }\n";
        var first = Source(hintName, firstText);
        var second = Source(hintName, secondText);
        var third = Source(hintName, thirdText);
        ImmutableArray<SourceOutputResult> entries = [first, second, first, third, second, third];
        SourceOutputResult[] expected =
        [
            first,
            Source($"{collisionPrefix}{GeneratedSourceOutput.CreateStableHash(secondText)}{suffix}", secondText),
            Source($"{collisionPrefix}{GeneratedSourceOutput.CreateStableHash(thirdText)}{suffix}", thirdText),
        ];

        Assert.Equal(expected, GeneratedSourceOutput.DeduplicateSourceOutputs(entries).ToArray());
        Assert.Equal(expected, GeneratedSourceOutput.DeduplicateSourceOutputs(entries.ToBuilder()).ToArray());
    }

    [Fact]
    public void CollisionHintsSkipOccupiedNames()
    {
        const string secondText = "class Second { }";
        var prefix = $"same.collision.{GeneratedSourceOutput.CreateStableHash(secondText)}";
        var first = Source("same.g.cs", "class First { }");
        var reserved = Source($"{prefix}.g.cs", "class Reserved { }");
        var reservedNumbered = Source($"{prefix}.1.g.cs", "class ReservedNumbered { }");
        var second = Source("same.g.cs", secondText);
        ImmutableArray<SourceOutputResult> entries = [reserved, first, reservedNumbered, second, second, first];

        var actual = GeneratedSourceOutput.DeduplicateSourceOutputs(entries);

        Assert.Equal(
            new[] { reserved, first, reservedNumbered, Source($"{prefix}.2.g.cs", secondText) },
            actual.ToArray());
    }

    [Fact]
    public void OriginalHintsCanMatchPreviouslyDisambiguatedHints()
    {
        const string firstText = "class First { }";
        const string secondText = "class Second { }";
        const string thirdText = "class Third { }";
        var secondHash = GeneratedSourceOutput.CreateStableHash(secondText);
        var thirdHash = GeneratedSourceOutput.CreateStableHash(thirdText);
        var disambiguatedHint = $"same.collision.{secondHash}.g.cs";
        var first = Source("same.g.cs", firstText);
        var second = Source("same.g.cs", secondText);
        var nestedCollision = Source(disambiguatedHint, thirdText);
        ImmutableArray<SourceOutputResult> entries =
        [
            first,
            second,
            Source(disambiguatedHint, secondText),
            nestedCollision,
            second,
            Source("same.g.cs", thirdText),
            nestedCollision,
        ];

        var actual = GeneratedSourceOutput.DeduplicateSourceOutputs(entries);

        Assert.Equal(
            new[]
            {
                first,
                Source(disambiguatedHint, secondText),
                Source($"same.collision.{secondHash}.collision.{thirdHash}.g.cs", thirdText),
                Source($"same.collision.{thirdHash}.g.cs", thirdText),
            },
            actual.ToArray());
    }

    private static SourceOutputResult Source(string hintName, string source)
        => SourceOutputResult.FromSource(new GeneratedSourceEntry(hintName, source));
}
