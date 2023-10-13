using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Serialization.TypeSystem;

/// <summary>
/// Utility class for parsing type names, as formatted by <see cref="RuntimeTypeNameFormatter"/>.
/// </summary>
public static class RuntimeTypeNameParser
{
    internal const int MaxAllowedGenericArity = 64;
    internal const char CompoundAliasStartIndicator = '(';
    internal const char CompoundAliasEndIndicator = ')';
    internal const char CompoundAliasElementSeparator = ',';
    internal const char LiteralDelimiter = '"';
    internal const char PointerIndicator = '*';
    internal const char ReferenceIndicator = '&';
    internal const char ArrayStartIndicator = '[';
    internal const char ArrayDimensionIndicator = ',';
    internal const char ArrayEndIndicator = ']';
    internal const char ParameterSeparator = ',';
    internal const char GenericTypeIndicator = '`';
    internal const char NestedTypeIndicator = '+';
    internal const char AssemblyIndicator = ',';
    internal static ReadOnlySpan<char> LiteralDelimiters => new[] { LiteralDelimiter };
    internal static ReadOnlySpan<char> TupleDelimiters => new[] { CompoundAliasElementSeparator, CompoundAliasEndIndicator };
    internal static ReadOnlySpan<char> AssemblyDelimiters => new[] { ArrayEndIndicator, CompoundAliasElementSeparator, CompoundAliasEndIndicator };
    internal static ReadOnlySpan<char> TypeNameDelimiters => new[] { ArrayStartIndicator, ArrayEndIndicator, PointerIndicator, ReferenceIndicator, AssemblyIndicator, GenericTypeIndicator, NestedTypeIndicator };

    /// <summary>
    /// Parse the provided value as a type name.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <returns>A parsed type specification.</returns>
    public static TypeSpec Parse(string input) => Parse(input.AsSpan());

    /// <summary>
    /// Parse the provided value as a type name.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <returns>A parsed type specification.</returns>
    public static TypeSpec Parse(ReadOnlySpan<char> input) => ParseInternal(ref input);

    private static TypeSpec ParseInternal(ref ReadOnlySpan<char> input)
    {
        BufferReader s = default;
        s.Input = input;
        var result = ParseInternal(ref s);
        input = s.Remaining;
        return result;
    }

    private static TypeSpec ParseInternal(ref BufferReader input)
    {
        TypeSpec result;
        char c;
        var arity = 0;

        TypeSpec coreType = null;

        // Read tuple
        if (input.TryPeek(out c) && c == CompoundAliasStartIndicator)
        {
            input.ConsumeCharacter(CompoundAliasStartIndicator);
            var elements = new List<TypeSpec>();
            while (input.TryPeek(out c) && c != CompoundAliasEndIndicator)
            {
                if (c == CompoundAliasElementSeparator)
                {
                    input.ConsumeCharacter(CompoundAliasElementSeparator);
                }

                input.ConsumeWhitespace();

                var element = ParseCompoundTypeAliasElement(ref input);
                elements.Add(element);
            }

            input.ConsumeCharacter(CompoundAliasEndIndicator);

            var genericArityStart = -1;
            while (input.TryPeek(out c))
            {
                if (genericArityStart < 0 && c == GenericTypeIndicator)
                {
                    genericArityStart = input.Index + 1;
                }
                else if (genericArityStart < 0 || !char.IsDigit(c))
                {
                    break;
                }

                input.ConsumeCharacter(c);
            }

            if (genericArityStart >= 0)
            {
                var aritySlice = input.Input[genericArityStart..input.Index];
#if NETCOREAPP3_1_OR_GREATER
                arity = int.Parse(aritySlice);
#else
                arity = int.Parse(aritySlice.ToString());
#endif
                input.TotalGenericArity += arity;
                if (input.TotalGenericArity > MaxAllowedGenericArity)
                {
                    ThrowGenericArityTooLarge(input.TotalGenericArity);
                }
            }

            coreType = new TupleTypeSpec(elements.ToArray(), input.TotalGenericArity);
        }
        else
        {
            // Read namespace and class name, including generic arity, which is a part of the class name.
            NamedTypeSpec named = null;
            while (true)
            {
                var typeName = ParseTypeName(ref input);
                named = new NamedTypeSpec(named, typeName.ToString(), input.TotalGenericArity);
                arity = named.Arity;

                if (input.TryPeek(out c) && c == NestedTypeIndicator)
                {
                    // Consume the nested type indicator, then loop to parse the nested type.
                    input.ConsumeCharacter(NestedTypeIndicator);
                    continue;
                }

                break;
            }

            coreType = named;
        }

        // Parse generic type parameters
        if (input.TotalGenericArity > 0 && input.TryPeek(out c, out var d) && c == ArrayStartIndicator && d == ArrayStartIndicator)
        {
            input.ConsumeCharacter(ArrayStartIndicator);
            var arguments = new TypeSpec[input.TotalGenericArity];
            for (var i = 0; i < input.TotalGenericArity; i++)
            {
                if (i > 0)
                {
                    input.ConsumeCharacter(ParameterSeparator);
                }

                // Parse the argument type
                input.ConsumeCharacter(ArrayStartIndicator);
                var remaining = input.Remaining;
                arguments[i] = ParseInternal(ref remaining);
                var consumed = input.Remaining.Length - remaining.Length;
                input.Consume(consumed);
                input.ConsumeCharacter(ArrayEndIndicator);
            }

            input.ConsumeCharacter(ArrayEndIndicator);
            result = new ConstructedGenericTypeSpec(coreType, arity, arguments);
        }
        else
        {
            // This is not a constructed generic type
            result = coreType;
        }

        // Parse modifiers
        bool hadModifier;
        do
        {
            hadModifier = false;

            if (!input.TryPeek(out c))
            {
                break;
            }

            switch (c)
            {
                case ArrayStartIndicator:
                    var dimensions = ParseArraySpecifier(ref input);
                    result = new ArrayTypeSpec(result, dimensions);
                    hadModifier = true;
                    break;
                case PointerIndicator:
                    result = new PointerTypeSpec(result);
                    input.ConsumeCharacter(PointerIndicator);
                    hadModifier = true;
                    break;
                case ReferenceIndicator:
                    result = new ReferenceTypeSpec(result);
                    input.ConsumeCharacter(ReferenceIndicator);
                    hadModifier = true;
                    break;
            }
        } while (hadModifier);

        // Extract the assembly, if specified.
        if (input.TryPeek(out c) && c == AssemblyIndicator)
        {
            input.ConsumeCharacter(AssemblyIndicator);
            var assembly = ExtractAssemblySpec(ref input);
            result = new AssemblyQualifiedTypeSpec(result, assembly.ToString());
        }

        return result;
    }

    private static ReadOnlySpan<char> ParseTypeName(ref BufferReader s)
    {
        var start = s.Index;
        var typeName = ParseSpan(ref s, TypeNameDelimiters);
        var genericArityStart = -1;
        while (s.TryPeek(out char c))
        {
            if (genericArityStart < 0 && c == GenericTypeIndicator)
            {
                genericArityStart = s.Index + 1;
            }
            else if (genericArityStart < 0 || !char.IsDigit(c))
            {
                break;
            }

            s.ConsumeCharacter(c);
        }

        if (genericArityStart >= 0)
        {
            // The generic arity is additive, so that a generic class nested in a generic class has an arity
            // equal to the sum of specified arity values. For example, "C`1+N`2" has an arity of 3.
            var aritySlice = s.Input[genericArityStart..s.Index];
#if NETCOREAPP3_1_OR_GREATER
            var arity = int.Parse(aritySlice);
#else
            var arity = int.Parse(aritySlice.ToString());
#endif
            s.TotalGenericArity += arity;
            if (s.TotalGenericArity > MaxAllowedGenericArity)
            {
                ThrowGenericArityTooLarge(s.TotalGenericArity);
            }

            // Include the generic arity in the type name.
            typeName = s.Input[start..s.Index];
        }

        return typeName;
    }

    private static int ParseArraySpecifier(ref BufferReader s)
    {
        s.ConsumeCharacter(ArrayStartIndicator);
        var dimensions = 1;

        while (s.TryPeek(out var c) && c != ArrayEndIndicator)
        {
            s.ConsumeCharacter(ArrayDimensionIndicator);
            ++dimensions;
        }

        s.ConsumeCharacter(ArrayEndIndicator);
        return dimensions;
    }

    private static ReadOnlySpan<char> ExtractAssemblySpec(ref BufferReader s)
    {
        s.ConsumeWhitespace();
        return ParseSpan(ref s, AssemblyDelimiters);
    }

    private static ReadOnlySpan<char> ParseSpan(ref BufferReader s, ReadOnlySpan<char> delimiters)
    {
        ReadOnlySpan<char> result;
        if (s.Remaining.IndexOfAny(delimiters) is int index && index > 0)
        {
            result = s.Remaining[..index];
        }
        else
        {
            result = s.Remaining;
        }

        s.Consume(result.Length);
        return result;
    }

    private static TypeSpec ParseCompoundTypeAliasElement(ref BufferReader input)
    {
        char c;

        // Read literal value
        if (input.TryPeek(out c))
        {
            if (c == LiteralDelimiter)
            {
                input.ConsumeCharacter(LiteralDelimiter);
                var literalSpan = ParseSpan(ref input, LiteralDelimiters);
                input.ConsumeCharacter(LiteralDelimiter);
                return new LiteralTypeSpec(new string(literalSpan));
            }
            else if (c == ArrayStartIndicator)
            {
                // Parse the argument type
                input.ConsumeCharacter(ArrayStartIndicator);
                var remaining = input.Remaining;
                var result = ParseInternal(ref remaining);
                var consumed = input.Remaining.Length - remaining.Length;
                input.Consume(consumed);
                input.ConsumeCharacter(ArrayEndIndicator);
                return result;
            }

            throw new ArgumentException($"Unexpected token '{c}' when reading compound type alias element.", nameof(input));
        }
        else
        {
            throw new IndexOutOfRangeException("Attempted to read past the end of the input buffer.");
        }
    }

    private static void ThrowGenericArityTooLarge(int arity) => throw new NotSupportedException($"An arity of {arity} is not supported.");

    private ref struct BufferReader
    {
        public ReadOnlySpan<char> Input;
        public int Index;
        public int TotalGenericArity;

        public readonly ReadOnlySpan<char> Remaining => Input[Index..];

        public readonly bool TryPeek(out char c)
        {
            if (Index < Input.Length)
            {
                c = Input[Index];
                return true;
            }

            c = default;
            return false;
        }

        public readonly bool TryPeek(out char c, out char d)
        {
            var result = TryPeek(out c);
            result &= TryPeek(Index + 1, out d);
            return result;
        }

        public readonly bool TryPeek(int index, out char c)
        {
            if (index < Input.Length)
            {
                c = Input[index];
                return true;
            }

            c = default;
            return false;
        }

        public void Consume(int chars)
        {
            if (Index < Input.Length)
            {
                Index += chars;
                return;
            }

            ThrowEndOfInput();
        }

        public void ConsumeCharacter(char assertChar)
        {
            if (Index < Input.Length)
            {
                var c = Input[Index];
                if (assertChar != c)
                {
                    ThrowUnexpectedCharacter(assertChar, c);
                }

                ++Index;
                return;
            }

            ThrowEndOfInput();
        }

        public void ConsumeWhitespace()
        {
            while (char.IsWhiteSpace(Input[Index]))
            {
                ++Index;
            }
        }

        private static void ThrowUnexpectedCharacter(char expected, char actual) => throw new InvalidOperationException($"Encountered unexpected character. Expected '{expected}', actual '{actual}'.");

        private static void ThrowEndOfInput() => throw new InvalidOperationException("Tried to read past the end of the input");

        public override readonly string ToString()
        {
            var result = new StringBuilder();
            var i = 0;
            foreach (var c in Input)
            {
                if (i == Index)
                {
                    result.Append("^^^");
                }

                result.Append(c);

                if (i == Index)
                {
                    result.Append("^^^");
                }

                ++i;
            }

            return result.ToString();
        }
    }
}