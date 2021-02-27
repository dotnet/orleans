using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;

namespace Orleans.Analyzers
{
    internal static class SerializationAttributesHelper
    {
        public static (List<MemberDeclarationSyntax> UnannotatedMembers, uint NextAvailableId) AnalyzeTypeDeclaration(TypeDeclarationSyntax declaration)
        {
            uint nextId = 0;
            var serializableMembers = new List<MemberDeclarationSyntax>();
            foreach (var member in declaration.Members)
            {
                if (!member.IsInstanceMember())
                {
                    continue;
                }

                if (!member.IsFieldOrAutoProperty())
                {
                    continue;
                }

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

                    continue;
                }

                if (member.HasAttribute(Constants.NonSerializedAttribute))
                {
                    // No need to add any attribute.
                    continue;
                }

                serializableMembers.Add(member);
            }

            return (serializableMembers, nextId);
        }
    }
}
