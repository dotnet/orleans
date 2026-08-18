using System;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGeneration;

internal static class RpcMethodIdGenerator
{
    private const uint Prime1 = 2_654_435_761U;
    private const uint Prime2 = 2_246_822_519U;
    private const uint Prime3 = 3_266_489_917U;
    private const uint Prime4 = 668_265_263U;
    private const uint Prime5 = 374_761_393U;

    public static string GetId(IMethodSymbol method)
    {
        var signature = Format(method);
        var hash = ComputeHash(Encoding.UTF8.GetBytes(signature));
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

    private static uint ComputeHash(ReadOnlySpan<byte> source)
    {
        var offset = 0;
        uint hash;
        if (source.Length >= 16)
        {
            var v1 = unchecked(Prime1 + Prime2);
            var v2 = Prime2;
            uint v3 = 0;
            var v4 = unchecked(0U - Prime1);
            var limit = source.Length - 16;
            do
            {
                v1 = Round(v1, ReadUInt32(source, offset));
                offset += 4;
                v2 = Round(v2, ReadUInt32(source, offset));
                offset += 4;
                v3 = Round(v3, ReadUInt32(source, offset));
                offset += 4;
                v4 = Round(v4, ReadUInt32(source, offset));
                offset += 4;
            }
            while (offset <= limit);

            hash = RotateLeft(v1, 1)
                + RotateLeft(v2, 7)
                + RotateLeft(v3, 12)
                + RotateLeft(v4, 18);
        }
        else
        {
            hash = Prime5;
        }

        hash += (uint)source.Length;
        while (offset <= source.Length - 4)
        {
            hash += ReadUInt32(source, offset) * Prime3;
            hash = RotateLeft(hash, 17) * Prime4;
            offset += 4;
        }

        while (offset < source.Length)
        {
            hash += source[offset] * Prime5;
            hash = RotateLeft(hash, 11) * Prime1;
            offset++;
        }

        hash ^= hash >> 15;
        hash *= Prime2;
        hash ^= hash >> 13;
        hash *= Prime3;
        hash ^= hash >> 16;
        return hash;
    }

    private static uint Round(uint accumulator, uint input)
    {
        accumulator += input * Prime2;
        accumulator = RotateLeft(accumulator, 13);
        accumulator *= Prime1;
        return accumulator;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset) =>
        source[offset]
        | (uint)source[offset + 1] << 8
        | (uint)source[offset + 2] << 16
        | (uint)source[offset + 3] << 24;

    private static uint RotateLeft(uint value, int offset) =>
        value << offset | value >> (32 - offset);
}
