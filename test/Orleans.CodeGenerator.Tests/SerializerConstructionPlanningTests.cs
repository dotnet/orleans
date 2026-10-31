using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class SerializerConstructionPlanningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConstructionPlansMatchEmittedConstructorsServicesAndBaseContracts(bool hotReload)
    {
        var compilation = await TestCompilationHelper.CreateCompilation("""
            using Orleans;
            [GenerateSerializer] public sealed class Empty { }
            [GenerateSerializer] public enum Flavor { First, Second }
            [GenerateSerializer] public struct Scalar { [Id(0)] public int Value; }
            [GenerateSerializer] public class Node { [Id(0)] public Node Next; }
            [GenerateSerializer] public sealed class Box<T>
            {
                [Id(0)] public T[] Flat;
                [Id(1)] public T[][] Nested;
                [Id(2)] public byte[] Bytes;
                [Id(3)] public System.Collections.Generic.List<T> Values;
            }
            [GenerateSerializer, Orleans.Immutable] public sealed class Immutable { [Id(0)] public int Value; }
            [GenerateSerializer, Orleans.Immutable] public class UnsealedImmutable { [Id(0)] public int Value; }
            public class Base { }
            [GenerateSerializer] public class Derived : Base { [Id(0)] public Node Value; }
            [GenerateSerializer] public abstract class Abstract { [Id(0)] public Node Value; }
            [GenerateSerializer] public class ExceptionPayload : System.Exception { [Id(0)] public Node Value; }
            [GenerateSerializer, UseActivator] public sealed class Activated { [Id(0)] public Node Value; }
            """);
        var services = new GeneratorServices(compilation, new CodeGeneratorOptions { HotReloadSafe = hotReload });
        var serializer = new SerializerGenerator(services);
        var copier = new CopierGenerator(services);
        foreach (var symbol in compilation.Assembly.GlobalNamespace.GetTypeMembers()
            .Where(symbol => symbol.HasAttribute(services.LibraryTypes.GenerateSerializerAttribute)))
        {
            var model = SerializableSourceOutputGenerator.CreateSerializableTypeDescription(services, symbol)!;
            Verify(serializer.GetConstruction(model), ParseClass(serializer.Generate(model)), services.LibraryTypes.BaseCodec_1, services.LibraryTypes.ArrayCodec);
            Verify(copier.GetConstruction(model), ParseClass(copier.GenerateCopier(model, new())), services.LibraryTypes.BaseCopier_1, services.LibraryTypes.ArrayCopier);
        }

        static void Verify(GeneratedTypeConstruction plan, ClassDeclarationSyntax? declaration, INamedTypeSymbol baseContract, INamedTypeSymbol arrayService)
        {
            var constructor = declaration?.Members.OfType<ConstructorDeclarationSyntax>().SingleOrDefault();
            Assert.Equal(constructor?.ParameterList.Parameters.Select(static parameter => parameter.ToString()) ?? [],
                plan.Parameters.Select(static parameter => parameter.ToSyntax().NormalizeWhitespace().ToString()));
            Assert.Equal(declaration?.BaseList?.Types.Any(type =>
                type.Type is QualifiedNameSyntax { Right: GenericNameSyntax generic }
                && generic.Identifier.ValueText == baseContract.Name) == true, plan.HasBaseContract);
            var requests = declaration?.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(static invocation => invocation.Expression is MemberAccessExpressionSyntax
                { Name: GenericNameSyntax { Identifier.ValueText: "GetService" } })
                .Select(static invocation => ((GenericNameSyntax)((MemberAccessExpressionSyntax)invocation.Expression).Name)
                    .TypeArgumentList.Arguments.Single())
                .Where(type => type is QualifiedNameSyntax { Right: GenericNameSyntax generic }
                    && generic.Identifier.ValueText == arrayService.Name)
                .Select(static type => type.ToString()).Distinct().Order(StringComparer.Ordinal).ToArray() ?? [];
            Assert.Equal(requests, plan.CanonicalArrays
                .Select(array => arrayService.Construct(array.ElementType).ToTypeSyntax().ToString())
                .Distinct().Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task ConstructionPlanningUsesServiceFieldsWithoutEmittingSerializationBodies()
    {
        var compilation = await TestCompilationHelper.CreateCompilation("""
            [Orleans.GenerateSerializer]
            public class Payload<T> { [Orleans.Id(0)] public T[] Values; }
            """);
        var services = new GeneratorServices(compilation, new CodeGeneratorOptions());
        var symbol = compilation.GetTypeByMetadataName("Payload`1")!;
        var model = SerializableSourceOutputGenerator.CreateSerializableTypeDescription(services, symbol)!;
        var constructionOnly = new ConstructionOnlyDescription(compilation, symbol, model.Members, services.LibraryTypes);
        var serializer = new SerializerGenerator(services);
        var copier = new CopierGenerator(services);
        foreach (var plan in new[] { serializer.GetConstruction(constructionOnly), copier.GetConstruction(constructionOnly) })
        {
            Assert.True(plan.HasBaseContract);
            Assert.True(Assert.Single(plan.Parameters).IsCodecProvider);
            Assert.Equal("T[]", Assert.Single(plan.CanonicalArrays).ToDisplayString());
        }

        Assert.Throws<InvalidOperationException>(() => serializer.Generate(constructionOnly));
        Assert.Throws<InvalidOperationException>(() => copier.GenerateCopier(constructionOnly, new()));
    }

    [Theory]
    [InlineData("Missing[]")]
    [InlineData("Missing<T>[]")]
    [InlineData("Container.Missing<T>[]")]
    public async Task IncompleteArrayDependenciesProduceContextDiagnostics(string memberType)
    {
        var compilation = await TestCompilationHelper.CreateCompilation($$"""
            [Orleans.GenerateSerializer]
            public sealed class Payload<T> { [Orleans.Id(0)] public {{memberType}} Values; }
            public class Container { }
            """);
        Assert.Contains(compilation.GetDiagnostics(TestContext.Current.CancellationToken),
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var services = new GeneratorServices(compilation, new CodeGeneratorOptions());
        var root = compilation.GetTypeByMetadataName("Payload`1")!.Construct(compilation.GetSpecialType(SpecialType.System_Int32));
        Assert.False(SerializerFactoryGenerator.TryCreate(services, [root], TestContext.Current.CancellationToken, out var graph, out var failure));
        Assert.Null(graph);
        Assert.Contains("Missing", failure!.Type.ToDisplayString(), StringComparison.Ordinal);
        Assert.False(failure.RequiresImplementation);
    }

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public async Task ConstructionPlanningPreservesClosedGraphBound(int typeCount, bool accepted)
    {
        var source = string.Join("\n", Enumerable.Range(0, typeCount)
            .Select(static index => $"[Orleans.GenerateSerializer] public sealed class Payload{index} {{ }}"));
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var services = new GeneratorServices(compilation, new CodeGeneratorOptions());
        var roots = compilation.Assembly.GlobalNamespace.GetTypeMembers();
        Assert.Equal(accepted, SerializerFactoryGenerator.TryCreate(services, roots, TestContext.Current.CancellationToken, out var graph, out var failure));
        if (accepted)
        {
            Assert.Null(failure);
            Assert.Equal(typeCount, graph!.Registrations.Count);
            Assert.Equal(typeCount, graph.ConfigurationStatements.Split("options.AddSerializer<", StringSplitOptions.None).Length - 1);
        }
        else
        {
            Assert.Null(graph);
            Assert.Equal("the dependency graph exceeds 1024 closed types; declare a finite serialization graph", failure!.Reason);
        }
    }

    private static ClassDeclarationSyntax? ParseClass(string? declaration)
        => declaration is null ? null : SyntaxFactory.ParseCompilationUnit(declaration).Members.OfType<ClassDeclarationSyntax>().Single();

    private sealed class ConstructionOnlyDescription(Compilation compilation, INamedTypeSymbol type, List<IMemberDescription> members, LibraryTypes library)
        : SerializableTypeDescription(compilation, type, false, members, library), ISerializableTypeDescription
    {
        ExpressionSyntax ISerializableTypeDescription.GetObjectCreationExpression()
            => throw new InvalidOperationException("Serialization body emission was requested.");
    }
}
