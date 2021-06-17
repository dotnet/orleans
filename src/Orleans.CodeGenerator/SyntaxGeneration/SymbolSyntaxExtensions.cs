using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Reflection;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

using System.Diagnostics;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.SyntaxGeneration
{
    internal static class SymbolSyntaxExtensions
    {
        public static ParenthesizedExpressionSyntax GetBindingFlagsParenthesizedExpressionSyntax(
            SyntaxKind operationKind,
            params BindingFlags[] bindingFlags)
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

        /// <summary>
        /// Returns the System.String that represents the current TypedConstant.
        /// </summary>
        /// <returns>A System.String that represents the current TypedConstant.</returns>
        public static ExpressionSyntax ToExpression(this TypedConstant constant)
        {
            if (constant.IsNull)
            {
                return LiteralExpression(SyntaxKind.NullLiteralExpression);
            }

            if (constant.Kind == TypedConstantKind.Array)
            {
                throw new NotSupportedException($"Unsupported TypedConstant: {constant.ToCSharpString()}");
            }

            if (constant.Kind == TypedConstantKind.Type)
            {
                Debug.Assert(constant.Value is object);
                return TypeOfExpression(((ITypeSymbol)constant.Value).ToTypeSyntax());
            }

            if (constant.Kind == TypedConstantKind.Enum)
            {
                return DisplayEnumConstant(constant);
            }

            return ParseExpression(constant.ToCSharpString());
        }

        // Decode the value of enum constant
        private static ExpressionSyntax DisplayEnumConstant(TypedConstant constant)
        {
            //string typeName = constant.Type.ToDisplayName();
            var constantToDecode = ConvertToUInt64(constant.Value);
            ulong curValue = 0;

            // Iterate through all the constant members in the enum type
            var members = constant.Type!.GetMembers();
            var type = constant.Type.ToTypeSyntax();
            ExpressionSyntax result = null;
            foreach (var member in members)
            {
                var field = member as IFieldSymbol;

                if (field is object && field.HasConstantValue)
                {
                    ulong memberValue = ConvertToUInt64(field.ConstantValue);

                    if (memberValue == constantToDecode)
                    {
                        return constant.Type.ToTypeSyntax().Member(field.Name);
                    }

                    if ((memberValue & constantToDecode) == memberValue)
                    {
                        // update the current value
                        curValue = curValue | memberValue;

                        var valueExpression = type.Member(field.Name);
                        if (result is null)
                        {
                            result = valueExpression;
                        }
                        else
                        {
                            result = BinaryExpression(SyntaxKind.BitwiseOrExpression, result, valueExpression);
                        }
                    }
                }
            }

            return result;
        }

        private static ulong ConvertToUInt64(object value)
        {
            return value switch
            {
                byte b => b,
                sbyte sb => (ulong)sb,
                short s => (ulong)s,
                ushort us => us,
                int i => (ulong)i,
                uint ui => ui,
                long l => (ulong)l,
                ulong ul => ul,
                _ => throw new NotSupportedException($"Type {value?.GetType()} not supported")
            };
        }
    }
}