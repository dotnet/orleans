using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator.SyntaxGeneration;

/// <summary>
/// Spells lexical C# fragments for <see cref="SourceWriter"/>.
/// </summary>
internal static class CodeText
{
    /// <summary>
    /// Spells a type, name, or type-argument fragment with idiomatic spacing: a space after each comma which separates
    /// written elements (<c>Dictionary&lt;int, string&gt;</c>, but <c>G&lt;,,&gt;</c> and <c>int[,]</c>), between adjacent words,
    /// and before a tuple element name (<c>(int[] items, int? count)</c>).
    /// </summary>
    /// <remarks>
    /// Type syntax holds only names, keywords, and punctuation, so token text is sufficient: no literal or trivia is significant.
    /// </remarks>
    public static string Type(SyntaxNode type)
    {
        var builder = new StringBuilder();
        AppendType(builder, type);
        return builder.ToString();
    }

    public static void AppendType(StringBuilder builder, SyntaxNode type)
    {
        if (type.ContainsDiagnostics)
        {
            // A malformed spelling, such as an unescaped keyword namespace (#11640), keeps its text, including skipped tokens.
            builder.Append(type.ToFullString().Trim());
            return;
        }

        var previous = SyntaxKind.None;
        foreach (var token in type.DescendantTokens())
        {
            if (token.Span.IsEmpty)
            {
                // Omitted type arguments have no text; the following comma or bracket determines spacing.
                continue;
            }

            var kind = token.Kind();
            if (previous == SyntaxKind.CommaToken && kind is not (SyntaxKind.CommaToken or SyntaxKind.GreaterThanToken or SyntaxKind.CloseBracketToken)
                || IsWord(previous) && IsWord(kind)
                || kind == SyntaxKind.IdentifierToken && token.Parent is TupleElementSyntax)
            {
                builder.Append(' ');
            }

            builder.Append(token.Text);
            previous = kind;
        }

        static bool IsWord(SyntaxKind kind) => kind == SyntaxKind.IdentifierToken || SyntaxFacts.IsKeywordKind(kind);
    }

    /// <summary>Spells an identifier, escaping C# keywords and contextual keywords as <see cref="StringExtensions.ToIdentifier"/> does.</summary>
    public static string Identifier(string identifier)
    {
        identifier = identifier.TrimStart('@');
        return SyntaxGeneration.Identifier.IsCSharpKeyword(identifier) ? "@" + identifier : identifier;
    }

    /// <summary>Spells an escaped C# string literal for an arbitrary string value.</summary>
    public static string StringLiteral(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    /// <summary>
    /// Quotes text without escaping it. Generated assembly names, aliases, and member names use this established spelling.
    /// </summary>
    public static string QuotedText(string value) => "\"" + value + "\"";

    public static string Literal(uint value) => value.ToString(CultureInfo.InvariantCulture) + "U";

    public static string Literal(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Literal(long value) => value.ToString(CultureInfo.InvariantCulture) + "L";

    /// <summary>Spells <c>name&lt;,,&gt;</c> for an open generic name with the given arity.</summary>
    public static string OpenGenericArguments(int arity) => arity switch
    {
        <= 0 => string.Empty,
        _ => "<" + new string(',', arity - 1) + ">",
    };
}
