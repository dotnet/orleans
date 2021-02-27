using System;
using Newtonsoft.Json.Serialization;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Serialization
{
    /// <inheritdoc />
    public class OrleansJsonSerializationBinder : DefaultSerializationBinder
    {
        private readonly TypeResolver typeResolver;

        /// <inheritdoc />
        public OrleansJsonSerializationBinder(TypeResolver typeResolver)
        {
            this.typeResolver = typeResolver;
        }

        /// <inheritdoc />
        public override Type BindToType(string assemblyName, string typeName)
        {
            var fullName = !string.IsNullOrWhiteSpace(assemblyName) ? typeName + ',' + assemblyName : typeName;
            if (typeResolver.TryResolveType(fullName, out var type)) return type;

            return base.BindToType(assemblyName, typeName);
        }
    }
}