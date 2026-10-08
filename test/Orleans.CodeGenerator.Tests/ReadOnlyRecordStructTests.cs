using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
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

    [Theory]
    [InlineData("readonly record struct", false, false, false)]
    [InlineData("readonly record struct", true, false, false)]
    [InlineData("readonly record struct", false, true, false)]
    [InlineData("readonly record struct", true, true, true)]
    [InlineData("record struct", false, false, false)]
    [InlineData("record struct", true, false, false)]
    [InlineData("record struct", false, true, false)]
    [InlineData("record struct", true, true, true)]
    public async Task ReferencedCustomInitRecordStructRequiresExplicitPrimaryConstructorMetadata(
        string declaration, bool includeParameters, bool annotateConstructor, bool annotateAdditionalConstructor)
    {
        var constructor = annotateConstructor
            ? """
              [ActivatorUtilitiesConstructor]
              public TestRecord(byte[] Value) => _storage = Value;
              """
            : "";
        var additionalConstructor = annotateAdditionalConstructor
            ? """
              [ActivatorUtilitiesConstructor]
              public TestRecord() : this(System.Array.Empty<byte>()) { }
              """
            : "";
        var producer = await TestCompilationHelper.CreateCompilation($$"""
            using Orleans;
            using Microsoft.Extensions.DependencyInjection;

            namespace TestProject;

            [GenerateSerializer{{(includeParameters ? "(IncludePrimaryConstructorParameters = true)" : "")}}]
            public {{declaration}} TestRecord{{(annotateConstructor ? "" : "(byte[] Value)")}}
            {
                {{constructor}}
                {{additionalConstructor}}

                [System.NonSerialized]
                private readonly byte[] _storage{{(annotateConstructor ? "" : " = Value")}};

                public byte[] Value
                {
                    get => _storage;
                    init => _storage = value;
                }
            }
            """);
        var (sourceOutput, sourceResult) = GenerateRecordCompilation(producer);
        Assert.Empty(sourceResult.Diagnostics);
        AssertNoCompilationErrors(sourceOutput);

        var consumer = await TestCompilationHelper.CreateCompilation(
            """
            [assembly: Orleans.GenerateCodeForDeclaringAssembly(typeof(TestProject.TestRecord))]
            """, "ConsumerProject", MetadataReference.CreateFromImage(EmitRecordCompilation(producer)));
        var imported = consumer.GetTypeByMetadataName("TestProject.TestRecord")!;
        Assert.False(imported.IsRecord);
        Assert.False(Assert.Single(imported.GetMembers("Value").OfType<IPropertySymbol>()).IsCompilerGenerated());
        Assert.Equal((annotateConstructor ? 1 : 0) + (annotateAdditionalConstructor ? 1 : 0),
            imported.Constructors.Count(constructor => constructor.GetAttributes()
                .Any(attribute => attribute.AttributeClass?.Name == nameof(ActivatorUtilitiesConstructorAttribute))));
        var (_, result) = GenerateRecordCompilation(consumer);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ORLEANS0106", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("no field ids were assigned to any candidate serializable members",
            diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("readonly record struct", false)]
    [InlineData("readonly record struct", true)]
    [InlineData("record struct", false)]
    [InlineData("record struct", true)]
    public async Task ReferencedCustomInitRecordStructAnnotationsPreservePrimaryMemberIdentity(
        string declaration, bool parameterIds)
    {
        var parameters = $"{(parameterIds ? "[Id(4)] " : "")}byte[] Value, {(parameterIds ? "[Id(9)] " : "")}int Number";
        string CreateDeclaration(bool positional, bool annotations) => $$"""
            [GenerateSerializer{{(annotations ? "(IncludePrimaryConstructorParameters = true)" : "")}}]
            {{(annotations && positional ? "[method: ActivatorUtilitiesConstructor]" : "")}}
            public {{declaration}} TestRecord{{(positional ? $"({parameters})" : "")}}
            {
                {{(positional ? "" : $$"""
                [ActivatorUtilitiesConstructor]
                public TestRecord({{parameters}})
                {
                    _storage = Value;
                    _number = Number;
                }
                """)}}

                [System.NonSerialized]
                private readonly byte[] _storage{{(positional ? " = Value" : "")}};

                [System.NonSerialized]
                private readonly int _number{{(positional ? " = Number" : "")}};

                public byte[] Value { get => _storage; init => _storage = value; }
                public int Number { get => _number; init => _number = value; }

                [Id(0)]
                public int Extra { get; init; }

                public TestRecord(int Number, byte[] Value) : this(Value, Number) { }
                public TestRecord(byte[] Value) : this(Value, -1) { }
            }
            """;
#if NET8_0
        // Roslyn 4.5 predates method-target attributes on positional record structs.
        const bool positionalProducer = false;
#else
        const bool positionalProducer = true;
#endif
        var producer = await TestCompilationHelper.CreateCompilation($$"""
            using Orleans;
            using Microsoft.Extensions.DependencyInjection;
            namespace TestProject;

            {{CreateDeclaration(positionalProducer, annotations: true)}}
            """, $"RecordContracts{Guid.NewGuid():N}");
        Assert.DoesNotContain(producer.GetDiagnostics(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.Id == "CS0657");
        var producerImage = EmitRecordCompilation(producer);
        var consumer = await TestCompilationHelper.CreateCompilation(
            """
            [assembly: Orleans.GenerateCodeForDeclaringAssembly(typeof(TestProject.TestRecord))]
            """ + RecordRuntimeProbe, $"RecordConsumer{Guid.NewGuid():N}", MetadataReference.CreateFromImage(producerImage));
        var imported = consumer.GetTypeByMetadataName("TestProject.TestRecord")!;
        Assert.False(imported.IsRecord);
        var selected = Assert.Single(imported.Constructors, constructor => constructor.GetAttributes()
            .Any(attribute => attribute.AttributeClass?.Name == nameof(ActivatorUtilitiesConstructorAttribute)));
        Assert.Equal(["Value", "Number"], selected.Parameters.Select(parameter => parameter.Name));
        Assert.All(imported.GetMembers().OfType<IPropertySymbol>().Where(property => property.Name is "Value" or "Number"),
            property => Assert.False(property.IsCompilerGenerated()));

        var (metadataOutput, metadataResult) = GenerateRecordCompilation(consumer);
        Assert.Empty(metadataResult.Diagnostics);
        AssertNoCompilationErrors(metadataOutput);

        var source = await TestCompilationHelper.CreateCompilation($$"""
            using Orleans;
            namespace TestProject
            {
                {{CreateDeclaration(positional: true, annotations: false)}}
            }
            """ + RecordRuntimeProbe, $"RecordSource{Guid.NewGuid():N}");
        var (sourceOutput, sourceResult) = GenerateRecordCompilation(source);
        Assert.Empty(sourceResult.Diagnostics);
        AssertNoCompilationErrors(sourceOutput);
        Assert.Equal(GetRecordSerializeMethod(sourceResult), GetRecordSerializeMethod(metadataResult));

        using var contractsStream = new MemoryStream(producerImage);
        AssemblyLoadContext.Default.LoadFromStream(contractsStream);
        var sourceProbe = LoadRecordProbe(sourceOutput);
        var metadataProbe = LoadRecordProbe(metadataOutput);
        var sourceBytes = sourceProbe.GetMethod("Serialize")!.CreateDelegate<Func<byte[]>>()();
        var metadataBytes = metadataProbe.GetMethod("Serialize")!.CreateDelegate<Func<byte[]>>()();
        Assert.Equal(sourceBytes, metadataBytes);

        foreach (var probe in new[] { sourceProbe, metadataProbe })
        {
            var deserialize = probe.GetMethod("Deserialize")!.CreateDelegate<Func<byte[], (byte[] Value, int Number, int Extra)>>();
            foreach (var bytes in new[] { sourceBytes, metadataBytes })
            {
                var restored = deserialize(bytes);
                Assert.Equal([5, 10, 15], restored.Value);
                Assert.Equal(137, restored.Number);
                Assert.Equal(23, restored.Extra);
            }

            var copy = probe.GetMethod("Copy")!.CreateDelegate<Func<(byte[] Value, int Number, int Extra, bool Independent, int OriginalFirst)>>()();
            Assert.Equal([99, 10, 15], copy.Value);
            Assert.Equal(137, copy.Number);
            Assert.Equal(23, copy.Extra);
            Assert.True(copy.Independent);
            Assert.Equal(5, copy.OriginalFirst);
        }
    }

    private const string RecordRuntimeProbe = """

        public static class RecordRuntimeProbe
        {
            private static Microsoft.Extensions.DependencyInjection.ServiceProvider CreateServices()
                => Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
                    Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                        new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
                        builder => Orleans.Serialization.SerializerBuilderExtensions.AddAssembly(builder, typeof(RecordRuntimeProbe).Assembly)));

            public static byte[] Serialize()
            {
                using var services = CreateServices();
                var serializer = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<
                    Orleans.Serialization.Serializer<TestProject.TestRecord>>(services);
                return serializer.SerializeToArray(new TestProject.TestRecord(new byte[] { 5, 10, 15 }, 137) { Extra = 23 });
            }

            public static (byte[], int, int) Deserialize(byte[] bytes)
            {
                using var services = CreateServices();
                var serializer = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<
                    Orleans.Serialization.Serializer<TestProject.TestRecord>>(services);
                var result = serializer.Deserialize(bytes);
                return (result.Value, result.Number, result.Extra);
            }

            public static (byte[], int, int, bool, int) Copy()
            {
                using var services = CreateServices();
                var copier = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<
                    Orleans.Serialization.DeepCopier>(services);
                var input = new TestProject.TestRecord(new byte[] { 5, 10, 15 }, 137) { Extra = 23 };
                var copy = copier.Copy(input);
                copy.Value[0] = 99;
                return (copy.Value, copy.Number, copy.Extra, !object.ReferenceEquals(input.Value, copy.Value), input.Value[0]);
            }
        }
        """;

    private static (Compilation Output, GeneratorDriverRunResult Result) GenerateRecordCompilation(CSharpCompilation compilation)
    {
        compilation = compilation.AddReferences(MetadataReference.CreateFromFile(typeof(ServiceProvider).Assembly.Location));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
            optionsProvider: TestCompilationHelper.CreateOptionsProvider());
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var output, out _, TestContext.Current.CancellationToken);
        return (output, driver.GetRunResult());
    }

    private static string GetRecordSerializeMethod(GeneratorDriverRunResult result)
    {
        var codec = Assert.Single(result.Results.Single().GeneratedSources
            .SelectMany(source => source.SyntaxTree.GetRoot(TestContext.Current.CancellationToken)
                .DescendantNodes().OfType<ClassDeclarationSyntax>()), type => type.Identifier.ValueText == "Codec_TestRecord");
        return Assert.Single(codec.Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == "Serialize").NormalizeWhitespace().ToFullString();
    }

    private static byte[] EmitRecordCompilation(Compilation compilation)
    {
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return image.ToArray();
    }

    private static Type LoadRecordProbe(Compilation compilation)
    {
        using var image = new MemoryStream(EmitRecordCompilation(compilation));
        return AssemblyLoadContext.Default.LoadFromStream(image).GetType("RecordRuntimeProbe", throwOnError: true)!;
    }

    private static void AssertNoCompilationErrors(Compilation compilation)
        => Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

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
