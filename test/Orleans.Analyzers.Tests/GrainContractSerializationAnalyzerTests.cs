using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Analyzers;
using Orleans.Serialization;
using Orleans.Storage;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Analyzer")]
public class GrainContractSerializationAnalyzerTests : DiagnosticAnalyzerTestBase<GrainContractSerializationAnalyzer>
{
    protected override ReportDiagnostic GetDiagnosticReport(DiagnosticDescriptor descriptor) => ReportDiagnostic.Warn;

    [Theory]
    [MemberData(nameof(GrainInterfaces))]
    public async Task UnannotatedRequest_ReportsWarningForEveryGrainKey(string grainInterface)
    {
        var (diagnostics, source) = await GetDiagnosticsAsync($$"""
            public class Request { }
            public interface IContract : {{grainInterface}} { Task Send(Request request); }
            """);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(GrainContractSerializationAnalyzer.ContractRuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("'Request'", diagnostic.GetMessage());
        Assert.Equal("Request", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
    }

    [Theory]
    [InlineData("Task<Response>")]
    [InlineData("ValueTask<Response>")]
    [InlineData("System.Collections.Generic.IAsyncEnumerable<Response>")]
    public async Task UnannotatedResponse_ReportsAtReturnType(string returnType)
    {
        var (diagnostics, source) = await GetDiagnosticsAsync($$"""
            public class Response { }
            public interface IContract : IGrainWithIntegerKey { {{returnType}} Read(); }
            """);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("'Response'", diagnostic.GetMessage());
        Assert.Equal(returnType, source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
    }

    [Theory]
    [InlineData("class")]
    [InlineData("struct")]
    [InlineData("record")]
    [InlineData("record struct")]
    public async Task UnannotatedDataKinds_AreChecked(string kind)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync($$"""
            public {{kind}} Request { }
            public interface IContract : IGrain { Task Send(Request request); }
            """);
        Assert.Single(diagnostics);
    }

    [Fact]
    public Task AnnotatedGraph_WithCycle_IsAccepted() => AssertNoDiagnostics("""
        [GenerateSerializer] public class Node
        {
            [Id(0)] public Node Next { get; set; }
            [Id(1)] public System.Collections.Generic.List<Node> Children { get; set; }
        }
        public interface IContract : IGrain { Task<Node> Echo(Node request); }
        """);

    [Fact]
    public async Task SerializedMembers_ExposeNestedMissingType()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class Missing { }
            [GenerateSerializer] public class Request { [Id(0)] public Missing Value { get; set; } }
            public interface IContract : IGrain { Task Send(Request request); }
            """);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Theory]
    [InlineData("Missing[]")]
    [InlineData("Missing[,]")]
    [InlineData("System.Collections.Generic.List<Missing>")]
    [InlineData("System.Collections.Generic.Dictionary<string, Missing>")]
    [InlineData("System.Collections.Generic.IReadOnlyList<Missing>")]
    [InlineData("System.Collections.Generic.IEnumerable<Missing>")]
    [InlineData("System.Collections.Immutable.ImmutableArray<Missing>")]
    [InlineData("System.Tuple<Missing>")]
    [InlineData("System.ValueTuple<int, Missing>")]
    public async Task CollectionElements_AreChecked(string parameterType)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync($$"""
            public class Missing { }
            public interface IContract : IGrain { Task Send({{parameterType}} request); }
            """);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task NullableStruct_IsChecked()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public struct Missing { }
            public interface IContract : IGrain { Task Send(Missing? request); }
            """);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task TransitiveNonGrainBase_WithClosedGenericContract_IsChecked()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class Missing { }
            public interface IBase<T> { Task<T> Echo(T request); }
            public interface IMiddle<T> : IBase<T> { }
            public interface IContract : IMiddle<Missing>, IGrainWithGuidKey { }
            """);
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Contains("'Missing'", diagnostic.GetMessage()));
    }

    [Fact]
    public async Task InheritedGrainMethods_AreChecked()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class Missing { }
            public interface IBase : IGrain { Task Send(Missing request); }
            public interface IContract : IBase { }
            """);
        Assert.Single(diagnostics);
    }

    [Fact]
    public Task PrimitiveAndRuntimeProvidedTypes_AreAccepted() => AssertNoDiagnostics("""
        public enum Choice { First }
        public interface IContract : IGrainWithStringKey
        {
            Task Send(int number, string text, Guid id, DateTime date, DateTimeOffset offset,
                TimeSpan duration, Uri uri, System.Net.IPAddress address, Choice choice, int? optional,
                IContract reference, System.Threading.CancellationToken cancellationToken);
            Task Ping();
            ValueTask Pong();
            Task<object> Read();
        }
        """);

    [Fact]
    public Task UnrelatedServices_StaticMethods_AndPrivateHelpers_AreIgnored() => AssertNoDiagnostics("""
        public class Local { }
        public interface IService { Task<Local> Read(Local request); }
        public interface IContract : IGrain
        {
            static Local Helper(Local request) => request;
            private Local LocalHelper(Local request) => request;
            Task Ping();
        }
        public class Implementation : Grain, IContract
        {
            private Local _local;
            public Local Helper(Local request) => request;
            public Task Ping() => Task.CompletedTask;
        }
        """);

    [Fact]
    public Task NonSerializedAndUnselectedMembers_AreIgnored() => AssertNoDiagnostics("""
        public class Local { }
        [GenerateSerializer] public class Request
        {
            [Id(0)] public int Value { get; set; }
            [NonSerialized] public Local Ignored;
            public Local Unselected { get; set; }
            public static Local Static;
        }
        public interface IContract : IGrain { Task Send(Request request); }
        """);

    [Theory]
    [InlineData("[Id(0)] public Missing Value;")]
    [InlineData("[Id(0)] public Missing Value { get; set; }")]
    public async Task FieldsAndProperties_AreChecked(string member)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync($$"""
            public class Missing { }
            [GenerateSerializer] public class Request { {{member}} }
            public interface IContract : IGrain { Task Send(Request request); }
            """);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task ImplicitPublicProperties_AreChecked()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class Missing { }
            [GenerateSerializer(GenerateFieldIds = GenerateFieldIds.PublicProperties)]
            public class Request { public Missing Value { get; set; } }
            public interface IContract : IGrain { Task Send(Request request); }
            """);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task PositionalRecordMembers_AreChecked()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class Missing { }
            [GenerateSerializer] public record Request(Missing Value);
            public interface IContract : IGrain { Task Send(Request request); }
            """);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task AnnotatedDerivedModel_RequiresSerializableBase()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class MissingBase { }
            [GenerateSerializer] public class Request : MissingBase { }
            public interface IContract : IGrain { Task Send(Request request); }
            """);
        Assert.Contains("'MissingBase'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task ExpandingGenericCycle_ReportsAnalysisLimit()
    {
        var diagnostics = await AnalyzeBoundedGraph("""
            using Orleans;
            [GenerateSerializer] public class Node<T>
            {
                [Id(0)] public Node<System.Collections.Generic.List<T>> Next { get; set; }
            }
            public interface IContract : IGrain { System.Threading.Tasks.Task Send(Node<int> request); }
            """);
        Assert.Contains("static traversal limit", Assert.Single(diagnostics).GetMessage());
    }

    [Theory]
    [InlineData("Box<int>")]
    [InlineData("Box<System.Collections.Generic.List<int>>")]
    public async Task ExpandingConverters_ReportAnalysisLimit(string request)
    {
        var diagnostics = await AnalyzeBoundedGraph($$"""
            using Orleans;
            public struct Box<T> { }
            [RegisterConverter] public class Converter<T> : IConverter<Box<T>, Box<T[]>>
            {
                public Box<T> ConvertFromSurrogate(in Box<T[]> value) => default;
                public Box<T[]> ConvertToSurrogate(in Box<T> value) => default;
            }
            public interface IContract : IGrain { System.Threading.Tasks.Task Send({{request}} request); }
            """);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("static traversal limit", diagnostic.GetMessage());
        Assert.DoesNotContain("no statically declared serializer", diagnostic.GetMessage());
    }

    [Fact]
    public async Task BroadGraphs_ReportAnalysisLimit()
    {
        var members = string.Join(Environment.NewLine, Enumerable.Range(0, 1500).Select(index =>
            $"[Id({index})] public Model{index} Value{index};"));
        var models = string.Join(Environment.NewLine, Enumerable.Range(0, 1500).Select(index =>
            $"[GenerateSerializer] public class Model{index} {{ }}"));
        var diagnostics = await AnalyzeBoundedGraph($$"""
            using Orleans;
            {{models}}
            [GenerateSerializer] public class Request { {{members}} }
            public interface IContract : IGrain { System.Threading.Tasks.Task Send(Request request); }
            """);
        Assert.Contains("static traversal limit", Assert.Single(diagnostics).GetMessage());
    }

    [Theory]
    [InlineData("Envelope<Local>")]
    [InlineData("System.Collections.Generic.List<Envelope<Local>>")]
    [InlineData("Outer<Local>.Inner")]
    public Task UnusedGenericArguments_DoNotRequireSerializers(string request) => AssertNoDiagnostics($$"""
        public class Local { }
        [GenerateSerializer] public class Envelope<T>
        {
            [Id(0)] public int Number { get; set; }
            [NonSerialized] public T Ignored;
        }
        public class Outer<T> { [GenerateSerializer] public class Inner { [Id(0)] public int Number; } }
        public interface IContract : IGrain { Task Send({{request}} request); }
        """);

    [Fact]
    public async Task RepeatedGenericConstructions_CheckTheirActualMemberCodecs()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(CodecSource + """
            public class Payload<T> { }
            [RegisterSerializer] public class IntPayloadCodec : Codec<Payload<int>> { }
            [GenerateSerializer] public class Box<T> { [Id(0)] public Payload<T> Value; }
            [GenerateSerializer] public class Request
            {
                [Id(0)] public Box<string> Bad;
                [Id(1)] public Box<int> Good;
            }
            public interface IContract : IGrain { Task Send(Request request); }
            """);
        Assert.Contains("'Payload<string>'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task GenericArguments_AreCheckedAcrossRepeatedDefinitions()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class Missing { }
            [GenerateSerializer] public class Box<T> { [Id(0)] public T Value { get; set; } }
            [GenerateSerializer] public class Request
            {
                [Id(0)] public Box<int> Good { get; set; }
                [Id(1)] public Box<Missing> Bad { get; set; }
            }
            public interface IContract : IGrain { Task Send(Request request); }
            """);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task SerializableAndLookalikeAttributes_DoNotDeclareOrleansSupport()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            namespace Custom { public class GenerateSerializerAttribute : Attribute { } }
            [Serializable, Custom.GenerateSerializer] public class Missing { }
            public interface IContract : IGrain { Task Send(Missing request); }
            """);
        Assert.Single(diagnostics);
    }

    [Fact]
    public Task RegisteredCodec_IsAcceptedWithoutInspectingItsImplementationDetails() => AssertNoDiagnostics(CodecSource + """
        public class Request { public Local Value { get; set; } }
        public class Local { }
        [RegisterSerializer] public class RequestCodec : Codec<Request> { }
        public interface IContract : IGrain { Task Send(Request request); }
        """);

    [Fact]
    public Task OpenRegisteredCodec_IsAccepted() => AssertNoDiagnostics(CodecSource + """
        public class Request<T> { }
        [RegisterSerializer] public class RequestCodec<T> : Codec<Request<T>> { }
        public interface IContract : IGrain { Task Send(Request<int> request); }
        """);

    [Fact]
    public async Task ClosedCodec_DoesNotExemptOtherConstructions()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(CodecSource + """
            public class Request<T> { }
            [RegisterSerializer] public class RequestCodec : Codec<Request<int>> { }
            public interface IContract : IGrain { Task Send(Request<string> request); }
            """);
        Assert.Contains("'Request<string>'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task ConstrainedCodec_DoesNotExemptIneligibleConstructions()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(CodecSource + """
            public class Request<T> { }
            [RegisterSerializer] public class RequestCodec<T> : Codec<Request<T>> where T : struct { }
            public interface IContract : IGrain { Task Send(Request<string> request); }
            """);
        Assert.Contains("'Request<string>'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public Task RegisteredSurrogateConverter_IsAccepted() => AssertNoDiagnostics("""
        public class Request { }
        [GenerateSerializer] public struct Surrogate { [Id(0)] public int Value; }
        [RegisterConverter] public class Converter : IConverter<Request, Surrogate>
        {
            public Request ConvertFromSurrogate(in Surrogate value) => new Request();
            public Surrogate ConvertToSurrogate(in Request value) => new Surrogate();
        }
        public interface IContract : IGrain { Task Send(Request request); }
        """);

    [Fact]
    public async Task ConverterSurrogate_AlsoRequiresSerializationSupport()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class Request { }
            public struct MissingSurrogate { }
            [RegisterConverter] public class Converter : IConverter<Request, MissingSurrogate>
            {
                public Request ConvertFromSurrogate(in MissingSurrogate value) => new Request();
                public MissingSurrogate ConvertToSurrogate(in Request value) => new MissingSurrogate();
            }
            public interface IContract : IGrain { Task Send(Request request); }
            """);
        Assert.Contains("'MissingSurrogate'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task GrainOfState_IsCheckedWithoutAnnotatingTheGrain()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class MissingState { }
            public class Implementation : Grain<MissingState> { }
            """);
        Assert.Equal(GrainContractSerializationAnalyzer.StateRuleId, Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task InjectedPersistentState_IsCheckedOnce()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class MissingState { }
            public class Implementation : Grain
            {
                private readonly Orleans.Runtime.IPersistentState<MissingState> _state;
                public Implementation(Orleans.Runtime.IPersistentState<MissingState> state) { _state = state; }
            }
            """);
        Assert.Equal(GrainContractSerializationAnalyzer.StateRuleId, Assert.Single(diagnostics).Id);
    }

    [Fact]
    public Task GeneratedState_IsAccepted() => AssertNoDiagnostics("""
        [GenerateSerializer] public class State { [Id(0)] public int Count { get; set; } }
        public class Implementation : Grain<State> { }
        """);

    [Fact]
    public async Task Rules_AreDisabledByDefault_AndCanBeEnabledIndependently()
    {
        var project = CreateAnalyzerTestProject("""
            using Orleans;
            using System.Threading.Tasks;
            public class Missing { }
            public interface IContract : IGrain { Task Send(Missing request); }
            public class Implementation : Grain<Missing> { }
            """);
        var compilation = (await project.GetCompilationAsync(TestContext.Current.CancellationToken))!;
        Assert.NotNull(compilation.GetTypeByMetadataName("Orleans.IGrain"));
        Assert.NotNull(compilation.GetTypeByMetadataName("Orleans.GenerateSerializerAttribute"));
        var analyzer = new GrainContractSerializationAnalyzer();
        Assert.All(analyzer.SupportedDiagnostics, descriptor => Assert.False(descriptor.IsEnabledByDefault));
        Assert.All(analyzer.SupportedDiagnostics, descriptor => Assert.Equal(DiagnosticSeverity.Warning, descriptor.DefaultSeverity));
        Assert.Empty(await RunAnalyzerAsync(compilation, analyzer, TestContext.Current.CancellationToken));
        foreach (var rule in analyzer.SupportedDiagnostics)
        {
            var enabled = compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
                new Dictionary<string, ReportDiagnostic> { [rule.Id] = ReportDiagnostic.Warn }));
            var diagnostic = Assert.Single(await RunAnalyzerAsync(enabled, analyzer, TestContext.Current.CancellationToken));
            Assert.Equal(rule.Id, diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        }
    }

    [Fact]
    public async Task ReferencedContract_IsCheckedAtGrainImplementation()
    {
        var reference = await CompileReference("""
            using Orleans;
            using System.Threading.Tasks;
            public class ExternalMissing { }
            public interface IExternal : IGrain { Task<ExternalMissing> Read(); }
            """);
        var diagnostics = await AnalyzeWithReference("""
            public class Implementation : Orleans.Grain, IExternal
            {
                public System.Threading.Tasks.Task<ExternalMissing> Read() => null;
            }
            """, reference);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("'ExternalMissing'", diagnostic.GetMessage());
        Assert.True(diagnostic.Location.IsInSource);
    }

    [Fact]
    public async Task ReferencedModelsAndCodecs_AreRecognized()
    {
        var reference = await CompileReference("""
            using Orleans;
            [GenerateSerializer] public class ExternalGenerated { [Id(0)] public int Value { get; set; } }
            """ + CodecSource + """
            public class ExternalCustom { }
            [RegisterSerializer] public class ExternalCodec : Codec<ExternalCustom> { }
            """);
        Assert.Empty(await AnalyzeWithReference("""
            public interface IContract : Orleans.IGrain
            {
                System.Threading.Tasks.Task Send(ExternalGenerated generated, ExternalCustom custom);
            }
            """, reference));
    }

    [Fact]
    public async Task SourceGenericContract_IsCheckedAtClosedGrainImplementation()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class Missing { }
            public interface IContract<T> : IGrain { Task Send(T request); }
            public class Implementation : Grain, IContract<Missing>
            {
                public Task Send(Missing request) => Task.CompletedTask;
            }
            """);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task GeneratedCodec_DoesNotHideMissingNestedModel()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(CodecSource + """
            public class Missing { }
            [GenerateSerializer] public class Request { [Id(0)] public Missing Value { get; set; } }
            [RegisterSerializer] public class GeneratedRequestCodec : Codec<Request> { }
            public interface IContract : IGrain { Task Send(Request request); }
            """);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task ReferencedPositionalRecord_ExposesMissingNestedModel()
    {
        var reference = await CompileReference("""
            using Orleans;
            public class Missing { }
            [GenerateSerializer] public record Request(Missing Value);
            """);
        var diagnostics = await AnalyzeWithReference("""
            public interface IContract : Orleans.IGrain { System.Threading.Tasks.Task Send(Request request); }
            """, reference);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConstructorSelectedClassMembers_AreCheckedInSourceAndMetadata(bool referenced)
    {
        const string model = """
            using Orleans;
            public class Missing { }
            [GenerateSerializer(IncludePrimaryConstructorParameters = true)] public class Request
            {
                [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
                public Request(Missing value) { Value = value; }
                public Missing Value { get; set; }
            }
            """;
        const string contract = "public interface IContract : Orleans.IGrain { System.Threading.Tasks.Task Send(Request request); }";
        var diagnostics = referenced
            ? await AnalyzeWithReference(contract, await CompileReference(model))
            : (await GetDiagnosticsFullSourceAsync(model + contract)).ToImmutableArray();
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public Task DisabledConstructorSelection_DoesNotSerializeMatchingProperties() => AssertNoDiagnostics("""
        public class Local { }
        [GenerateSerializer(IncludePrimaryConstructorParameters = false)] public class Request
        {
            [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
            public Request(Local value) { Value = value; }
            public Local Value { get; set; }
            [Id(0)] public int Number { get; set; }
        }
        public interface IContract : IGrain { Task Send(Request request); }
        """);

    [Fact]
    public Task SecondaryRecordConstructor_DoesNotSelectAdditionalProperties() => AssertNoDiagnostics("""
        public class Local { }
        [GenerateSerializer] public record Request(int Number)
        {
            public Request(Local Local) : this(0) { this.Local = Local; }
            public Local Local { get; set; }
        }
        public interface IContract : IGrain { Task Send(Request request); }
        """);

    [Fact]
    public Task ConstructorParameterId_DisablesImplicitPropertySelection() => AssertNoDiagnostics("""
        public class Local { }
        [GenerateSerializer(IncludePrimaryConstructorParameters = true, GenerateFieldIds = GenerateFieldIds.PublicProperties)]
        public class Request
        {
            [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
            public Request([Id(0)] int number) { Number = number; }
            public int Number { get; set; }
            public Local Unselected { get; set; }
        }
        public interface IContract : IGrain { Task Send(Request request); }
        """);

    [Fact]
    public Task ExplicitIds_DisableImplicitPropertySelection() => AssertNoDiagnostics("""
        public class Local { }
        [GenerateSerializer(GenerateFieldIds = GenerateFieldIds.PublicProperties)] public class Request
        {
            [Id(0)] public int Value { get; set; }
            public Local Unselected { get; set; }
        }
        public interface IContract : IGrain { Task Send(Request request); }
        """);

    [Fact]
    public async Task ExcludedMemberId_DoesNotDisableImplicitPropertySelection()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class Missing { }
            [GenerateSerializer(GenerateFieldIds = GenerateFieldIds.PublicProperties)] public class Request
            {
                [NonSerialized, Id(0)] public int Ignored;
                public Missing Value { get; set; }
            }
            public interface IContract : IGrain { Task Send(Request request); }
            """);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Theory]
    [InlineData("private Missing Value { get; set; }")]
    [InlineData("private Missing Value { get; }")]
    public async Task ImplicitMemberSelection_FollowsTheGeneratorForPrivateProperties(string member)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync($$"""
            public class Missing { }
            [GenerateSerializer(GenerateFieldIds = GenerateFieldIds.PublicProperties)]
            public class Request { {{member}} }
            public interface IContract : IGrain { Task Send(Request request); }
            """);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public Task ImplicitMemberSelection_SkipsReadOnlyComputedProperties() => AssertNoDiagnostics("""
        public class Local { }
        [GenerateSerializer(GenerateFieldIds = GenerateFieldIds.PublicProperties)]
        public class Request
        {
            public int Value { get; set; }
            public Local Computed => null;
        }
        public interface IContract : IGrain { Task Send(Request request); }
        """);

    [Fact]
    public async Task ReferencedImplicitReadOnlyProperty_IsChecked()
    {
        var reference = await CompileReference("""
            using Orleans;
            public class Missing { }
            [GenerateSerializer(GenerateFieldIds = GenerateFieldIds.PublicProperties)]
            public class Request { public Missing Value { get; } }
            """);
        var diagnostics = await AnalyzeWithReference("""
            public interface IContract : Orleans.IGrain { System.Threading.Tasks.Task Send(Request request); }
            """, reference);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task ImplicitMemberSelection_FollowsConventionalBackingFields()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class Missing { }
            [GenerateSerializer(GenerateFieldIds = GenerateFieldIds.PublicProperties)]
            public class Request { private Missing _value; public Missing Value => _value; }
            public interface IContract : IGrain { Task Send(Request request); }
            """);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public Task ImplicitMemberSelection_SkipsInitOnlyComputedPropertiesWithoutBackingFields() => AssertNoDiagnostics("""
        public class Local { }
        [GenerateSerializer(GenerateFieldIds = GenerateFieldIds.PublicProperties)]
        public class Request
        {
            public int Value { get; set; }
            public Local Computed { get => null; init { } }
        }
        public interface IContract : IGrain { Task Send(Request request); }
        """);

    private static async Task<PortableExecutableReference> CompileReference(string source)
    {
        var compilation = (await CreateAnalyzerTestProject(source).GetCompilationAsync(TestContext.Current.CancellationToken))!.WithAssemblyName("ExternalModels");
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    [Fact]
    public async Task PartialContract_IsAnalyzedOnce()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class Missing { }
            public partial interface IContract : IGrain { Task Send(Missing request); }
            public partial interface IContract { Task Ping(); }
            """);
        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task ObserverContract_IsChecked()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class Missing { }
            public interface IObserver : IGrainObserver { void Notify(Missing value); }
            """);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task CodecWithoutRegistration_DoesNotDeclareSupport()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(CodecSource + """
            public class Missing { }
            public class UnregisteredCodec : Codec<Missing> { }
            public interface IContract : IGrain { Task Send(Missing request); }
            """);
        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task UnboundCodecParameter_DoesNotDeclareSupport()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(CodecSource + """
            public class Missing<T> { }
            [RegisterSerializer] public class IncompleteCodec<T, TOther> : Codec<Missing<T>> { }
            public interface IContract : IGrain { Task Send(Missing<int> request); }
            """);
        Assert.Contains("'Missing<int>'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public Task ArrayPatternCodec_MatchesItsClosedType() => AssertNoDiagnostics(CodecSource + """
        public class Request<T> { }
        [RegisterSerializer] public class ArrayCodec<T> : Codec<Request<T[]>> { }
        public interface IContract : IGrain { Task Send(Request<int[]> request); }
        """);

    [Fact]
    public async Task GenericSurrogate_SubstitutesArrayArguments()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class Missing { }
            public class Request<T> { }
            [GenerateSerializer] public struct Surrogate<T> { [Id(0)] public T Value; }
            [RegisterConverter] public class Converter<T> : IConverter<Request<T>, Surrogate<T[]>>
            {
                public Request<T> ConvertFromSurrogate(in Surrogate<T[]> value) => new Request<T>();
                public Surrogate<T[]> ConvertToSurrogate(in Request<T> value) => new Surrogate<T[]>();
            }
            public interface IContract : IGrain { Task Send(Request<Missing> request); }
            """);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task GenericGrainBase_SubstitutesState()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class MissingState { }
            public abstract class Base<T> : Grain<T> where T : new() { }
            public class Implementation : Base<MissingState> { }
            """);
        Assert.Contains("'MissingState'", Assert.Single(diagnostics).GetMessage());
    }

    [Fact]
    public async Task NestedStateGraph_IsChecked()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class Missing { }
            [GenerateSerializer] public class State { [Id(0)] public Missing Value { get; set; } }
            public class Implementation : Grain<State> { }
            """);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(GrainContractSerializationAnalyzer.StateRuleId, diagnostic.Id);
        Assert.Contains("'Missing'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task CompilationWithoutOrleans_IsIgnored()
    {
        var compilation = CSharpCompilation.Create("NoOrleans", [CSharpSyntaxTree.ParseText("public class Local { }", cancellationToken: TestContext.Current.CancellationToken)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(await RunAnalyzerAsync(compilation, new GrainContractSerializationAnalyzer(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NestedGenericSurrogate_SubstitutesDeclaringTypeArguments()
    {
        var (diagnostics, _) = await GetDiagnosticsAsync("""
            public class Missing { }
            public class Request<T> { }
            public class Outer<T>
            {
                [GenerateSerializer] public struct Surrogate { [Id(0)] public T Value; }
            }
            [RegisterConverter] public class Converter<T> : IConverter<Request<T>, Outer<T>.Surrogate>
            {
                public Request<T> ConvertFromSurrogate(in Outer<T>.Surrogate value) => new Request<T>();
                public Outer<T>.Surrogate ConvertToSurrogate(in Request<T> value) => new Outer<T>.Surrogate();
            }
            public interface IContract : IGrain { Task Send(Request<Missing> request); }
            """);
        Assert.Contains("'Missing'", Assert.Single(diagnostics).GetMessage());
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeWithReference(string source, MetadataReference reference)
    {
        var compilation = (await CreateAnalyzerTestProject(source).AddMetadataReference(reference).GetCompilationAsync(TestContext.Current.CancellationToken))!;
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var options = compilation.Options.WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>
        {
            [GrainContractSerializationAnalyzer.ContractRuleId] = ReportDiagnostic.Warn,
            [GrainContractSerializationAnalyzer.StateRuleId] = ReportDiagnostic.Warn,
        });
        return await RunAnalyzerAsync(compilation.WithOptions(options), new GrainContractSerializationAnalyzer(), TestContext.Current.CancellationToken);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeBoundedGraph(string source)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var compilation = (await CreateAnalyzerTestProject(source).GetCompilationAsync(timeout.Token))!;
        Assert.Empty(compilation.GetDiagnostics(timeout.Token).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        compilation = compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
            new Dictionary<string, ReportDiagnostic> { [GrainContractSerializationAnalyzer.ContractRuleId] = ReportDiagnostic.Warn }));
        return await RunAnalyzerAsync(compilation, new GrainContractSerializationAnalyzer(), timeout.Token);
    }

    [Fact]
    public void UnusedGenericArgument_OrleansRoundTripSucceeds()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var payload = serializer.SerializeToArray(new Envelope<LocalOnly> { Number = 17 });
        var result = serializer.Deserialize<Envelope<LocalOnly>>(payload);
        Assert.NotNull(result);
        Assert.Equal(17, result.Number);
    }

    [Fact]
    public void JsonStorageDoesNotProvideTheCodecUsedForStateMigration()
    {
        var json = new SystemTextJsonGrainStorageSerializer(Options.Create(new SystemTextJsonGrainStorageSerializerOptions()));
        var state = new JsonOnlyState { Number = 19 };
        Assert.Equal(19, json.Deserialize<JsonOnlyState>(json.Serialize(state))!.Number);

        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        // StateStorageBridge dehydrates this wrapper through MigrationContext, independently of the storage serializer.
        Assert.Throws<CodecNotFoundException>(() => serializer.SerializeToArray(new Orleans.GrainState<JsonOnlyState>(state)));
        var payload = serializer.SerializeToArray(new Orleans.GrainState<MigratableState>(new() { Number = 23 }));
        var migrated = serializer.Deserialize<Orleans.GrainState<MigratableState>>(payload);
        Assert.NotNull(migrated);
        Assert.NotNull(migrated.State);
        Assert.Equal(23, migrated.State.Number);
    }

    public class LocalOnly { }
    public class JsonOnlyState { public int Number { get; set; } }
    [GenerateSerializer] public class Envelope<T> { [Id(0)] public int Number { get; set; } }
    [GenerateSerializer] public class MigratableState { [Id(0)] public int Number { get; set; } }

    private static async Task<ImmutableArray<Diagnostic>> RunAnalyzerAsync(Compilation compilation, DiagnosticAnalyzer analyzer,
        CancellationToken cancellationToken)
    {
        compilation = compilation.WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithSpecificDiagnosticOptions(compilation.Options.SpecificDiagnosticOptions));
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var options = new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), (exception, _, _) => failures.Enqueue(exception), true, false);
        var diagnostics = await compilation.WithAnalyzers([analyzer], options).GetAnalyzerDiagnosticsAsync(cancellationToken);
        Assert.Empty(failures);
        return diagnostics;
    }

    private const string CodecSource = """
        public abstract class Codec<T> : Orleans.Serialization.Codecs.IFieldCodec<T>
        {
            public T ReadValue<TInput>(ref Orleans.Serialization.Buffers.Reader<TInput> reader,
                Orleans.Serialization.WireProtocol.Field field) => throw new System.NotImplementedException();
            public void WriteField<TBufferWriter>(ref Orleans.Serialization.Buffers.Writer<TBufferWriter> writer,
                uint fieldIdDelta, System.Type expectedType, T value) where TBufferWriter : System.Buffers.IBufferWriter<byte>
                => throw new System.NotImplementedException();
        }

        """;
}
