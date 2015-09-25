/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
