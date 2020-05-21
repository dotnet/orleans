using System;
using System.Collections.Generic;
using Orleans.Runtime;
using Orleans.Utilities;

namespace Orleans.Metadata
{
    internal sealed class DiagnosticInfoGrainPropertiesProvider : IGrainPropertiesProvider, IGrainInterfacePropertiesProvider
    {
        public void Populate(Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties["diag.type"] = RuntimeTypeNameFormatter.Format(grainClass);
            properties["diag.asm"] = grainClass.Assembly.GetName().Name;
        }

        public void Populate(Type interfaceType, GrainInterfaceId interfaceId, Dictionary<string, string> properties)
        {
            properties["diag.type"] = RuntimeTypeNameFormatter.Format(interfaceType);
            properties["diag.asm"] = interfaceType.Assembly.GetName().Name;
        }
    }
}
