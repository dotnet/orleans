using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    internal class ImplementedInterfaceProvider : IGrainPropertiesProvider
    {
        private readonly GrainInterfaceIdResolver interfaceIdProvider;

        public ImplementedInterfaceProvider(GrainInterfaceIdResolver interfaceIdProvider)
        {
            this.interfaceIdProvider = interfaceIdProvider;
        }

        public void Populate(Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            var counter = 0;
            foreach (var @interface in grainClass.GetInterfaces())
            {
                if (!IsGrainInterface(@interface)) continue;

                var interfaceId = this.interfaceIdProvider.GetGrainInterfaceId(@interface);
                var key = WellKnownGrainTypeProperties.ImplementedInterfacePrefix + counter.ToString(CultureInfo.InvariantCulture);
                properties[key] = interfaceId.ToStringUtf8();
                ++counter;
            }
        }

        public static bool IsGrainInterface(Type t)
        {
            if (t.IsClass)
                return false;
            if (t == typeof(IGrainObserver) || t == typeof(IAddressable) || t == typeof(IGrainExtension))
                return false;
            if (t == typeof(IGrain) || t == typeof(IGrainWithGuidKey) || t == typeof(IGrainWithIntegerKey)
                || t == typeof(IGrainWithGuidCompoundKey) || t == typeof(IGrainWithIntegerCompoundKey))
                return false;
            if (t == typeof(ISystemTarget))
                return false;

            return typeof(IAddressable).IsAssignableFrom(t);
        }
    }
}
