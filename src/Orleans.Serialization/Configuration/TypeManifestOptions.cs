using System;
using System.Collections.Generic;
#if NET5_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
#endif
using System.Reflection;
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
        public HashSet<Type> Activators { get; } = new HashSet<Type>();

        /// <summary>
        /// Gets the set of known field codecs, which are responsible for serializing and deserializing fields of a given type.
        /// </summary>
        public HashSet<Type> FieldCodecs { get; } = new HashSet<Type>();

        /// <summary>
        /// Gets the set of known serializers, which are responsible for serializing and deserializing a given type.
        /// </summary>
        public HashSet<Type> Serializers { get; } = new HashSet<Type>();

        /// <summary>
        /// Gets the set of copiers, which are responsible for creating deep copies of a given type.
        /// </summary>
        public HashSet<Type> Copiers { get; } = new HashSet<Type>();

        /// <summary>
        /// Gets the set of converters, which are responsible for converting from one type to another.
        /// </summary>
        public HashSet<Type> Converters { get; } = new HashSet<Type>();

        /// <summary>
        /// Gets the set of known interfaces, which are interfaces that have corresponding proxies in the <see cref="InterfaceProxies"/> collection.
        /// </summary>
        public HashSet<Type> Interfaces { get; } = new HashSet<Type>();

        /// <summary>
        /// Gets the set of known interface proxies, which capture method invocations which can be serialized, deserialized, and invoked against an implementation of this interface.
        /// </summary>
        /// <remarks>
        /// This allows decoupling the caller and target, so that remote procedure calls can be implemented by capturing an invocation, transmitting it, and later invoking it against a target object.
        /// </remarks>
        public HashSet<Type> InterfaceProxies { get; } = new HashSet<Type>();

        /// <summary>
        /// Gets the set of interface implementations, which are implementations of the interfaces present in <see cref="Interfaces"/>.
        /// </summary>
        public HashSet<Type> InterfaceImplementations { get; } = new HashSet<Type>();

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

        /// <summary>
        /// Adds a serializer implementation type and preserves the members used to inspect and activate it.
        /// </summary>
        /// <param name="type">The serializer implementation type.</param>
        public void AddSerializer(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(ImplementationTypeMembers)]
#endif
            Type type) => Serializers.Add(type ?? throw new ArgumentNullException(nameof(type)));

        /// <summary>
        /// Adds a field codec implementation type and preserves the members used to inspect and activate it.
        /// </summary>
        /// <param name="type">The field codec implementation type.</param>
        public void AddFieldCodec(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(ImplementationTypeMembers)]
#endif
            Type type) => FieldCodecs.Add(type ?? throw new ArgumentNullException(nameof(type)));

        /// <summary>
        /// Adds a copier implementation type and preserves the members used to inspect and activate it.
        /// </summary>
        /// <param name="type">The copier implementation type.</param>
        public void AddCopier(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(ImplementationTypeMembers)]
#endif
            Type type) => Copiers.Add(type ?? throw new ArgumentNullException(nameof(type)));

        /// <summary>
        /// Adds a converter implementation type and preserves the members used to inspect and activate it.
        /// </summary>
        /// <param name="type">The converter implementation type.</param>
        public void AddConverter(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(ImplementationTypeMembers)]
#endif
            Type type) => Converters.Add(type ?? throw new ArgumentNullException(nameof(type)));

        /// <summary>
        /// Adds an activator implementation type and preserves the members used to inspect and activate it.
        /// </summary>
        /// <param name="type">The activator implementation type.</param>
        public void AddActivator(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(ImplementationTypeMembers)]
#endif
            Type type) => Activators.Add(type ?? throw new ArgumentNullException(nameof(type)));

        /// <summary>
        /// Adds a generated interface type and preserves the methods and inherited interfaces used by generated invokables.
        /// </summary>
        /// <param name="type">The generated interface type.</param>
        public void AddInterface(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(InterfaceTypeMembers)]
#endif
            Type type) => Interfaces.Add(type ?? throw new ArgumentNullException(nameof(type)));

        /// <summary>
        /// Adds a generated proxy type and preserves its implemented interfaces.
        /// </summary>
        /// <param name="type">The generated proxy type.</param>
        public void AddInterfaceProxy(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(
                DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)]
#endif
            Type type) => InterfaceProxies.Add(type ?? throw new ArgumentNullException(nameof(type)));

        /// <summary>
        /// Adds a generated interface implementation type and preserves its implemented interfaces.
        /// </summary>
        /// <param name="type">The generated interface implementation type.</param>
        public void AddInterfaceImplementation(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
#endif
            Type type) => InterfaceImplementations.Add(type ?? throw new ArgumentNullException(nameof(type)));

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
