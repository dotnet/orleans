using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Utilities;

namespace Orleans.Runtime
{
    /// <summary>
    /// Formats and parses <see cref="Type"/> instances using configured rules.
    /// </summary>
    public class TypeConverter
    {
        private readonly ITypeConverter[] _converters;
        private readonly ClrTypeConverter _defaultFormatter;
        private readonly Func<QualifiedType, QualifiedType> _convertToDisplayName;
        private readonly Func<QualifiedType, QualifiedType> _convertFromDisplayName;

        public TypeConverter(IEnumerable<ITypeConverter> formatters)
        {
            _converters = formatters.ToArray();
            _defaultFormatter = new ClrTypeConverter();
            _convertToDisplayName = ConvertToDisplayName;
            _convertFromDisplayName = ConvertFromDisplayName;
        }

        /// <summary>
        /// Formats the provided type.
        /// </summary>
        public string Format(Type type) => FormatInternal(type);

        /// <summary>
        /// Formats the provided type, rewriting elements using the provided delegate.
        /// </summary>
        internal string Format(Type type, Func<TypeSpec, TypeSpec> rewriter) => FormatInternal(type, rewriter);

        /// <summary>
        /// Parses the provided type string.
        /// </summary>
        public Type Parse(string formatted) => ParseInternal(formatted);

        private string FormatInternal(Type type, Func<TypeSpec, TypeSpec> rewriter = null)
        {
            string runtimeType = null;
            foreach (var converter in _converters)
            {
                if (converter.TryFormat(type, out var value))
                {
                    runtimeType = value;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(runtimeType))
            {
                runtimeType = _defaultFormatter.Format(type);
            }

            var runtimeTypeSpec = RuntimeTypeNameParser.Parse(runtimeType);
            var displayTypeSpec = RuntimeTypeNameRewriter.Rewrite(runtimeTypeSpec, _convertToDisplayName);
            if (rewriter is object)
            {
                displayTypeSpec = rewriter(displayTypeSpec);
            }

            var formatted = displayTypeSpec.Format();

            return formatted;
        }

        private Type ParseInternal(string formatted)
        {
            var parsed = RuntimeTypeNameParser.Parse(formatted);
            var runtimeTypeSpec = RuntimeTypeNameRewriter.Rewrite(parsed, _convertFromDisplayName);
            var runtimeType = runtimeTypeSpec.Format();

            foreach (var converter in _converters)
            {
                if (converter.TryParse(runtimeType, out var result))
                {
                    return result;
                }
            }

            return _defaultFormatter.Parse(runtimeType);
        }

        private QualifiedType ConvertToDisplayName(QualifiedType input)
        {
            return input switch
            {
                (_, "System.Object") => new QualifiedType(null, "object"),
                (_, "System.String") => new QualifiedType(null, "string"),
                (_, "System.Char") => new QualifiedType(null, "char"),
                (_, "System.SByte") => new QualifiedType(null, "sbyte"),
                (_, "System.Byte") => new QualifiedType(null, "byte"),
                (_, "System.Boolean") => new QualifiedType(null, "bool"),
                (_, "System.Int16") => new QualifiedType(null, "short"),
                (_, "System.UInt16") => new QualifiedType(null, "ushort"),
                (_, "System.Int32") => new QualifiedType(null, "int"),
                (_, "System.UInt32") => new QualifiedType(null, "uint"),
                (_, "System.Int64") => new QualifiedType(null, "long"),
                (_, "System.UInt64") => new QualifiedType(null, "ulong"),
                (_, "System.Single") => new QualifiedType(null, "float"),
                (_, "System.Double") => new QualifiedType(null, "double"),
                (_, "System.Decimal") => new QualifiedType(null, "decimal"),
                _ => input,
            };
        }

        private QualifiedType ConvertFromDisplayName(QualifiedType input)
        {
            return input switch
            {
                (_, "object") => new QualifiedType(null, "System.Object"),
                (_, "string") => new QualifiedType(null, "System.String"),
                (_, "char") => new QualifiedType(null, "System.Char"),
                (_, "sbyte") => new QualifiedType(null, "System.SByte"),
                (_, "byte") => new QualifiedType(null, "System.Byte"),
                (_, "bool") => new QualifiedType(null, "System.Boolean"),
                (_, "short") => new QualifiedType(null, "System.Int16"),
                (_, "ushort") => new QualifiedType(null, "System.UInt16"),
                (_, "int") => new QualifiedType(null, "System.Int32"),
                (_, "uint") => new QualifiedType(null, "System.UInt32"),
                (_, "long") => new QualifiedType(null, "System.Int64"),
                (_, "ulong") => new QualifiedType(null, "System.UInt64"),
                (_, "float") => new QualifiedType(null, "System.Single"),
                (_, "double") => new QualifiedType(null, "System.Double"),
                (_, "decimal") => new QualifiedType(null, "System.Decimal"),
                _ => input,
            };
        }
    }

    /// <summary>
    /// Extensions for working with <see cref="TypeConverter"/>.
    /// </summary>
    public static class TypeConverterExtensions
    {
        private const char GenericTypeIndicator = '`';
        private const char StartArgument = '[';

        /// <summary>
        /// Returns true if the provided type string is a generic type.
        /// </summary>
        public static bool IsGenericType(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return false;
            return type.IndexOf(GenericTypeIndicator) >= 0;
        }

        /// <summary>
        /// Returns true if the provided type string is a constructed generic type.
        /// </summary>
        public static bool IsConstructed(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return false;
            var index = type.IndexOf(StartArgument);
            return index > 0;
        }

        /// <summary>
        /// Returns the deconstructed form of the provided generic type.
        /// </summary>
        public static string GetDeconstructed(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return null;
            var index = type.IndexOf(StartArgument);

            if (index <= 0)
            {
                return type;
            }

            return type.Substring(0, index);
        }

        /// <summary>
        /// Returns the constructed form of the provided generic type.
        /// </summary>
        public static string GetConstructed(this TypeConverter formatter, string unconstructed, params Type[] typeArguments)
        {
            var typeString = unconstructed;
            var indicatorIndex = typeString.IndexOf(GenericTypeIndicator);
            var argumentsIndex = typeString.IndexOf(StartArgument, indicatorIndex);
            if (argumentsIndex >= 0)
            {
                throw new InvalidOperationException("Cannot construct an already-constructed type");
            }

            var arityString = typeString.Substring(indicatorIndex + 1);
            var arity = int.Parse(arityString);
            if (typeArguments.Length != arity)
            {
                throw new InvalidOperationException($"Insufficient number of type arguments, {typeArguments.Length}, provided while constructing type \"{unconstructed}\" of arity {arity}");
            }

            var typeSpecs = new TypeSpec[typeArguments.Length];
            for (var i = 0; i < typeArguments.Length; i++)
            {
                typeSpecs[i] = RuntimeTypeNameParser.Parse(formatter.Format(typeArguments[i]));
            }

            var constructed = new ConstructedGenericTypeSpec(new NamedTypeSpec(null, typeString, typeArguments.Length), typeSpecs).Format();
            return constructed;
        }

        /// <summary>
        /// Returns the type arguments for the provided constructed generic type string.
        /// </summary>
        public static string GetArgumentsString(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return null;
            var index = type.IndexOf(StartArgument);

            if (index <= 0)
            {
                return null;
            }

            return type.Substring(index);
        }

        /// <summary>
        /// Returns the type arguments for the provided constructed generic type string.
        /// </summary>
        public static Type[] GetArguments(this TypeConverter formatter, string constructed)
        {
            var str = constructed;
            var index = str.IndexOf(StartArgument);
            if (index <= 0)
            {
                return Array.Empty<Type>();
            }

            var safeString = "safer" + str.Substring(str.IndexOf(GenericTypeIndicator));
            var parsed = RuntimeTypeNameParser.Parse(safeString);
            if (!(parsed is ConstructedGenericTypeSpec spec))
            {
                throw new InvalidOperationException($"Unable to correctly parse grain type {str}");
            }

            var result = new Type[spec.Arguments.Length];
            for (var i = 0; i < result.Length; i++)
            {
                var arg = spec.Arguments[i];
                var formattedArg = arg.Format();
                result[i] = formatter.Parse(formattedArg);
                if (result[i] is null)
                {
                    throw new InvalidOperationException($"Unable to parse argument \"{formattedArg}\" as a type for grain type \"{str}\"");
                }
            }

            return result;
        }
    }

    internal class ClrTypeConverter
    {
        private readonly CachedTypeResolver _resolver = new CachedTypeResolver();

        public string Format(Type type) => RuntimeTypeNameFormatter.Format(type);

        public Type Parse(string formatted) => _resolver.ResolveType(formatted);
    }

    /// <summary>
    /// Converts between <see cref="Type"/> and <see cref="string"/> representations.
    /// </summary>
    public interface ITypeConverter
    {
        /// <summary>
        /// Formats the provided type as a string.
        /// </summary>
        bool TryFormat(Type type, out string formatted);

        /// <summary>
        /// Parses the provided type.
        /// </summary>
        bool TryParse(string formatted, out Type type);
    }
}
