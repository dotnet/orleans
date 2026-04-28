using System;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator.Model;

/// <summary>
/// A value-type reference to a type, stored as a string representation of its syntax.
/// Used in incremental pipeline models to avoid holding <c>ITypeSymbol</c> references.
/// </summary>
internal readonly record struct TypeRef
{
    public TypeRef(string syntaxString) => SyntaxString = syntaxString ?? string.Empty;

    public string SyntaxString { get; }

    /// <summary>
    /// Reconstructs a <see cref="TypeSyntax"/> from the stored string.
    /// </summary>
    public TypeSyntax ToTypeSyntax() => SyntaxFactory.ParseTypeName(SyntaxString);

    public override string ToString() => SyntaxString;

    public static TypeRef Empty { get; } = new TypeRef(string.Empty);

    public bool IsEmpty => string.IsNullOrEmpty(SyntaxString);
}
