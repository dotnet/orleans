namespace Orleans.CodeGenerator.Generators.ApplicationPartsGenerator;

using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

internal partial class ApplicationPartsGenerator
{

    private class Emitter : EmitterBase
    {

        private static string _metaDataClassString =
            $$"""
        namespace OrleansCodeGen.HelloWorldGrains
        {
            using global::Orleans.Serialization.Codecs;
            using global::Orleans.Serialization.GeneratedCodeHelpers;

            [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", {{typeof(ApplicationPartsGenerator).Assembly.GetName().Version.ToString().GetLiteralExpression()}})]
            internal sealed class Metadata_HelloWorldGrains : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
            {
                protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
                {

                    //config.AddAssemblySerializerTypes();
                    //config.AddAssemblyCopierTypes();
                    //config.AddAssemblyInterfaceProxyTypes();
                    //config.AddAssemblyInterfaceTypes();
                    //config.AddAssemblyTypeAliases();
                }
            }
        }

        """;

        private static MetadataGeneratorContext _context;

        public Emitter(IncrementalGeneratorContext context, SourceProductionContext sourceProductionContext) : base(sourceProductionContext)
        {
            _context = (MetadataGeneratorContext)context;
        }

        public override void Emit()
        {
            AddAssemblyAttributes();
            AddMetadataClass();
        }

        private void AddMetadataClass()
        {

            AddSource("Metadata", _metaDataClassString);


        }

        private void AddAssemblyAttributes()
        {
            var metadataClassNamespace = Constants.CodeGeneratorName + "." + SyntaxGeneration.Identifier.SanitizeIdentifierName(_context.AssemblyName);
            var metadataClass = "Metadata_" + SyntaxGeneration.Identifier.SanitizeIdentifierName(_context.AssemblyName);


            var metadataAttribute = AttributeList()
               .WithTarget(AttributeTargetSpecifier(Token(SyntaxKind.AssemblyKeyword)))
               .WithAttributes(
                   SingletonSeparatedList(
                       Attribute(_context.TypeManifestProviderAttribute.ToNameSyntax())
                           .AddArgumentListArguments(AttributeArgument(TypeOfExpression(QualifiedName(IdentifierName(metadataClassNamespace), IdentifierName(metadataClass)))))));

            var assemblyAttributes = GenerateSyntax();
            assemblyAttributes.Add(metadataAttribute);


            var content = ConvertIntoString(CompilationUnit().WithAttributeLists(List(assemblyAttributes)));

            AddSource("ApplicationPart", content);
        }

        public static List<AttributeListSyntax> GenerateSyntax()
        {
            var attributes = new List<AttributeListSyntax>();

            foreach (var assemblyName in _context.ApplicationParts)
            {
                // Generate an assembly-level attribute with an instance of that class.
                var attribute = AttributeList(
                    AttributeTargetSpecifier(Token(SyntaxKind.AssemblyKeyword)),
                    SingletonSeparatedList(
                        Attribute(_context.ApplicationPartAttribute.ToNameSyntax())
                            .AddArgumentListArguments(AttributeArgument(assemblyName.GetLiteralExpression()))));
                attributes.Add(attribute);
            }

            return attributes;
        }
    }
}

