namespace Orleans.CodeGenerator.Utilities
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

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
            return SyntaxFactory.VerbatimIdentifier(
                SyntaxTriviaList.Empty,
                identifier,
                identifier,
                SyntaxTriviaList.Empty);
        }

        public static IdentifierNameSyntax ToIdentifierName(this string identifier)
        {
            return SyntaxFactory.IdentifierName(identifier.ToIdentifier());
        }

        public static GenericNameSyntax ToGenericName(this string identifier)
        {
            return SyntaxFactory.GenericName(identifier.ToIdentifier());
        }
    }
}
