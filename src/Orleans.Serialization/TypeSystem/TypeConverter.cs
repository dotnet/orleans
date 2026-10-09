using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
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
    private readonly Dictionary<QualifiedType, Type> _wireKnownTypes = new(QualifiedType.EqualityComparer);
    private readonly HashSet<Type> _wireKnownIdentities = [];
    private readonly HashSet<QualifiedType> _wireAmbiguousNames = new(QualifiedType.EqualityComparer);
    private readonly Dictionary<string, Type> _wireAliases;
    private readonly HashSet<Type> _wireGrants = [];
    private readonly HashSet<Type> _wireExplicitTypes = [];
    private readonly Dictionary<Type, List<Type>> _wireClosedGenericTypes = [];
    private readonly Dictionary<(Type Element, int Rank), Type> _wireArrayTypes = [];
    private readonly ConcurrentDictionary<QualifiedType, bool?> _wireNameOpinions = new(QualifiedType.EqualityComparer);
    private readonly ConcurrentDictionary<Type, bool?> _wireTypeOpinions = new();
    private readonly HashSet<Type> _admittedExceptionTypes;
    private readonly HashSet<Assembly> _wireAllowedAssemblies;
    private static readonly List<(string DisplayName, Type Type)> WellKnownTypeAliases =
    [
        ("object", typeof(object)),
        ("string", typeof(string)),
        ("char", typeof(char)),
        ("sbyte", typeof(sbyte)),
        ("byte", typeof(byte)),
        ("bool", typeof(bool)),
        ("short", typeof(short)),
        ("ushort", typeof(ushort)),
        ("int", typeof(int)),
        ("uint", typeof(uint)),
        ("long", typeof(long)),
        ("ulong", typeof(ulong)),
        ("float", typeof(float)),
        ("double", typeof(double)),
        ("decimal", typeof(decimal)),
        ("Guid", typeof(Guid)),
        ("TimeSpan", typeof(TimeSpan)),
        ("DateTime", typeof(DateTime)),
        ("DateTimeOffset", typeof(DateTimeOffset)),
        ("Type", typeof(Type)),
    ];
    private static readonly HashSet<string> WellKnownRuntimeTypeNames =
        WellKnownTypeAliases.Select(static alias => alias.Type.FullName!).ToHashSet(StringComparer.Ordinal);

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
        _admittedExceptionTypes = options.Value.AdmittedExceptionTypes;
        _wireAllowedAssemblies = new(options.Value.AllowedAssemblyIdentities);
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
        }

        ConsumeMetadata(options.Value);
        var aliases = options.Value.WellKnownTypeAliases;
        _wireAliases = new(aliases, StringComparer.Ordinal);
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

        foreach (var (_, type) in WellKnownTypeAliases)
        {
            RegisterWireType(type, allowed: true);
        }

        foreach (var type in options.Value.WellKnownTypeIds.Values) RegisterWireType(type, allowed: true);
        foreach (var type in aliases.Values) RegisterWireType(type, allowed: false);
        foreach (var type in _compoundTypeAliases.GetTypes()) RegisterWireType(type, allowed: false);
        foreach (var type in options.Value.ContextTypes) RegisterWireType(type, allowed: true);
        foreach (var type in options.Value.InterfaceTypes) RegisterWireType(type, allowed: true);
        foreach (var type in options.Value.AllowedTypeIdentities) RegisterWireType(type, allowed: true, explicitlyAllowed: true);
        foreach (var type in options.Value.AllowedAssemblyTypes) RegisterWireType(type, allowed: true);

        // Resolve host-provided string registrations once, before accepting wire input.
        var identityNames = options.Value.AllowedTypeIdentities
            .Select(static type => RuntimeTypeNameFormatter.FormatInternalNoCache(type, allowAliases: false))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var configured in options.Value.AllowedTypes)
        {
            if (!identityNames.Contains(configured) && _resolver.TryResolveType(configured, out var type))
            {
                RegisterWireType(type, allowed: true, explicitlyAllowed: true);
            }
        }

        foreach (var configured in options.Value.AllowedAssemblies)
        {
            var assemblies = _wireKnownTypes.Values.Select(static type => type.Assembly).Distinct()
                .Where(assembly => assembly.FullName == configured || CachedTypeResolver.GetName(assembly) == configured)
                .ToArray();
            if (assemblies is [var assembly]) _wireAllowedAssemblies.Add(assembly);
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
        foreach (var type in metadata.SerializerTypes) RegisterWireType(type, allowed: true);
        foreach (var type in metadata.FieldCodecTypes) RegisterWireType(type, allowed: true);
        foreach (var type in metadata.CodecFactories.Keys)
        {
            AddAllowedType(type);
        }

        AddFromMetadata(metadata.SerializerTypes, metadata.SerializerContracts, typeof(IBaseCodec<>));
        AddFromMetadata(metadata.SerializerTypes, metadata.SerializerContracts, typeof(IValueSerializer<>));
        AddFromMetadata(metadata.SerializerTypes, metadata.SerializerContracts, typeof(IFieldCodec<>));
        AddFromMetadata(metadata.FieldCodecTypes, metadata.SerializerContracts, typeof(IFieldCodec<>));
        AddFromMetadata(metadata.ActivatorTypes, metadata.ActivatorContracts, typeof(IActivator<>));
        AddFromMetadata(metadata.CopierTypes, metadata.CopierContracts, typeof(IDeepCopier<>));
        AddFromMetadata(metadata.CopierTypes, metadata.CopierContracts, typeof(IBaseCopier<>));
        AddFromMetadata(metadata.ConverterTypes, metadata.ConverterContracts, typeof(IConverter<,>));
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
            Justification = "Legacy implementation-only registrations preserve implemented interfaces through annotated TypeManifestOptions methods. Explicit contract registrations are consumed directly. The HashSet<Type> boundary cannot retain the legacy annotations.")]
#endif
        void AddFromMetadata(
            HashSet<Type> metadataCollection,
            Dictionary<Type, List<SerializationContract>> contracts,
            Type genericType)
        {
            Debug.Assert(genericType.GetGenericArguments().Length >= 1);

            foreach (var type in metadataCollection)
            {
                if (contracts.TryGetValue(type, out var registrations))
                {
                    foreach (var registration in registrations)
                    {
                        if (registration.ContractType == genericType)
                        {
                            if (registration.TargetDescription is { } targetDescription)
                            {
                                foreach (var referenced in targetDescription.GetReferencedTypes())
                                {
                                    InspectGenericArgument(referenced);
                                }
                            }
                            else
                            {
                                InspectGenericArgument(registration.TargetType!);
                            }
                            if (registration.SurrogateType is { } surrogateType)
                            {
                                InspectGenericArgument(surrogateType);
                            }
                            if (registration.SurrogateDescription is { } description)
                            {
                                foreach (var referencedType in description.GetReferencedTypes())
                                {
                                    InspectGenericArgument(referencedType);
                                }
                            }
                        }
                    }

                    if (!metadata.DiscoverInterfaces(type, genericType))
                    {
                        continue;
                    }
                }

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

            if (genericArgument.IsArray)
            {
                FormatAndAddAllowedType(genericArgument);
                return;
            }

            if (genericArgument.IsGenericParameter)
            {
                return;
            }

            AddAllowedType(genericArgument);
        }

#if NET5_0_OR_GREATER
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2070",
            Justification = "Explicit contract APIs and SerializationType.Create preserve target interface metadata, and AddInterfaceProxy preserves proxy interfaces. The manifest collections and intermediate Type values cannot retain these annotations.")]
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
            RegisterWireType(type, allowed: true);
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

    internal Type ParseForDeserialization(string formatted)
        => TryParseForDeserialization(formatted, out var type)
            ? type
            : throw new TypeLoadException($"Wire type \"{formatted}\" is unavailable. Register its Type identity in the serializer manifest.");

    internal bool TryParseForDeserialization(string formatted, [NotNullWhen(true)] out Type result, bool allowAllTypes = false)
    {
        var parsed = RuntimeTypeNameParser.ParseForDeserialization(formatted);
        var state = (Converter: this, AllowAll: allowAllTypes);
        _ = RuntimeTypeNameRewriter.Rewrite(parsed, static (in QualifiedType name, ref (TypeConverter Converter, bool AllowAll) state) =>
        {
            state.Converter.CheckWireName(name, state.AllowAll);
            return name;
        }, ref state);
        result = BindWireType(parsed, assembly: null, allowAllTypes)!;
        return result is not null;
    }

    internal bool IsExceptionTypeAdmitted(Type type) => _admittedExceptionTypes.Contains(type);

    internal void AuthorizeForDeserialization(Type type) => AuthorizeWireType(type);

    private void RegisterWireType(Type type, bool allowed, bool explicitlyAllowed = false)
    {
        if (type.IsGenericParameter) return;
        if (type.IsGenericType && type.ContainsGenericParameters && !type.IsGenericTypeDefinition)
        {
            type = type.GetGenericTypeDefinition();
        }

        if (allowed) _wireGrants.Add(type);
        _wireKnownIdentities.Add(type);
        if (explicitlyAllowed) _wireExplicitTypes.Add(type);
        if (type.HasElementType)
        {
            if (type.IsArray && (type.IsSZArray || type.GetArrayRank() > 1))
            {
                _wireArrayTypes[(type.GetElementType()!, type.GetArrayRank())] = type;
            }

            RegisterWireType(type.GetElementType()!, allowed, explicitlyAllowed);
            return;
        }

        if (type.IsConstructedGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (!_wireClosedGenericTypes.TryGetValue(definition, out var closedTypes))
            {
                _wireClosedGenericTypes[definition] = closedTypes = [];
            }

            if (!closedTypes.Contains(type)) closedTypes.Add(type);
            RegisterWireType(definition, allowed, explicitlyAllowed);
            foreach (var argument in type.GenericTypeArguments) RegisterWireType(argument, allowed, explicitlyAllowed);
            return;
        }

        if (type.FullName is not { } name) return;
        Add(new QualifiedType(null, name));
        Add(new QualifiedType(CachedTypeResolver.GetName(type.Assembly), name));
        Add(new QualifiedType(type.Assembly.FullName, name));
        if (type.DeclaringType is { } declaring) RegisterWireType(declaring, allowed, explicitlyAllowed);

        void Add(QualifiedType key)
        {
            if (_wireAmbiguousNames.Contains(key)) return;
            if (_wireKnownTypes.TryGetValue(key, out var existing) && existing != type)
            {
                _wireKnownTypes.Remove(key);
                _wireAmbiguousNames.Add(key);
                return;
            }

            _wireKnownTypes[key] = type;
        }
    }

    private Type? FindWireType(in QualifiedType name)
    {
        foreach (var (displayName, type) in WellKnownTypeAliases)
        {
            if (name.Assembly is null && (string.Equals(name.Type, displayName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name.Type, type.FullName, StringComparison.Ordinal)))
            {
                return type;
            }
        }

        if (_wireKnownTypes.TryGetValue(name, out var known)) return known;
        if (name.Assembly is null && _wireAliases.TryGetValue(name.Type, out known)) return known;
        return null;
    }

    private void CheckWireName(in QualifiedType name, bool allowAllTypes = false, bool knownIdentity = false)
    {
        if (_allowAllTypes || allowAllTypes || FindWireType(name) is { } type && _wireExplicitTypes.Contains(type)) return;
        if (GetWireNameOpinion(name, knownIdentity) == false)
        {
            throw new InvalidOperationException($"Wire type \"{name.Type}\" from assembly \"{name.Assembly}\" is denied by {nameof(ITypeNameFilter)}.");
        }
    }

    private bool? GetWireNameOpinion(QualifiedType name, bool knownIdentity = false)
        => knownIdentity || FindWireType(name) is not null
            ? _wireNameOpinions.GetOrAdd(name, InspectWireNameFilters)
            : InspectWireNameFilters(name);

    private bool? InspectWireNameFilters(QualifiedType key)
    {
        bool? result = null;
        foreach (var filter in _typeNameFilters)
        {
            var opinion = filter.IsTypeNameAllowed(key.Type, key.Assembly ?? string.Empty);
            if (opinion == false) return false;
            if (opinion == true && filter is not DefaultTypeFilter) result = true;
        }

        return result;
    }

    private Type? BindWireType(TypeSpec spec, string? assembly, bool allowAllTypes = false)
    {
        Type? result;
        switch (spec)
        {
            case AssemblyQualifiedTypeSpec qualified:
                return BindWireType(qualified.Type, qualified.Assembly!.Trim(), allowAllTypes);
            case NamedTypeSpec named:
                result = FindWireType(new QualifiedType(assembly, named.GetNamespaceQualifiedName()));
                break;
            case ConstructedGenericTypeSpec generic:
                var definition = BindWireType(generic.UnconstructedType, assembly, allowAllTypes);
                if (definition is null) return null;
                if (!definition.IsGenericTypeDefinition || definition.GetGenericArguments().Length != generic.Arguments.Length)
                {
                    throw new FormatException($"Type \"{generic.Format()}\" has an invalid generic definition.");
                }

                var arguments = new Type[generic.Arguments.Length];
                for (var i = 0; i < arguments.Length; i++)
                {
                    if (BindWireType(generic.Arguments[i], assembly: null, allowAllTypes) is not { } argument) return null;
                    arguments[i] = argument;
                }

                result = _wireClosedGenericTypes.TryGetValue(definition, out var closedTypes)
                    ? closedTypes.Find(candidate => candidate.GenericTypeArguments.SequenceEqual(arguments))
                    : null;
                result ??= definition.MakeGenericType(arguments);
                break;
            case ArrayTypeSpec array:
                if (BindWireType(array.ElementType, assembly, allowAllTypes) is not { } element) return null;
                result = _wireArrayTypes.TryGetValue((element, array.Dimensions), out var registeredArray)
                    ? registeredArray
                    : array.Dimensions == 1 ? element.MakeArrayType() : element.MakeArrayType(array.Dimensions);
                break;
            case PointerTypeSpec pointer:
                if (BindWireType(pointer.ElementType, assembly, allowAllTypes) is not { } pointed) return null;
                result = pointed.MakePointerType();
                break;
            case ReferenceTypeSpec reference:
                if (BindWireType(reference.ElementType, assembly, allowAllTypes) is not { } referenced) return null;
                result = referenced.MakeByRefType();
                break;
            case TupleTypeSpec alias:
                var tree = _compoundTypeAliases;
                foreach (var component in alias.Elements)
                {
                    object key;
                    if (component is LiteralTypeSpec literal) key = literal.Value;
                    else if (BindWireType(component, assembly: null, allowAllTypes) is { } componentType) key = componentType;
                    else return null;
                    tree = tree?.GetChildOrDefault(key);
                }

                result = tree?.Value;
                break;
            default:
                throw new FormatException($"Invalid wire type specification \"{spec.Format()}\".");
        }

        if (result is not null) AuthorizeWireType(result, allowAllTypes);
        return result;
    }

    private void AuthorizeWireType(Type type, bool allowAllTypes = false)
    {
        if (type.HasElementType)
        {
            AuthorizeWireType(type.GetElementType()!, allowAllTypes);
            return;
        }

        if (type.IsConstructedGenericType)
        {
            AuthorizeWireType(type.GetGenericTypeDefinition(), allowAllTypes);
            foreach (var argument in type.GenericTypeArguments) AuthorizeWireType(argument, allowAllTypes);
        }
        else if (!_wireKnownIdentities.Contains(type))
        {
            throw new TypeLoadException($"Wire type \"{type}\" has no host-registered identity.");
        }

        if (_allowAllTypes || allowAllTypes || _wireExplicitTypes.Contains(type)) return;
        var name = new QualifiedType(CachedTypeResolver.GetName(type.Assembly), type.FullName!);
        CheckWireName(name, knownIdentity: true);
        var allowed = type.IsConstructedGenericType || _wireGrants.Contains(type) || type.IsEnum
            || _wireAllowedAssemblies.Contains(type.Assembly) || GetWireNameOpinion(name, knownIdentity: true) == true;
        var typeOpinion = _wireTypeOpinions.GetOrAdd(type, candidate =>
        {
            bool? result = null;
            foreach (var filter in _typeFilters)
            {
                var opinion = filter.IsTypeAllowed(candidate);
                if (opinion == false) return false;
                if (opinion == true) result = true;
            }

            return result;
        });
        if (typeOpinion == false) throw new InvalidOperationException($"Wire type \"{type}\" is denied by {nameof(ITypeFilter)}.");
        if (typeOpinion == true) allowed = true;
        if (!allowed) throw new InvalidOperationException($"Wire type \"{type}\" is not authorized. Register its Type identity using {nameof(TypeManifestOptions.AddAllowedType)}.");
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

        foreach (var (displayName, knownType) in WellKnownTypeAliases)
        {
            if (displayName.Equals(type.Type, StringComparison.Ordinal) || knownType.FullName!.Equals(type.Type, StringComparison.Ordinal))
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

        foreach (var (displayName, type) in WellKnownTypeAliases)
        {
            if (string.Equals(input.Type, type.FullName, StringComparison.OrdinalIgnoreCase))
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

        foreach (var (displayName, knownType) in WellKnownTypeAliases)
        {
            if (string.Equals(input.Type, displayName, StringComparison.OrdinalIgnoreCase))
            {
                return new QualifiedType(null, knownType.FullName!);
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
        if (!type.IsConstructedGenericType && type.FullName is not null)
        {
            var typeSpec = RuntimeTypeNameParser.Parse(RuntimeTypeNameFormatter.Format(type));
            var qualifiedType = typeSpec is AssemblyQualifiedTypeSpec qualified
                ? new QualifiedType(qualified.Assembly, qualified.Type.Format())
                : new QualifiedType(null, typeSpec.Format());
            result = Combine(result, IsNamedTypeAllowed(qualifiedType));
        }

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

            result = Combine(result, InspectTypeCore(type.GetGenericTypeDefinition()));
        }

        return result;
    }

    private bool? InspectGenericArguments(Type type)
    {
        bool? result = true;
        foreach (var parameter in type.GenericTypeArguments)
        {
            var parameterResult = InspectTypeCore(parameter);
            if (parameterResult == false)
            {
                return false;
            }

            if (parameterResult is null)
            {
                result = null;
            }
        }

        return result;
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
