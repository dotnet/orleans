namespace Orleans.CodeGenerator.Generators.AliasGenerator;

using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

[Generator]
internal partial class AliasGenerator : BaseIncrementalGenerator
{
    private static IncrementalValuesProvider<(TypeDeclarationSyntax, SemanticModel)> _alasTypesIncremetalValues;

    protected override void AddSyntaxProvider(SyntaxValueProvider syntaxProvider)
    {
        _alasTypesIncremetalValues = syntaxProvider.ForAttributeWithMetadataName(Constants.AliasAttribute, TransformPredicate, Transform);



        (TypeDeclarationSyntax, SemanticModel) Transform(GeneratorAttributeSyntaxContext context, CancellationToken token) => ((TypeDeclarationSyntax)context.TargetNode, context.SemanticModel);
        bool TransformPredicate(SyntaxNode node, CancellationToken token) => node is TypeDeclarationSyntax;
    }
    protected override IncrementalValueProvider<IncrementalGeneratorContext> Execute(IncrementalGeneratorInitializationContext context)
    {
        return context.CompilationProvider.Combine(_alasTypesIncremetalValues.Collect()).Select(SelectContext);



        static IncrementalGeneratorContext SelectContext((Compilation, ImmutableArray<(TypeDeclarationSyntax, SemanticModel)>) tuple, CancellationToken token)
        {
            Parser parser = new Parser(tuple.Item1, tuple.Item2);
            return parser.Parse(token);
        }

    }



    protected override void RegisterSourceOutput(SourceProductionContext context, IncrementalGeneratorContext igContext)
    {
        Emitter emitter = new Emitter(igContext, context);
        emitter.Emit();
    }
}
