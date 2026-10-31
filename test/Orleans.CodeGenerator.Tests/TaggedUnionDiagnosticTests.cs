// net10.0 tests reference Roslyn 5.9, which parses closed types and unions; net8.0 tests reference the oldest supported Roslyn.
#if NET10_0_OR_GREATER
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Xunit;
using static Orleans.CodeGenerator.Tests.TaggedUnionTestCompilation;
using static VerifyXunit.Verifier;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Tests the source generator's ORLEANS0116 diagnostics for closed type hierarchies and union types.
/// </summary>
/// <remarks>
/// These tests compile closed and union types with <see cref="LanguageVersion.Preview"/>. Union diagnostics which depend on union case types
/// require a compiler which exposes them, so the tests cover the union diagnostics which are available with the referenced compiler.
/// </remarks>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class TaggedUnionDiagnosticTests
{
    [Fact]
    public void ValidClosedHierarchyGeneratesCompilableCodec()
    {
        var (output, result) = Generate("""
            [GenerateSerializer] public closed record Shape<T>;
            [GenerateSerializer, Case(0)] public sealed record Circle<T>([property: Id(0)] T Radius) : Shape<T>;
            [GenerateSerializer, Case(1)] public sealed record Square<X>([property: Id(0)] X Side) : Shape<X>;
            public static class Outer
            {
                [GenerateSerializer] public closed record NestedBase<T>;
            }
            [GenerateSerializer, Case(0)] public sealed record Item<T>([property: Id(0)] T Value) : Outer.NestedBase<T>;
            """);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0116");
        AssertGeneratesTaggedUnionCodec(result);
        AssertNoErrors(output);
    }

    [Fact]
    public void NullabilityConstraintsOfCasesDoNotAffectGeneratedCodec()
    {
        // Nullability is not part of a type's runtime identity, and generated code has no nullable context, so these cases are supported.
        var (output, result) = Generate("""
            [GenerateSerializer] public closed record NotNullBase<T>;
            [GenerateSerializer, Case(0)] public sealed record NotNullCase<T>([property: Id(0)] T Value) : NotNullBase<T> where T : notnull;
            [GenerateSerializer] public closed record NullableClassBase<T> where T : class?;
            [GenerateSerializer, Case(0)] public sealed record ClassCase<T>([property: Id(0)] T Value) : NullableClassBase<T> where T : class;
            """);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0116");
        AssertGeneratesTaggedUnionCodec(result);
        AssertNoErrors(output);
    }

    [Fact]
    public void PermutedCaseParametersAndConstraintsGenerateCompilableCodec()
    {
        var (output, result) = Generate("""
            [GenerateSerializer] public closed record Pair<T, U> where T : class where U : struct;
            [GenerateSerializer, Case(7)] internal sealed record Item<X, Y>([property: Id(0)] X First, [property: Id(1)] Y Second)
                : Pair<Y, X> where X : struct where Y : class;
            [GenerateSerializer] public closed record Related<T, U> where T : U;
            [GenerateSerializer, Case(0)] public sealed record RelatedItem<X, Y>([property: Id(0)] Y Value)
                : Related<Y, X> where Y : X;
            """);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0116");
        AssertNoErrors(output);
        Assert.Contains("global::Item<U, T>", GetCodec(result, "Codec_Pair").NormalizeWhitespace().ToFullString(), StringComparison.Ordinal);
        Assert.Contains("global::RelatedItem<U, T>", GetCodec(result, "Codec_Related").NormalizeWhitespace().ToFullString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("class", "class")]
    [InlineData("struct", "struct")]
    [InlineData("unmanaged", "unmanaged")]
    [InlineData("new()", "new()")]
    [InlineData("System.IComparable<T>", "System.IComparable<X>")]
    [InlineData("System.Collections.Generic.IEnumerable<T[]>", "System.Collections.Generic.IEnumerable<X[]>")]
    [InlineData("System.Collections.Generic.IEnumerable<T[,]>", "System.Collections.Generic.IEnumerable<X[,]>")]
    public void MatchingCaseConstraintsGenerateCompilableCodec(string baseConstraint, string caseConstraint)
    {
        var (output, result) = Generate($$"""
            [GenerateSerializer] public closed record Base<T> where T : {{baseConstraint}};
            [GenerateSerializer, Case(0)] public sealed record Item<X>([property: Id(0)] X Value) : Base<X> where X : {{caseConstraint}};
            """);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0116");
        AssertGeneratesTaggedUnionCodec(result);
        AssertNoErrors(output);
    }

    [Theory]
    [InlineData("class")]
    [InlineData("struct")]
    [InlineData("unmanaged")]
    [InlineData("new()")]
    [InlineData("System.IComparable<X>")]
    [InlineData("System.Collections.Generic.IEnumerable<X[]>")]
    public void StrongerCaseConstraintsProduceGeneratorErrors(string constraint)
    {
        var (_, result) = Generate($$"""
            [GenerateSerializer] public closed record Base<T>;
            [GenerateSerializer, Case(0)] public sealed record Item<X>([property: Id(0)] X Value) : Base<X> where X : {{constraint}};
            """);

        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0116");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("has type parameter constraints which closed type 'Base<T>' does not declare", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnionMembersWithoutGeneratedSerializerRespectCopyPolicy(bool immutable)
    {
        var (output, result) = Generate($$"""
            {{(immutable ? "[Immutable]" : "")}}
            public union CustomUnion(int, string);
            [GenerateSerializer] public sealed class Holder
            {
                [Id(0)] public CustomUnion Value { get; set; }
            }
            """);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0116");
        AssertNoErrors(output);
        var copier = GetCodec(result, "Copier_Holder").NormalizeWhitespace().ToFullString();
        if (immutable)
        {
            Assert.DoesNotContain("IDeepCopier<global::CustomUnion>", copier, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("IDeepCopier<global::CustomUnion>", copier, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Constraints<T>.INested", "Constraints<X>.INested")]
    [InlineData("Constraints<T>.INested<int>", "Constraints<X>.INested<int>")]
    [InlineData("Constraints<int>.INested<T>", "Constraints<int>.INested<X>")]
    [InlineData("Constraints<int>.INested<string>", "Constraints<int>.INested<string>")]
    [InlineData("Constraints<T>.Middle.INested<T>", "Constraints<X>.Middle.INested<X>")]
    public void MatchingNestedGenericConstraintsGenerateCompilableCodec(string baseConstraint, string caseConstraint)
    {
        var (output, result) = Generate($$"""
            public class Constraints<T>
            {
                public interface INested { }
                public interface INested<U> { }
                public class Middle
                {
                    public interface INested<U> { }
                }
            }
            [GenerateSerializer] public closed record Base<T> where T : {{baseConstraint}};
            [GenerateSerializer, Case(0)] public sealed record Item<X>([property: Id(0)] X Value) : Base<X> where X : {{caseConstraint}};
            """);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0116");
        AssertGeneratesTaggedUnionCodec(result);
        AssertNoErrors(output);
    }

    [Theory]
    [InlineData(
        """
        [GenerateSerializer] public closed record Base;
        [GenerateSerializer, Case(0)] public sealed record First : Base;
        [GenerateSerializer] public sealed record Second : Base;
        """,
        "'Second' derives from closed type 'Base', which uses case identifiers")]
    [InlineData(
        """
        [GenerateSerializer] public closed record Base;
        [GenerateSerializer, Case(1)] public sealed record First : Base;
        [GenerateSerializer, Case(1)] public sealed record Second : Base;
        """,
        "Case identifier 1 is used by both 'First' and 'Second' in 'Base'")]
    [InlineData(
        """
        [GenerateSerializer] public closed record Base;
        [GenerateSerializer, Case(0)] public sealed record Box<T>([property: Id(0)] T Item) : Base;
        """,
        "Case 'Box<T>' must pass its own type parameters as the type arguments of 'Base'")]
    [InlineData(
        """
        [GenerateSerializer] public closed record Base;
        public static class Outer<T>
        {
            [GenerateSerializer, Case(0)] public sealed record Box([property: Id(0)] T Item) : Base;
        }
        """,
        "Case 'Outer<T>.Box' must pass its own type parameters as the type arguments of 'Base'")]
    [InlineData(
        """
        public static class Outer<T>
        {
            [GenerateSerializer] public closed record Base<U>;
        }
        [GenerateSerializer, Case(0)] public sealed record Item<U>([property: Id(0)] U Value) : Outer<int>.Base<U>;
        """,
        "Case 'Item<U>' must pass its own type parameters as the type arguments of 'Outer<T>.Base<U>'")]
    [InlineData(
        """
        public static class Outer<T>
        {
            [GenerateSerializer] public closed record Base;
        }
        [GenerateSerializer, Case(0)] public sealed record Item([property: Id(0)] int Value) : Outer<int>.Base;
        """,
        "Case 'Item' must pass its own type parameters as the type arguments of 'Outer<T>.Base'")]
    [InlineData(
        """
        [GenerateSerializer] public closed record Shape<T>;
        [GenerateSerializer, Case(0)] public sealed record IntSquare([property: Id(0)] int Side) : Shape<int>;
        """,
        "Case 'IntSquare' must pass its own type parameters as the type arguments of 'Shape<T>'")]
    [InlineData(
        """
        [GenerateSerializer] public closed record Shape<T>;
        [GenerateSerializer, Case(0)] public sealed record ListShape<U>([property: Id(0)] U Item) : Shape<System.Collections.Generic.List<U>>;
        """,
        "Case 'ListShape<U>' must pass its own type parameters as the type arguments of 'Shape<T>'")]
    [InlineData(
        """
        [GenerateSerializer] public closed record Shape<T>;
        [GenerateSerializer, Case(0)] public sealed record RefShape<T>([property: Id(0)] T Item) : Shape<T> where T : class;
        """,
        "Case 'RefShape<T>' has type parameter constraints which closed type 'Shape<T>' does not declare")]
    [InlineData(
        """
        public class Constraints<T> { public interface INested { } }
        [GenerateSerializer] public closed record Base<T> where T : Constraints<int>.INested;
        [GenerateSerializer, Case(0)] public sealed record Item<T>([property: Id(0)] T Value) : Base<T>
            where T : Constraints<int>.INested, Constraints<string>.INested;
        """,
        "Case 'Item<T>' has type parameter constraints which closed type 'Base<T>' does not declare")]
    [InlineData(
        """
        public static class Outer
        {
            [GenerateSerializer] public closed record Base;
            [GenerateSerializer, Case(0)] private sealed record Hidden : Base;
        }
        """,
        "Case type 'Outer.Hidden' of 'Outer.Base' is not accessible to generated code")]
    [InlineData(
        """
        [GenerateSerializer] public closed record Base<T, U>;
        [GenerateSerializer, Case(0)] public sealed record Item<X, Y>([property: Id(0)] X Value) : Base<X, X>;
        """,
        "Case 'Item<X, Y>' must pass its own type parameters as the type arguments of 'Base<T, U>'")]
    [InlineData(
        """
        [GenerateSerializer] public closed record Base<T> where T : struct;
        [GenerateSerializer, Case(0)] public sealed record Item<X>([property: Id(0)] X Value) : Base<X> where X : unmanaged;
        """,
        "Case 'Item<X>' has type parameter constraints which closed type 'Base<T>' does not declare")]
    [InlineData(
        """
        [GenerateSerializer] public closed record Base<T, U>;
        [GenerateSerializer, Case(0)] public sealed record Item<X, Y>([property: Id(0)] X Value) : Base<X, Y> where X : Y;
        """,
        "Case 'Item<X, Y>' has type parameter constraints which closed type 'Base<T, U>' does not declare")]
    [InlineData(
        """
        public sealed record Cat;
        public sealed record Dog;
        [GenerateSerializer] public union Pet(Cat, Dog);
        """,
        "The compiler does not expose the case types of union type 'Pet' to source generators")]
    public void InvalidCasesProduceGeneratorErrors(string source, string expectedMessage)
    {
        var (_, result) = Generate(source);

        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0116");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(expectedMessage, diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void AddingClosedCasesPreservesHotReloadFieldsAndConstructor()
    {
        const string before = """
            [GenerateSerializer] public closed record Base;
            [GenerateSerializer, Case(9)] public sealed record Existing([property: Id(0)] string Value) : Base;
            """;
        const string added = """
            [GenerateSerializer, Case(1)] public sealed record Added([property: Id(0)] string Value) : Base;
            """;
        var (beforeOutput, beforeResult) = Generate(before, hotReload: true);
        var (afterOutput, afterResult) = Generate(before + "\n" + added, hotReload: true);
        AssertNoErrors(beforeOutput);
        AssertNoErrors(afterOutput);
        var beforeCodec = GetCodec(beforeResult, "Codec_Base");
        var afterCodec = GetCodec(afterResult, "Codec_Base");

        Assert.Equal(
            Assert.Single(beforeCodec.Members.OfType<ConstructorDeclarationSyntax>()).ParameterList.ToString(),
            Assert.Single(afterCodec.Members.OfType<ConstructorDeclarationSyntax>()).ParameterList.ToString());
        var fields = afterCodec.Members.OfType<FieldDeclarationSyntax>().ToDictionary(static field => field.Declaration.Variables.Single().Identifier.Text);
        Assert.Contains("_codecProvider", fields.Keys);
        Assert.Equal(
            beforeCodec.Members.OfType<FieldDeclarationSyntax>().Single(static field => field.Declaration.Variables.Single().Identifier.Text == "_caseCodec9").ToString(),
            fields["_caseCodec9"].ToString());
        Assert.DoesNotContain(fields["_caseCodec1"].Modifiers, static modifier => modifier.IsKind(SyntaxKind.ReadOnlyKeyword));
        Assert.Contains("(_caseCodec1 ??= OrleansGeneratedCodeHelper.GetService<", afterCodec.NormalizeWhitespace().ToFullString(), StringComparison.Ordinal);
    }

    [Fact]
    public Task HotReloadClosedHierarchyGeneratedSourceMatchesSnapshot()
    {
        var (output, result) = Generate("""
            [GenerateSerializer] public closed record Base;
            [GenerateSerializer, Case(9)] public sealed record Existing([property: Id(0)] string Value) : Base;
            [GenerateSerializer, Case(1)] public sealed record Added([property: Id(0)] string Value) : Base;
            """, hotReload: true);
        AssertNoErrors(output);
        var source = Assert.Single(result.Results.SelectMany(static generator => generator.GeneratedSources),
            static generated => generated.SourceText.ToString().Contains("class Codec_Base :", StringComparison.Ordinal));

        return Verify(source.SourceText.ToString(), extension: "cs").UseDirectory("snapshots");
    }

    [Fact]
    public void HotReloadClosedCaseFieldsResolveWhenConstructorInitializationWasSkipped()
    {
        var (output, _) = Generate("""
            [GenerateSerializer] public closed record Base;
            [GenerateSerializer, Case(9)] public sealed record Item([property: Id(0)] string Value) : Base;

            public static class Probe
            {
                public static bool RoundTrip()
                {
                    using var services = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
                        Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                            new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
                            builder => Orleans.Serialization.SerializerBuilderExtensions.AddAssembly(builder, typeof(Probe).Assembly)));
                    var provider = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<
                        Orleans.Serialization.Serializers.ICodecProvider>(services);
                    var serializer = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<
                        Orleans.Serialization.Serializer<Base>>(services);
                    var codec = provider.GetCodec<Base>();
                    var field = codec.GetType().GetField("_caseCodec9", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    field.SetValue(codec, null);
                    var bytes = serializer.SerializeToArray(new Item("after update"));
                    var initializedByWrite = field.GetValue(codec) is not null;
                    field.SetValue(codec, null);
                    var result = serializer.Deserialize(bytes);
                    return initializedByWrite && field.GetValue(codec) is not null && result == new Item("after update");
                }
            }
            """, hotReload: true);
        AssertNoErrors(output);
        using var image = new MemoryStream();
        var emitted = output.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        image.Position = 0;
        var probe = AssemblyLoadContext.Default.LoadFromStream(image).GetType("Probe", throwOnError: true)!;
        Assert.True(probe.GetMethod("RoundTrip")!.CreateDelegate<Func<bool>>()());
    }

    private static void AssertGeneratesTaggedUnionCodec(GeneratorDriverRunResult result)
        => Assert.Contains(result.Results.SelectMany(static generator => generator.GeneratedSources), static source => source.SourceText.ToString().Contains("WriteStartUnion", StringComparison.Ordinal));
}
#endif
