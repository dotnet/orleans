using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Serialization
{
    /// <summary>
    /// Implementation of <see cref="ISerializationBinder"/> which resolves types using a <see cref="TypeConverter"/>
    /// and enforces the configured Orleans type allow-list, preventing arbitrary types from being constructed
    /// during deserialization.
    /// </summary>
    /// <remarks>
    /// This constructor does not enforce the Orleans type allow-list: any resolvable type may be constructed
    /// during deserialization. It is retained for backwards compatibility. Prefer the constructor which accepts
    /// a <see cref="TypeConverter"/> so that the type allow-list is enforced.
    /// </remarks>
    /// <param name="typeResolver">The type resolver.</param>
    public class OrleansJsonSerializationBinder(TypeResolver typeResolver) : DefaultSerializationBinder
    {
        private readonly TypeResolver _typeResolver = typeResolver;
        private readonly TypeConverter? _typeConverter;
        private readonly bool _allowAllTypes = true;

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansJsonSerializationBinder"/> class which enforces the
        /// Orleans type allow-list.
        /// </summary>
        /// <param name="typeConverter">The type converter used to resolve and validate types against the allow-list.</param>
        /// <param name="typeResolver">The type resolver used to resolve types when the allow-list is disabled.</param>
        /// <param name="allowAllTypes">
        /// When <see langword="true"/>, restores the legacy behavior of allowing any resolvable type to be constructed
        /// during deserialization, bypassing the type allow-list. This is insecure and is not recommended.
        /// </param>
        public OrleansJsonSerializationBinder(TypeConverter typeConverter, TypeResolver typeResolver, bool allowAllTypes = false)
            : this(typeResolver)
        {
            _typeConverter = typeConverter;
            _allowAllTypes = allowAllTypes;
        }

        /// <inheritdoc />
        public override Type BindToType(string? assemblyName, string typeName)
        {
            var fullName = !string.IsNullOrWhiteSpace(assemblyName) ? typeName + ',' + assemblyName : typeName;

            if (_allowAllTypes || _typeConverter is null)
            {
                // Legacy, permissive behavior: resolve any type without consulting the allow-list.
                if (_typeResolver is not null && _typeResolver.TryResolveType(fullName, out var resolvedType))
                {
                    return resolvedType;
                }

                return base.BindToType(assemblyName, typeName);
            }

            // Enforce the Orleans type allow-list. TypeConverter.TryParse throws for types which are explicitly
            // disallowed and only resolves types which are permitted by the configured filters and allowed-type
            // configuration.
            try
            {
                if (_typeConverter.TryParse(fullName, out var type))
                {
                    return type;
                }
            }
            catch (InvalidOperationException exception)
            {
                // TypeConverter throws InvalidOperationException when the type is resolvable but not permitted by
                // the allow-list. Surface a JSON-specific error which also mentions the opt-out.
                throw new JsonSerializationException(BuildNotAllowedMessage(fullName), exception);
            }

            throw new JsonSerializationException(BuildNotAllowedMessage(fullName));
        }

        private static string BuildNotAllowedMessage(string fullName) =>
            $"Unable to resolve type \"{fullName}\". The type could not be found or is not permitted by the configured type allow-list. " +
            $"To allow it, mark the type with [GenerateSerializer], call {nameof(Configuration.TypeManifestOptions)}.{nameof(Configuration.TypeManifestOptions.AddAllowedType)}, " +
            $"call {nameof(Configuration.TypeManifestOptions)}.{nameof(Configuration.TypeManifestOptions.AddAllowedAssembly)}, add its Orleans-formatted name to {nameof(Configuration.TypeManifestOptions)}.{nameof(Configuration.TypeManifestOptions.AllowedTypes)}, " +
            $"or register an {nameof(ITypeNameFilter)} or {nameof(ITypeFilter)} which allows it. Setting {nameof(OrleansJsonSerializerOptions)}.{nameof(OrleansJsonSerializerOptions.AllowAllTypes)} to true restores the previous behavior but is insecure when serialized input can be influenced by an untrusted party.";
    }
}
