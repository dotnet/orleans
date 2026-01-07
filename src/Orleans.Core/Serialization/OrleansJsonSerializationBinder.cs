using System;
using Newtonsoft.Json.Serialization;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Serialization
{
    /// <summary>
    /// Implementation of <see cref="ISerializationBinder"/> which resolves types using a <see cref="TypeResolver"/>.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="OrleansJsonSerializationBinder"/> class.
    /// </remarks>
    /// <param name="typeResolver">The type resolver.</param>
    public class OrleansJsonSerializationBinder(TypeResolver typeResolver) : DefaultSerializationBinder
    {

        /// <inheritdoc />
        public override Type BindToType(string assemblyName, string typeName)
        {
            var fullName = !string.IsNullOrWhiteSpace(assemblyName) ? typeName + ',' + assemblyName : typeName;
            if (typeResolver.TryResolveType(fullName, out var type)) return type;

            return base.BindToType(assemblyName, typeName);
        }
    }
}
