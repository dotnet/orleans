using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

public class ReadOnlyRecordStructTests
{
    [Theory]
    [InlineData("readonly record struct", true, false)]
    [InlineData("readonly record struct", true, true)]
    [InlineData("record struct", true, false)]
    [InlineData("record struct", true, true)]
    [InlineData("record class", false, false)]
    [InlineData("record class", false, true)]
    public async Task InitOnlyPrimaryConstructorPropertyAccessorsUseMatchingReceivers(
        string declaration, bool isValueType, bool referencedAssembly)
    {
        var property = referencedAssembly
            ? "public byte[] Value { get; init; } = Value;"
            : """
              [System.NonSerialized]
              private readonly byte[] _storage = Value;

              public byte[] Value
              {
                  get => _storage;
                  init => _storage = value;
              }
              """;
        var source = $$"""
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public {{declaration}} TestRecord(byte[] Value)
            {
                {{property}}
            }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        if (referencedAssembly)
        {
            using var stream = new MemoryStream();
            var emitResult = compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));
            compilation = await TestCompilationHelper.CreateCompilation(
                """
                using Orleans;
                [assembly: GenerateCodeForDeclaringAssembly(typeof(TestProject.TestRecord))]
                """,
                "ConsumerProject",
                MetadataReference.CreateFromImage(stream.ToArray()));
        }

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
            optionsProvider: TestCompilationHelper.CreateOptionsProvider());
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var output, out var diagnostics, TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));

        var classes = driver.GetRunResult().Results.Single().GeneratedSources
            .SelectMany(s => s.SyntaxTree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<ClassDeclarationSyntax>())
            .ToList();
        var usesFieldAccessor = referencedAssembly && isValueType;
        foreach (var className in new[] { "Codec_TestRecord", "Copier_TestRecord" })
        {
            var type = Assert.Single(classes, c => c.Identifier.ValueText == className);
            var accessor = Assert.Single(type.Members.OfType<MethodDeclarationSyntax>(), m => m.Modifiers.Any(SyntaxKind.ExternKeyword));
            Assert.Contains(usesFieldAccessor ? "UnsafeAccessorKind.Field" : "UnsafeAccessorKind.Method", accessor.ToString(), StringComparison.Ordinal);
            Assert.Contains(usesFieldAccessor ? "Name = \"<Value>k__BackingField\"" : "Name = \"set_Value\"", accessor.ToString(), StringComparison.Ordinal);
            Assert.Equal(usesFieldAccessor ? 1 : 2, accessor.ParameterList.Parameters.Count);
            Assert.Equal(usesFieldAccessor, accessor.ReturnType is RefTypeSyntax);
            Assert.Equal(isValueType, accessor.ParameterList.Parameters[0].Modifiers.Any(SyntaxKind.RefKeyword));
            Assert.Equal("global::TestProject.TestRecord", accessor.ParameterList.Parameters[0].Type!.ToString());
            var invocations = type.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression.ToString() == accessor.Identifier.ValueText).ToList();
            Assert.NotEmpty(invocations);
            Assert.All(invocations, invocation =>
                Assert.Equal(isValueType, invocation.ArgumentList.Arguments[0].RefKindKeyword.IsKind(SyntaxKind.RefKeyword)));
        }
    }

    [Fact]
    public void ReadOnlyRecordStructAutoInitPropertiesRoundTripAndCopy()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var input = new AutoInitRecord([2, 4, 8]) { Number = 137, Extra = [3, 6, 9] };
        var serializer = services.GetRequiredService<Serializer<AutoInitRecord>>();
        var result = serializer.Deserialize(serializer.SerializeToArray(input));
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);

        Assert.Equal(137, result.Number);
        Assert.Equal(input.Value, result.Value);
        Assert.Equal(input.Extra, result.Extra);
        Assert.Equal(137, copy.Number);
        Assert.Equal(input.Value, copy.Value);
        Assert.Equal(input.Extra, copy.Extra);
        Assert.NotSame(input.Value, copy.Value);
        Assert.NotSame(input.Extra, copy.Extra);
        copy.Value[0] = 99;
        copy.Extra[0] = 99;
        Assert.Equal(2, input.Value[0]);
        Assert.Equal(3, input.Extra[0]);
    }

    [Fact]
    public void ReadOnlyRecordStructCustomInitPropertyRoundTripsAndCopies()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var input = new CustomInitRecord([5, 10, 15]);
        var serializer = services.GetRequiredService<Serializer<CustomInitRecord>>();
        var result = serializer.Deserialize(serializer.SerializeToArray(input));
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);

        Assert.Equal(input.Value, result.Value);
        Assert.Equal(input.Value, copy.Value);
        Assert.NotSame(input.Value, copy.Value);
        copy.Value[0] = 99;
        Assert.Equal(5, input.Value[0]);
    }

    [GenerateSerializer]
    public readonly record struct AutoInitRecord(byte[] Value)
    {
        [Id(0)]
        public int Number { get; init; }

        [Id(1)]
        public byte[] Extra { get; init; } = [];
    }

    [GenerateSerializer]
    public readonly record struct CustomInitRecord(byte[] Value)
    {
        [NonSerialized]
        private readonly byte[] _storage = Value;

        public byte[] Value
        {
            get => _storage;
            init => _storage = value;
        }
    }
}
