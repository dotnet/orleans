using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator.Utilities
{
    /// <summary>
    /// Extensions to the generate syntax for literals.
    /// </summary>
    internal static class LiteralExtensions
    {
        /// <summary>
        /// Returns the provided string as a literal expression.
        /// </summary>
        public static LiteralExpressionSyntax ToLiteralExpression(this string str)
        {
            var syntaxToken = Literal(TriviaList(), @"""" + str + @"""", str, TriviaList());
            return LiteralExpression(SyntaxKind.StringLiteralExpression, syntaxToken);
        }

        public static SyntaxToken ToIdentifier(this string identifier)
        {
            identifier = identifier.TrimStart('@');
            
            if (Identifier.IsCSharpKeyword(identifier))
            {
                return VerbatimIdentifier(
                    SyntaxTriviaList.Empty,
                    identifier,
                    identifier,
                    SyntaxTriviaList.Empty);
            }

            return Identifier(SyntaxTriviaList.Empty, identifier, SyntaxTriviaList.Empty);
        }

        public static IdentifierNameSyntax ToIdentifierName(this string identifier)
        {
            return IdentifierName(identifier.ToIdentifier());
        }

        public static GenericNameSyntax ToGenericName(this string identifier)
        {
            return GenericName(identifier.ToIdentifier());
        }

        public static ExpressionSyntax ToHexLiteral(this int val)
        {
            ExpressionSyntax expr = CastExpression(PredefinedType(Token(SyntaxKind.IntKeyword)),
                LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal($"0x{val:X}", val)));
            if (val < 0)
            {
                expr = CheckedExpression(SyntaxKind.UncheckedExpression, expr);
            }

            return expr;
        }
    }
}
