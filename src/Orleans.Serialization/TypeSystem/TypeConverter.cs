using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;

namespace Orleans.Serialization.TypeSystem;

/// <summary>
/// Formats and parses <see cref="Type"/> instances using configured rules.
/// </summary>
public class TypeConverter
{
    private readonly ITypeConverter[] _converters;
    private readonly ITypeNameFilter[] _typeNameFilters;
    private readonly ITypeFilter[] _typeFilters;
    private readonly bool _allowAllTypes;
    private readonly CompoundTypeAliasTree _compoundTypeAliases;
    private readonly TypeResolver _resolver;
    private readonly RuntimeTypeNameRewriter.Rewriter<ValidationResult> _convertToDisplayName;
    private readonly RuntimeTypeNameRewriter.Rewriter<ValidationResult> _convertFromDisplayName;
    private readonly RuntimeTypeNameRewriter.CompoundAliasResolver<ValidationResult> _compoundAliasResolver;
    private readonly Dictionary<QualifiedType, QualifiedType> _wellKnownAliasToType;
    private readonly Dictionary<QualifiedType, QualifiedType> _wellKnownTypeToAlias;
    private readonly ConcurrentDictionary<QualifiedType, bool> _allowedTypes;
    private readonly HashSet<string> _allowedAssembliesConfiguration;
    private readonly HashSet<string> _allowedTypesConfiguration;
    private static readonly List<(string DisplayName, string RuntimeName)> WellKnownTypeAliases =
    [
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
    ];
    private static readonly HashSet<string> WellKnownRuntimeTypeNames =
        WellKnownTypeAliases.Select(static alias => alias.RuntimeName).ToHashSet(StringComparer.Ordinal);

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
        _compoundTypeAliases = options.Value.CompoundTypeAliases;
        _convertToDisplayName = ConvertToDisplayName;
        _convertFromDisplayName = ConvertFromDisplayName;
        _compoundAliasResolver = ResolveCompoundAliasType;

        _wellKnownAliasToType = [];
        _wellKnownTypeToAlias = [];

        _allowedTypes = new ConcurrentDictionary<QualifiedType, bool>(QualifiedType.EqualityComparer);
        _allowedAssembliesConfiguration = new(StringComparer.Ordinal);
        _allowedTypesConfiguration = new(StringComparer.Ordinal);

        if (!_allowAllTypes)
        {
            foreach (var assembly in options.Value.AllowedAssemblies)
            {
                _allowedAssembliesConfiguration.Add(assembly);
            }

            foreach (var t in options.Value.AllowedTypes)
            {
                AddConfiguredAllowedType(t);
            }

            ConsumeMetadata(options.Value);
        }

        var aliases = options.Value.WellKnownTypeAliases;
        foreach (var item in aliases)
        {
            var alias = new QualifiedType(null, item.Key);
            var spec = RuntimeTypeNameParser.Parse(RuntimeTypeNameFormatter.Format(item.Value));
            string? asmName = null;
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
            if (!_allowAllTypes)
            {
                _allowedTypes[originalQualifiedType] = true;
                if (asmName is { Length: > 0 })
                {
                    _allowedTypes[new QualifiedType(null, spec.Format())] = true;
                }
            }
        }
    }

    private void AddConfiguredAllowedType(string typeName)
    {
        _allowedTypesConfiguration.Add(typeName);

        var parsed = RuntimeTypeNameParser.Parse(typeName);
        var converter = this;
        _ = RuntimeTypeNameRewriter.Rewrite(parsed, static (in QualifiedType type, ref TypeConverter converter) =>
        {
            converter._allowedTypes[type] = true;
            return type;
        }, ref converter);
    }

    private void ConsumeMetadata(TypeManifestOptions metadata)
    {
        AddFromMetadata(metadata.SerializerTypes, typeof(IBaseCodec<>));
        AddFromMetadata(metadata.SerializerTypes, typeof(IValueSerializer<>));
        AddFromMetadata(metadata.SerializerTypes, typeof(IFieldCodec<>));
        AddFromMetadata(metadata.FieldCodecTypes, typeof(IFieldCodec<>));
        AddFromMetadata(metadata.ActivatorTypes, typeof(IActivator<>));
        AddFromMetadata(metadata.CopierTypes, typeof(IDeepCopier<>));
        AddFromMetadata(metadata.ConverterTypes, typeof(IConverter<,>));
        foreach (var type in metadata.InterfaceProxyTypes)
        {
            AddAllowedType(type switch
            {
                { IsGenericType: true } => type.GetGenericTypeDefinition(),
                _ => type
            });
        }

#if NET5_0_OR_GREATER
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2075",
            Justification = "Generated manifests and trim-safe manual configuration use the annotated TypeManifestOptions implementation registration methods, which preserve implemented interfaces. The HashSet<Type> boundary cannot retain those annotations.")]
#endif
        void AddFromMetadata(HashSet<Type> metadataCollection, Type genericType)
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

            if (genericArgument.IsConstructedGenericType && Array.Exists(genericArgument.GenericTypeArguments, arg => arg.IsGenericParameter))
            {
                genericArgument = genericArgument.GetGenericTypeDefinition();
            }

            if (genericArgument.IsGenericParameter || genericArgument.IsArray)
            {
                return;
            }

            AddAllowedType(genericArgument);
        }

#if NET5_0_OR_GREATER
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2070",
            Justification = "Generated manifests register proxy metadata through TypeManifestOptions.AddInterfaceProxy, which preserves implemented interfaces. The intermediate Type values cannot retain that annotation.")]
#endif
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
            _ = RuntimeTypeNameRewriter.Rewrite(parsed, AddQualifiedType, ResolveCompoundAliasType, ref converter);
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
        return ParseInternal(formatted, out result);
    }

    private string FormatInternal(Type type, Func<TypeSpec, TypeSpec>? rewriter = null)
    {
        string? runtimeType = null;
        foreach (var converter in _converters)
        {
            if (converter.TryFormat(type, out var value))
            {
                runtimeType = value;
                break;
            }
        }

        runtimeType = string.IsNullOrWhiteSpace(runtimeType) ? RuntimeTypeNameFormatter.Format(type) : runtimeType;

        var runtimeTypeSpec = RuntimeTypeNameParser.Parse(runtimeType);
        ValidationResult validationState = default;
        var displayTypeSpec = RuntimeTypeNameRewriter.Rewrite(runtimeTypeSpec, _convertToDisplayName, compoundAliasRewriter: null, ref validationState);
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
            if (!InspectType(type))
            {
                ThrowTypeNotAllowed(type);
            }
        }

        return formatted;
    }

    private bool ParseInternal(string formatted, out Type type)
    {
        var parsed = RuntimeTypeNameParser.Parse(formatted);
        return ParseInternal(parsed, out type);
    }

    private bool ParseInternal(TypeSpec parsed, out Type type)
    {
        ValidationResult validationState = default;
        var runtimeTypeSpec = RuntimeTypeNameRewriter.Rewrite(parsed, _convertFromDisplayName, _compoundAliasResolver, ref validationState);
        var runtimeType = runtimeTypeSpec.Format();

        if (validationState.IsTypeNameAllowed == false)
        {
            ThrowTypeNotAllowed(parsed.Format(), validationState.ErrorTypes);
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
                if (!InspectType(type))
                {
                    ThrowTypeNotAllowed(type);
                }
            }

            return true;
        }

        return false;
    }

    private bool? IsNamedTypeAllowed(in QualifiedType type)
    {
        if (_allowAllTypes)
        {
            return true;
        }

        if (_allowedTypes.TryGetValue(type, out var allowed))
        {
            return allowed;
        }

        var filterResult = InspectTypeNameFilters(type);
        if (filterResult == false)
        {
            return false;
        }

        foreach (var (displayName, runtimeName) in WellKnownTypeAliases)
        {
            if (displayName.Equals(type.Type, StringComparison.Ordinal) || runtimeName.Equals(type.Type, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (_allowedTypesConfiguration.Contains(type.Type))
        {
            return true;
        }

        if (filterResult == true)
        {
            return _allowedTypes[type] = true;
        }

        if (_wellKnownAliasToType.TryGetValue(type, out var runtimeType))
        {
            return IsNamedTypeAllowed(runtimeType);
        }

        return null;
    }

    private bool? InspectTypeNameFilters(in QualifiedType type)
    {
        bool? result = null;
        foreach (var filter in _typeNameFilters)
        {
            var isAllowed = filter.IsTypeNameAllowed(type.Type, type.Assembly ?? string.Empty);
            if (isAllowed == false)
            {
                _allowedTypes[type] = false;
                return false;
            }

            if (isAllowed == true)
            {
                result = true;
            }
        }

        return result;
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
        switch (IsNamedTypeAllowed(input))
        {
            case true:
                return new(true, state.HasUnknownTypeNames, state.ErrorTypes);
            case false:
                var newErrorList = state.ErrorTypes;
                newErrorList.Add(input);
                return new(state.HasAllowedTypeNames, state.HasUnknownTypeNames, newErrorList);
            default:
                return new(state.HasAllowedTypeNames, true, state.ErrorTypes);
        }
    }

    [DoesNotReturn]
    private static void ThrowTypeNotAllowed(string fullTypeName, List<QualifiedType> errors)
    {
        const string allowListMessage = $"A registered {nameof(ITypeNameFilter)} denied it. Update that filter, or set {nameof(TypeManifestOptions)}.{nameof(TypeManifestOptions.AllowAllTypes)} to true to bypass type-name validation. Allowing all types is insecure when serialized input can be influenced by an untrusted party.";
        if (errors is { Count: 1 })
        {
            var value = errors[0];

            if (!string.IsNullOrWhiteSpace(value.Assembly))
            {
                throw new InvalidOperationException($"Type \"{value.Type}\" from assembly \"{value.Assembly}\" is not allowed. {allowListMessage}");
            }
            else
            {
                throw new InvalidOperationException($"Type \"{value.Type}\" is not allowed. {allowListMessage}");
            }
        }

        StringBuilder message = new($"Some types in the type string \"{fullTypeName}\" are not allowed by configuration. {allowListMessage}");
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
    private static void ThrowTypeNotAllowed(Type value)
    {
        var message = $"Type \"{value.FullName}\" is not allowed. To allow it, call {nameof(TypeManifestOptions)}.{nameof(TypeManifestOptions.AddAllowedType)}, call {nameof(TypeManifestOptions)}.{nameof(TypeManifestOptions.AddAllowedAssembly)}, add its Orleans-formatted name to {nameof(TypeManifestOptions)}.{nameof(TypeManifestOptions.AllowedTypes)}, register an {nameof(ITypeNameFilter)} or {nameof(ITypeFilter)} instance which allows it, or set {nameof(TypeManifestOptions)}.{nameof(TypeManifestOptions.AllowAllTypes)} to true. Allowing all types is insecure when serialized input can be influenced by an untrusted party.";
        throw new InvalidOperationException(message);
    }

    private readonly struct ValidationResult(bool hasAllowedTypeNames, bool hasUnknownTypeNames, List<QualifiedType>? errorTypes)
    {
        private readonly List<QualifiedType>? _errorTypes = errorTypes;

        public bool HasAllowedTypeNames { get; } = hasAllowedTypeNames;
        public bool HasUnknownTypeNames { get; } = hasUnknownTypeNames;
        public List<QualifiedType> ErrorTypes => _errorTypes ?? [];

        public bool? IsTypeNameAllowed =>
            ErrorTypes is { Count: > 0 }
                ? false
                : HasAllowedTypeNames && !HasUnknownTypeNames
                    ? true
                    : null;
    }

    private bool InspectType(Type type) => InspectTypeCore(type) == true;

    private bool? InspectTypeCore(Type type)
    {
        bool? result = null;
        if (type.HasElementType)
        {
            result = Combine(result, InspectTypeCore(type.GetElementType()!));
            return result;
        }

        result = Combine(result, IsTypeAllowedByConfiguration(type));
        var isAllowedByTypeFilter = false;

        foreach (var filter in _typeFilters)
        {
            var filterResult = filter.IsTypeAllowed(type);
            if (filterResult == true)
            {
                isAllowedByTypeFilter = true;
            }

            result = Combine(result, filterResult);
            if (result == false)
            {
                return false;
            }
        }

        // Enums and well-known types carry no user-defined behavior, so allow them absent an explicit opinion.
        if (result is null && (type.IsEnum || type.FullName is { } fullName && WellKnownRuntimeTypeNames.Contains(fullName)))
        {
            result = true;
        }

        if (type.IsConstructedGenericType)
        {
            var genericArgumentsResult = InspectGenericArguments(type);
            if (genericArgumentsResult == false)
            {
                return false;
            }

            if (isAllowedByTypeFilter)
            {
                return true;
            }

            if (genericArgumentsResult != true)
            {
                return genericArgumentsResult;
            }

            result = Combine(result, genericArgumentsResult);
        }

        return result;
    }

    private bool? InspectGenericArguments(Type type)
    {
        foreach (var parameter in type.GenericTypeArguments)
        {
            var result = InspectTypeCore(parameter);
            if (result != true)
            {
                return result;
            }
        }

        return true;
    }

    private bool? IsTypeAllowedByConfiguration(Type type)
    {
        return IsAssemblyAllowed(CachedTypeResolver.GetName(type.Assembly)) || IsAssemblyAllowed(type.Assembly.FullName) ? true : null;
    }

    private bool IsAssemblyAllowed(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            return false;
        }

        if (_allowedAssembliesConfiguration.Contains(assemblyName))
        {
            return true;
        }

        var simpleNameEnd = assemblyName.IndexOf(',');
        return simpleNameEnd > 0 && _allowedAssembliesConfiguration.Contains(assemblyName[..simpleNameEnd].Trim());
    }

    private static bool? Combine(bool? left, bool? right)
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

    private TypeSpec ResolveCompoundAliasType<TState>(TupleTypeSpec input, ref TState state)
    {
        var resolvedElements = new object[input.Elements.Length];
        for (var i = 0; i < input.Elements.Length; i++)
        {
            var inputElement = input.Elements[i];
            if (inputElement is LiteralTypeSpec literal)
            {
                resolvedElements[i] = literal.Value;
            }
            else
            {
                if (!ParseInternal(inputElement, out var type))
                {
                    throw new TypeLoadException($"Unable to parse or load type \"{inputElement.Format()}\".");
                }

                resolvedElements[i] = type;
            }
        }

        var tree = _compoundTypeAliases;
        foreach (var element in resolvedElements)
        {
            tree = tree?.GetChildOrDefault(element);
            if (tree is null) break;
        }

        var resultType = tree?.Value;
        if (resultType is null)
        {
            throw new TypeLoadException($"Unable to resolve type alias \"{input.Format()}\".");
        }

        var formatted = RuntimeTypeNameFormatter.FormatInternalNoCache(resultType, allowAliases: false);
        var parsed = RuntimeTypeNameParser.Parse(formatted);
        return parsed;
    }
}
