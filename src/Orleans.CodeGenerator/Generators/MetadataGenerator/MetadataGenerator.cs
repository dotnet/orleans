namespace Orleans.CodeGenerator.Generators.ApplicationPartsGenerator;
using System.Threading;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Generators;

[Generator]
internal partial class MetadataGenerator : BaseIncrementalGenerator
{


    protected override void AddSyntaxProvider(SyntaxValueProvider syntaxProvider)
    { }



    protected override IncrementalValueProvider<IncrementalGeneratorContext> Execute(IncrementalGeneratorInitializationContext context)
    {
        return context.CompilationProvider.Select(SelectApplicationParts);



        static IncrementalGeneratorContext SelectApplicationParts(Compilation compilation, CancellationToken token)
        {
            Parser parser = new Parser(compilation);
            return parser.Parse(token);
        }

    }


    protected override void RegisterSourceOutput(SourceProductionContext context, IncrementalGeneratorContext igContext)
    {
        Emitter emitter = new Emitter(igContext, context);
        emitter.Emit();
    }
}
