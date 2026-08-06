// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace PackageJsonGenerator.Helpers;

/// <summary>Canonical representation of a type for schema emission.</summary>
internal sealed class CanonicalType
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Accessibility { get; set; } = "public";
    public bool IsAbstract { get; set; }
    public bool IsSealed { get; set; }
    public bool IsStatic { get; set; }
    public bool IsGeneric { get; set; }
    public bool IsReadOnly { get; set; }
    public List<CanonicalGenericParameter>? GenericParameters { get; set; }
    public string? BaseType { get; set; }
    public List<string> Interfaces { get; set; } = [];
    public List<CanonicalMember> Members { get; set; } = [];
    public CanonicalDocumentation? Docs { get; set; }
    public List<CanonicalEnumMember>? EnumMembers { get; set; }
    public string? DelegateReturnType { get; set; }
    public List<CanonicalParameter>? DelegateParameters { get; set; }
    public List<CanonicalAttribute>? Attributes { get; set; }
    /// <summary>Full names of public nested types declared on this type. Roslyn merges partial declarations, so this includes nested types contributed by every partial source file.</summary>
    public List<string>? NestedTypes { get; set; }
    /// <summary>Repo-relative source file path (e.g. "src/Orleans.Core/Foo.cs").</summary>
    public string? SourceFile { get; set; }
    /// <summary>Line range in the source file (e.g. "15-200").</summary>
    public string? SourceLines { get; set; }

    internal static string GetTypeKind(Microsoft.CodeAnalysis.INamedTypeSymbol symbol)
    {
        if (symbol.IsRecord) return symbol.IsValueType ? "record struct" : "record";

        return symbol.TypeKind switch
        {
            Microsoft.CodeAnalysis.TypeKind.Class => "class",
            Microsoft.CodeAnalysis.TypeKind.Struct => "struct",
            Microsoft.CodeAnalysis.TypeKind.Extension => "extension",
            Microsoft.CodeAnalysis.TypeKind.Interface => "interface",
            Microsoft.CodeAnalysis.TypeKind.Enum => "enum",
            Microsoft.CodeAnalysis.TypeKind.Delegate => "delegate",
            _ => "class",
        };
    }
}

/// <summary>Canonical generic parameter.</summary>
internal sealed class CanonicalGenericParameter
{
    public string Name { get; set; } = "";
    public List<string> Constraints { get; set; } = [];
}

/// <summary>Canonical member.</summary>
internal sealed class CanonicalMember
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Accessibility { get; set; } = "public";
    public bool IsStatic { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsVirtual { get; set; }
    public bool IsOverride { get; set; }
    public bool IsAsync { get; set; }
    public bool IsExtension { get; set; }
    public bool HasGet { get; set; }
    public bool HasSet { get; set; }
    public bool IsInitOnly { get; set; }
    public bool IsConst { get; set; }
    public bool IsReadOnly { get; set; }
    public string? ReturnType { get; set; }
    public bool IsReturnNullable { get; set; }
    public List<CanonicalParameter>? Parameters { get; set; }
    public List<CanonicalGenericParameter>? GenericParameters { get; set; }
    public string Signature { get; set; } = "";
    public CanonicalDocumentation? Docs { get; set; }
    public List<CanonicalAttribute>? Attributes { get; set; }
    /// <summary>Repo-relative source file path (e.g. "src/Orleans.Core/Foo.cs").</summary>
    public string? SourceFile { get; set; }
    /// <summary>Line range in the source file (e.g. "15-200").</summary>
    public string? SourceLines { get; set; }
}
internal sealed class CanonicalParameter
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool IsNullable { get; set; }
    public bool IsOptional { get; set; }
    public string? DefaultValue { get; set; }
    public string? Modifier { get; set; }
    public List<CanonicalAttribute>? Attributes { get; set; }
}

/// <summary>Canonical enum member.</summary>
internal sealed class CanonicalEnumMember
{
    public string Name { get; set; } = "";
    public long Value { get; set; }
    public string? Description { get; set; }
}

/// <summary>Canonical attribute.</summary>
internal sealed class CanonicalAttribute
{
    public string Name { get; set; } = "";
    public List<string>? ConstructorArguments { get; set; }
    public Dictionary<string, string>? Arguments { get; set; }
}

/// <summary>Canonical documentation.</summary>
internal sealed class CanonicalDocumentation
{
    public List<DocNode>? Summary { get; set; }
    public List<DocNode>? Remarks { get; set; }
    public List<DocNode>? Returns { get; set; }
    public Dictionary<string, List<DocNode>>? Parameters { get; set; }
    public Dictionary<string, List<DocNode>>? TypeParameters { get; set; }
    public List<CanonicalException>? Exceptions { get; set; }
    public List<DocExample>? Examples { get; set; }
    public List<DocNode>? Value { get; set; }
    public List<string>? SeeAlso { get; set; }
}

/// <summary>A documented exception that a member can throw.</summary>
internal sealed class CanonicalException
{
    public string Type { get; set; } = "";
    public List<DocNode>? Description { get; set; }
}

/// <summary>
/// A structured documentation node that preserves XML doc element semantics.
/// Maps to the rich format consumed by the DocContent.astro component.
/// </summary>
internal sealed class DocNode
{
    public string Kind { get; set; } = "";
    public string? Text { get; set; }
    public string? Value { get; set; }
    public string? Language { get; set; }
    public string? Style { get; set; }
    public List<DocNode>? Children { get; set; }
    public List<DocListItem>? Items { get; set; }
    public DocListItem? Header { get; set; }
}

/// <summary>A list item within a documentation list.</summary>
internal sealed class DocListItem
{
    public List<DocNode>? Term { get; set; }
    public List<DocNode>? Description { get; set; }
}

/// <summary>A structured code example from XML doc &lt;example&gt; elements.</summary>
internal sealed class DocExample
{
    public string Code { get; set; } = "";
    public string Language { get; set; } = "csharp";
    public List<DocNode>? Description { get; set; }
    public string? Region { get; set; }
}
