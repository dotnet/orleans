using System.Buffers;
using System.IO;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class RpcResponseHolderNamingTests
{
    private const string FirstResult = """
        namespace TestProject;
        [Orleans.GenerateSerializer]
        public sealed class Response42799 { [Orleans.Id(0)] public int Value { get; set; } }
        public interface IFirst : Orleans.IGrainWithIntegerKey
        {
            System.Threading.Tasks.Task<Response42799> First();
        }
        """;

    private const string SecondResult = """
        namespace TestProject;
        [Orleans.GenerateSerializer]
        public sealed class Response123510 { [Orleans.Id(0)] public int Value { get; set; } }
        public interface ISecond : Orleans.IGrainWithIntegerKey
        {
            System.Threading.Tasks.ValueTask<Response123510> Second();
        }
        """;

    private const string Target = """
        namespace TestProject;
        public sealed class CollisionTarget : IFirst, ISecond
        {
            public Response42799 FirstValue { get; } = new() { Value = 47 };
            public Response123510 SecondValue { get; } = new() { Value = 59 };
            public System.Threading.Tasks.Task<Response42799> First() => System.Threading.Tasks.Task.FromResult(FirstValue);
            public System.Threading.Tasks.ValueTask<Response123510> Second() => new(SecondValue);
        }
        """;

    [Fact]
    public void ResolvedNamesDisambiguateHashesAndPreserveNoncollidingNames()
    {
        string[] types = ["global::TestProject.Response42799", "string", "global::TestProject.Response123510"];
        var names = RpcResponseHolderGenerator.GetNames(types);
        Assert.Equal(
            [
                ("global::TestProject.Response123510", "RpcResponse_A633008A"),
                ("global::TestProject.Response42799", "RpcResponse_A633008A_1"),
                ("string", "RpcResponse_9146C7E3"),
            ],
            names);
        Assert.Equal(names, RpcResponseHolderGenerator.GetNames(Enumerable.Reverse(types).Concat(types)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollidingResultsCompileAndUseDistinctFactories(bool compatibilityInvokers)
    {
        var compilation = await CreateCompilation($"RpcResponseCollision{Guid.NewGuid():N}");
        var (result, output) = RunGenerator(compilation, compatibilityInvokers);
        Assert.Empty(result.Diagnostics);
        AssertNoErrors(output.GetDiagnostics(TestContext.Current.CancellationToken));
        using var image = new MemoryStream();
        var emit = output.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var assembly = Assembly.Load(image.ToArray());
        using var services = new ServiceCollection().AddSerializer(builder => builder.AddAssembly(assembly)).BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var contexts = services.GetRequiredService<CopyContextPool>();
        var copier = services.GetRequiredService<DeepCopier>().GetCopier<Response>();
        var target = Activator.CreateInstance(assembly.GetType("TestProject.CollisionTarget")!);
        var requests = assembly.GetTypes()
            .Where(static type => typeof(IResponseInvokable).IsAssignableFrom(type))
            .Select(static type => (IInvokable)Activator.CreateInstance(type)!)
            .OrderBy(static request => request.GetMethodName(), StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, requests.Length);
        using var firstRequest = requests[0];
        using var secondRequest = requests[1];
        firstRequest.SetTarget(new TargetHolder(target!));
        secondRequest.SetTarget(new TargetHolder(target!));
        using var first = await ((IResponseInvokable)firstRequest).InvokeAndCopy(provider, contexts, copier);
        using var second = await ((IResponseInvokable)secondRequest).InvokeAndCopy(provider, contexts, copier);
        Assert.IsAssignableFrom<IRawResponseWriter>(first);
        Assert.IsAssignableFrom<IRawResponseWriter>(second);
        Assert.NotEqual(first.GetType(), second.GetType());
        Assert.NotSame(provider.GetCodec(first.GetType()), provider.GetCodec(second.GetType()));
        Assert.True(provider.TryGetRawResponseReader(Assert.IsAssignableFrom<Type>(first.GetSimpleResultType()), out var firstReader));
        Assert.True(provider.TryGetRawResponseReader(Assert.IsAssignableFrom<Type>(second.GetSimpleResultType()), out var secondReader));
        Assert.NotSame(firstReader, secondReader);
        Assert.NotEqual(firstReader.GetType(), secondReader.GetType());
        AssertCopiedPayload(first, target!, "FirstValue", "Response42799", 47);
        AssertCopiedPayload(second, target!, "SecondValue", "Response123510", 59);
        AssertRawRoundTrip(first, firstReader, services, 47);
        AssertRawRoundTrip(second, secondReader, services, 59);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReorderedCollidingResultsProduceIdenticalSources(bool compatibilityInvokers)
    {
        var compilation = await CreateCompilation();
        var reversed = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(compilation.SyntaxTrees.Reverse());
        var (result, output) = RunGenerator(compilation, compatibilityInvokers);
        var (reordered, reorderedOutput) = RunGenerator(reversed, compatibilityInvokers);
        AssertNoErrors(output.GetDiagnostics(TestContext.Current.CancellationToken));
        AssertNoErrors(reorderedOutput.GetDiagnostics(TestContext.Current.CancellationToken));
        Assert.Equal(
            result.GeneratedSources.OrderBy(static source => source.HintName, StringComparer.Ordinal)
                .Select(static source => (source.HintName, Source: source.SourceText.ToString())),
            reordered.GeneratedSources.OrderBy(static source => source.HintName, StringComparer.Ordinal)
                .Select(static source => (source.HintName, Source: source.SourceText.ToString())));
    }

    private static async Task<CSharpCompilation> CreateCompilation(string assemblyName = "TestProject")
    {
        var compilation = await TestCompilationHelper.CreateCompilation(FirstResult, assemblyName);
        return compilation.AddReferences(
                MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(ServiceProvider).Assembly.Location))
            .AddSyntaxTrees(
                CSharpSyntaxTree.ParseText(SecondResult, cancellationToken: TestContext.Current.CancellationToken),
                CSharpSyntaxTree.ParseText(Target, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static (GeneratorRunResult Result, Compilation Output) RunGenerator(CSharpCompilation compilation, bool compatibilityInvokers)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(preprocessorSymbols: ["NET5_0_OR_GREATER"]),
            optionsProvider: TestCompilationHelper.CreateOptionsProvider(new Dictionary<string, string>
            {
                ["build_property.OrleansGenerateCompatibilityInvokers"] = compatibilityInvokers.ToString(),
            }));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        AssertNoErrors(diagnostics);
        return (driver.GetRunResult().Results.Single(), output);
    }

    private static void AssertCopiedPayload(Response response, object target, string property, string typeName, int expected)
    {
        Assert.Null(response.Exception);
        var value = response.Result;
        Assert.NotNull(value);
        Assert.Equal(typeName, value.GetType().Name);
        Assert.Equal(value.GetType(), response.GetSimpleResultType());
        Assert.Equal(expected, value.GetType().GetProperty("Value")!.GetValue(value));
        var original = target.GetType().GetProperty(property)!.GetValue(target);
        Assert.NotSame(original, value);
        value.GetType().GetProperty("Value")!.SetValue(original, -1);
        Assert.Equal(expected, value.GetType().GetProperty("Value")!.GetValue(value));
    }

    private static void AssertRawRoundTrip(Response response, IRawResponseReader rawReader, ServiceProvider services, int expected)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var sessions = services.GetRequiredService<SerializerSessionPool>();
        using (var session = sessions.GetSession())
        {
            var writer = Writer.Create(buffer, session);
            ((IRawResponseWriter)response).WriteRaw(ref writer);
            writer.Commit();
        }

        using var readerSession = sessions.GetSession();
        var reader = Reader.Create(buffer.WrittenMemory, readerSession);
        var field = reader.ReadFieldHeader();
        Assert.Equal(response.GetSimpleResultType(), field.FieldType);
        using var roundTrip = rawReader.ReadRaw(ref reader, ref field);
        Assert.Equal(response.GetType(), roundTrip.GetType());
        Assert.Equal(response.GetSimpleResultType(), roundTrip.GetSimpleResultType());
        Assert.NotNull(roundTrip.Result);
        Assert.Equal(expected, roundTrip.Result.GetType().GetProperty("Value")!.GetValue(roundTrip.Result));
        Assert.Equal(buffer.WrittenCount, reader.Position);
    }

    private static void AssertNoErrors(IEnumerable<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(static error => error.ToString())));
    }

    private sealed class TargetHolder(object target) : ITargetHolder
    {
        public object GetTarget() => target;
        public object? GetComponent(Type componentType) => componentType.IsInstanceOfType(target) ? target : null;
    }
}
