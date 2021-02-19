using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Compatibility;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.Utilities;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator.Generators
{
    internal static class ApplicationPartAttributeGenerator 
    {
        public static List<AttributeListSyntax> GenerateSyntax(WellKnownTypes wellKnownTypes, AggregatedModel model)
        {
            var attributes = new List<AttributeListSyntax>();

            foreach (var assemblyName in model.ApplicationParts)
            {
                // Generate an assembly-level attribute with an instance of that class.
                var attribute = AttributeList(
                    AttributeTargetSpecifier(Token(SyntaxKind.AssemblyKeyword)),
                    SingletonSeparatedList(
                        Attribute(wellKnownTypes.ApplicationPartAttribute.ToNameSyntax())
                            .AddArgumentListArguments(AttributeArgument(assemblyName.ToLiteralExpression()))));
                attributes.Add(attribute);
            }

            return attributes;
        }
    }
}
