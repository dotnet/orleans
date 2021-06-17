using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator.SyntaxGeneration
{
    /// <summary>
    /// Extensions to the <see cref="string"/> class to support code generation.
    /// </summary>
    internal static class StringExtensions
    {
        /// <summary>
        /// Returns the provided string as a literal expression.
        /// </summary>
        /// <param name="str">
        /// The string.
        /// </param>
        /// <returns>
        /// The literal expression.
        /// </returns>
        public static LiteralExpressionSyntax GetLiteralExpression(this string str)
        {
            var syntaxToken = SyntaxFactory.Literal(
                SyntaxFactory.TriviaList(),
                @"""" + str + @"""",
                str,
                SyntaxFactory.TriviaList());
            return SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, syntaxToken);
        }

        public static SyntaxToken ToIdentifier(this string identifier)
        {
            identifier = identifier.TrimStart('@');
            if (Identifier.IsCSharpKeyword(identifier))
            {
                return SyntaxFactory.VerbatimIdentifier(
                    SyntaxTriviaList.Empty,
                    identifier,
                    identifier,
                    SyntaxTriviaList.Empty);
            }

            return SyntaxFactory.Identifier(SyntaxTriviaList.Empty, identifier, SyntaxTriviaList.Empty);
        }

        public static string EscapeIdentifier(this string str)
        {
            if (Identifier.IsCSharpKeyword(str))
            {
                return "@" + str;
            }

            return str;
        }

        public static IdentifierNameSyntax ToIdentifierName(this string identifier) => SyntaxFactory.IdentifierName(identifier.ToIdentifier());
    }
}