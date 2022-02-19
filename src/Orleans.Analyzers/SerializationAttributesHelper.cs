using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Analyzers
{
    internal static class SerializationAttributesHelper
    {
        public static bool ShouldGenerateSerializer(TypeDeclarationSyntax declaration)
        {
            if (!declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) && declaration.HasAttribute(Constants.GenerateSerializerAttributeName))
            {
                return true;
            }

            return false;
        }

        public static (List<MemberDeclarationSyntax> UnannotatedMembers, List<MemberDeclarationSyntax> AnnotatedMembers, uint NextAvailableId) AnalyzeTypeDeclaration(TypeDeclarationSyntax declaration)
        {
            uint nextId = 0;
            var unannotatedSerializableMembers = new List<MemberDeclarationSyntax>();
            var annotatedSerializableMembers = new List<MemberDeclarationSyntax>();
            foreach (var member in declaration.Members)
            {
                // Skip members with existing [Id(x)] attributes, but record the highest value of x so that newly added attributes can begin from that value.
                if (member.TryGetAttribute(Constants.IdAttributeName, out var attribute))
                {
                    var args = attribute.ArgumentList?.Arguments;
                    if (args.HasValue)
                    {
                        if (args.Value.Count > 0)
                        {
                            var idArg = args.Value[0];
                            if (idArg.Expression is LiteralExpressionSyntax literalExpression
                                && uint.TryParse(literalExpression.Token.ValueText, out var value)
                                && value >= nextId)
                            {
                                nextId = value + 1;
                            }
                        }
                    }

                    annotatedSerializableMembers.Add(member);
                    continue;
                }

                if (!member.IsInstanceMember() || !member.IsFieldOrAutoProperty() || member.HasAttribute(Constants.NonSerializedAttribute) || member.IsAbstract())
                {
                    // No need to add any attribute.
                    continue;
                }

                unannotatedSerializableMembers.Add(member);
            }

            return (unannotatedSerializableMembers, annotatedSerializableMembers, nextId);
        }
    }
}
