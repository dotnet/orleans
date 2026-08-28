using System.Text;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Hashing;

namespace Orleans.CodeGenerator;

internal static class MethodIdProvider
{
    public static string Create(IMethodSymbol method)
    {
        var signature = Format(method);
        var hash = XxHash32.Hash(Encoding.UTF8.GetBytes(signature));
        var result = new StringBuilder(hash.Length * 2);
        foreach (var value in hash)
        {
            result.Append(value.ToString("X2"));
        }

        return result.ToString();
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
            for (var index = 0; index < method.TypeArguments.Length; index++)
            {
                if (index > 0)
                {
                    result.Append(',');
                }

                result.Append(method.TypeArguments[index].Name);
            }

            result.Append('>');
        }

        result.Append('(');
        for (var index = 0; index < method.Parameters.Length; index++)
        {
            if (index > 0)
            {
                result.Append(',');
            }

            var parameterType = method.Parameters[index].Type;
            result.Append(parameterType is ITypeParameterSymbol
                ? parameterType.Name
                : parameterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }

        result.Append(')');
        return result.ToString();
    }
}
