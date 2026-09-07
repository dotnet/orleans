using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Diagnostics;

namespace Orleans.CodeGenerator.Tests;

public class RpcParameterFieldIdTests
{
    [Fact]
    public async Task AutomaticIds_ExcludeCancellationTokens_AndPreserveClrArgumentOrdinals()
    {
        const string code = """
            using Orleans;
            using Orleans.Runtime;
            using System.Threading;
            using System.Threading.Tasks;

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface ITestGrain : IGrainWithIntegerKey
            {
                ValueTask TokenFirst(CancellationToken cancellationToken, int first, string second);
                ValueTask TokenMiddle(int first, CancellationToken cancellationToken, string second);
                ValueTask TokenLast(int first, string second, CancellationToken cancellationToken);
            }
            """;

        var result = await RunGenerator(code);

        var diagnostics = result.Diagnostics
            .Where(static diagnostic => diagnostic.Id == DiagnosticRuleId.CancellationTokenNotLast)
            .ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, static diagnostic => Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity));
        Assert.Equal(["cancellationToken", "cancellationToken"], diagnostics.Select(GetDiagnosticSource));
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Id == DiagnosticRuleId.CancellationTokenNotLast
                && diagnostic.GetMessage().Contains("TokenLast", StringComparison.Ordinal));
        AssertInvokable(
            result,
            "TokenFirst",
            ["arg0", "arg1", "arg2"],
            ["global::System.Threading.CancellationToken", "int", "string"],
            [(0, "arg1"), (1, "arg2")]);
        AssertInvokable(
            result,
            "TokenMiddle",
            ["arg0", "arg1", "arg2"],
            ["int", "global::System.Threading.CancellationToken", "string"],
            [(0, "arg0"), (1, "arg2")]);
        AssertInvokable(
            result,
            "TokenLast",
            ["arg0", "arg1", "arg2"],
            ["int", "string", "global::System.Threading.CancellationToken"],
            [(0, "arg0"), (1, "arg1")]);
    }

    [Fact]
    public async Task ExplicitIds_OverrideAutomaticSerializedOrdinals_AndOrderWireFields()
    {
        const string code = """
            using Orleans;
            using Orleans.Runtime;
            using System.Threading;
            using System.Threading.Tasks;

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface ITestGrain : IGrainWithIntegerKey
            {
                ValueTask Call([Id(7)] int first, CancellationToken cancellationToken, string second, [Id(12)] long third);
            }
            """;

        var result = await RunGenerator(code);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.CancellationTokenNotLast, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Call", diagnostic.GetMessage(), StringComparison.Ordinal);
        AssertInvokable(
            result,
            "Call",
            ["arg0", "arg1", "arg2", "arg3"],
            ["int", "global::System.Threading.CancellationToken", "string", "long"],
            [(1, "arg2"), (7, "arg0"), (12, "arg3")]);
    }

    [Fact]
    public async Task DuplicateExplicitIds_ReportOffendingParameter()
    {
        const string code = """
            using Orleans;
            using Orleans.Runtime;
            using System.Threading.Tasks;

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface ITestGrain : IGrainWithIntegerKey
            {
                ValueTask Call([Id(4)] int first, [Id(4)] string second);
            }
            """;

        var diagnostic = Assert.Single((await RunGenerator(code)).Diagnostics);

        Assert.Equal(DiagnosticRuleId.InvalidRpcParameterId, diagnostic.Id);
        Assert.Contains("second", GetDiagnosticSource(diagnostic), StringComparison.Ordinal);
        Assert.Contains("already used by parameter 'first'", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitIdConflictingWithAutomaticId_ReportsOffendingParameter()
    {
        const string code = """
            using Orleans;
            using Orleans.Runtime;
            using System.Threading.Tasks;

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface ITestGrain : IGrainWithIntegerKey
            {
                ValueTask Call(int first, [Id(0)] string second);
            }
            """;

        var diagnostic = Assert.Single((await RunGenerator(code)).Diagnostics);

        Assert.Equal(DiagnosticRuleId.InvalidRpcParameterId, diagnostic.Id);
        Assert.Contains("second", GetDiagnosticSource(diagnostic), StringComparison.Ordinal);
        Assert.Contains("already used by parameter 'first'", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationTokenWithExplicitId_ReportsOffendingParameter()
    {
        const string code = """
            using Orleans;
            using Orleans.Runtime;
            using System.Threading;
            using System.Threading.Tasks;

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface ITestGrain : IGrainWithIntegerKey
            {
                ValueTask Call([Id(0)] CancellationToken cancellationToken);
            }
            """;

        var diagnostic = Assert.Single((await RunGenerator(code)).Diagnostics);

        Assert.Equal(DiagnosticRuleId.InvalidRpcParameterId, diagnostic.Id);
        Assert.Contains("cancellationToken", GetDiagnosticSource(diagnostic), StringComparison.Ordinal);
        Assert.Contains("cancellation tokens are not serialized", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    private static void AssertInvokable(
        GeneratorRunResult result,
        string methodName,
        string[] expectedArgumentFields,
        string[] expectedReflectionParameterTypes,
        (uint FieldId, string FieldName)[] expectedSerializedFields)
    {
        var invokable = GetGeneratedClasses(result)
            .Single(declaration => declaration.Identifier.ValueText.StartsWith("Invokable_", StringComparison.Ordinal)
                && declaration.Members.OfType<MethodDeclarationSyntax>().Any(method =>
                    method.Identifier.ValueText == "GetMethodName"
                    && method.ExpressionBody?.Expression is LiteralExpressionSyntax literal
                    && string.Equals(literal.Token.ValueText, methodName, StringComparison.Ordinal)));
        var argumentFields = invokable.Members
            .OfType<FieldDeclarationSyntax>()
            .Where(field => field.Modifiers.Any(SyntaxKind.PublicKeyword))
            .SelectMany(field => field.Declaration.Variables)
            .Select(variable => variable.Identifier.ValueText)
            .ToArray();
        Assert.Equal(expectedArgumentFields, argumentFields);

        var methodBackingField = Assert.Single(
            invokable.Members.OfType<FieldDeclarationSyntax>(),
            field => field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "MethodBackingField"));
        var parameterTypes = Assert.Single(
                methodBackingField.DescendantNodes().OfType<ImplicitArrayCreationExpressionSyntax>())
            .Initializer.Expressions
            .OfType<TypeOfExpressionSyntax>()
            .Select(static expression => expression.Type.ToString())
            .ToArray();
        Assert.Equal(expectedReflectionParameterTypes, parameterTypes);

        var getArgument = Assert.Single(
            invokable.Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == "GetArgument");
        var setArgument = Assert.Single(
            invokable.Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == "SetArgument");
        for (var index = 0; index < expectedArgumentFields.Length; index++)
        {
            Assert.Contains($"case {index}:", getArgument.ToString(), StringComparison.Ordinal);
            Assert.Contains($"return {expectedArgumentFields[index]};", getArgument.ToString(), StringComparison.Ordinal);
            Assert.Contains($"case {index}:", setArgument.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{expectedArgumentFields[index]} =", setArgument.ToString(), StringComparison.Ordinal);
        }

        var invokeInner = Assert.Single(
            invokable.Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == "InvokeInner");
        Assert.Contains(
            $"_target.{methodName}({string.Join(", ", expectedArgumentFields)})",
            invokeInner.ToString(),
            StringComparison.Ordinal);

        var codec = GetGeneratedClasses(result)
            .Single(declaration => declaration.Identifier.ValueText == $"Codec_{invokable.Identifier.ValueText}");
        var deserialize = Assert.Single(
            codec.Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == "Deserialize");
        var serializedFields = deserialize.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Select(static statement => TryGetSerializedField(statement))
            .OfType<(uint FieldId, string FieldName)>()
            .ToArray();
        Assert.Equal(expectedSerializedFields, serializedFields);
    }

    private static (uint FieldId, string FieldName)? TryGetSerializedField(IfStatementSyntax statement)
    {
        if (statement.Condition is not BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.EqualsExpression,
                Left: IdentifierNameSyntax { Identifier.ValueText: "id" },
                Right: LiteralExpressionSyntax fieldIdLiteral
            })
        {
            return null;
        }

        var assignment = statement.Statement.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault(static assignment =>
                assignment.Left is MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: "instance" }
                });
        if (assignment?.Left is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        return (Convert.ToUInt32(fieldIdLiteral.Token.Value), memberAccess.Name.Identifier.ValueText);
    }

    private static IEnumerable<ClassDeclarationSyntax> GetGeneratedClasses(GeneratorRunResult result) =>
        result.GeneratedSources.SelectMany(static source =>
            CSharpSyntaxTree.ParseText(source.SourceText.ToString().TrimStart('\uFEFF'))
                .GetCompilationUnitRoot()
                .DescendantNodes()
                .OfType<ClassDeclarationSyntax>());

    private static string GetDiagnosticSource(Diagnostic diagnostic) =>
        diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan);

    private static async Task<GeneratorRunResult> RunGenerator(string code)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(code);
        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new OrleansSerializationSourceGenerator().AsSourceGenerator());
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult().Results.Single();
    }
}
