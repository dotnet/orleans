using System;
using Newtonsoft.Json.Serialization;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Serialization
{
    /// <summary>
    /// Implementation of <see cref="ISerializationBinder"/> which resolves types using a <see cref="TypeResolver"/>.
    /// </summary>
    public class OrleansJsonSerializationBinder : DefaultSerializationBinder
    {
        private readonly TypeResolver typeResolver;

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansJsonSerializationBinder"/> class.
        /// </summary>
        /// <param name="typeResolver">The type resolver.</param>
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