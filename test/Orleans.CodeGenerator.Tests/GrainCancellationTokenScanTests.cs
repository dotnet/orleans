using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Tests that generated invokables opt out of the runtime's <c>GrainCancellationToken</c> argument scan
/// only when none of their arguments can hold a <c>GrainCancellationToken</c>.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class GrainCancellationTokenScanTests
{
    [Theory]
    [InlineData("Task M(System.Guid id, int count, uint? sequence)", false)]
    [InlineData("Task M(string value)", false)]
    [InlineData("Task M(int value, System.Threading.CancellationToken cancellationToken)", false)]
    [InlineData("Task M()", true)]
    [InlineData("Task M(int value, GrainCancellationToken token)", true)]
    [InlineData("Task M(object value)", true)]
    [InlineData("Task M(System.IDisposable value)", true)]
    [InlineData("Task M<T>(T value)", true)]
    public async Task MayHaveGrainCancellationTokenReflectsParameterTypes(string signature, bool expected)
    {
        var source = $$"""
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface IScanGrain : IGrainWithIntegerKey
            {
                {{signature}};
            }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
            optionsProvider: TestCompilationHelper.CreateOptionsProvider());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics, TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics);
        Assert.Empty(outputCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var requestBase = outputCompilation.GetTypeByMetadataName("Orleans.Runtime.RequestBase");
        Assert.NotNull(requestBase);
        var invokable = Assert.Single(GetTypes(outputCompilation.Assembly.GlobalNamespace), type => DerivesFrom(type, requestBase));
        var property = invokable.GetMembers("MayHaveGrainCancellationToken").OfType<IPropertySymbol>().SingleOrDefault();
        var actual = property is null || GetConstantValue(outputCompilation, property);

        Assert.Equal(expected, actual);
    }

    private static bool GetConstantValue(Compilation compilation, IPropertySymbol property)
    {
        var declaration = (PropertyDeclarationSyntax)Assert.Single(property.DeclaringSyntaxReferences).GetSyntax(TestContext.Current.CancellationToken);
        var expression = Assert.IsType<ArrowExpressionClauseSyntax>(declaration.ExpressionBody).Expression;
        var value = compilation.GetSemanticModel(declaration.SyntaxTree).GetConstantValue(expression, TestContext.Current.CancellationToken);
        Assert.True(value.HasValue);
        return Assert.IsType<bool>(value.Value);
    }

    private static IEnumerable<INamedTypeSymbol> GetTypes(INamespaceOrTypeSymbol container)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceOrTypeSymbol nested)
            {
                if (nested is INamedTypeSymbol type)
                {
                    yield return type;
                }

                foreach (var descendant in GetTypes(nested))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType))
            {
                return true;
            }
        }

        return false;
    }
}
