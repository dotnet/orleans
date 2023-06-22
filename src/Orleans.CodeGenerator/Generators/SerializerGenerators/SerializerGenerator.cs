namespace Orleans.CodeGenerator.Generators.SerializerGenerators;

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

[Generator]
internal partial class SerializerGenerator : BaseIncrementalGenerator
{
    private static IncrementalValuesProvider<(InterfaceDeclarationSyntax, SemanticModel)> _serializerInterfaceIncremetalValues;
    private static IncrementalValuesProvider<(TypeDeclarationSyntax, SemanticModel)> _generateSerializerIncrementalValues;
    private static IncrementalValuesProvider<(TypeDeclarationSyntax, SemanticModel)> _registerSerializerIncrementalValues;
    private static IncrementalValuesProvider<(TypeDeclarationSyntax, SemanticModel)> _registerActivatorIncrementalValues;
    private static IncrementalValuesProvider<(TypeDeclarationSyntax, SemanticModel)> _registerConverterIncrementalValues;
    private static IncrementalValuesProvider<(TypeDeclarationSyntax, SemanticModel)> _registerCopierIncrementalValues;

    private static IncrementalValueProvider<CodeGeneratorOptions> _codeGeneratorOptions;

    protected override void AddSyntaxProvider(SyntaxValueProvider syntaxProvider)
    {
        _serializerInterfaceIncremetalValues = syntaxProvider.CreateSyntaxProvider(TransformPredicateForInterface, TransformForInterface);
        _generateSerializerIncrementalValues = syntaxProvider.ForAttributeWithMetadataName(Constants.GenerateSerializerAttribute, TransformPredicateForGenerateSerializer, TransformForGenerateSerializer);
        _registerSerializerIncrementalValues = syntaxProvider.ForAttributeWithMetadataName(Constants.RegisterSerializerAttribute, TransformPredicateForConcreateType, TransforForConcreateType);
        _registerActivatorIncrementalValues = syntaxProvider.ForAttributeWithMetadataName(Constants.RegisterActivatorAttribute, TransformPredicateForConcreateType, TransforForConcreateType);
        _registerConverterIncrementalValues = syntaxProvider.ForAttributeWithMetadataName(Constants.RegisterConverterAttribute, TransformPredicateForConcreateType, TransforForConcreateType);
        _registerCopierIncrementalValues = syntaxProvider.ForAttributeWithMetadataName(Constants.RegisterCopierAttribute, TransformPredicateForConcreateType, TransforForConcreateType);





        static bool TransformPredicateForConcreateType(SyntaxNode node, CancellationToken token) =>
            node is TypeDeclarationSyntax tds &&
            (tds.IsKind(SyntaxKind.ClassDeclaration) || tds.IsKind(SyntaxKind.StructDeclaration) &&
            !tds.Modifiers.Any(SyntaxKind.AbstractKeyword));

        static (TypeDeclarationSyntax, SemanticModel) TransforForConcreateType(GeneratorAttributeSyntaxContext context, CancellationToken token) => ((TypeDeclarationSyntax)context.TargetNode, context.SemanticModel);


        static (InterfaceDeclarationSyntax, SemanticModel) TransformForInterface(GeneratorSyntaxContext context, CancellationToken token) => ((InterfaceDeclarationSyntax)context.Node, context.SemanticModel);
        static bool TransformPredicateForInterface(SyntaxNode node, CancellationToken token) => node is InterfaceDeclarationSyntax;

        static (TypeDeclarationSyntax, SemanticModel) TransformForGenerateSerializer(GeneratorAttributeSyntaxContext context, CancellationToken token) => ((TypeDeclarationSyntax)context.TargetNode, context.SemanticModel);
        static bool TransformPredicateForGenerateSerializer(SyntaxNode node, CancellationToken token) => node is TypeDeclarationSyntax;

    }



    protected override IncrementalValueProvider<IncrementalGeneratorContext> Execute(IncrementalGeneratorInitializationContext context)
    {
        _codeGeneratorOptions = context.AnalyzerConfigOptionsProvider.Select(GetCodeGeneratorOptions);
        return context.CompilationProvider
            .Combine(_serializerInterfaceIncremetalValues.Collect())
            .Combine(_generateSerializerIncrementalValues.Collect())
            .Combine(_registerSerializerIncrementalValues.Collect())
            .Combine(_registerActivatorIncrementalValues.Collect())
            .Combine(_registerConverterIncrementalValues.Collect())
            .Combine(_registerCopierIncrementalValues.Collect())
            .Combine(_codeGeneratorOptions)
            .Select(SelectContext);



        static IncrementalGeneratorContext SelectContext(
            (((((((Compilation compilation, ImmutableArray<(InterfaceDeclarationSyntax, SemanticModel)> serializerInterface) Left, ImmutableArray<(TypeDeclarationSyntax, SemanticModel)> generateSerializer)
            Left, ImmutableArray<(TypeDeclarationSyntax, SemanticModel)> registerSerializer) Left, ImmutableArray<(TypeDeclarationSyntax, SemanticModel)> registerActivator)
            Left, ImmutableArray<(TypeDeclarationSyntax, SemanticModel)> registerConverter) Left, ImmutableArray<(TypeDeclarationSyntax, SemanticModel)> registerCopier) Left, CodeGeneratorOptions codeGeneratorOptions) tuple, CancellationToken token)
        {

            ParserSpecs parserSpecs = new ParserSpecs()
            {
                GenerateSerializers = tuple.Left.Left.Left.Left.Left.generateSerializer,
                RegisterActivators = tuple.Left.Left.Left.registerActivator,
                RegisterConverters = tuple.Left.Left.registerConverter,
                RegisterCopiers = tuple.Left.registerCopier,
                RegisterSerializers = tuple.Left.Left.Left.Left.registerSerializer,
                SerializerInterfaces = tuple.Left.Left.Left.Left.Left.Left.serializerInterface,
                Compilation = tuple.Left.Left.Left.Left.Left.Left.compilation,
                CodeGeneratorOptions = tuple.codeGeneratorOptions

            };

            Parser parser = new Parser(parserSpecs);
            return parser.Parse(token);
        }

    }


    protected override void RegisterSourceOutput(SourceProductionContext context, IncrementalGeneratorContext igContext)
    {
        Emitter emitter = new Emitter((SerializerGeneratorContext)igContext, context);
        emitter.Emit();
    }


    private static CodeGeneratorOptions GetCodeGeneratorOptions(AnalyzerConfigOptionsProvider acop, CancellationToken token)
    {
        try
        {
            var options = new CodeGeneratorOptions();
            if (acop.GlobalOptions.TryGetValue("build_property.orleans_immutableattributes", out var immutableAttributes) && immutableAttributes is { Length: > 0 })
            {
                options.ImmutableAttributes.AddRange(immutableAttributes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList());
            }

            if (acop.GlobalOptions.TryGetValue("build_property.orleans_aliasattributes", out var aliasAttributes) && aliasAttributes is { Length: > 0 })
            {
                options.AliasAttributes.AddRange(aliasAttributes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList());
            }

            if (acop.GlobalOptions.TryGetValue("build_property.orleans_idattributes", out var idAttributes) && idAttributes is { Length: > 0 })
            {
                options.IdAttributes.AddRange(idAttributes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList());
            }

            if (acop.GlobalOptions.TryGetValue("build_property.orleans_generateserializerattributes", out var generateSerializerAttributes) && generateSerializerAttributes is { Length: > 0 })
            {
                options.GenerateSerializerAttributes.AddRange(generateSerializerAttributes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList());
            }

            if (acop.GlobalOptions.TryGetValue("build_property.orleans_generatefieldids", out var generateFieldIds) && generateFieldIds is { Length: > 0 })
            {
                if (Enum.TryParse(generateFieldIds, out GenerateFieldIds fieldIdOption))
                    options.GenerateFieldIds = fieldIdOption;
            }
            return options;
        }
        catch (Exception)
        {
            return null;
        }
    }


}



internal partial class SerializerGenerator
{
    internal class ParserSpecs
    {
        public ImmutableArray<(TypeDeclarationSyntax, SemanticModel)> GenerateSerializers { get; set; }
        public ImmutableArray<(InterfaceDeclarationSyntax, SemanticModel)> SerializerInterfaces { get; set; }

        public ImmutableArray<(TypeDeclarationSyntax, SemanticModel)> RegisterSerializers { get; set; }

        public ImmutableArray<(TypeDeclarationSyntax, SemanticModel)> RegisterActivators { get; set; }

        public ImmutableArray<(TypeDeclarationSyntax, SemanticModel)> RegisterConverters { get; set; }

        public ImmutableArray<(TypeDeclarationSyntax, SemanticModel)> RegisterCopiers { get; set; }

        public Compilation Compilation { get; set; }

        public CodeGeneratorOptions CodeGeneratorOptions { get; set; }
    }
}