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
    public class OrleansJsonSerializationBinder : DefaultSerializationBinder
    {
        private readonly TypeConverter? _typeConverter;
        private readonly bool _allowAllTypes;

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansJsonSerializationBinder"/> class.
        /// </summary>
        /// <param name="typeResolver">The type resolver.</param>
        /// <remarks>
        /// Supply the constructor which accepts a <see cref="TypeConverter"/> for deserialization.
        /// That constructor binds host-established identities and applies the Orleans type policy.
        /// This overload is retained for binary compatibility; deserialization reports a configuration error.
        /// </remarks>
        public OrleansJsonSerializationBinder(TypeResolver typeResolver)
        {
            _allowAllTypes = true;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansJsonSerializationBinder"/> class which enforces the
        /// Orleans type allow-list.
        /// </summary>
        /// <param name="typeConverter">The type converter used to resolve and validate types against the allow-list.</param>
        /// <param name="typeResolver">The ordinary lookup resolver retained for signature compatibility.</param>
        /// <param name="allowAllTypes">
        /// When <see langword="true"/>, bypasses authorization for host-established type identities.
        /// Prefer individual type grants for less-trusted input.
        /// </param>
        public OrleansJsonSerializationBinder(TypeConverter typeConverter, TypeResolver typeResolver, bool allowAllTypes = false)
        {
            _typeConverter = typeConverter;
            _allowAllTypes = allowAllTypes;
        }

        /// <inheritdoc />
        public override Type BindToType(string? assemblyName, string typeName)
        {
            var fullName = !string.IsNullOrWhiteSpace(assemblyName) ? typeName + ',' + assemblyName : typeName;

            if (_typeConverter is null)
            {
                throw new JsonSerializationException("Configure OrleansJsonSerializationBinder with a TypeConverter to bind host-registered wire types.");
            }

            try
            {
                if (_typeConverter.TryParseForDeserialization(fullName, out var type, allowAllTypes: _allowAllTypes))
                {
                    return type;
                }
            }
            catch (InvalidOperationException exception)
            {
                throw new JsonSerializationException(BuildNotAllowedMessage(fullName), exception);
            }

            throw new JsonSerializationException(BuildNotAllowedMessage(fullName));
        }

        private static string BuildNotAllowedMessage(string fullName) =>
            $"Unable to resolve type \"{fullName}\". The type could not be found or is not permitted by the configured type allow-list. " +
            $"To allow it, mark the type with [GenerateSerializer], call {nameof(Configuration.TypeManifestOptions)}.{nameof(Configuration.TypeManifestOptions.AddAllowedType)}, " +
            $"call {nameof(Configuration.TypeManifestOptions)}.{nameof(Configuration.TypeManifestOptions.AddAllowedAssembly)}, add its Orleans-formatted name to {nameof(Configuration.TypeManifestOptions)}.{nameof(Configuration.TypeManifestOptions.AllowedTypes)}, " +
            $"or register its Type identity and an {nameof(ITypeNameFilter)} or {nameof(ITypeFilter)} which allows it.";
    }
}
