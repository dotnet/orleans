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
using System.Runtime.CompilerServices;
using System.Text;

namespace Orleans.Serialization.TypeSystem
{
    /// <summary>
    /// Formats and parses <see cref="Type"/> instances using configured rules.
    /// </summary>
    public class TypeConverter
    {
        private readonly ITypeConverter[] _converters;
        private readonly ITypeNameFilter[] _typeNameFilters;
        private readonly ITypeFilter[] _typeFilters;
        private readonly bool _allowAllTypes;
        private readonly TypeResolver _resolver;
        private readonly RuntimeTypeNameRewriter.Rewriter<ValidationResult> _convertToDisplayName;
        private readonly RuntimeTypeNameRewriter.Rewriter<ValidationResult> _convertFromDisplayName;
        private readonly Dictionary<QualifiedType, QualifiedType> _wellKnownAliasToType;
        private readonly Dictionary<QualifiedType, QualifiedType> _wellKnownTypeToAlias;
        private readonly ConcurrentDictionary<QualifiedType, bool> _allowedTypes;
        private readonly HashSet<string> _allowedTypesConfiguration;
        private static readonly List<(string DisplayName, string RuntimeName)> WellKnownTypeAliases = new()
        {
            ("object", "System.Object"),
            ("string", "System.String"),
            ("char", "System.Char"),
            ("sbyte", "System.SByte"),
            ("byte", "System.Byte"),
            ("bool", "System.Boolean"),
            ("short", "System.Int16"),
            ("ushort", "System.UInt16"),
            ("int", "System.Int32"),
            ("uint", "System.UInt32"),
            ("long", "System.Int64"),
            ("ulong", "System.UInt64"),
            ("float", "System.Single"),
            ("double", "System.Double"),
            ("decimal", "System.Decimal"),
            ("Guid", "System.Guid"),
            ("TimeSpan", "System.TimeSpan"),
            ("DateTime", "System.DateTime"),
            ("DateTimeOffset", "System.DateTimeOffset"),
            ("Type", "System.Type"),
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="TypeConverter"/> class.
        /// </summary>
        /// <param name="formatters">The type name formatters.</param>
        /// <param name="typeNameFilters">The type name filters.</param>
        /// <param name="typeFilters">The type filters.</param>
        /// <param name="options">The options.</param>
        /// <param name="typeResolver">The type resolver.</param>
        public TypeConverter(
            IEnumerable<ITypeConverter> formatters,
            IEnumerable<ITypeNameFilter> typeNameFilters,
            IEnumerable<ITypeFilter> typeFilters,
            IOptions<TypeManifestOptions> options,
            TypeResolver typeResolver)
        {
            _resolver = typeResolver;
            _converters = formatters.ToArray();
            _typeNameFilters = typeNameFilters.ToArray();
            _typeFilters = typeFilters.ToArray();
            _allowAllTypes = options.Value.AllowAllTypes;
            _convertToDisplayName = ConvertToDisplayName;
            _convertFromDisplayName = ConvertFromDisplayName;

            _wellKnownAliasToType = new Dictionary<QualifiedType, QualifiedType>();
            _wellKnownTypeToAlias = new Dictionary<QualifiedType, QualifiedType>();

            _allowedTypes = new ConcurrentDictionary<QualifiedType, bool>(QualifiedType.EqualityComparer);
            _allowedTypesConfiguration = new(StringComparer.Ordinal);

            if (!_allowAllTypes)
            {
                foreach (var t in options.Value.AllowedTypes)
                {
                    _allowedTypesConfiguration.Add(t);
                }

                ConsumeMetadata(options.Value);
            }

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
            AddFromMetadata(metadata.Converters, typeof(IConverter<,>));
            foreach (var type in metadata.InterfaceProxies)
            {
                AddAllowedType(type switch
                {
                    { IsGenericType: true } => type.GetGenericTypeDefinition(),
                    _ => type
                });
            }

            void AddFromMetadata(IEnumerable<Type> metadataCollection, Type genericType)
            {
                Debug.Assert(genericType.GetGenericArguments().Length >= 1);

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

                        foreach (var genericArgument in @interface.GetGenericArguments())
                        {
                            InspectGenericArgument(genericArgument);
                        }
                    }
                }
            }

            void InspectGenericArgument(Type genericArgument)
            {
                if (typeof(object) == genericArgument)
                {
                    return;
                }

                if (genericArgument.IsConstructedGenericType && genericArgument.GenericTypeArguments.Any(arg => arg.IsGenericParameter))
                {
                    genericArgument = genericArgument.GetGenericTypeDefinition();
                }

                if (genericArgument.IsGenericParameter || genericArgument.IsArray)
                {
                    return;
                }

                AddAllowedType(genericArgument);
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
                var converter = this;
                _ = RuntimeTypeNameRewriter.Rewrite(parsed, AddQualifiedType, ref converter);
                static QualifiedType AddQualifiedType(in QualifiedType type, ref TypeConverter self)
                {
                    self._allowedTypes[type] = true;
                    return type;
                }
            }
        }

        /// <summary>
        /// Formats the provided type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="allowAllTypes">Whether all types are allowed or not.</param>
        /// <returns>The formatted type name.</returns>
        public string Format(Type type, bool allowAllTypes = false) => FormatInternal(type);

        /// <summary>
        /// Formats the provided type, rewriting elements using the provided delegate.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="rewriter">A delegate used to rewrite the type.</param>
        /// <param name="allowAllTypes">Whether all types are allowed or not.</param>
        /// <returns>The formatted type name.</returns>
        public string Format(Type type, Func<TypeSpec, TypeSpec> rewriter, bool allowAllTypes = false) => FormatInternal(type, rewriter);

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
            ValidationResult validationState = default;
            var displayTypeSpec = RuntimeTypeNameRewriter.Rewrite(runtimeTypeSpec, _convertToDisplayName, ref validationState);
            if (rewriter is not null)
            {
                displayTypeSpec = rewriter(displayTypeSpec);
            }

            var formatted = displayTypeSpec.Format();

            if (validationState.IsTypeNameAllowed == false)
            {
                ThrowTypeNotAllowed(formatted, validationState.ErrorTypes);
            }

            if (!_allowAllTypes && validationState.IsTypeNameAllowed != true)
            {
                if (InspectType(_typeFilters, type) == false)
                {
                    ThrowTypeNotAllowed(type);
                }
            }

            return formatted;
        }

        private bool ParseInternal(string formatted, out Type type)
        {
            var parsed = RuntimeTypeNameParser.Parse(formatted);
            ValidationResult validationState = default;
            var runtimeTypeSpec = RuntimeTypeNameRewriter.Rewrite(parsed, _convertFromDisplayName, ref validationState);
            var runtimeType = runtimeTypeSpec.Format();

            if (validationState.IsTypeNameAllowed == false)
            {
                ThrowTypeNotAllowed(formatted, validationState.ErrorTypes);
            }

            foreach (var converter in _converters)
            {
                if (converter.TryParse(runtimeType, out type))
                {
                    return true;
                }
            }

            if (_resolver.TryResolveType(runtimeType, out type))
            {
                if (!_allowAllTypes && validationState.IsTypeNameAllowed != true)
                {
                    if (InspectType(_typeFilters, type) == false)
                    {
                        ThrowTypeNotAllowed(type);
                    }
                }

                return true;
            }

            return false;
        }

        private bool? IsNameTypeAllowed(in QualifiedType type)
        {
            if (_allowAllTypes)
            {
                return true;
            }

            if (_allowedTypes.TryGetValue(type, out var allowed))
            {
                return allowed;
            }

            foreach (var (displayName, runtimeName) in WellKnownTypeAliases)
            {
                if (displayName.Equals(type.Type) || runtimeName.Equals(type.Type))
                {
                    return true;
                }
            }

            if (_allowedTypesConfiguration.Contains(type.Type))
            {
                return true;
            }

            foreach (var filter in _typeNameFilters)
            {
                var isAllowed = filter.IsTypeNameAllowed(type.Type, type.Assembly);
                if (isAllowed.HasValue)
                {
                    allowed = _allowedTypes[type] = isAllowed.Value;
                    return allowed;
                }
            }

            return null;
        }

        private QualifiedType ConvertToDisplayName(in QualifiedType input, ref ValidationResult state)
        {
            state = UpdateValidationResult(input, state);

            foreach (var (displayName, runtimeName) in WellKnownTypeAliases)
            {
                if (string.Equals(input.Type, runtimeName, StringComparison.OrdinalIgnoreCase))
                {
                    return new QualifiedType(null, displayName);
                }
            }

            if (_wellKnownTypeToAlias.TryGetValue(input, out var alias))
            {
                return alias;
            }

            return input;
        }

        private QualifiedType ConvertFromDisplayName(in QualifiedType input, ref ValidationResult state)
        {
            state = UpdateValidationResult(input, state);

            foreach (var (displayName, runtimeName) in WellKnownTypeAliases)
            {
                if (string.Equals(input.Type, displayName, StringComparison.OrdinalIgnoreCase))
                {
                    return new QualifiedType(null, runtimeName);
                }
            }

            if (_wellKnownAliasToType.TryGetValue(input, out var type))
            {
                return type;
            }

            return input;
        }

        private ValidationResult UpdateValidationResult(QualifiedType input, ValidationResult state)
        {
            // If there has not been an error yet, inspect this type to ensure it is allowed.
            if (IsNameTypeAllowed(input) is bool allowed)
            {
                var newAllowed = allowed && (state.IsTypeNameAllowed ?? true);
                var newErrorList = state.ErrorTypes ?? new List<QualifiedType>();
                if (!allowed)
                {
                    newErrorList.Add(input);
                }

                return new(newAllowed, newErrorList);
            }

            return state;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static QualifiedType ThrowTypeNotAllowed(string fullTypeName, List<QualifiedType> errors)
        {
            if (errors is { Count: 1 })
            {
                var value = errors[0];

                if (!string.IsNullOrWhiteSpace(value.Assembly))
                {
                    throw new InvalidOperationException($"Type \"{value.Type}\" from assembly \"{value.Assembly}\" is not allowed. To allow it, add it to {nameof(TypeManifestOptions)}.{nameof(TypeManifestOptions.AllowedTypes)} or register an {nameof(ITypeNameFilter)} instance which allows it.");
                }
                else
                {
                    throw new InvalidOperationException($"Type \"{value.Type}\" is not allowed. To allow it, add it to {nameof(TypeManifestOptions)}.{nameof(TypeManifestOptions.AllowedTypes)} or register an {nameof(ITypeNameFilter)} instance which allows it.");
                }
            }

            StringBuilder message = new($"Some types in the type string \"{fullTypeName}\" are not allowed by configuration. To allow them, add them to {nameof(TypeManifestOptions)}.{nameof(TypeManifestOptions.AllowedTypes)} or register an {nameof(ITypeNameFilter)} instance which allows them.");
            foreach (var value in errors)
            {
                if (!string.IsNullOrWhiteSpace(value.Assembly))
                {
                    message.AppendLine($"Type \"{value.Type}\" from assembly \"{value.Assembly}\"");
                }
                else
                {
                    message.AppendLine($"Type \"{value.Type}\"");
                }
            }

            throw new InvalidOperationException(message.ToString());
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowTypeNotAllowed(Type value)
        {
            var message = $"Type \"{value.FullName}\" is not allowed. To allow it, add it to {nameof(TypeManifestOptions)}.{nameof(TypeManifestOptions.AllowedTypes)} or register an {nameof(ITypeNameFilter)} or {nameof(ITypeFilter)} instance which allows it.";
            throw new InvalidOperationException(message);
        }

        private readonly struct ValidationResult
        {
            public ValidationResult(bool? isTypeNameAllowed, List<QualifiedType> errorTypes)
            {
                IsTypeNameAllowed = isTypeNameAllowed;
                ErrorTypes = errorTypes;
            }

            public bool? IsTypeNameAllowed { get; }
            public List<QualifiedType> ErrorTypes { get; }
        }

        private static bool? InspectType(ITypeFilter[] filters, Type type)
        {
            bool? result = null;
            if (type.HasElementType)
            {
                result = Combine(result, InspectType(filters, type.GetElementType()));
                return result;
            }

            foreach (var filter in filters)
            {
                result = Combine(result, filter.IsTypeAllowed(type));
                if (result == false)
                {
                    return false;
                }
            }

            if (type.IsConstructedGenericType)
            {
                foreach (var parameter in type.GenericTypeArguments)
                {
                    result = Combine(result, InspectType(filters, parameter));
                    if (result == false)
                    {
                        return false;
                    }
                }
            }

            return result;

            static bool? Combine(bool? left, bool? right)
            {
                if (left == false || right == false)
                {
                    return false;
                }
                else if (left == true || right == true)
                {
                    return true;
                }

                return null;
            }
        }
    }
}