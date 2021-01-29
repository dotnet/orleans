using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator.Utilities
{
    /// <summary>
    /// Extensions for syntax types.
    /// </summary>
    internal static class SymbolSyntaxExtensions
    {
        public static string GetParsableReplacementName(this INamedTypeSymbol originalType, string replacementTypeName)
        {
            var t = originalType.WithoutTypeParameters();
            var ns = t.GetNamespaceName();
            if (!string.IsNullOrWhiteSpace(ns)) ns += '.';

            return ns + replacementTypeName + t.GetGenericTypeSuffix();
        }

        public static SyntaxKind ToSyntaxKind(this SpecialType specialType)
        {
            switch (specialType)
            {
                case SpecialType.System_Object:
                    return SyntaxKind.ObjectKeyword;
                case SpecialType.System_Void:
                    return SyntaxKind.VoidKeyword;
                case SpecialType.System_Boolean:
                    return SyntaxKind.BoolKeyword;
                case SpecialType.System_Char:
                    return SyntaxKind.CharKeyword;
                case SpecialType.System_SByte:
                    return SyntaxKind.SByteKeyword;
                case SpecialType.System_Byte:
                    return SyntaxKind.ByteKeyword;
                case SpecialType.System_Int16:
                    return SyntaxKind.ShortKeyword;
                case SpecialType.System_UInt16:
                    return SyntaxKind.UShortKeyword;
                case SpecialType.System_Int32:
                    return SyntaxKind.IntKeyword;
                case SpecialType.System_UInt32:
                    return SyntaxKind.UIntKeyword;
                case SpecialType.System_Int64:
                    return SyntaxKind.LongKeyword;
                case SpecialType.System_UInt64:
                    return SyntaxKind.ULongKeyword;
                case SpecialType.System_Decimal:
                    return SyntaxKind.DecimalKeyword;
                case SpecialType.System_Single:
                    return SyntaxKind.FloatKeyword;
                case SpecialType.System_Double:
                    return SyntaxKind.DoubleKeyword;
                case SpecialType.System_String:
                    return SyntaxKind.StringKeyword;
                default:
                    return SyntaxKind.None;
            }
        }

        public static bool GetPredefinedType(INamedTypeSymbol type, out PredefinedTypeSyntax predefined)
        {
            var kind = type.SpecialType.ToSyntaxKind();
            if (kind == SyntaxKind.None)
            {
                predefined = null;
                return false;
            }

            predefined = PredefinedType(Token(kind));
            return true;
        }

        public static TypeSyntax ToTypeSyntax(this ITypeSymbol typeSymbol)
        {
            if (typeSymbol is IErrorTypeSymbol error)
            {
                Console.WriteLine(
                    $"Warning: attempted to get TypeSyntax for unknown (error) type, \"{error}\"."
                    + $" Possible reason: {error.CandidateReason}."
                    + $" Possible candidates: {string.Join(", ", error.CandidateSymbols.Select(s => s.ToDisplayString()))}");
            }

            if (typeSymbol is INamedTypeSymbol named) return named.ToTypeSyntax();
            return ParseTypeName(typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }

        public static TypeSyntax ToTypeSyntax(this INamedTypeSymbol type, Func<SymbolDisplayFormat, SymbolDisplayFormat> modifyFormat = null)
        {
            if (GetPredefinedType(type, out var predefined))
            {
                return predefined;
            }

            var format = SymbolDisplayFormat.FullyQualifiedFormat;
            if (modifyFormat != null) format = modifyFormat(format);

            return ParseTypeName(type.ToDisplayString(format));
        }

        public static NameSyntax ToNameSyntax(this INamedTypeSymbol type, bool includeNamespace = true)
        {
            var format = SymbolDisplayFormat.FullyQualifiedFormat;
            if (!includeNamespace)
            {
                format = format.WithTypeQualificationStyle(SymbolDisplayTypeQualificationStyle.NameAndContainingTypes);
            }

            return ParseName(type.ToDisplayString(format));
        }

        public static ParenthesizedExpressionSyntax GetBindingFlagsParenthesizedExpressionSyntax(SyntaxKind operationKind, params System.Reflection.BindingFlags[] bindingFlags)
        {
            if (bindingFlags.Length < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bindingFlags),
                    $"Can't create parenthesized binary expression with {bindingFlags.Length} arguments");
            }

            var flags = AliasQualifiedName("global", IdentifierName("System")).Member("Reflection").Member("BindingFlags");
            var bindingFlagsBinaryExpression = BinaryExpression(
                operationKind,
                flags.Member(bindingFlags[0].ToString()),
                flags.Member(bindingFlags[1].ToString()));
            for (var i = 2; i < bindingFlags.Length; i++)
            {
                bindingFlagsBinaryExpression = BinaryExpression(
                    operationKind,
                    bindingFlagsBinaryExpression,
                    flags.Member(bindingFlags[i].ToString()));
            }

            return ParenthesizedExpression(bindingFlagsBinaryExpression);
        }

        public static MethodDeclarationSyntax GetDeclarationSyntax(this IMethodSymbol method)
        {
            if (!(method.ReturnType is INamedTypeSymbol returnType)) throw new InvalidOperationException($"Return type \"{method.ReturnType?.GetType()}\" for method {method} is not a named type.");
            var syntax =
                MethodDeclaration(ToTypeSyntax(returnType), method.Name.ToIdentifier())
                    .WithParameterList(ParameterList().AddParameters(method.Parameters.Select(p => Parameter(p.Name.ToIdentifier()).WithType(p.Type.ToTypeSyntax())).ToArray()));
            if (method.IsGenericMethod)
            {
                syntax = syntax.WithTypeParameterList(TypeParameterList().AddParameters(method.GetTypeParameterListSyntax()));

                // Handle type constraints on type parameters.
                var typeParameters = method.TypeParameters;
                var typeParameterConstraints = new List<TypeParameterConstraintClauseSyntax>();
                foreach (var arg in typeParameters)
                {
                    typeParameterConstraints.AddRange(GetTypeParameterConstraints(arg));
                }

                if (typeParameterConstraints.Count > 0)
                {
                    syntax = syntax.AddConstraintClauses(typeParameterConstraints.ToArray());
                }
            }

            syntax = syntax.WithModifiers(syntax.Modifiers.AddAccessibilityModifiers(method.DeclaredAccessibility));

            return syntax;
        }

        public static ArrayTypeSyntax GetArrayTypeSyntax(this TypeSyntax type)
        {
            return ArrayType(type, SingletonList(ArrayRankSpecifier(SingletonSeparatedList<ExpressionSyntax>(OmittedArraySizeExpression()))));
        }

        public static ConstructorDeclarationSyntax GetConstructorDeclarationSyntax(this IMethodSymbol constructor, string typeName)
        {
            var syntax =
                ConstructorDeclaration(typeName.ToIdentifier())
                    .WithParameterList(ParameterList().AddParameters(constructor.GetParameterListSyntax()));

            return syntax.WithModifiers(syntax.Modifiers.AddAccessibilityModifiers(constructor.DeclaredAccessibility));
        }

        public static SyntaxTokenList AddAccessibilityModifiers(this SyntaxTokenList syntax, Accessibility accessibility)
        {
            switch (accessibility)
            {
                case Accessibility.Public:
                    syntax = With(syntax, SyntaxKind.PublicKeyword);
                    break;
                case Accessibility.Private:
                    syntax = With(syntax, SyntaxKind.PrivateKeyword);
                    break;
                case Accessibility.Internal:
                    syntax = With(syntax, SyntaxKind.InternalKeyword);
                    break;
                case Accessibility.Protected:
                    syntax = With(syntax, SyntaxKind.ProtectedKeyword);
                    break;
                case Accessibility.ProtectedOrInternal:
                    syntax = With(With(syntax, SyntaxKind.ProtectedKeyword), SyntaxKind.InternalKeyword);
                    break;
                case Accessibility.ProtectedAndInternal:
                    syntax = With(With(syntax, SyntaxKind.PrivateKeyword), SyntaxKind.ProtectedKeyword);
                    break;
            }

            SyntaxTokenList With(SyntaxTokenList s, SyntaxKind keyword)
            {
                foreach (var t in s)
                {
                    if (t.IsKind(keyword)) return s;
                }

                return s.Add(Token(keyword));
            }

            return syntax;
        }

        public static ParameterSyntax[] GetParameterListSyntax(this IMethodSymbol method)
        {
            return
                method.Parameters
                    .Select(
                        (parameter, parameterIndex) =>
                        Parameter(parameter.Name.ToIdentifier())
                            .WithType(parameter.Type.ToTypeSyntax()))
                    .ToArray();
        }

        public static TypeParameterSyntax[] GetTypeParameterListSyntax(this IMethodSymbol method)
        {
            return method.TypeParameters
                    .Select(parameter => TypeParameter(parameter.Name))
                    .ToArray();
        }

        public static TypeParameterConstraintClauseSyntax[] GetTypeConstraintSyntax(this INamedTypeSymbol type)
        {
            if (type.IsGenericType)
            {
                var constraints = new List<TypeParameterConstraintClauseSyntax>();
                foreach (var genericParameter in type.GetHierarchyTypeParameters())
                {
                    constraints.AddRange(GetTypeParameterConstraints(genericParameter));
                }

                return constraints.ToArray();
            }

            return new TypeParameterConstraintClauseSyntax[0];
        }

        private static TypeParameterConstraintClauseSyntax[] GetTypeParameterConstraints(ITypeParameterSymbol genericParameter)
        {
            var results = new List<TypeParameterConstraintClauseSyntax>();
            var parameterConstraints = new List<TypeParameterConstraintSyntax>();

            // The "class" or "struct" constraints must come first.
            if (genericParameter.HasReferenceTypeConstraint)
            {
                parameterConstraints.Add(ClassOrStructConstraint(SyntaxKind.ClassConstraint));
            }
            else if (genericParameter.HasValueTypeConstraint)
            {
                parameterConstraints.Add(ClassOrStructConstraint(SyntaxKind.StructConstraint));
            }

            // Follow with the base class or interface constraints.
            foreach (var genericType in genericParameter.ConstraintTypes)
            {
                // If the "struct" constraint was specified, skip the corresponding "ValueType" constraint.
                if (genericType.SpecialType == SpecialType.System_ValueType)
                {
                    continue;
                }

                parameterConstraints.Add(TypeConstraint(genericType.ToTypeSyntax()));
            }

            // The "new()" constraint must be the last constraint in the sequence.
            if (genericParameter.HasConstructorConstraint
                && !genericParameter.HasValueTypeConstraint)
            {
                parameterConstraints.Add(ConstructorConstraint());
            }

            if (parameterConstraints.Count > 0)
            {
                results.Add(
                    TypeParameterConstraintClause(genericParameter.Name)
                                 .AddConstraints(parameterConstraints.ToArray()));
            }

            return results.ToArray();
        }

        public static MemberAccessExpressionSyntax Member(this ExpressionSyntax instance, string member)
        {
            return instance.Member(member.ToIdentifierName());
        }

        public static MemberAccessExpressionSyntax Member(
            this ExpressionSyntax instance,
            string member,
            params INamedTypeSymbol[] genericTypes)
        {
            return
                instance.Member(
                    member.ToGenericName()
                        .AddTypeArgumentListArguments(genericTypes.Select(_ => _.ToTypeSyntax()).ToArray()));
        }

        public static MemberAccessExpressionSyntax Member<TInstance, T>(
            this ExpressionSyntax instance,
            Expression<Func<TInstance, T>> member,
            params INamedTypeSymbol[] genericTypes)
        {
            switch (member.Body)
            {
                case MethodCallExpression methodCall:
                    if (genericTypes != null && genericTypes.Length > 0)
                    {
                        return instance.Member(methodCall.Method.Name, genericTypes);
                    }

                    return instance.Member(methodCall.Method.Name.ToIdentifierName());
                case MemberExpression memberAccess:
                    if (genericTypes != null && genericTypes.Length > 0)
                    {
                        return instance.Member(memberAccess.Member.Name, genericTypes);
                    }

                    return instance.Member(memberAccess.Member.Name.ToIdentifierName());
            }

            throw new ArgumentException("Expression type unsupported.");
        }

        public static MemberAccessExpressionSyntax Member(this ExpressionSyntax instance, IdentifierNameSyntax member)
        {
            return MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, instance, member);
        }

        public static MemberAccessExpressionSyntax Member(this ExpressionSyntax instance, GenericNameSyntax member)
        {
            return MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, instance, member);
        }
    }
}