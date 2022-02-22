using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Populates grain interface properties with the grain interfaces implemented by a grain class.
    /// </summary>
    internal class ImplementedInterfaceProvider : IGrainPropertiesProvider
    {
        private readonly GrainInterfaceTypeResolver interfaceTypeResolver;

        /// <summary>
        /// Initializes a new instance of the <see cref="ImplementedInterfaceProvider"/> class.
        /// </summary>
        /// <param name="interfaceTypeResolver">The interface type resolver.</param>
        public ImplementedInterfaceProvider(GrainInterfaceTypeResolver interfaceTypeResolver)
        {
            this.interfaceTypeResolver = interfaceTypeResolver;
        }

        /// <inheritdoc/>
        public void Populate(Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            var counter = 0;
            foreach (var @interface in grainClass.GetInterfaces())
            {
                if (!IsGrainInterface(@interface)) continue;

                var type = @interface switch
                {
                    { IsGenericType: true } when grainClass is { IsGenericType: true } => @interface.GetGenericTypeDefinition(),
                    _ => @interface
                };
                var interfaceId = this.interfaceTypeResolver.GetGrainInterfaceType(type);
                var key = WellKnownGrainTypeProperties.ImplementedInterfacePrefix + counter.ToString(CultureInfo.InvariantCulture);
                properties[key] = interfaceId.ToStringUtf8();
                ++counter;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the specified type is a grain interface type.
        /// </summary>
        /// <param name="type">The type to inspect.</param>
        /// <returns>A value indicating whether the specified type is a grain interface type.</returns>
        public static bool IsGrainInterface(Type type)
        {
            if (type.IsClass)
                return false;
            if (type == typeof(IGrainObserver) || type == typeof(IAddressable) || type == typeof(IGrainExtension))
                return false;
            if (type == typeof(IGrain) || type == typeof(IGrainWithGuidKey) || type == typeof(IGrainWithIntegerKey)
                || type == typeof(IGrainWithGuidCompoundKey) || type == typeof(IGrainWithIntegerCompoundKey))
                return false;
            if (type == typeof(ISystemTarget))
                return false;

            return typeof(IAddressable).IsAssignableFrom(type);
        }
    }
}
