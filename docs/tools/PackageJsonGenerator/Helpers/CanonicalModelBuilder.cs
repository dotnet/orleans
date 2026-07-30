// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace PackageJsonGenerator.Helpers;

/// <summary>
/// Builds canonical API models from Roslyn symbols.
/// </summary>
internal sealed class CanonicalModelBuilder(Compilation compilation)
{
    private readonly Compilation _compilation = compilation;

    public List<CanonicalType> BuildTypes(ImmutableArray<INamedTypeSymbol> types)
    {
        var result = new List<CanonicalType>();

        foreach (var type in types.OrderBy(t => t.ToDisplayString()))
        {
            result.Add(BuildType(type));
        }

        return result;
    }

    private CanonicalType BuildType(INamedTypeSymbol symbol)
    {
        var isEnum = symbol.TypeKind == TypeKind.Enum;

        var type = new CanonicalType
        {
            Name = symbol.Name,
            FullName = symbol.ToDisplayString(),
            Namespace = symbol.ContainingNamespace?.ToDisplayString() ?? "",
            Kind = CanonicalType.GetTypeKind(symbol),
            Accessibility = symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
            IsAbstract = symbol.IsAbstract && symbol.TypeKind != TypeKind.Interface,
            IsSealed = symbol.IsSealed && !isEnum,
            IsStatic = symbol.IsStatic,
            IsGeneric = symbol.IsGenericType,
            IsReadOnly = symbol.IsReadOnly && symbol.IsValueType,
        };

        // Generic parameters
        if (symbol.IsGenericType)
        {
            type.GenericParameters = [.. symbol.TypeParameters.Select(BuildGenericParameter)];
        }

        // Base type
        if (symbol.BaseType is not null &&
            symbol.BaseType.SpecialType != SpecialType.System_Object &&
            symbol.BaseType.SpecialType != SpecialType.System_ValueType &&
            symbol.BaseType.SpecialType != SpecialType.System_Enum)
        {
            type.BaseType = symbol.BaseType.ToDisplayString();
        }

        // Interfaces
        type.Interfaces = [.. symbol.Interfaces
            .Select(i => i.ToDisplayString())
            .OrderBy(i => i)];

        // Members
        type.Members = BuildMembers(symbol);

        // XML docs
        type.Docs = ExtractDocumentation(symbol);

        // Enum members
        if (isEnum)
        {
            type.EnumMembers = [.. symbol.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(f => f.HasConstantValue)
                .Select(f => new CanonicalEnumMember
                {
                    Name = f.Name,
                    Value = Convert.ToInt64(f.ConstantValue),
                    Description = ExtractSummary(f),
                })];
        }

        // Delegate signature
        if (symbol.TypeKind == TypeKind.Delegate && symbol.DelegateInvokeMethod is { } invokeMethod)
        {
            type.DelegateReturnType = invokeMethod.ReturnsVoid ? "void" : invokeMethod.ReturnType.ToDisplayString();
            type.DelegateParameters = [.. invokeMethod.Parameters.Select(parameter => BuildParameter(parameter))];
        }

        // Attributes
        type.Attributes = ExtractRelevantAttributes(symbol);

        // Nested types — Roslyn merges partial declarations, so GetTypeMembers()
        // returns nested types contributed by every partial source file (e.g.
        // FoundryModel + FoundryModel.Generated.cs).
        var nestedNames = symbol.GetTypeMembers()
            .Where(IsPublicNestedType)
            .Select(t => t.ToDisplayString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (nestedNames.Count > 0)
        {
            type.NestedTypes = nestedNames;
        }

        return type;
    }

    private static bool IsPublicNestedType(INamedTypeSymbol type)
    {
        return type.DeclaredAccessibility == Accessibility.Public
            && !string.IsNullOrEmpty(type.Name)
            && !type.Name.StartsWith('<')
            && !type.GetAttributes().Any(a => a.AttributeClass?.Name == "CompilerGeneratedAttribute");
    }

    private List<CanonicalMember> BuildMembers(INamedTypeSymbol type)
    {
        var members = new List<CanonicalMember>();

        foreach (var member in type.GetMembers().OrderBy(m => m.Name))
        {
            if (member.DeclaredAccessibility != Accessibility.Public ||
                member.IsImplicitlyDeclared)
            {
                continue;
            }

            switch (member)
            {
                case IMethodSymbol method when method.MethodKind is
                    MethodKind.Ordinary or
                    MethodKind.Constructor or
                    MethodKind.UserDefinedOperator or
                    MethodKind.Conversion:
                    members.Add(BuildMethod(method));
                    break;

                case IPropertySymbol property:
                    members.Add(BuildProperty(property));
                    break;

                case IFieldSymbol field when type.TypeKind != TypeKind.Enum:
                    members.Add(BuildField(field));
                    break;

                case IEventSymbol evt:
                    members.Add(BuildEvent(evt));
                    break;
            }
        }

        return members;
    }

    private CanonicalMember BuildMethod(IMethodSymbol method) => new()
    {
        Name = method.MethodKind == MethodKind.Constructor ? ".ctor" : method.Name,
        Kind = method.MethodKind == MethodKind.Constructor ? "constructor" : "method",
        Accessibility = method.DeclaredAccessibility.ToString().ToLowerInvariant(),
        IsStatic = method.IsStatic,
        IsAbstract = method.IsAbstract,
        IsVirtual = method.IsVirtual,
        IsOverride = method.IsOverride,
        IsAsync = method.IsAsync,
        IsExtension = method.IsExtensionMethod,
        ReturnType = method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(),
        IsReturnNullable = method.ReturnType.NullableAnnotation == NullableAnnotation.Annotated,
        Parameters = [.. method.Parameters
            .AsEnumerable()
            .Select((parameter, index) => BuildParameter(parameter, index == 0 && method.IsExtensionMethod))],
        GenericParameters = method.IsGenericMethod
            ? [.. method.TypeParameters.Select(BuildGenericParameter)]
            : null,
        Signature = FormatMethodSignature(method),
        Docs = ExtractDocumentation(method),
        Attributes = ExtractRelevantAttributes(method),
    };

    private CanonicalMember BuildProperty(IPropertySymbol property) => new()
    {
        Name = property.IsIndexer ? "this[]" : property.Name,
        Kind = property.IsIndexer ? "indexer" : "property",
        Accessibility = property.DeclaredAccessibility.ToString().ToLowerInvariant(),
        IsStatic = property.IsStatic,
        IsAbstract = property.IsAbstract,
        IsVirtual = property.IsVirtual,
        IsOverride = property.IsOverride,
        HasGet = property.GetMethod is not null,
        HasSet = property.SetMethod is not null,
        IsInitOnly = property.SetMethod?.IsInitOnly ?? false,
        ReturnType = property.Type.ToDisplayString(),
        IsReturnNullable = property.Type.NullableAnnotation == NullableAnnotation.Annotated,
        Parameters = property.IsIndexer
            ? [.. property.Parameters.Select(parameter => BuildParameter(parameter))]
            : null,
        Signature = PrependModifiers(
            property.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            property.DeclaredAccessibility, property.IsStatic, property.IsAbstract,
            property.IsVirtual, property.IsOverride),
        Docs = ExtractDocumentation(property),
        Attributes = ExtractRelevantAttributes(property),
    };

    private static CanonicalParameter BuildParameter(IParameterSymbol parameter, bool isExtensionReceiver = false) => new()
    {
        Name = parameter.Name,
        Type = parameter.Type.ToDisplayString(),
        IsNullable = parameter.Type.NullableAnnotation == NullableAnnotation.Annotated,
        IsOptional = parameter.IsOptional,
        DefaultValue = parameter.HasExplicitDefaultValue ? parameter.ExplicitDefaultValue?.ToString() : null,
        Modifier = isExtensionReceiver
            ? "this"
            : parameter.RefKind switch
            {
                RefKind.Ref => "ref",
                RefKind.Out => "out",
                RefKind.In => "in",
                RefKind.RefReadOnlyParameter => "ref readonly",
                _ => parameter.IsParams ? "params" : null,
            },
        Attributes = ExtractRelevantAttributes(parameter),
    };

    private CanonicalMember BuildField(IFieldSymbol field) => new()
    {
        Name = field.Name,
        Kind = "field",
        Accessibility = field.DeclaredAccessibility.ToString().ToLowerInvariant(),
        IsStatic = field.IsStatic,
        IsConst = field.IsConst,
        IsReadOnly = field.IsReadOnly,
        ReturnType = field.Type.ToDisplayString(),
        IsReturnNullable = field.Type.NullableAnnotation == NullableAnnotation.Annotated,
        Signature = PrependModifiers(
            field.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            field.DeclaredAccessibility, field.IsStatic,
            isConst: field.IsConst, isReadOnly: field.IsReadOnly),
        Docs = ExtractDocumentation(field),
        Attributes = ExtractRelevantAttributes(field),
    };

    private CanonicalMember BuildEvent(IEventSymbol evt) => new()
    {
        Name = evt.Name,
        Kind = "event",
        Accessibility = evt.DeclaredAccessibility.ToString().ToLowerInvariant(),
        IsStatic = evt.IsStatic,
        IsAbstract = evt.IsAbstract,
        IsVirtual = evt.IsVirtual,
        IsOverride = evt.IsOverride,
        ReturnType = evt.Type.ToDisplayString(),
        Signature = PrependModifiers(
            evt.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            evt.DeclaredAccessibility, evt.IsStatic, evt.IsAbstract,
            evt.IsVirtual, evt.IsOverride),
        Docs = ExtractDocumentation(evt),
        Attributes = ExtractRelevantAttributes(evt),
    };

    /// <summary>
    /// Format a method signature, ensuring extension methods include `this` on
    /// the first parameter — Roslyn's MinimallyQualifiedFormat omits it.
    /// </summary>
    private static string FormatMethodSignature(IMethodSymbol method)
    {
        var sig = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        if (method.IsExtensionMethod && method.Parameters.Length > 0)
        {
            // The signature looks like: ReturnType Class.Method(ParamType paramName, ...)
            // We need to insert "this " before the first parameter type inside the parentheses.
            var openParen = sig.IndexOf('(');
            if (openParen >= 0 && openParen + 1 < sig.Length && sig[openParen + 1] != ')')
            {
                sig = sig.Insert(openParen + 1, "this ");
            }
        }

        return PrependModifiers(sig, method.DeclaredAccessibility, method.IsStatic, method.IsAbstract,
            method.IsVirtual, method.IsOverride, isSealed: method.IsSealed && method.IsOverride,
            isAsync: method.IsAsync);
    }

    /// <summary>
    /// Build a display-friendly signature for a property, field, or event by
    /// prepending C#-style modifiers (accessibility, static, etc.) to the
    /// Roslyn <c>MinimallyQualifiedFormat</c> string.
    /// </summary>
    private static string PrependModifiers(
        string rawSignature,
        Accessibility accessibility,
        bool isStatic = false,
        bool isAbstract = false,
        bool isVirtual = false,
        bool isOverride = false,
        bool isSealed = false,
        bool isConst = false,
        bool isReadOnly = false,
        bool isAsync = false)
    {
        var sb = new StringBuilder();

        // Accessibility
        var access = accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Protected => "protected",
            Accessibility.Internal => "internal",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.Private => "private",
            _ => null,
        };
        if (access is not null)
        {
            sb.Append(access);
            sb.Append(' ');
        }

        // Modifiers in conventional C# order
        if (isConst) sb.Append("const ");
        if (isStatic && !isConst) sb.Append("static ");
        if (isAbstract) sb.Append("abstract ");
        if (isSealed) sb.Append("sealed ");
        if (isOverride) sb.Append("override ");
        else if (isVirtual) sb.Append("virtual ");
        if (isAsync) sb.Append("async ");
        if (isReadOnly && !isConst) sb.Append("readonly ");

        sb.Append(rawSignature);
        return sb.ToString();
    }

    private static CanonicalGenericParameter BuildGenericParameter(ITypeParameterSymbol tp) => new()
    {
        Name = tp.Name,
        Constraints = GetConstraints(tp),
    };

    private static List<string> GetConstraints(ITypeParameterSymbol tp)
    {
        var constraints = new List<string>();

        if (tp.HasReferenceTypeConstraint) constraints.Add("class");
        if (tp.HasValueTypeConstraint) constraints.Add("struct");
        if (tp.HasUnmanagedTypeConstraint) constraints.Add("unmanaged");
        if (tp.HasNotNullConstraint) constraints.Add("notnull");
        if (tp.HasConstructorConstraint) constraints.Add("new()");

        foreach (var ct in tp.ConstraintTypes)
        {
            constraints.Add(ct.ToDisplayString());
        }

        return constraints;
    }

    private CanonicalDocumentation? ExtractDocumentation(ISymbol symbol, int inheritDepth = 0)
    {
        var xmlComment = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xmlComment))
        {
            return inheritDepth < 3 ? TryResolveInheritDoc(symbol, null, inheritDepth) : null;
        }

        try
        {
            var doc = XDocument.Parse(xmlComment);
            var root = doc.Root;
            if (root is null) return null;

            // Check for <inheritdoc/> — supports optional cref attribute
            var inheritDocElement = root.Element("inheritdoc");
            CanonicalDocumentation? inherited = null;
            if (inheritDocElement is not null && inheritDepth < 3)
            {
                var cref = inheritDocElement.Attribute("cref")?.Value;
                inherited = TryResolveInheritDoc(symbol, cref, inheritDepth);

                // If the doc ONLY contains <inheritdoc/> (no other sections), return inherited directly
                var hasExplicitContent = root.Element("summary") is not null ||
                    root.Element("remarks") is not null ||
                    root.Element("returns") is not null ||
                    root.Element("value") is not null ||
                    root.Elements("param").Any() ||
                    root.Elements("typeparam").Any() ||
                    root.Elements("exception").Any() ||
                    root.Elements("example").Any() ||
                    root.Elements("seealso").Any();

                if (!hasExplicitContent && inherited is not null)
                {
                    return inherited;
                }
            }

            var docs = new CanonicalDocumentation
            {
                Summary = ExtractDocNodes(root.Element("summary")),
                Remarks = ExtractDocNodes(root.Element("remarks")),
                Returns = ExtractDocNodes(root.Element("returns")),
                Value = ExtractDocNodes(root.Element("value")),
            };

            // Parameters
            var paramElements = root.Elements("param").ToList();
            if (paramElements.Count > 0)
            {
                docs.Parameters = new Dictionary<string, List<DocNode>>();
                foreach (var param in paramElements)
                {
                    var name = param.Attribute("name")?.Value;
                    if (name is not null)
                    {
                        var nodes = ExtractDocNodes(param);
                        if (nodes is not null)
                        {
                            docs.Parameters[name] = nodes;
                        }
                    }
                }
                if (docs.Parameters.Count == 0) docs.Parameters = null;
            }

            // Type parameters
            var typeParamElements = root.Elements("typeparam").ToList();
            if (typeParamElements.Count > 0)
            {
                docs.TypeParameters = new Dictionary<string, List<DocNode>>();
                foreach (var tp in typeParamElements)
                {
                    var name = tp.Attribute("name")?.Value;
                    if (name is not null)
                    {
                        var nodes = ExtractDocNodes(tp);
                        if (nodes is not null)
                        {
                            docs.TypeParameters[name] = nodes;
                        }
                    }
                }
                if (docs.TypeParameters.Count == 0) docs.TypeParameters = null;
            }

            // Exceptions
            var exceptionElements = root.Elements("exception").ToList();
            if (exceptionElements.Count > 0)
            {
                docs.Exceptions = [];
                foreach (var exc in exceptionElements)
                {
                    var cref = exc.Attribute("cref")?.Value;
                    if (cref is not null)
                    {
                        docs.Exceptions.Add(new CanonicalException
                        {
                            Type = cref,
                            Description = ExtractDocNodes(exc),
                        });
                    }
                }
            }

            // Examples
            var exampleElements = root.Elements("example").ToList();
            if (exampleElements.Count > 0)
            {
                docs.Examples = exampleElements
                    .Select(ExtractDocExample)
                    .Where(e => e is not null)
                    .Cast<DocExample>()
                    .ToList();
                if (docs.Examples.Count == 0) docs.Examples = null;
            }

            // SeeAlso
            var seeAlsoElements = root.Elements("seealso").ToList();
            if (seeAlsoElements.Count > 0)
            {
                docs.SeeAlso = seeAlsoElements
                    .Select(s => s.Attribute("cref")?.Value)
                    .Where(c => c is not null)
                    .Cast<string>()
                    .ToList();
            }

            // Merge: fill missing sections from inherited docs
            if (inherited is not null)
            {
                MergeInheritedDocs(docs, inherited);
            }

            return docs;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fills missing documentation sections in <paramref name="target"/> from
    /// <paramref name="inherited"/>. Explicit sections are never overwritten.
    /// </summary>
    private static void MergeInheritedDocs(CanonicalDocumentation target, CanonicalDocumentation inherited)
    {
        target.Summary ??= inherited.Summary;
        target.Remarks ??= inherited.Remarks;
        target.Returns ??= inherited.Returns;
        target.Value ??= inherited.Value;
        target.Examples ??= inherited.Examples;
        target.SeeAlso ??= inherited.SeeAlso;

        // Merge parameter docs — fill missing params only
        if (inherited.Parameters is not null)
        {
            target.Parameters ??= new Dictionary<string, List<DocNode>>();
            foreach (var kvp in inherited.Parameters)
            {
                target.Parameters.TryAdd(kvp.Key, kvp.Value);
            }
        }

        // Merge type parameter docs
        if (inherited.TypeParameters is not null)
        {
            target.TypeParameters ??= new Dictionary<string, List<DocNode>>();
            foreach (var kvp in inherited.TypeParameters)
            {
                target.TypeParameters.TryAdd(kvp.Key, kvp.Value);
            }
        }

        // Merge exceptions — append only unique exception types
        if (inherited.Exceptions is { Count: > 0 })
        {
            if (target.Exceptions is null)
            {
                target.Exceptions = inherited.Exceptions;
            }
            else
            {
                var existingTypes = new HashSet<string>(target.Exceptions.Select(e => e.Type));
                foreach (var exc in inherited.Exceptions)
                {
                    if (!existingTypes.Contains(exc.Type))
                    {
                        target.Exceptions.Add(exc);
                    }
                }
            }
        }
    }

    private CanonicalDocumentation? TryResolveInheritDoc(ISymbol symbol, string? cref, int inheritDepth)
    {
        ISymbol? baseSymbol;

        if (cref is not null)
        {
            baseSymbol = ResolveCrefSymbol(symbol, cref);
        }
        else
        {
            baseSymbol = FindInheritDocSource(symbol);
        }

        var documentation = baseSymbol is not null ? ExtractDocumentation(baseSymbol, inheritDepth + 1) : null;
        if (baseSymbol is not null && documentation is not null)
        {
            RemapInheritedDocumentation(symbol, baseSymbol, documentation);
        }

        return documentation;
    }

    /// <summary>
    /// Resolves a documentation cref string (e.g. "T:Namespace.Type" or
    /// "M:Namespace.Type.Method") to a Roslyn symbol using the compilation.
    /// </summary>
    private ISymbol? ResolveCrefSymbol(ISymbol context, string cref)
    {
        var resolvedSymbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, _compilation)
            ?? DocumentationCommentId.GetFirstSymbolForReferenceId(cref, _compilation);
        if (resolvedSymbol is not null)
        {
            return resolvedSymbol;
        }

        // Strip documentation ID prefix (T:, M:, P:, F:, E:)
        var docId = cref;
        if (docId.Length > 2 && docId[1] == ':')
        {
            docId = docId[2..];
        }

        // Strip method parameters for type lookup
        var parenIdx = docId.IndexOf('(');
        var nameWithoutParams = parenIdx >= 0 ? docId[..parenIdx] : docId;

        // Try to find as a type first
        var typeSymbol = _compilation.GetTypeByMetadataName(nameWithoutParams);
        if (typeSymbol is not null)
        {
            return typeSymbol;
        }

        // Try as a member: split on last dot to get containing type + member name
        var lastDot = nameWithoutParams.LastIndexOf('.');
        if (lastDot > 0)
        {
            var containingTypeName = nameWithoutParams[..lastDot];
            var memberName = nameWithoutParams[(lastDot + 1)..];

            // Handle generic arity (e.g., List`1)
            var backtickIdx = containingTypeName.IndexOf('`');
            var metadataName = backtickIdx >= 0 ? containingTypeName : containingTypeName;

            var containingType = _compilation.GetTypeByMetadataName(metadataName);
            if (containingType is not null)
            {
                // Find the member by name
                var members = containingType.GetMembers(memberName);
                if (members.Length > 0)
                {
                    return members[0];
                }
            }
        }

        // Fallback: try the default inheritance chain
        return FindInheritDocSource(context);
    }

    private static void RemapInheritedDocumentation(
        ISymbol targetSymbol,
        ISymbol sourceSymbol,
        CanonicalDocumentation documentation)
    {
        var targetParameters = GetParameters(targetSymbol);
        var sourceParameters = GetParameters(sourceSymbol);
        documentation.Parameters = RemapDocumentationByOrdinal(
            documentation.Parameters,
            sourceParameters.Select(static parameter => parameter.Name),
            targetParameters.Select(static parameter => parameter.Name));

        var targetTypeParameters = GetTypeParameters(targetSymbol);
        var sourceTypeParameters = GetTypeParameters(sourceSymbol);
        documentation.TypeParameters = RemapDocumentationByOrdinal(
            documentation.TypeParameters,
            sourceTypeParameters.Select(static parameter => parameter.Name),
            targetTypeParameters.Select(static parameter => parameter.Name));
    }

    private static Dictionary<string, List<DocNode>>? RemapDocumentationByOrdinal(
        Dictionary<string, List<DocNode>>? documentation,
        IEnumerable<string> sourceNames,
        IEnumerable<string> targetNames)
    {
        if (documentation is null)
        {
            return null;
        }

        var result = new Dictionary<string, List<DocNode>>();
        foreach (var pair in sourceNames.Zip(targetNames))
        {
            if (documentation.TryGetValue(pair.First, out var value))
            {
                result[pair.Second] = value;
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static ImmutableArray<IParameterSymbol> GetParameters(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.Parameters,
        IPropertySymbol property => property.Parameters,
        _ => [],
    };

    private static ImmutableArray<ITypeParameterSymbol> GetTypeParameters(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.TypeParameters,
        INamedTypeSymbol type => type.TypeParameters,
        _ => [],
    };

    private static ISymbol? FindInheritDocSource(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol method:
                if (method.OverriddenMethod is not null)
                    return method.OverriddenMethod;
                return FindInterfaceMemberImplementation(method);

            case IPropertySymbol property:
                if (property.OverriddenProperty is not null)
                    return property.OverriddenProperty;
                return FindInterfaceMemberImplementation(property);

            case IEventSymbol evt:
                if (evt.OverriddenEvent is not null)
                    return evt.OverriddenEvent;
                return FindInterfaceMemberImplementation(evt);

            case IFieldSymbol field:
                // Fields can't override, but may implement interface members
                return FindInterfaceMemberImplementation(field);

            case INamedTypeSymbol type:
                return type.BaseType;

            default:
                return null;
        }
    }

    private static ISymbol? FindInterfaceMemberImplementation(ISymbol member)
    {
        var containingType = member.ContainingType;
        if (containingType is null) return null;

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var ifaceMember in iface.GetMembers())
            {
                var impl = containingType.FindImplementationForInterfaceMember(ifaceMember);
                if (impl is not null && SymbolEqualityComparer.Default.Equals(impl, member))
                {
                    return ifaceMember;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts structured documentation nodes from an XML element,
    /// preserving semantic structure for rich rendering on the frontend.
    /// </summary>
    private static List<DocNode>? ExtractDocNodes(XElement? element)
    {
        if (element is null) return null;

        var nodes = CollectNodes(element);
        return nodes.Count > 0 ? nodes : null;
    }

    /// <summary>
    /// Recursively collects child nodes of an XML element into a flat list
    /// of structured DocNode objects, preserving inline and block semantics.
    /// </summary>
    private static List<DocNode> CollectNodes(XElement element)
    {
        var nodes = new List<DocNode>();

        foreach (var xNode in element.Nodes())
        {
            switch (xNode)
            {
                case XText textNode:
                    var text = textNode.Value;
                    // Normalize whitespace within text runs (newlines → spaces, collapse runs)
                    text = text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                    while (text.Contains("  ")) text = text.Replace("  ", " ");
                    if (!string.IsNullOrEmpty(text))
                    {
                        nodes.Add(new DocNode { Kind = "text", Text = text });
                    }
                    break;

                case XElement child:
                    switch (child.Name.LocalName)
                    {
                        case "see":
                        {
                            var cref = child.Attribute("cref")?.Value;
                            if (cref is not null)
                            {
                                nodes.Add(new DocNode { Kind = "cref", Value = cref });
                            }
                            else
                            {
                                var langword = child.Attribute("langword")?.Value;
                                if (langword is not null)
                                {
                                    nodes.Add(new DocNode { Kind = "langword", Value = langword });
                                }
                                else
                                {
                                    var href = child.Attribute("href")?.Value;
                                    if (href is not null)
                                    {
                                        nodes.Add(new DocNode
                                        {
                                            Kind = "href",
                                            Value = href,
                                            Text = child.Value.Trim() is { Length: > 0 } t ? t : null,
                                        });
                                    }
                                }
                            }
                            break;
                        }

                        case "paramref":
                        {
                            var name = child.Attribute("name")?.Value;
                            if (name is not null)
                            {
                                nodes.Add(new DocNode { Kind = "paramref", Value = name });
                            }
                            break;
                        }

                        case "typeparamref":
                        {
                            var name = child.Attribute("name")?.Value;
                            if (name is not null)
                            {
                                nodes.Add(new DocNode { Kind = "typeparamref", Value = name });
                            }
                            break;
                        }

                        case "c":
                            nodes.Add(new DocNode { Kind = "code", Text = child.Value });
                            break;

                        case "code":
                        {
                            var lang = NormalizeLanguage(
                                child.Attribute("lang")?.Value
                                ?? child.Attribute("language")?.Value
                                ?? "csharp");
                            var region = child.Attribute("region")?.Value;
                            nodes.Add(new DocNode
                            {
                                Kind = "codeblock",
                                Text = child.Value,
                                Language = lang,
                                Value = region, // reuse value for region
                            });
                            break;
                        }

                        case "para":
                        {
                            var children = CollectNodes(child);
                            if (children.Count > 0)
                            {
                                nodes.Add(new DocNode { Kind = "para", Children = children });
                            }
                            break;
                        }

                        case "note":
                        {
                            var noteType = child.Attribute("type")?.Value ?? "note";
                            var children = CollectNodes(child);
                            if (children.Count > 0)
                            {
                                nodes.Add(new DocNode { Kind = "note", Value = noteType, Children = children });
                            }
                            break;
                        }

                        case "list":
                        {
                            var style = child.Attribute("type")?.Value ?? "bullet";
                            var listNode = new DocNode { Kind = "list", Style = style };

                            // Header
                            var headerElem = child.Element("listheader");
                            if (headerElem is not null)
                            {
                                listNode.Header = BuildDocListItem(headerElem);
                            }

                            // Items
                            var items = child.Elements("item").ToList();
                            if (items.Count > 0)
                            {
                                listNode.Items = items.Select(BuildDocListItem).ToList();
                            }

                            nodes.Add(listNode);
                            break;
                        }

                        case "a":
                        {
                            var href = child.Attribute("href")?.Value;
                            if (href is not null)
                            {
                                nodes.Add(new DocNode
                                {
                                    Kind = "href",
                                    Value = href,
                                    Text = child.Value.Trim() is { Length: > 0 } t ? t : null,
                                });
                            }
                            break;
                        }

                        case "example":
                            // Examples nested inside other elements — collect as children
                            var exChildren = CollectNodes(child);
                            if (exChildren.Count > 0)
                            {
                                nodes.AddRange(exChildren);
                            }
                            break;

                        default:
                            // Unknown elements — recurse into their children
                            var defaultChildren = CollectNodes(child);
                            nodes.AddRange(defaultChildren);
                            break;
                    }
                    break;
            }
        }

        return nodes;
    }

    private static DocListItem BuildDocListItem(XElement element)
    {
        var term = ExtractDocNodes(element.Element("term"));
        var description = ExtractDocNodes(element.Element("description"));

        // Support XML doc list items that contain plain text/paragraph content
        // directly inside <item> instead of nested <description> nodes.
        if (term is null && description is null)
        {
            description = ExtractDocNodes(element);
        }

        return new DocListItem
        {
            Term = term,
            Description = description,
        };
    }

    /// <summary>
    /// Extracts a structured example from an &lt;example&gt; XML element.
    /// Separates code blocks from descriptive text.
    /// </summary>
    private static DocExample? ExtractDocExample(XElement exampleElement)
    {
        // Find the first <code> element — that's the example code
        var codeElement = exampleElement.Descendants("code").FirstOrDefault();
        if (codeElement is null)
        {
            // No code block — try to use the whole content as code
            var plainText = ExtractPlainText(exampleElement);
            return plainText is not null ? new DocExample { Code = plainText } : null;
        }

        var lang = NormalizeLanguage(
            codeElement.Attribute("lang")?.Value
            ?? codeElement.Attribute("language")?.Value
            ?? "csharp");
        var region = codeElement.Attribute("region")?.Value;

        // Collect description nodes: everything that is NOT the code element
        var descNodes = new List<DocNode>();
        foreach (var node in exampleElement.Nodes())
        {
            if (node == codeElement) continue;

            switch (node)
            {
                case XText textNode:
                    var text = textNode.Value.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                    while (text.Contains("  ")) text = text.Replace("  ", " ");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        descNodes.Add(new DocNode { Kind = "text", Text = text.Trim() });
                    }
                    break;

                case XElement child when child != codeElement:
                    var childNodes = CollectNodes(child);
                    if (child.Name.LocalName == "para")
                    {
                        if (childNodes.Count > 0)
                            descNodes.Add(new DocNode { Kind = "para", Children = childNodes });
                    }
                    else
                    {
                        descNodes.AddRange(childNodes);
                    }
                    break;
            }
        }

        return new DocExample
        {
            Code = codeElement.Value,
            Language = lang,
            Description = descNodes.Count > 0 ? descNodes : null,
            Region = region,
        };
    }

    /// <summary>
    /// Extracts plain text from an XML element, collapsing all structure.
    /// Used for simple contexts like enum member descriptions.
    /// </summary>
    private static string? ExtractPlainText(XElement? element)
    {
        if (element is null) return null;

        var sb = new StringBuilder();
        AppendPlainText(element, sb);

        var text = sb.ToString()
            .Replace("\r\n", " ")
            .Replace("\n", " ")
            .Replace("\r", " ")
            .Trim();

        // Collapse multiple spaces
        while (text.Contains("  "))
        {
            text = text.Replace("  ", " ");
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static void AppendPlainText(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText textNode:
                    sb.Append(textNode.Value);
                    break;

                case XElement child:
                    switch (child.Name.LocalName)
                    {
                        case "see":
                            var cref = child.Attribute("cref")?.Value;
                            if (cref is not null)
                            {
                                sb.Append(ExtractShortNameFromCref(cref));
                            }
                            else
                            {
                                var langword = child.Attribute("langword")?.Value;
                                if (langword is not null)
                                {
                                    sb.Append(langword);
                                }
                            }
                            break;

                        case "paramref":
                        case "typeparamref":
                            var name = child.Attribute("name")?.Value;
                            if (name is not null)
                            {
                                sb.Append(name);
                            }
                            break;

                        case "c":
                        case "code":
                            sb.Append(child.Value);
                            break;

                        case "para":
                            sb.Append(' ');
                            AppendPlainText(child, sb);
                            sb.Append(' ');
                            break;

                        default:
                            AppendPlainText(child, sb);
                            break;
                    }
                    break;
            }
        }
    }

    private static string ExtractShortNameFromCref(string cref)
    {
        // Strip documentation ID prefix (T:, M:, P:, F:, E:, N:)
        if (cref.Length > 2 && cref[1] == ':')
        {
            cref = cref[2..];
        }

        // Strip method parameters
        var parenIdx = cref.IndexOf('(');
        if (parenIdx >= 0)
        {
            cref = cref[..parenIdx];
        }

        // Get the last segment (type or member name)
        var lastDot = cref.LastIndexOf('.');
        if (lastDot >= 0)
        {
            cref = cref[(lastDot + 1)..];
        }

        // Strip generic arity suffix (e.g., List`1 → List)
        var backtickIdx = cref.IndexOf('`');
        if (backtickIdx >= 0)
        {
            cref = cref[..backtickIdx];
        }

        return cref;
    }

    private static string? ExtractSummary(ISymbol symbol)
    {
        var xmlComment = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xmlComment)) return null;

        try
        {
            var doc = XDocument.Parse(xmlComment);
            return ExtractPlainText(doc.Root?.Element("summary"));
        }
        catch
        {
            return null;
        }
    }

    private static List<CanonicalAttribute>? ExtractRelevantAttributes(ISymbol symbol)
    {
        var relevant = symbol.GetAttributes()
            .Where(a => IsRelevantAttribute(a.AttributeClass?.Name ?? ""))
            .Select(a => new CanonicalAttribute
            {
                Name = a.AttributeClass?.ToDisplayString() ?? "",
                ConstructorArguments = ExtractConstructorArguments(a),
                Arguments = ExtractNamedArguments(a),
            })
            .ToList();

        return relevant.Count > 0 ? relevant : null;
    }

    private static bool IsRelevantAttribute(string name) => name is
        "AliasAttribute" or
        "AlwaysInterleaveAttribute" or
        "DefaultGrainTypeAttribute" or
        "GenerateMethodSerializersAttribute" or
        "GenerateSerializerAttribute" or
        "GrainDirectoryAttribute" or
        "GrainInterfaceTypeAttribute" or
        "GrainTypeAttribute" or
        "IdAttribute" or
        "ImmutableAttribute" or
        "LogConsistencyProviderAttribute" or
        "MayInterleaveAttribute" or
        "OneWayAttribute" or
        "ReadOnlyAttribute" or
        "ReentrantAttribute" or
        "StatelessWorkerAttribute" or
        "StorageProviderAttribute" or
        "SuppressReferenceTrackingAttribute" or
        "TransactionAttribute" or
        "ObsoleteAttribute" or
        "ExperimentalAttribute" or
        "FlagsAttribute" or
        "JsonConverterAttribute" or
        "JsonDerivedTypeAttribute" or
        "JsonPolymorphicAttribute" or
        "JsonNumberHandlingAttribute" or
        "JsonRequiredAttribute" or
        "RequiredAttribute" or
        "DefaultValueAttribute";

    private static List<string>? ExtractConstructorArguments(AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length == 0)
        {
            return null;
        }

        var values = attribute.ConstructorArguments
            .SelectMany(FlattenTypedConstant)
            .ToList();

        return values.Count > 0 ? values : null;
    }

    private static Dictionary<string, string>? ExtractNamedArguments(AttributeData attribute)
    {
        if (attribute.NamedArguments.Length == 0)
        {
            return null;
        }

        var values = attribute.NamedArguments
            .ToDictionary(n => n.Key, n => FormatTypedConstant(n.Value));

        return values.Count > 0 ? values : null;
    }

    private static IEnumerable<string> FlattenTypedConstant(TypedConstant constant)
    {
        if (constant.Kind == TypedConstantKind.Array)
        {
            foreach (var item in constant.Values)
            {
                foreach (var nested in FlattenTypedConstant(item))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        yield return FormatTypedConstant(constant);
    }

    private static string FormatTypedConstant(TypedConstant constant)
    {
        if (constant.Kind == TypedConstantKind.Array)
        {
            return string.Join(", ", constant.Values.SelectMany(FlattenTypedConstant));
        }

        return constant.Value switch
        {
            ITypeSymbol typeSymbol => typeSymbol.ToDisplayString(),
            null => "null",
            _ => constant.Value.ToString() ?? "",
        };
    }

    /// <summary>
    /// Normalizes XML doc language identifiers to lowercase aliases expected by
    /// syntax highlighters (e.g. "C#" → "csharp").
    /// </summary>
    private static string NormalizeLanguage(string lang) => lang switch
    {
        "C#" => "csharp",
        "F#" => "fsharp",
        "VB" or "VB.NET" => "vb",
        _ => lang.ToLowerInvariant(),
    };
}
