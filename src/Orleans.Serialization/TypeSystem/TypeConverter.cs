using Microsoft.Extensions.Options;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Orleans.Serialization.TypeSystem
{
    /// <summary>
    /// Formats and parses <see cref="Type"/> instances using configured rules.
    /// </summary>
    public class TypeConverter
    {
        private readonly ITypeConverter[] _converters;
        private readonly ITypeFilter[] _filters;
        private readonly TypeResolver _resolver;
        private readonly RuntimeTypeNameRewriter.Rewriter _convertToDisplayName;
        private readonly RuntimeTypeNameRewriter.Rewriter _convertFromDisplayName;
        private readonly Dictionary<QualifiedType, QualifiedType> _wellKnownAliasToType;
        private readonly Dictionary<QualifiedType, QualifiedType> _wellKnownTypeToAlias;
        private readonly ConcurrentDictionary<QualifiedType, bool> _allowedTypes;
        private readonly HashSet<string> _allowedTypesConfiguration;

        /// <summary>
        /// Initializes a new instance of the <see cref="TypeConverter"/> class.
        /// </summary>
        /// <param name="formatters">The type name formatters.</param>
        /// <param name="filters">The type filters.</param>
        /// <param name="options">The options.</param>
        /// <param name="typeResolver">The type resolver.</param>
        public TypeConverter(IEnumerable<ITypeConverter> formatters, IEnumerable<ITypeFilter> filters, IOptions<TypeManifestOptions> options, TypeResolver typeResolver)
        {
            _resolver = typeResolver;
            _converters = formatters.ToArray();
            _filters = filters.ToArray();
            _convertToDisplayName = ConvertToDisplayName;
            _convertFromDisplayName = ConvertFromDisplayName;

            _wellKnownAliasToType = new Dictionary<QualifiedType, QualifiedType>();
            _wellKnownTypeToAlias = new Dictionary<QualifiedType, QualifiedType>();

            _allowedTypes = new ConcurrentDictionary<QualifiedType, bool>(QualifiedType.EqualityComparer);
            _allowedTypesConfiguration = new(StringComparer.Ordinal);
            foreach (var t in options.Value.AllowedTypes)
            {
                _allowedTypesConfiguration.Add(t);
            }

            ConsumeMetadata(options.Value);

            var aliases = options.Value.WellKnownTypeAliases;
            foreach (var item in aliases)
            {
                var alias = new QualifiedType(null, item.Key);
                var spec = RuntimeTypeNameParser.Parse(RuntimeTypeNameFormatter.Format(item.Value));
                string asmName = null;
                if (spec is AssemblyQualifiedTypeSpec asm)
                {
                    asmName = asm.Assembly;
                    spec = asm.Type;
                }

                var originalQualifiedType = new QualifiedType(asmName, spec.Format());
                _wellKnownTypeToAlias[originalQualifiedType] = alias;
                if (asmName is { Length: > 0 })
                {
                    _wellKnownTypeToAlias[new QualifiedType(null, spec.Format())] = alias;
                }

                _wellKnownAliasToType[alias] = originalQualifiedType;
            }
        }

        private void ConsumeMetadata(TypeManifestOptions metadata)
        {
            AddFromMetadata(metadata.Serializers, typeof(IBaseCodec<>));
            AddFromMetadata(metadata.Serializers, typeof(IValueSerializer<>));
            AddFromMetadata(metadata.Serializers, typeof(IFieldCodec<>));
            AddFromMetadata(metadata.FieldCodecs, typeof(IFieldCodec<>));
            AddFromMetadata(metadata.Activators, typeof(IActivator<>));
            AddFromMetadata(metadata.Copiers, typeof(IDeepCopier<>));
            AddFromMetadata(metadata.Copiers, typeof(IBaseCopier<>));
            foreach (var type in metadata.InterfaceProxies)
            {
                AddAllowedType(type switch {
                    { IsGenericType: true} => type.GetGenericTypeDefinition(),
                    _ => type
                });
            }

            void AddFromMetadata(IEnumerable<Type> metadataCollection, Type genericType)
            {
                Debug.Assert(genericType.GetGenericArguments().Length == 1);

                foreach (var type in metadataCollection)
                {
                    var interfaces = type.GetInterfaces();
                    foreach (var @interface in interfaces)
                    {
                        if (!@interface.IsGenericType)
                        {
                            continue;
                        }

                        if (genericType != @interface.GetGenericTypeDefinition())
                        {
                            continue;
                        }

                        var genericArgument = @interface.GetGenericArguments()[0];
                        if (typeof(object) == genericArgument)
                        {
                            continue;
                        }

                        if (genericArgument.IsConstructedGenericType && genericArgument.GenericTypeArguments.Any(arg => arg.IsGenericParameter))
                        {
                            genericArgument = genericArgument.GetGenericTypeDefinition();
                        }

                        if (genericArgument.IsGenericParameter || genericArgument.IsArray)
                        {
                            continue;
                        }

                        AddAllowedType(genericArgument);
                    }
                }
            }

            void AddAllowedType(Type type)
            {
                FormatAndAddAllowedType(type);

                if (type.DeclaringType is { } declaring)
                {
                    AddAllowedType(declaring);
                }

                foreach (var @interface in type.GetInterfaces())
                {
                    FormatAndAddAllowedType(@interface);
                }
            }

            void FormatAndAddAllowedType(Type type)
            {
                var formatted = RuntimeTypeNameFormatter.Format(type);
                var parsed = RuntimeTypeNameParser.Parse(formatted);

                // Use the type name rewriter to visit every component of the type.
                _ = RuntimeTypeNameRewriter.Rewrite(parsed, AddQualifiedType);
                QualifiedType AddQualifiedType(in QualifiedType type)
                {
                    _allowedTypes[type] = true;
                    return type;
                }
            }
        }

        /// <summary>
        /// Formats the provided type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>The formatted type name.</returns>
        public string Format(Type type) => FormatInternal(type);

        /// <summary>
        /// Formats the provided type, rewriting elements using the provided delegate.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="rewriter">A delegate used to rewrite the type.</param>
        /// <returns>The formatted type name.</returns>
        public string Format(Type type, Func<TypeSpec, TypeSpec> rewriter) => FormatInternal(type, rewriter);

        /// <summary>
        /// Parses the provided type string.
        /// </summary>
        /// <param name="formatted">The formatted type name.</param>
        /// <returns>The parsed type.</returns>
        /// <exception cref="TypeLoadException">Unable to load the resulting type.</exception>
        public Type Parse(string formatted)
        {
            if (ParseInternal(formatted, out var type))
            {
                return type;
            }

            throw new TypeLoadException($"Unable to parse or load type \"{formatted}\"");
        }

        /// <summary>
        /// Parses the provided type string.
        /// </summary>
        /// <param name="formatted">The formatted type name.</param>
        /// <param name="result">The result.</param>
        /// <returns><see langword="true"/> if the type was parsed and loaded; otherwise <see langword="false"/>.</returns>
        public bool TryParse(string formatted, [NotNullWhen(true)] out Type result)
        {
            if (ParseInternal(formatted, out result))
            {
                return true;
            }

            return false;
        }

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
                runtimeType = RuntimeTypeNameFormatter.Format(type);
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

        private bool ParseInternal(string formatted, out Type type)
        {
            var parsed = RuntimeTypeNameParser.Parse(formatted);
            var runtimeTypeSpec = RuntimeTypeNameRewriter.Rewrite(parsed, _convertFromDisplayName);
            var runtimeType = runtimeTypeSpec.Format();

            foreach (var converter in _converters)
            {
                if (converter.TryParse(runtimeType, out type))
                {
                    return true;
                }
            }

            return _resolver.TryResolveType(runtimeType, out type);
        }

        private bool IsTypeAllowed(in QualifiedType type)
        {
            if (_allowedTypes.TryGetValue(type, out var allowed))
            {
                return allowed;
            }

            if (_allowedTypesConfiguration.Contains(type.Type))
            {
                return true;
            }

            foreach (var filter in _filters)
            {
                var isAllowed = filter.IsTypeNameAllowed(type.Type, type.Assembly);
                if (isAllowed.HasValue)
                {
                    allowed = _allowedTypes[type] = isAllowed.Value;
                    return allowed;
                }
            }

            return false;
        }

        private QualifiedType ConvertToDisplayName(in QualifiedType input) => input switch
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
            (_, "System.Guid") => new QualifiedType(null, "Guid"),
            (_, "System.TimeSpan") => new QualifiedType(null, "TimeSpan"),
            (_, "System.DateTime") => new QualifiedType(null, "DateTime"),
            (_, "System.DateTimeOffset") => new QualifiedType(null, "DateTimeOffset"),
            (_, "System.Type") => new QualifiedType(null, "Type"),
            (_, "System.RuntimeType") => new QualifiedType(null, "Type"),
            _ when _wellKnownTypeToAlias.TryGetValue(input, out var alias) => alias,
            var value when IsTypeAllowed(in value) => input,
            var value => ThrowTypeNotAllowed(in value)
        };

        private QualifiedType ConvertFromDisplayName(in QualifiedType input) => input switch
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
            (_, "Guid") => new QualifiedType(null, "System.Guid"),
            (_, "TimeSpan") => new QualifiedType(null, "System.TimeSpan"),
            (_, "DateTime") => new QualifiedType(null, "System.DateTime"),
            (_, "DateTimeOffset") => new QualifiedType(null, "System.DateTimeOffset"),
            (_, "Type") => new QualifiedType(null, "System.Type"),
            _ when _wellKnownAliasToType.TryGetValue(input, out var type) => type,
            var value when IsTypeAllowed(in value) => input,
            var value => ThrowTypeNotAllowed(in value)
        };

        private static QualifiedType ThrowTypeNotAllowed(in QualifiedType value)

        {
            string message;

            if (!string.IsNullOrWhiteSpace(value.Assembly))
            {
                message = $"Type \"{value.Type}\" from assembly \"{value.Assembly}\" is not allowed. To allow it, add it to {nameof(TypeManifestOptions)}.{nameof(TypeManifestOptions.AllowedTypes)} or register an {nameof(ITypeFilter)} instance which allows it.";
            }
            else
            {
                message = $"Type \"{value.Type}\" is not allowed. To allow it, add it to {nameof(TypeManifestOptions)}.{nameof(TypeManifestOptions.AllowedTypes)} or register an {nameof(ITypeFilter)} instance which allows it.";
            }

            throw new InvalidOperationException(message);
        }
    }
}