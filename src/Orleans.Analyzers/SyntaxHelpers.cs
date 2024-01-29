using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.Analyzers
{
    internal readonly record struct AttributeArgumentBag<T>(T Value, Location Location);

    internal static class SyntaxHelpers
    {
        public static bool TryGetTypeName(this AttributeSyntax attributeSyntax, out string typeName)
        {
            typeName = attributeSyntax.Name switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                GenericNameSyntax generic => generic.Identifier.Text,
                AliasQualifiedNameSyntax aliased => aliased.Name.Identifier.Text,
                _ => null
            };

            return typeName != null;
        }

        public static bool IsAttribute(this AttributeSyntax attributeSyntax, string attributeName) =>
            attributeSyntax.TryGetTypeName(out var name) &&
            (string.Equals(name, attributeName, StringComparison.Ordinal)
             || (name.StartsWith(attributeName, StringComparison.Ordinal) && name.EndsWith(nameof(Attribute), StringComparison.Ordinal) && name.Length == attributeName.Length + nameof(Attribute).Length));

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

        public static string GetMemberNameOrDefault(this MemberDeclarationSyntax member)
        {
            if (member is PropertyDeclarationSyntax property)
            {
                return property.ChildTokens().FirstOrDefault(token => token.IsKind(SyntaxKind.IdentifierToken)).ValueText;
            }
            else if (member is FieldDeclarationSyntax field)
            {
                return field.ChildNodes().OfType<VariableDeclarationSyntax>().FirstOrDefault()?.ChildNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Identifier.ValueText;
            }

            return null;
        }

        public static bool IsAbstract(this MemberDeclarationSyntax member) => member.HasModifier(SyntaxKind.AbstractKeyword);

        public static bool IsStatic(this MemberDeclarationSyntax member) => member.HasModifier(SyntaxKind.StaticKeyword);

        public static bool HasModifier(this MemberDeclarationSyntax member, SyntaxKind modifierKind)
        {
            foreach (var modifier in member.Modifiers)
            {
                var kind = modifier.Kind();
                if (kind == modifierKind)
                {
                    return true;
                }
            }

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
                        bool hasBody = property.ExpressionBody is not null;
                        var accessors = property.AccessorList?.Accessors;
                        if (!hasBody && accessors.HasValue)
                        {
                            foreach (var accessor in accessors)
                            {
                                if (accessor.ExpressionBody is not null || accessor.Body is not null)
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

        public static bool ExtendsGrainInterface(this InterfaceDeclarationSyntax interfaceDeclaration, SemanticModel semanticModel)
        {
            if (interfaceDeclaration is null)
            {
                return false;
            }

            var symbol = semanticModel.GetDeclaredSymbol(interfaceDeclaration);
            if (symbol is null || symbol.TypeKind != TypeKind.Interface)
            {
                return false;
            }

            foreach (var interfaceSymbol in symbol.AllInterfaces)
            {
                if (Constants.IAddressibleFullyQualifiedName.Equals(interfaceSymbol.ToDisplayString(NullableFlowState.None), StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool InheritsGrainClass(this ClassDeclarationSyntax declaration, SemanticModel semanticModel)
        {
            var baseTypes = declaration.BaseList?.Types;
            if (baseTypes is null)
            {
                return false;
            }

            foreach (var baseTypeSyntax in baseTypes)
            {
                var baseTypeSymbol = semanticModel.GetTypeInfo(baseTypeSyntax.Type).Type;
                if (baseTypeSymbol is INamedTypeSymbol currentTypeSymbol)
                {
                    if (currentTypeSymbol.IsGenericType &&
                        currentTypeSymbol.TypeParameters.Length == 1 &&
                        currentTypeSymbol.BaseType is { } baseBaseTypeSymbol)
                    {
                        currentTypeSymbol = baseBaseTypeSymbol;
                    }

                    if (Constants.GrainBaseFullyQualifiedName.Equals(currentTypeSymbol.ToDisplayString(NullableFlowState.None), StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static AttributeArgumentBag<T> GetArgumentBag<T>(this AttributeSyntax attribute, SemanticModel semanticModel)
        {
            if (attribute is null)
            {
                return default;
            }

            var argument = attribute.ArgumentList?.Arguments.FirstOrDefault();
            if (argument is null || argument.Expression is not { } expression)
            {
                return default;
            }

            var constantValue = semanticModel.GetConstantValue(expression);
            return constantValue.HasValue && constantValue.Value is T value ?
                new(value, attribute.GetLocation()) : default;
        }

        public static IEnumerable<AttributeSyntax> GetAttributeSyntaxes(this SyntaxList<AttributeListSyntax> attributeLists, string attributeName) =>
            attributeLists
                .SelectMany(attributeList => attributeList.Attributes)
                .Where(attribute => attribute.IsAttribute(attributeName));
    }
}