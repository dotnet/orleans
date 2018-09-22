using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Utilities;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator.Generators
{
    internal static class GeneratedCodeAttributeGenerator
    {
        internal static AttributeSyntax GetGeneratedCodeAttributeSyntax(WellKnownTypes wellKnownTypes)
        {
            return
                Attribute(wellKnownTypes.GeneratedCodeAttribute.ToNameSyntax())
                    .AddArgumentListArguments(
                        AttributeArgument(CodeGenerator.ToolName.ToLiteralExpression()),
                        AttributeArgument(CodeGenerator.Version.ToLiteralExpression()));
        }
    }
}
