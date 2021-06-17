using System;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.TypeSystem
{
    /// <summary>
    /// Utility class for parsing CLR type names, as formatted by <see cref="RuntimeTypeNameFormatter"/>.
    /// </summary>
    public static class RuntimeTypeNameParser
    {
        private const int MaxAllowedGenericArity = 64;
        private const char PointerIndicator = '*';
        private const char ReferenceIndicator = '&';
        private const char ArrayStartIndicator = '[';
        private const char ArrayDimensionIndicator = ',';
        private const char ArrayEndIndicator = ']';
        private const char ParameterSeparator = ',';
        private const char GenericTypeIndicator = '`';
        private const char NestedTypeIndicator = '+';
        private const char AssemblyIndicator = ',';
        private static ReadOnlySpan<char> AssemblyDelimiters => new[] { ArrayEndIndicator };
        private static ReadOnlySpan<char> TypeNameDelimiters => new[] { ArrayStartIndicator, ArrayEndIndicator, PointerIndicator, ReferenceIndicator, AssemblyIndicator, GenericTypeIndicator, NestedTypeIndicator };

        /// <summary>
        /// Parse the provided value as a type name.
        /// </summary>
        public static TypeSpec Parse(string input) => Parse(input.AsSpan());

        /// <summary>
        /// Parse the provided value as a type name.
        /// </summary>
        public static TypeSpec Parse(ReadOnlySpan<char> input) => ParseInternal(ref input);

        private static TypeSpec ParseInternal(ref ReadOnlySpan<char> input)
        {
            TypeSpec result;
            char c;
            BufferReader s = default;
            s.Input = input;

            // Read namespace and class name, including generic arity, which is a part of the class name.
            NamedTypeSpec named = null;
            while (true)
            {
                var typeName = ParseTypeName(ref s);
                named = new NamedTypeSpec(named, typeName.ToString(), s.TotalGenericArity);

                if (s.TryPeek(out c) && c == NestedTypeIndicator)
                {
                    // Consume the nested type indicator, then loop to parse the nested type.
                    s.ConsumeCharacter(NestedTypeIndicator);
                    continue;
                }

                break;
            }

            // Parse generic type parameters
            if (s.TotalGenericArity > 0 && s.TryPeek(out c) && c == ArrayStartIndicator)
            {
                s.ConsumeCharacter(ArrayStartIndicator);

                var arguments = new TypeSpec[s.TotalGenericArity];
                for (var i = 0; i < s.TotalGenericArity; i++)
                {
                    if (i > 0)
                    {
                        s.ConsumeCharacter(ParameterSeparator);
                    }

                    // Parse the argument type
                    s.ConsumeCharacter(ArrayStartIndicator);
                    var remaining = s.Remaining;
                    arguments[i] = ParseInternal(ref remaining);
                    var consumed = s.Remaining.Length - remaining.Length;
                    s.Consume(consumed);
                    s.ConsumeCharacter(ArrayEndIndicator);
                }

                s.ConsumeCharacter(ArrayEndIndicator);
                result = new ConstructedGenericTypeSpec(named, arguments);
            }
            else
            {
                // This is not a constructed generic type
                result = named;
            }

            // Parse modifiers
            bool hadModifier;
            do
            {
                hadModifier = false;

                if (!s.TryPeek(out c))
                {
                    break;
                }

                switch (c)
                {
                    case ArrayStartIndicator:
                        var dimensions = ParseArraySpecifier(ref s);
                        result = new ArrayTypeSpec(result, dimensions);
                        hadModifier = true;
                        break;
                    case PointerIndicator:
                        result = new PointerTypeSpec(result);
                        s.ConsumeCharacter(PointerIndicator);
                        hadModifier = true;
                        break;
                    case ReferenceIndicator:
                        result = new ReferenceTypeSpec(result);
                        s.ConsumeCharacter(ReferenceIndicator);
                        hadModifier = true;
                        break;
                }
            } while (hadModifier);

            // Extract the assembly, if specified.
            if (s.TryPeek(out c) && c == AssemblyIndicator)
            {
                s.ConsumeCharacter(AssemblyIndicator);
                var assembly = ExtractAssemblySpec(ref s);
                result = new AssemblyQualifiedTypeSpec(result, assembly.ToString());
            }

            input = s.Remaining;
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
                var aritySlice = s.Input.Slice(genericArityStart, s.Index - genericArityStart);
#if NETCOREAPP
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
                typeName = s.Input.Slice(start, s.Index - start);
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
                result = s.Remaining.Slice(0, index);
            }
            else
            {
                result = s.Remaining;
            }

            s.Consume(result.Length);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowGenericArityTooLarge(int arity) => throw new NotSupportedException($"An arity of {arity} is not supported");

        private ref struct BufferReader
        {
            public ReadOnlySpan<char> Input;
            public int Index;
            public int TotalGenericArity;

            public readonly ReadOnlySpan<char> Remaining => Input.Slice(Index);

            public bool TryPeek(out char c)
            {
                if (Index < Input.Length)
                {
                    c = Input[Index];
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
                        return;
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

            [MethodImpl(MethodImplOptions.NoInlining)]
            private static void ThrowUnexpectedCharacter(char expected, char actual) => throw new InvalidOperationException($"Encountered unexpected character. Expected '{expected}', actual '{actual}'.");

            [MethodImpl(MethodImplOptions.NoInlining)]
            private static void ThrowEndOfInput() => throw new InvalidOperationException("Tried to read past the end of the input");
        }
    }
}