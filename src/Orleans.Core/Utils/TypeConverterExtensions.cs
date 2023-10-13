using System;
using System.Buffers.Text;
using System.Text;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Utilities
{
    /// <summary>
    /// Extensions for working with <see cref="TypeConverter"/>.
    /// </summary>
    internal static class TypeConverterExtensions
    {
        private const char GenericTypeIndicator = '`';
        private const char StartArgument = '[';

        /// <summary>
        /// Returns true if the provided type string is a generic type.
        /// </summary>
        public static bool IsGenericType(IdSpan type) => type.AsSpan().IndexOf((byte)GenericTypeIndicator) >= 0;

        /// <summary>
        /// Returns true if the provided type string is a constructed generic type.
        /// </summary>
        public static bool IsConstructed(IdSpan type) => type.AsSpan().IndexOf((byte)StartArgument) > 0;

        /// <summary>
        /// Returns the deconstructed form of the provided generic type.
        /// </summary>
        public static IdSpan GetDeconstructed(IdSpan type)
        {
            var span = type.AsSpan();
            var index = span.IndexOf((byte)StartArgument);
            return index <= 0 ? type : new IdSpan(span[..index].ToArray());
        }

        /// <summary>
        /// Returns the constructed form of the provided generic type.
        /// </summary>
        public static IdSpan GetConstructed(this TypeConverter formatter, IdSpan unconstructed, params Type[] typeArguments)
        {
            var typeString = unconstructed.AsSpan();
            var indicatorIndex = typeString.IndexOf((byte)GenericTypeIndicator);
            var arityString = typeString[(indicatorIndex + 1)..];
            if (indicatorIndex < 0 || arityString.IndexOf((byte)StartArgument) >= 0)
            {
                throw new InvalidOperationException("Cannot construct an already-constructed type");
            }

            if (!Utf8Parser.TryParse(arityString, out int arity, out var len) || len < arityString.Length || typeArguments.Length != arity)
            {
                throw new InvalidOperationException($"Insufficient number of type arguments, {typeArguments.Length}, provided while constructing type \"{unconstructed}\"");
            }

            var typeSpecs = new TypeSpec[typeArguments.Length];
            for (var i = 0; i < typeArguments.Length; i++)
            {
                typeSpecs[i] = RuntimeTypeNameParser.Parse(formatter.Format(typeArguments[i]));
            }

            var constructed = new ConstructedGenericTypeSpec(new NamedTypeSpec(null, unconstructed.ToString(), typeArguments.Length), typeArguments.Length, typeSpecs).Format();
            return IdSpan.Create(constructed);
        }

        /// <summary>
        /// Returns the constructed form of the provided generic grain type using the type arguments from the provided constructed interface type.
        /// </summary>
        public static GrainType GetConstructed(this GrainType grainType, GrainInterfaceType typeArguments)
        {
            var args = typeArguments.Value.AsSpan();
            var index = args.IndexOf((byte)StartArgument);
            if (index <= 0) return grainType; // if no type arguments are provided, then the current logic expects the unconstructed form (but the grain call is going to fail later anyway...)
            args = args[index..];

            var type = grainType.Value.AsSpan();
            var buf = new byte[type.Length + args.Length];
            type.CopyTo(buf);
            args.CopyTo(buf.AsSpan(type.Length));
            return new GrainType(buf);
        }

        /// <summary>
        /// Returns the type arguments for the provided constructed generic type string.
        /// </summary>
        public static Type[] GetArguments(this TypeConverter formatter, IdSpan constructed)
        {
            var str = constructed.AsSpan();
            var index = str.IndexOf((byte)StartArgument);
            if (index <= 0)
            {
                return Array.Empty<Type>();
            }

            var safeString = "safer" + Encoding.UTF8.GetString(str[str.IndexOf((byte)GenericTypeIndicator)..]);
            var parsed = RuntimeTypeNameParser.Parse(safeString);
            if (!(parsed is ConstructedGenericTypeSpec spec))
            {
                throw new InvalidOperationException($"Unable to correctly parse grain type {constructed}");
            }

            var result = new Type[spec.Arguments.Length];
            for (var i = 0; i < result.Length; i++)
            {
                var arg = spec.Arguments[i];
                var formattedArg = arg.Format();
                result[i] = formatter.Parse(formattedArg);
                if (result[i] is null)
                {
                    throw new InvalidOperationException($"Unable to parse argument \"{formattedArg}\" as a type for grain type \"{constructed}\"");
                }
            }

            return result;
        }
    }
}
