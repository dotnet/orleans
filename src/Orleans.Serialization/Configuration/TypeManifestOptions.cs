using System;
using System.Collections.Generic;
#if NET5_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
#endif
using System.Reflection;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Serialization.Configuration
{
    /// <summary>
    /// Configuration of all types which are known to the code generator.
    /// </summary>
    public sealed class TypeManifestOptions
    {
#if NET5_0_OR_GREATER
        private const DynamicallyAccessedMemberTypes ImplementationTypeMembers =
            DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces;

        private const DynamicallyAccessedMemberTypes InterfaceTypeMembers =
            DynamicallyAccessedMemberTypes.PublicMethods
            | DynamicallyAccessedMemberTypes.NonPublicMethods
            | DynamicallyAccessedMemberTypes.Interfaces;
#endif

        private readonly HashSet<Type> _activators = new();
        private readonly HashSet<Type> _fieldCodecs = new();
        private readonly HashSet<Type> _serializers = new();
        private readonly HashSet<Type> _copiers = new();
        private readonly HashSet<Type> _converters = new();
        private readonly HashSet<Type> _interfaces = new();
        private readonly HashSet<Type> _interfaceProxies = new();
        private readonly HashSet<Type> _interfaceImplementations = new();
        private readonly HashSet<Type> _legacySerializers = new();
        private readonly HashSet<Type> _legacyCopiers = new();
        private readonly HashSet<Type> _legacyActivators = new();
        private readonly HashSet<Type> _legacyConverters = new();
        // Mutable collection access requests legacy discovery, including types added through retained collection references.
        private bool _serializerCollectionAccessed;
        private bool _copierCollectionAccessed;
        private bool _activatorCollectionAccessed;
        private bool _converterCollectionAccessed;
        internal Dictionary<Type, List<SerializationContract>> SerializerContracts { get; } = new();
        internal Dictionary<Type, List<SerializationContract>> CopierContracts { get; } = new();
        internal Dictionary<Type, List<SerializationContract>> ActivatorContracts { get; } = new();
        internal Dictionary<Type, List<SerializationContract>> ConverterContracts { get; } = new();

        internal Dictionary<Type, Func<ICodecProvider, IFieldCodec>> CodecFactories { get; } = new();
        internal Dictionary<Type, Func<ICodecProvider, IDeepCopier>> CopierFactories { get; } = new();
        internal Dictionary<Type, Func<ICodecProvider, object>> SerializerServiceFactories { get; } = new();
        internal Dictionary<Type, DefaultSerializerContract> DefaultSerializerContracts { get; } = new();
        internal Dictionary<Type, DefaultSerializerContract> DefaultCodecFactoryContracts { get; } = new();
        internal Dictionary<Type, DefaultSerializerContract> DefaultCopierFactoryContracts { get; } = new();
        internal Dictionary<Type, Func<ICodecProvider, IRawResponseReader>> RawResponseReaderFactories { get; } = new();

        /// <summary>
        /// Registers a statically constructed raw response reader for a closed invocation result type.
        /// </summary>
        /// <typeparam name="TResult">The invocation result type encoded in the wire header.</typeparam>
        /// <param name="factory">The reader factory.</param>
        /// <remarks>The first reader registration for a result type is used.</remarks>
        public void AddRawResponseReader<TResult>(Func<ICodecProvider, IRawResponseReader> factory)
        {
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            RawResponseReaderFactories.TryAdd(typeof(TResult), factory);
        }
        private readonly HashSet<Type> _defaultSerializerServices = new();
        internal HashSet<Type> ContextTypes { get; } = new();

        /// <summary>
        /// Registers statically constructed serialization and copying implementations for a closed type.
        /// </summary>
        /// <typeparam name="T">The serialized type.</typeparam>
        /// <param name="codecFactory">The factory for the field codec.</param>
        /// <param name="copierFactory">The factory for the deep copier.</param>
        /// <remarks>Explicit registrations replace defaults. The first explicit registration for each service is used.</remarks>
        public void AddSerializer<T>(
            Func<ICodecProvider, IFieldCodec<T>> codecFactory,
            Func<ICodecProvider, IDeepCopier<T>> copierFactory)
            => RegisterSerializerFactories(codecFactory, copierFactory, isDefault: false);

        /// <summary>
        /// Registers default closed serialization factories which yield to explicit registrations.
        /// </summary>
        /// <typeparam name="T">The serialized type.</typeparam>
        /// <param name="codecFactory">The default field codec factory.</param>
        /// <param name="copierFactory">The default deep copier factory.</param>
        /// <remarks>
        /// The first default registration is used until an explicit <see cref="AddSerializer{T}"/>
        /// registration supplies the implementation, in either registration order.
        /// </remarks>
        public void AddDefaultSerializer<T>(
            Func<ICodecProvider, IFieldCodec<T>> codecFactory,
            Func<ICodecProvider, IDeepCopier<T>> copierFactory)
            => RegisterSerializerFactories(codecFactory, copierFactory, isDefault: true);

        /// <summary>
        /// Registers inferred closed factories with their canonical implementations and construction dependencies.
        /// </summary>
        /// <typeparam name="T">The serialized type.</typeparam>
        /// <typeparam name="TCodec">The canonical field codec.</typeparam>
        /// <typeparam name="TCopier">The canonical deep copier.</typeparam>
        /// <param name="codecFactory">The field codec factory.</param>
        /// <param name="copierFactory">The deep copier factory.</param>
        /// <param name="compatibleCodecType">An equivalent metadata codec implementation.</param>
        /// <param name="compatibleCopierType">An equivalent metadata copier implementation.</param>
        /// <param name="codecDependencies">The canonical codec's serialization service dependencies.</param>
        /// <param name="copierDependencies">The canonical copier's serialization service dependencies.</param>
        /// <remarks>Inferred factories yield to the selected custom metadata implementation and explicit factories.</remarks>
        public void AddDefaultSerializer<T, TCodec, TCopier>(
            Func<ICodecProvider, IFieldCodec<T>> codecFactory,
            Func<ICodecProvider, IDeepCopier<T>> copierFactory,
            Type? compatibleCodecType = null,
            Type? compatibleCopierType = null,
            Type[]? codecDependencies = null,
            Type[]? copierDependencies = null)
            where TCodec : class, IFieldCodec<T>
            where TCopier : class, IDeepCopier<T>
        {
            codecDependencies = CopyDefaultDependencies(codecDependencies);
            copierDependencies = CopyDefaultDependencies(copierDependencies);
            var registerCodec = !SerializerServiceFactories.ContainsKey(typeof(IFieldCodec<T>));
            var registerCopier = !SerializerServiceFactories.ContainsKey(typeof(IDeepCopier<T>));
            RegisterSerializerFactories(codecFactory, copierFactory, isDefault: true);
            if (registerCodec)
            {
                RegisterDefaultContract(typeof(IFieldCodec<T>), typeof(TCodec), compatibleCodecType, codecDependencies);
                DefaultCodecFactoryContracts.Add(typeof(T), DefaultSerializerContracts[typeof(IFieldCodec<T>)]);
            }
            if (registerCopier)
            {
                RegisterDefaultContract(typeof(IDeepCopier<T>), typeof(TCopier), compatibleCopierType, copierDependencies);
                DefaultCopierFactoryContracts.Add(typeof(T), DefaultSerializerContracts[typeof(IDeepCopier<T>)]);
            }
        }

        private void RegisterSerializerFactories<T>(
            Func<ICodecProvider, IFieldCodec<T>> codecFactory,
            Func<ICodecProvider, IDeepCopier<T>> copierFactory,
            bool isDefault)
        {
            if (codecFactory is null) throw new ArgumentNullException(nameof(codecFactory));
            if (copierFactory is null) throw new ArgumentNullException(nameof(copierFactory));
            CodecFactories.TryAdd(typeof(T), static provider =>
                Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<IFieldCodec<T>>(null!, provider));
            CopierFactories.TryAdd(typeof(T), static provider =>
                Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<IDeepCopier<T>>(null!, provider));
            AddSerializerServiceFactory(typeof(IFieldCodec<T>), codecFactory, isDefault);
            AddSerializerServiceFactory(typeof(IDeepCopier<T>), copierFactory, isDefault);
            ContextTypes.Add(typeof(T));
        }

        /// <summary>
        /// Registers a statically constructed service used by generated serializers and copiers.
        /// </summary>
        /// <typeparam name="TService">The closed service type.</typeparam>
        /// <param name="factory">The service factory.</param>
        /// <remarks>
        /// The codec provider constructs and caches one instance per service type. Recursive generated
        /// constructors retain references to in-progress dependencies through the generated-code helper.
        /// Explicit registrations replace defaults. The first explicit registration for a service type is used.
        /// </remarks>
        public void AddSerializerService<TService>(Func<ICodecProvider, TService> factory) where TService : class
        {
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            AddSerializerServiceFactory(typeof(TService), factory, isDefault: false);
        }

        /// <summary>
        /// Registers a default closed service factory which yields to an explicit service registration.
        /// </summary>
        /// <typeparam name="TService">The closed service type.</typeparam>
        /// <param name="factory">The default service factory.</param>
        /// <remarks>
        /// The first default registration is used until <see cref="AddSerializerService{TService}"/>
        /// supplies an explicit factory, in either registration order.
        /// </remarks>
        public void AddDefaultSerializerService<TService>(Func<ICodecProvider, TService> factory) where TService : class
        {
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            AddSerializerServiceFactory(typeof(TService), factory, isDefault: true);
        }

        /// <summary>
        /// Registers an inferred service factory with its canonical implementation identity.
        /// </summary>
        /// <typeparam name="TService">The serialization service contract.</typeparam>
        /// <typeparam name="TImplementation">The canonical implementation.</typeparam>
        /// <param name="factory">The service factory.</param>
        /// <param name="compatibleImplementationType">An equivalent metadata implementation.</param>
        /// <param name="dependencies">The canonical implementation's serialization service dependencies.</param>
        public void AddDefaultSerializerService<TService, TImplementation>(
            Func<ICodecProvider, TService> factory,
            Type? compatibleImplementationType = null,
            Type[]? dependencies = null)
            where TService : class
            where TImplementation : class, TService
        {
            dependencies = CopyDefaultDependencies(dependencies);
            var register = !SerializerServiceFactories.ContainsKey(typeof(TService));
            AddDefaultSerializerService(factory);
            if (register) RegisterDefaultContract(typeof(TService), typeof(TImplementation), compatibleImplementationType, dependencies);
        }

        internal bool IsDefaultSerializerService(Type type) => _defaultSerializerServices.Contains(type);

        private void RegisterDefaultContract(Type service, Type implementation, Type? compatible, Type[]? dependencies)
        {
            if (!_defaultSerializerServices.Contains(service) || DefaultSerializerContracts.ContainsKey(service)) return;
            var copiedDependencies = dependencies ?? Type.EmptyTypes;
            DefaultSerializerContracts.Add(service, new(service, implementation, compatible, copiedDependencies));
            DefaultSerializerContracts.TryAdd(implementation, new(service, implementation, compatible, copiedDependencies));
        }

        private static Type[] CopyDefaultDependencies(Type[]? dependencies)
        {
            var result = dependencies is null ? Type.EmptyTypes : (Type[])dependencies.Clone();
            foreach (var dependency in result)
            {
                if (dependency is null) throw new ArgumentException("Serialization service dependencies must be non-null.", nameof(dependencies));
            }
            return result;
        }

        internal sealed record DefaultSerializerContract(Type Service, Type Implementation, Type? CompatibleImplementation, Type[] Dependencies);

        private void AddSerializerServiceFactory(Type type, Func<ICodecProvider, object> factory, bool isDefault)
        {
            if (!isDefault && _defaultSerializerServices.Remove(type))
            {
                SerializerServiceFactories[type] = factory;
            }
            else if (SerializerServiceFactories.TryAdd(type, factory) && isDefault)
            {
                _defaultSerializerServices.Add(type);
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether <see cref="SerializerConfigurationAnalyzer"/> should be enabled.
        /// </summary>
        /// <remarks>
        /// This property does not cause <see cref="SerializerConfigurationAnalyzer"/> to be invoked.
        /// That is the responsibility of the consuming framework.
        /// </remarks>
        public bool? EnableConfigurationAnalysis { get; set; }

        /// <summary>
        /// Gets the set of known activators, which are responsible for creating instances of a given type.
        /// </summary>
        public HashSet<Type> Activators
        {
#if NET5_0_OR_GREATER
            [RequiresUnreferencedCode(
                "Direct collection access cannot preserve activator members required by trimming. "
                + "Use AddActivator(Type) when registering activators.")]
#endif
            get
            {
                _activatorCollectionAccessed = true;
                return _activators;
            }
        }

        /// <summary>
        /// Gets the set of known field codecs, which are responsible for serializing and deserializing fields of a given type.
        /// </summary>
        public HashSet<Type> FieldCodecs
        {
#if NET5_0_OR_GREATER
            [RequiresUnreferencedCode(
                "Direct collection access cannot preserve field codec members required by trimming. "
                + "Use AddFieldCodec(Type) when registering field codecs.")]
#endif
            get
            {
                _serializerCollectionAccessed = true;
                return _fieldCodecs;
            }
        }

        /// <summary>
        /// Gets the set of known serializers, which are responsible for serializing and deserializing a given type.
        /// </summary>
        public HashSet<Type> Serializers
        {
#if NET5_0_OR_GREATER
            [RequiresUnreferencedCode(
                "Direct collection access cannot preserve serializer members required by trimming. "
                + "Use AddSerializer(Type) when registering serializers.")]
#endif
            get
            {
                _serializerCollectionAccessed = true;
                return _serializers;
            }
        }

        /// <summary>
        /// Gets the set of copiers, which are responsible for creating deep copies of a given type.
        /// </summary>
        public HashSet<Type> Copiers
        {
#if NET5_0_OR_GREATER
            [RequiresUnreferencedCode(
                "Direct collection access cannot preserve copier members required by trimming. "
                + "Use AddCopier(Type) when registering copiers.")]
#endif
            get
            {
                _copierCollectionAccessed = true;
                return _copiers;
            }
        }

        /// <summary>
        /// Gets the set of converters, which are responsible for converting from one type to another.
        /// </summary>
        public HashSet<Type> Converters
        {
#if NET5_0_OR_GREATER
            [RequiresUnreferencedCode(
                "Direct collection access cannot preserve converter members required by trimming. "
                + "Use AddConverter(Type) when registering converters.")]
#endif
            get
            {
                _converterCollectionAccessed = true;
                return _converters;
            }
        }

        /// <summary>
        /// Gets the set of known interfaces, which are interfaces that have corresponding proxies in the <see cref="InterfaceProxies"/> collection.
        /// </summary>
        public HashSet<Type> Interfaces
        {
#if NET5_0_OR_GREATER
            [RequiresUnreferencedCode(
                "Direct collection access cannot preserve interface members required by trimming. "
                + "Use AddInterface(Type) when registering interfaces.")]
#endif
            get => _interfaces;
        }

        /// <summary>
        /// Gets the set of known interface proxies, which capture method invocations which can be serialized, deserialized, and invoked against an implementation of this interface.
        /// </summary>
        /// <remarks>
        /// This allows decoupling the caller and target, so that remote procedure calls can be implemented by capturing an invocation, transmitting it, and later invoking it against a target object.
        /// </remarks>
        public HashSet<Type> InterfaceProxies
        {
#if NET5_0_OR_GREATER
            [RequiresUnreferencedCode(
                "Direct collection access cannot preserve proxy members required by trimming. "
                + "Use AddInterfaceProxy(Type) when registering interface proxies.")]
#endif
            get => _interfaceProxies;
        }

        /// <summary>
        /// Gets the set of interface implementations, which are implementations of the interfaces present in <see cref="Interfaces"/>.
        /// </summary>
        public HashSet<Type> InterfaceImplementations
        {
#if NET5_0_OR_GREATER
            [RequiresUnreferencedCode(
                "Direct collection access cannot preserve implementation members required by trimming. "
                + "Use AddInterfaceImplementation(Type) when registering interface implementations.")]
#endif
            get => _interfaceImplementations;
        }

        /// <summary>
        /// Gets the mapping of well-known type identifiers to their corresponding type.
        /// </summary>
        public Dictionary<uint, Type> WellKnownTypeIds { get; } = new Dictionary<uint, Type>();

        /// <summary>
        /// Gets the mapping of well-known type aliases to their corresponding type.
        /// </summary>
        public Dictionary<string, Type> WellKnownTypeAliases { get; } = new Dictionary<string, Type>();

        /// <summary>
        /// Gets the set of allowed Orleans-formatted runtime type names.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An Orleans-formatted runtime type name uses CLR type-name syntax, including namespace,
        /// nesting, generic arguments, arrays, and optional assembly qualification. Use
        /// <see cref="RuntimeTypeNameFormatter.Format(Type)"/> to produce this format.
        /// </para>
        /// <para>
        /// Prefer <see cref="AddAllowedType(Type)"/> when the <see cref="Type"/> is available. It produces
        /// the underlying CLR name without compound aliases so that later alias configuration does not
        /// affect the allow-list entry. Unqualified names such as <see cref="Type.FullName"/> remain
        /// supported for compatibility.
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// services.AddSerializer(builder => builder.Configure(options =>
        /// {
        ///     // Preferred: let Orleans construct the entry.
        ///     options.AddAllowedType(typeof(MyMessage));
        ///
        ///     // Supported when a string entry is required.
        ///     options.AllowedTypes.Add(
        ///         RuntimeTypeNameFormatter.Format(typeof(MyOtherMessage)));
        /// }));
        /// </code>
        /// </example>
        public HashSet<string> AllowedTypes { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Gets the set of assembly names whose types are allowed.
        /// </summary>
        /// <remarks>
        /// Prefer <see cref="AddAllowedAssembly(Assembly)"/> when the assembly is available to avoid
        /// constructing assembly names manually.
        /// </remarks>
        /// <example>
        /// <code>
        /// services.AddSerializer(builder => builder.Configure(options =>
        ///     options.AddAllowedAssembly(typeof(MyMessage).Assembly)));
        /// </code>
        /// </example>
        public HashSet<string> AllowedAssemblies { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Gets the mapping from compound type aliases to types.
        /// </summary>
        public CompoundTypeAliasTree CompoundTypeAliases { get; } = CompoundTypeAliasTree.Create();

        /// <summary>
        /// Gets or sets a value indicating whether to allow all types by default.
        /// Default: <see langword="false"/>.
        /// </summary>
        /// <remarks>
        /// Setting this property to <see langword="true"/> bypasses type-name validation and permits any
        /// resolvable type. This is insecure when serialized input can be influenced by an untrusted party.
        /// Prefer allowing individual types or trusted assemblies.
        /// </remarks>
        public bool AllowAllTypes { get; set; }

        /// <summary>
        /// Gets the set of type manifest providers which have configured this instance.
        /// </summary>
        internal HashSet<object> TypeManifestProviders { get; } = new();

        internal HashSet<Type> ActivatorTypes => _activators;

        internal HashSet<Type> FieldCodecTypes => _fieldCodecs;

        internal HashSet<Type> SerializerTypes => _serializers;

        internal HashSet<Type> CopierTypes => _copiers;

        internal HashSet<Type> ConverterTypes => _converters;

        internal HashSet<Type> InterfaceTypes => _interfaces;

        internal HashSet<Type> InterfaceProxyTypes => _interfaceProxies;

        internal HashSet<Type> InterfaceImplementationTypes => _interfaceImplementations;

        internal bool DiscoverInterfaces(Type type, Type contractType)
            => contractType == typeof(IFieldCodec<>) || contractType == typeof(IBaseCodec<>) || contractType == typeof(IValueSerializer<>)
                ? _serializerCollectionAccessed || _legacySerializers.Contains(type)
                : contractType == typeof(IDeepCopier<>) || contractType == typeof(IBaseCopier<>)
                    ? _copierCollectionAccessed || _legacyCopiers.Contains(type)
                    : contractType == typeof(IActivator<>)
                        ? _activatorCollectionAccessed || _legacyActivators.Contains(type)
                        : _converterCollectionAccessed || _legacyConverters.Contains(type);

        /// <summary>
        /// Adds a serializer implementation type and preserves the members used to inspect and activate it.
        /// </summary>
        /// <param name="type">The serializer implementation type.</param>
        public void AddSerializer(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(ImplementationTypeMembers)]
#endif
            Type type) => AddImplementation(_serializers, _legacySerializers, type);

        /// <summary>
        /// Adds a field codec implementation type and preserves the members used to inspect and activate it.
        /// </summary>
        /// <param name="type">The field codec implementation type.</param>
        public void AddFieldCodec(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(ImplementationTypeMembers)]
#endif
            Type type) => AddImplementation(_fieldCodecs, _legacySerializers, type);

        /// <summary>
        /// Adds a copier implementation type and preserves the members used to inspect and activate it.
        /// </summary>
        /// <param name="type">The copier implementation type.</param>
        public void AddCopier(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(ImplementationTypeMembers)]
#endif
            Type type) => AddImplementation(_copiers, _legacyCopiers, type);

        /// <summary>
        /// Adds a converter implementation type and preserves the members used to inspect and activate it.
        /// </summary>
        /// <param name="type">The converter implementation type.</param>
        public void AddConverter(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(ImplementationTypeMembers)]
#endif
            Type type) => AddImplementation(_converters, _legacyConverters, type);

        /// <summary>
        /// Adds an activator implementation type and preserves the members used to inspect and activate it.
        /// </summary>
        /// <param name="type">The activator implementation type.</param>
        public void AddActivator(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(ImplementationTypeMembers)]
#endif
            Type type) => AddImplementation(_activators, _legacyActivators, type);

        private static void AddImplementation(HashSet<Type> types, HashSet<Type> legacyTypes, Type type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            types.Add(type);
            legacyTypes.Add(type);
        }

        /// <summary>
        /// Registers a field codec for its target type and preserves its activation and target interface metadata.
        /// </summary>
        /// <param name="type">The codec implementation type.</param>
        /// <param name="targetType">The serialized type, or its generic definition for an open generic codec.</param>
        public void AddSerializer(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
#endif
            Type type,
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
#endif
            Type targetType) => AddContract(_serializers, SerializerContracts, type, targetType, typeof(IFieldCodec<>));

        /// <summary>
        /// Registers a field codec for its target type and preserves its activation and target interface metadata.
        /// </summary>
        /// <param name="type">The codec implementation type.</param>
        /// <param name="targetType">The serialized type, or its generic definition for an open generic codec.</param>
        public void AddFieldCodec(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
#endif
            Type type,
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
#endif
            Type targetType) => AddContract(_fieldCodecs, SerializerContracts, type, targetType, typeof(IFieldCodec<>));

        /// <summary>
        /// Registers a base codec for its target type and preserves its activation and target interface metadata.
        /// </summary>
        /// <param name="type">The base codec implementation type.</param>
        /// <param name="targetType">The serialized type, or its generic definition for an open generic codec.</param>
        public void AddBaseCodec(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
#endif
            Type type,
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
#endif
            Type targetType) => AddContract(_serializers, SerializerContracts, type, targetType, typeof(IBaseCodec<>));

        /// <summary>
        /// Registers a value serializer for its target type and preserves its activation and target interface metadata.
        /// </summary>
        /// <param name="type">The value serializer implementation type.</param>
        /// <param name="targetType">The serialized type, or its generic definition for an open generic serializer.</param>
        public void AddValueSerializer(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
#endif
            Type type,
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
#endif
            Type targetType) => AddContract(_serializers, SerializerContracts, type, targetType, typeof(IValueSerializer<>));

        /// <summary>
        /// Registers a deep copier for its target type and preserves its activation and target interface metadata.
        /// </summary>
        /// <param name="type">The copier implementation type.</param>
        /// <param name="targetType">The copied type, or its generic definition for an open generic copier.</param>
        public void AddCopier(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
#endif
            Type type,
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
#endif
            Type targetType) => AddContract(_copiers, CopierContracts, type, targetType, typeof(IDeepCopier<>));

        /// <summary>
        /// Registers a base copier for its target type and preserves its activation and target interface metadata.
        /// </summary>
        /// <param name="type">The base copier implementation type.</param>
        /// <param name="targetType">The copied type, or its generic definition for an open generic copier.</param>
        public void AddBaseCopier(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
#endif
            Type type,
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
#endif
            Type targetType) => AddContract(_copiers, CopierContracts, type, targetType, typeof(IBaseCopier<>));

        /// <summary>
        /// Registers an activator for its target type and preserves its activation and target interface metadata.
        /// </summary>
        /// <param name="type">The activator implementation type.</param>
        /// <param name="targetType">The activated type, or its generic definition for an open generic activator.</param>
        public void AddActivator(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
#endif
            Type type,
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
#endif
            Type targetType) => AddContract(_activators, ActivatorContracts, type, targetType, typeof(IActivator<>));

        /// <summary>
        /// Registers a converter for its value and surrogate types and preserves its activation and target interface metadata.
        /// </summary>
        /// <param name="type">The converter implementation type.</param>
        /// <param name="targetType">The converted value type, or its generic definition for an open generic converter.</param>
        /// <param name="surrogateType">The surrogate type, or a generic definition using the converter's arguments in the same order.</param>
        public void AddConverter(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
#endif
            Type type,
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
#endif
            Type targetType,
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
#endif
            Type surrogateType)
        {
            if (surrogateType is null)
            {
                throw new ArgumentNullException(nameof(surrogateType));
            }

            AddContract(_converters, ConverterContracts, type, targetType, typeof(IConverter<,>), surrogateType);
        }

        /// <summary>
        /// Registers a converter whose surrogate type is described using its generic parameters.
        /// </summary>
        /// <param name="type">The converter implementation type.</param>
        /// <param name="targetType">The converted type or its generic definition.</param>
        /// <param name="surrogateType">The surrogate description, binding parameters to the converter implementation.</param>
        public void AddConverter(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
#endif
            Type type,
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
#endif
            Type targetType,
            SerializationType surrogateType)
        {
            if (surrogateType is null)
            {
                throw new ArgumentNullException(nameof(surrogateType));
            }

            AddContract(_converters, ConverterContracts, type, targetType, typeof(IConverter<,>), surrogateDescription: surrogateType);
        }

        /// <summary>
        /// Registers a serialization contract whose target is described using the implementation's generic parameters.
        /// </summary>
        /// <param name="type">The implementation type.</param>
        /// <param name="contractType">The generic definition of the field codec, base codec, value serializer, copier, activator, or converter interface.</param>
        /// <param name="targetType">The target type description.</param>
        /// <param name="surrogateType">The surrogate description for a converter contract.</param>
        public void AddSerializationContract(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
#endif
            Type type,
            Type contractType,
            SerializationType targetType,
            SerializationType? surrogateType = null)
        {
            if (targetType is null)
            {
                throw new ArgumentNullException(nameof(targetType));
            }

            var (types, contracts) = contractType == typeof(IFieldCodec<>) || contractType == typeof(IBaseCodec<>) || contractType == typeof(IValueSerializer<>)
                ? (_serializers, SerializerContracts)
                : contractType == typeof(IDeepCopier<>) || contractType == typeof(IBaseCopier<>)
                    ? (_copiers, CopierContracts)
                    : contractType == typeof(IActivator<>)
                        ? (_activators, ActivatorContracts)
                        : contractType == typeof(IConverter<,>)
                            ? (_converters, ConverterContracts)
                            : throw new ArgumentException("The contract must be a supported serialization interface definition.", nameof(contractType));
            if ((contractType == typeof(IConverter<,>)) != (surrogateType is not null))
            {
                throw new ArgumentException("Only converter contracts require a surrogate description.", nameof(surrogateType));
            }

            AddContract(types, contracts, type, null, contractType,
                surrogateDescription: surrogateType, targetDescription: targetType);
        }

        private static void AddContract(
            HashSet<Type> types,
            Dictionary<Type, List<SerializationContract>> contracts,
            Type type,
            Type? targetType,
            Type contractType,
            Type? surrogateType = null,
            SerializationType? surrogateDescription = null,
            SerializationType? targetDescription = null)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (targetType is null && targetDescription is null)
            {
                throw new ArgumentNullException(nameof(targetType));
            }

            types.Add(type);
            if (!contracts.TryGetValue(type, out var registrations))
            {
                contracts[type] = registrations = new();
            }

            var registration = new SerializationContract(contractType, targetType, surrogateType, surrogateDescription, targetDescription);
            if (!registrations.Contains(registration))
            {
                registrations.Add(registration);
            }
        }

        /// <summary>
        /// Adds a generated interface type and preserves the methods and inherited interfaces used by generated invokables.
        /// </summary>
        /// <param name="type">The generated interface type.</param>
        public void AddInterface(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(InterfaceTypeMembers)]
#endif
            Type type) => _interfaces.Add(type ?? throw new ArgumentNullException(nameof(type)));

        /// <summary>
        /// Adds a generated proxy type and preserves its implemented interfaces.
        /// </summary>
        /// <param name="type">The generated proxy type.</param>
        public void AddInterfaceProxy(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(
                DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)]
#endif
            Type type) => _interfaceProxies.Add(type ?? throw new ArgumentNullException(nameof(type)));

        /// <summary>
        /// Adds a generated interface implementation type and preserves its public constructors and implemented interfaces.
        /// </summary>
        /// <param name="type">The generated interface implementation type.</param>
        public void AddInterfaceImplementation(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(ImplementationTypeMembers)]
#endif
            Type type) => _interfaceImplementations.Add(type ?? throw new ArgumentNullException(nameof(type)));

        /// <summary>
        /// Adds the Orleans-formatted runtime type name for <paramref name="type"/> to
        /// <see cref="AllowedTypes"/>.
        /// </summary>
        /// <remarks>
        /// This is the preferred way to allow an available <see cref="Type"/>. It formats the underlying
        /// CLR type name without compound aliases and includes all constructed generic components.
        /// </remarks>
        /// <param name="type">The type to allow.</param>
        public void AddAllowedType(Type type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            AllowedTypes.Add(RuntimeTypeNameFormatter.FormatInternalNoCache(type, allowAliases: false));
            ContextTypes.Add(type);
        }

        /// <summary>
        /// Adds the assembly name for <paramref name="assembly"/> to <see cref="AllowedAssemblies"/>.
        /// </summary>
        /// <param name="assembly">The assembly to allow.</param>
        public void AddAllowedAssembly(Assembly assembly)
        {
            if (assembly is null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            AllowedAssemblies.Add(CachedTypeResolver.GetName(assembly));
        }
    }
}
