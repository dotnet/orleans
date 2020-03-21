using System;
using Newtonsoft.Json.Serialization;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    /// <inheritdoc />
    public class OrleansJsonSerializationBinder : DefaultSerializationBinder
    {
        private readonly ITypeResolver typeResolver;

        /// <inheritdoc />
        public OrleansJsonSerializationBinder(ITypeResolver typeResolver)
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