using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.Analyzers
{
    internal static class SyntaxHelpers
    {
        public static string GetTypeName(this AttributeSyntax attributeSyntax) => attributeSyntax.Name switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            _ => throw new NotSupportedException()
        };

        public static bool IsAttribute(this AttributeSyntax attributeSyntax, string attributeName)
        {
            var name = attributeSyntax.GetTypeName();
            return string.Equals(name, attributeName, StringComparison.Ordinal)
                || (name.StartsWith(attributeName) && name.EndsWith(nameof(Attribute)) && name.Length == attributeName.Length + nameof(Attribute).Length);
        }

        public static bool HasAttribute(this MemberDeclarationSyntax member, string attributeName)
        {
            foreach (var list in member.AttributeLists)
            {
                foreach (var attr in list.Attributes)
                {
                    if (attr.IsAttribute(attributeName))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool TryGetAttribute(this MemberDeclarationSyntax member, string attributeName, out AttributeSyntax attribute)
        {
            foreach (var list in member.AttributeLists)
            {
                foreach (var attr in list.Attributes)
                {
                    if (attr.IsAttribute(attributeName))
                    {
                        attribute = attr;
                        return true;
                    }
                }
            }

            attribute = default;
            return false;
        }

        public static bool IsInstanceMember(this MemberDeclarationSyntax member)
        {
            foreach (var modifier in member.Modifiers)
            {
                var kind = modifier.Kind();
                if (kind == SyntaxKind.StaticKeyword || kind == SyntaxKind.ConstKeyword)
                {
                    return false;
                }
            }

            return true;
        }

        public static bool IsFieldOrAutoProperty(this MemberDeclarationSyntax member)
        {
            bool isFieldOrAutoProperty = false;
            switch (member)
            {
                case FieldDeclarationSyntax:
                    isFieldOrAutoProperty = true;
                    break;
                case PropertyDeclarationSyntax property:
                    {
                        bool hasBody = property.ExpressionBody is object;
                        var accessors = property.AccessorList?.Accessors;
                        if (!hasBody && accessors.HasValue)
                        {
                            foreach (var accessor in accessors)
                            {
                                if (accessor.ExpressionBody is object || accessor.Body is object)
                                {
                                    hasBody = true;
                                    break;
                                }
                            }
                        }

                        if (!hasBody)
                        {
                            isFieldOrAutoProperty = true;
                        }

                        break;
                    }
            }

            return isFieldOrAutoProperty;
        }

    }
}
