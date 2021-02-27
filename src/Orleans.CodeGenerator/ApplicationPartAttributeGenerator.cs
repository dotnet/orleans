using System.Collections.Generic;
using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    internal static class ApplicationPartAttributeGenerator 
    {
        public static List<AttributeListSyntax> GenerateSyntax(LibraryTypes wellKnownTypes, MetadataModel model)
        {
            var attributes = new List<AttributeListSyntax>();

            foreach (var assemblyName in model.ApplicationParts)
            {
                // Generate an assembly-level attribute with an instance of that class.
                var attribute = AttributeList(
                    AttributeTargetSpecifier(Token(SyntaxKind.AssemblyKeyword)),
                    SingletonSeparatedList(
                        Attribute(wellKnownTypes.ApplicationPartAttribute.ToNameSyntax())
                            .AddArgumentListArguments(AttributeArgument(assemblyName.GetLiteralExpression()))));
                attributes.Add(attribute);
            }

            return attributes;
        }
    }
}
