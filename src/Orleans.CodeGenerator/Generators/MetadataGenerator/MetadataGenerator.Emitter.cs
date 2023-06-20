namespace Orleans.CodeGenerator.Generators.ApplicationPartsGenerator;

using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

internal partial class MetadataGenerator
{

    private class Emitter : EmitterBase
    {

        private string _metadataClassName;
        private string _metadataClassNamespace;

        private static MetadataGeneratorContext _context;

        public Emitter(IncrementalGeneratorContext context, SourceProductionContext sourceProductionContext) : base(sourceProductionContext)
        {
            _context = (MetadataGeneratorContext)context;

        }

        private string GetMetadataClassContent(string metadataClassName, string _metadataClassNamespace)
        {
            return $$"""
        namespace {{_metadataClassNamespace}}
        {
            using global::Orleans.Serialization.Codecs;
            using global::Orleans.Serialization.GeneratedCodeHelpers;

            [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", {{typeof(MetadataGenerator).Assembly.GetName().Version.ToString().GetLiteralExpression()}})]
            internal sealed class {{_metadataClassName}} : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
            {
                protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
                {
                    //config.AddAssemblySerializerTypes();
                    //config.AddAssemblyCopierTypes();
                    //config.AddAssemblyInterfaceProxyTypes();
                    //config.AddAssemblyInterfaceTypes();
                    config.AddAssemblyTypeAliases();
                }
            }
        }

        """;

        }

        public override void Emit()
        {
            _metadataClassName = "Metadata_" + SyntaxGeneration.Identifier.SanitizeIdentifierName(_context.AssemblyName);
            _metadataClassNamespace = Constants.CodeGeneratorName + "." + SyntaxGeneration.Identifier.SanitizeIdentifierName(_context.AssemblyName);

            AddMetadataClass();
            AddAssemblyAttributes();
        }

        private void AddMetadataClass()
        {
            var _metaDataClassContent = GetMetadataClassContent(_metadataClassName, _metadataClassNamespace);

            AddSource("Metadata", _metaDataClassContent);


        }



        private void AddAssemblyAttributes()
        {
            var metadataAttribute = AttributeList()
               .WithTarget(AttributeTargetSpecifier(Token(SyntaxKind.AssemblyKeyword)))
               .WithAttributes(
                   SingletonSeparatedList(
                       Attribute(_context.TypeManifestProviderAttribute.ToNameSyntax())
                           .AddArgumentListArguments(AttributeArgument(TypeOfExpression(QualifiedName(IdentifierName(_metadataClassNamespace), IdentifierName(_metadataClassName)))))));

            var assemblyAttributes = GenerateSyntax();
            assemblyAttributes.Add(metadataAttribute);


            var content = ConvertCompilationUnitSyntaxIntoString(CompilationUnit().WithAttributeLists(List(assemblyAttributes)));

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

