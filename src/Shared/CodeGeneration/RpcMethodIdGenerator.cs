using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Hashing;

namespace Orleans.CodeGeneration;

internal static class RpcMethodIdGenerator
{
    public static string GetId(IMethodSymbol method)
    {
        var signature = Format(method);
        var hashBytes = XxHash32.Hash(Encoding.UTF8.GetBytes(signature));
        var hash = BinaryPrimitives.ReadUInt32BigEndian(hashBytes);
        return hash.ToString("X8", CultureInfo.InvariantCulture);
    }

    private static string Format(IMethodSymbol method)
    {
        var result = new StringBuilder();
        result.Append(method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        result.Append('.');
        result.Append(method.Name);

        if (method.IsGenericMethod)
        {
            result.Append('<');
            for (var i = 0; i < method.TypeArguments.Length; i++)
            {
                if (i > 0)
                {
                    result.Append(',');
                }

                result.Append(method.TypeArguments[i].Name);
            }

            result.Append('>');
        }

        result.Append('(');
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            if (i > 0)
            {
                result.Append(',');
            }

            var parameterType = method.Parameters[i].Type;
            result.Append(parameterType is ITypeParameterSymbol
                ? parameterType.Name
                : parameterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }

        result.Append(')');
        return result.ToString();
    }
}
