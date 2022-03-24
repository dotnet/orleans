using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator.SyntaxGeneration
{
    internal static class SymbolExtensions
    {
#pragma warning disable RS1024 // Compare symbols correctly
        private static readonly ConcurrentDictionary<ITypeSymbol, TypeSyntax> TypeCache = new(SymbolEqualityComparer.Default);
        private static readonly ConcurrentDictionary<ISymbol, string> NameCache = new(SymbolEqualityComparer.Default);
#pragma warning restore RS1024 // Compare symbols correctly

        public static TypeSyntax ToTypeSyntax(this ITypeSymbol typeSymbol)
        {
            if (typeSymbol.SpecialType == SpecialType.System_Void)
            {
                return PredefinedType(Token(SyntaxKind.VoidKeyword));
            }

            if (!TypeCache.TryGetValue(typeSymbol, out var result))
            {
                result = TypeCache[typeSymbol] = ParseTypeName(typeSymbol.ToDisplayName());
            }

            return result;
        }

        public static TypeSyntax ToTypeSyntax(this ITypeSymbol typeSymbol, Dictionary<ITypeParameterSymbol, string> substitutions)
        {
            if (substitutions is null or { Count: 0 })
            {
                return typeSymbol.ToTypeSyntax();
            }

            if (typeSymbol.SpecialType == SpecialType.System_Void)
            {
                return PredefinedType(Token(SyntaxKind.VoidKeyword));
            }

            var res = new StringBuilder();
            ToTypeSyntaxInner(typeSymbol, substitutions, res);
            var result = ParseTypeName(res.ToString());
            return result;
        }

        public static string ToDisplayName(this ITypeSymbol typeSymbol, Dictionary<ITypeParameterSymbol, string> substitutions, bool includeGlobalSpecifier = true, bool includeNamespace = true)
        {
            if (typeSymbol.SpecialType == SpecialType.System_Void)
            {
                return "void";
            }

            var result = new StringBuilder();
            ToTypeSyntaxInner(typeSymbol, substitutions, result, includeGlobalSpecifier, includeNamespace);
            return result.ToString();
        }

        public static string ToDisplayName(this ITypeSymbol typeSymbol)
        {
            if (typeSymbol.SpecialType == SpecialType.System_Void)
            {
                return "void";
            }

            if (!NameCache.TryGetValue(typeSymbol, out var result))
            {
                result = NameCache[typeSymbol] = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }

            return result;
        }

        public static string ToDisplayName(this IAssemblySymbol assemblySymbol)
        {
            if (assemblySymbol is null)
            {
                return string.Empty;
            }

            if (!NameCache.TryGetValue(assemblySymbol, out var result))
            {
                result = NameCache[assemblySymbol] = assemblySymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            }

            return result;
        }

        private static void ToTypeSyntaxInner(ITypeSymbol typeSymbol, Dictionary<ITypeParameterSymbol, string> substitutions, StringBuilder res, bool includeGlobalSpecifier = true, bool includeNamespace = true)
        {
            switch (typeSymbol)
            {
                case IDynamicTypeSymbol:
                    res.Append("dynamic");
                    break;
                case IArrayTypeSymbol a:
                    ToTypeSyntaxInner(a.ElementType, substitutions, res, includeGlobalSpecifier, includeNamespace);
                    res.Append('[');
                    if (a.Rank > 1)
                    {
                        res.Append(new string(',', a.Rank - 1));
                    }

                    res.Append(']');
                    break;
                case ITypeParameterSymbol tp:
                    if (substitutions.TryGetValue(tp, out var sub))
                    {
                        res.Append(sub);
                    }
                    else
                    {
                        res.Append(tp.Name.EscapeIdentifier());
                    }
                    break;
                case INamedTypeSymbol n:
                    OnNamedTypeSymbol(n, substitutions, res, includeGlobalSpecifier, includeNamespace);
                    break;
                default:
                    throw new NotSupportedException($"Symbols of type {typeSymbol?.GetType().ToString() ?? "null"} are not supported");
            }

            static void OnNamedTypeSymbol(INamedTypeSymbol symbol, Dictionary<ITypeParameterSymbol, string> substitutions, StringBuilder res, bool includeGlobalSpecifier, bool includeNamespace)
            {
                switch (symbol.ContainingSymbol)
                {
                    case INamespaceSymbol ns when includeNamespace:
                        AddFullNamespace(ns, res, includeGlobalSpecifier);
                        break;
                    case INamedTypeSymbol containingType:
                        OnNamedTypeSymbol(containingType, substitutions, res, includeGlobalSpecifier, includeNamespace);
                        res.Append('.');
                        break;
                }

                res.Append(symbol.Name.EscapeIdentifier());
                if (symbol.TypeArguments.Length > 0)
                {
                    res.Append('<');
                    bool first = true;
                    foreach (var typeParameter in symbol.TypeArguments)
                    {
                        if (!first)
                        {
                            res.Append(',');
                        }

                        ToTypeSyntaxInner(typeParameter, substitutions, res, includeGlobalSpecifier, includeNamespace);
                        first = false;
                    }
                    res.Append('>');
                }
            }

            static void AddFullNamespace(INamespaceSymbol symbol, StringBuilder res, bool includeGlobalSpecifier)
            {
                if (symbol.ContainingNamespace is { } parent)
                {
                    AddFullNamespace(parent, res, includeGlobalSpecifier);
                }

                if (symbol.IsGlobalNamespace)
                {
                    if (includeGlobalSpecifier)
                    {
                        res.Append("global::");
                    }
                }
                else
                {
                    res.Append(symbol.Name.EscapeIdentifier());
                    res.Append('.');
                }
            }
        }

        public static TypeSyntax ToTypeSyntax(this ITypeSymbol typeSymbol, params TypeSyntax[] genericParameters)
        {
            var displayString = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var nameSyntax = ParseName(displayString);

            switch (nameSyntax)
            {
                case AliasQualifiedNameSyntax aliased:
                    return aliased.WithName(WithGenericParameters(aliased.Name));
                case QualifiedNameSyntax qualified:
                    return qualified.WithRight(WithGenericParameters(qualified.Right));
                case GenericNameSyntax g:
                    return WithGenericParameters(g);
                default:
                    ThrowInvalidOperationException();
                    return default;
            }

            SimpleNameSyntax WithGenericParameters(SimpleNameSyntax simpleNameSyntax)
            {
                if (simpleNameSyntax is GenericNameSyntax generic)
                {
                    return generic.WithTypeArgumentList(TypeArgumentList(SeparatedList(genericParameters)));
                }

                ThrowInvalidOperationException();
                return default;
            }

            void ThrowInvalidOperationException()
            {
                throw new InvalidOperationException(
                    $"Attempted to add generic parameters to non-generic type {displayString} ({nameSyntax.GetType()}, adding parameters {string.Join(", ", genericParameters.Select(n => n.ToFullString()))}");
            }
        }

        public static TypeSyntax ToOpenTypeSyntax(this INamedTypeSymbol typeSymbol)
        {
            var displayString = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var nameSyntax = ParseName(displayString);
            return Visit(nameSyntax);

            static NameSyntax Visit(NameSyntax nameSyntax)
            {
                switch (nameSyntax)
                {
                    case GenericNameSyntax generic:
                        {
                            var argCount = generic.TypeArgumentList.Arguments.Count;
                            return generic.WithTypeArgumentList(TypeArgumentList(SeparatedList<TypeSyntax>(Enumerable.Range(0, argCount).Select(_ => OmittedTypeArgument()))));
                        }
                    case AliasQualifiedNameSyntax aliased:
                        return aliased.WithName((SimpleNameSyntax)Visit(aliased.Name));
                    case QualifiedNameSyntax qualified:
                        return qualified.WithRight((SimpleNameSyntax)Visit(qualified.Right)).WithLeft(Visit(qualified.Left));
                    default:
                        return nameSyntax;
                }
            }
        }

        public static NameSyntax ToNameSyntax(this ITypeSymbol typeSymbol) => ParseName(typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

        public static string GetValidIdentifier(this ITypeSymbol type) => type switch
        {
            INamedTypeSymbol named when !named.IsGenericType => $"{named.Name}",
            INamedTypeSymbol named => $"{named.Name}_{string.Join("_", named.TypeArguments.Select(GetValidIdentifier))}",
            IArrayTypeSymbol array => $"{GetValidIdentifier(array.ElementType)}_{array.Rank}",
            ITypeParameterSymbol parameter => $"{parameter.Name}",
            _ => throw new NotSupportedException($"Unable to format type of kind {type.GetType()} with name \"{type.Name}\""),
        };

        public static bool HasBaseType(this ITypeSymbol typeSymbol, INamedTypeSymbol baseType)
        {
            for (; typeSymbol != null; typeSymbol = typeSymbol.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(baseType, typeSymbol))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool HasAnyAttribute(this ISymbol symbol, List<INamedTypeSymbol> attributeType)
        {
            foreach (var t in attributeType)
            {
                if (symbol.HasAttribute(t))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool HasAttribute(this ISymbol symbol, INamedTypeSymbol attributeType)
        {
            var attributes = symbol.GetAttributes();
            foreach (var attr in attributes)
            {
                if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeType))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets all attributes which are assignable to the specified attribute type.
        /// </summary>
        public static bool GetAttributes(this ISymbol symbol, INamedTypeSymbol attributeType, out AttributeData[] attributes)
        {
            var result = default(List<AttributeData>);
            foreach (var attr in symbol.GetAttributes())
            {
                if (!attr.AttributeClass.HasBaseType(attributeType))
                {
                    continue;
                }

                if (result is null)
                {
                    result = new List<AttributeData>();
                }

                result.Add(attr);
            }

            attributes = result?.ToArray();
            return attributes != null && attributes.Length > 0;
        }

        public static IEnumerable<TSymbol> GetAllMembers<TSymbol>(this ITypeSymbol type, string name) where TSymbol : ISymbol
        {
            foreach (var member in type.GetAllMembers<TSymbol>())
            {
                if (!string.Equals(member.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                yield return member;
            }
        }

        public static IEnumerable<TSymbol> GetAllMembers<TSymbol>(this ITypeSymbol type, string name, Accessibility accessibility) where TSymbol : ISymbol
        {
            foreach (var member in type.GetAllMembers<TSymbol>(name))
            {
                if (member.DeclaredAccessibility != accessibility)
                {
                    continue;
                }

                yield return member;
            }
        }

        public static IEnumerable<TSymbol> GetAllMembers<TSymbol>(this ITypeSymbol type) where TSymbol : ISymbol
        {
            var bases = new Stack<ITypeSymbol>();
            var b = type.BaseType;
            while (b is { })
            {
                bases.Push(b);
                b = b.BaseType;
            }

            foreach (var @base in bases)
            {
                foreach (var member in @base.GetDeclaredInstanceMembers<TSymbol>())
                {
                    yield return member;
                }
            }

            foreach (var iface in type.AllInterfaces)
            {
                foreach (var member in iface.GetDeclaredInstanceMembers<TSymbol>())
                {
                    yield return member;
                }
            }

            foreach (var member in type.GetDeclaredInstanceMembers<TSymbol>())
            {
                yield return member;
            }
        }
        
        public static IEnumerable<TSymbol> GetDeclaredInstanceMembers<TSymbol>(this ITypeSymbol type) where TSymbol : ISymbol
        {
            foreach (var candidate in type.GetMembers())
            {
                if (candidate.IsStatic)
                {
                    continue;
                }

                if (candidate is TSymbol symbol)
                {
                    yield return symbol;
                }
            }
        }

        public static string GetNamespaceAndNesting(this ISymbol symbol)
        {
            var result = new StringBuilder();
            Visit(symbol, result);
            return result.ToString();

            static void Visit(ISymbol symbol, StringBuilder res)
            {
                switch (symbol.ContainingSymbol)
                {
                    case INamespaceOrTypeSymbol parent:
                        Visit(parent, res);

                        if (res is { Length: > 0 })
                        {
                            res.Append('.');
                        }

                        res.Append(parent.Name);
                        break;
                }
            }
        }

        public static IEnumerable<ITypeParameterSymbol> GetAllTypeParameters(this INamedTypeSymbol symbol)
        {
            // Note that this will not work if multiple points in the inheritance hierarchy are containing within a single generic type.
            // To solve that, we could retain some context throughout the recursive calls.
            if (symbol.ContainingType is { } containingType && containingType.IsGenericType)
            {
                foreach (var containingTypeParameter in containingType.GetAllTypeParameters())
                {
                    yield return containingTypeParameter;
                }
            }

            foreach (var tp in symbol.TypeParameters)
            {
                yield return tp;
            }
        }

        public static IEnumerable<INamedTypeSymbol> GetDeclaredTypes(this IAssemblySymbol reference)
        {
            foreach (var module in reference.Modules)
            {
                foreach (var type in GetDeclaredTypes(module.GlobalNamespace))
                {
                    yield return type;
                }
            }

            IEnumerable<INamedTypeSymbol> GetDeclaredTypes(INamespaceOrTypeSymbol ns)
            {
                foreach (var member in ns.GetMembers())
                {
                    switch (member)
                    {
                        case INamespaceSymbol nestedNamespace:
                            foreach (var nested in GetDeclaredTypes(nestedNamespace)) yield return nested;
                            break;
                        case ITypeSymbol type:
                            if (type is INamedTypeSymbol namedType) yield return namedType;
                            foreach (var nested in GetDeclaredTypes(type)) yield return nested;
                            break;
                    }
                }
            }
        }
    }
}